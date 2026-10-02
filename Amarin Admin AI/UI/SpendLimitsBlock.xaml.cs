using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Блок «Лимиты трат» (E1) и «Порог остатка» (E4) на странице «Key &amp; Info».
/// </summary>
/// <remarks>
/// Как у страниц настроек: правда в <see cref="AppSettings.SpendLimits"/>, поля только отражают её.
/// Сохранение подменяет объект лимитов целиком, а не правит словарь на месте: его читает
/// <see cref="SpendGuard"/> из потока хода, и словарь, меняющийся под чтением, мог бы бросить.
/// </remarks>
public partial class SpendLimitsBlock : UserControl
{
    private AppServices? _services;

    public SpendLimitsBlock() => InitializeComponent();

    /// <summary>Лимит или порог поменялся — строка-ссылка на странице перечитывает своё значение.</summary>
    internal event Action? Changed;

    /// <summary>Значение строки-ссылки: самый заметный из лимитов коротко, или «выключено».</summary>
    internal static string Summary(SpendLimits? limits) =>
        limits switch
        {
            { DayUsd: { } day } => Loc.Format("S.Limit.Short.Day", SpendReport.FormatUsd(day)),
            { MonthUsd: { } month } => Loc.Format("S.Limit.Short.Month", SpendReport.FormatUsd(month)),
            { TurnUsd: { } turn } => Loc.Format("S.Limit.Short.Turn", SpendReport.FormatUsd(turn)),
            { Keys.Count: > 0 } => Loc.Get("S.Limit.Short.Keys"),
            _ => Loc.Get("S.Common.Off")
        };

    internal void Load(AppServices services)
    {
        _services = services;
        var limits = services.Settings.SpendLimits ?? new SpendLimits();

        PeriodGrid.Children.Clear();
        PeriodGrid.RowDefinitions.Clear();
        AddRow(PeriodGrid, null, "", Loc.Get("S.Limit.PerDay"), Loc.Get("S.Limit.PerMonth"), header: true);
        AddRow(
            PeriodGrid,
            null,
            Loc.Get("S.Limit.Profile"),
            MoneyField(limits.DayUsd, value => Change(copy => copy.DayUsd = value), Loc.Get("S.Limit.Profile") + " · " + Loc.Get("S.Limit.PerDay")),
            MoneyField(limits.MonthUsd, value => Change(copy => copy.MonthUsd = value), Loc.Get("S.Limit.Profile") + " · " + Loc.Get("S.Limit.PerMonth")));

        // По строке на секрет: ключ окружения и его копия в keys.json — один счёт.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in services.KeyStore.List())
        {
            if (entry.IsBroken || !seen.Add(ApiKeyStore.Fingerprint(entry.Secret)))
            {
                continue;
            }

            var fingerprint = ApiKeyStore.Fingerprint(entry.Secret);
            limits.Keys.TryGetValue(fingerprint, out var own);
            var name = Loc.Format("S.Limit.KeyRow", entry.Label, ProviderSpec.For(entry.Provider).Name);
            AddRow(
                PeriodGrid,
                fingerprint,
                name,
                MoneyField(own?.DayUsd, value => ChangeKey(fingerprint, key => key.DayUsd = value), name + " · " + Loc.Get("S.Limit.PerDay")),
                MoneyField(own?.MonthUsd, value => ChangeKey(fingerprint, key => key.MonthUsd = value), name + " · " + Loc.Get("S.Limit.PerMonth")));
        }

        TurnSlot.Content = MoneyField(limits.TurnUsd, value => Change(copy => copy.TurnUsd = value), Loc.Get("S.Limit.TurnLabel"));
        WarnBox.Text = limits.WarnPercent.ToString(CultureInfo.InvariantCulture);
        LoadBalance(services);
    }

