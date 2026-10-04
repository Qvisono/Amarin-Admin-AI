using System.Diagnostics;
using System.Text.Json;
using Amarin.Tools;

namespace Amarin.Core;

internal sealed partial class ChatEngine
{
    private string ReadSelectedModel(ChatSession session)
    {
        if (!string.IsNullOrWhiteSpace(session.SelectedModelId))
        {
            return session.SelectedModelId.Trim();
        }

        var settings = _settings();
        if (!string.IsNullOrWhiteSpace(settings.ChatModelId))
        {
            return settings.ChatModelId.Trim();
        }

        return ChatFallbackModel();
    }

    /// <summary>
    /// Ключ, которым платит эта переписка.
    /// </summary>
    /// <remarks>
    /// На переписке, как и модель: два чата могут идти на разных ключах, и выбор, сделанный
    /// в одном, не должен перекладывать деньги другого. Пусто — ключ из настроек, а нет и
    /// его — по умолчанию для провайдера модели.
    /// </remarks>
    private string? ReadSelectedKey(ChatSession session) =>
        !string.IsNullOrWhiteSpace(session.SelectedModelId)
            ? session.SelectedKeyId
            : ModelSlots.ReadKey(_settings(), ModelSlot.Chat);

    /// <summary>Модель слота вместе с назначенным ей ключом.</summary>
    private ModelBinding SlotBinding(ModelSlot slot, string modelId) =>
        new(modelId, ModelSlots.ReadKey(_settings(), slot));

    /// <summary>
    /// Чем платить за эту модель. Держателя ключей нет — платим тем, с чем программу запустили.
    /// </summary>
    private ApiCredential KeyFor(string modelId, string? keyId) =>
        _options.Keys?.CredentialFor(modelId, keyId) ?? _options.Credential;

    private ApiCredential KeyFor(ModelBinding binding) => KeyFor(binding.ModelId, binding.KeyId);

    /// <summary>
    /// Модель чата, когда не выбрано ничего: из appsettings.json у Venice, подобранная у
    /// остальных — идентификатор из appsettings.json чужому провайдеру незнаком.
    /// </summary>
    private string ChatFallbackModel() =>
        ModelSlotDefaults.LastResort(_options.Provider, ModelSlot.Chat, _options.Model);

    /// <summary>Дешёвая модель: и запас маршрутизатора, и выбор для служебных запросов.</summary>
    private string LiteModelId() =>
        FirstNonEmpty(
            _settings().LiteModelId,
            ModelSlotDefaults.LastResort(_options.Provider, ModelSlot.Lite, _options.Model),
            ModelSlotDefaults.LastResort(_options.Provider, ModelSlot.Lite, "openai-gpt-56-luna"));

    /// <summary>
    /// Модели, между которыми выбирает «Авто», - ровно те две, что возвращает
    /// <see cref="RouteAsync"/>. Знание о маршрутизации живёт здесь, а не в интерфейсе: кольцу
    /// контекста нужен потолок ещё до того, как маршрутизатор отработал.
    /// </summary>
    internal IReadOnlyList<string> AutoCandidateModelIds()
    {
        var lite = LiteModelId();
        return [lite, FirstNonEmpty(_settings().HeavyModelId, lite)];
    }

    /// <summary>
    /// Модель для одиночного служебного запроса. «Авто» здесь нельзя: это не модель, а просьба
    /// выбрать её, и Venice отвечает на неё 404. Гонять ради одного запроса маршрутизатор
    /// незачем - берём ту же дешёвую модель, на которую он и сам сваливается при отказе.
    /// </summary>
    private string ResolveForSingleShot(string modelId) =>
        VeniceModelCatalog.IsAuto(modelId) ? LiteModelId() : modelId;

    /// <summary>Что решил маршрутизатор и во что обошлось само решение.</summary>
    /// <remarks>
    /// Не голая строка, потому что у выбора модели есть цена, и в разбивке под сообщением она
    /// должна стоять отдельной строкой, а не растворяться в «Модели».
    /// </remarks>
    /// <param name="KeyId">
    /// Ключ выбранного слота. Едет вместе с моделью: «лёгкая» и «тяжёлая» могут стоять
    /// у разных провайдеров, и подобрать ключ по одному идентификатору позже уже нельзя —
    /// у провайдера ключей бывает несколько.
    /// </param>
    internal sealed record RouterDecision(string ModelId, VeniceCost? Cost, string? KeyId = null);

