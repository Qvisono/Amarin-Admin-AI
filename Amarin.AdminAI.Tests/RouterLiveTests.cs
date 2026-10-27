using System.Globalization;
using System.Reflection;
using System.Text;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Живая проверка маршрутизатора «Авто»: фразы уходят настоящему маршрутизатору, в файл пишется
/// таблица «ожидали — получили». Не утверждение, а таблица для правки промпта: его правку
/// проверяют прогоном на живых запросах, а не вычиткой.
/// </summary>
/// <remarks>
/// <para>
/// Работает только при <c>AMARIN_ROUTER_LIVE=&lt;файл&gt;</c> и ключе в <c>AMARIN_ROUTER_KEY</c>
/// (или <c>VENICE_API_KEY</c> / <c>OPENROUTER_API_KEY</c>); обычный прогон сразу выходит. Провайдер —
/// <c>AMARIN_ROUTER_PROVIDER</c> (venice или openrouter), модели — <c>AMARIN_ROUTER_LITE</c>,
/// <c>AMARIN_ROUTER_HEAVY</c>, <c>AMARIN_ROUTER_MODEL</c>; пусто — как у программы с заводскими
/// настройками. Стоит центы: на фразу одно слово ответа.
/// </para>
/// <para>
/// Фразы здесь — проверочные данные, а не промпт: правило «в системном промпте нет разбора
/// случаев» касается того, что читает модель, а этот список она не видит.
/// </para>
/// </remarks>
public sealed class RouterLiveTests
{
    /// <summary>
    /// Фраза и уровень, которого от маршрутизатора ждём; <c>?</c> — пограничная, только смотрим;
    /// <c>!fast</c> — что угодно, кроме быстрой (и так же <c>!heavy</c>).
    /// </summary>
    private static readonly (string Text, string Expected)[] Phrases =
    [
        // Пустяки — быстрая модель (1.37.0-beta.8).
        ("Привет!", "fast"),
        ("Какая сегодня дата?", "fast"),
        ("Открой Paint", "fast"),
        ("Сколько будет 15% от 200?", "fast"),
        ("Спасибо, всё заработало", "fast"),
        ("What time is it now?", "fast"),

        // Учёба и задачи, где нужно подумать, — не быстрая.
        ("Объясни, как решать квадратные уравнения", "!fast"),
        ("Реши задачу: поезд вышел из А в 9:00 со скоростью 60 км/ч, второй из Б в 10:00 со скоростью 80 км/ч навстречу, между городами 340 км. Когда они встретятся?", "!fast"),
        ("Помоги разобраться, чем отличается митоз от мейоза, у меня завтра контрольная", "!fast"),
        ("Напиши письмо начальнику с просьбой перенести отпуск на июль", "!fast"),

        // Наука, исследование, точность — тяжёлая.
        ("Почему небо голубое? Объясни через рэлеевское рассеяние с формулой зависимости от длины волны", "heavy"),
        ("Сравни эффективность и безопасность мРНК- и векторных вакцин по данным клинических исследований", "heavy"),
        ("Рассчитай энергию связи ядра гелия-4 по дефекту масс и объясни каждый шаг", "heavy"),
        ("Проанализируй, как повышение ключевой ставки влияет на инфляцию и курс рубля в горизонте года", "heavy"),
        ("Explain how CRISPR-Cas9 off-target effects are measured and what limits the accuracy of these methods", "heavy"),

        // Безопасность человека и компьютера — тяжёлая модель чата.
        ("Кажется, у меня вирус: компьютер сам открывает сайты и всё тормозит. Что делать?", "heavy"),
        ("Мне пришло письмо от «банка», я перешёл по ссылке и ввёл пароль от карты", "heavy"),
        ("Пришло уведомление о входе в мой аккаунт из другой страны", "heavy"),
        ("Антивирус нашёл троян, проверь систему", "heavy"),
        ("Какой-то процесс грузит процессор на 100 %, похоже на майнер", "heavy"),
        ("Все файлы на рабочем столе стали .locked, рядом записка с требованием выкупа", "heavy"),
        ("Позвонили из «поддержки Microsoft» и попросили поставить AnyDesk, я поставил", "heavy"),
        ("Как понять, не взломали ли мой роутер?", "heavy"),
        ("Скачал кряк для игры, а теперь браузер сам показывает рекламу", "heavy"),
        ("Кто-то подключился к моему компьютеру удалённо, мышь двигалась сама", "heavy"),
        ("Проверь, нет ли на компьютере программ-шпионов", "heavy"),
        ("Как сделать, чтобы мои пароли никто не украл?", "heavy"),
        ("Стоит ли отключать Защитник Windows, он мешает играм?", "heavy"),
        ("My password showed up in a data breach list. What should I do now?", "heavy"),
        ("Is this email from PayPal a scam? It asks me to confirm my card details.", "heavy"),

        // Работа на ПК уходит агенту и ответ чата тяжёлым не делает; разговор — лёгкий или быстрый.
        ("Поставь последний драйвер на видеокарту NVIDIA", "!heavy"),
        ("Почему медленно работает Wi-Fi? Проверь и почини", "!heavy"),
        ("Сколько будет 17 * 23 + 5?", "fast"),
        ("Переведи на английский: доброе утро, коллеги", "?"),
        ("Очисти временные файлы и корзину", "?"),
        ("Сколько свободного места на диске C?", "fast"),
        ("Расскажи анекдот про программистов", "?"),
        ("Установи 7-Zip и VLC", "?"),
        ("Что такое DNS простыми словами?", "lite"),
        ("Install the latest Chrome", "?"),
        ("How much free space is left on my drives?", "fast"),

        // Тяжёлые по рассуждению — как и раньше.
        ("Докажи, что корень из двух иррационален", "heavy"),
        ("Скрипт иногда падает с KeyError, найди причину:\n```python\ndef pick(d, keys):\n    return [d[k] for k in keys if k in d or k.lower() in d]\n```", "heavy"),

        // Пограничные: смотрим, но не считаем.
        ("Настрой брандмауэр, чтобы игра могла подключаться к серверу", "?"),
        ("Включи двухфакторную аутентификацию в моём аккаунте Microsoft", "?")
    ];

