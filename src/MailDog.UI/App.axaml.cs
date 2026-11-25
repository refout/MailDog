using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using AvaloniaRichEditor;
using MailDog.UI.Services;
using MailDog.UI.ViewModels;
using MailDog.UI.Views;

namespace MailDog.UI;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        ConfigureRichEditorLocalization();
    }

    private static void ConfigureRichEditorLocalization()
    {
        RichEditorLocalization.Language = "zh";
        RichEditorLocalization.Register("zh", new Dictionary<string, string>
        {
            ["Hyperlink"] = "链接",
            ["InsertLink"] = "插入链接…",
            ["EditLink"] = "编辑链接…",
            ["RemoveLink"] = "移除链接",
            ["OpenLink"] = "打开链接",
            ["CopyLink"] = "复制链接",
            ["OK"] = "确定",
            ["Cancel"] = "取消",
            ["Cut"] = "剪切",
            ["Copy"] = "复制",
            ["Paste"] = "粘贴",
            ["Delete"] = "删除",
            ["SelectAll"] = "全选",
            ["Undo"] = "撤销",
            ["Redo"] = "重做",
            ["Bold"] = "加粗",
            ["Italic"] = "斜体",
            ["Underline"] = "下划线",
            ["Strikethrough"] = "删除线"
        });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            ApplyAppIdentity();

            var engine = new EngineClient();
            var viewModel = new MainWindowViewModel(engine);

            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = new MainWindow(viewModel);
        }

        base.OnFrameworkInitializationCompleted();
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appUserModelId);

    /// <summary>与 Engine 共用应用标识，通知与任务栏才会认成同一个 MailDog。</summary>
    private static void ApplyAppIdentity()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            SetCurrentProcessExplicitAppUserModelID("MailDog.MailDog.App");
        }
        catch
        {
            // 旧系统不支持时忽略。
        }
    }
}
