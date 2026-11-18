using System.Windows.Input;

namespace MailDog.UI.ViewModels;

/// <summary>阅读区“移动到”菜单项。携带目标文件夹与当前选中邮件的移动命令。</summary>
public sealed class MoveTargetViewModel
{
    public MoveTargetViewModel(FolderViewModel folder, ICommand command)
    {
        FolderId = folder.Id;
        Name = folder.Name;
        Command = command;
    }

    public long FolderId { get; }

    public string Name { get; }

    public ICommand Command { get; }
}
