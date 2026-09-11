using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

    /// <summary>Copy a share code for the dialog up to and including this message.</summary>
    public Action<ChatDisplayMessage>? Share;

    /// <summary>Write the dialog up to this message out as plain JSON.</summary>
    public Action<ChatDisplayMessage>? Export;

    /// <summary>False hides both of the above (Settings → Data Controls).</summary>
    public Func<bool>? SharingEnabled;

    /// <summary>Raised by the "add to allowlist" chip under a download blocked by the domain list.</summary>
    public Action<string>? AddDownloadDomain;
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
    public required TextBlock Thinking { get; init; }
    public required Ellipse ThinkingDot { get; init; }
    public required TextBlock Cost { get; init; }
    public required Ellipse CostDot { get; init; }
    public required Border CostChip { get; init; }
    public required StackPanel Actions { get; init; }
    public required Button CancelButton { get; init; }
    public required Image LogoImage { get; init; }
    public required TextBlock LogoLetter { get; init; }
    public required System.Windows.Shapes.Path LogoLightning { get; init; }
    public required FrameworkElement RootElement { get; init; }
    public required StackPanel ToolsHost { get; init; }
    public required FrameworkElement Host { get; init; }

    /// <summary>Callbacks owned by the window; used by the blocked-download chip.</summary>
    public MessageActions? Callbacks { get; set; }

    private Storyboard? _pulse;
    private Expander? _toolsExpander;
    private readonly Dictionary<string, bool> _nestedExpanded = new();

    public AssistantMessageView(FrameworkElement root) => Root = root;

    public void ApplyBranding(FrameworkElement host, string modelId)
    {
        ModelName.Text = VeniceModelCatalog.GetDisplayName(modelId);
        ModelBrand.Apply(host, modelId, LogoImage, LogoLetter, LogoLightning);
    }

    /// <param name="streaming">
    /// Ответ ещё печатается. Разметка применяется всегда — троттлинг живого рендера живёт
    /// в окне; здесь флаг решает только, прятать ли пока пустое тело.
    /// </param>
    public void SetBody(string text, bool streaming)
    {
        var empty = string.IsNullOrWhiteSpace(text);
        Body.Visibility = empty && streaming
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (empty)
        {
            return;
        }

        ChatMarkdown.Write(Body, Host, text, fontSize: 13.5, lineHeight: 21);
    }

    public void ShowWorking(TimeSpan elapsed)
    {
        Duration.Text = ChatFormat.Working(elapsed);
        Duration.FontWeight = FontWeights.SemiBold;
        Duration.SetResourceReference(TextBlock.ForegroundProperty, "Text.Muted");
        Cost.Visibility = Visibility.Collapsed;
        CostDot.Visibility = Visibility.Collapsed;
        Thinking.Visibility = Visibility.Collapsed;
        ThinkingDot.Visibility = Visibility.Collapsed;
        StartPulse();
    }

    /// <summary>
    /// Below this the figure is noise: the model started answering as soon as the connection
    /// was open, and "думал 0s" beside the duration says nothing.
    /// </summary>
    private static readonly TimeSpan ThinkingWorthShowing = TimeSpan.FromSeconds(1);

    public void ShowFinished(ChatDisplayMessage message)
    {
        StopPulse();
        Duration.Opacity = 1;
        Duration.FontWeight = FontWeights.Normal;
        Duration.SetResourceReference(TextBlock.ForegroundProperty, "Text.Dim");
        Duration.Text = ChatFormat.Duration(message.Duration);

        if (message.ThinkingDuration >= ThinkingWorthShowing)
        {
            Thinking.Text = Loc.Format("S.Message.Thought", ChatFormat.Duration(message.ThinkingDuration));
            Thinking.Visibility = Visibility.Visible;
            ThinkingDot.Visibility = Visibility.Visible;
        }
        else
        {
            Thinking.Visibility = Visibility.Collapsed;
            ThinkingDot.Visibility = Visibility.Collapsed;
        }

        var cost = ChatFormat.Cost(message.Cost);
        if (string.IsNullOrEmpty(cost))
        {
            Cost.Visibility = Visibility.Collapsed;
            CostDot.Visibility = Visibility.Collapsed;
            CostChip.ToolTip = null;
        }
        else
        {
            Cost.Text = cost;
            Cost.Visibility = Visibility.Visible;
            CostDot.Visibility = Visibility.Visible;

            // Rebuilt from the message itself, so a chat reopened from disk gets the same
            // breakdown as one that just finished — every part of it is persisted.
            CostChip.ToolTip = CostBreakdownTooltip.Build(Host, message);
        }

        Body.Visibility = Visibility.Visible;
        Actions.Margin = new Thickness(0, 10, 0, 0);
        CancelButton.Visibility = Visibility.Collapsed;
        foreach (UIElement child in Actions.Children)
        {
            if (!ReferenceEquals(child, CancelButton) && !ChatMessageViews.IsPermanentlyHidden(child))
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
        // Заблокированный домен — единственное, что здесь требует действия пользователя,
        // а внутри свёрнутого списка инструментов подсказку с кнопкой попросту не видно.
        _toolsExpander.IsExpanded = wasExpanded || HasBlockedDomain(message);
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
            Text = Loc.Get(running ? "S.Tools.Running" : "S.Tools.Done")
        });

        if (!running && calls.Count > 0)
        {
            var count = new TextBlock
            {
                Text = "· " + calls.Count,
                FontSize = 12,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            count.SetResourceReference(TextBlock.ForegroundProperty, "Text.Faint");
            header.Children.Add(count);
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
                    if (BuildBlockedDomainNotice(call) is { } notice)
                    {
                        body.Children.Add(notice);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(round.InfoLine) &&
                !round.InfoLine.Equals(Loc.Get("S.Tools.Running"), StringComparison.Ordinal))
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
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var check = new System.Windows.Shapes.Path { Style = (Style)Host.FindResource("ToolDoneIcon") };
        var name = new TextBlock
        {
            Style = (Style)Host.FindResource("ToolName"),
            Text = call.Name
        };
        var preview = new TextBlock
        {
            Style = (Style)Host.FindResource("ToolResult"),
            Text = string.IsNullOrWhiteSpace(call.ResultPreview)
                ? Loc.Get("S.Tools.CallDone")
                : call.ResultPreview
        };
        Grid.SetColumn(name, 1);
        Grid.SetColumn(preview, 2);
        grid.Children.Add(check);
        grid.Children.Add(name);
        grid.Children.Add(preview);

        // Инструменты вроде generate_image стоят заметно дороже самого разговора — без ценника
        // прямо здесь непонятно, откуда в шапке сообщения взялась вся сумма.
        if (ChatFormat.Cost(call.Cost) is { Length: > 0 } price)
        {
            var cost = new TextBlock
            {
                Style = (Style)Host.FindResource("ToolResult"),
                Text = price,
                Margin = new Thickness(8, 0, 0, 0),
                ToolTip = Loc.Format("S.Tools.CallCost", call.Name)
            };
            Grid.SetColumn(cost, 3);
            grid.Children.Add(cost);
        }

        return grid;
    }

    private static bool HasBlockedDomain(ChatDisplayMessage message) =>
        message.ToolRounds
            .SelectMany(round => round.Calls)
            .Any(call =>
                !call.Success &&
                call.Name.Equals("download_file", StringComparison.OrdinalIgnoreCase) &&
                call.ResultPreview.StartsWith(DomainList.BlockedMarker, StringComparison.Ordinal));

    /// <summary>
    /// Offer to allow the host when <c>download_file</c> was refused by the allowlist.
    /// Returns null for every other tool result.
    /// </summary>
    private Border? BuildBlockedDomainNotice(ToolCallRecord call)
    {
        if (call.Success ||
            !call.Name.Equals("download_file", StringComparison.OrdinalIgnoreCase) ||
            !call.ResultPreview.StartsWith(DomainList.BlockedMarker, StringComparison.Ordinal))
        {
            return null;
        }

        if (!DomainList.TryGetHostFromToolArguments(call.ArgumentsJson, out var host))
        {
            // Fall back to the host embedded in the marker: "DOMAIN_BLOCKED: example.com ...".
            var rest = call.ResultPreview[DomainList.BlockedMarker.Length..].TrimStart();
            host = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            if (string.IsNullOrWhiteSpace(host))
            {
                return null;
            }
        }

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = (Brush)Host.FindResource("Status.Warning"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        row.Children.Add(new TextBlock
        {
            Text = Loc.Format("S.Tools.DomainBlocked", host),
            FontSize = 12,
            Foreground = (Brush)Host.FindResource("Text.Secondary"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        });

        var allow = new Button
        {
            Style = (Style)Host.FindResource("InlineLinkButton"),
            Content = Loc.Get("S.Tools.AddToAllowlist")
        };
        allow.Click += (_, _) => Callbacks?.AddDownloadDomain?.Invoke(host);
        row.Children.Add(allow);

        return new Border
        {
            Style = (Style)Host.FindResource("BlockedDomainNotice"),
            Child = row
        };
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
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            // Точку-разделитель собираем здесь: она вёрстка, а не текст для перевода.
            Text = "· " + (running
                ? Loc.Get("S.Tools.AgentRunning")
                : Loc.Format(
                    "S.Tools.AgentToolsCount",
                    agent.ToolRounds.SelectMany(round => round.Calls).Count()))
        };
        suffix.SetResourceReference(TextBlock.ForegroundProperty, "Text.Faint");
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
                !round.InfoLine.Equals(Loc.Get("S.Tools.Running"), StringComparison.Ordinal))
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
            FontStyle = FontStyles.Italic,
            VerticalAlignment = VerticalAlignment.Center,
            Text = text,
            TextWrapping = TextWrapping.Wrap
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Text.Faint");
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
    // Ключи палитры, а не Color: тело сообщения обязано перекрашиваться вместе с темой.
    private const string UserForeground = "Text.Body";
    private const string AiForeground = "Text.Secondary";

    public static UserMessageView CreateUser(
        FrameworkElement host,
        ChatDisplayMessage message,
        MessageActions? actions = null)
    {
        // Блочная разметка не влезает в обжатый по тексту пузырь — такие сообщения
        // растягиваем до максимума, как ответы ассистента.
        var wide = ChatMarkdown.HasBlockConstructs(message.Text);
        var display = CreateReadOnlyBox(UserForeground, 13.5, 19, shrinkWrap: !wide);
        ChatMarkdown.Write(
            display, host, message.Text, 13.5, 19, fillAvailableWidth: wide);
        if (wide)
        {
            display.MaxWidth = UserBubbleInnerMax;
        }
        else
        {
            FitUserBubble(display, ChatMarkdown.FlattenInline(message.Text), host);
        }

        var editor = new TextBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontSize = 13.5,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            Visibility = Visibility.Collapsed,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = UserBubbleInnerMax
        };
        editor.SetResourceReference(Control.ForegroundProperty, UserForeground);
        editor.SetResourceReference(TextBoxBase.CaretBrushProperty, UserForeground);

        var hostGrid = new Grid
        {
            HorizontalAlignment = wide ? HorizontalAlignment.Stretch : HorizontalAlignment.Left
        };
        hostGrid.Children.Add(display);
        hostGrid.Children.Add(editor);

        var bubble = new Border { Style = (Style)host.FindResource("UserBubble"), Child = hostGrid };

        var row = new StackPanel
        {
            Style = (Style)host.FindResource("ActionsRow"),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, -14, 0, 16)
        };
        var copy = IconAction(host, "Copy", Loc.Get("S.Common.Copy"));
        copy.Click += (_, _) => actions?.Copy?.Invoke(message);
        var edit = IconAction(host, "Compose", Loc.Get("S.Common.Edit"));
        row.Children.Add(copy);
        row.Children.Add(edit);

        // Left as a stretched panel: UserBubble right-aligns itself and relies on its own margin.
        var root = new StackPanel();
        if (message.Images.Count > 0)
        {
            root.Children.Add(CreateImageStrip(host, message));
        }

        if (message.Files.Count > 0)
        {
            root.Children.Add(CreateFileStrip(message));
        }

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
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        var logoLightning = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M6,0 L1,8 L5,8 L4,14 L10,5 L6,5 Z"),
            Width = 9,
            Height = 12,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };

        // These two are the fallbacks drawn when a model has no logo image, and they sit on
        // AiLogoBorder — whose background is Bg.Card, so it follows the theme. A fixed grey
        // would be a light mark on a light chip.
        logoLetter.SetResourceReference(TextBlock.ForegroundProperty, "Text.Muted");
        logoLightning.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Text.Muted");
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
            Text = VeniceModelCatalog.GetDisplayName(message.ResolvedModelId ?? message.RequestedModelId ?? "")
        };

        // A step brighter than the rest of the meta row (AiMetaText is Text.Dim) so the model
        // stands out — but through the palette, not a fixed grey: a hardcoded one is invisible
        // on the light themes.
        modelName.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
        var clock = new TextBlock
        {
            Style = (Style)host.FindResource("AiMetaText"),
            Text = ChatFormat.Clock(message.CreatedAt)
        };
        var duration = new TextBlock { Style = (Style)host.FindResource("AiMetaText") };
        var thinkingDot = new Ellipse
        {
            Style = (Style)host.FindResource("AiMetaDot"),
            Visibility = Visibility.Collapsed
        };
        var thinking = new TextBlock
        {
            Style = (Style)host.FindResource("AiMetaText"),
            Visibility = Visibility.Collapsed
        };
        var costDot = new Ellipse { Style = (Style)host.FindResource("AiMetaDot"), Visibility = Visibility.Collapsed };
        var cost = new TextBlock { Style = (Style)host.FindResource("AiMetaText"), Visibility = Visibility.Collapsed };

        // The price and its dot ride in a transparent border so the whole chip is one hover
        // target: a bare TextBlock only answers the mouse over the glyphs themselves, and the
        // breakdown would flicker as the pointer crossed a gap between digits.
        var costChip = new Border { Background = Brushes.Transparent };
        ToolTipService.SetInitialShowDelay(costChip, 150);
        ToolTipService.SetShowDuration(costChip, 20000);
        ToolTipService.SetVerticalOffset(costChip, 4);
        var costRow = new StackPanel { Orientation = Orientation.Horizontal };
        costRow.Children.Add(costDot);
        costRow.Children.Add(cost);
        costChip.Child = costRow;

        var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(1, 4, 0, 9) };
        meta.Children.Add(modelName);
        meta.Children.Add(new Ellipse { Style = (Style)host.FindResource("AiMetaDot") });
        meta.Children.Add(clock);
        meta.Children.Add(new Ellipse { Style = (Style)host.FindResource("AiMetaDot") });
        meta.Children.Add(duration);
        meta.Children.Add(thinkingDot);
        meta.Children.Add(thinking);
        meta.Children.Add(costChip);

        var body = CreateReadOnlyBox(AiForeground, 13.5, 21);
        var streaming = message.Status is AssistantStatus.Streaming;
        if (streaming && string.IsNullOrWhiteSpace(message.Text))
        {
            body.Visibility = Visibility.Collapsed;
        }
        else
        {
            ChatMarkdown.Write(body, host, message.Text, 13.5, 21);
        }

        var cancel = IconAction(host, "Cancel", Loc.Get("S.Message.Stop"));
        cancel.Click += (_, _) => actions?.Cancel?.Invoke(message);

        var row = new StackPanel
        {
            Style = (Style)host.FindResource("ActionsRow"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 10, 0, 0)
        };
        var regenerate = IconAction(host, "Regenerate", Loc.Get("S.Message.Regenerate"));
        regenerate.Click += (_, _) => actions?.Regenerate?.Invoke(message);
        row.Children.Add(regenerate);
        var copy = IconAction(host, "Copy", Loc.Get("S.Common.Copy"));
        copy.Click += (_, _) => actions?.Copy?.Invoke(message);
        row.Children.Add(copy);
        var sharingOn = actions?.SharingEnabled?.Invoke() ?? true;
        var share = IconAction(host, "Upload", Loc.Get("S.Message.Share"));
        share.Click += (_, _) => actions?.Share?.Invoke(message);
        HideIf(share, !sharingOn);
        row.Children.Add(share);
        var export = IconAction(host, "ExportJson", Loc.Get("S.Message.Export"));
        export.Click += (_, _) => actions?.Export?.Invoke(message);
        HideIf(export, !sharingOn);
        row.Children.Add(export);
        var compress = IconAction(host, "Compress", Loc.Get("S.Message.Compress"));
        compress.IsEnabled = false;
        row.Children.Add(compress);
        var expand = IconAction(host, "Expand", Loc.Get("S.Message.Expand"));
        expand.IsEnabled = false;
        row.Children.Add(expand);
        var compose = IconAction(host, "Compose", Loc.Get("S.Common.Edit"));
        compose.IsEnabled = false;
        row.Children.Add(compose);
        var delete = IconAction(host, "Delete", Loc.Get("S.Common.Delete"));
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
            Thinking = thinking,
            ThinkingDot = thinkingDot,
            Cost = cost,
            CostDot = costDot,
            CostChip = costChip,
            Actions = row,
            CancelButton = cancel,
            LogoImage = logoImage,
            LogoLetter = logoLetter,
            LogoLightning = logoLightning,
            RootElement = grid,
            ToolsHost = toolsHost,
            Host = host,
            Callbacks = actions
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
            view.SetBody(message.Text, streaming: false);
            view.ShowFinished(message);
        }

        return view;
    }

    private const double UserBubbleInnerMax = 530;
    private const double UserBubbleWidthSlack = 12;

    private static RichTextBox CreateReadOnlyBox(
        string foregroundKey,
        double fontSize,
        double lineHeight,
        bool shrinkWrap = false)
    {
        var box = new RichTextBox
        {
            // Без IsDocumentEnabled ни ссылки, ни кнопка «копировать» внутри
            // BlockUIContainer не получают мышь в режиме только для чтения.
            IsDocumentEnabled = true,
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
            CaretBrush = Brushes.Transparent,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            FocusVisualStyle = null
        };
        box.SetResourceReference(Control.ForegroundProperty, foregroundKey);
        box.SetResourceReference(TextBoxBase.SelectionBrushProperty, "Bg.Elevated");
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

    /// <summary>
    /// Thumbnails of the images sent with a user message, shown above the bubble so they are
    /// still there after the chat is reloaded from disk.
    /// </summary>
    private static FrameworkElement CreateImageStrip(FrameworkElement host, ChatDisplayMessage message)
    {
        var strip = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 0, 6)
        };

        for (var index = 0; index < message.Images.Count; index++)
        {
            var attachment = message.Images[index];
            var position = index;
            var frame = new Border
            {
                Width = 96,
                Height = 96,
                Margin = new Thickness(6, 0, 0, 6),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = attachment.Label
            };
            frame.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                ImageViewerHost.Open(host, message.Images, position);
            };
            frame.SetResourceReference(Border.BorderBrushProperty, "Border.Default");
            frame.SetResourceReference(Border.BackgroundProperty, "Bg.Card");
            RoundedClip.SetRadius(frame, 8);

            if (MainWindow.TryDecode(attachment, decodePixelWidth: 192) is { } source)
            {
                frame.Child = new Image { Source = source, Stretch = Stretch.UniformToFill };
            }

            strip.Children.Add(frame);
        }

        return strip;
    }

    /// <summary>
    /// Карточки документов, отправленных с сообщением. Показываются над пузырём — так видно,
    /// что именно ушло модели, даже после перезагрузки чата с диска.
    /// </summary>
    private static FrameworkElement CreateFileStrip(ChatDisplayMessage message)
    {
        var strip = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 0, 6)
        };

        foreach (var file in message.Files)
        {
            var frame = new Border
            {
                MaxWidth = 220,
                Margin = new Thickness(6, 0, 0, 6),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 7, 12, 7),
                ToolTip = $"{file.FileName} — {AttachmentTypes.FormatSize(file.SizeBytes)}"
            };
            frame.SetResourceReference(Border.BorderBrushProperty, "Border.Default");
            frame.SetResourceReference(Border.BackgroundProperty, "Bg.Card");
            RoundedClip.SetRadius(frame, 8);

            var rows = new StackPanel();

            var kind = new TextBlock
            {
                Text = MainWindow.FileBadge(file.FileName),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold
            };
            kind.SetResourceReference(TextBlock.ForegroundProperty, "Accent.Fill");

            var name = new TextBlock
            {
                Text = file.FileName,
                FontSize = 11.5,
                Margin = new Thickness(0, 1, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Text.Body");

            var size = new TextBlock
            {
                Text = AttachmentTypes.FormatSize(file.SizeBytes),
                FontSize = 10.5,
                Margin = new Thickness(0, 1, 0, 0)
            };
            size.SetResourceReference(TextBlock.ForegroundProperty, "Text.Faint");

            rows.Children.Add(kind);
            rows.Children.Add(name);
            rows.Children.Add(size);
            frame.Child = rows;
            strip.Children.Add(frame);
        }

        return strip;
    }

    /// <summary>
    /// Marker for buttons that must stay hidden. <see cref="AssistantMessageView.ShowFinished"/>
    /// re-shows the whole action row, so a plain Collapsed would be undone on the next status change.
    /// </summary>
    private const string HiddenTag = "amarin.hidden";

    internal static bool IsPermanentlyHidden(UIElement element) =>
        element is FrameworkElement { Tag: HiddenTag };

    private static void HideIf(FrameworkElement element, bool hidden)
    {
        if (!hidden)
        {
            return;
        }

        element.Tag = HiddenTag;
        element.Visibility = Visibility.Collapsed;
    }

    internal static Button IconAction(FrameworkElement host, string resourceKey, string? tooltip = null)
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
                "Regenerate" or "Upload" or "Cancel" or "ExportJson" => new Thickness(4),
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
