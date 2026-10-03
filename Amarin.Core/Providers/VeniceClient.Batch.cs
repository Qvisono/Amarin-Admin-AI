using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Amarin.Core;

public sealed partial class VeniceClient
{
    // ───────────────────────── пакетная очередь OpenRouter ─────────────────────────

    /// <summary>
    /// Перерыв между опросами заявки. Подменяется в тестах: иначе каждая проверка ожидания
    /// стоила бы несколько настоящих секунд.
    /// </summary>
    internal Func<TimeSpan, CancellationToken, Task> BatchDelay { get; set; } = Task.Delay;

    /// <summary>Идёт ли этот запрос через очередь, а не через обычный эндпоинт.</summary>
    /// <remarks>
    /// Провайдер проверяется вместе с пометкой: <c>:batch</c> — выдумка OpenRouter, и у модели
    /// Venice такой хвост означал бы что угодно, только не очередь.
    /// <para>
    /// Очередь обслуживает только сам ответ человеку. Служебную работу — заголовок переписки,
    /// сводку, маршрутизатор, защиту, поиск в сети — ждёт не человек, а сам ход: маршрутизатор
    /// выбирает модель до первого слова, поиск отвечает внутри вызова инструмента, и сутки
    /// ожидания там означают просто зависший чат. Узнаётся она по пометке статьи расхода
    /// (<see cref="ChargeAs"/>) — той самой, которой уже помечены все одиннадцать таких мест;
    /// перечислять их по именам значило бы забыть двенадцатое. Работа агента приравнена к
    /// служебной по той же причине: агента зовут инструментом посреди раунда.
    /// </para>
    /// <para>
    /// Такой запрос уходит обычным путём к близнецу без пометки — его подставляет
    /// <see cref="BuildOpenRouterPayload"/>. Вдвое дороже, но за заголовок чата это центы,
    /// а очередь там не работает вовсе.
    /// </para>
    /// </remarks>
    private bool IsBatchRoute(string model) =>
        ModelRef.Of(model, _options.Provider) == LlmProvider.OpenRouter &&
        ModelRef.IsBatchOnly(model) &&
        string.IsNullOrWhiteSpace(ChargeLabel.Value) &&
        AgentRunScope.Current is null;

    /// <summary>
    /// Ведёт один запрос через пакетную очередь: кладёт заявку, ждёт её готовности и отдаёт
    /// ответ в том же виде, в каком его отдал бы обычный эндпоинт.
    /// </summary>
    /// <remarks>
    /// Снаружи это неотличимо от синхронного запроса — тем и ценно: агент, инструменты, учёт
    /// трат и запись переписки продолжают работать, ничего не зная про очередь. Плата за
    /// половинную цену — время: окно выполнения у очереди сутки, и ответ законно может идти
    /// часами. Отмена хода снимает и заявку, иначе человек платил бы за ответ, которого уже
    /// никто не прочитает.
    /// </remarks>
    /// <param name="onProgress">
    /// Куда писать, что происходит с заявкой. У потокового пути это то же место, где обычно
    /// проступает ответ: без единого слова ход выглядел бы зависшим.
    /// </param>
    private async Task<ChatCompletionResponse> RunBatchAsync(
        ChatCompletionRequest request,
        string model,
        bool prepareMessages,
        Action<string>? onProgress,
        ApiCredential credential,
        CancellationToken cancellationToken)
    {
        var payload = BuildPayload(request, model, prepareMessages, stream: false);

        if (OpenRouterBatchPlan.UnsupportedReason(payload.Messages) is { } unsupported)
        {
            throw new VeniceApiException(Loc.Format("S.Batch.Unsupported", unsupported));
        }

        var slug = OpenRouterBatchPlan.SlugFor(model);
        var submit = new OpenRouterBatchSubmit
        {
            Endpoint = OpenRouterBatchPlan.ChatEndpoint,
            Model = slug,
            Requests =
            [
                new OpenRouterBatchItem
                {
                    CustomId = OpenRouterBatchPlan.SingleRequestId,
                    Body = OpenRouterBatchPlan.ToBatchBody(payload, slug)
                }
            ]
        };

        onProgress?.Invoke(Loc.Get("S.Batch.Submitting"));
        var batch = await SubmitBatchAsync(submit, model, credential, cancellationToken)
            .ConfigureAwait(false);

        var id = batch.Id;
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new VeniceApiException(
                Loc.Get("S.Batch.NoId"));
        }

        try
        {
            batch = await AwaitBatchAsync(id, batch, onProgress, credential, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Отмена хода не снимает заявку сама: она уже стоит в очереди провайдера и будет
            // посчитана. Снимаем её отдельно и молча — отменять отмену нечем, а человек и так
            // уже ушёл от этого ответа.
            await CancelBatchQuietlyAsync(id, credential).ConfigureAwait(false);
            throw;
        }

        if (!OpenRouterBatchPlan.IsCompleted(batch.Status))
        {
            throw new VeniceApiException(
                Loc.Format(
                    "S.Batch.Failed",
                    batch.Status ?? "?",
                    OpenRouterBatchPlan.Describe(batch.Error)));
        }

        var answer = OpenRouterBatchPlan.ReadAnswer(batch);
        RecordCost(answer, ChargeSku(model), credential);
        return answer;
    }

    private async Task<OpenRouterBatchObject> SubmitBatchAsync(
        OpenRouterBatchSubmit submit,
        string model,
        ApiCredential credential,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            submit, VeniceJsonContext.Default.OpenRouterBatchSubmit);
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        using var httpRequest = Request(HttpMethod.Post, "batches", credential);
        httpRequest.Content = content;

