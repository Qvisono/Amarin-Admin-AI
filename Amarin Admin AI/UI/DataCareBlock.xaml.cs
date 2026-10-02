using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Подстраница «Хранение и очистка» на странице данных (F3).
/// </summary>
/// <remarks>
/// Правда о хранении — в <see cref="AppSettings.Retention"/>, контролы только отражают её. Всё,
/// что ходит по диску (объёмы, очистка, крупнейшие чаты), — на рабочем потоке. Что спросить
/// человека, какой чат открыть и какие чаты сейчас заняты — решает окно через свойства ниже.
/// </remarks>
public partial class DataCareBlock : UserControl
{
    private static readonly CleanupTarget[] Targets =
        [CleanupTarget.Logs, CleanupTarget.Snapshots, CleanupTarget.Shared, CleanupTarget.BackgroundCache];

    private AppServices? _services;
    private bool _loading;
    private CancellationTokenSource? _measuring;

    public DataCareBlock() => InitializeComponent();

    /// <summary>Правило хранения поменялось — строка-ссылка на странице перечитывает своё значение.</summary>
    internal event Action? Changed;

    /// <summary>Значение строки-ссылки: что делается со старыми чатами.</summary>
    internal static string Summary(ChatRetention? retention) =>
        (retention?.Mode ?? RetentionMode.Off) switch
        {
            RetentionMode.Archive => Loc.Format("S.Care.Short.Archive", retention!.Days),
            RetentionMode.Delete => Loc.Format("S.Care.Short.Delete", retention!.Days),
            _ => Loc.Get("S.Care.Short.Keep")
        };

    /// <summary>Спросить человека (заголовок, текст, подпись кнопки «да»); true — да. Ставит окно.</summary>
    internal Func<string, string, string, Task<bool>>? Confirm { get; set; }

    /// <summary>Открыть чат по щелчку в списке крупнейших. Ставит окно.</summary>
    internal Action<string>? OpenChat { get; set; }

    /// <summary>Какие чаты сейчас заняты: открыт или идёт ход. Их правило хранения не трогает.</summary>
    internal Func<IReadOnlySet<string>>? BusyChats { get; set; }

    /// <summary>Что-то удалено — пересчитать «Занято на диске».</summary>
    internal Action? Cleaned { get; set; }

    internal void Load(AppServices services)
    {
        _services = services;
        _loading = true;
        try
        {
            var retention = services.Settings.Retention ?? new ChatRetention();
            SelectMode(retention.Mode);
            DaysBox.Text = retention.Days.ToString(CultureInfo.InvariantCulture);
            DaysRow.IsEnabled = retention.Mode != RetentionMode.Off;
        }
        finally
        {
            _loading = false;
        }

        Detached.Run(MeasureAsync(), "data_care_measure");
    }

    private async Task MeasureAsync()
    {
        if (_services is not { } services)
        {
            return;
        }

        _measuring?.Cancel();
        var cancellation = new CancellationTokenSource();
        _measuring = cancellation;

        var appRoot = AppPaths.Root;
        var localRoot = CrashLog.DefaultDirectory();
        var chats = services.ChatStore.List();
        var chatsFolder = services.ChatStore.Folder;
        var (sizes, largest) = await Task.Run(() =>
        {
            var measured = Targets.ToDictionary(
                target => target,
                target => DataCleanup.Measure(target, appRoot, localRoot, DataCleanup.DefaultKeepSnapshots));
            return (measured, DataCleanup.LargestChats(chatsFolder, chats.Select(chat => chat.Id), 8));
        }).ConfigureAwait(true);

        if (cancellation.IsCancellationRequested)
        {
            return;
        }

        CleanupRows.Children.Clear();
        foreach (var target in Targets)
        {
            CleanupRows.Children.Add(CleanupRow(target, sizes[target]));
        }

        LargestRows.Children.Clear();
        var titles = chats.ToDictionary(chat => chat.Id, chat => chat.Title, StringComparer.Ordinal);
        foreach (var chat in largest)
        {
            LargestRows.Children.Add(LargestRow(chat, titles.GetValueOrDefault(chat.Id) ?? chat.Id));
        }

        LargestEmpty.Text = Loc.Get("S.Care.LargestNone");
        LargestEmpty.Visibility = largest.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string TitleKey(CleanupTarget target) => target switch
    {
        CleanupTarget.Logs => "S.Care.Logs",
        CleanupTarget.Snapshots => "S.Care.Snapshots",
        CleanupTarget.Shared => "S.Care.Shared",
        _ => "S.Care.BackgroundCache"
    };

    private FrameworkElement CleanupRow(CleanupTarget target, CleanupSize size)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(new TextBlock { Text = Loc.Get(TitleKey(target)), Style = (Style)FindResource("SettingTitle") });
        text.Children.Add(new TextBlock
        {
            Text = size.Files == 0
                ? Loc.Get("S.Care.Nothing")
                : Loc.Format("S.Care.Size", AttachmentTypes.FormatSize(size.Bytes), size.Files.ToString(CultureInfo.InvariantCulture)),
            Style = (Style)FindResource("SettingDesc")
        });
        grid.Children.Add(text);

        var button = new Button
        {
            Content = Loc.Get("S.Care.Clean"),
            Style = (Style)FindResource("SecondaryButton"),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = size.Files > 0
        };
        System.Windows.Automation.AutomationProperties.SetName(button, Loc.Get("S.Care.Clean") + " · " + Loc.Get(TitleKey(target)));
        button.Click += (_, _) => Detached.Run(CleanAsync(target, size), "data_care_clean");
        Grid.SetColumn(button, 1);
        grid.Children.Add(button);
        return grid;
    }

