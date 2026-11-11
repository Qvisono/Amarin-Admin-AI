using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Достоверный снимок данных правой панели текущего чата. Панель не разбирает текст модели и не
/// вычисляет токены: вызывающий код передаёт только результаты реестра, инструментов и измерителя
/// контекста.
/// </summary>
public sealed record SessionRailSnapshot(
    SessionRailContext? Context,
    IReadOnlyList<SessionRailAgent> Agents,
    IReadOnlyList<SessionRailFile> Files,
    SessionRailSettings Settings,
    bool CanCompact,
    bool IsCompacted = false,
    SessionRailCompaction? Compaction = null)
{
    /// <summary>Пустой чат до получения данных ядра.</summary>
    public static SessionRailSnapshot Empty { get; } = new(null, [], [], new SessionRailSettings(false, false), false);
}

/// <summary>То, что нужно экрану сжатия от владельца чата.</summary>
/// <param name="ModelId">Модель слота «Сводка»: с неё экран начинает при каждом открытии.</param>
/// <param name="KeyId">Ключ этого слота; null — ключ по умолчанию.</param>
/// <param name="Preview">Прикидка результата для выбранных настроек; null в ответе: с ними нечего сокращать.</param>
public sealed record SessionRailCompaction(
    string ModelId, string? KeyId, Func<SessionRailCompactOptions, SessionRailCompactPreview?> Preview);

/// <summary>Настройки объёма сжатия, выбранные на экране.</summary>
public sealed record SessionRailCompactOptions(int KeepMessages, bool CompactFirst);

/// <summary>Контекст после сжатия и число сообщений ленты под сводкой (ноль — неизвестно).</summary>
public sealed record SessionRailCompactPreview(long UsedAfter, int CompactedMessages, int TotalMessages);

/// <summary>Просьба сжать открытый чат с настройками экрана.</summary>
public sealed class SessionRailCompactRequestedEventArgs(
    SessionRailCompactOptions options, string modelId, string? keyId, string instructions) : EventArgs
{
    public SessionRailCompactOptions Options { get; } = options;

    public string ModelId { get; } = modelId;

    public string? KeyId { get; } = keyId;

    public string Instructions { get; } = instructions;
}

/// <summary>Просьба показать файл в проводнике.</summary>
public sealed class SessionRailFileEventArgs(string path) : EventArgs
{
    public string Path { get; } = path;
}

/// <summary>Общий расход контекста и оценка его составляющих.</summary>
public sealed record SessionRailContext(long UsedTokens, long CapacityTokens, IReadOnlyList<SessionRailContextPart> Parts,
    bool IsEstimate = true, bool HasUnmeasuredAttachments = false)
{
    /// <summary>Доля контекста в пределах от нуля до единицы.</summary>
    public double Fraction => CapacityTokens == 0 ? 0 : Math.Clamp((double)UsedTokens / CapacityTokens, 0, 1);

    internal void Validate()
    {
        if (UsedTokens < 0 || CapacityTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(UsedTokens));
        }

        if (Parts.Any(part => part.Tokens < 0))
        {
            throw new ArgumentException("Context parts must have non-negative token counts.", nameof(Parts));
        }
    }
}

/// <summary>Одна подтверждённая составляющая измеренного контекста.</summary>
public sealed record SessionRailContextPart(SessionRailContextCategory Category, long Tokens);

/// <summary>Категория контекста, которую измеряет ядро.</summary>
public enum SessionRailContextCategory
{
    Conversation,
    Tools,
    Files,
    System
}

/// <summary>Агент текущего запуска или сохранённой истории чата.</summary>
public sealed record SessionRailAgent(string Id, string Name, string ModelId, bool IsWorking, bool IsSuccessful = true);

/// <summary>Файл, создание или изменение которого подтвердил структурированный результат инструмента.</summary>
public sealed record SessionRailFile(string Path, SessionRailFileChange Change);

/// <summary>Вид подтверждённого изменения файла.</summary>
public enum SessionRailFileChange
{
    Created,
    Modified
}

/// <summary>Локальные переключатели, принадлежащие одному чату.</summary>
public sealed record SessionRailSettings(
    bool ComputerEnabled, bool BrowserEnabled, bool ComputerAvailable = true, bool BrowserAvailable = true);

/// <summary>Новые локальные настройки после нажатия переключателя панели.</summary>
public sealed class SessionRailSettingsChangedEventArgs(SessionRailSettings settings) : EventArgs
{
    /// <summary>Полное новое значение обеих настроек.</summary>
    public SessionRailSettings Settings { get; } = settings;
}

