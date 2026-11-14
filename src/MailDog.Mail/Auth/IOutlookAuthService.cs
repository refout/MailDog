namespace MailDog.Mail.Auth;

public interface IOutlookAuthService
{
    /// <summary>启动浏览器登录流程并立即返回；结果通过 AuthState 事件推送。</summary>
    Task StartAsync(string? loginHint, CancellationToken cancellationToken);

    void Cancel();
}
