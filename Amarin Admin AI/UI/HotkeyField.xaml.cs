using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Поле назначения сочетания клавиш: показывает текущее, по нажатию записывает новое.
/// </summary>
/// <remarks>
/// Отдельным контролом, а не строками в общей разметке: действий станет больше, а
/// <c>MainWindow.xaml</c> и без того на три тысячи строк.
/// <para>
/// Клавиши ловятся туннелем (<see cref="UIElement.PreviewKeyDown"/>) и гасятся: записываемое
/// сочетание не должно по дороге сработать. Без этого назначение <c>Ctrl+F</c> само же и
/// заводило бы новый чат прямо в момент записи.
/// </para>
/// </remarks>
public partial class HotkeyField : UserControl
{
    private string _gesture = "";
    private string _default = "";
    private bool _recording;

    public HotkeyField()
    {
        InitializeComponent();

        // Ушли с поля, не дописав сочетание, — запись отменяется: иначе следующее нажатие
        // где-нибудь в настройках попало бы сюда.
        LostKeyboardFocus += (_, _) => StopRecording();
    }

    /// <summary>Человек назначил новое сочетание или вернул заводское.</summary>
    public event EventHandler<string>? GestureChanged;

    /// <summary>Назначенное сейчас сочетание в каноническом виде.</summary>
    public string Gesture => _gesture;

    /// <summary>Ставит показываемое сочетание, не поднимая <see cref="GestureChanged"/>.</summary>
    public void SetGesture(string gesture, string defaultGesture)
    {
        _default = defaultGesture ?? "";
        _gesture = HotkeyMap.TryParse(gesture, out var normalized) ? normalized : _default;
        StopRecording();
    }

    private void Frame_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_recording)
        {
            StopRecording();
            return;
        }

        BeginRecording();
    }

    /// <summary>Переводит поле в режим записи. Отдельно от обработчика — ради тестов.</summary>
    /// <inheritdoc cref="Record"/>
    internal void BeginRecording()
    {
        _recording = true;
        Keyboard.Focus(this);
        Render();
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        StopRecording();
        if (string.Equals(_gesture, _default, StringComparison.Ordinal))
        {
            return;
        }

        _gesture = _default;
        Render();
        GestureChanged?.Invoke(this, _gesture);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_recording)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        // Alt приезжает под Key.System, а настоящая клавиша прячется в SystemKey.
        Record(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers);

        // Гасим всё, пока идёт запись: назначаемое сочетание не должно по дороге сработать.
        // Без этого назначение Ctrl+F само же и заводило бы новый чат в момент записи.
        e.Handled = true;
    }

    /// <summary>
    /// Принимает нажатие в режиме записи.
    /// </summary>
    /// <remarks>
    /// Отдельно от обработчика ради тестов: <c>Keyboard.Modifiers</c> в поднятом
    /// <c>RaiseEvent</c> событии не подделать, и нажатие с Ctrl через события не изобразить.
    /// Тем же приёмом отделён <c>ChatZoom.ZoomBy</c>.
    /// </remarks>
    internal void Record(Key key, ModifierKeys modifiers)
    {
        if (!_recording)
        {
            return;
        }

        if (key == Key.Escape)
        {
            StopRecording();
            return;
        }

        // Модификатор сам по себе — это ещё не сочетание, а его начало: ждём дальше, иначе
        // один Ctrl закрывал бы запись отказом.
        if (!Hotkeys.TryRecord(key, modifiers, out var recorded))
        {
            return;
        }

        _recording = false;
        if (string.Equals(recorded, _gesture, StringComparison.Ordinal))
        {
            Render();
            return;
        }

        _gesture = recorded;
        Render();
        GestureChanged?.Invoke(this, _gesture);
    }

    private void StopRecording()
    {
        _recording = false;
        Render();
    }

    private void Render()
    {
        GestureText.Text = _recording ? Loc.Get("S.Hotkeys.Press") : HotkeyMap.Display(_gesture);
        GestureText.SetResourceReference(
            TextBlock.ForegroundProperty, _recording ? "Text.Dim" : "Text.Body");
        Frame.SetResourceReference(
            Border.BorderBrushProperty, _recording ? "Accent.Fill" : "Border.Default");
        ResetButton.Visibility = !_recording && !string.Equals(_gesture, _default, StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
}
