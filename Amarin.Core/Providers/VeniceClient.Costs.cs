using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Amarin.Core;

public sealed partial class VeniceClient
{
    public void ResetRequestCost()
    {
        lock (_costGate)
        {
            RequestCost = VeniceCost.Zero;
        }
    }

    private void RecordCost(ChatCompletionResponse result, string model, ApiCredential credential)
    {
        // По тому же правилу, по которому цену ответа читают побочные запросы для разбивки под
        // сообщением: в журнале трат и в разбивке обязана стоять одна и та же сумма.
        var usage = result.Usage;
        var cost = CostOf(
            result.ReportedCost() ?? VeniceCost.Zero,
            model,
            usage?.PromptTokens ?? 0,
            usage?.CompletionTokens ?? 0,
            result.ServiceTier);

        // Посчитанную цену — и в сам ответ: восемь побочных запросов (заголовок, сводка,
        // маршрутизатор…) читают её через ReportedCost и ставят свою строку в разбивку.
        if (cost.Estimated && usage is not null)
        {
            usage.Cost = cost.Usd;
        }

        Book(cost, model, credential);
    }

    /// <summary>Записывает расход, если в нём есть хоть деньги, хоть токены.</summary>
    private void Book(VeniceCost cost, string model, ApiCredential credential)
    {
        if (cost.HasData || cost.InputTokens > 0 || cost.OutputTokens > 0)
        {
            AddCost(cost, ChargeSku(model), credential);
        }
    }

    /// <summary>
    /// Цена ответа: названная провайдером, а нет её — токены × цена модели из каталога.
    /// </summary>
    /// <remarks>
    /// Считается только у провайдеров, которые цену не называют вовсе: у Venice и OpenRouter
    /// нулевая цена — это бесплатная модель, а не молчание, и досчитывать её нельзя. Токены
    /// приписываются всегда — по ним ведётся расход ключа, выданного пакетом токенов.
    /// </remarks>
    /// <param name="servedTier">
    /// Каким уровнем ответ обслужили (1.37.0). Каталог знает только обычную цену, а Flex, Fast и
    /// Ultrafast стоят иначе: без поправки оценка врала бы вдвое и больше, а по ней держатся лимиты.
    /// </param>
    private VeniceCost CostOf(
        VeniceCost reported,
        string model,
        int promptTokens,
        int completionTokens,
        string? servedTier = null)
    {
        var cost = new VeniceCost
        {
            Usd = reported.Usd,
            Diem = reported.Diem,
            HasData = reported.HasData,
            InputTokens = Math.Max(0, promptTokens),
            OutputTokens = Math.Max(0, completionTokens)
        };

        if (reported.HasData || ProviderSpec.For(ModelRef.Of(model, _options.Provider)).ReportsCost)
        {
            return cost;
        }

        if (CostEstimator.PriceOf(ResolveModelInfo?.Invoke(model)) is not { } price)
        {
            return cost;
        }

        var usd = (cost.InputTokens * price.InputPerMillion + cost.OutputTokens * price.OutputPerMillion) / 1_000_000m
                  * ModelSpeeds.PriceFactor(servedTier);
        if (usd > 0m)
        {
            cost.Usd = usd;
            cost.HasData = true;
            cost.Estimated = true;
        }

        return cost;
    }

    /// <summary>
    /// The one way money is booked. Tool calls in a round run in parallel and share this client,
    /// so the running total needs a gate; the same charge is also billed to whichever tool call
    /// is on the stack, which is what puts a price tag next to generate_image in the transcript.
    /// </summary>
    /// <param name="sku">
    /// За что списали: идентификатор модели либо служебная статья вроде <c>web-search-request</c>.
    /// Из этих пометок складывается разбивка «на что ушло» на странице «Key &amp; Info».
    /// </param>
    /// <param name="credential">
    /// Ключ, которым за это заплатили. Именно он, а не выбранный ключ программы: слоты моделей
    /// платят разными ключами, а рисование картинок и чтение страниц — всегда ключом Venice.
    /// Прежде списание записывалось на выбранный ключ, и деньги ключа Venice за картинку
    /// оказывались в журнале ключа OpenRouter — график врал у обоих.
    /// </param>
    private void AddCost(VeniceCost cost, string sku, ApiCredential credential)
    {
        lock (_costGate)
        {
            RequestCost = RequestCost.Add(cost);
        }

        // Собственный журнал трат: Venice свой отдаёт только админ-ключу, а работают
        // обычным. Здесь же, в единственной точке учёта, видны все деньги программы сразу.
        _options.SpendSink?.Invoke(credential.Secret, cost, sku);

        // Свой счёт у каждого хода чата: один общий RequestCost на несколько одновременных
        // ходов не делится. Сам он остаётся — им пользуется агент, у которого клиент на прогон.
        VeniceTurnScope.Current?.Add(cost);
        SpendScope.Current?.Add(cost);
        AgentRunScope.Charge(cost);
    }

