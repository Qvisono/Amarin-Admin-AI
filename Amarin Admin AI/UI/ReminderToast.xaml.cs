using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Amarin.UI;

/// <summary>Какая карточка: напоминание или задача, оборвавшаяся посреди прогона.</summary>
internal enum ReminderLook
{
    Reminder,
    Interrupted
}

/// <summary>На сколько отложить напоминание.</summary>
internal enum ReminderSnooze
{
    None,
    TenMinutes,
    OneHour,
    TomorrowMorning
}

/// <summary>Что человек сделал с карточкой.</summary>
internal enum ReminderAction
{
    /// <summary>«Готово» или крестик у напоминания.</summary>
    Done,
    Snooze,
    OpenChat,
    Retry,
    Cancel,

    /// <summary>Крестик у прерванной задачи: решить можно и потом, на вкладке «Отложенные».</summary>
    Dismiss,

    /// <summary>Щелчок по карточке со скрытым текстом: показать окно, чтобы войти.</summary>
    Open
}

/// <summary>Одна карточка стопки: что на ней написано и какие у неё кнопки.</summary>
internal sealed class ReminderItem(string taskId, ReminderLook look) : INotifyPropertyChanged
{
    private string _heading = "";
    private string _body = "";
    private string _meta = "";
    private string _more = "";
    private bool _snoozing;
    private bool _hidden;
    private bool _hasChat;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string TaskId { get; } = taskId;

    public ReminderLook Look { get; } = look;

    public string Heading { get => _heading; set => Set(ref _heading, value); }

    public string Body { get => _body; set => Set(ref _body, value); }

    /// <summary>Срок, опоздание, пропущенные — одной строкой; пусто — строки нет.</summary>
    public string Meta { get => _meta; set => Set(ref _meta, value); }

    /// <summary>«ещё N» у верхней карточки, когда на экран помещаются не все.</summary>
    public string More { get => _more; set => Set(ref _more, value); }

    /// <summary>«Отложить» нажато: вместо кнопок — на сколько.</summary>
    public bool Snoozing { get => _snoozing; set => Set(ref _snoozing, value); }

    /// <summary>Программа заблокирована паролем: текст и кнопки ждут входа.</summary>
    public bool Hidden { get => _hidden; set => Set(ref _hidden, value); }

    /// <summary>Есть чат задачи — кнопка «Открыть чат».</summary>
    public bool HasChat { get => _hasChat; set => Set(ref _hasChat, value); }

    /// <summary>
    /// Насколько срок опоздал: его называет только срабатывание, а перезаполнять карточку
    /// (блокировка, повторный срок) приходится и без него.
    /// </summary>
    public TimeSpan? Late { get; set; }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// Карточки напоминаний в правом нижнем углу — стопкой в одном окне поверх всех и без активации.
/// </summary>
/// <remarks>
/// <para>
/// Облик — карточки «ответ готов» (<see cref="NotificationToast"/>), но напоминание не гаснет само:
/// оно для того, чтобы его не пропустили, и ждёт «Готово» или «Отложить». Поэтому оно и не идёт
/// через <c>Notify</c>: у системного уведомления трея кнопок нет.
/// </para>
/// <para>
/// Окно одно на всю стопку, а не по окну на карточку: у каждого окна свои поля под тень, и
/// соседние карточки перекрывали бы друг друга прозрачными краями — щелчок по верху нижней
/// карточки уходил бы в тень верхней.
/// </para>
/// </remarks>
public partial class ReminderToast : Window
{
    private Rect? _workArea;
    private double _bottomInset;

    /// <summary>Нажатие на карточке: что сделано и, у «Отложить», на сколько.</summary>
    internal event Action<ReminderItem, ReminderAction, ReminderSnooze>? Acted;

    internal ObservableCollection<ReminderItem> Items { get; } = [];

    public ReminderToast()
    {
        InitializeComponent();
        Cards.ItemsSource = Items;

        // SizeToContent доводит размер за несколько проходов; карточка, уходящая из стопки,
        // меняет высоту каждый кадр. Окно держится за правый нижний угол на каждом из них.
        SizeChanged += (_, _) => Place();
        ContentRendered += (_, _) => Place();
    }

