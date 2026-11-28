using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MailDog.Contracts.Dto;
using MailDog.Contracts.Messages;
using MailDog.Contracts.Serialization;
using MailDog.Mail.Accounts;
using MailDog.Mail.Storage;
using MailKit.Security;

namespace MailDog.Mail.Auth;

/// <summary>Outlook OAuth2 Authorization Code + PKCE。浏览器登录在 Engine 内完成，不依赖 UI。</summary>
public sealed class OutlookAuthService : IOutlookAuthService, IAccessTokenProvider, IDisposable
{
    private const string AuthorizeEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize";
    private const string TokenEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/token";

    // Entra 公共客户端 ID 不是机密，内置后用户无需自行注册应用。
    private const string DefaultClientId = "036018c4-ee11-4e8c-86ee-1eeb1e409094";

    private const string Scope =
        "offline_access openid profile email "
        + "https://outlook.office.com/IMAP.AccessAsUser.All "
        + "https://outlook.office.com/SMTP.Send";

    private readonly SqliteStore _store;
    private readonly AccountService _accounts;
    private readonly SecretStore _secrets;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly ConcurrentDictionary<string, (string AccessToken, DateTimeOffset ExpiresAt)> _cache = new(StringComparer.Ordinal);
    private CancellationTokenSource? _activeCts;
    private bool _disposed;

    public OutlookAuthService(SqliteStore store, AccountService accounts, SecretStore secrets)
    {
        _store = store;
        _accounts = accounts;
        _secrets = secrets;
    }

    public event Action<AuthStateEvent>? AuthStateChanged;

    public event Action<AccountChangedEvent>? AccountChanged;

    public event Action<string>? Diagnostic;

    public Task StartAsync(string? loginHint, CancellationToken cancellationToken)
    {
        var clientId = ResolveClientId();

        Cancel();

        var cts = new CancellationTokenSource();
        _activeCts = cts;
        _ = Task.Run(() => RunFlowAsync(clientId, loginHint, cts.Token), CancellationToken.None);

        return Task.CompletedTask;
    }

    public void Cancel()
    {
        var cts = Interlocked.Exchange(ref _activeCts, null);
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        cts.Dispose();
    }

    public async Task<string> GetAccessTokenAsync(AccountRecord account, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(account.Id, out var cached)
            && cached.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
        {
            return cached.AccessToken;
        }

        var refreshToken = _accounts.GetRefreshToken(account);
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new AuthenticationException("缺少 Outlook refresh token，请重新登录。");
        }

        var clientId = ResolveClientId();

