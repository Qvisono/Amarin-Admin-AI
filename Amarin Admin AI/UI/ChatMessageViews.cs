using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Amarin.Core;

namespace Amarin.UI;

internal sealed class MessageActions
{
    public Action<ChatDisplayMessage>? Copy;
    public Action<ChatDisplayMessage>? Edit;
    public Action<ChatDisplayMessage, string>? CommitEdit;
    public Action<ChatDisplayMessage>? Delete;
    public Action<ChatDisplayMessage>? Regenerate;
    public Action<ChatDisplayMessage>? Cancel;
}

internal sealed class UserMessageView
{
    public FrameworkElement Root { get; }
    public required Border Bubble { get; init; }
    public required RichTextBox Display { get; init; }
    public required TextBox Editor { get; init; }

    public UserMessageView(FrameworkElement root) => Root = root;

    public void BeginEdit(string text)
    {
        Editor.Text = text;
        var width = Display.Width > 1 ? Display.Width : Display.ActualWidth;
        if (width > 1)
        {
            Editor.MinWidth = width;
            Editor.Width = width;
        }

        Display.Visibility = Visibility.Collapsed;
        Editor.Visibility = Visibility.Visible;
        Editor.Focus();
        Editor.CaretIndex = Editor.Text.Length;
    }

    public void CancelEdit()
    {
        Editor.Visibility = Visibility.Collapsed;
        Display.Visibility = Visibility.Visible;
    }
}

internal sealed class AssistantMessageView
{
    public FrameworkElement Root { get; }
    public required RichTextBox Body { get; init; }
    public required TextBlock ModelName { get; init; }
    public required TextBlock Clock { get; init; }
    public required TextBlock Duration { get; init; }
    public required TextBlock Cost { get; init; }
    public required Ellipse CostDot { get; init; }
    public required StackPanel Actions { get; init; }
    public required Button CancelButton { get; init; }
    public required Image LogoImage { get; init; }
    public required TextBlock LogoLetter { get; init; }
    public required System.Windows.Shapes.Path LogoLightning { get; init; }
    public required FrameworkElement RootElement { get; init; }
    public required StackPanel ToolsHost { get; init; }
    public required FrameworkElement Host { get; init; }

    private Storyboard? _pulse;
    private Expander? _toolsExpander;
    private readonly Dictionary<string, bool> _nestedExpanded = new();

    public AssistantMessageView(FrameworkElement root) => Root = root;

    public void ApplyBranding(FrameworkElement host, string modelId)
    {
        ModelName.Text = VeniceModelCatalog.GetDisplayName(modelId);
        ModelBrand.Apply(host, modelId, LogoImage, LogoLetter, LogoLightning);
    }

    public void SetBody(string text, bool markdown)
    {
        var empty = string.IsNullOrWhiteSpace(text);
        Body.Visibility = empty && markdown is false
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (empty)
        {
            return;
        }

        ChatMarkdown.Write(
            Body,
            text,
            markdown,
            Color.FromRgb(0xDC, 0xDC, 0xDC),
            fontSize: 13.5,
            lineHeight: 21);
    }

    public void ShowWorking(TimeSpan elapsed)
    {
        Duration.Text = ChatFormat.Working(elapsed);
        Duration.FontWeight = FontWeights.SemiBold;
        Duration.Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
        Cost.Visibility = Visibility.Collapsed;
        CostDot.Visibility = Visibility.Collapsed;
        StartPulse();
    }