    /// <summary>
    /// Ключ Venice для того, что умеет только Venice. Пустой — значит такого ключа в программе
    /// нет вовсе.
    /// </summary>
    private ApiCredential VeniceCredential() => _options.VeniceCredential;

    /// <summary>
    /// Отказ, который читает модель.
    /// </summary>
    /// <remarks>
    /// Пишем ей, что делать дальше, а не только что пошло не так: иначе она повторяет вызов
    /// раунд за раундом, пока не кончатся попытки, и человек платит за каждый.
    /// </remarks>
    private static ApiCredential RequireVenice(ApiCredential credential, string what)
    {
        if (credential.IsEmpty)
        {
            throw new VeniceApiException(
                $"{what} работает только через Venice, а ключа Venice в программе нет. " +
                "Скажи пользователю добавить ключ Venice на странице настроек «Key & Info». " +
                "Не повторяй этот вызов.");
        }

        return credential;
    }

    /// <summary>
    /// Лимиты трат (E1) — до того, как запрос уйдёт: списание уже не отменить.
    /// </summary>
    /// <remarks>
    /// Одна проверка на вход в каждый платный путь, а не на каждую попытку внутри: цепочка
    /// запасных моделей и очередь OpenRouter идут дальше тем же решением. Цены запроса заранее
    /// никто не знает, поэтому лимит — порог по уже потраченному: последний запрос может
    /// перешагнуть его на свою цену.
    /// </remarks>
    private Task GuardSpendAsync(ApiCredential credential, CancellationToken cancellationToken) =>
        _options.SpendGate?.Invoke(credential, cancellationToken) ?? Task.CompletedTask;

    /// <param name="credential">
    /// Чей это остаток. Нужен книге остатков: заголовки приходят на ответах всех ключей, и
    /// без имени владельца сумма по ключам не складывается.
    /// </param>
    private void UpdateBalanceFromHeaders(HttpResponseMessage response, ApiCredential credential)
    {
        var usd = TryReadHeaderDecimal(response, "x-venice-balance-usd");
        var diem = TryReadHeaderDecimal(response, "x-venice-balance-diem");

        if (usd is null && diem is null)
        {
            return;
        }

        var balance = new VeniceBalance
        {
            CanConsume = true,
            Usd = usd,
            Diem = diem
        };

        _options.BalanceSink?.Invoke(credential, balance);

        // Поле клиента — остаток выбранного ключа и только его: на нём стоит запасной путь
        // страницы настроек, и чужая цифра там читалась бы как деньги человека.
        if (IsSelectedKey(credential))
        {
            LastBalance = balance;
        }
    }

    /// <summary>
    /// Остаток, названный провайдером в ответе на прямой запрос.
    /// </summary>
    /// <remarks>
    /// Доллары складываются с пакетными кредитами: это одни и те же деньги, номинированные
    /// в долларах, и показывать их двумя числами значило бы спрашивать человека, какое из них
    /// его остаток. DIEM живёт отдельно — у него свой курс.
    /// </remarks>
    private void NoteBalance(ApiCredential credential, VeniceRateLimitsData limits)
    {
        if (_options.BalanceSink is not { } sink || limits.Balances is not { } balances)
        {
            return;
        }

        sink(credential, new VeniceBalance
        {
            CanConsume = limits.AccessPermitted,
            Usd = (balances.Usd ?? 0m) + (balances.BundledCredits ?? 0m),
            Diem = balances.Diem
        });
    }

    private static decimal? TryReadHeaderDecimal(HttpResponseMessage response, string headerName)
    {
        if (!response.Headers.TryGetValues(headerName, out var values))
        {
            return null;
        }

        var text = values.FirstOrDefault();
        return decimal.TryParse(text, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}
