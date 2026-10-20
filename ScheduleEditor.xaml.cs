using System.Windows;
using System.Windows.Controls;
using DiskImager.Core;

namespace DiskImager;

public partial class ScheduleEditor : UserControl
{
    private static readonly DayOfWeek[] WeekOrder =
    {
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
    };

    private readonly Dictionary<DayOfWeek, CheckBox> _days = new();

    /// <summary>Raised whenever the schedule is edited (kind, time, days, day of month).</summary>
    public event Action? Changed;

    public ScheduleEditor()
    {
        InitializeComponent();
        foreach (var d in WeekOrder)
        {
            var cb = new CheckBox { Content = d.ToString()[..3], Margin = new Thickness(0, 0, 12, 0) };
            cb.Click += (_, _) => Changed?.Invoke();
            _days[d] = cb;
            DaysPanel.Children.Add(cb);
        }
        KindBox.SelectedIndex = 0;
        InputBehaviors.AttachTime(TimeBox);
        InputBehaviors.AttachNumber(DayBox, 2);
        TimeBox.TextChanged += (_, _) => Changed?.Invoke();
        DayBox.TextChanged += (_, _) => Changed?.Invoke();
    }

    /// <summary>The schedule as it stands, or null if it isn't complete yet (no exceptions, for live checks).</summary>
    public Schedule? Peek()
    {
        var s = new Schedule { Kind = (ScheduleKind)Math.Max(0, KindBox.SelectedIndex) };
        if (s.Kind == ScheduleKind.None) return s;
        if (!TimeSpan.TryParse(InputBehaviors.CompleteTime(TimeBox.Text), out var t) || t < TimeSpan.Zero || t >= TimeSpan.FromDays(1)) return null;
        s.Time = $"{t.Hours:D2}:{t.Minutes:D2}";
        if (s.Kind == ScheduleKind.Weekly)
        {
            s.Days = _days.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();
            if (s.Days.Count == 0) return null;
        }
        if (s.Kind == ScheduleKind.Monthly)
        {
            if (!int.TryParse(DayBox.Text.Trim(), out int day) || day < 1 || day > 31) return null;
            s.DayOfMonth = day;
        }
        return s;
    }

    public void SetTime(string time) => TimeBox.Text = time;

    public void Load(Schedule s)
    {
        KindBox.SelectedIndex = (int)s.Kind;
        TimeBox.Text = s.Time;
        DayBox.Text = s.DayOfMonth.ToString();
        foreach (var (day, cb) in _days) cb.IsChecked = s.Days.Contains(day);
        UpdateVisibility();
    }

    /// <summary>Returns the schedule, or throws with a user-readable message if it's invalid.</summary>
    public Schedule Read(string label)
    {
        var s = new Schedule { Kind = (ScheduleKind)KindBox.SelectedIndex };
        if (s.Kind == ScheduleKind.None) return s;

        TimeBox.Text = InputBehaviors.CompleteTime(TimeBox.Text);
        if (!TimeSpan.TryParse(TimeBox.Text, out var t) || t < TimeSpan.Zero || t >= TimeSpan.FromDays(1))
            throw new ArgumentException($"{label}: enter a time like 22:00.");
        s.Time = $"{t.Hours:D2}:{t.Minutes:D2}";

        if (s.Kind == ScheduleKind.Weekly)
        {
            s.Days = _days.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();
            if (s.Days.Count == 0) throw new ArgumentException($"{label}: tick at least one day of the week.");
        }
        if (s.Kind == ScheduleKind.Monthly)
        {
            if (!int.TryParse(DayBox.Text.Trim(), out int day) || day < 1 || day > 31)
                throw new ArgumentException($"{label}: day of month must be 1–31.");
            s.DayOfMonth = day;
        }
        return s;
    }

    private void KindBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateVisibility();
        Changed?.Invoke();
    }

    private void UpdateVisibility()
    {
        if (TimePanel == null) return;
        var kind = (ScheduleKind)Math.Max(0, KindBox.SelectedIndex);
        TimePanel.Visibility = kind == ScheduleKind.None ? Visibility.Collapsed : Visibility.Visible;
        MonthPanel.Visibility = kind == ScheduleKind.Monthly ? Visibility.Visible : Visibility.Collapsed;
        DaysPanel.Visibility = kind == ScheduleKind.Weekly ? Visibility.Visible : Visibility.Collapsed;
    }
}
