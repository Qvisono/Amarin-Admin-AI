using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Блок «Хранение и очистка» на странице данных (F3).
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
            (retention.Mode switch
            {
                RetentionMode.Archive => ModeArchive,
                RetentionMode.Delete => ModeDelete,
                _ => ModeOff
            }).IsChecked = true;
            DaysBox.Text = retention.Days.ToString(CultureInfo.InvariantCulture);
            DaysBox.IsEnabled = retention.Mode != RetentionMode.Off;
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
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        text.Children.Add(new TextBlock { Text = Loc.Get(TitleKey(target)), Style = (Style)FindResource("SettingTitle") });
        text.Children.Add(new TextBlock
        {
            Text = size.Files == 0
                ? Loc.Get("S.Care.Nothing")
                : Loc.Format("S.Care.Size", AttachmentTypes.FormatSize(size.Bytes), size.Files.ToString(CultureInfo.InvariantCulture)),
            Style = (Style)FindResource("FieldHint")
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

    private Button LargestRow(ChatSize chat, string title)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new TextBlock
        {
            Text = title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        var size = new TextBlock { Text = AttachmentTypes.FormatSize(chat.Bytes), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        size.SetResourceReference(TextBlock.ForegroundProperty, "Text.Dim");
        Grid.SetColumn(size, 1);
        grid.Children.Add(name);
        grid.Children.Add(size);

        var button = new Button { Content = grid, Style = (Style)FindResource("CardButton"), Margin = new Thickness(0, 0, 0, 6) };
        System.Windows.Automation.AutomationProperties.SetName(button, title);
        button.Click += (_, _) => OpenChat?.Invoke(chat.Id);
        return button;
    }

    private async void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || _services is not { } services)
        {
            return;
        }

        var mode = ModeDelete.IsChecked == true ? RetentionMode.Delete
            : ModeArchive.IsChecked == true ? RetentionMode.Archive
            : RetentionMode.Off;
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
                (retention.Mode == RetentionMode.Archive ? ModeArchive : ModeOff).IsChecked = true;
                _loading = false;
                return;
            }
        }

        DaysBox.IsEnabled = mode != RetentionMode.Off;
        Save(copy => copy.Mode = mode);
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
    }
}
