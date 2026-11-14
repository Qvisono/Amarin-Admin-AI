using System.Globalization;
using System.Windows;
using Amarin.Core;

namespace Amarin.UI;

public partial class SettingsKeyPage
{
    private void RefreshPolzaRate(IReadOnlyList<ApiKeyEntry> entries)
    {
        PolzaRatePanel.Visibility = entries.Any(key => key.Provider == LlmProvider.Polza)
            ? Visibility.Visible : Visibility.Collapsed;
        PolzaRateBox.Text = _services?.Settings.RublesPerUsd?.ToString("0.############################", CultureInfo.InvariantCulture) ?? "";
        PolzaRateError.Visibility = Visibility.Collapsed;
    }

    private void PolzaRateBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_services is not { } services)
        {
            return;
        }

        var text = PolzaRateBox.Text.Trim();
        decimal? rate = null;
        if (text.Length > 0)
        {
            if (!decimal.TryParse(text.Replace(',', '.'), NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out var parsed) || parsed <= 0m)
            {
                PolzaRateError.Visibility = Visibility.Visible;
                return;
            }

            rate = parsed;
        }

        PolzaRateError.Visibility = Visibility.Collapsed;
        if (services.Settings.RublesPerUsd == rate)
        {
            return;
        }

        services.Settings.RublesPerUsd = rate;
        services.SettingsStore.Save(services.Settings);
        services.Models.Invalidate(LlmProvider.Polza);
        services.Balances.Invalidate(services.KeyStore.List(), LlmProvider.Polza);
        if (IsLoaded)
        {
            Detached.Run(ReloadAsync(force: false), "polza_rate");
            RefreshKeyRows();
        }
    }

    private bool HasPolzaRate => _services?.Settings.RublesPerUsd is > 0m;

    private static string FormatProviderUsd(decimal usd, LlmProvider provider) =>
        (provider == LlmProvider.Polza ? "≈" : "") + FormatUsd(usd);
}
