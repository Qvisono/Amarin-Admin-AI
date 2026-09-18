using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Amarin.Core;
// Почему псевдоним нужен — в csproj, рядом с убранными неявными using WPF.
using Path = System.Windows.Shapes.Path;

namespace Amarin.UI;

public sealed class ReasoningChoiceChangedEventArgs : EventArgs
{
    public required bool DisableThinking { get; init; }

    public required string? Effort { get; init; }
}

public partial class ReasoningPicker : UserControl
{
    public static readonly DependencyProperty LabelTextProperty =
        DependencyProperty.Register(
            nameof(LabelText),
            typeof(string),
            typeof(ReasoningPicker),
            new PropertyMetadata("Без размышления"));

    public static readonly DependencyProperty ShowGaugeIconProperty =
        DependencyProperty.Register(
            nameof(ShowGaugeIcon),
            typeof(bool),
            typeof(ReasoningPicker),
            new PropertyMetadata(false, OnShowGaugeIconChanged));

    private readonly string _chipGroup = "ReasoningEffort_" + Guid.NewGuid().ToString("N");
    private IReadOnlyList<VeniceModelInfo> _catalog = [];
    private string _modelId = "";
    private bool _disableThinking = true;
    private string? _effort;
    private bool _autoMode;
    private bool _withTools = true;
    private bool _suppress;

    public ReasoningPicker()
    {
        InitializeComponent();
        PopupManager.Register(PickerPopup, OpenButton);
    }

    public string LabelText
    {
        get => (string)GetValue(LabelTextProperty);
        set => SetValue(LabelTextProperty, value);
    }

    public bool ShowGaugeIcon
    {
        get => (bool)GetValue(ShowGaugeIconProperty);
        set => SetValue(ShowGaugeIconProperty, value);
    }

