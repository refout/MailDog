namespace MailDog.UI.ViewModels;

/// <summary>日历面板中的一个日期单元格。</summary>
public sealed class CalendarDayViewModel(
    DateTime date,
    bool isCurrentMonth,
    bool isSelected,
    bool isToday)
{
    public DateTime Date { get; } = date;

    public string DayText { get; } = date.Day.ToString();

    public bool IsCurrentMonth { get; } = isCurrentMonth;

    public bool IsSelected { get; } = isSelected;

    public bool IsToday { get; } = isToday;
}
