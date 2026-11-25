namespace MailDog.Engine.Tray;

/// <summary>通知上的快捷操作；Action URI 会走 maildog: 协议回到引擎处理。</summary>
public sealed record NotificationAction(string Id, string Label, string Uri);
