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
/// Правила правки — в <see cref="SpendLimitsEditor"/>; здесь только поля и их подписи.
/// </remarks>
public partial class SpendLimitsBlock : UserControl
{
    private SpendLimitsEditor? _editor;

    public SpendLimitsBlock() => InitializeComponent();

    /// <summary>Лимит или порог поменялся — строка-ссылка на странице перечитывает своё значение.</summary>
    internal event Action? Changed;

    internal void Load(AppServices services)
    {
        var editor = new SpendLimitsEditor(() => services.Settings, settings => services.SettingsStore.Save(settings));
        editor.Changed += () => Changed?.Invoke();
        _editor = editor;
        var limits = editor.Limits;
        var keys = SpendLimitsEditor.KeyRows(services.KeyStore.List());

        PeriodGrid.Children.Clear();
        PeriodGrid.RowDefinitions.Clear();
        AddRow(PeriodGrid, null, "", Loc.Get("S.Limit.PerDay"), Loc.Get("S.Limit.PerMonth"), header: true);
        AddRow(
            PeriodGrid,
            null,
            Loc.Get("S.Limit.Profile"),
            MoneyField(limits.DayUsd, editor.SetProfileDay, Loc.Get("S.Limit.Profile") + " · " + Loc.Get("S.Limit.PerDay")),
            MoneyField(limits.MonthUsd, editor.SetProfileMonth, Loc.Get("S.Limit.Profile") + " · " + Loc.Get("S.Limit.PerMonth")));

        foreach (var key in keys)
        {
            limits.Keys.TryGetValue(key.Fingerprint, out var own);
            var name = KeyName(key);
            AddRow(
                PeriodGrid,
                key.Fingerprint,
                name,
                MoneyField(own?.DayUsd, value => editor.SetKeyDay(key.Fingerprint, value), name + " · " + Loc.Get("S.Limit.PerDay")),
                MoneyField(own?.MonthUsd, value => editor.SetKeyMonth(key.Fingerprint, value), name + " · " + Loc.Get("S.Limit.PerMonth")));
        }

        TurnSlot.Content = MoneyField(limits.TurnUsd, editor.SetTurn, Loc.Get("S.Limit.TurnLabel"));
        WarnBox.Text = limits.WarnPercent.ToString(CultureInfo.InvariantCulture);
        LoadBalance(editor, keys);
    }

    /// <summary>Пороги остатка (E4): сумма по всем ключам — «мало» и «почти кончились», у ключа — «мало».</summary>
    private void LoadBalance(SpendLimitsEditor editor, IReadOnlyList<SpendKeyRow> keys)
    {
        var thresholds = editor.Thresholds;
        BalanceGrid.Children.Clear();
        BalanceGrid.RowDefinitions.Clear();
        AddRow(BalanceGrid, null, "", Loc.Get("S.BalanceLimit.Low"), Loc.Get("S.BalanceLimit.Critical"), header: true);
        AddRow(
            BalanceGrid,
            null,
            Loc.Get("S.Limit.Profile"),
            MoneyField(thresholds.LowUsd, editor.SetBalanceLow, Loc.Get("S.Limit.Profile") + " · " + Loc.Get("S.BalanceLimit.Low")),
            MoneyField(thresholds.CriticalUsd, editor.SetBalanceCritical, Loc.Get("S.Limit.Profile") + " · " + Loc.Get("S.BalanceLimit.Critical")));

        foreach (var key in keys)
        {
            decimal? low = thresholds.Keys.TryGetValue(key.Fingerprint, out var own) ? own : null;
            var name = KeyName(key);
            AddRow(
                BalanceGrid,
                key.Fingerprint,
                name,
                MoneyField(low, value => editor.SetKeyBalanceLow(key.Fingerprint, value), name + " · " + Loc.Get("S.BalanceLimit.Low")),
                null);
        }
    }

    private static string KeyName(SpendKeyRow key) =>
        Loc.Format("S.Limit.KeyRow", key.Label, ProviderSpec.For(key.Provider).Name);

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

        var current = value;
        box.LostFocus += (_, _) =>
        {
            var edit = SpendLimitsEditor.AcceptMoney(box.Text, current);
            box.Text = edit.Text;
            if (edit.Changed)
            {
                current = edit.Value;
                save(edit.Value);
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

    private void WarnBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_editor is not { } editor)
        {
            return;
        }

        WarnBox.Text = editor.SetWarnPercent(WarnBox.Text).ToString(CultureInfo.InvariantCulture);
    }
}
