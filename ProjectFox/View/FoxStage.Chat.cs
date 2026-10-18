using System.Windows;
using System.Windows.Controls;

namespace Amarin.Fox;

/// <summary>Переписка: маленькая панель у Розы над головой, привязанная к нему (<see cref="FoxChat"/>).</summary>
/// <remarks>
/// Панель — часть слоя Розы, а не окно поверх программы: ни затемнения, ни блокировки чата. Пока
/// она открыта, Роза никуда сам не уходит (иначе панель ездила бы под рукой человека), а реплики
/// над головой встают над ней. Ответ на сказанное в переписке показывается только в ней.
/// </remarks>
internal sealed partial class FoxStage
{
    private FoxChat? _chat;

    /// <summary>Переписка открыта.</summary>
    private bool Chatting => _chat is not null;

    /// <summary>Открыта ли переписка — тестам и окну.</summary>
    internal FoxChat? Chat => _chat;

    /// <summary>«Переписка» из меню: панель над головой, курсор — сразу в её поле.</summary>
    public void OpenChat()
    {
        if (_chat is not null)
        {
            _chat.FocusInput();
            return;
        }

        if (!StopToListen() || RefusesTalk())
        {
            return;
        }

        CloseTalk();
        _chat = new FoxChat(_store.Snapshot().Conversation, Talk, CloseChat);

        // Панель растёт с каждым сообщением: переставляется сразу, а не на следующем кадре, иначе
        // до него наезжала бы на Розу.
        _chat.SizeChanged += (_, _) =>
        {
            PlaceChat();
            PlaceBubble();
        };
        _host.Layer.Children.Add(_chat);
        Draw();

        // Фокус — только разложенному полю: до раскладки он молча не встаёт, и Esc тогда панели
        // не доставался (как у поля разговора, см. OpenTalk).
        _chat.UpdateLayout();
        _chat.FocusInput();
    }

    /// <summary>Закрывает переписку; фокус возвращает окно, как после поля разговора.</summary>
    public void CloseChat()
    {
        if (_chat is null)
        {
            return;
        }

        _host.Layer.Children.Remove(_chat);
        _chat = null;
        _host.TalkClosed();
        Draw();
    }

    /// <summary>
    /// Ответ своей модели на разговор. <c>true</c> — его ждала переписка и показала у себя: над
    /// головой реплику повторять не нужно.
    /// </summary>
    public bool ChatAnswered(FoxReply reply) => _chat?.Answered(reply) == true;

    /// <summary>
    /// Над головой, внутри окна и ниже заголовка; если сверху места нет — под ним. Убежавшему и
    /// спрятавшемуся переписка ни к чему: закрывается.
    /// </summary>
    private void PlaceChat()
    {
        if (_chat is null)
        {
            return;
        }

        if (_mode is FoxMode.Off or FoxMode.Away or FoxMode.Hidden)
        {
            _host.Layer.Children.Remove(_chat);
            _chat = null;
            return;
        }

        _chat.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _chat.DesiredSize;
        var left = Math.Clamp(_move.X + (_sprite.Side / 2) - (size.Width / 2), 8, Math.Max(8, _host.Width() - size.Width - 8));
        var above = _move.Y - size.Height - 6;
        var below = Math.Min(_move.Y + _sprite.Side + 6, Math.Max(34, _host.Layer.ActualHeight - size.Height - 8));
        Canvas.SetLeft(_chat, Math.Round(left));
        Canvas.SetTop(_chat, Math.Round(above >= 34 ? above : below));
    }

    /// <summary>Верх переписки, если она открыта над головой: пузырь реплики встаёт над ней.</summary>
    private double? ChatTop() =>
        _chat is not null && Canvas.GetTop(_chat) is var top && !double.IsNaN(top) && top < _move.Y ? top : null;
}