/// <summary>
/// Правая панель текущего чата. Она показывает только переданный снимок и возвращает намерения
/// пользователя событиями, чтобы не хранить состояние чата, очереди или параметры в контроле.
/// </summary>
public partial class SessionRailPanel : UserControl
{
    internal const double MinWindowWidth = 1120;

    private bool _applying;
    private SessionRailSettings _settings = new(false, false);
    private SessionRailSnapshot _snapshot = SessionRailSnapshot.Empty;
    private int _keep = CompactionOptions.DefaultKeep;

    public SessionRailPanel()
    {
        InitializeComponent();
        ShowSession(SessionRailSnapshot.Empty);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape && IsCompactOpen)
            {
                e.Handled = true;
                ShowCompact(false);
            }
        };
    }

    /// <summary>Просьба сжать открытый чат с настройками экрана сжатия.</summary>
    public event EventHandler<SessionRailCompactRequestedEventArgs>? CompactionRequested;

    /// <summary>Человек щёлкнул по файлу: владелец показывает его в проводнике.</summary>
    public event EventHandler<SessionRailFileEventArgs>? FileOpenRequested;

    internal bool IsCompactOpen => CompactView.Visibility == Visibility.Visible;

    /// <summary>Просьба отменить действующую сводку.</summary>
    public event EventHandler? CompactionUndoRequested;

    /// <summary>Локальные настройки изменены человеком и должны быть сохранены владельцем чата.</summary>
    public event EventHandler<SessionRailSettingsChangedEventArgs>? SettingsChanged;

    internal static bool FitsWindow(double width) => width >= MinWindowWidth;

    /// <summary>
    /// Показывает снимок текущего чата. Этот метод вызывается на потоке WPF после смены чата,
    /// окончания инструмента, обновления реестра агентов или нового измерения контекста.
    /// </summary>
    public void ShowSession(SessionRailSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Agents);
        ArgumentNullException.ThrowIfNull(snapshot.Files);
        ArgumentNullException.ThrowIfNull(snapshot.Settings);
        snapshot.Context?.Validate();

        _applying = true;
        try
        {
            _settings = snapshot.Settings;
            ComputerToggle.IsChecked = _settings.ComputerEnabled;
            BrowserToggle.IsChecked = _settings.BrowserEnabled;
            ComputerToggle.IsEnabled = _settings.ComputerAvailable;
            BrowserToggle.IsEnabled = _settings.BrowserAvailable;
            ComputerToggle.ToolTip = _settings.ComputerAvailable ? null : Loc.Get("S.SessionRail.GlobalDisabled");
            BrowserToggle.ToolTip = _settings.BrowserAvailable ? null : Loc.Get("S.SessionRail.GlobalDisabled");
            _snapshot = snapshot;
            CompactButton.IsEnabled = snapshot.CanCompact || snapshot.IsCompacted;
            CompactUndoButton.Visibility = snapshot.IsCompacted ? Visibility.Visible : Visibility.Collapsed;
            if (IsCompactOpen)
            {
                // Экран остаётся открытым и пересчитывает шапку; занятому чату сжатие недоступно.
                if (CompactButton.IsEnabled)
                {
                    ShowCompactPreview();
                }
                else
                {
                    ShowCompact(false);
                }
            }

            ShowContext(snapshot.Context);
            ShowFiles(snapshot.Files);
            ShowAgents(snapshot.Agents);
        }
        finally
        {
            _applying = false;
        }
    }

    private void ShowContext(SessionRailContext? context)
    {
        var available = context is not null;
        var hasScale = context is { CapacityTokens: > 0 };
        ContextUnavailable.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        ContextTotal.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        ContextPercent.Visibility = hasScale ? Visibility.Visible : Visibility.Collapsed;
        ContextBar.Visibility = hasScale ? Visibility.Visible : Visibility.Collapsed;
        ContextParts.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        ContextAttachmentNote.Visibility = context?.HasUnmeasuredAttachments == true ? Visibility.Visible : Visibility.Collapsed;
        ContextParts.Children.Clear();

        if (context is null)
        {
            ContextTotal.Text = "";
            ContextPercent.Text = "";
            return;
        }

        ContextTotal.Text = (context.IsEstimate ? "≈ " : "") + (context.CapacityTokens > 0
            ? string.Format(CultureInfo.CurrentCulture, "{0:N0} / {1:N0}", context.UsedTokens, context.CapacityTokens)
            : context.UsedTokens.ToString("N0", CultureInfo.CurrentCulture));
        ContextPercent.Text = context.Fraction.ToString("P0", CultureInfo.CurrentCulture);
        ContextUsedColumn.Width = new GridLength(Math.Min(context.UsedTokens, context.CapacityTokens), GridUnitType.Star);
        ContextFreeColumn.Width = new GridLength(Math.Max(0, context.CapacityTokens - context.UsedTokens), GridUnitType.Star);

        var total = context.Parts.Sum(part => part.Tokens);
        foreach (var part in context.Parts)
        {
            ContextParts.Children.Add(ContextPartRow(part, total));
        }
    }

    private Grid ContextPartRow(SessionRailContextPart part, long total)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = Meta(ContextPartLabel(part.Category));
        var tokens = Meta((total > 0 ? (double)part.Tokens / total : 0).ToString("P0", CultureInfo.CurrentCulture));
        Grid.SetColumn(tokens, 1);
        row.Children.Add(name);
        row.Children.Add(tokens);
        return row;
    }

    private static string ContextPartLabel(SessionRailContextCategory category) =>
        Loc.Get(category switch
        {
            SessionRailContextCategory.Conversation => "S.SessionRail.ContextConversation",
            SessionRailContextCategory.Tools => "S.Cost.Tools",
            SessionRailContextCategory.Files => "S.SessionRail.Files",
            SessionRailContextCategory.System => "S.SessionRail.ContextSystem",
            _ => throw new ArgumentOutOfRangeException(nameof(category))
        });

    private void ShowFiles(IReadOnlyList<SessionRailFile> files)
    {
        FilesItems.Children.Clear();
        var known = files
            .Where(file => !string.IsNullOrWhiteSpace(file.Path))
            .GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();
        FilesCount.Text = known.Count.ToString(CultureInfo.CurrentCulture);

        foreach (var file in known)
        {
            FilesItems.Children.Add(FileRow(file));
        }
    }

    private Button FileRow(SessionRailFile file)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M3.5,1.5 H9 L12.5,5 V14.5 H3.5 Z M9,1.5 V5 H12.5"),
            Style = (Style)FindResource("RailIcon")
        };
        var name = Text(System.IO.Path.GetFileName(file.Path), "RailText");
        Grid.SetColumn(name, 1);
        row.Children.Add(icon);
        row.Children.Add(name);

        var button = new Button { Style = (Style)FindResource("RailActionRow"), Height = 24, Content = row, ToolTip = file.Path };
        button.Click += (_, _) => FileOpenRequested?.Invoke(this, new SessionRailFileEventArgs(file.Path));
        return button;
    }

    private void ShowAgents(IReadOnlyList<SessionRailAgent> agents)
    {
        AgentItems.Children.Clear();
        var known = agents
            .Where(agent => !string.IsNullOrWhiteSpace(agent.Id) && !string.IsNullOrWhiteSpace(agent.Name))
            .GroupBy(agent => agent.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();
        AgentsCount.Text = known.Count.ToString(CultureInfo.CurrentCulture);

        foreach (var agent in known)
        {
            AgentItems.Children.Add(AgentRow(agent));
        }
    }

    private Grid AgentRow(SessionRailAgent agent)
    {
        var row = new Grid { Height = 22, Margin = new Thickness(0, 3, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });

        var logoBox = new Grid { Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        var logo = new Image { Width = 16, Height = 16 };
        var letter = Meta("");
        letter.FontWeight = FontWeights.SemiBold;
        letter.TextAlignment = TextAlignment.Center;
        letter.HorizontalAlignment = HorizontalAlignment.Center;
        ModelBrand.Apply(this, agent.ModelId, logo, letter, null);
        logoBox.Children.Add(logo);
        logoBox.Children.Add(letter);

        // Что это агент, говорит заголовок карточки: в строке остаётся только его модель.
        var name = Text(string.IsNullOrWhiteSpace(agent.ModelId) ? agent.Name : VeniceModelCatalog.GetDisplayName(agent.ModelId), "RailText");
        name.ToolTip = string.IsNullOrWhiteSpace(agent.ModelId) ? agent.Id : agent.ModelId;
        Grid.SetColumn(name, 1);
        row.Children.Add(logoBox);
        row.Children.Add(name);
        if (agent.IsWorking)
        {
            var working = new AgentWorkingIndicator { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(working, 2);
            row.Children.Add(working);
        }
        else if (!agent.IsSuccessful)
        {
            var failed = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M3,3 L11,11 M11,3 L3,11"),
                Width = 12,
                Height = 12,
                Stretch = Stretch.Uniform,
                StrokeThickness = 1.4,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            failed.SetResourceReference(System.Windows.Shapes.Path.StrokeProperty, "Status.Danger");
            Grid.SetColumn(failed, 2);
            row.Children.Add(failed);
        }
        else
        {
            var done = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M2,8 L6.2,12 L14,3.5"),
                Style = (Style)FindResource("ToolDoneIcon"),
                Margin = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(done, 2);
            row.Children.Add(done);
        }

        return row;
    }

    private TextBlock Text(string value, string style) => new()
    {
        Text = value,
        Style = (Style)FindResource(style)
    };

    private TextBlock Meta(string value) => Text(value, "RailMeta");

    private void Compact_Click(object sender, RoutedEventArgs e)
    {
        // Модель выбирается на одно сжатие: каждый раз экран начинает с модели слота «Сводка».
        if (_snapshot.Compaction is { } compaction)
        {
            CompactModelField.SetSelected(compaction.ModelId, compaction.KeyId);
        }

        ShowCompact(true);
    }

    private void CompactBack_Click(object sender, RoutedEventArgs e) => ShowCompact(false);

    private void KeepLess_Click(object sender, RoutedEventArgs e) => SetKeep(_keep - 1);

    private void KeepMore_Click(object sender, RoutedEventArgs e) => SetKeep(_keep + 1);

    private void CompactOption_Changed(object sender, RoutedEventArgs e)
    {
        // Переключатель срабатывает и во время чтения разметки, когда экрана ещё нет.
        if (CompactView is not null && IsCompactOpen)
        {
            ShowCompactPreview();
        }
    }

    private void SetKeep(int value)
    {
        _keep = Math.Clamp(value, CompactionOptions.MinKeep, CompactionOptions.MaxKeep);
        ShowCompactPreview();
    }

    private void ShowCompact(bool open)
    {
        CompactView.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        CardsPanel.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        if (open)
        {
            ShowCompactPreview();
        }
    }

    private SessionRailCompactOptions CompactOptions => new(_keep, CompactFirstToggle.IsChecked == true);

    /// <summary>Шапка экрана сжатия: сколько занято сейчас и сколько останется с выбранными настройками.</summary>
    private void ShowCompactPreview()
    {
        KeepValue.Text = _keep.ToString(CultureInfo.CurrentCulture);
        KeepLessButton.IsEnabled = _keep > CompactionOptions.MinKeep;
        KeepMoreButton.IsEnabled = _keep < CompactionOptions.MaxKeep;

        var preview = _snapshot.CanCompact ? _snapshot.Compaction?.Preview(CompactOptions) : null;
        var used = _snapshot.Context?.UsedTokens ?? 0;
        var after = preview?.UsedAfter ?? used;

        CompactRunButton.IsEnabled = preview is not null;
        CompactBefore.Text = used.ToString("N0", CultureInfo.CurrentCulture);
        CompactAfter.Text = "≈ " + after.ToString("N0", CultureInfo.CurrentCulture);
        CompactArrow.Visibility = CompactAfter.Visibility = preview is null ? Visibility.Collapsed : Visibility.Visible;
        KeepHint.Text = preview switch
        {
            null => Loc.Get("S.Compact.Nothing"),
            { CompactedMessages: > 0 } => Loc.Format("S.SessionRail.CompactKeepHint", preview.CompactedMessages, preview.TotalMessages),
            _ => ""
        };
        KeepHint.Visibility = KeepHint.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        // Шкала меряет занятое сейчас, а не окно модели: так видна доля, которую сжатие освободит.
        var freed = Math.Max(0, used - after);
        CompactBar.Visibility = used > 0 ? Visibility.Visible : Visibility.Collapsed;
        CompactKeptColumn.Width = new GridLength(Math.Max(0, used - freed), GridUnitType.Star);
        CompactFreedColumn.Width = new GridLength(freed, GridUnitType.Star);
        CompactFrees.Visibility = freed > 0 ? Visibility.Visible : Visibility.Collapsed;
        CompactFrees.Text = freed > 0
            ? Loc.Format("S.SessionRail.CompactFrees", ((double)freed / used).ToString("P0", CultureInfo.CurrentCulture))
            : "";
    }

    private void CompactRun_Click(object sender, RoutedEventArgs e)
    {
        ShowCompact(false);
        CompactionRequested?.Invoke(this, new SessionRailCompactRequestedEventArgs(
            CompactOptions, CompactModelField.SelectedModelId, CompactModelField.SelectedKeyId, CompactInstructionsBox.Text.Trim()));
    }

    private void CompactUndo_Click(object sender, RoutedEventArgs e)
    {
        ShowCompact(false);
        CompactionUndoRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SessionSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_applying)
        {
            return;
        }

        _settings = new SessionRailSettings(ComputerToggle.IsChecked == true, BrowserToggle.IsChecked == true);
        SettingsChanged?.Invoke(this, new SessionRailSettingsChangedEventArgs(_settings));
    }

}
