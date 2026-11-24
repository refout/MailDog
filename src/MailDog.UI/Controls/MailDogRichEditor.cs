using Avalonia.Input;
using AvaloniaRichEditor.Controls;

namespace MailDog.UI.Controls;

/// <summary>
/// AvaloniaRichEditor 的薄封装：补一个可编程触发的链接对话框入口。
/// 链接对话框本身没有公开 API，只能复用控件的 Ctrl+K 命令。
/// </summary>
public sealed class MailDogRichEditor : RichEditor
{
    public void OpenLinkDialog()
    {
        if (IsReadOnly)
        {
            return;
        }

        Focus();
        var args = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.K,
            KeyModifiers = KeyModifiers.Control
        };
        OnKeyDown(args);
    }
}
