using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Вкладка «Расписание» на странице «Автоматизация» (C3): задачи агенту, которые выполняются
/// сами, только на чтение, с отчётом в чат.
/// </summary>
/// <remarks>
/// Контролы не читаются как состояние: правда лежит в <c>schedule.json</c>, и каждое действие
/// пишет файл, а список перестраивается из него — как у инструкций.
/// </remarks>
public partial class SchedulePanel : UserControl
{
    /// <summary>Порядок дней в редакторе — с понедельника, как в календаре большинства людей.</summary>
    internal static readonly DayOfWeek[] Week =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    ];

    private AppServices? _services;
    private ScheduledJob? _editing;

    public SchedulePanel()
    {
        InitializeComponent();
        CapBox.TextChanged += (_, _) => CapPlaceholder.Visibility = CapBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var day in Week)
        {
            var chip = new CheckBox { Tag = day, Style = (Style)FindResource("ChipCheck"), Margin = new Thickness(0, 0, 5, 4) };
            chip.SetResourceReference(ContentProperty, DayKey(day));
            DaysRow.Children.Add(chip);
        }
    }

    /// <summary>Открыть чат прогона — хозяин закрывает настройки и переходит в него.</summary>
    internal event Action<string>? OpenChatRequested;

    /// <summary>«Запустить сейчас» — делает хозяин: таймер и агент живут в окне.</summary>
    internal Func<string, Task<bool>>? RunNow { get; set; }

    /// <summary>Часы для подписей; подменяются в тестах.</summary>
    internal Func<DateTime> Clock { get; set; } = () => DateTime.Now;

    internal void Attach(AppServices services) => _services = services;

    internal void Load()
    {
        ShowList();
        Refresh();
    }

    /// <summary>Перечитывает задачи и последние прогоны.</summary>
    internal void Refresh()
    {
        if (_services is null)
        {
            return;
        }

        var now = Clock();
        var format = _services.Settings.DateFormat;
        var jobs = _services.Schedule.Load();
        JobItems.ItemsSource = jobs.Select(job => new ScheduleJobRow(job, now, format)).ToList();
        EmptyState.Visibility = jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var recent = _services.Schedule.Recent(5);
        RecentItems.ItemsSource = recent.Select(entry => new ScheduleRunRow(entry, format)).ToList();
        RecentTitle.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowList()
    {
        EditorPane.Visibility = Visibility.Collapsed;
        ListPane.Visibility = Visibility.Visible;
    }

    private ScheduledJob? JobOf(object sender) =>
        sender is FrameworkElement { Tag: string id } ? _services?.Schedule.Load().FirstOrDefault(job => job.Id == id) : null;

    // ───────────────────────── список ─────────────────────────

    private void Card_Click(object sender, RoutedEventArgs e)
    {
        if (JobOf(sender) is { } job)
        {
            OpenEditor(job);
        }
    }

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_services is null || sender is not CheckBox { Tag: string id } toggle)
        {
            return;
        }

        _services.Schedule.Update(id, job => job.Enabled = toggle.IsChecked == true);
        Refresh();
    }

    private void OpenChat_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: string chatId } && chatId.Length > 0)
        {
            OpenChatRequested?.Invoke(chatId);
        }
    }

    private async void RunNow_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (RunNow is null || sender is not Button { Tag: string id } button)
        {
            return;
        }

        button.IsEnabled = false;
        button.SetResourceReference(ContentProperty, "S.Schedule.Running");
        try
        {
            await RunNow(id);
        }
        catch (OperationCanceledException)
        {
            // Программа закрывается — показывать нечего.
        }
        finally
        {
            Refresh();
        }
    }

    private void AddHealth_Click(object sender, RoutedEventArgs e) => OpenEditor(ScheduleTemplates.HealthCheck());

    private void Create_Click(object sender, RoutedEventArgs e) =>
        OpenEditor(new ScheduledJob { Created = Clock() });

    // ───────────────────────── редактор ─────────────────────────

    private void OpenEditor(ScheduledJob job)
    {
        _editing = job;
        EditorTitle.SetResourceReference(TextBlock.TextProperty,
            string.IsNullOrEmpty(job.Id) ? "S.Schedule.NewTitle" : "S.Schedule.EditTitle");
        NameBox.Text = job.Name;
        PromptBox.Text = job.Prompt;
        EnabledToggle.IsChecked = job.Enabled;
        TimeBox.Text = ScheduleClock.TimeOfDay(job).ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        HoursBox.Text = ScheduleClock.Hours(job).ToString(CultureInfo.InvariantCulture);
        CapBox.Text = SpendRules.FormatField(job.MaxCostUsd);
        foreach (var chip in DaysRow.Children.OfType<CheckBox>())
        {
            chip.IsChecked = job.Days.Contains((DayOfWeek)chip.Tag);
        }

        foreach (var kind in KindChoices.Children.OfType<RadioButton>())
        {
            kind.IsChecked = (string)kind.Tag == job.Kind.ToString();
        }

        ApplyKind(job.Kind);
        DeleteButton.Visibility = string.IsNullOrEmpty(job.Id) ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Visibility = Visibility.Collapsed;
        ListPane.Visibility = Visibility.Collapsed;
        EditorPane.Visibility = Visibility.Visible;
        NameBox.Focus();
    }

    private ScheduleKind SelectedKind =>
        KindChoices.Children.OfType<RadioButton>().FirstOrDefault(kind => kind.IsChecked == true) is { Tag: string tag } &&
        Enum.TryParse(tag, out ScheduleKind kind)
            ? kind
            : ScheduleKind.Daily;

    private void Kind_Checked(object sender, RoutedEventArgs e) => ApplyKind(SelectedKind);

    /// <summary>Показывает только поля выбранного вида: время, дни или интервал.</summary>
    private void ApplyKind(ScheduleKind kind)
    {
        TimeRow.Visibility = kind is ScheduleKind.Daily or ScheduleKind.Weekly ? Visibility.Visible : Visibility.Collapsed;
        DaysRow.Visibility = kind == ScheduleKind.Weekly ? Visibility.Visible : Visibility.Collapsed;
        HoursRow.Visibility = kind == ScheduleKind.EveryHours ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Что не так с черновиком задачи; null — можно сохранять.</summary>
    internal static string? Validate(string name, string prompt, ScheduleKind kind, string time, string hours, int days)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Loc.Get("S.Schedule.NeedName");
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            return Loc.Get("S.Schedule.NeedPrompt");
        }

        if (kind is ScheduleKind.Daily or ScheduleKind.Weekly && !IsTimeOfDay(time))
        {
            return Loc.Get("S.Schedule.BadTime");
        }

        if (kind == ScheduleKind.Weekly && days == 0)
        {
            return Loc.Get("S.Schedule.NeedDays");
        }

        if (kind == ScheduleKind.EveryHours &&
            (!int.TryParse(hours.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var every) || every is < 1 or > 168))
        {
            return Loc.Get("S.Schedule.BadHours");
        }

        return null;
    }

    private static bool IsTimeOfDay(string text) =>
        TimeSpan.TryParseExact(text.Trim(), @"h\:mm", CultureInfo.InvariantCulture, out var time) &&
        time >= TimeSpan.Zero && time < TimeSpan.FromDays(1);

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_services is null || _editing is not { } job)
        {
            return;
        }

        var kind = SelectedKind;
        var days = DaysRow.Children.OfType<CheckBox>().Where(chip => chip.IsChecked == true).Select(chip => (DayOfWeek)chip.Tag).ToList();
        var problem = Validate(NameBox.Text, PromptBox.Text, kind, TimeBox.Text, HoursBox.Text, days.Count)
            ?? (CapBox.Text.Trim().Length > 0 && SpendRules.ParseUsd(CapBox.Text) is null ? Loc.Get("S.Schedule.BadCap") : null);
        if (problem is not null)
        {
            ErrorText.Text = problem;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        job.Name = NameBox.Text.Trim();
        job.Prompt = PromptBox.Text.Trim();
        job.Kind = kind;
        job.Enabled = EnabledToggle.IsChecked == true;
        job.Days = days.Count > 0 ? days : job.Days;
        if (kind is ScheduleKind.Daily or ScheduleKind.Weekly)
        {
            job.Time = TimeSpan.ParseExact(TimeBox.Text.Trim(), @"h\:mm", CultureInfo.InvariantCulture)
                .ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        }

        if (kind == ScheduleKind.EveryHours)
        {
            job.EveryHours = int.Parse(HoursBox.Text.Trim(), CultureInfo.InvariantCulture);
        }

        job.MaxCostUsd = SpendRules.ParseUsd(CapBox.Text);

        var jobs = _services.Schedule.Load();
        if (string.IsNullOrEmpty(job.Id))
        {
            job.Id = Guid.NewGuid().ToString("N")[..12];
            if (job.Created == default)
            {
                job.Created = Clock();
            }

            jobs.Add(job);
        }
        else
        {
            var index = jobs.FindIndex(item => item.Id == job.Id);
            if (index >= 0)
            {
                jobs[index] = job;
            }
            else
            {
                jobs.Add(job);
            }
        }

        if (!_services.Schedule.Save(jobs))
        {
            ErrorText.Text = Loc.Get("S.Schedule.SaveFailed");
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        Load();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_services is null || _editing is not { Id.Length: > 0 } job || Window.GetWindow(this) is not MainWindow window)
        {
            return;
        }

        var confirmed = await window.ShowNoticeAsync(
            Loc.Get("S.Schedule.DeleteTitle"),
            Loc.Format("S.Schedule.DeleteText", job.Name),
            Loc.Get("S.Common.Delete"),
            Loc.Get("S.Common.Cancel"),
            NoticeTone.Danger);
        if (!confirmed)
        {
            return;
        }

        var jobs = _services.Schedule.Load();
        jobs.RemoveAll(item => item.Id == job.Id);
        _services.Schedule.Save(jobs);
        Load();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => ShowList();

    // ───────────────────────── подписи ─────────────────────────

    internal static string DayKey(DayOfWeek day) => "S.Schedule.Day." + day;

    /// <summary>«Ежедневно в 09:00», «Пн, Ср в 09:00», «Каждые 6 ч», «При запуске».</summary>
    internal static string Describe(ScheduledJob job)
    {
        var time = ScheduleClock.TimeOfDay(job).ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        return job.Kind switch
        {
            ScheduleKind.Daily => Loc.Format("S.Schedule.When.Daily", time),
            ScheduleKind.Weekly => Loc.Format("S.Schedule.When.Weekly",
                string.Join(", ", Week.Where(job.Days.Contains).Select(day => Loc.Get(DayKey(day)))), time),
            ScheduleKind.EveryHours => Loc.Format("S.Schedule.When.EveryHours", ScheduleClock.Hours(job)),
            _ => Loc.Get("S.Schedule.When.AtStartup")
        };
    }

    internal static string StatusText(ScheduleStatus? status) => Loc.Get(status switch
    {
        ScheduleStatus.Ok => "S.Schedule.Status.Ok",
        ScheduleStatus.Attention => "S.Schedule.Status.Attention",
        ScheduleStatus.Problem => "S.Schedule.Status.Problem",
        ScheduleStatus.Failed => "S.Schedule.Status.Failed",
        _ => "S.Schedule.Status.Unknown"
    });
}

