namespace Amarin.Fox;

/// <summary>
/// Что Роза помнит о человеке в этом профиле. Лежит в <c>fox/state.json</c> папки профиля.
/// </summary>
/// <remarks>
/// Свой файл у каждого профиля: отношения с Розой у каждого свои. Здесь только то, что должно
/// пережить перезапуск, — настроение текущей минуты (куда смотрит, что делает) живёт в сцене.
/// Читается и пишется <see cref="FoxStore"/>; меняет его только <see cref="FoxBrain"/>.
/// </remarks>
public sealed class FoxState
{
    /// <summary>Нижняя граница привязанности: «шипит».</summary>
    public const double MinAffection = -100;

    /// <summary>Верхняя граница привязанности: «лучший друг».</summary>
    public const double MaxAffection = 100;

    /// <summary>С чего начинается знакомство: дружелюбно, глаза розовые, но не лучший друг.</summary>
    public const double StartAffection = 20;

    /// <summary>Первое появление Розы в этом профиле: от него считаются «дни вместе» и годовщина.</summary>
    public DateTime? MetAt { get; set; }

    /// <summary>Привязанность: −100…100.</summary>
    public double Affection { get; set; } = StartAffection;

    /// <summary>Обида: 0…100. Пока она высокая, Роза дуется и гладить себя даёт не сразу.</summary>
    public double Resentment { get; set; }

    /// <summary>
    /// Память обид: 0…100. Каждый проступок ложится сюда целиком; пока память есть, ласка греет
    /// слабее (<see cref="FoxFriendship.Trust"/>). В отличие от обиды, часы её не лечат — стирают
    /// только добрые дни (<see cref="FoxFriendship.ForgivenPerDay"/>).
    /// </summary>
    public double Grudge { get; set; }

    /// <summary>Скука: 0…100. Копится, пока на Розу не обращают внимания при открытом окне.</summary>
    public double Boredom { get; set; }

    /// <summary>
    /// Силы: 0…100. Тают от беготни и к ночи, копятся во сне. Уставший чаще лежит и засыпает,
    /// бодрый охотнее играет (<see cref="FoxWill"/>).
    /// </summary>
    public double Energy { get; set; } = 80;

    /// <summary>Глаза синие — холодный и жёсткий. Меняется с запасом, см. <see cref="FoxFriendship"/>.</summary>
    public bool Cold { get; set; }

    /// <summary>Когда окно с Розой последний раз было открыто: по нему он понимает, что «скучал».</summary>
    public DateTime? LastSeenAt { get; set; }

    /// <summary>Когда его последний раз гладили или с ним играли.</summary>
    public DateTime? LastAttentionAt { get; set; }

    /// <summary>
    /// Сколько дней подряд программа была открыта, а Розу не гладили и с ним не играли. Дни, когда
    /// программу не открывали вовсе, сюда не входят: тогда он не обижен, а «скучал».
    /// </summary>
    public int NeglectDays { get; set; }

    /// <summary>Когда он последний раз требовал внимания: не чаще раза в несколько минут.</summary>
    public DateTime? LastDemandAt { get; set; }

    /// <summary>Когда последний раз шалил: шалостей не больше нескольких в час.</summary>
    public DateTime? LastMischiefAt { get; set; }

    /// <summary>Раньше этого воля (<see cref="FoxWill"/>) нового занятия не выбирает: занятия не чаще раза в минуту-две.</summary>
    public DateTime? NextActivityAt { get; set; }

    /// <summary>Когда последний раз исполнилось желание своей модели (<see cref="FoxActs"/>).</summary>
    public DateTime? LastActAt { get; set; }

    /// <summary>Когда последний раз думал о своём своей моделью.</summary>
    public DateTime? LastThoughtAt { get; set; }

    /// <summary>
    /// Сколько раз подряд он отказался прийти на «Позвать» в этом побеге. На третий раз приходит
    /// всегда: обида не делает из него того, кого не вернуть.
    /// </summary>
    public int RefusedCalls { get; set; }

    /// <summary>Сколько раз его гладили за всё время — ради отметки «сотое поглаживание».</summary>
    public int PetsTotal { get; set; }

    /// <summary>Памятные отметки, которые уже отпраздновали: знакомство, сотое поглаживание, годовщины.</summary>
    public List<string> Milestones { get; set; } = [];

    /// <summary>Человек попросил спрятать Розу — до этого времени его нет на экране.</summary>
    public DateTime? HiddenUntil { get; set; }

    /// <summary>Сегодняшний день: из него пишется запись дневника, и в нём же дневные потолки запросов.</summary>
    public FoxDay Today { get; set; } = new();

    /// <summary>
    /// Прошедшие дни, запись за которые ещё не написана (модель не ответила, программу закрыли).
    /// Не больше трёх: дальше дневник по ним не догоняется.
    /// </summary>
    public List<FoxDay> Unwritten { get; set; } = [];