    public void ShowFinished(ChatDisplayMessage message)
    {
        StopPulse();
        Duration.Opacity = 1;
        Duration.FontWeight = FontWeights.Normal;
        Duration.Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));
        Duration.Text = ChatFormat.Duration(message.Duration);
        var cost = ChatFormat.Cost(message.Cost);
        if (string.IsNullOrEmpty(cost))
        {
            Cost.Visibility = Visibility.Collapsed;
            CostDot.Visibility = Visibility.Collapsed;
        }
        else
        {
            Cost.Text = cost;
            Cost.Visibility = Visibility.Visible;
            CostDot.Visibility = Visibility.Visible;
        }

        Body.Visibility = Visibility.Visible;
        Actions.Margin = new Thickness(0, 10, 0, 0);
        CancelButton.Visibility = Visibility.Collapsed;
        foreach (UIElement child in Actions.Children)
        {
            if (!ReferenceEquals(child, CancelButton))
            {
                child.Visibility = Visibility.Visible;
            }
        }
    }

    public void ShowCancelOnly()
    {
        Actions.Margin = new Thickness(0, 4, 0, 0);
        CancelButton.Visibility = Visibility.Visible;
        foreach (UIElement child in Actions.Children)
        {
            if (!ReferenceEquals(child, CancelButton))
            {
                child.Visibility = Visibility.Collapsed;
            }
        }
    }

    public void UpdateTools(ChatDisplayMessage message)
    {
        if (message.ToolRounds.Count == 0)
        {
            ToolsHost.Visibility = Visibility.Collapsed;
            return;
        }

        ToolsHost.Visibility = Visibility.Visible;
        _toolsExpander ??= CreateToolsExpander();
        if (!ToolsHost.Children.Contains(_toolsExpander))
        {
            ToolsHost.Children.Clear();
            ToolsHost.Children.Add(_toolsExpander);
        }

        var wasExpanded = _toolsExpander.IsExpanded;
        _toolsExpander.Header = BuildToolsHeader(message);
        _toolsExpander.Content = BuildToolsBody(message);
        _toolsExpander.IsExpanded = wasExpanded;
    }

    private Expander CreateToolsExpander() =>
        new()
        {
            Style = (Style)Host.FindResource("ToolsExpander"),
            IsExpanded = false,
            Margin = new Thickness(0, 0, 0, 8)
        };

    private object BuildToolsHeader(ChatDisplayMessage message)
    {
        var calls = message.ToolRounds.SelectMany(round => round.Calls).ToList();
        var running = calls.Any(call =>
            call.Status is ToolCallStatus.Pending or ToolCallStatus.Running ||
            call.NestedAgent?.Status == AgentRunStatus.Running);

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        if (!running)
        {
            header.Children.Add(new System.Windows.Shapes.Path
            {
                Style = (Style)Host.FindResource("ToolDoneIcon"),
                Margin = new Thickness(0, 0, 7, 0)
            });
        }

        header.Children.Add(new TextBlock
        {
            Style = (Style)Host.FindResource("ExpanderHeaderText"),
            Text = running ? "Запускаю инструменты" : "Инструменты выполнены"
        });

        if (!running && calls.Count > 0)
        {
            header.Children.Add(new TextBlock
            {
                Text = "· " + calls.Count,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x56, 0x56, 0x56)),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        return header;
    }

    private StackPanel BuildToolsBody(ChatDisplayMessage message)
    {
        var body = new StackPanel();
        for (var i = 0; i < message.ToolRounds.Count; i++)
        {
            var round = message.ToolRounds[i];
            if (i > 0)
            {
                body.Children.Add(new Border { Style = (Style)Host.FindResource("ToolStepDivider") });
            }

            foreach (var call in round.Calls)
            {
                body.Children.Add(BuildCallRow(call));
                if (call.NestedAgent is not null)
                {
                    body.Children.Add(BuildNestedAgentExpander(call));
                }

                if (call.Status is ToolCallStatus.Done or ToolCallStatus.Failed)
                {
                    body.Children.Add(BuildResultRow(call));
                }
            }

            if (!string.IsNullOrWhiteSpace(round.InfoLine) &&
                !round.InfoLine.Equals("Запускаю инструменты", StringComparison.Ordinal))
            {
                body.Children.Add(BuildInfoRow(round.InfoLine));
            }
        }

        return body;
    }

    private Grid BuildCallRow(ToolCallRecord call)
    {
        var grid = new Grid { Style = (Style)Host.FindResource("ToolRow") };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var pending = call.Status is ToolCallStatus.Pending or ToolCallStatus.Running;
        UIElement icon;
        if (call.Name.Equals("init_agent", StringComparison.OrdinalIgnoreCase))
        {
            icon = BuildAgentGlyph(call.NestedAgent?.ModelId ?? AgentHost.ForcedAgentModelId, 14);
        }
        else
        {
            icon = new TextBlock
            {
                Style = pending
                    ? (Style)Host.FindResource("ToolPendingIcon")
                    : (Style)Host.FindResource("ToolIcon"),
                Text = call.Name.Equals("search_web", StringComparison.OrdinalIgnoreCase) ? "🔍" : "⚙"
            };
        }

        var name = new TextBlock
        {
            Style = (Style)Host.FindResource("ToolName"),
            Text = call.Name
        };
        var args = new TextBlock
        {
            Style = (Style)Host.FindResource("ToolArgs"),
            Text = call.ArgumentsJson
        };
        Grid.SetColumn(name, 1);
        Grid.SetColumn(args, 2);
        grid.Children.Add(icon);
        grid.Children.Add(name);
        grid.Children.Add(args);
        return grid;
    }

    private Grid BuildResultRow(ToolCallRecord call)
    {
        var grid = new Grid { Style = (Style)Host.FindResource("ToolRow") };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var check = new System.Windows.Shapes.Path { Style = (Style)Host.FindResource("ToolDoneIcon") };
        var name = new TextBlock
        {
            Style = (Style)Host.FindResource("ToolName"),
            Text = call.Name
        };
        var preview = new TextBlock
        {
            Style = (Style)Host.FindResource("ToolResult"),
            Text = string.IsNullOrWhiteSpace(call.ResultPreview) ? "готово" : call.ResultPreview
        };
        Grid.SetColumn(name, 1);
        Grid.SetColumn(preview, 2);
        grid.Children.Add(check);
        grid.Children.Add(name);
        grid.Children.Add(preview);
        return grid;
    }

    private Expander BuildNestedAgentExpander(ToolCallRecord call)
    {
        var agent = call.NestedAgent!;
        var running = agent.Status == AgentRunStatus.Running;
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock
        {
            Style = (Style)Host.FindResource("ExpanderHeaderText"),
            Text = string.IsNullOrWhiteSpace(agent.DisplayName)
                ? "Агент " + agent.ModelId
                : agent.DisplayName
        });

        var suffix = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x56, 0x56, 0x56)),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Text = running
                ? "· выполняется"
                : "· " + agent.ToolRounds.SelectMany(round => round.Calls).Count() + " инструментов"
        };
        if (running)
        {
            suffix.BeginAnimation(
                UIElement.OpacityProperty,
                new DoubleAnimation(1, 0.3, new Duration(TimeSpan.FromSeconds(0.7)))
                {
                    RepeatBehavior = RepeatBehavior.Forever,
                    AutoReverse = true
                });
        }

        header.Children.Add(suffix);

        var inner = new StackPanel();
        foreach (var round in agent.ToolRounds)
        {
            foreach (var nested in round.Calls)
            {
                inner.Children.Add(BuildCallRow(nested));
                if (nested.Status is ToolCallStatus.Done or ToolCallStatus.Failed)
                {
                    inner.Children.Add(BuildResultRow(nested));
                }
            }

            if (!string.IsNullOrWhiteSpace(round.InfoLine) &&
                !round.InfoLine.Equals("Запускаю инструменты", StringComparison.Ordinal))
            {
                inner.Children.Add(BuildInfoRow(round.InfoLine));
            }
        }

        var wasExpanded = _nestedExpanded.GetValueOrDefault(call.Id);
        var expander = new Expander
        {
            Style = (Style)Host.FindResource("ToolsExpander"),
            Header = header,
            Content = inner,
            IsExpanded = wasExpanded,
            Margin = new Thickness(24, 3, 0, 5)
        };
        expander.Expanded += (_, _) => _nestedExpanded[call.Id] = true;
        expander.Collapsed += (_, _) => _nestedExpanded[call.Id] = false;
        return expander;
    }

    private UIElement BuildAgentGlyph(string? modelId, double size)
    {
        var key = VeniceModelCatalog.GetLogoResourceKey(modelId ?? "");
        if (key is not null && Host.TryFindResource(key) is ImageSource source)
        {
            var glyph = new Image { Source = source, Width = size, Height = size };
            ModelBrand.ApplyLogoBox(glyph, key);
            return new Border
            {
                Width = size,
                Height = size,
                Margin = new Thickness(1, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = glyph
            };
        }

        return new TextBlock
        {
            Style = (Style)Host.FindResource("ToolIcon"),
            Text = "⚙"
        };
    }

    private Grid BuildInfoRow(string text)
    {
        var grid = new Grid { Style = (Style)Host.FindResource("ToolRow") };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new TextBlock
        {
            Style = (Style)Host.FindResource("ToolIcon"),
            Text = "ℹ"
        };
        var label = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x6E)),
            FontStyle = FontStyles.Italic,
            VerticalAlignment = VerticalAlignment.Center,
            Text = text,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(icon);
        grid.Children.Add(label);
        return grid;
    }

    private void StartPulse()
    {
        if (_pulse is not null)
        {
            return;
        }

        _pulse = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true };
        var animation = new DoubleAnimation(1, 0.3, TimeSpan.FromSeconds(0.7));
        Storyboard.SetTarget(animation, Duration);
        Storyboard.SetTargetProperty(animation, new PropertyPath(UIElement.OpacityProperty));
        _pulse.Children.Add(animation);
        _pulse.Begin();
    }

    private void StopPulse()
    {
        _pulse?.Stop();
        _pulse = null;
        Duration.Opacity = 1;
    }
}

