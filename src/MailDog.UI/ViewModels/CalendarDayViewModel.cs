using System.Globalization;

namespace MailDog.UI.ViewModels;

/// <summary>日历面板中的一个日期单元格。</summary>
public sealed class CalendarDayViewModel(
    DateTime date,
    bool isCurrentMonth,
    bool isSelected,
    bool isToday,
    bool hasEvents = false,
    IReadOnlyList<string>? eventTitles = null)
{
    public DateTime Date { get; } = date;

    public string DayText { get; } = date.Day.ToString();

    public string AutomationName { get; } = $"选择日期：{date.ToString("M月d日", CultureInfo.InvariantCulture)}";

    public bool IsCurrentMonth { get; } = isCurrentMonth;

    public bool IsSelected { get; } = isSelected;

    public bool IsToday { get; } = isToday;

    public bool HasEvents { get; } = hasEvents;

    public IReadOnlyList<string> EventTitles { get; } = eventTitles ?? [];

    public bool HasPrimaryEvent => EventTitles.Count > 0;

    public bool HasSecondaryEvent => EventTitles.Count > 1;

    public bool HasMoreEvents => EventTitles.Count > 2;

    public string PrimaryEventTitle => EventTitles.Count > 0 ? EventTitles[0] : string.Empty;

    public string SecondaryEventTitle => EventTitles.Count > 1 ? EventTitles[1] : string.Empty;

    public string MoreEventsText => EventTitles.Count > 2 ? $"+{EventTitles.Count - 2}" : string.Empty;
}