    /// <summary>Последние разговоры с Розой, старые — первыми (<see cref="FoxMemory"/>).</summary>
    public List<FoxExchange> Conversation { get; set; } = [];

    /// <summary>Что Роза решил запомнить о человеке. Видно и стирается в «Нашей дружбе».</summary>
    public List<FoxNote> Notes { get; set; } = [];
}

/// <summary>Один разговор: что сказал человек и что ответил Роза.</summary>
public sealed class FoxExchange
{
    public string Human { get; set; } = "";

    public string Rosa { get; set; } = "";

    public DateTime At { get; set; }
}

/// <summary>Заметка о человеке, которую Роза записал сам, своими словами.</summary>
public sealed class FoxNote
{
    public string Text { get; set; } = "";

    public DateTime At { get; set; }
}

/// <summary>Один день с Розой: что было и сколько раз — сырьё для записи дневника и дневные потолки.</summary>
public sealed class FoxDay
{
    /// <summary>Дата по местным часам, <c>yyyy-MM-dd</c>.</summary>
    public string Date { get; set; } = "";

    /// <summary>Привязанность в начале дня — чтобы дневник знал, потеплело ли за день.</summary>
    public double AffectionAtStart { get; set; }

    /// <summary>
    /// Сколько привязанности за день прибавилось и сколько убавилось — порознь, а не итогом: иначе
    /// проступок и ласка по кругу снова открывали бы дневной предел (<see cref="FoxFriendship.MaxGainPerDay"/>).
    /// </summary>
    public double Gained { get; set; }

    public double Lost { get; set; }

    /// <summary>Сообщений человек отправил и сколько знаков в них было.</summary>
    public int Messages { get; set; }

    public int Characters { get; set; }

    public int NewChats { get; set; }

    public int DeletedChats { get; set; }

    public int Pets { get; set; }

    public int Boops { get; set; }

    public int TailPulls { get; set; }

    public int Throws { get; set; }

    public int Treats { get; set; }

    public int Games { get; set; }

    public int Talks { get; set; }

    public int Searches { get; set; }

    public int Escapes { get; set; }

    public int Mischief { get; set; }

    /// <summary>Сколько раз за день кусал курсор человека.</summary>
    public int Bites { get; set; }

    /// <summary>Сколько раз за день отказался прийти на «Позвать» и послал человека.</summary>
    public int CallRefusals { get; set; }

    /// <summary>Сколько раз за день Роза спрашивал свою модель — дневной потолок трат.</summary>
    public int ModelCalls { get; set; }

    /// <summary>Сколько раз за день думал о своём — свой потолок (<see cref="FoxActs.MaxThoughts"/>).</summary>
    public int Thoughts { get; set; }

    /// <summary>О чём шла речь: тема из <see cref="FoxTopic"/> → сколько раз.</summary>
    public Dictionary<string, int> Topics { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Сколько за день набралось раздражения и радости — шкалы 0–10 дневника считаются отсюда.</summary>
    public double Annoyance { get; set; }

    public double Joy { get; set; }

    /// <summary>Заметные события дня ключами (<c>scared-virus</c>, <c>praised</c>…), без повторов, не больше 16.</summary>
    public List<string> Events { get; set; } = [];

    /// <summary>Сколько сообщений-бреда подряд: с третьего Роза фыркает, а не недоумевает.</summary>
    public int NonsenseStreak { get; set; }

    /// <summary>Отмечает событие дня один раз. <c>true</c> — его ещё не было.</summary>
    public bool Note(string key)
    {
        if (Events.Contains(key) || Events.Count >= 16)
        {
            return false;
        }

        Events.Add(key);
        return true;
    }

    /// <summary>Был ли день вообще: открыто окно, хоть что-то случилось.</summary>
    public bool IsEmpty =>
        Messages == 0 && Pets == 0 && Boops == 0 && TailPulls == 0 && Throws == 0 && Treats == 0 && Games == 0 &&
        Talks == 0 && NewChats == 0 && DeletedChats == 0 && Bites == 0 && CallRefusals == 0 && Events.Count == 0;
}

/// <summary>Запись дневника Розы за день, от первого лица. Лежит в <c>fox/diary.json</c> массивом.</summary>
public sealed class FoxDiaryEntry
{
    /// <summary>За какой день, <c>yyyy-MM-dd</c>. По нему архив сливает дневники.</summary>
    public string Date { get; set; } = "";

    public string Text { get; set; } = "";

    /// <summary>Как сильно человек бесил в этот день: 0–10.</summary>
    public int Annoyed { get; set; }

    /// <summary>Как сильно радовал: 0–10.</summary>
    public int Pleased { get; set; }

    /// <summary>Мордочка, с которой написана запись (имя из faces.fox).</summary>
    public string Mood { get; set; } = "";

    /// <summary>Написала своя модель Розы — или шаблон из статистики дня.</summary>
    public bool ByModel { get; set; }

    public DateTime WrittenAt { get; set; }
}
