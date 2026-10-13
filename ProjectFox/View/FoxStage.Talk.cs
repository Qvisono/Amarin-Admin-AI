using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Amarin.Core;

namespace Amarin.Fox;

/// <summary>Разговор: пузырёк с полем над Розой; ответ приходит от его модели обычной репликой.</summary>
/// <remarks>
/// Поле — обычный редактируемый <see cref="TextBox"/>, и окно уступает ему набранное, как любому
/// полю с текстом: иначе разговор с Розой утёк бы в поле сообщения, то есть в чат с моделью.
/// Стили — общие <c>DialogField</c>/<c>DialogInput</c> окна, по ключу: сборке Розы своих не нужно.
/// </remarks>
internal sealed partial class FoxStage
{
    /// <summary>Сколько знаков можно сказать Розе за раз.</summary>
    private const int TalkLimit = 300;

    private Border? _talk;
    private TextBox? _talkInput;

    /// <summary>Поле разговора открыто и в фокусе: Esc закрывает его, а не останавливает ответ.</summary>
    private bool Talking => _talkInput?.IsKeyboardFocusWithin == true;

    /// <summary>«Поговорить» из меню: пузырёк с полем над головой.</summary>
    public void OpenTalk()
    {
        if (_mode is not (FoxMode.Idle or FoxMode.Sleep or FoxMode.Peek))
        {
            return;
        }

        if (_mode == FoxMode.Sleep)
        {
            Wake();
        }

        if (_mode == FoxMode.Peek)
        {
            EndPeek();
        }

        if (_talk is null)
        {
            _talkInput = new TextBox { MaxLength = TalkLimit, Width = 230, Tag = Loc.Get("S.Fox.TalkHint") };
            _talkInput.SetResourceReference(FrameworkElement.StyleProperty, "DialogInput");
            _talkInput.PreviewKeyDown += OnTalkKey;
            _talkInput.LostKeyboardFocus += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(_talkInput.Text))
                {
                    CloseTalk();
                }
            };
            _talk = new Border { Child = _talkInput };
            _talk.SetResourceReference(FrameworkElement.StyleProperty, "DialogField");
            _host.Layer.Children.Add(_talk);
        }

        _talkInput!.Text = "";
        _talk.Visibility = Visibility.Visible;
        if (_bubble is not null)
        {
            _bubble.Visibility = Visibility.Collapsed;
        }

        PlaceTalk();
        _talk.UpdateLayout();
        _talkInput.Focus();
        Keyboard.Focus(_talkInput);
        Play("tail", "sit");
        _face = "curious";
        _faceUntil = _clock().AddSeconds(2);
        Draw();
    }

    private void OnTalkKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                CloseTalk();
                break;
            case Key.Enter when Keyboard.Modifiers == ModifierKeys.None:
                e.Handled = true;
                var text = _talkInput!.Text.Trim();
                CloseTalk();
                if (text.Length > 0)
                {
                    Signal(new FoxSignal(FoxSignalKind.Talk, Text: text));
                }

                break;
        }
    }

    /// <summary>Закрывает поле разговора; фокус возвращает окно, как после любого своего слоя.</summary>
    private void CloseTalk()
    {
        if (_talk is null || _talk.Visibility != Visibility.Visible)
        {
            return;
        }

        _talk.Visibility = Visibility.Collapsed;
        _host.TalkClosed();
    }

    private void PlaceTalk()
    {
        if (_talk is null || _talk.Visibility != Visibility.Visible)
        {
            return;
        }

        _talk.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _talk.DesiredSize;
        var left = Math.Clamp(_move.X + (_sprite.Side / 2) - (size.Width / 2), 8, Math.Max(8, _host.Width() - size.Width - 8));
        var top = Math.Max(34, _move.Y - size.Height - 6);
        Canvas.SetLeft(_talk, Math.Round(left));
        Canvas.SetTop(_talk, Math.Round(top));
    }

    /// <summary>Ждёт ответа своей модели: думает, над головой — многоточие.</summary>
    private void Ponder()
    {
        if (_mode != FoxMode.Idle)
        {
            return;
        }

        Play("think", "sit");
        _reactUntil = DateTime.MaxValue;
        _face = "thinking";
        _faceUntil = _clock().AddSeconds(3);
        Burst("dots", 1);
    }
}