        using var response = await _http.SendAsync(
                httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        UpdateBalanceFromHeaders(response, credential);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        PerfLog.Write($"batch submit status={(int)response.StatusCode} model={model}");

        if (!response.IsSuccessStatusCode)
        {
            throw ApiError(credential, (int)response.StatusCode, ExtractErrorMessage(body));
        }

        return ParseBatch(body);
    }

    /// <summary>
    /// Опрашивает заявку, пока она не придёт к окончательному состоянию.
    /// </summary>
    /// <remarks>
    /// Первые секунды после <c>202</c> заявки ещё нет: очередь её приняла и сохранила, но
    /// спросить о ней нельзя — и <c>GET</c>, и список отвечают 404 «Batch job not found». Это
    /// нормальный ход дела, а не отказ, поэтому 404 внутри окна проявления означает «ещё не
    /// доехала» и опрос продолжается. Без этого первый же опрос обрывал ход ошибкой, которую
    /// человек не мог ни понять, ни обойти.
    /// <para>
    /// Окно отмеряется накопленными перерывами, а не часами: перерывы подменяются в тестах, и
    /// на настоящих часах проверка «заявка так и не проявилась» стоила бы полторы минуты. Со
    /// стороны сети это ещё и правильнее — медленные ответы удлиняют настоящее ожидание, а не
    /// укорачивают его.
    /// </para>
    /// </remarks>
    private async Task<OpenRouterBatchObject> AwaitBatchAsync(
        string id,
        OpenRouterBatchObject batch,
        Action<string>? onProgress,
        ApiCredential credential,
        CancellationToken cancellationToken)
    {
        var delay = TimeSpan.Zero;
        var waited = TimeSpan.Zero;
        var landed = false;

        while (!OpenRouterBatchPlan.IsTerminal(batch.Status))
        {
            onProgress?.Invoke(landed ? Describe(batch) : Loc.Get("S.Batch.Landing"));

            delay = OpenRouterBatchPlan.NextDelay(delay);
            waited += delay;
            await BatchDelay(delay, cancellationToken).ConfigureAwait(false);

            var polled = await PollBatchAsync(id, credential, cancellationToken).ConfigureAwait(false);
            if (polled is null)
            {
                // Уже отвечала — значит заявку удалили, и ждать её возвращения нечего.
                if (landed || waited > OpenRouterBatchPlan.VisibilityWindow)
                {
                    throw new VeniceApiException(Loc.Format("S.Batch.Lost", id));
                }

                continue;
            }

            landed = true;
            batch = polled;
        }

        return batch;
    }

    /// <summary>Состояние заявки — либо <c>null</c>, если очередь её пока не видит.</summary>
    private async Task<OpenRouterBatchObject?> PollBatchAsync(
        string id,
        ApiCredential credential,
        CancellationToken cancellationToken)
    {
        using var httpRequest = Request(
            HttpMethod.Get, "batches/" + Uri.EscapeDataString(id), credential);
        using var response = await _http.SendAsync(
                httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw ApiError(credential, (int)response.StatusCode, ExtractErrorMessage(body));
        }

        return ParseBatch(body);
    }

    /// <summary>
    /// Снимает заявку, о судьбе которой уже никто не спросит.
    /// </summary>
    /// <remarks>
    /// Отдельного описания у этого эндпоинта нет, но состояния <c>cancelling</c> и
    /// <c>cancelled</c> у заявки есть, а сам API повторяет форму OpenAI — значит попытка
    /// уместна. Отказ не важен: худшее, что случится, — заявка доработает и будет посчитана,
    /// то есть ровно то, что было бы без попытки. Токен отмены сюда не передаётся намеренно:
    /// зовут это как раз из обработчика отмены, и общий токен отменил бы саму уборку.
    /// </remarks>
    private async Task CancelBatchQuietlyAsync(string id, ApiCredential credential)
    {
        try
        {
            using var httpRequest = Request(
                HttpMethod.Post, "batches/" + Uri.EscapeDataString(id) + "/cancel", credential);
            using var response = await _http.SendAsync(httpRequest, CancellationToken.None)
                .ConfigureAwait(false);
            PerfLog.Write($"batch cancel status={(int)response.StatusCode}");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            PerfLog.Write($"batch cancel failed: {exception.Message}");
        }
    }

    private static OpenRouterBatchObject ParseBatch(string body)
    {
        try
        {
            return JsonSerializer.Deserialize(body, VeniceJsonContext.Default.OpenRouterBatchObject)
                   ?? throw new VeniceApiException(Loc.Get("S.Batch.EmptyReply"));
        }
        catch (JsonException ex)
        {
            throw new VeniceApiException(
                Loc.Format("S.Batch.Unreadable", ex.Message, Preview(body)));
        }
    }

    /// <summary>Состояние заявки строкой для человека: сам статус и сколько запросов готово.</summary>
    private static string Describe(OpenRouterBatchObject batch)
    {
        var status = Loc.Get("S.Batch.Status." + (batch.Status ?? "").Trim().ToLowerInvariant(), "");
        if (status.Length == 0)
        {
            status = batch.Status ?? "?";
        }

        var counts = batch.RequestCounts;
        return counts is { Total: > 1 }
            ? Loc.Format("S.Batch.WaitingCounted", status, counts.Completed ?? 0, counts.Total)
            : Loc.Format("S.Batch.Waiting", status);
    }
}