/// <summary>Карточка задачи.</summary>
internal sealed class ScheduleJobRow(ScheduledJob job, DateTime now, DateFormat format)
{
    public string Id { get; } = job.Id;

    public string Name { get; } = job.Name;

    public bool Enabled { get; } = job.Enabled;

    public string? LastChatId { get; } = job.LastChatId;

    public Visibility ChatVisibility => string.IsNullOrEmpty(LastChatId) ? Visibility.Collapsed : Visibility.Visible;

    public string When { get; } = SchedulePanel.Describe(job) +
                                  (job.Enabled && ScheduleClock.Next(job, now) is { } next
                                      ? " · " + Loc.Format("S.Schedule.Next", ChatFormat.DateTimeShort(next, format))
                                      : job.Enabled ? "" : " · " + Loc.Get("S.Schedule.Off"));

    public string Last { get; } = job.LastRun is { } last
        ? Loc.Format("S.Schedule.Last", ChatFormat.DateTimeShort(last, format), SchedulePanel.StatusText(job.LastStatus))
        : Loc.Get("S.Schedule.NeverRun");

    /// <summary>Цвет точки: имя значения <see cref="ScheduleStatus"/>.</summary>
    public string Tone { get; } = (job.LastStatus ?? ScheduleStatus.Unknown).ToString();
}

/// <summary>Строка «Последние прогоны».</summary>
internal sealed class ScheduleRunRow(ScheduleRunEntry entry, DateFormat format)
{
    public string Tone { get; } = entry.Status.ToString();

    public string Text { get; } = string.Join(" · ", new[]
    {
        ChatFormat.DateTimeShort(entry.Time, format),
        entry.Name,
        SchedulePanel.StatusText(entry.Status),
        entry.CostUsd > 0 ? "$" + entry.CostUsd.ToString("0.####", CultureInfo.InvariantCulture) : null
    }.Where(part => !string.IsNullOrEmpty(part)));
}
