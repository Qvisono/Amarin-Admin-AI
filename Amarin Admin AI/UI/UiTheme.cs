using Amarin.Tools;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Amarin.UI;

internal static class UiTheme
{
    public const string IconSuccess = "✓";
    public const string IconError = "✗";
    public const string IconWarning = "⚠";
    public const string IconInfo = "ℹ";
    public const string IconTool = "⚙";
    public const string IconSearch = "🔍";
    public const string IconImage = "📷";
    public const string IconUndo = "↩";
    public const string IconAsk = "❓";
    public const string IconDanger = "⚡";

    public static readonly Color Primary = Color.Cyan1;
    public static readonly Color Muted = Color.Grey;
    public static readonly Color Border = Color.Grey23;
    public static readonly Color Warning = Color.Yellow3;
    public static readonly Color Error = Color.Red3;
    public static readonly Color Success = Color.Green3;
    public static readonly Color AskUser = Color.MediumPurple1;
    public static readonly Color Accent = Color.DeepSkyBlue1;

    public static readonly Style PrimaryStyle = new(Primary);
    public static readonly Style MutedStyle = new(Muted);
    public static readonly Style WarningStyle = new(Warning);
    public static readonly Style ErrorStyle = new(Error);
    public static readonly Style SuccessStyle = new(Success);

    public static Panel CreatePanel(
        string header,
        IRenderable content,
        Color borderColor,
        Color? headerColor = null)
    {
        return new Panel(content)
        {
            Header = new PanelHeader($"  {header}  "),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(borderColor),
            Padding = new Padding(2, 1)
        };
    }

    /// <summary>
    /// Neutral impact scale for confirmation UI — informational, not alarming.
    /// </summary>
    public static string ImpactBadge(DangerousRiskLevel level) => level switch
    {
        DangerousRiskLevel.Low => "[green]●[/] [dim]незначительное[/]",
        DangerousRiskLevel.Medium => "[yellow]●●[/] [yellow]умеренное[/]",
        DangerousRiskLevel.High => "[orange1]●●●[/] [orange1]существенное[/]",
        DangerousRiskLevel.Critical => "[red]●●●●[/] [red]серьёзное[/]",
        _ => "[grey]●[/] [dim]не оценено[/]"
    };
}