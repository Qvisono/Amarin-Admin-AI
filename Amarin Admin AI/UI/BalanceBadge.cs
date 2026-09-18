using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shape = System.Windows.Shapes;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// The remaining Venice balance, shown beside the send button. Venice reports it on the headers of
/// every request, so the plate is a lagging figure by nature: it says what was left after the last
/// answer, which is exactly the moment the user wants to see it.
/// </summary>
/// <remarks>
/// <para>
/// Colours are mixed here rather than declared in the palettes. The plate has three states and
/// there are eighteen palettes; adding fifty-four tokens to carry a tint that is mechanically
/// derived from one brush would be a lot of hand-edited XAML to keep in step. Reading the source
/// brush and thinning it also means a new palette needs no work at all.
/// </para>
/// <para>
/// The plate keeps one colour whatever the figure says: it is a readout, not an alarm, and a
/// number that turns red as it falls makes the composer flash for something the user already
/// knows. The warning lives in the tooltip instead, where it can say what is actually at risk.
/// </para>
/// </remarks>
internal sealed class BalanceBadge
{
    /// <summary>Below this the plate turns amber: a couple of drawn pictures and it is gone.</summary>
    private const decimal LowUsd = 1.00m;

    /// <summary>Below this it turns red — the next image generation may not go through.</summary>
    private const decimal CriticalUsd = 0.25m;

    private readonly Border _plate;
    private readonly Shape.Path _coin;
    private readonly TextBlock _amount;
    private readonly BalanceStore? _store;

    private VeniceBalance? _balance;

    public BalanceBadge(Border plate, Shape.Path coin, TextBlock amount, BalanceStore? store = null)
    {
        _plate = plate;
        _coin = coin;
        _amount = amount;
        _store = store;
        _balance = store?.Load();

        // The palette lives in swapped dictionaries, so the mixed brushes have to be rebuilt when
        // the theme changes -- a DynamicResource would have handled it, but these are derived.
        ThemeManager.EffectiveThemeChanged += Render;
        Render();
    }

    /// <summary>
    /// Called after every turn. A null balance leaves whatever was last known on screen: Venice
    /// omits the headers on some responses, and blanking the plate on those would make it flicker.
    /// </summary>
    public void Show(VeniceBalance? balance)
    {
        if (balance is null || (balance.Usd is null && balance.Diem is null))
        {
            return;
        }

        // Only a changed figure is worth a disk write; the plate is refreshed after every turn,
        // including the ones that spent nothing.
        if (_balance is null || _balance.Usd != balance.Usd || _balance.Diem != balance.Diem)
        {
            _store?.Save(balance);
        }

        _balance = balance;
        Render();
    }

    private void Render()
    {
        _plate.Visibility = Visibility.Visible;

        Paint();

        if (_balance is not { } balance)
        {
            // Before the first answer of the very first run there is no figure anywhere. A dash
            // is honest; "$0.00" would read as "you are out of money".
            _amount.Text = FormatUsd(null);
            _plate.ToolTip = Loc.Get("S.Balance.Unknown");
            return;
        }

        _amount.Text = FormatUsd(balance.Usd);
        _plate.ToolTip = BuildTooltip(balance);
    }

    private void Paint()
    {
        var accent = _plate.TryFindResource("Text.Muted") as Brush ?? Frozen(Colors.Gray);
        _amount.Foreground = accent;
        _coin.Fill = accent;

        // A wash of the same hue rather than a palette surface: the plate reads as one object,
        // and it keeps its meaning on both the near-black and the near-white themes.
        var colour = accent is SolidColorBrush { Color: var value } ? value : Colors.Gray;
        _plate.Background = Frozen(Color.FromArgb(0x24, colour.R, colour.G, colour.B));
        _plate.BorderBrush = Frozen(Color.FromArgb(0x40, colour.R, colour.G, colour.B));
    }

    /// <summary>
    /// Money, so cents are always shown — "$18,4" reads like a broken number rather than a
    /// balance. Only past a thousand do they get dropped, where the plate would otherwise crowd
    /// the send button and the cents stopped mattering anyway.
    /// </summary>
    internal static string FormatUsd(decimal? usd) =>
        usd is null
            ? "-"
            : "$" + usd.Value.ToString(usd.Value < 1000m ? "0.00" : "#,0", CultureInfo.InvariantCulture)
                .Replace(",", " ")
                .Replace('.', ',');

    private static string BuildTooltip(VeniceBalance balance)
    {
        // Переносы строк собираются здесь, а не внутри самих подписей: вёрстка подсказки —
        // не то, что переводчик обязан беречь, да и XAML обрезал бы ведущий перенос.
        var text = Loc.Get("S.Balance.Title") + " " + balance.Format();
        if (balance.Usd is { } usd && usd < LowUsd)
        {
            text += "\n" + (usd < CriticalUsd
                ? Loc.Get("S.Balance.AlmostOut")
                : Loc.Get("S.Balance.Low"));
        }

        return text + "\n" + Loc.Get("S.Balance.Refresh");
    }

    private static SolidColorBrush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }
}
