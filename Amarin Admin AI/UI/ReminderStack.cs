using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Стопка карточек напоминаний: по одной на задачу, не больше трёх на экране, остальные ждут.
/// </summary>
/// <remarks>
/// <para>
/// Задача держит одну карточку: повторный срок неподтверждённого напоминания обновляет её
/// («пропущено: N»), а не ставит вторую. Карточки сверх трёх ждут очереди, и о них говорит
/// «ещё N» у верхней — стопка из десяти карточек закрыла бы пол-экрана.
/// </para>
/// <para>
/// Окно создаётся при первой карточке и закрывается с последней: программа без напоминаний
/// не держит лишнего окна поверх всех.
/// </para>
/// </remarks>
internal sealed class ReminderStack(Func<int> uiScalePercent, Func<IntPtr> ownerHandle)
{
    /// <summary>Сколько карточек на экране сразу.</summary>
    internal const int MaxVisible = 3;

    private readonly List<ReminderItem> _all = [];
    private ReminderToast? _window;
    private bool _windowShown;
    private double _bottomInset;

    /// <summary>Нажатие на карточке — то же, что поднимает окно стопки.</summary>
    public event Action<ReminderItem, ReminderAction, ReminderSnooze>? Acted;

    /// <summary>Все карточки, и показанные, и ждущие очереди.</summary>
    public IReadOnlyList<ReminderItem> Items => _all;

    /// <summary>Окно стопки, пока в нём есть карточки; для тестов и снимков.</summary>
    internal ReminderToast? Window => _window;

    /// <summary>
    /// Как показать окно стопки. Тесты подменяют на «не показывать»: окно поверх всех встало бы в
    /// угол настоящего экрана.
    /// </summary>
    internal Action<ReminderToast> ShowWindow { get; init; } = window => window.Show();

    public ReminderItem? Find(string taskId) => _all.FirstOrDefault(item => item.TaskId == taskId);

    /// <summary>Ставит карточку задачи или обновляет уже висящую.</summary>
    /// <param name="fill">Заполняет текст карточки; зовётся и для новой, и для висящей.</param>
    public ReminderItem Put(string taskId, ReminderLook look, Action<ReminderItem> fill)
    {
        if (Find(taskId) is { } existing && existing.Look == look)
        {
            fill(existing);
            existing.Snoozing = false;
            return existing;
        }

        Drop(taskId);
        var item = new ReminderItem(taskId, look);
        fill(item);
        _all.Add(item);
        Sync();
        return item;
    }

    /// <summary>Убирает карточку задачи, если она есть; её место занимает следующая из очереди.</summary>
    public void Remove(string taskId)
    {
        if (Find(taskId) is not { } item)
        {
            return;
        }

        _all.Remove(item);
        if (_window is { } window && window.Items.Contains(item))
        {
            window.Remove(item, Sync);
            return;
        }

        Sync();
    }

    /// <summary>Перезаполняет все карточки: снялась или встала блокировка, сменился язык.</summary>
    public void Refill(Action<ReminderItem> fill)
    {
        foreach (var item in _all)
        {
            fill(item);
        }
    }

    /// <summary>Убирает все карточки сразу — смена профиля: напоминания ушедшего не его дело.</summary>
    public void Clear()
    {
        _all.Clear();
        CloseWindow();
    }

    /// <summary>Сколько места внизу занимает карточка «ответ готов».</summary>
    public double BottomInset
    {
        get => _bottomInset;
        set
        {
            _bottomInset = value;
            if (_window is { } window)
            {
                window.BottomInset = value;
            }
        }
    }

    private void Drop(string taskId)
    {
        if (Find(taskId) is { } item)
        {
            _all.Remove(item);
            _window?.Items.Remove(item);
        }
    }

    /// <summary>Показывает первые <see cref="MaxVisible"/> карточек и пишет «ещё N» у верхней.</summary>
    private void Sync()
    {
        if (_all.Count == 0)
        {
            CloseWindow();
            return;
        }

        var window = EnsureWindow();
        var visible = _all.Take(MaxVisible).ToList();

        // Ушедшие сворачиваются сами (Remove); здесь — только добавить недостающие по порядку.
        foreach (var item in window.Items.Where(item => !visible.Contains(item)).ToList())
        {
            window.Items.Remove(item);
        }

        for (var i = 0; i < visible.Count; i++)
        {
            if (i >= window.Items.Count || !ReferenceEquals(window.Items[i], visible[i]))
            {
                window.Items.Remove(visible[i]);
                window.Items.Insert(Math.Min(i, window.Items.Count), visible[i]);
            }
        }

        var waiting = _all.Count - visible.Count;
        for (var i = 0; i < visible.Count; i++)
        {
            visible[i].More = i == 0 && waiting > 0 ? Loc.Format("S.Deferred.Toast.More", waiting) : "";
        }

        if (!_windowShown)
        {
            _windowShown = true;
            ShowWindow(window);
        }
    }

    private ReminderToast EnsureWindow()
    {
        if (_window is { } existing)
        {
            return existing;
        }

        var window = ReminderToast.Create(uiScalePercent(), ownerHandle());
        _windowShown = false;
        window.BottomInset = _bottomInset;
        window.Acted += (item, action, snooze) => Acted?.Invoke(item, action, snooze);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_window, window))
            {
                _window = null;
            }
        };
        _window = window;
        return window;
    }

    private void CloseWindow()
    {
        var window = _window;
        _window = null;
        window?.Close();
    }
}
