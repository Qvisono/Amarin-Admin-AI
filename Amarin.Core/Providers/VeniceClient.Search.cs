using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Amarin.Core;

public sealed partial class VeniceClient
{
    /// <summary>
    /// Чем платить за поиск в сети.
    /// </summary>
    /// <remarks>
    /// Поиск оплачивается токенами модели, которая его ведёт, — значит и ключом той же модели.
    /// Порядок такой:
    /// <list type="number">
    /// <item>ключ хода чата — он снят при его начале и съезжает вместе с моделью;</item>
    /// <item>ключ самой копии настроек, если он того же провайдера: у прогона агента ход
    /// подавлен (<c>VeniceTurnScope.Suppress</c>), и это единственное место, где виден ключ,
    /// назначенный слоту агента;</item>
    /// <item>ключ провайдера по умолчанию — когда клиент платит другому серверу.</item>
    /// </list>
    /// Без второго шага поиск внутри агента уходил с ключом провайдера по умолчанию, и его
    /// деньги попадали в журнал чужого ключа.
    /// </remarks>
    private ApiCredential SearchCredential(VeniceTurnContext? turn, LlmProvider provider)
    {
        if (turn?.Credential is { IsEmpty: false } fromTurn && fromTurn.Provider == provider)
        {
            return fromTurn;
        }

        var own = _options.Credential;
        if (own.Provider == provider && !own.IsEmpty)
        {
            return own;
        }

        return _options.Keys?.DefaultFor(provider) ?? own;
    }

    /// <param name="plan">
    /// Что человек выбрал для поиска: закреплённый провайдер с ключом (модель под него уже
    /// подобрана в <see cref="ModelSlots.WebSearch"/>) и движок OpenRouter. Пусто — искать
    /// моделью хода умолчательным движком, как было до появления настройки.
    /// </param>
    public async Task<string> SearchWebAsync(
        string query,
        WebSearchPlan? plan = null,
        CancellationToken cancellationToken = default)
    {
        // Поиск раньше «случайно» попадал на модель чата: её только что записал SetActiveModel.
        // Теперь поле клиента не дрейфует, поэтому модель хода берём из его собственного контекста.
        // Модель хода, а вне хода — своя, подобранная под провайдера: в _options.Model лежит
        // идентификатор из appsettings.json, то есть модель Venice, и на ключе OpenRouter
        // поиск уходил бы к модели, которой там нет.
        var turn = VeniceTurnScope.Current;

        // Пусто и «авто» — одно состояние: пусто приходит из нетронутых настроек, «авто» —
        // из плашки, где человек вернул выбор обратно.
        var fixedSearch = plan is { FollowsTurn: false } pinned ? pinned.Binding : (ModelBinding?)null;

        var model = fixedSearch?.ModelId
                    ?? turn?.ModelId
                    ?? ModelSlotDefaults.LastResort(_options.Provider, ModelSlot.Chat, _options.Model);
        var provider = ModelRef.Of(model, _options.Provider);

        // Закреплённый поиск платит своим ключом: он и выбирался ради того, чтобы деньги за
        // интернет шли с названного счёта, а не с того, на котором идёт разговор.
        var credential = fixedSearch is { } target
            ? RequireKey(_options.Keys?.CredentialFor(target) ?? _options.Credential, model)
            : SearchCredential(turn, provider);

        // Своя поисковая надстройка у каждого провайдера: у Venice — venice_parameters,
        // у OpenRouter — плагин. Поиск xAI живёт только у Venice.
        var useXSearch = provider == LlmProvider.Venice &&
                         (_options.EnableXSearch
                          ?? model.Contains("grok", StringComparison.OrdinalIgnoreCase));

        // Поиск оплачивается токенами модели, но в разбивке трат он обязан стоять своей
        // строкой: человек спрашивает «сколько ушло на интернет», а не «сколько ушло на Grok
        // во время поиска».
        using (ChargeAs(VeniceSku.WebSearch))
        {
            var response = await CreateChatCompletionAsync(new ChatCompletionRequest
            {
                Model = model,
                Messages =
                [
                    new ChatMessage
                    {
                        Role = "system",
                        // Ссылки — суть, а не украшение: вызывающий — модель, которая скачает
                        // картинку или прочтёт страницу, только получив адрес. Прежняя просьба о
                        // «сводке с практическими советами» давала прозу вроде «поищите тег на
                        // таком-то сайте» — совет, по которому не сработает ни один инструмент.
                        Content = ChatContent.Text(
                            "You are a web research assistant. Search the web and answer concisely in the language of the question.\n" +
                            "ALWAYS end with a section 'Ссылки:' listing the full URLs you actually used, " +
                            "one per line, bare (no markdown, no shortening). Never write a link as a " +
                            "description like 'ищи по тегу X on site Y' - give the address itself.\n" +
                            "If the request is about pictures, art, wallpapers, photos or covers, list at " +
                            "least 5 URLs of pages that show a matching image, and direct file URLs " +
                            "(.jpg/.png/.webp) whenever the search results reveal them.")
                    },
                    new ChatMessage { Role = "user", Content = ChatContent.Text(query) }
                ],
                VeniceParameters = provider == LlmProvider.Venice
                    ? new VeniceParameters
                    {
                        IncludeVeniceSystemPrompt = false,
                        EnableWebSearch = "on",
                        EnableWebCitations = _options.EnableWebCitations,
                        EnableXSearch = useXSearch ? true : null
                    }
                    : null,
                // Движок — только у OpenRouter: у Venice поиск один, и лишние поля он
                // отвергает вместе со всем запросом.
                Plugins = provider == LlmProvider.Venice
                    ? null
                    :
                    [
                        new RequestPlugin
                        {
                            Id = "web",
                            MaxResults = 5,
                            Engine = plan?.Engine,
                            Mode = plan?.Mode
                        }
                    ]
            }, cancellationToken, credential).ConfigureAwait(false);

            var message = response.Choices.FirstOrDefault()?.Message;
            var text = ReasoningSplit.Split(ChatContent.ReadText(message?.Content) ?? "").Answer;
            text = WithCitations(text, message?.Annotations);
            return string.IsNullOrWhiteSpace(text) ? "Результаты поиска не найдены." : text;
        }
    }

    /// <summary>
    /// Дописывает к ответу ссылки, которые провайдер приложил отдельным полем.
    /// </summary>
    /// <remarks>
    /// Адреса — весь смысл поиска: зовёт его модель, которая умеет открыть страницу или забрать
    /// картинку, но только если ей дали адрес. Просьбы в системном промпте недостаточно —
    /// OpenRouter кладёт найденное в <c>annotations</c>, и модель, отвечающая по этим ссылкам,
    /// пересказать их текстом забывает. Уже названные в ответе не повторяем.
    /// </remarks>
    private static string WithCitations(string text, JsonElement? annotations)
    {
        if (annotations is not { ValueKind: JsonValueKind.Array } list)
        {
            return text;
        }

        var links = new List<string>();
        foreach (var item in list.EnumerateArray())
        {
            if (!item.TryGetProperty("url_citation", out var citation) ||
                !citation.TryGetProperty("url", out var url) ||
                url.GetString() is not { Length: > 0 } address ||
                links.Contains(address, StringComparer.OrdinalIgnoreCase) ||
                text.Contains(address, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            links.Add(address);
        }

        if (links.Count == 0)
        {
            return text;
        }

        var header = text.Contains("Ссылки:", StringComparison.Ordinal)
            ? Environment.NewLine
            : Environment.NewLine + Environment.NewLine + "Ссылки:" + Environment.NewLine;
        return text + header + string.Join(Environment.NewLine, links);
    }
}
