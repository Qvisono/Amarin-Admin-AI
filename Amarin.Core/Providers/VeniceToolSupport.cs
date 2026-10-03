using System.Collections.Concurrent;

namespace Amarin.Core;

/// <summary>
/// Модели, которые заявляют поддержку инструментов, но ломаются, получив их.
/// </summary>
/// <remarks>
/// Venice для части моделей собирает грамматику ограниченного декодирования («structural tags»)
/// из наших описаний инструментов и подставляет имя инструмента в неё <b>без кавычек</b>. В lark
/// строчный идентификатор — это ссылка на правило, поэтому компилятор ищет правило
/// <c>read_file</c> и не находит:
/// <code>Failed to compile structural_tag grammar: at 6(8): unknown name: "read_file"</code>
/// Ломается любое имя: с заглавной буквы падает ещё раньше, на первом символе, потому что в lark
/// заглавные идентификаторы — терминалы. То есть дело не в наших схемах и не в именах, и
/// переименование инструментов ничего не даст — чинить это может только Venice.
///
/// Для программы такая модель бесполезна: инструменты уходят в каждом ходе чата и агента.
/// Поэтому ход не «чинится» повтором без инструментов — молча ответившая модель не смогла бы
/// ничего сделать с компьютером, зато могла бы написать, что сделала.
/// </remarks>
internal static class VeniceToolSupport
{
    /// <summary>
    /// Модели, поймавшие этот отказ в этом запуске. Список живёт до перезапуска: почин на
    /// стороне Venice подхватится сам, без правки программы.
    /// </summary>
    private static readonly ConcurrentDictionary<string, byte> Broken = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Узнаёт отказ компилятора грамматики среди прочих ошибок Venice.</summary>
    public static bool IsToolGrammarFailure(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        // Двух признаков достаточно и они переживут смену формулировки: имя фичи и глагол.
        return message.Contains("structural_tag", StringComparison.OrdinalIgnoreCase)
               && message.Contains("grammar", StringComparison.OrdinalIgnoreCase);
    }

    public static void Remember(string? modelId)
    {
        var id = modelId?.Trim();
        if (!string.IsNullOrWhiteSpace(id))
        {
            Broken[id] = 0;
        }
    }

    public static bool IsBroken(string? modelId)
    {
        var id = modelId?.Trim();
        return !string.IsNullOrWhiteSpace(id) && Broken.ContainsKey(id);
    }

    /// <summary>Что показать человеку вместо куска грамматики на пол-экрана.</summary>
    public static string FailureText(string? modelId) =>
        Loc.Format("S.Venice.ToolsBroken", string.IsNullOrWhiteSpace(modelId) ? "?" : modelId.Trim());
}
