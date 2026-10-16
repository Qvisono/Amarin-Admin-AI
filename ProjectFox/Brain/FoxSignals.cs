namespace Amarin.Fox;

/// <summary>Что случилось — вход мозга Розы.</summary>
internal enum FoxSignalKind
{
    /// <summary>Роза пришёл в окно: начало сеанса, после побега или после «спрятать».</summary>
    Appeared,

    /// <summary>Окно вернули из свёрнутого или из трея.</summary>
    Restored,

    /// <summary>Сняли блокировку экраном пароля.</summary>
    Unlocked,

    /// <summary>Прошло время (<see cref="FoxSignal.Value"/> — секунд) при открытом окне.</summary>
    Tick,

    Boop,
    Pet,
    Scratch,
    TailPull,
    EarTouch,
    DoubleClick,
    TripleClick,
    HoverStart,

    /// <summary>Курсор задержался на Розе: холодный кусает.</summary>
    HoverLong,

    Grab,

    /// <summary>Трясут в руках.</summary>
    Shake,

    /// <summary>Бросили; <see cref="FoxSignal.Value"/> — скорость, точек за миллисекунду.</summary>
    Thrown,

    /// <summary>Приземлился; <see cref="FoxSignal.Value"/> — скорость удара.</summary>
    Landed,

    Treat,
    PlayBall,
    PlayLaser,

    /// <summary>Человек сказал Розе что-то в его пузырьке (<see cref="FoxSignal.Text"/>).</summary>
    Talk,

    /// <summary>Спрятать: <see cref="FoxSignal.Value"/> — часов, 0 — до завтра.</summary>
    Hide,

    /// <summary>Позвали обратно по следу лапки после побега.</summary>
    Call,

    /// <summary>Человек отправил сообщение (<see cref="FoxSignal.Text"/>).</summary>
    UserSent,
    AssistantStarted,

    /// <summary>Ответ готов (<see cref="FoxSignal.Text"/> — его начало).</summary>
    AssistantCompleted,
    AssistantFailed,
    AssistantCancelled,

    /// <summary>Ответ идёт долго.</summary>
    LongWait,
    AgentWorking,
    SpendWarning,
    NewChat,

    /// <summary>Удалили чаты: <see cref="FoxSignal.Value"/> — сколько.</summary>
    ChatsDeleted,
    ThemeChanged,

    /// <summary>Открыли настройки; <see cref="FoxSignal.Text"/> — страница (<c>keys</c> — ключи).</summary>
    SettingsOpened,
    SettingsClosed,

    /// <summary>Вопрос об опасном действии.</summary>
    DangerAsked,

    /// <summary>Модель Розы ответила реакцией: <see cref="FoxSignal.Text"/> — реплика, <see cref="FoxSignal.Name"/> — мордочка, <see cref="FoxSignal.Value"/> — сдвиг дружбы.</summary>
    MindReaction,

    /// <summary>Модель Розы решила сбежать на <see cref="FoxSignal.Value"/> минут.</summary>
    MindEscape,

    /// <summary>Модель Розы чего-то захотела (<see cref="FoxActs"/>): <see cref="FoxSignal.Name"/> — что. Решает мозг.</summary>
    MindAct,

    /// <summary>Курсор крутится и дёргается рядом с Розой — дразнят.</summary>
    Teased,

    /// <summary>Прятки: человек нашёл выглядывающего Розу.</summary>
    Found,

    /// <summary>Догонялки: человек поймал убегающего Розу.</summary>
    Caught,

    /// <summary>Роза принёс мячик и звал играть, а мячик не бросили.</summary>
    GameIgnored,

    /// <summary>Открыли журнал действий или «Состояние ПК»: выглядывает из-за верха карточки.</summary>
    ViewerOpened,
    ViewerClosed,

    /// <summary>Открыли другой чат: <see cref="FoxSignal.Text"/> — заголовок, <see cref="FoxSignal.Value"/> — сколько в нём сообщений.</summary>
    ChatOpened,

    /// <summary>Человек начал говорить голосом: уши торчком.</summary>
    RecordingStarted,

    /// <summary>Человек ищет по чатам: принюхивается.</summary>
    SearchTyping,

    /// <summary>К сообщению прикрепили файл или картинку.</summary>
    AttachmentAdded
}

