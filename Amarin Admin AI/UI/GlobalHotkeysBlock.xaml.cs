using System.Windows;
using System.Windows.Controls;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Сочетания, которые работают из любой программы (G5): показать окно, новый чат с буфером, со
/// снимком экрана.
/// </summary>
/// <remarks>
/// Правда — в <see cref="WindowsIntegrationSettings.Hotkeys"/>; после правки окно регистрирует
/// сочетания заново через <see cref="Applied"/> и отдаёт, что ответила Windows. Пустое поле —
/// явное «без сочетания»: заводское само не вернётся.
/// </remarks>
public partial class GlobalHotkeysBlock : UserControl
{
    private AppServices? _services;
    private readonly Dictionary<string, TextBlock> _status = new(StringComparer.Ordinal);

    public GlobalHotkeysBlock() => InitializeComponent();

    /// <summary>Зарегистрировать сочетания заново — ставит окно. Возвращает отказы Windows.</summary>
    internal Func<IReadOnlyDictionary<string, string>>? Applied { get; set; }

    internal void Load(AppServices services)
    {
        _services = services;
        var settings = services.Settings.Windows ??= new WindowsIntegrationSettings();
        Rows.Children.Clear();
        _status.Clear();
        foreach (var action in GlobalHotkeys.All)
        {
            Rows.Children.Add(BuildRow(action, GlobalHotkeys.Effective(settings, action)));
        }
    }

    /// <summary>Что Windows ответила на регистрацию — под каждым сочетанием.</summary>
    internal void ShowProblems(IReadOnlyDictionary<string, string> problems)
    {
        foreach (var (action, label) in _status)
        {
            label.Text = problems.TryGetValue(action, out var problem) ? problem : "";
            label.Visibility = label.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>Строка той же формы, что у сочетаний программы: подпись слева, поле справа.</summary>
    private Grid BuildRow(string action, string? gesture)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var caption = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        var title = new TextBlock { Style = (Style)FindResource("SettingTitle") };
        title.SetResourceReference(TextBlock.TextProperty, "S.Windows.Hotkey." + action);
        caption.Children.Add(title);
        var status = new TextBlock { Style = (Style)FindResource("SettingDesc"), Visibility = Visibility.Collapsed };
        status.SetResourceReference(TextBlock.ForegroundProperty, "Status.Warning");
        caption.Children.Add(status);
        _status[action] = status;
        row.Children.Add(caption);

        var box = new TextBox
        {
            Text = gesture ?? "",
            Style = (Style)FindResource("FieldTextBox"),
            MaxLength = 40,
            FontSize = 11.5,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            ToolTip = Loc.Get("S.Windows.HotkeysHint")
        };
        System.Windows.Automation.AutomationProperties.SetName(box, Loc.Get("S.Windows.Hotkey." + action));
        var placeholder = new TextBlock
        {
            Style = (Style)FindResource("Placeholder"),
            FontSize = 11.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed
        };
        placeholder.SetResourceReference(TextBlock.TextProperty, "S.Windows.HotkeyNone");
        box.TextChanged += (_, _) => placeholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.LostFocus += (_, _) =>
        {
            var text = box.Text.Trim();
            if (text.Length > 0 && !GlobalHotkeys.TryParse(text, out _, out _))
            {
                status.Text = Loc.Get("S.Windows.HotkeyInvalid");
                status.Visibility = Visibility.Visible;
                return;
            }

            Save(action, text);
        };

        // Рамка как у поля сочетания программы (HotkeyField): одна строка — один вид.
        var frame = new Border
        {
            Height = 26,
            MinWidth = 104,
            Width = 150,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        frame.SetResourceReference(Border.BackgroundProperty, "Bg.Raised");
        frame.SetResourceReference(Border.BorderBrushProperty, "Border.Default");
        var inner = new Grid();
        inner.Children.Add(box);
        inner.Children.Add(placeholder);
        frame.Child = inner;
        Grid.SetColumn(frame, 1);
        row.Children.Add(frame);
        return row;
    }

    private void Save(string action, string gesture)
    {
        if (_services is null)
        {
            return;
        }

        var settings = _services.Settings.Windows ??= new WindowsIntegrationSettings();
        settings.Hotkeys[action] = gesture;
        _services.SettingsStore.Save(_services.Settings);
        if (Applied?.Invoke() is { } problems)
        {
            ShowProblems(problems);
        }
    }
}