    /// <summary>Создаёт пустое окно стопки для монитора главного окна.</summary>
    internal static ReminderToast Create(int uiScalePercent, IntPtr ownerHandle)
    {
        var toast = new ReminderToast { _workArea = ToastScreen.WorkAreaOf(ownerHandle) };
        var factor = Math.Clamp(uiScalePercent, 50, 300) / 100.0;
        if (Math.Abs(factor - 1.0) > 0.001)
        {
            toast.Cards.LayoutTransform = new ScaleTransform(factor, factor);
        }

        return toast;
    }

    /// <summary>
    /// Сколько места снизу занято карточкой «ответ готов»: стопка встаёт над ней, а не поверх.
    /// </summary>
    internal double BottomInset
    {
        get => _bottomInset;
        set
        {
            if (Math.Abs(_bottomInset - value) < 0.5)
            {
                return;
            }

            _bottomInset = value;
            Place();
        }
    }

    /// <summary>Убирает карточку: она сворачивается, и стопка над ней опускается.</summary>
    internal void Remove(ReminderItem item, Action done)
    {
        if (Cards.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement container && IsVisible)
        {
            UiMotion.Shrink(container, 0, () =>
            {
                Items.Remove(item);
                done();
            });
            return;
        }

        Items.Remove(item);
        done();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ToastScreen.MakeQuiet(new WindowInteropHelper(this).Handle);

        // Первый кадр — сразу в углу, а не в 0,0: точное место приедет с раскладкой.
        var area = ToastScreen.InDips(this, _workArea);
        Left = area.Right - EstimatedWidth;
        Top = area.Bottom - _bottomInset - EstimatedHeight;
    }

    // Карточка 340 и поля по 30 под тень; высота — по тексту.
    private const double EstimatedWidth = 400;
    private const double EstimatedHeight = 190;

    private void Place()
    {
        var width = ActualWidth >= 1 ? ActualWidth : DesiredSize.Width;
        var height = ActualHeight >= 1 ? ActualHeight : DesiredSize.Height;
        if (width < 1 || height < 1)
        {
            return;
        }

        var area = ToastScreen.InDips(this, _workArea);
        Left = area.Right - width;
        Top = area.Bottom - _bottomInset - height;
    }

    private void Card_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is UIElement card)
        {
            UiMotion.Enter(card, dy: 18, milliseconds: 220);
        }
    }

    private static ReminderItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as ReminderItem;

    private void Raise(object sender, ReminderAction action, ReminderSnooze snooze = ReminderSnooze.None)
    {
        if (ItemOf(sender) is { } item)
        {
            Acted?.Invoke(item, action, snooze);
        }
    }

    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Щелчок по кнопке сюда тоже всплывает, но кнопка его уже забрала.
        if (!e.Handled && ItemOf(sender) is { Hidden: true } item)
        {
            Acted?.Invoke(item, ReminderAction.Open, ReminderSnooze.None);
        }
    }

    private void Ack_Click(object sender, RoutedEventArgs e) => Raise(sender, ReminderAction.Done);

    private void Snooze_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
        {
            item.Snoozing = true;
        }
    }

    private void SnoozeFor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ReminderSnooze snooze })
        {
            Raise(sender, ReminderAction.Snooze, snooze);
        }
    }

    private void OpenChat_Click(object sender, RoutedEventArgs e) => Raise(sender, ReminderAction.OpenChat);

    private void Retry_Click(object sender, RoutedEventArgs e) => Raise(sender, ReminderAction.Retry);

    private void CancelTask_Click(object sender, RoutedEventArgs e) => Raise(sender, ReminderAction.Cancel);

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
        {
            Acted?.Invoke(item, item.Look == ReminderLook.Reminder && !item.Hidden ? ReminderAction.Done : ReminderAction.Dismiss, ReminderSnooze.None);
        }
    }
}