internal static class ChatMessageViews
{
    private static readonly Color UserForeground = Color.FromRgb(0xE8, 0xE8, 0xE8);
    private static readonly Color AiForeground = Color.FromRgb(0xDC, 0xDC, 0xDC);

    public static UserMessageView CreateUser(
        FrameworkElement host,
        ChatDisplayMessage message,
        MessageActions? actions = null)
    {
        var display = CreateReadOnlyBox(UserForeground, 13.5, 19, shrinkWrap: true);
        ChatMarkdown.Write(display, message.Text, markdown: false, UserForeground, 13.5, 19, fillAvailableWidth: false);
        FitUserBubble(display, message.Text, host);

        var editor = new TextBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(UserForeground),
            CaretBrush = new SolidColorBrush(UserForeground),
            FontSize = 13.5,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            Visibility = Visibility.Collapsed,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = UserBubbleInnerMax
        };

        var hostGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        hostGrid.Children.Add(display);
        hostGrid.Children.Add(editor);

        var bubble = new Border { Style = (Style)host.FindResource("UserBubble"), Child = hostGrid };

        var row = new StackPanel
        {
            Style = (Style)host.FindResource("ActionsRow"),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, -14, 0, 16)
        };
        var copy = IconAction(host, "Copy", "Копировать");
        copy.Click += (_, _) => actions?.Copy?.Invoke(message);
        var edit = IconAction(host, "Compose", "Изменить");
        row.Children.Add(copy);
        row.Children.Add(edit);

        var root = new StackPanel();
        root.Children.Add(bubble);
        root.Children.Add(row);
        var view = new UserMessageView(root)
        {
            Bubble = bubble,
            Display = display,
            Editor = editor
        };
        edit.Click += (_, _) =>
        {
            actions?.Edit?.Invoke(message);
            view.BeginEdit(message.Text);
        };
        editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                view.CancelEdit();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                e.Handled = true;
                var text = editor.Text.Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    view.CancelEdit();
                    return;
                }

                view.CancelEdit();
                actions?.CommitEdit?.Invoke(message, text);
            }
        };
        return view;
    }

    public static AssistantMessageView CreateAssistant(
        FrameworkElement host,
        ChatDisplayMessage message,
        MessageActions? actions = null)
    {
        var logoImage = new Image { Width = 20, Height = 20 };
        var logoLetter = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        var logoLightning = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M6,0 L1,8 L5,8 L4,14 L10,5 L6,5 Z"),
            Fill = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
            Width = 9,
            Height = 12,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        var logoBorder = new Border { Style = (Style)host.FindResource("AiLogoBorder") };
        var logoHost = new Grid();
        logoHost.Children.Add(logoImage);
        logoHost.Children.Add(logoLetter);
        logoHost.Children.Add(logoLightning);
        logoBorder.Child = logoHost;

        var modelName = new TextBlock
        {
            Style = (Style)host.FindResource("AiMetaText"),
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0)),
            Text = VeniceModelCatalog.GetDisplayName(message.ResolvedModelId ?? message.RequestedModelId ?? "")
        };
        var clock = new TextBlock
        {
            Style = (Style)host.FindResource("AiMetaText"),
            Text = ChatFormat.Clock(message.CreatedAt)
        };
        var duration = new TextBlock { Style = (Style)host.FindResource("AiMetaText") };
        var costDot = new Ellipse { Style = (Style)host.FindResource("AiMetaDot"), Visibility = Visibility.Collapsed };
        var cost = new TextBlock { Style = (Style)host.FindResource("AiMetaText"), Visibility = Visibility.Collapsed };

        var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(1, 4, 0, 9) };
        meta.Children.Add(modelName);
        meta.Children.Add(new Ellipse { Style = (Style)host.FindResource("AiMetaDot") });
        meta.Children.Add(clock);
        meta.Children.Add(new Ellipse { Style = (Style)host.FindResource("AiMetaDot") });
        meta.Children.Add(duration);
        meta.Children.Add(costDot);
        meta.Children.Add(cost);

        var body = CreateReadOnlyBox(AiForeground, 13.5, 21);
        var streaming = message.Status is AssistantStatus.Streaming;
        if (streaming && string.IsNullOrWhiteSpace(message.Text))
        {
            body.Visibility = Visibility.Collapsed;
        }
        else
        {
            ChatMarkdown.Write(
                body,
                message.Text,
                markdown: !streaming,
                AiForeground,
                13.5,
                21);
        }

        var cancel = IconAction(host, "Cancel", "Остановить");
        cancel.Click += (_, _) => actions?.Cancel?.Invoke(message);

        var row = new StackPanel
        {
            Style = (Style)host.FindResource("ActionsRow"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 10, 0, 0)
        };
        var regenerate = IconAction(host, "Regenerate", "Повторить");
        regenerate.Click += (_, _) => actions?.Regenerate?.Invoke(message);
        row.Children.Add(regenerate);
        var copy = IconAction(host, "Copy", "Копировать");
        copy.Click += (_, _) => actions?.Copy?.Invoke(message);
        row.Children.Add(copy);
        var share = IconAction(host, "Upload", "Поделиться");
        share.IsEnabled = false;
        row.Children.Add(share);
        var compress = IconAction(host, "Compress", "Сжать");
        compress.IsEnabled = false;
        row.Children.Add(compress);
        var expand = IconAction(host, "Expand", "Расширить");
        expand.IsEnabled = false;
        row.Children.Add(expand);
        var compose = IconAction(host, "Compose", "Изменить");
        compose.IsEnabled = false;
        row.Children.Add(compose);
        var delete = IconAction(host, "Delete", "Удалить");
        delete.Click += (_, _) => actions?.Delete?.Invoke(message);
        row.Children.Add(delete);
        row.Children.Add(cancel);

        var toolsHost = new StackPanel { Visibility = Visibility.Collapsed };
        var column = new StackPanel();
        column.Children.Add(meta);
        column.Children.Add(toolsHost);
        column.Children.Add(body);
        column.Children.Add(row);

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(logoBorder);
        Grid.SetColumn(column, 1);
        grid.Children.Add(column);

        var view = new AssistantMessageView(grid)
        {
            Body = body,
            ModelName = modelName,
            Clock = clock,
            Duration = duration,
            Cost = cost,
            CostDot = costDot,
            Actions = row,
            CancelButton = cancel,
            LogoImage = logoImage,
            LogoLetter = logoLetter,
            LogoLightning = logoLightning,
            RootElement = grid,
            ToolsHost = toolsHost,
            Host = host
        };
        view.ApplyBranding(host, message.ResolvedModelId ?? message.RequestedModelId ?? "");
        view.UpdateTools(message);
        if (message.Status is AssistantStatus.Streaming)
        {
            view.ShowWorking(message.Duration);
            view.ShowCancelOnly();
        }
        else
        {
            view.SetBody(message.Text, markdown: true);
            view.ShowFinished(message);
        }

        return view;
    }

    private const double UserBubbleInnerMax = 530;
    private const double UserBubbleWidthSlack = 12;

    private static RichTextBox CreateReadOnlyBox(
        Color foreground,
        double fontSize,
        double lineHeight,
        bool shrinkWrap = false)
    {
        var box = new RichTextBox
        {
            IsReadOnly = true,
            IsUndoEnabled = false,
            IsReadOnlyCaretVisible = false,
            AcceptsReturn = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            MinHeight = 0,
            HorizontalAlignment = shrinkWrap ? HorizontalAlignment.Left : HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            FontSize = fontSize,
            Foreground = new SolidColorBrush(foreground),
            CaretBrush = Brushes.Transparent,
            SelectionBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            FocusVisualStyle = null
        };
        box.Document.PagePadding = new Thickness(0);
        box.Document.LineHeight = lineHeight;
        box.Document.PageHeight = double.NaN;
        if (!shrinkWrap)
        {
            box.SizeChanged += (_, _) =>
            {
                if (box.ActualWidth > 1)
                {
                    box.Document.PageWidth = box.ActualWidth;
                }
            };
        }

        return box;
    }

    private static void FitUserBubble(RichTextBox box, string text, FrameworkElement dpiHost)
    {
        var dip = 1.0;
        try
        {
            dip = VisualTreeHelper.GetDpi(dpiHost).PixelsPerDip;
        }
        catch
        {
            // design-time / not yet attached
        }

        var width = MeasureWrappedWidth(text, 13.5, 19, UserBubbleInnerMax, Math.Max(1.0, dip));
        box.Width = width;
        box.Document.PageWidth = width;
    }

    internal static double MeasureWrappedWidth(
        string text,
        double fontSize,
        double lineHeight,
        double maxWidth,
        double pixelsPerDip)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 12;
        }

        var formatted = new FormattedText(
            text.Replace("\r\n", "\n", StringComparison.Ordinal),
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            fontSize,
            Brushes.White,
            pixelsPerDip)
        {
            LineHeight = lineHeight
        };

        var natural = formatted.WidthIncludingTrailingWhitespace;
        if (natural <= 0)
        {
            natural = formatted.Width;
        }

        if (natural + UserBubbleWidthSlack <= maxWidth)
        {
            return Math.Max(8, Math.Ceiling(natural + UserBubbleWidthSlack));
        }

        formatted.MaxTextWidth = maxWidth;
        var wrapped = formatted.WidthIncludingTrailingWhitespace;
        if (wrapped <= 0)
        {
            wrapped = formatted.Width;
        }

        return Math.Clamp(Math.Ceiling(wrapped + UserBubbleWidthSlack), 8, maxWidth);
    }

    private static Button IconAction(FrameworkElement host, string resourceKey, string? tooltip = null)
    {
        var button = new Button
        {
            Style = (Style)host.FindResource("MsgActionButton"),
            ToolTip = tooltip
        };
        var image = new Image
        {
            Stretch = Stretch.Uniform,
            Width = double.NaN,
            Height = double.NaN,
            Margin = resourceKey switch
            {
                "Compose" => new Thickness(2),
                "Regenerate" or "Upload" or "Cancel" => new Thickness(4),
                "Copy" => new Thickness(5),
                _ => new Thickness(6)
            }
        };
        if (host.TryFindResource(resourceKey) is ImageSource source)
        {
            image.Source = source;
        }

        button.Content = image;
        return button;
    }
}