/// <summary>Сигнал мозгу: вид и необязательные текст, имя и число.</summary>
internal sealed record FoxSignal(FoxSignalKind Kind, string Text = "", double Value = 0, string Name = "")
{
    public static implicit operator FoxSignal(FoxSignalKind kind) => new(kind);
}

/// <summary>Что Роза делает в ответ — выход мозга, его исполняет сцена.</summary>
internal enum FoxActionKind
{
    /// <summary>Анимация из Sprites (<see cref="FoxAction.Name"/>).</summary>
    Anim,

    /// <summary>Мордочка из faces.fox на <see cref="FoxAction.Ms"/>.</summary>
    Face,

    /// <summary>Фраза из словаря строк: <see cref="FoxAction.Name"/> — ключ.</summary>
    Say,

    /// <summary>Реплика своей модели: <see cref="FoxAction.Text"/>.</summary>
    Line,

    /// <summary>Частицы: <see cref="FoxAction.Name"/>, <see cref="FoxAction.Ms"/> — сколько штук.</summary>
    Particle,

    /// <summary>Предмет в лапах или во рту на <see cref="FoxAction.Ms"/>.</summary>
    Item,

    /// <summary>Дрожит <see cref="FoxAction.Ms"/>.</summary>
    Shake,

    /// <summary>Сменить глаза: <see cref="FoxAction.Name"/> — <c>cold</c> или <c>warm</c>, с морганием.</summary>
    Eyes,

    /// <summary>Перейти в место из <see cref="FoxSpot"/>: домой, на карточку, в середину поля ввода, за поле ввода.</summary>
    Go,

    /// <summary>Отвернуться от человека.</summary>
    TurnAway,

    /// <summary>Убежать за край окна на <see cref="FoxAction.Ms"/>.</summary>
    Escape,

    /// <summary>Спрятаться до времени из <see cref="FoxState.HiddenUntil"/>.</summary>
    Hide,

    /// <summary>Шалость из <see cref="FoxMischief"/>.</summary>
    Mischief,

    /// <summary>Спросить свою модель: <see cref="FoxAction.Name"/> — <c>react</c> или <c>talk</c>, <see cref="FoxAction.Text"/> — что прочитать.</summary>
    AskMind,

    /// <summary>Написать дневник за дни из <see cref="FoxState.Unwritten"/>.</summary>
    Diary,

    /// <summary>Значок эмоции в пузыре над головой: <see cref="FoxAction.Name"/> — значок, <see cref="FoxAction.Ms"/> — сколько.</summary>
    Emote,

    /// <summary>
    /// Эффект вокруг Розы: <c>tremble</c> — дрожь и волны по бокам, <c>chomp</c> — зубы смыкаются над
    /// курсором; <see cref="FoxAction.Ms"/> — сколько.
    /// </summary>
    Effect,

    /// <summary>Занятие, которое выбрала воля (<see cref="FoxWill"/>): <see cref="FoxAction.Name"/> — какое.</summary>
    Do,

    /// <summary>Фраза из-за края окна, у следа «Позвать»: Роза убежал и отказывается прийти.</summary>
    Shout
}

internal sealed record FoxAction(FoxActionKind Kind, string Name = "", int Ms = 0, string Text = "")
{
    public static FoxAction Anim(string name) => new(FoxActionKind.Anim, name);

    public static FoxAction Face(string name, int ms = 1600) => new(FoxActionKind.Face, name, ms);

    public static FoxAction Say(string key, int ms = 2000) => new(FoxActionKind.Say, key, ms);

    public static FoxAction Line(string text, int ms = 3200) => new(FoxActionKind.Line, "", ms, text);

    public static FoxAction Particle(string name, int count = 1) => new(FoxActionKind.Particle, name, count);

    public static FoxAction Item(string name, int ms = 2800) => new(FoxActionKind.Item, name, ms);

    public static FoxAction Shake(int ms) => new(FoxActionKind.Shake, "", ms);

    public static FoxAction Eyes(bool cold) => new(FoxActionKind.Eyes, cold ? "cold" : "warm");

    public static FoxAction Go(string spot) => new(FoxActionKind.Go, spot);

    public static FoxAction TurnAway() => new(FoxActionKind.TurnAway);

