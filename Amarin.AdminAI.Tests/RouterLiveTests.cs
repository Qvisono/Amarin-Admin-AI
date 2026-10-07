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
    /// <summary>Фраза и уровень, которого от маршрутизатора ждём; <c>?</c> — пограничная, только смотрим.</summary>
    private static readonly (string Text, string Expected)[] Phrases =
    [
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

        // Обычная работа на ПК и разговор — лёгкая.
        ("Поставь последний драйвер на видеокарту NVIDIA", "lite"),
        ("Почему медленно работает Wi-Fi? Проверь и почини", "lite"),
        ("Сколько будет 17 * 23 + 5?", "lite"),
        ("Переведи на английский: доброе утро, коллеги", "lite"),
        ("Очисти временные файлы и корзину", "lite"),
        ("Сколько свободного места на диске C?", "lite"),
        ("Расскажи анекдот про программистов", "lite"),
        ("Установи 7-Zip и VLC", "lite"),
        ("Что такое DNS простыми словами?", "lite"),
        ("Install the latest Chrome", "lite"),
        ("How much free space is left on my drives?", "lite"),

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
            LiteModelId = Environment.GetEnvironmentVariable("AMARIN_ROUTER_LITE") ?? "",
            HeavyModelId = Environment.GetEnvironmentVariable("AMARIN_ROUTER_HEAVY")
                           ?? (provider == LlmProvider.OpenRouter ? "openrouter:anthropic/claude-sonnet-4.5" : "claude-sonnet-5"),
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
        var engine = new ChatEngine(new VeniceClient(http, options), options, () => settings, new ToolRegistry([]));
        var route = typeof(ChatEngine).GetMethod("RouteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var heavyId = engine.AutoCandidateModelIds()[1];

        var table = new StringBuilder();
        table.AppendLine(CultureInfo.InvariantCulture, $"## router live — {DateTime.Now:yyyy-MM-dd HH:mm}, heavy = {heavyId}");
        table.AppendLine("| # | ждали | вышло | фраза |").AppendLine("|---|---|---|---|");
        var counted = 0;
        var matched = 0;
        var priced = 0;
        for (var i = 0; i < Phrases.Length; i++)
        {
            var (text, expected) = Phrases[i];
            var decision = await (Task<ChatEngine.RouterDecision>)route.Invoke(engine, [text, null, CancellationToken.None])!;
            var got = string.Equals(decision.ModelId, heavyId, StringComparison.Ordinal) ? "heavy" : "lite";
            priced += decision.Cost is { HasData: true } ? 1 : 0;
            if (expected != "?")
            {
                counted++;
                matched += got == expected ? 1 : 0;
            }

            var mark = expected == "?" ? "" : got == expected ? "" : " ✗";
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
