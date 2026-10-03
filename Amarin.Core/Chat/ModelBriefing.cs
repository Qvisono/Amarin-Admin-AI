namespace Amarin.Core;

/// <summary>
/// Короткая справка о моделях, которые участвуют в разговоре прямо сейчас: кто отвечает,
/// между кем выбирает маршрутизатор, кого получит агент на каждом уровне.
/// </summary>
/// <remarks>
/// Ни маршрутизатор, ни модель чата не знали, какие модели стоят в настройках, и потому не
/// могли оценить избыточность: список из четырёх примеров арифметики уходил на флагман, а
/// рутинная проверка службы — тяжёлому агенту. Блок собирается на лету, потому что
/// идентификаторы моделей — это настройки, а промпты — константы: вшитое имя стало бы ложью в
/// тот момент, когда человек сменит слот.
/// <para>
/// Отдельный класс, а не метод движка: блок нужен двум местам сразу и обязан проверяться без
/// сети. Возможности берутся тем же <c>ResolveModelInfo</c>, которым живёт клиент, — каталог
/// подтягивается лениво, поэтому «имени достаточно» здесь штатный исход, а не сбой.
/// </para>
/// </remarks>
internal static class ModelBriefing
{
    private const string Header = "MODELS";

    /// <summary>Между кем выбирает «Авто». Порядок строк — от дешёвой к дорогой.</summary>
    public static string ForRouter(string liteId, string heavyId, Func<string, VeniceModelInfo?>? resolve)
    {
        var lines = new List<string>
        {
            Header,
            Slot("lite", liteId, resolve),
            Slot("heavy", heavyId, resolve)
        };

        return string.Join("\n", lines);
    }

    /// <summary>Между кем выбирает маршрутизатор уровня агента. Порядок — от дешёвой к дорогой.</summary>
    public static string ForAgentRouter(
        string fastId,
        string liteId,
        string heavyId,
        Func<string, VeniceModelInfo?>? resolve)
    {
        var lines = new List<string>
        {
            Header,
            Slot("fast", fastId, resolve),
            Slot("lite", liteId, resolve),
            Slot("heavy", heavyId, resolve)
        };

        // Единственное, что здесь говорится о деньгах, и оно верно по построению: таблицы цен в
        // программе нет, а выдумывать цифры для промпта нельзя.
        if (Same(liteId, heavyId))
        {
            lines.Add("lite and heavy are the same model: the choice between them changes nothing.");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Что видит собеседник в чате: только своя модель.
    /// </summary>
    /// <remarks>
    /// Три модели агента отсюда убраны вместе с аргументом <c>complexity</c>: исполнителя теперь
    /// выбирает <see cref="AgentTierRouter"/>, а чужие модели в промпте только возвращали модель
    /// чата к решению, которое больше не её. Своя строка остаётся — по ней она знает своё окно и
    /// свои возможности.
    /// </remarks>
    /// <param name="currentModelId">
    /// Модель, на которой идёт ход. Пусто или «auto» — блок будет пустым: так он собирается для
    /// кольца контекста, которому модель ещё не известна.
    /// </param>
    public static string ForChat(string? currentModelId, Func<string, VeniceModelInfo?>? resolve)
    {
        var self = currentModelId?.Trim() ?? "";
        if (self.Length == 0 || VeniceModelCatalog.IsAuto(self))
        {
            return "";
        }

        return Header + "\n" + Slot("you", self, resolve);
    }

    private static bool Same(string left, string right) =>
        left.Trim().Equals(right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Slot(string label, string modelId, Func<string, VeniceModelInfo?>? resolve)
    {
        // GetDisplayName уходит в Loc только на «auto» и на пустой строке, а сюда приходит
        // то, что уже пропустил FirstNonEmpty, — обращения к словарю из безоконного теста не будет.
        var name = VeniceModelCatalog.GetDisplayName(modelId);
        var info = resolve?.Invoke(modelId);
        if (info is null)
        {
            return $"{label} -> {name}";
        }

        var parts = new List<string> { $"{label} -> {name}" };
        var context = VeniceModelCatalog.FormatContext(
            info.ModelSpec?.AvailableContextTokens ?? info.ContextLength);
        if (context.Length > 0)
        {
            parts.Add(context + " context");
        }

        var traits = Traits(info);
        if (traits.Length > 0)
        {
            parts.Add(traits);
        }

        return string.Join("; ", parts);
    }

    /// <summary>
    /// Ровно те признаки, по которым модель отличают в интерфейсе, — и считаются они теми же
    /// помощниками: иначе «что такое code» разошлось бы между подсказкой и промптом.
    /// </summary>
    private static string Traits(VeniceModelInfo model)
    {
        var traits = new List<string>();
        if (model.ModelSpec?.Capabilities?.SupportsReasoning == true)
        {
            traits.Add("reasoning");
        }

        if (VeniceModelCatalog.HasVision(model))
        {
            traits.Add("vision");
        }

        if (VeniceModelCatalog.HasCode(model))
        {
            traits.Add("code");
        }

        return string.Join(", ", traits);
    }
}
