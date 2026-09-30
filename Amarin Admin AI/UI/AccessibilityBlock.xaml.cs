using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Блок «Специальные возможности» на странице оформления (I2, I3).
/// </summary>
/// <remarks>
/// Правда — в <see cref="AppSettings"/>; после правки окно применяет её сразу через
/// <see cref="Changed"/>: тема перекрашивается, лента пересобирается без сброса лупы.
/// </remarks>
public partial class AccessibilityBlock : UserControl
{
    private AppServices? _services;
    private bool _loading;

    public AccessibilityBlock() => InitializeComponent();

    /// <summary>Настройка сменилась — применить. Ставит окно.</summary>
    internal Action? Changed { get; set; }

    internal void Load(AppServices services)
    {
        _services = services;
        _loading = true;
        try
        {
            var settings = services.Settings;
            ContrastToggle.IsChecked = settings.FollowHighContrast;
            var size = ChatFonts.Clamp(settings.ChatFontSize);
            foreach (var chip in SizeChips.Children.OfType<RadioButton>())
            {
                chip.IsChecked = double.Parse((string)chip.Tag, CultureInfo.InvariantCulture) == size;
            }

            var mono = settings.CodeFont ?? ChatFonts.DefaultMono;
            foreach (var chip in MonoChips.Children.OfType<RadioButton>())
            {
                chip.IsChecked = (string)chip.Tag == mono;
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private void Save(Action<AppSettings> change)
    {
        if (_loading || _services is null)
        {
            return;
        }

        change(_services.Settings);
        _services.SettingsStore.Save(_services.Settings);
        Changed?.Invoke();
    }

    private void ContrastToggle_Changed(object sender, RoutedEventArgs e) =>
        Save(settings => settings.FollowHighContrast = ContrastToggle.IsChecked == true);

    private void Size_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag })
        {
            var size = double.Parse(tag, CultureInfo.InvariantCulture);

            // Заводской размер — пустое поле: так файл прежних версий и новый выглядят одинаково.
            Save(settings => settings.ChatFontSize = size == ChatFonts.DefaultSize ? null : size);
        }
    }

    private void Mono_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag })
        {
            Save(settings => settings.CodeFont = tag == ChatFonts.DefaultMono ? null : tag);
        }
    }
}
