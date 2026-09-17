namespace Amarin.Core;

/// <summary>
/// Состояние одного хода чата: какая модель у него сейчас, с каким размышлением он идёт и
/// сколько уже потратил.
/// </summary>
/// <remarks>
/// Раньше всё это лежало прямо в <see cref="VeniceClient"/> и <see cref="ChatEngine"/> —
/// по одному экземпляру на приложение. Пока ход был ровно один, это сходило с рук; с
/// несколькими одновременными чатами старт второго хода обнулял цену первого
/// (<c>ResetRequestCost</c>), а маршрутизатор «Авто» на секунду подменял активную модель всем
/// сразу. Теперь у каждого хода своя копия, а общий клиент состояния хода не держит.
/// </remarks>
internal sealed class VeniceTurnContext
{
    private readonly Lock _gate = new();
    private VeniceCost _total = VeniceCost.Zero;

    /// <summary>
    /// Модель, которую ход запросил. Это корень цепочки fallback, и он не меняется: без него
    /// перебор после отказа начинался бы заново с уже упавшей модели — лишний 503 на ровном месте.
    /// </summary>
    public required string RequestedModelId { get; init; }

    /// <summary>Модель, которая отвечает сейчас. Съезжает, если сработал fallback.</summary>
    public required string ModelId { get; set; }

    public ReasoningChoice Reasoning { get; set; } = ReasoningChoice.Disabled;

    /// <summary>Во что обошёлся маршрутизатор этого хода; null, когда «Авто» не выбрана.</summary>
    /// <remarks>
    /// На ходе, а не на сообщении: маршрутизатор работает один раз, а ответов ход может дать
    /// два (дописанное сообщение), и обоим в счёт уходит накопленный итог хода — то есть деньги
    /// маршрутизатора сидят в обоих. Показать строку только первому значило бы, что у второго
    /// они молча вернулись в «Модель» и строки перестали складываться в «Итого» под ними.
    /// </remarks>
    public VeniceCost? RouterCost { get; set; }

    /// <summary>
    /// Сколько ответ шёл до того, как его оборвали. Ноль у обычного хода.
    /// </summary>
    /// <remarks>
    /// Продолжение возобновляет прерванный ответ в том же сообщении, а секундомер у него свой,
    /// новый. Без этого слагаемого часы под ответом, простоявшим полторы минуты и продолженным,
    /// показали бы только последние секунды — как будто вся работа уложилась в них.
    /// </remarks>
    public TimeSpan ElapsedBefore { get; init; }

    /// <summary>Сколько ход потратил. Под замком: инструменты раунда идут параллельно.</summary>
    public VeniceCost Total
    {
        get
        {
            lock (_gate)
            {
                return _total;
            }
        }
    }

    public void Add(VeniceCost cost)
    {
        lock (_gate)
        {
            _total = _total.Add(cost);
        }
    }
}

/// <summary>
/// Ambient-контекст хода по образцу <see cref="AgentRunScope"/>.
/// </summary>
/// <remarks>
/// Именно ambient, а не параметр: платящие инструменты (<c>generate_image</c>, <c>web_search</c>,
/// <c>scrape_url</c>) собираются один раз при запуске замыканиями на общий клиент и о ходе ничего
/// не знают. Протащить в них накопитель означало бы переписать <c>ITool</c>.
/// <see cref="AsyncLocal{T}"/> переживает <c>Task.Run</c>, которым раунд запускает инструменты
/// параллельно, — на этом же держится <see cref="AgentRunScope"/>.
/// </remarks>
internal static class VeniceTurnScope
{
    private static readonly AsyncLocal<VeniceTurnContext?> CurrentContext = new();

    public static VeniceTurnContext? Current => CurrentContext.Value;

    public static IDisposable Push(VeniceTurnContext? context)
    {
        var previous = CurrentContext.Value;
        CurrentContext.Value = context;
        return new Popper(() => CurrentContext.Value = previous);
    }

    /// <summary>
    /// Закрывает ход от вложенной работы, у которой свой счёт. Нужно агенту: его клиент
    /// отдельный, но AsyncLocal протекает внутрь, а стоимость агента и так попадает в ход
    /// через <c>NestedAgent.Cost</c> — без этого она посчиталась бы дважды.
    /// </summary>
    public static IDisposable Suppress() => Push(null);

    private sealed class Popper(Action restore) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                restore();
            }
        }
    }
}
