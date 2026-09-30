using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Где исполняются команды чата: этот ПК или удалённая машина (C10). Кнопка в панели композера —
/// рядом с отправкой, чтобы цель была перед глазами в ту минуту, когда уходит задание.
/// </summary>
public partial class TargetPicker : UserControl
{
    private IReadOnlyList<RemoteMachine> _machines = [];

    public TargetPicker()
    {
        InitializeComponent();
        PopupManager.Register(Popup, Toggle);
    }

    /// <summary>Выбрана цель: id машины или null — этот ПК.</summary>
    internal event Action<string?>? Picked;

    /// <summary>«Настроить машины…».</summary>
    internal event Action? ManageRequested;

    /// <summary>Выбранная цель; null — этот ПК.</summary>
    internal string? SelectedId { get; private set; }

    /// <summary>
    /// Список и выбор. Без машин кнопка прячется, если только чат не смотрит на машину: тогда
    /// её видно всегда — пусть и удалённую, — чтобы человек не гадал, куда уходят команды.
    /// </summary>
    internal void Show(IReadOnlyList<RemoteMachine> machines, string? selectedId)
    {
        _machines = machines;
        SelectedId = selectedId;
        var selected = machines.FirstOrDefault(machine => machine.Id == selectedId);
        TargetLabel.Text = selectedId is null
            ? Loc.Get("S.Remote.ThisPc")
            : selected?.Name ?? Loc.Get("S.Remote.Missing");
        RemoteDot.Visibility = selectedId is null ? Visibility.Collapsed : Visibility.Visible;
        Visibility = machines.Count > 0 || selectedId is not null ? Visibility.Visible : Visibility.Collapsed;
        BuildItems();
    }

    private void BuildItems()
    {
        Items.Children.Clear();
        Items.Children.Add(Item(null, Loc.Get("S.Remote.ThisPc"), Environment.MachineName));
        foreach (var machine in _machines)
        {
            Items.Children.Add(Item(machine.Id, machine.Name, machine.Address));
        }
    }

    private Button Item(string? id, string title, string detail)
    {
        var text = new StackPanel();
        var name = new TextBlock { Text = title, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis };
        name.SetResourceReference(TextBlock.ForegroundProperty, id == SelectedId ? "Text.Primary" : "Text.Secondary");
        if (id == SelectedId)
        {
            name.FontWeight = FontWeights.SemiBold;
        }

        var sub = new TextBlock { Text = detail, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "Text.Faint");
        text.Children.Add(name);
        text.Children.Add(sub);

        var border = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 5, 10, 5), Child = text };
        border.SetResourceReference(Border.BackgroundProperty, id == SelectedId ? "Bg.Selected" : "Bg.Panel");
        var button = new Button
        {
            Tag = id,
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = ItemTemplate(),
            Content = border
        };
        AutomationProperties.SetName(button, title);
        button.Click += (_, _) =>
        {
            Toggle.IsChecked = false;
            if (id != SelectedId)
            {
                Picked?.Invoke(id);
            }
        };
        return button;
    }

    private static ControlTemplate ItemTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        template.VisualTree = presenter;
        return template;
    }

    private void Manage_Click(object sender, RoutedEventArgs e)
    {
        Toggle.IsChecked = false;
        ManageRequested?.Invoke();
    }
}