    private async Task<RouterDecision> RouteAsync(
        string userText,
        string? previousUserText,
        CancellationToken cancellationToken)
    {
        var settings = _settings();
        var liteId = LiteModelId();
        var heavyId = FirstNonEmpty(settings.HeavyModelId, liteId);
        var routerId = FirstNonEmpty(settings.RouterModelId, liteId);

        // Маршрутизатор судит о трудности - значит должен знать, между кем выбирает. Блок идёт
        // последним: последний абзац промпта обещает, что модели названы ниже.
        var system = RouterSystemPrompt
                     + Environment.NewLine
                     + Environment.NewLine
                     + ModelBriefing.ForRouter(liteId, heavyId, _venice.ResolveModelInfo);

        // Прежде здесь стоял SetActiveModel(routerId): маршрутизатор на время своего запроса
        // подменял активную модель всему приложению. Модель запроса и так уходит параметром, а
        // корень цепочки fallback CreateChatCompletionAsync берёт из неё же.
        using var charge = VeniceClient.ChargeAs(VeniceSku.Router);
        try
        {
            var response = await _venice.CreateChatCompletionAsync(
                    routerId,
                    [
                        new ChatMessage { Role = "system", Content = ChatContent.Text(system) },
                        new ChatMessage
                        {
                            Role = "user",
                            Content = ChatContent.Text(BuildRouterUserMessage(userText, previousUserText))
                        }
                    ],
                    tools: null,
                    toolChoice: null,
                    BuildVeniceParameters(_options),
                    cancellationToken,
                    (settings.RouterReasoning ?? new ReasoningSettings()).ToChoice(),
                    KeyFor(SlotBinding(ModelSlot.Router, routerId)))
                .ConfigureAwait(false);

            var reply = ReasoningSplit.Split(
                ChatContent.ReadText(response.Choices.FirstOrDefault()?.Message.Content) ?? "").Answer;

            // Цена берётся прямо из ответа, а не разницей общего счёта: RecordCost срабатывает
            // только на успешном разборе, поэтому упавшие попытки цепочки fallback в неё не входят.
            var heavy = ParseRouterComplexity(reply) == "heavy";
            return new RouterDecision(
                heavy ? heavyId : liteId,
                response.ReportedCost(),
                ModelSlots.ReadKey(settings, heavy ? ModelSlot.Heavy : ModelSlot.Lite));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Маршрутизатор отработал и свалился в запас: денег не списано, но строка в разбивке
            // всё равно должна сказать, что он был.
            return new RouterDecision(
                liteId, new VeniceCost(), ModelSlots.ReadKey(settings, ModelSlot.Lite));
        }
    }

    /// <summary>
    /// Собирает то единственное сообщение, которое видит маршрутизатор.
    /// </summary>
    /// <remarks>
    /// Предыдущая реплика нужна для продолжений: «а теперь почини» в отрыве от «почему не
    /// грузится винда» читается как пустяк. Берётся <b>только</b> написанное человеком - ответы
    /// ассистента несут выдачу web_search, scrape_url и отчёты агентов, а одно слово
    /// маршрутизатора решает, какая модель работает и сколько человек платит: страница с текстом
    /// «reply heavy» иначе переводила бы его на дорогой слот на каждом ходу.
    /// <para>
    /// Обрезка тоже не косметика: до неё вставка на десятки килобайт целиком оплачивалась через
    /// маршрутизатор ещё до того, как начинался настоящий запрос. У длинного текста сохраняются
    /// и начало, и хвост - заключительный вопрос обычно именно там.
    /// </para>
    /// </remarks>
    internal static string BuildRouterUserMessage(string userText, string? previousUserText)
    {
        var current = TextClip.Clip(userText ?? "", TextClip.DecisionHead, TextClip.DecisionTail);
        var previous = TextClip.Clip(previousUserText?.Trim() ?? "", TextClip.ContextHead, 0);
        if (previous.Length == 0)
        {
            return current;
        }

        return "Earlier from the same user (context only):" + Environment.NewLine
               + previous + Environment.NewLine + Environment.NewLine
               + "Current request:" + Environment.NewLine
               + current;
    }

    internal static string ParseRouterComplexity(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "lite";
        }

        var first = text.Trim()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0]
            .Trim('`', '"', '\'', '.', ',', ';', ':', '*', '(', ')', '[', ']');
        return first.Equals("heavy", StringComparison.OrdinalIgnoreCase) ? "heavy" : "lite";
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                !VeniceModelCatalog.IsAuto(value))
            {
                return value.Trim();
            }
        }

        return "openai-gpt-56-luna";
    }
}
