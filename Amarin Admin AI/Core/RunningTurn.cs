using System.Collections.Concurrent;

namespace Amarin.Core;

/// <summary>Откуда взялся ход. Нужно там, где обращение к нему зависит от происхождения.</summary>
internal enum TurnKind
{
    /// <summary>Обычная отправка из композера.</summary>
    Send,

    /// <summary>Команда <c>/agent</c>.</summary>
    AgentCommand,

    /// <summary>Регенерация или продолжение после правки сообщения.</summary>
    Continue,

    /// <summary>«Create infographic» по видео.</summary>
    Infographic
}

/// <summary>
/// Один идущий ход чата: чья сессия, чем его отменить и что уже нарисовано.
/// </summary>
/// <remarks>
/// Прежде всё это лежало полями главного окна — по одному набору на программу, — и потому ход
/// был ровно один: переключение чата его убивало. <see cref="Session"/> здесь обязана быть тем
/// же объектом, с которым работает движок: если открыть чат заново с диска, получится копия, и
/// ход продолжит писать в «сироту», пока на экране показывают другой объект.
/// </remarks>
internal sealed class RunningTurn
{
    /// <summary>
    /// What the user typed while this turn was still working. The engine drains it on a round
    /// boundary, which is why it is a queue and not a single slot — three quick lines in a row
    /// must reach the model in the order they were written.
    /// </summary>
    /// <remarks>
    /// Concurrent because the composer fills it on the UI thread while the engine empties it on
    /// whichever thread the tool round finished on.
    /// </remarks>
    private readonly ConcurrentQueue<string> _queued = new();

    public required ChatSession Session { get; init; }

    public required CancellationTokenSource Cancellation { get; init; }

    public required TurnKind Kind { get; init; }

    /// <summary>
    /// Когда ход начался. Часы «думает N с» считаются отсюда, а не от момента возврата в чат:
    /// иначе при каждом переключении они обнулялись бы.
    /// </summary>
    public required DateTime StartedAt { get; init; }

    public string SessionId => Session.Id;

    /// <summary>Живое сообщение ассистента. Появляется в момент старта ответа.</summary>
    public ChatDisplayMessage? Assistant { get; set; }

    /// <summary>Его идентификатор — по нему вьюшка находится заново при возврате в чат.</summary>
    public string? AssistantId { get; set; }

    /// <summary>
    /// Накопленный текст ответа и то, что из него уже нарисовано. На ходе, а не на окне:
    /// у двух ходов буферы перемешались бы.
    /// </summary>
    public string PendingText { get; set; } = "";

    public string RenderedText { get; set; } = "";

    public bool Finished { get; set; }

    public bool HasQueued => !_queued.IsEmpty;

    public void Enqueue(string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            _queued.Enqueue(text.Trim());
        }
    }

    public bool TryTakeQueued(out string text) => _queued.TryDequeue(out text!);
}