        OAuthTokenResponse token;
        try
        {
            token = await PostTokenAsync(
                new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refreshToken,
                    ["scope"] = Scope
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AuthenticationException)
        {
            _store.UpdateAccountStatus(account.Id, AccountStatus.AuthRequired);
            AuthStateChanged?.Invoke(new AuthStateEvent
            {
                AccountId = account.Id,
                State = AuthStateKind.Required,
                Message = "Outlook 登录已过期，请重新登录。"
            });
            throw;
        }

        if (!string.IsNullOrWhiteSpace(token.RefreshToken) && account.SecretRef is not null)
        {
            _secrets.Save(account.SecretRef, "oauth_refresh", token.RefreshToken);
        }

        var expiry = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn - 60));
        _store.UpdateAccountTokens(account.Id, expiry);
        _cache[account.Id] = (token.AccessToken, expiry);

        return token.AccessToken;
    }

    private async Task RunFlowAsync(string clientId, string? loginHint, CancellationToken cancellationToken)
    {
        var accountId = loginHint ?? "pending";

        try
        {
            var state = CreateRandomBase64Url(16);
            var verifier = CreateRandomBase64Url(32);
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

            await using var server = new LoopbackOAuthServer(state);
            var redirectUri = server.RedirectUri;
            var authorizeUri = BuildAuthorizeUri(clientId, redirectUri, challenge, state, loginHint);

            AuthStateChanged?.Invoke(new AuthStateEvent
            {
                AccountId = accountId,
                State = AuthStateKind.BrowserOpened,
                Message = "已在浏览器中打开登录页面。"
            });

            Process.Start(new ProcessStartInfo(authorizeUri) { UseShellExecute = true });

            var callback = await server.WaitAsync(TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(callback.Error))
            {
                throw new AuthenticationException($"登录失败：{callback.ErrorDescription ?? callback.Error}");
            }

            if (string.IsNullOrWhiteSpace(callback.Code))
            {
                throw new AuthenticationException("未收到授权码。");
            }

            AuthStateChanged?.Invoke(new AuthStateEvent
            {
                AccountId = accountId,
                State = AuthStateKind.BrowserOpened,
                Message = "正在交换令牌…"
            });

            var token = await ExchangeCodeAsync(clientId, redirectUri, callback.Code, verifier, cancellationToken)
                .ConfigureAwait(false);

            var (email, name) = ParseIdToken(token.IdToken);
            email ??= loginHint;

            if (string.IsNullOrWhiteSpace(email))
            {
                throw new AuthenticationException("无法从登录结果中获取邮箱地址。");
            }

            if (string.IsNullOrWhiteSpace(token.RefreshToken))
            {
                throw new AuthenticationException("未收到 refresh token，请确认已同意 offline_access。");
            }

            var secretRef = $"oauth:{email.ToLowerInvariant()}:refresh";
            _secrets.Save(secretRef, "oauth_refresh", token.RefreshToken);

            var account = _accounts.AddOAuthAccount(
                MailProvider.Outlook,
                email,
                string.IsNullOrWhiteSpace(name) ? email : name,
                secretRef);

            var expiry = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn - 60));
            _store.UpdateAccountTokens(account.Id, expiry);
            _cache[account.Id] = (token.AccessToken, expiry);

            // 先广播账户列表变化，再广播登录完成；UI 只由 AccountChanged 重载列表，
            // 避免两个事件几乎同时到达时重复清空并重建同一组账户控件。
            AccountChanged?.Invoke(new AccountChangedEvent { Account = account });
            AuthStateChanged?.Invoke(new AuthStateEvent
            {
                AccountId = account.Id,
                State = AuthStateKind.Connected
            });
        }
        catch (OperationCanceledException)
        {
            AuthStateChanged?.Invoke(new AuthStateEvent
            {
                AccountId = accountId,
                State = AuthStateKind.Failed,
                Message = "登录已取消。"
            });
        }
        catch (Exception exception)
        {
            Diagnostic?.Invoke($"Outlook 登录失败：{exception.Message}");
            AuthStateChanged?.Invoke(new AuthStateEvent
            {
                AccountId = accountId,
                State = AuthStateKind.Failed,
                Message = exception.Message
            });
        }
    }

    private async Task<OAuthTokenResponse> ExchangeCodeAsync(
        string clientId,
        Uri redirectUri,
        string code,
        string verifier,
        CancellationToken cancellationToken)
        => await PostTokenAsync(
            new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri.ToString(),
                ["code_verifier"] = verifier,
                ["scope"] = Scope
            },
            cancellationToken).ConfigureAwait(false);

    private async Task<OAuthTokenResponse> PostTokenAsync(
        IReadOnlyDictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsync(
            TokenEndpoint,
            new FormUrlEncodedContent(form),
            cancellationToken).ConfigureAwait(false);

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            OAuthTokenError? error = null;
            try
            {
                error = JsonSerializer.Deserialize(json, IpcJsonContext.Default.OAuthTokenError);
            }
            catch (JsonException)
            {
                // 保留原始响应作为兜底。
            }

            throw new AuthenticationException(error?.ErrorDescription ?? error?.Error ?? "令牌请求失败。");
        }

        return JsonSerializer.Deserialize(json, IpcJsonContext.Default.OAuthTokenResponse)
               ?? throw new AuthenticationException("令牌响应为空。");
    }

    private static string ResolveClientId()
    {
        // 保留环境变量覆盖，便于用自己的 Entra 应用调试或分发。
        var fromEnvironment = Environment.GetEnvironmentVariable("MAILDOG_ENTRA_CLIENT_ID");
        return string.IsNullOrWhiteSpace(fromEnvironment) ? DefaultClientId : fromEnvironment.Trim();
    }

    private static string BuildAuthorizeUri(
        string clientId,
        Uri redirectUri,
        string challenge,
        string state,
        string? loginHint)
    {
        var parameters = new List<string>
        {
            "client_id=" + Uri.EscapeDataString(clientId),
            "response_type=code",
            "redirect_uri=" + Uri.EscapeDataString(redirectUri.ToString()),
            "response_mode=query",
            "scope=" + Uri.EscapeDataString(Scope),
            "code_challenge=" + Uri.EscapeDataString(challenge),
            "code_challenge_method=S256",
            "state=" + Uri.EscapeDataString(state),
            "prompt=select_account"
        };

        if (!string.IsNullOrWhiteSpace(loginHint))
        {
            parameters.Add("login_hint=" + Uri.EscapeDataString(loginHint.Trim()));
        }

        return AuthorizeEndpoint + "?" + string.Join('&', parameters);
    }

    private static (string? Email, string? Name) ParseIdToken(string? idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken))
        {
            return (null, null);
        }

        var parts = idToken.Split('.');
        if (parts.Length < 2)
        {
            return (null, null);
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var bytes = Convert.FromBase64String(payload);

            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;

            var email = root.TryGetProperty("preferred_username", out var username)
                ? username.GetString()
                : root.TryGetProperty("email", out var emailElement)
                    ? emailElement.GetString()
                    : null;

            var name = root.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            return (email, name);
        }
        catch
        {
            return (null, null);
        }
    }

    private static string CreateRandomBase64Url(int byteLength)
        => Base64Url(RandomNumberGenerator.GetBytes(byteLength));

    private static string Base64Url(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Cancel();
        _http.Dispose();
    }
}