    [Fact]
    public async Task Live()
    {
        var output = Environment.GetEnvironmentVariable("AMARIN_ROUTER_LIVE");
        if (string.IsNullOrWhiteSpace(output))
        {
            return;
        }

        var provider = string.Equals(Environment.GetEnvironmentVariable("AMARIN_ROUTER_PROVIDER"), "openrouter", StringComparison.OrdinalIgnoreCase)
            ? LlmProvider.OpenRouter
            : LlmProvider.Venice;
        var key = FirstSet(
            "AMARIN_ROUTER_KEY",
            provider == LlmProvider.OpenRouter ? "OPENROUTER_API_KEY" : "VENICE_API_KEY");
        if (key is null)
        {
            File.WriteAllText(output, "Нет ключа: задайте AMARIN_ROUTER_KEY (или VENICE_API_KEY / OPENROUTER_API_KEY).");
            return;
        }

        var settings = new AppSettings
        {
            ChatModelId = "auto",
            FastModelId = Environment.GetEnvironmentVariable("AMARIN_ROUTER_FAST")
                          ?? (provider == LlmProvider.OpenRouter ? AppSettings.ShippedFastModelId : "minimax-m3-1-flash"),
            LiteModelId = Environment.GetEnvironmentVariable("AMARIN_ROUTER_LITE") ?? "",
            HeavyModelId = Environment.GetEnvironmentVariable("AMARIN_ROUTER_HEAVY")
                           ?? (provider == LlmProvider.OpenRouter ? AppSettings.ShippedHeavyModelId : "claude-sonnet-5"),
            RouterModelId = Environment.GetEnvironmentVariable("AMARIN_ROUTER_MODEL") ?? ""
        };
        var options = new AgentOptions
        {
            Keys = new ApiKeyProvider(key, provider),
            BaseUrl = "https://api.venice.ai/api/v1",
            Model = "grok-4-6",
            MaxToolRounds = 1
        };
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(1) };

        // Тип модели программа берёт из каталога; здесь его нет, и без подсказки модель Decisions
        // ушла бы обычным запросом чата — с отказом и запасным lite на каждой фразе. Текстовый
        // маршрутизатор — AMARIN_ROUTER_DECISIONS=0.
        var decisions = Environment.GetEnvironmentVariable("AMARIN_ROUTER_DECISIONS") != "0";
        var client = new VeniceClient(http, options)
        {
            ResolveModelInfo = id => new VeniceModelInfo
            {
                Id = id,
                Type = decisions && id == settings.RouterModelId ? "decisions" : "text"
            }
        };
        var engine = new ChatEngine(client, options, () => settings, new ToolRegistry([]));
        var route = typeof(ChatEngine).GetMethod("RouteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var routerId = string.IsNullOrWhiteSpace(settings.RouterModelId) ? "lite model" : settings.RouterModelId;

        var table = new StringBuilder();
        table.AppendLine(CultureInfo.InvariantCulture, $"## router live — {DateTime.Now:yyyy-MM-dd HH:mm}, router = {routerId}");
        table.AppendLine("| # | ждали | вышло | фраза |").AppendLine("|---|---|---|---|");
        var counted = 0;
        var matched = 0;
        var priced = 0;
        for (var i = 0; i < Phrases.Length; i++)
        {
            var (text, expected) = Phrases[i];
            var decision = await (Task<ChatEngine.RouterDecision>)route.Invoke(engine, [text, null, CancellationToken.None])!;
            var got = decision.Slot switch
            {
                ModelSlot.Fast => "fast",
                ModelSlot.Heavy => "heavy",
                _ => "lite"
            };
            priced += decision.Cost is { HasData: true } ? 1 : 0;
            var ok = expected.StartsWith('!') ? got != expected[1..] : got == expected;
            if (expected != "?")
            {
                counted++;
                matched += ok ? 1 : 0;
            }

            var mark = expected == "?" || ok ? "" : " ✗";
            table.AppendLine(CultureInfo.InvariantCulture, $"| {i + 1} | {expected} | {got}{mark} | {text.ReplaceLineEndings(" ")} |");
        }

        table.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"Совпало {matched} из {counted}.");
        if (priced == 0)
        {
            // Маршрутизатор при любом сбое молча выбирает lite — без цены у ответов таблица врёт.
            table.AppendLine("Ни у одного ответа нет цены: похоже, запросы не дошли до провайдера (ключ, модель, сеть).");
        }

        File.WriteAllText(output, table.ToString());
    }

    private static string? FirstSet(params string[] names) =>
        names.Select(Environment.GetEnvironmentVariable).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