    private async Task CleanAsync(CleanupTarget target, CleanupSize size)
    {
        var question = Loc.Format(
            target == CleanupTarget.Snapshots ? "S.Care.ConfirmSnapshots" : "S.Care.Confirm",
            Loc.Get(TitleKey(target)),
            AttachmentTypes.FormatSize(size.Bytes),
            DataCleanup.DefaultKeepSnapshots.ToString(CultureInfo.InvariantCulture));
        if (Confirm is not { } confirm || !await confirm(Loc.Get("S.Care.ConfirmTitle"), question, Loc.Get("S.Care.Clean")).ConfigureAwait(true))
        {
            return;
        }

        var appRoot = AppPaths.Root;
        var localRoot = CrashLog.DefaultDirectory();
        await Task.Run(() => DataCleanup.Run(target, appRoot, localRoot, DataCleanup.DefaultKeepSnapshots)).ConfigureAwait(true);
        Cleaned?.Invoke();
        await MeasureAsync().ConfigureAwait(true);
    }

    /// <summary>Строка-ссылка, как у подстраниц: название чата, справа размер и «›» — щелчок открывает чат.</summary>
    private Button LargestRow(ChatSize chat, string title)
    {
        var name = new TextBlock
        {
            Text = title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Style = (Style)FindResource("SettingTitle")
        };

        var button = new Button
        {
            Content = name,
            Tag = AttachmentTypes.FormatSize(chat.Bytes),
            Style = (Style)FindResource("SettingsLinkRow"),
            Margin = new Thickness(-8, -2, -8, 2)
        };
        System.Windows.Automation.AutomationProperties.SetName(button, title);
        button.Click += (_, _) => OpenChat?.Invoke(chat.Id);
        return button;
    }

    private void SelectMode(RetentionMode mode) =>
        ModeCombo.SelectedItem = ModeCombo.Items.OfType<ComboBoxItem>().First(item => (string)item.Tag == mode.ToString());

    private RetentionMode SelectedMode =>
        ModeCombo.SelectedItem is ComboBoxItem { Tag: string tag } && Enum.TryParse(tag, out RetentionMode mode) ? mode : RetentionMode.Off;

    private async void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _services is not { } services)
        {
            return;
        }

        var mode = SelectedMode;
        var retention = services.Settings.Retention ?? new ChatRetention();

        // Удаление — необратимо: включают его, увидев, сколько чатов уйдёт прямо сейчас.
        if (mode == RetentionMode.Delete && retention.Mode != RetentionMode.Delete)
        {
            var count = DataCleanup.PickForRetention(
                services.ChatStore.List(),
                services.Organizer.Snapshot(),
                new ChatRetention { Mode = RetentionMode.Delete, Days = retention.Days },
                DateTime.Now,
                BusyChats?.Invoke() ?? new HashSet<string>()).Count;
            var agreed = Confirm is { } confirm && await confirm(
                Loc.Get("S.Care.DeleteTitle"),
                Loc.Format("S.Care.DeleteConfirm", retention.Days.ToString(CultureInfo.InvariantCulture), count.ToString(CultureInfo.InvariantCulture)),
                Loc.Get("S.Care.EnableDelete"))
                .ConfigureAwait(true);
            if (!agreed)
            {
                _loading = true;
                SelectMode(retention.Mode);
                _loading = false;
                return;
            }
        }

        DaysRow.IsEnabled = mode != RetentionMode.Off;
        Save(copy => copy.Mode = mode);
    }

    /// <summary>
    /// Переписывает чаты прежних версий: вложения уходят в отдельные файлы (F4). Байтами и под
    /// замком файла — идущий ход при этом не теряет ни слова (см. <c>ChatStore.ExternalizeExisting</c>).
    /// </summary>
    private async void Rewrite_Click(object sender, RoutedEventArgs e)
    {
        if (_services is not { } services)
        {
            return;
        }

        RewriteButton.IsEnabled = false;
        AttachmentsText.Text = Loc.Get("S.Care.Rewriting");
        try
        {
            var count = await Task.Run(() => services.ChatStore.ExternalizeExisting(CancellationToken.None)).ConfigureAwait(true);
            AttachmentsText.Text = Loc.Format("S.Care.Rewritten", count.ToString(CultureInfo.InvariantCulture));
            Cleaned?.Invoke();
            await MeasureAsync().ConfigureAwait(true);
        }
        finally
        {
            RewriteButton.IsEnabled = true;
        }
    }

    private void DaysBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var current = _services?.Settings.Retention?.Days ?? 180;
        var days = int.TryParse(DaysBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= DataCleanup.MinRetentionDays && value <= 3650
            ? value
            : current;
        DaysBox.Text = days.ToString(CultureInfo.InvariantCulture);
        if (days != current)
        {
            Save(copy => copy.Days = days);
        }
    }

    private void Save(Action<ChatRetention> change)
    {
        if (_services is null)
        {
            return;
        }

        var retention = _services.Settings.Retention ??= new ChatRetention();
        change(retention);
        _services.SettingsStore.Save(_services.Settings);
        Changed?.Invoke();
    }
}