    private static void OnShowGaugeIconChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ReasoningPicker picker)
        {
            picker.Refresh(raise: false);
        }
    }

    public bool DisableThinking => _disableThinking;

    public string? Effort => _effort;

    public event EventHandler<ReasoningChoiceChangedEventArgs>? ChoiceChanged;

    public void SetChoice(bool disableThinking, string? effort)
    {
        _disableThinking = disableThinking;
        _effort = string.IsNullOrWhiteSpace(effort) ? null : effort.Trim();
        Refresh(raise: false);
    }

    public void SetModel(string modelId)
    {
        _modelId = modelId ?? "";
        Refresh(raise: false);
    }

    public void SetCatalog(IReadOnlyList<VeniceModelInfo> models)
    {
        _catalog = models;
        Refresh(raise: false);
    }

    /// <summary>Ставит всё состояние разом и перестраивает пикер один раз.</summary>
    /// <remarks>
    /// Каждый отдельный сеттер перестраивает пикер целиком — с раскрытием шаблона кнопки,
    /// пересчётом спидометра и списка сил. Вызывали их подряд по два-три: открытие чата и
    /// страница настроек с девятью пикерами платили за лишние перестройки каждый раз.
    /// </remarks>
    public void SetState(string? modelId, bool autoMode, bool disableThinking, string? effort)
    {
        _modelId = modelId ?? "";
        _autoMode = autoMode;
        _disableThinking = disableThinking;
        _effort = string.IsNullOrWhiteSpace(effort) ? null : effort.Trim();
        Refresh(raise: false);
    }

    /// <summary>
    /// Chat and agent slots always send function tools. Title/router do not.
    /// Effort chips that 400 next to tools are hidden when this is true.
    /// </summary>
    public void SetUsesTools(bool usesTools)
    {
        _withTools = usesTools;
        Refresh(raise: false);
    }

    private VeniceModelInfo? CurrentModel() => ReasoningPolicy.Find(_catalog, _modelId);

    private void ThinkingToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppress)
        {
            return;
        }

        // Тумблер положительный, поле хранит отрицание — инверсия ровно здесь, в одном месте.
        _disableThinking = ThinkingToggle.IsChecked != true;
        Raise();
        Refresh(raise: false);
    }

    private void EffortChip_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppress || sender is not RadioButton { Tag: string effort })
        {
            return;
        }

        _effort = effort;
        if (_disableThinking)
        {
            _disableThinking = false;
        }

        Raise();
        Refresh(raise: false);
    }

    private void Raise()
    {
        ChoiceChanged?.Invoke(this, new ReasoningChoiceChangedEventArgs
        {
            DisableThinking = _disableThinking,
            Effort = _effort
        });
    }

    private void Refresh(bool raise)
    {
        _suppress = true;
        try
        {
            var model = CurrentModel();
            var choice = new ReasoningChoice(_disableThinking, _effort);
            LabelText = ReasoningPolicy.ButtonText(choice, model, _autoMode, _withTools);
            OpenButton?.ApplyTemplate();
            if (OpenButton?.Template.FindName("Label", OpenButton) is TextBlock buttonLabel)
            {
                buttonLabel.Text = LabelText;
            }

            UpdateGauge(choice, model);

            if (ThinkingToggle is not null)
            {
                ThinkingToggle.IsChecked = !_disableThinking;
            }

            if (ThinkingHint is not null)
            {
                ThinkingHint.Text = Loc.Get(_disableThinking
                    ? "S.Reasoning.ToggleOff"
                    : "S.Reasoning.ToggleOn");
            }

            var options = _autoMode ? [] : ReasoningPolicy.VisibleEffortOptions(model, _withTools);
            var showEffort = options.Count > 0;
            if (DisableRow is not null)
            {
                DisableRow.Visibility = _autoMode ? Visibility.Collapsed : Visibility.Visible;
            }

            if (ThinkingHint is not null)
            {
                ThinkingHint.Visibility = _autoMode ? Visibility.Collapsed : Visibility.Visible;
            }

            if (AutoHint is not null)
            {
                AutoHint.Visibility = _autoMode ? Visibility.Visible : Visibility.Collapsed;
            }

            if (OpenButton is not null)
            {
                OpenButton.IsEnabled = !_autoMode;
                OpenButton.IsHitTestVisible = !_autoMode;
                OpenButton.Cursor = _autoMode ? Cursors.Arrow : Cursors.Hand;
                if (_autoMode)
                {
                    OpenButton.IsChecked = false;
                }
            }

            if (EffortCaption is not null)
            {
                EffortCaption.Visibility = showEffort ? Visibility.Visible : Visibility.Collapsed;
            }

            RebuildChips(options, model);
        }
        finally
        {
            _suppress = false;
        }

        if (raise)
        {
            Raise();
        }
    }

    private void RebuildChips(IReadOnlyList<string> options, VeniceModelInfo? model)
    {
        if (EffortHost is null)
        {
            return;
        }

        EffortHost.Children.Clear();
        if (options.Count == 0)
        {
            return;
        }

        var selected = ReasoningPolicy.ClampEffort(_effort, model, _withTools);
        foreach (var option in options)
        {
            var chip = new RadioButton
            {
                Style = (Style)FindResource("EffortChip"),
                GroupName = _chipGroup,
                Content = ReasoningPolicy.EffortLabel(option),
                Tag = option,
                IsChecked = selected is not null &&
                            option.Equals(selected, StringComparison.OrdinalIgnoreCase),
                IsEnabled = !_disableThinking
            };
            chip.Checked += EffortChip_Checked;
            EffortHost.Children.Add(chip);
        }
    }

    // ───────────────────────── Спидометр ─────────────────────────
    // Одни константы на разметку и на обе дуги: развёртка 220° вокруг точки (10,12)
    // в боксе 20×20. У прежнего рисунка стрелка ходила ±72° по полукругу, то есть
    // занимала треть шкалы и никогда не доходила до её краёв.
    private const double GaugeCenterX = 10;
    private const double GaugeCenterY = 12;
    private const double GaugeRadius = 6.5;
    private const double GaugeStartAngle = -110;
    private const double GaugeSweep = 220;

    private void UpdateGauge(ReasoningChoice choice, VeniceModelInfo? model)
    {
        if (OpenButton?.Template.FindName("GaugeIcon", OpenButton) is not FrameworkElement gauge)
        {
            return;
        }

        gauge.Visibility = ShowGaugeIcon ? Visibility.Visible : Visibility.Collapsed;
        if (!ShowGaugeIcon)
        {
            return;
        }

        var fraction = GaugeFraction(choice, model);
        var angle = GaugeStartAngle + (fraction * GaugeSweep);

        if (OpenButton.Template.FindName("GaugeTrack", OpenButton) is Path track)
        {
            track.Data = BuildArc(GaugeStartAngle, GaugeStartAngle + GaugeSweep);
        }

        if (OpenButton.Template.FindName("GaugeProgress", OpenButton) is Path progress)
        {
            // На нуле дуга вырождается в точку: у ArcSegment с совпадающими концами
            // поведение не определено, поэтому просто прячем её.
            var visible = !choice.DisableThinking && fraction > 0.001;
            progress.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible)
            {
                progress.Data = BuildArc(GaugeStartAngle, angle);
            }
        }

        if (OpenButton.Template.FindName("GaugeNeedleRotate", OpenButton) is RotateTransform rotate)
        {
            rotate.Angle = angle;
        }

        // Выключенное размышление — приглушённая стрелка: шкала на нуле и без цвета
        // читается как «не думает», а не как «думает на минимуме».
        var needleBrush = choice.DisableThinking ? "Text.Dim" : "Text.Secondary";
        if (OpenButton.Template.FindName("GaugeNeedle", OpenButton) is Path needle)
        {
            needle.SetResourceReference(Shape.StrokeProperty, needleBrush);
        }

        if (OpenButton.Template.FindName("GaugeHub", OpenButton) is Ellipse hub)
        {
            hub.SetResourceReference(Shape.FillProperty, needleBrush);
        }
    }

    /// <summary>Доля шкалы, которую занимает выбранный уровень: 0 — начало, 1 — упор.</summary>
    private double GaugeFraction(ReasoningChoice choice, VeniceModelInfo? model)
    {
        if (choice.DisableThinking)
        {
            return 0;
        }

        var effort = ReasoningPolicy.ClampEffort(choice.Effort, model, _withTools)
                     ?? choice.Effort;
        return (effort ?? "").Trim().ToLowerInvariant() switch
        {
            "none" => 0,
            "minimal" => 1d / 6,
            "low" => 2d / 6,
            "medium" => 3d / 6,
            "high" => 4d / 6,
            "xhigh" => 5d / 6,
            "max" => 1,
            _ => 3d / 6
        };
    }

    private static Geometry BuildArc(double fromAngle, double toAngle)
    {
        var figure = new PathFigure { StartPoint = PointOnDial(fromAngle), IsClosed = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = PointOnDial(toAngle),
            Size = new Size(GaugeRadius, GaugeRadius),
            IsLargeArc = Math.Abs(toAngle - fromAngle) > 180,
            SweepDirection = SweepDirection.Clockwise
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    /// <summary>Угол отсчитывается от «вверх» по часовой стрелке, как у настоящей шкалы.</summary>
    private static Point PointOnDial(double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180;
        return new Point(
            GaugeCenterX + (GaugeRadius * Math.Sin(radians)),
            GaugeCenterY - (GaugeRadius * Math.Cos(radians)));
    }
}