    /// <summary>Пороги остатка (E4): сумма по всем ключам — «мало» и «почти кончились», у ключа — «мало».</summary>
    private void LoadBalance(AppServices services)
    {
        var thresholds = services.Settings.BalanceThresholds ?? new BalanceThresholds();
        BalanceGrid.Children.Clear();
        BalanceGrid.RowDefinitions.Clear();
        AddRow(BalanceGrid, null, "", Loc.Get("S.BalanceLimit.Low"), Loc.Get("S.BalanceLimit.Critical"), header: true);
        AddRow(
            BalanceGrid,
            null,
            Loc.Get("S.Limit.Profile"),
            MoneyField(thresholds.LowUsd, value => ChangeBalance(copy => copy.LowUsd = value), Loc.Get("S.Limit.Profile") + " · " + Loc.Get("S.BalanceLimit.Low")),
            MoneyField(thresholds.CriticalUsd, value => ChangeBalance(copy => copy.CriticalUsd = value), Loc.Get("S.Limit.Profile") + " · " + Loc.Get("S.BalanceLimit.Critical")));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in services.KeyStore.List())
        {
            var fingerprint = ApiKeyStore.Fingerprint(entry.Secret);
            if (entry.IsBroken || !seen.Add(fingerprint))
            {
                continue;
            }

            decimal? low = thresholds.Keys.TryGetValue(fingerprint, out var own) ? own : null;
            var name = Loc.Format("S.Limit.KeyRow", entry.Label, ProviderSpec.For(entry.Provider).Name);
            AddRow(
                BalanceGrid,
                fingerprint,
                name,
                MoneyField(low, value => ChangeBalance(copy =>
                {
                    if (value is { } set)
                    {
                        copy.Keys[fingerprint] = set;
                    }
                    else
                    {
                        copy.Keys.Remove(fingerprint);
                    }
                }), name + " · " + Loc.Get("S.BalanceLimit.Low")),
                null);
        }
    }

    private void ChangeBalance(Action<BalanceThresholds> change)
    {
        if (_services is null)
        {
            return;
        }

        var source = _services.Settings.BalanceThresholds ?? new BalanceThresholds();
        var copy = new BalanceThresholds
        {
            LowUsd = source.LowUsd,
            CriticalUsd = source.CriticalUsd,
            Keys = new Dictionary<string, decimal>(source.Keys, StringComparer.Ordinal)
        };
        change(copy);
        _services.Settings.BalanceThresholds = copy;
        _services.SettingsStore.Save(_services.Settings);
        Changed?.Invoke();
    }

    /// <summary>Строка таблицы: подпись и два поля (или две подписи колонок).</summary>
    private void AddRow(Grid grid, string? tag, string label, object day, object? month, bool header = false)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var caption = new TextBlock
        {
            Text = label,
            Tag = tag,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, header ? 0 : 3, 10, 3),
            Style = (Style)FindResource(header ? "FieldHint" : "SettingTitle")
        };
        Grid.SetRow(caption, row);
        grid.Children.Add(caption);

        var column = 1;
        foreach (var cell in new[] { day, month })
        {
            if (cell is null)
            {
                continue;
            }

            var element = cell as FrameworkElement ?? new TextBlock
            {
                Text = (string)cell,
                Style = (Style)FindResource("FieldHint"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0)
            };
            element.Margin = new Thickness(column == 1 ? 0 : 8, header ? 0 : 3, 0, 3);
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column++);
            grid.Children.Add(element);
        }
    }

    /// <summary>Поле суммы: «$» впереди, «нет» пустым, проверка при уходе фокуса.</summary>
    private FrameworkElement MoneyField(decimal? value, Action<decimal?> save, string name)
    {
        var box = new TextBox
        {
            Text = SpendRules.FormatField(value),
            Style = (Style)FindResource("FieldTextBox"),
            MaxLength = 9,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        System.Windows.Automation.AutomationProperties.SetName(box, name);
        var placeholder = new TextBlock
        {
            Style = (Style)FindResource("Placeholder"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed
        };
        placeholder.SetResourceReference(TextBlock.TextProperty, "S.Limit.None");
        box.TextChanged += (_, _) => placeholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Непонятное число не сохраняется «как нет лимита» молча: поле возвращает прежнее значение.
        var current = value;
        box.LostFocus += (_, _) =>
        {
            var text = box.Text.Trim();
            var parsed = SpendRules.ParseUsd(text);
            if (text.Length > 0 && parsed is null && !IsZero(text))
            {
                box.Text = SpendRules.FormatField(current);
                return;
            }

            box.Text = SpendRules.FormatField(parsed);
            if (parsed != current)
            {
                current = parsed;
                save(parsed);
            }
        };

        var frame = new Border { Style = (Style)FindResource("FieldFrame"), Height = 28, Width = 84 };
        var grid = new Grid();
        grid.Children.Add(box);
        grid.Children.Add(placeholder);
        frame.Child = grid;

        var dollar = new TextBlock { Text = "$", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), FontSize = 12 };
        dollar.SetResourceReference(TextBlock.ForegroundProperty, "Text.Dim");
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(dollar);
        panel.Children.Add(frame);
        return panel;
    }

    /// <summary>«0» — осознанное «без лимита», а не опечатка.</summary>
    private static bool IsZero(string text) =>
        decimal.TryParse(text.TrimStart('$').Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value <= 0m;

    private void WarnBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var percent = int.TryParse(WarnBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value is >= 1 and <= 99
            ? value
            : _services?.Settings.SpendLimits?.WarnPercent ?? 80;
        WarnBox.Text = percent.ToString(CultureInfo.InvariantCulture);
        if (_services?.Settings.SpendLimits?.WarnPercent != percent)
        {
            Change(copy => copy.WarnPercent = percent);
        }
    }

    private void ChangeKey(string fingerprint, Action<KeySpendLimit> change) =>
        Change(copy =>
        {
            var key = copy.Keys.TryGetValue(fingerprint, out var existing)
                ? new KeySpendLimit { DayUsd = existing.DayUsd, MonthUsd = existing.MonthUsd }
                : new KeySpendLimit();
            change(key);
            if (key.DayUsd is null && key.MonthUsd is null)
            {
                copy.Keys.Remove(fingerprint);
            }
            else
            {
                copy.Keys[fingerprint] = key;
            }
        });

    private void Change(Action<SpendLimits> change)
    {
        if (_services is null)
        {
            return;
        }

        var copy = Copy(_services.Settings.SpendLimits ?? new SpendLimits());
        change(copy);
        _services.Settings.SpendLimits = copy;
        _services.SettingsStore.Save(_services.Settings);
        Changed?.Invoke();
    }

    internal static SpendLimits Copy(SpendLimits source) => new()
    {
        DayUsd = source.DayUsd,
        MonthUsd = source.MonthUsd,
        TurnUsd = source.TurnUsd,
        WarnPercent = source.WarnPercent,
        Keys = source.Keys.ToDictionary(
            pair => pair.Key,
            pair => new KeySpendLimit { DayUsd = pair.Value.DayUsd, MonthUsd = pair.Value.MonthUsd },
            StringComparer.Ordinal)
    };
}
