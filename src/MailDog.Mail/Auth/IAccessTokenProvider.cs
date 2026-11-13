using MailDog.Mail.Storage;

namespace MailDog.Mail.Auth;

public interface IAccessTokenProvider
{
    Task<string> GetAccessTokenAsync(AccountRecord account, CancellationToken cancellationToken);
}

public sealed class NullAccessTokenProvider : IAccessTokenProvider
{
    public Task<string> GetAccessTokenAsync(AccountRecord account, CancellationToken cancellationToken)
        => throw new NotSupportedException("OAuth 账户尚未配置令牌提供程序。");
}
