using MailDog.Mail.Accounts;
using MailDog.Mail.Auth;
using MailDog.Mail.Storage;
using MailKit.Net.Imap;
using MailKit.Security;

namespace MailDog.Mail.Imap;

/// <summary>建立并认证一个 IMAP 连接（同步会话与 IDLE 监听共用）。</summary>
internal static class ImapConnectionFactory
{
    public static async Task<ImapClient> ConnectAsync(
        AccountRecord account,
        AccountService accounts,
        IAccessTokenProvider tokens,
        CancellationToken cancellationToken)
    {
        var config = accounts.GetServerConfig(account);
        var client = new ImapClient { Timeout = 30000 };
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
                await client.AuthenticateAsync(
                    new SaslMechanismOAuth2(account.Email, token),
                    cancellationToken).ConfigureAwait(false);
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
}
