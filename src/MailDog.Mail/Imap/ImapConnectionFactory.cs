using System.Net.Security;
using MailDog.Mail.Accounts;
using MailDog.Mail.Auth;
using MailDog.Mail.Storage;
using MailKit.Net.Imap;
using MailKit.Security;

namespace MailDog.Mail.Imap;

/// <summary>建立并认证一个 IMAP 连接（同步会话与 IDLE 监听共用）。</summary>
internal static class ImapConnectionFactory
{
    public static Task<ImapClient> ConnectAsync(
        AccountRecord account,
        AccountService accounts,
        IAccessTokenProvider tokens,
        CancellationToken cancellationToken)
        => ConnectAsync(account, accounts, tokens, null, cancellationToken);

    public static async Task<ImapClient> ConnectAsync(
        AccountRecord account,
        AccountService accounts,
        IAccessTokenProvider tokens,
        RemoteCertificateValidationCallback? certificateValidator,
        CancellationToken cancellationToken)
    {
        var config = accounts.GetServerConfig(account);
        var protocolLogger = new AuthenticationTraceLogger();
        var client = new ImapClient(protocolLogger) { Timeout = 30000 };

        if (certificateValidator is not null)
        {
            client.ServerCertificateValidationCallback = certificateValidator;
        }

        var socketOptions = config.ImapStartTls
            ? SecureSocketOptions.StartTls
            : SecureSocketOptions.SslOnConnect;

        try
        {
            await client.ConnectAsync(
                config.ImapHost,
                config.ImapPort,
                socketOptions,
                cancellationToken).ConfigureAwait(false);

            if (account.AuthKind == "oauth2")
            {
                var token = await tokens.GetAccessTokenAsync(account, cancellationToken).ConfigureAwait(false);

                try
                {
                    await client.AuthenticateAsync(
                        new SaslMechanismOAuth2(account.Email, token),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (AuthenticationException exception)
                {
                    // MailKit 会把服务端的 NO 响应吞成 "Authentication failed."；
                    // 这里补回服务器原文，便于区分令牌失效与邮箱协议被禁用。
                    var detail = protocolLogger.LastFailure;
                    if (string.IsNullOrWhiteSpace(detail))
                    {
                        throw;
                    }

                    throw new AuthenticationException(
                        $"{exception.Message}（{FormatServerFailure(detail)}）",
                        exception);
                }
            }
            else
            {
                var password = accounts.GetPassword(account)
                    ?? throw new AuthenticationException("未找到账户授权码，请重新添加账户。");

                await client.AuthenticateAsync(account.Email, password, cancellationToken).ConfigureAwait(false);
            }

            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static string FormatServerFailure(string line)
    {
        var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 ? parts[2].Trim() : line;
    }
}
