using System.Text;
using System.Text.Json;

namespace Amarin.Core;

/// <summary>
/// Разбор аргументов вызова инструмента — с поправкой на то, что их пишет языковая модель.
/// </summary>
/// <remarks>
/// <para>
/// Ровно один объект JSON и ничего больше приходит не всегда. Встречается лишняя скобка в
/// конце, два объекта подряд (когда поток склеил два вызова в один), пояснение после закрывающей
/// скобки, обёртка в <c>```json</c> и объект, ещё раз закодированный строкой. Всё это лечится
/// здесь, а не просьбами в описании инструмента: до модели такие просьбы доходят через раз, и
/// каждый сбой стоит пользователю целого хода.
/// </para>
/// <para>
/// Читается первое значение JSON, хвост отбрасывается. Оборванный на середине объект
/// достраивается недостающими скобками — обрыв потока не должен терять уже названные аргументы.
/// </para>
/// </remarks>
public static class ToolArguments
{
    /// <summary>Разобранные аргументы. Пустая строка — это пустой объект, а не ошибка.</summary>
    /// <exception cref="JsonException">Ничего похожего на JSON извлечь не удалось.</exception>
    public static JsonElement Parse(string? argumentsJson)
    {
        var text = (argumentsJson ?? "").Trim();
        if (text.Length == 0)
        {
            return Empty();
        }

        // Инструмент вернул текст ошибки вместо аргументов — это не «почти JSON», а другое.
        if (text.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
        {
            throw new JsonException("tool arguments look like an error string, not JSON");
        }

        text = StripFence(text);
        text = CutToFirstValue(text);

        if (TryReadFirstValue(text, out var element) ||
            TryReadFirstValue(CloseUnbalanced(text), out element))
        {
            return Unwrap(element);
        }

        // Сообщение от JsonDocument точнее нашего: оно называет позицию и символ.
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>Пустой объект аргументов.</summary>
    public static JsonElement Empty() => JsonDocument.Parse("{}").RootElement.Clone();

    private static string StripFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var firstBreak = text.IndexOf('\n');
        if (firstBreak < 0)
        {
            return text;
        }

        var body = text[(firstBreak + 1)..];
        var closing = body.LastIndexOf("```", StringComparison.Ordinal);
        return (closing >= 0 ? body[..closing] : body).Trim();
    }

    /// <summary>Срезает пояснение перед объектом: «Вот аргументы: {…}».</summary>
    private static string CutToFirstValue(string text)
    {
        if (text.Length == 0 || text[0] is '{' or '[' or '"')
        {
            return text;
        }

        var start = text.IndexOfAny(['{', '[']);
        return start > 0 ? text[start..] : text;
    }

    private static bool TryReadFirstValue(string text, out JsonElement element)
    {
        element = default;
        if (text.Length == 0)
        {
            return false;
        }

        try
        {
            var reader = new Utf8JsonReader(
                Encoding.UTF8.GetBytes(text),
                new JsonReaderOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });

            // ParseValue читает ровно одно значение и не смотрит на то, что идёт после него.
            using var document = JsonDocument.ParseValue(ref reader);
            element = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Аргументы, ещё раз завёрнутые в строку: <c>"{\"path\":\"C:\\\\\"}"</c>.</summary>
    private static JsonElement Unwrap(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            return element;
        }

        var inner = (element.GetString() ?? "").Trim();
        if (inner.Length == 0 || inner[0] is not ('{' or '['))
        {
            return element;
        }

        return TryReadFirstValue(inner, out var unwrapped) ? unwrapped : element;
    }

    /// <summary>
    /// Достраивает скобки, которых не хватает оборванному объекту. Строки и экранирование
    /// учитываются, иначе скобка внутри пути «C:\{...}» посчиталась бы за настоящую.
    /// </summary>
    private static string CloseUnbalanced(string text)
    {
        var stack = new Stack<char>();
        var inString = false;
        var escaped = false;

        foreach (var c in text)
        {
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    stack.Push('}');
                    break;
                case '[':
                    stack.Push(']');
                    break;
                case '}' or ']':
                    if (stack.Count > 0)
                    {
                        stack.Pop();
                    }

                    break;
            }
        }

        if (!inString && stack.Count == 0)
        {
            return text;
        }

        var builder = new StringBuilder(text);
        if (inString)
        {
            builder.Append('"');
        }

        while (stack.Count > 0)
        {
            builder.Append(stack.Pop());
        }

        return builder.ToString();
    }
}
