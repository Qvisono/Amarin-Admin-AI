using System.Windows;
using System.Windows.Controls;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Подстраница «Лисёнок Роза» (1.35.0). Как у страниц настроек: правда в
/// <see cref="FoxSettings"/>, контролы только отражают её и пишут изменения под <c>_loading</c>.
/// </summary>
/// <remarks>
/// Сам Роза здесь не нужен: кнопки зовут окно, а то — Розу, если он пришёл, или его файлы, если
/// выключен. Типов сборки Розы в этом классе нет — страница «Модели» строится в простое после
/// первого кадра, и тянуть с ней лисёнка незачем.
/// </remarks>
public partial class FoxSettingsBlock : UserControl
{
    private MainWindow? _window;
    private AppServices? _services;
    private bool _loading;

    public FoxSettingsBlock() => InitializeComponent();

    /// <summary>Что-то поменялось — строка-ссылка на странице перечитывает своё значение.</summary>
    internal event Action? Changed;

    internal void Load(MainWindow window, AppServices services)
    {
        _window = window;
        _services = services;
        _loading = true;
        try
        {
            var fox = services.Settings.Fox;
            ShowToggle.IsChecked = fox.Enabled;
            SizeSmall.IsChecked = fox.Size == FoxSettings.SmallSize;
            SizeMedium.IsChecked = fox.Size == FoxSettings.MediumSize;
            SizeLarge.IsChecked = fox.Size == FoxSettings.LargeSize;
            MischiefToggle.IsChecked = fox.Mischief;
            ReadChatToggle.IsChecked = fox.ReadChat;
            SearchToggle.IsChecked = fox.WebSearch;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Значение строки-ссылки: показан Роза или выключен.</summary>
    internal static string Summary(AppSettings settings) =>
        Loc.Get(settings.Fox.Enabled ? "S.FoxSettings.On" : "S.FoxSettings.Off");

    private void Save(Action<FoxSettings> change)
    {
        if (_loading || _services is null || _window is null)
        {
            return;
        }

        change(_services.Settings.Fox);
        _services.SettingsStore.Save(_services.Settings);
        _window.ApplyFoxSettings();
        Changed?.Invoke();
    }

    private void Toggle_Changed(object sender, RoutedEventArgs e)
    {
        var on = (sender as CheckBox)?.IsChecked == true;
        if (ReferenceEquals(sender, ShowToggle))
        {
            Save(fox => fox.Enabled = on);
        }
        else if (ReferenceEquals(sender, MischiefToggle))
        {
            Save(fox => fox.Mischief = on);
        }
        else if (ReferenceEquals(sender, ReadChatToggle))
        {
            Save(fox => fox.ReadChat = on);
        }
        else if (ReferenceEquals(sender, SearchToggle))
        {
            Save(fox => fox.WebSearch = on);
        }
    }

    private void Size_Checked(object sender, RoutedEventArgs e) =>
        Save(fox => fox.Size =
            ReferenceEquals(sender, SizeLarge) ? FoxSettings.LargeSize
            : ReferenceEquals(sender, SizeMedium) ? FoxSettings.MediumSize
            : FoxSettings.SmallSize);

    private void FriendshipButton_Click(object sender, RoutedEventArgs e) =>
        Detached.Run(_window?.ShowFoxFriendshipAsync() ?? Task.CompletedTask, "fox_friendship");

    private void DiaryButton_Click(object sender, RoutedEventArgs e) =>
        Detached.Run(_window?.ShowFoxDiaryAsync() ?? Task.CompletedTask, "fox_diary");

    private void CallButton_Click(object sender, RoutedEventArgs e) => _window?.Fox?.Unhide();

    private void ResetButton_Click(object sender, RoutedEventArgs e) =>
        Detached.Run(_window?.ResetFoxAsync() ?? Task.CompletedTask, "fox_reset");
}
