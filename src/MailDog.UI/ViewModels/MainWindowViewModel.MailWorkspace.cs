using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Mail;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using MailDog.Contracts.Dto;
using MailDog.Contracts.Ipc;
using MailDog.Contracts.Messages;
using MailDog.Contracts.Platform;
using MailDog.Html;
using MailDog.UI.Services;
using MailDog.UI.Views;

namespace MailDog.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    [RelayCommand]
    private void ToggleSelectionMode() => IsSelectionMode = !IsSelectionMode;

    public int CheckedCount => Messages.Count(message => message.IsSelected);

    public bool HasCheckedMessages => CheckedCount > 0;

    public string CheckedText => $"已选 {CheckedCount} 封";

    public bool IsAllChecked
    {
        get => Messages.Count > 0 && Messages.All(message => message.IsSelected);
        set
        {
            foreach (var message in Messages)
            {
                message.IsSelected = value;
            }

            NotifySelectionState();
        }
    }

    private void NotifySelectionState()
    {
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(HasCheckedMessages));
        OnPropertyChanged(nameof(CheckedText));
        OnPropertyChanged(nameof(IsAllChecked));
        RebuildBatchMoveTargets();
    }

    public bool IsFilterAll
    {
        get => FilterIndex == 0;
        set
        {
            if (value)
            {
                FilterIndex = 0;
            }
        }
    }

    public bool IsFilterUnread
    {
        get => FilterIndex == 1;
        set
        {
            if (value)
            {
                FilterIndex = 1;
            }
        }
    }

    public bool IsFilterFlagged
    {
        get => FilterIndex == 2;
        set
        {
            if (value)
            {
                FilterIndex = 2;
            }
        }
    }

    public bool IsFilterAttachments
    {
        get => FilterIndex == 3;
        set
        {
            if (value)
            {
                FilterIndex = 3;
            }
        }
    }

    partial void OnFilterIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsFilterAll));
        OnPropertyChanged(nameof(IsFilterUnread));
        OnPropertyChanged(nameof(IsFilterFlagged));
        OnPropertyChanged(nameof(IsFilterAttachments));
        ApplyFilter();
    }

    public bool IsSortByDate
    {
        get => SortFieldIndex == 0;
        set
        {
            if (value)
            {
                SortFieldIndex = 0;
            }
        }
    }

    public bool IsSortBySender
    {
        get => SortFieldIndex == 1;
        set
        {
            if (value)
            {
                SortFieldIndex = 1;
            }
        }
    }

    public bool IsSortByCategory
    {
        get => SortFieldIndex == 2;
        set
        {
            if (value)
            {
                SortFieldIndex = 2;
            }
        }
    }

    public bool IsSortBySize
    {
        get => SortFieldIndex == 3;
        set
        {
            if (value)
            {
                SortFieldIndex = 3;
            }
        }
    }

    public bool IsSortByImportance
    {
        get => SortFieldIndex == 4;
        set
        {
            if (value)
            {
                SortFieldIndex = 4;
            }
        }
    }

    public bool IsSortBySubject
    {
        get => SortFieldIndex == 5;
        set
        {
            if (value)
            {
                SortFieldIndex = 5;
            }
        }
    }

    public bool IsSortByType
    {
        get => SortFieldIndex == 6;
        set
        {
            if (value)
            {
                SortFieldIndex = 6;
            }
        }
    }

    public bool IsSortNewestFirst
    {
        get => SortDescending;
        set
        {
            if (value)
            {
                SortDescending = true;
            }
        }
    }

    public bool IsSortOldestFirst
    {
        get => !SortDescending;
        set
        {
            if (value)
            {
                SortDescending = false;
            }
        }
    }

    partial void OnSortFieldIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsSortByDate));
        OnPropertyChanged(nameof(IsSortBySender));
        OnPropertyChanged(nameof(IsSortByCategory));
        OnPropertyChanged(nameof(IsSortBySize));
        OnPropertyChanged(nameof(IsSortByImportance));
        OnPropertyChanged(nameof(IsSortBySubject));
        OnPropertyChanged(nameof(IsSortByType));
        ApplyFilter();
    }

    partial void OnSortDescendingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSortNewestFirst));
        OnPropertyChanged(nameof(IsSortOldestFirst));
        ApplyFilter();
    }

    public string JumpLabel => JumpRangeIndex switch
    {
        1 => "今天",
        2 => "昨天",
        3 => "上周",
        4 => "上个月",
        5 => CustomDate is { } date
            ? $"{date:M/d}"
            : "自定义",
        6 => "去年",
        _ => "跳转"
    };

    public bool IsJumpToday
    {
        get => JumpRangeIndex == 1;
        set
        {
            if (value)
            {
                JumpRangeIndex = 1;
            }
        }
    }

    public bool IsJumpYesterday
    {
        get => JumpRangeIndex == 2;
        set
        {
            if (value)
            {
                JumpRangeIndex = 2;
            }
        }
    }

    public bool IsJumpLastWeek
    {
        get => JumpRangeIndex == 3;
        set
        {
            if (value)
            {
                JumpRangeIndex = 3;
            }
        }
    }

    public bool IsJumpLastMonth
    {
        get => JumpRangeIndex == 4;
        set
        {
            if (value)
            {
                JumpRangeIndex = 4;
            }
        }
    }

    public bool IsJumpLastYear
    {
        get => JumpRangeIndex == 6;
        set
        {
            if (value)
            {
                JumpRangeIndex = 6;
            }
        }
    }

    partial void OnJumpRangeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(JumpLabel));
        OnPropertyChanged(nameof(IsJumpToday));
        OnPropertyChanged(nameof(IsJumpYesterday));
        OnPropertyChanged(nameof(IsJumpLastWeek));
        OnPropertyChanged(nameof(IsJumpLastMonth));
        OnPropertyChanged(nameof(IsJumpLastYear));
        JumpToRange();
    }

    partial void OnCustomDateChanged(DateTime? value)
    {
        OnPropertyChanged(nameof(JumpLabel));
        OnPropertyChanged(nameof(CustomDateText));
        BuildCalendar();
    }

    [RelayCommand]
    private void ApplyCustomRange()
    {
        if (CustomDate is null)
        {
            return;
        }

        if (JumpRangeIndex == 5)
        {
            JumpToRange();
        }
        else
        {
            JumpRangeIndex = 5;
        }
    }

    /// <summary>跳转到指定时间范围的第一封邮件：不改变筛选，只选中并滚动到该邮件。</summary>
    private void JumpToRange()
    {
        if (Messages.Count == 0 || GetJumpRange() is not { } range)
        {
            return;
        }

        // 范围内没有邮件时，退化为定位到时间上最接近的一封。
        var target = Messages.FirstOrDefault(message => InRange(message.Date.ToLocalTime(), range))
                     ?? Messages.OrderBy(message => DistanceTo(message.Date.ToLocalTime(), range)).First();

        SelectedMessage = target;
        ScrollToMessageRequested?.Invoke(target);
    }

    private static bool InRange(DateTimeOffset value, (DateTimeOffset Start, DateTimeOffset End) range)
        => value >= range.Start && value < range.End;

    private static TimeSpan DistanceTo(DateTimeOffset value, (DateTimeOffset Start, DateTimeOffset End) range)
        => value < range.Start
            ? range.Start - value
            : value >= range.End
                ? value - range.End
                : TimeSpan.Zero;


    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MailSummaryViewModel.IsSelected))
        {
            NotifySelectionState();
        }
    }

    [RelayCommand]
    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        UpdateService.CleanupDownloadedInstallers();
        await ConnectAsync();
        await TakePendingNavigationAsync();
    }

    [RelayCommand]
    private async Task RetryConnectionAsync()
    {
        await ConnectAsync();
    }

    [RelayCommand]
    private async Task RetryOfflineAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (!IsConnected)
        {
            await ConnectAsync();
            return;
        }

        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (Accounts.Count == 0)
        {
            return;
        }

        try
        {
            // 统一文件夹跨账户，立即同步需要覆盖全部账户。
            foreach (var account in Accounts)
            {
                await _engine.RequestAsync<OkResponse>(
                    IpcMessageTypes.SyncAccount,
                    new SyncAccountRequest { AccountId = account.Id });
            }

            HasError = false;
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (!HasMore || _activeScope is null || IsLoadingMore)
        {
            return;
        }

        var scope = _activeScope;
        IsLoadingMore = true;

        try
        {
            var response = await _engine.RequestAsync<MessagesPageResponse>(
                IpcMessageTypes.GetMessages,
                BuildMessagesRequest(50, _nextDate, _nextId));

            if (_activeScope != scope)
            {
                // 加载期间切换了文件夹，丢弃过期结果。
                return;
            }

            AddMessages(response.Items);
            _ = RefreshTagsForMessagesAsync(response.Items.Select(item => item.Id).ToArray());
            HasMore = response.HasMore;
            _nextDate = response.NextDate;
            _nextId = response.NextId;
            ApplyFilter();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    [RelayCommand]
    private async Task ToggleReadAsync()
    {
        if (SelectedMessage is not null)
        {
            await ToggleReadMessageAsync(SelectedMessage);
        }
    }

    /// <summary>稍后提醒：把选中邮件收起来，到点后回到未读。</summary>
    [RelayCommand]
    private async Task SnoozeSelectedAsync(string? preset)
    {
        if (SelectedMessage is null)
        {
            return;
        }

        var until = ResolveSnoozeTime(preset);
        if (until is null)
        {
            StatusMessage = "稍后提醒时间无效。";
            return;
        }

        await SnoozeMessageAsync(SelectedMessage, until.Value);
    }

    public async Task SnoozeMessageAsync(MailSummaryViewModel message, DateTimeOffset until)
    {
        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.SnoozeMessage,
                new SnoozeMessageRequest { MessageId = message.Id, UntilUtc = until });

            RemoveMessageFromView(message);
            await ReloadCurrentFolderAsync();
            StatusMessage = $"已稍后提醒，{until.LocalDateTime:M月d日 HH:mm} 回到未读。";
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private static DateTimeOffset? ResolveSnoozeTime(string? preset)
    {
        var now = DateTimeOffset.Now;

        return preset switch
        {
            "1h" => now.AddHours(1),
            "4h" => now.AddHours(4),
            "tonight" => new DateTimeOffset(now.Year, now.Month, now.Day, 20, 0, 0, now.Offset)
                .AddDays(now.Hour >= 20 ? 1 : 0),
            "tomorrow" => new DateTimeOffset(now.Year, now.Month, now.Day, 8, 0, 0, now.Offset)
                .AddDays(1),
            "weekend" => NextSaturdayMorning(now),
            _ => null
        };
    }

    private static DateTimeOffset NextSaturdayMorning(DateTimeOffset now)
    {
        var days = ((int)DayOfWeek.Saturday - (int)now.DayOfWeek + 7) % 7;
        var date = new DateTimeOffset(now.Year, now.Month, now.Day, 9, 0, 0, now.Offset)
            .AddDays(days == 0 ? 7 : days);
        return date;
    }

    /// <summary>按 List-Unsubscribe 退订；有 RFC 8058 一键退订时直接 POST，否则打开网页 / mailto。</summary>
    [RelayCommand]
    private async Task UnsubscribeSelectedAsync()
    {
        if (SelectedMessage is null)
        {
            return;
        }

        try
        {
            var response = await _engine.RequestAsync<UnsubscribeInfoResponse>(
                IpcMessageTypes.GetUnsubscribeInfo,
                new GetUnsubscribeInfoRequest { MessageId = SelectedMessage.Id },
                TimeSpan.FromSeconds(60));

            var info = response.Info;
            if (!info.Available)
            {
                StatusMessage = info.Error ?? "这封邮件没有提供退订方式。";
                return;
            }

            if (info.OneClickPost && info.Url is { } postUrl)
            {
                using var content = new StringContent(
                    "List-Unsubscribe=One-Click",
                    Encoding.UTF8,
                    "application/x-www-form-urlencoded");
                using var result = await UnsubscribeClient.PostAsync(postUrl, content);
                StatusMessage = result.IsSuccessStatusCode
                    ? "已发送一键退订请求。"
                    : $"退订服务返回 {result.StatusCode}；可以再打开网页退订。";
                return;
            }

            var target = info.Url ?? info.MailTo;
            if (target is null)
            {
                StatusMessage = "这封邮件的退订地址不可用。";
                return;
            }

            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            StatusMessage = "已打开退订页面，请在浏览器里完成确认。";
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    /// <summary>标记垃圾邮件，并把发件人加入本地拦截规则（之后的新邮件直接进垃圾箱）。</summary>
    [RelayCommand]
    private async Task MarkJunkAndBlockSenderAsync()
    {
        if (SelectedMessage is null)
        {
            return;
        }

        var message = SelectedMessage;
        var junk = Folders.FirstOrDefault(folder =>
            string.Equals(folder.AccountId, message.AccountId, StringComparison.Ordinal)
            && folder.Role == FolderRole.Junk);

        try
        {
            if (junk is not null && message.FolderId != junk.Id)
            {
                await _engine.RequestAsync<OkResponse>(
                    IpcMessageTypes.MoveMessages,
                    new MoveMessagesRequest { MessageIds = [message.Id], TargetFolderId = junk.Id });
            }

            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.SaveRule,
                new SaveRuleRequest
                {
                    Rule = new RuleDto
                    {
                        AccountId = message.AccountId,
                        Field = "from",
                        Pattern = message.SenderAddress,
                        MoveToJunk = true,
                        Mute = true,
                        Enabled = true
                    }
                });

            await LoadRulesAsync();
            RemoveMessageFromView(message);
            await ReloadCurrentFolderAsync();
            StatusMessage = junk is null
                ? $"已记住发件人 {message.SenderAddress}；该账户没有垃圾文件夹，后续只静音通知。"
                : $"已标记垃圾邮件，并记住发件人 {message.SenderAddress}。";
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    /// <summary>切换指定邮件的已读状态；列表行快捷操作与工具栏共用。</summary>
    public async Task ToggleReadMessageAsync(MailSummaryViewModel message)
        => await MarkReadAsync(message, !message.IsRead);

    [RelayCommand]
    private async Task ToggleFlagAsync()
    {
        if (SelectedMessage is null)
        {
            return;
        }

        var value = !SelectedMessage.IsFlagged;

        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.SetFlag,
                new SetFlagRequest { MessageId = SelectedMessage.Id, Value = value });

            SelectedMessage.IsFlagged = value;
            ApplyFilter();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (SelectedMessage is not null)
        {
            await DeleteMessageAsync(SelectedMessage);
        }
    }

    /// <summary>删除指定邮件；列表行悬停快捷操作也走这里。</summary>
    public async Task DeleteMessageAsync(MailSummaryViewModel message)
    {
        var permanent = _foldersById.GetValueOrDefault(message.FolderId)?.Role == FolderRole.Trash;
        StatusMessage = permanent ? "正在永久删除邮件…" : "正在删除邮件…";

        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.DeleteMessage,
                new DeleteMessageRequest { MessageId = message.Id, Permanent = permanent });

            RemoveMessageFromView(message);
            await ReloadCurrentFolderAsync();
            StatusMessage = permanent ? "邮件已永久删除。" : "邮件已移至已删除。";
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    [RelayCommand]
    private async Task ArchiveSelectedAsync()
    {
        if (SelectedMessage is not null)
        {
            await ArchiveMessageAsync(SelectedMessage);
        }
    }

    /// <summary>把指定邮件移到同账户归档；列表行快捷操作与工具栏共用。</summary>
    public async Task ArchiveMessageAsync(MailSummaryViewModel message)
    {
        var source = _foldersById.GetValueOrDefault(message.FolderId);
        if (source is null)
        {
            StatusMessage = "找不到邮件所在的文件夹。";
            return;
        }

        var archive = Folders.FirstOrDefault(folder =>
            string.Equals(folder.AccountId, source.AccountId, StringComparison.Ordinal)
            && folder.Role == FolderRole.Archive
            && folder.Id != source.Id);

        if (archive is null)
        {
            StatusMessage = "当前账户没有归档文件夹，请先在服务器上创建归档文件夹。";
            return;
        }

        await MoveMessageToFolderAsync(message, archive.Id);
    }

    [RelayCommand]
    private async Task MoveSelectedToFolderAsync(long targetFolderId)
    {
        if (SelectedMessage is not null)
        {
            await MoveMessageToFolderAsync(SelectedMessage, targetFolderId);
        }
    }

    /// <summary>把指定邮件移动到同账户文件夹；列表行与阅读区共用。</summary>
    public async Task MoveMessageToFolderAsync(MailSummaryViewModel message, long targetFolderId)
    {
        var target = Folders.FirstOrDefault(folder => folder.Id == targetFolderId
            && string.Equals(folder.AccountId, message.AccountId, StringComparison.Ordinal));
        if (target is null)
        {
            StatusMessage = "只能把邮件移动到同一账户的文件夹。";
            return;
        }

        StatusMessage = $"正在移动到“{target.Name}”…";

        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.MoveMessage,
                new MoveMessageRequest { MessageId = message.Id, TargetFolderId = targetFolderId });

            RemoveMessageFromView(message);
            await ReloadCurrentFolderAsync();
            StatusMessage = $"邮件已移动到“{target.Name}”。";
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private void RemoveMessageFromView(MailSummaryViewModel message)
    {
        RemoveMessagesFromView([message]);
    }

    private void RemoveMessagesFromView(IEnumerable<MailSummaryViewModel> messages)
    {
        var removed = messages.ToArray();
        if (removed.Length == 0)
        {
            return;
        }

        foreach (var message in removed)
        {
            _allMessages.Remove(message);
            Messages.Remove(message);
            message.IsSelected = false;
        }

        if (SelectedMessage is { } selected && removed.Contains(selected))
        {
            SelectedMessage = null;
            _currentBody = null;
            Reader.Reset();
        }

        ApplyFilter();
        NotifySelectionState();
    }

    private void RebuildMoveTargets()
    {
        MoveTargets.Clear();
        if (SelectedMessage is null)
        {
            OnPropertyChanged(nameof(HasMoveTargets));
            return;
        }

        var targets = Folders
            .Where(folder => string.Equals(folder.AccountId, SelectedMessage.AccountId, StringComparison.Ordinal)
                             && folder.Id != SelectedMessage.FolderId
                             && folder.Role != FolderRole.Drafts)
            .OrderBy(folder => folder.Role == FolderRole.Other ? 1 : 0)
            .ThenBy(folder => folder.Name, StringComparer.CurrentCulture);

        foreach (var folder in targets)
        {
            MoveTargets.Add(new MoveTargetViewModel(folder, MoveSelectedToFolderCommand));
        }

        OnPropertyChanged(nameof(HasMoveTargets));
        OnPropertyChanged(nameof(SelectedMessageFolder));
        OnPropertyChanged(nameof(SelectedMessageInTrash));
        OnPropertyChanged(nameof(SelectedMessageInDrafts));
        OnPropertyChanged(nameof(DeleteActionText));
        RebuildBatchMoveTargets();
    }

    private void RebuildBatchMoveTargets()
    {
        BatchMoveTargets.Clear();

        var checkedMessages = Messages.Where(message => message.IsSelected).ToArray();
        var accountIds = checkedMessages.Select(message => message.AccountId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (accountIds.Length == 1)
        {
            var sourceFolderIds = checkedMessages.Select(message => message.FolderId).ToHashSet();
            var accountId = accountIds[0];
            var targets = Folders
                .Where(folder => string.Equals(folder.AccountId, accountId, StringComparison.Ordinal)
                                 && folder.Role != FolderRole.Drafts
                                 && !sourceFolderIds.Contains(folder.Id))
                .OrderBy(folder => folder.Role == FolderRole.Other ? 1 : 0)
                .ThenBy(folder => folder.Name, StringComparer.CurrentCulture);

            foreach (var folder in targets)
            {
                BatchMoveTargets.Add(new MoveTargetViewModel(folder, BatchMoveToFolderCommand));
            }
        }

        OnPropertyChanged(nameof(HasBatchMoveTargets));
    }

    public void BeginCreateFolder(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return;
        }

        _folderEditorAccountId = accountId;
        _folderEditorTarget = null;
        FolderEditorTitle = "新建文件夹";
        FolderEditorName = string.Empty;
        IsFolderDeleteConfirmOpen = false;
        IsFolderEditorOpen = true;
    }

    public void BeginRenameFolder(FolderViewModel folder)
    {
        if (folder.Role != FolderRole.Other)
        {
            StatusMessage = "系统文件夹不能重命名。";
            return;
        }

        _folderEditorAccountId = folder.AccountId;
        _folderEditorTarget = folder;
        FolderEditorTitle = $"重命名“{folder.Name}”";
        FolderEditorName = folder.Name;
        IsFolderDeleteConfirmOpen = false;
        IsFolderEditorOpen = true;
    }

    public void RequestDeleteFolder(FolderViewModel folder)
    {
        if (folder.Role != FolderRole.Other)
        {
            StatusMessage = "系统文件夹不能删除。";
            return;
        }

        _folderDeleteTarget = folder;
        FolderDeleteName = folder.Name;
        IsFolderEditorOpen = false;
        IsFolderDeleteConfirmOpen = true;
    }

    [RelayCommand]
    private void CancelFolderEdit()
    {
        IsFolderEditorOpen = false;
        _folderEditorTarget = null;
    }

    [RelayCommand]
    private void CancelFolderDelete()
    {
        IsFolderDeleteConfirmOpen = false;
        _folderDeleteTarget = null;
    }

    [RelayCommand]
    private async Task SubmitFolderEditAsync()
    {
        var name = FolderEditorName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusMessage = "文件夹名称不能为空。";
            return;
        }

        var target = _folderEditorTarget;
        if (target is null && string.IsNullOrWhiteSpace(_folderEditorAccountId))
        {
            StatusMessage = "找不到要创建文件夹的账户。";
            return;
        }

        try
        {
            if (target is null)
            {
                await _engine.RequestAsync<OkResponse>(
                    IpcMessageTypes.CreateFolder,
                    new CreateFolderRequest { AccountId = _folderEditorAccountId!, Name = name });
                StatusMessage = $"文件夹“{name}”已创建。";
            }
            else
            {
                await _engine.RequestAsync<OkResponse>(
                    IpcMessageTypes.RenameFolder,
                    new RenameFolderRequest { FolderId = target.Id, Name = name });
                StatusMessage = $"文件夹已重命名为“{name}”。";
            }

            IsFolderEditorOpen = false;
            _folderEditorTarget = null;
            await RefreshFolderTreeAsync();
            await RefreshMessagesAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    [RelayCommand]
    private async Task ConfirmDeleteFolderAsync()
    {
        var folder = _folderDeleteTarget;
        if (folder is null)
        {
            return;
        }

        var wasSelected = SelectedFolder?.Id == folder.Id;
        StatusMessage = $"正在删除文件夹“{folder.Name}”…";

        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.DeleteFolder,
                new DeleteFolderRequest { FolderId = folder.Id });

            IsFolderDeleteConfirmOpen = false;
            _folderDeleteTarget = null;

            if (wasSelected)
            {
                SelectedFolder = null;
                SelectedMessage = null;
                _currentBody = null;
                Reader.Reset();
                _allMessages.Clear();
                Messages.Clear();
            }

            await RefreshFolderTreeAsync();
            StatusMessage = $"文件夹“{folder.Name}”已删除。";

            if (wasSelected)
            {
                var account = Accounts.FirstOrDefault(item => item.Id == folder.AccountId);
                if (account is not null)
                {
                    SelectedAccount = account;
                    SelectedUnifiedFolder = null;
                    SelectedFolder = account.Folders.FirstOrDefault(item => item.Role == FolderRole.Inbox)
                                     ?? account.Folders.FirstOrDefault();
                }
            }
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }


    [RelayCommand]
    private void ShowImages()
    {
        _allowRemoteImages = true;
        RenderCurrentBody();
    }

    private async Task<AttachmentBytesDto> LoadAttachmentBytesAsync(AttachmentViewModel attachment)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("尚未连接 Engine。");
        }

        var response = await _engine.RequestAsync<AttachmentBytesResponse>(
            IpcMessageTypes.ReadAttachment,
            new ReadAttachmentRequest
            {
                MessageId = attachment.MessageId,
                Index = attachment.Index
            },
            TimeSpan.FromSeconds(60));

        return response.Attachment;
    }

    private async Task DownloadAttachmentAsync(AttachmentViewModel attachment)
    {
        if (!IsConnected)
        {
            return;
        }

        try
        {
            var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
                ?.MainWindow;

            if (window is null)
            {
                return;
            }

            var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "保存附件",
                SuggestedFileName = AttachmentFileNames.Sanitize(attachment.FileName),
                DefaultExtension = Path.GetExtension(attachment.FileName).TrimStart('.'),
                ShowOverwritePrompt = true
            });

            var path = file?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var response = await _engine.RequestAsync<AttachmentSavedResponse>(
                IpcMessageTypes.DownloadAttachment,
                new DownloadAttachmentRequest
                {
                    MessageId = attachment.MessageId,
                    Index = attachment.Index,
                    DestinationPath = path
                },
                TimeSpan.FromSeconds(120));

            HasError = false;
            StatusMessage = $"附件已保存：{response.Path}";
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private static string AttachmentCacheDirectory()
        => Path.Combine(ProductPaths.DataDirectory, "attachments");

    /// <summary>批量保存到用户选择的文件夹；同名附件自动加序号，不覆盖已有文件。</summary>
    private async Task SaveAttachmentsAsync(IReadOnlyList<AttachmentViewModel> attachments)
    {
        if (!IsConnected || attachments.Count == 0)
        {
            return;
        }

        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow;
        if (window is null)
        {
            return;
        }

        try
        {
            var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "保存附件",
                AllowMultiple = false
            });
            var directory = folders.Count == 0 ? null : folders[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var saved = 0;
            var errors = new List<string>();
            foreach (var attachment in attachments)
            {
                var path = AttachmentFileNames.CreateUniquePath(directory, attachment.FileName, reserved);
                try
                {
                    await _engine.RequestAsync<AttachmentSavedResponse>(
                        IpcMessageTypes.DownloadAttachment,
                        new DownloadAttachmentRequest
                        {
                            MessageId = attachment.MessageId,
                            Index = attachment.Index,
                            DestinationPath = path
                        },
                        TimeSpan.FromSeconds(120));
                    saved++;
                }
                catch (Exception exception)
                {
                    errors.Add($"{attachment.FileName}: {GetErrorMessage(exception)}");
                }
            }

            if (saved == attachments.Count)
            {
                HasError = false;
                StatusMessage = $"已保存 {saved} 个附件到 {directory}。";
                return;
            }

            HasError = true;
            var detail = errors.Count == 0 ? string.Empty : $"（{errors[0]}）";
            StatusMessage = $"已保存 {saved}/{attachments.Count} 个附件{detail}";
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    /// <summary>拖出附件：先落到固定的本地缓存目录，再交给拖放层生成文件对象。</summary>
    public async Task<IReadOnlyList<string>> PrepareAttachmentDragFilesAsync(
        IReadOnlyList<AttachmentViewModel> attachments)
    {
        if (!IsConnected || attachments.Count == 0)
        {
            return [];
        }

        var directory = AttachmentCacheDirectory();
        Directory.CreateDirectory(directory);
        var paths = new List<string>();
        var errors = new List<string>();
        foreach (var attachment in attachments)
        {
            var path = Path.Combine(
                directory,
                AttachmentFileNames.Sanitize($"{attachment.MessageId}-{attachment.Index}-{attachment.FileName}"));
            try
            {
                await _engine.RequestAsync<AttachmentSavedResponse>(
                    IpcMessageTypes.DownloadAttachment,
                    new DownloadAttachmentRequest
                    {
                        MessageId = attachment.MessageId,
                        Index = attachment.Index,
                        DestinationPath = path
                    },
                    TimeSpan.FromSeconds(120));
                paths.Add(path);
            }
            catch (Exception exception)
            {
                errors.Add($"{attachment.FileName}: {GetErrorMessage(exception)}");
            }
        }

        AttachmentCacheQuota.Enforce(directory, AttachmentCacheQuota.DefaultLimitBytes, paths);

        if (paths.Count == 0)
        {
            HasError = true;
            StatusMessage = errors.Count == 0
                ? "没有可拖出的附件。"
                : $"附件准备失败：{errors[0]}";
        }
        else
        {
            HasError = false;
            StatusMessage = $"已准备 {paths.Count} 个附件，可拖到资源管理器。";
        }

        return paths;
    }

    private Task OpenAttachmentAsync(AttachmentViewModel attachment)
    {
        if (!IsConnected)
        {
            return Task.CompletedTask;
        }

        return OpenAttachmentCoreAsync(attachment);
    }

    private async Task OpenAttachmentCoreAsync(AttachmentViewModel attachment)
    {
        try
        {
            var directory = AttachmentCacheDirectory();
            Directory.CreateDirectory(directory);

            var path = Path.Combine(
                directory,
                AttachmentFileNames.Sanitize($"{attachment.MessageId}-{attachment.Index}-{attachment.FileName}"));

            var response = await _engine.RequestAsync<AttachmentSavedResponse>(
                IpcMessageTypes.DownloadAttachment,
                new DownloadAttachmentRequest
                {
                    MessageId = attachment.MessageId,
                    Index = attachment.Index,
                    DestinationPath = path
                },
                TimeSpan.FromSeconds(120));

            Process.Start(new ProcessStartInfo(response.Path)
            {
                UseShellExecute = true
            });

            StatusMessage = $"已打开附件：{response.FileName}";
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    [RelayCommand]
    private void SelectAllMessages()
    {
        if (Messages.Count == 0)
        {
            return;
        }

        if (!IsSelectionMode)
        {
            IsSelectionMode = true;
        }

        var select = !IsAllChecked;

        foreach (var message in Messages)
        {
            message.IsSelected = select;
        }

        NotifySelectionState();
    }

    [RelayCommand]
    private void ClearCheckedMessages()
    {
        if (!HasCheckedMessages)
        {
            return;
        }

        foreach (var message in Messages)
        {
            message.IsSelected = false;
        }

        NotifySelectionState();
    }

    [RelayCommand]
    private Task MarkCheckedReadAsync() => SetCheckedReadAsync(true);

    [RelayCommand]
    private Task MarkCheckedUnreadAsync() => SetCheckedReadAsync(false);

    [RelayCommand]
    private Task FlagCheckedAsync() => SetCheckedFlagAsync(true);

    [RelayCommand]
    private Task UnflagCheckedAsync() => SetCheckedFlagAsync(false);

    [RelayCommand]
    private async Task BatchArchiveAsync()
    {
        var targets = CheckedTargets();
        if (targets.Length == 0)
        {
            return;
        }

        IsBatchBusy = true;
        var moved = new List<MailSummaryViewModel>();
        var missing = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            foreach (var group in targets.GroupBy(message => message.AccountId))
            {
                var source = _foldersById.GetValueOrDefault(group.First().FolderId);
                var archive = Folders.FirstOrDefault(folder =>
                    string.Equals(folder.AccountId, group.Key, StringComparison.Ordinal)
                    && folder.Role == FolderRole.Archive
                    && folder.Id != source?.Id);

                if (archive is null)
                {
                    missing.Add(group.Key);
                    continue;
                }

                var batch = group.Where(message => message.FolderId != archive.Id).ToArray();
                if (batch.Length == 0)
                {
                    continue;
                }

                await _engine.RequestAsync<OkResponse>(
                    IpcMessageTypes.MoveMessages,
                    new MoveMessagesRequest
                    {
                        MessageIds = batch.Select(message => message.Id).ToArray(),
                        TargetFolderId = archive.Id
                    });

                moved.AddRange(batch);
            }

            if (moved.Count > 0)
            {
                RemoveMessagesFromView(moved);
                await ReloadCurrentFolderAsync();
            }

            StatusMessage = moved.Count == 0
                ? "没有可归档的邮件：所选邮件已在归档，或账户没有归档文件夹。"
                : missing.Count == 0
                    ? $"已归档 {moved.Count} 封邮件。"
                    : $"已归档 {moved.Count} 封邮件；{missing.Count} 个账户没有归档文件夹，已跳过。";
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            IsBatchBusy = false;
        }
    }

    [RelayCommand]
    private async Task BatchMarkJunkAsync()
    {
        var targets = CheckedTargets();
        if (targets.Length == 0)
        {
            return;
        }

        IsBatchBusy = true;
        var moved = new List<MailSummaryViewModel>();
        var missing = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            foreach (var group in targets.GroupBy(message => message.AccountId))
            {
                var junk = Folders.FirstOrDefault(folder =>
                    string.Equals(folder.AccountId, group.Key, StringComparison.Ordinal)
                    && folder.Role == FolderRole.Junk);

                if (junk is null)
                {
                    missing.Add(group.Key);
                    continue;
                }

                var batch = group.Where(message => message.FolderId != junk.Id).ToArray();
                if (batch.Length == 0)
                {
                    continue;
                }

                await _engine.RequestAsync<OkResponse>(
                    IpcMessageTypes.MoveMessages,
                    new MoveMessagesRequest
                    {
                        MessageIds = batch.Select(message => message.Id).ToArray(),
                        TargetFolderId = junk.Id
                    });

                moved.AddRange(batch);
            }

            if (moved.Count > 0)
            {
                RemoveMessagesFromView(moved);
                await ReloadCurrentFolderAsync();
            }

            StatusMessage = moved.Count == 0
                ? "没有可标记的邮件：所选邮件已在垃圾邮件，或账户没有垃圾邮件文件夹。"
                : missing.Count == 0
                    ? $"已将 {moved.Count} 封邮件标记为垃圾邮件。"
                    : $"已标记 {moved.Count} 封邮件；{missing.Count} 个账户没有垃圾邮件文件夹，已跳过。";
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            IsBatchBusy = false;
        }
    }

    [RelayCommand]
    private async Task BatchDeleteAsync()
    {
        var targets = CheckedTargets();
        if (targets.Length == 0)
        {
            return;
        }

        IsBatchBusy = true;

        try
        {
            foreach (var group in targets.GroupBy(message =>
                         _foldersById.GetValueOrDefault(message.FolderId)?.Role == FolderRole.Trash))
            {
                await _engine.RequestAsync<OkResponse>(
                    IpcMessageTypes.DeleteMessages,
                    new DeleteMessagesRequest
                    {
                        MessageIds = group.Select(message => message.Id).ToArray(),
                        Permanent = group.Key
                    });
            }

            var permanentCount = targets.Count(message =>
                _foldersById.GetValueOrDefault(message.FolderId)?.Role == FolderRole.Trash);
            RemoveMessagesFromView(targets);
            await ReloadCurrentFolderAsync();
            StatusMessage = permanentCount == targets.Length
                ? $"已永久删除 {targets.Length} 封邮件。"
                : $"已删除 {targets.Length} 封邮件（{permanentCount} 封来自垃圾箱，已永久删除）。";
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            IsBatchBusy = false;
        }
    }

    [RelayCommand]
    private async Task BatchMoveToFolderAsync(long targetFolderId)
    {
        var targets = CheckedTargets();
        if (targets.Length == 0)
        {
            return;
        }

        var accountIds = targets.Select(message => message.AccountId).Distinct(StringComparer.Ordinal).ToArray();
        if (accountIds.Length != 1)
        {
            StatusMessage = "批量移动只支持同一个账户的邮件；跨账户请分开选择。";
            return;
        }

        var target = Folders.FirstOrDefault(folder => folder.Id == targetFolderId
            && string.Equals(folder.AccountId, accountIds[0], StringComparison.Ordinal)
            && folder.Role != FolderRole.Drafts);
        if (target is null)
        {
            StatusMessage = "只能把邮件移动到同一账户的文件夹。";
            return;
        }

        var batchMessages = targets.Where(message => message.FolderId != target.Id).ToArray();
        if (batchMessages.Length == 0)
        {
            StatusMessage = "所选邮件已经在目标文件夹。";
            return;
        }

        IsBatchBusy = true;

        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.MoveMessages,
                new MoveMessagesRequest
                {
                    MessageIds = batchMessages.Select(message => message.Id).ToArray(),
                    TargetFolderId = target.Id
                });

            RemoveMessagesFromView(batchMessages);
            await ReloadCurrentFolderAsync();
            StatusMessage = $"已把 {batchMessages.Length} 封邮件移动到“{target.Name}”。";
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            IsBatchBusy = false;
        }
    }

    private async Task SetCheckedReadAsync(bool isRead)
    {
        var targets = CheckedTargets();
        if (targets.Length == 0)
        {
            return;
        }

        IsBatchBusy = true;

        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.MarkReadBatch,
                new MarkReadBatchRequest
                {
                    MessageIds = targets.Select(message => message.Id).ToArray(),
                    IsRead = isRead
                });

            var changed = 0;

            foreach (var message in targets)
            {
                if (message.IsRead != isRead)
                {
                    message.IsRead = isRead;
                    changed++;
                }

                message.IsSelected = false;
            }

            if (changed > 0)
            {
                foreach (var message in targets)
                {
                    AdjustFolderUnread(message.FolderId, isRead ? -1 : 1);
                }

                RecalculateUnifiedCounts();
            }

            if (FilterIndex == 1)
            {
                ApplyFilter();
            }

            StatusMessage = $"已更新 {targets.Length} 封邮件";
            NotifySelectionState();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            IsBatchBusy = false;
        }
    }

    private async Task SetCheckedFlagAsync(bool value)
    {
        var targets = CheckedTargets();
        if (targets.Length == 0)
        {
            return;
        }

        IsBatchBusy = true;

        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.SetFlagBatch,
                new SetFlagBatchRequest
                {
                    MessageIds = targets.Select(message => message.Id).ToArray(),
                    Value = value
                });

            foreach (var message in targets)
            {
                message.IsFlagged = value;
                message.IsSelected = false;
            }

            if (FilterIndex == 2)
            {
                ApplyFilter();
            }

            StatusMessage = $"已更新 {targets.Length} 封邮件的旗标";
            NotifySelectionState();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            IsBatchBusy = false;
        }
    }

    private MailSummaryViewModel[] CheckedTargets()
        => IsBatchBusy
            ? []
            : Messages.Where(message => message.IsSelected).ToArray();

}