    public static FoxAction Escape(TimeSpan duration) => new(FoxActionKind.Escape, "", (int)duration.TotalMilliseconds);

    public static FoxAction Hide() => new(FoxActionKind.Hide);

    public static FoxAction Mischief(string kind) => new(FoxActionKind.Mischief, kind);

    public static FoxAction AskMind(string kind, string text) => new(FoxActionKind.AskMind, kind, 0, text);

    public static FoxAction Diary() => new(FoxActionKind.Diary);

    public static FoxAction Emote(string name, int ms = 2000) => new(FoxActionKind.Emote, name, ms);

    public static FoxAction Effect(string name, int ms = 0) => new(FoxActionKind.Effect, name, ms);

    public static FoxAction Do(string activity) => new(FoxActionKind.Do, activity);

    public static FoxAction Shout(string key) => new(FoxActionKind.Shout, key, 2200);
}

/// <summary>
/// Места Розы: кромки, на которых он сидит, и цели, куда его зовёт мозг (<see cref="FoxActionKind.Go"/>).
/// </summary>
/// <remarks>Строками, как занятия и шалости: мозг передаёт место сцене именем в действии.</remarks>
internal static class FoxSpot
{
    /// <summary>Верхняя кромка поля ввода — дом.</summary>
    public const string Home = "home";

    /// <summary>Верх карточки настроек.</summary>
    public const string Settings = "settings";

    /// <summary>Верх карточки журнала или «Состояния ПК».</summary>
    public const string Viewer = "viewer";

    /// <summary>Верх нижнего блока колонки чатов.</summary>
    public const string Column = "column";

    /// <summary>Низ окна.</summary>
    public const string Floor = "floor";

    /// <summary>Кнопка отправки — шалость «сесть на неё».</summary>
    public const string Send = "send";

    /// <summary>Плашка модели — шалость «погрызть».</summary>
    public const string Chip = "chip";

    /// <summary>Только цель: середина поля ввода, где забытый требует внимания.</summary>
    public const string Center = "center";

    /// <summary>Только цель: спрятаться за поле ввода и выглядывать.</summary>
    public const string Peek = "peek";
}

/// <summary>
/// Обстановка в окне в миг сигнала: от неё зависит, можно ли шалить и что видно.
/// </summary>
/// <remarks>Собирает сцена; мозг окна не знает и читает только это.</remarks>
internal sealed record FoxSituation
{
    /// <summary>Окно активно и видно.</summary>
    public bool WindowActive { get; init; } = true;

    /// <summary>Поверх ленты лежит вопрос, уведомление, журнал или любой другой слой.</summary>
    public bool Covered { get; init; }

    /// <summary>Поле ввода пустое и без вложений.</summary>
    public bool InputEmpty { get; init; } = true;

    /// <summary>Сколько прошло с последнего нажатия клавиши в поле ввода.</summary>
    public TimeSpan SinceTyping { get; init; } = TimeSpan.MaxValue;

    /// <summary>В открытом чате идёт ход.</summary>
    public bool TurnRunning { get; init; }

    /// <summary>Человек что-то делает в колонке или ленте: выбор чатов, рамка, лупа, запись голоса, поиск.</summary>
    public bool UiBusy { get; init; }

    /// <summary>Шалости включены в настройках Розы.</summary>
    public bool MischiefEnabled { get; init; } = true;

    /// <summary>Можно читать сообщения своей моделью.</summary>
    public bool ReadChat { get; init; } = true;

    /// <summary>Сколько раз Розу тыкнули в нос за последние секунды.</summary>
    public int RecentBoops { get; init; }

    /// <summary>Роза сейчас за краем окна — убежал.</summary>
    public bool Away { get; init; }

    /// <summary>Двигаться плавно можно: без анимаций воля выбирает только смену позы.</summary>
    public bool Motion { get; init; } = true;

    /// <summary>Свои мысли модели включены в настройках Розы. В обстановке по умолчанию — нет: мысль стоит денег.</summary>
    public bool Thoughts { get; init; }

    /// <summary>Роза занят человеком: в руках, в игре, в разговоре.</summary>
    public bool Holding { get; init; }

    /// <summary>Роза спит.</summary>
    public bool Asleep { get; init; }
}
