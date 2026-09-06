using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// The itemised bill behind the single price in a message's meta row.
/// </summary>
/// <remarks>
/// <para>
/// The total covers the conversation with the model, every tool that charged for itself —
/// drawing a picture, scraping a page — and every sub-agent the turn started. A turn that drew
/// two pictures can cost twenty times what the text did, and with one number on screen there is
/// no way to tell that from an expensive model.
/// </para>
/// <para>
/// Built in code rather than declared in XAML because the number of rows depends on the turn.
/// The chrome copies the warning tooltip in <c>MainWindow.xaml</c>: a transparent
/// <see cref="ToolTip"/> whose template draws an arrow and a themed card. UI scaling is applied
/// by <c>UiScale</c> through a class handler on <see cref="ToolTip.OpenedEvent"/>, so nothing
/// has to be registered here.
/// </para>
/// </remarks>
internal static class CostBreakdownTooltip
{
    public static ToolTip Build(FrameworkElement host, ChatDisplayMessage message)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(message);

        var rows = new Grid { Margin = new Thickness(0) };
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var line = 0;
        var model = VeniceModelCatalog.GetDisplayName(
            message.ResolvedModelId ?? message.RequestedModelId ?? "");
        AddRow(rows, ref line, string.IsNullOrWhiteSpace(model) ? "Модель" : model, message.ModelCost, bold: false);

        foreach (var round in message.ToolRounds)
        {
            foreach (var call in round.Calls)
            {
                if (call.NestedAgent is { Cost.HasData: true } agent)
                {
                    var name = string.IsNullOrWhiteSpace(agent.DisplayName)
                        ? "Агент " + agent.ModelId
                        : "Агент · " + agent.DisplayName;
                    AddRow(rows, ref line, name, agent.Cost, bold: false);
                    continue;
                }

                if (call.Cost is { HasData: true })
                {
                    AddRow(rows, ref line, call.Name, call.Cost, bold: false);
                }
            }
        }

        // With a single line there is nothing to add up, and a total under it would just repeat
        // the row above.
        if (line > 1)
        {
            var rule = new Border
            {
                Height = 1,
                Margin = new Thickness(0, 5, 0, 5),
                SnapsToDevicePixels = true
            };
            rule.SetResourceReference(Border.BackgroundProperty, "Border.Default");
            rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(rule, line);
            Grid.SetColumnSpan(rule, 2);
            rows.Children.Add(rule);
            line++;

            AddRow(rows, ref line, "Итого", message.Cost, bold: true);
        }

        return Chrome(rows);
    }

    private static void AddRow(Grid rows, ref int line, string label, VeniceCost? cost, bool bold)
    {
        var name = new TextBlock
        {
            Text = label,
            FontSize = 11.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 190,
            Margin = new Thickness(0, 1, 14, 1),
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, bold ? "Text.Secondary" : "Text.Muted");

        var price = new TextBlock
        {
            // A zero-cost line is still worth a row: it says the tool ran and charged nothing,
            // which is different from it not having run.
            Text = ChatFormat.Cost(cost) is { Length: > 0 } value ? value : "$0",
            FontSize = 11.5,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 1, 0, 1),
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal
        };
        price.SetResourceReference(TextBlock.ForegroundProperty, bold ? "Text.Bright" : "Text.Secondary");

        rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(name, line);
        Grid.SetRow(price, line);
        Grid.SetColumn(price, 1);
        rows.Children.Add(name);
        rows.Children.Add(price);
        line++;
    }

    private static ToolTip Chrome(UIElement content)
    {
        var card = new Border
        {
            MinWidth = 180,
            MaxWidth = 300,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Child = content
        };
        card.SetResourceReference(Border.BackgroundProperty, "Bg.Card");
        card.SetResourceReference(Border.BorderBrushProperty, "Border.Default");

        var arrow = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M0,6 L6,0 L12,6 Z"),
            StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(12, 0, 0, -1)
        };
        arrow.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Bg.Card");
        arrow.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Border.Default");

        var stack = new Grid();
        stack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
        stack.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(arrow, 0);
        Grid.SetRow(card, 1);
        stack.Children.Add(arrow);
        stack.Children.Add(card);

        var tip = new ToolTip
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HasDropShadow = false,
            Content = stack
        };
        tip.Template = BuildTemplate();
        return tip;
    }

    /// <summary>
    /// A bare template: the default one paints its own background and border around the card,
    /// which would double the rim and put an opaque rectangle behind the arrow.
    /// </summary>
    private static ControlTemplate BuildTemplate()
    {
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        return new ControlTemplate(typeof(ToolTip)) { VisualTree = presenter };
    }
}
