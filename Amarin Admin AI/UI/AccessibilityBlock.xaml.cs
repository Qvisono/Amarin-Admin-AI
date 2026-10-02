using System.Globalization;
using System.Windows.Controls;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Размер текста ленты и шрифт кода на странице оформления (I3).
/// </summary>
/// <remarks>
/// Правда — в <see cref="AppSettings"/>; после правки окно применяет её сразу через
/// <see cref="Changed"/>: лента пересобирается без сброса лупы.
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
            var size = ChatFonts.Clamp(settings.ChatFontSize);
            SizeCombo.SelectedItem = SizeCombo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => double.Parse((string)item.Tag, CultureInfo.InvariantCulture) == size);

            var mono = settings.CodeFont ?? ChatFonts.DefaultMono;
            MonoCombo.SelectedItem = MonoCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == mono);
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

    private void SizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SizeCombo.SelectedItem is ComboBoxItem { Tag: string tag })
        {
            var size = double.Parse(tag, CultureInfo.InvariantCulture);

            // Заводской размер — пустое поле: так файл прежних версий и новый выглядят одинаково.
            Save(settings => settings.ChatFontSize = size == ChatFonts.DefaultSize ? null : size);
        }
    }

    private void MonoCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MonoCombo.SelectedItem is ComboBoxItem { Tag: string tag })
        {
            Save(settings => settings.CodeFont = tag == ChatFonts.DefaultMono ? null : tag);
        }
    }
}
