using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Вкладка «Отложенные» на странице «Автоматизация»: что чат поставил на потом, когда оно
/// сработает и чем кончилось.
/// </summary>
/// <remarks>
/// Задачи заводит чат, а не эта вкладка: здесь их видят, выполняют раньше срока, отменяют и
/// возобновляют. Правда лежит в <c>deferred.json</c>; действие идёт через проверку сроков окна,
/// а список перестраивается из файла.
/// </remarks>
public partial class DeferredPanel : UserControl
{
    /// <summary>Сколько выполненных показывать: дальше это уже архив, а не «что было недавно».</summary>
    internal const int HistoryShown = 15;

    private AppServices? _services;

    public DeferredPanel()
    {
        InitializeComponent();
    }

    /// <summary>Открыть чат задачи — хозяин закрывает настройки и переходит в него.</summary>
    internal event Action<string>? OpenChatRequested;

    /// <summary>Проверка сроков окна: «Выполнить сейчас», «Отменить», «Возобновить».</summary>
    internal Func<DeferredRunner?> Runner { get; set; } = () => null;

    /// <summary>Почему Планировщик не завёл запуск к сроку; null — завёл.</summary>
    internal Func<string?> WakeProblem { get; set; } = () => null;

    /// <summary>Часы для подписей; подменяются в тестах.</summary>
    internal Func<DeferredFacts> Facts { get; set; } = DeferredClock.Now;

    internal void Attach(AppServices services) => _services = services;

    internal void Load() => Refresh();

    /// <summary>Перечитывает задачи.</summary>
    internal void Refresh()
    {
        if (_services is null)
        {
            return;
        }

        var (waiting, history) = Rows(_services.Deferred.Snapshot(), Facts(), _services.Settings.DateFormat);
        WaitingItems.ItemsSource = waiting;
        HistoryItems.ItemsSource = history;
        WaitingTitle.Visibility = waiting.Count > 0 && history.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryTitle.Visibility = history.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = waiting.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        WakeProblemText.Visibility = waiting.Count > 0 && WakeProblem() is not null ? Visibility.Visible : Visibility.Collapsed;
        WakeProblemText.ToolTip = WakeProblem();
    }

    /// <summary>Ждущие — сверху, в порядке срока; ниже — недавно выполненные.</summary>
    internal static (IReadOnlyList<DeferredRow> Waiting, IReadOnlyList<DeferredHistoryRow> History) Rows(
        IEnumerable<DeferredTask> tasks,
        DeferredFacts facts,
        DateFormat format)
    {
        var all = tasks.ToList();
        var waiting = all
            .Where(task => task.IsActive || task.Status == DeferredStatus.Paused)
            .OrderBy(task => task.SnoozedUntilUtc ?? task.NextDueUtc ?? DateTime.MaxValue)
            .ThenBy(task => task.CreatedUtc)
            .Select(task => new DeferredRow(task, facts))
            .ToList();
        var history = all
            .Where(task => !task.IsActive && task.Status != DeferredStatus.Paused)
            .OrderByDescending(task => task.LastFiredUtc ?? task.LastAttemptUtc ?? task.CreatedUtc)
            .Take(HistoryShown)
            .Select(task => new DeferredHistoryRow(task, format))
            .ToList();
        return (waiting, history);
    }

    private void OpenChat_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string chatId } && chatId.Length > 0)
        {
            OpenChatRequested?.Invoke(chatId);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id })
        {
            Runner()?.Cancel(id);
            Refresh();
        }
    }

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id } || _services?.Deferred.Peek(id) is not { } task || Runner() is not { } runner)
        {
            return;
        }

        if (task.Status == DeferredStatus.Paused)
        {
            runner.Resume(id);
        }
        else
        {
            runner.RunNow(id);
        }

        Refresh();
    }
}

/// <summary>Карточка ждущей задачи.</summary>
internal sealed class DeferredRow(DeferredTask task, DeferredFacts facts)
{
    public string Id { get; } = task.Id;

    public string Title { get; } = task.Title;

    public DeferredKind Kind { get; } = task.Kind;

    /// <summary>Что задача сделает: текст напоминания или поручения, команда.</summary>
    public string? Detail { get; } = (task.Kind == DeferredKind.Command ? task.Command : task.Text) is { Length: > 0 } text ? text : null;

    public string When { get; } = DeferredText.When(task, facts);

    public string State { get; } = StateOf(task, facts);

    /// <summary>Цвет точки — см. стиль <c>StatusDot</c>.</summary>
    public string Tone { get; } = task.Status switch
    {
        DeferredStatus.Running => "Running",
        DeferredStatus.Interrupted or DeferredStatus.Paused => "Attention",
        _ => ""
    };

    public string? ChatId { get; } = task.ResultChatId ?? task.ChatId;

    public Visibility ChatVisibility => string.IsNullOrEmpty(ChatId) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Идущую задачу не отменить: её останавливают кнопкой «Стоп» в её чате.</summary>
    public Visibility CancelVisibility { get; } = task.Status == DeferredStatus.Running ? Visibility.Collapsed : Visibility.Visible;

    public string ActionText { get; } = task.Status switch
    {
        DeferredStatus.Paused => Loc.Get("S.Deferred.Resume"),
        DeferredStatus.Interrupted => Loc.Get("S.Deferred.Toast.Retry"),
        _ => Loc.Get("S.Deferred.RunNow")
    };

    public Visibility ActionVisibility { get; } = task.Status == DeferredStatus.Running ? Visibility.Collapsed : Visibility.Visible;

    private static string StateOf(DeferredTask task, DeferredFacts facts)
    {
        // У разовой задачи срок уже назван строкой выше; у повторяющейся там правило, и ближайший
        // раз дописывается сюда.
        var state = Loc.Get("S.Deferred.Status." + task.Status);
        if (task.Status != DeferredStatus.Pending || (task.SnoozedUntilUtc ?? task.NextDueUtc) is not { } due ||
            (task.Repeat.Kind == DeferredRepeatKind.Once && task.SnoozedUntilUtc is null))
        {
            return state;
        }

        return state + " · " + DeferredText.Moment(due, facts);
    }
}

/// <summary>Строка выполненной задачи.</summary>
internal sealed class DeferredHistoryRow(DeferredTask task, DateFormat format)
{
    public string Tone { get; } = task.Status switch
    {
        DeferredStatus.Done => "Ok",
        DeferredStatus.Failed => "Failed",
        _ => ""
    };

    public string Text { get; } = string.Join(" · ", new[]
    {
        (task.LastFiredUtc ?? task.LastAttemptUtc) is { } when ? ChatFormat.DateTimeShort(when.ToLocalTime(), format) : null,
        task.Title,
        Loc.Get("S.Deferred.Status." + task.Status),
        task.CostUsd > 0 ? "$" + task.CostUsd.ToString("0.####", CultureInfo.InvariantCulture) : null
    }.Where(part => !string.IsNullOrEmpty(part)));

    /// <summary>Чем кончилось: ошибка или начало отчёта.</summary>
    public string? Detail { get; } = (task.Error ?? task.Output) is { Length: > 0 } text ? text : null;
}
