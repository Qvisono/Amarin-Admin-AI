using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Amarin.Core;

/// <summary>
/// Текст отчёта об аварии: он уходит и в <see cref="CrashLog"/>, и в буфер обмена по кнопке
/// «Скопировать детали».
/// </summary>
/// <remarks>
/// Отчёт человек пересылает — в переписку, в issue. Поэтому перед выдачей он проходит
/// <see cref="Scrub"/>: ключ Venice попадает в сообщения HTTP-исключений и в заголовок
/// <c>Authorization</c>, а секреты не коммитим и тем более не рассылаем.
/// </remarks>
internal static partial class CrashReport
{
    public static string Build(Exception exception, string kind, string? secret = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var sb = new StringBuilder();
        sb.Append("=== Amarin Admin AI — сбой (")
          .Append(kind)
          .Append(") ")
          .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
          .AppendLine(" ===");
        sb.Append("Версия: ").AppendLine(Version);
        sb.Append("ОС: ").AppendLine(Environment.OSVersion.VersionString);
        sb.Append("Разрядность процесса: ").AppendLine(Environment.Is64BitProcess ? "x64" : "x86");
        sb.Append(".NET: ").AppendLine(Environment.Version.ToString());

        var depth = 0;
        for (var current = exception; current is not null; current = current.InnerException)
        {
            sb.AppendLine();
            sb.Append(depth == 0 ? "Исключение: " : $"Вложенное исключение #{depth}: ")
              .AppendLine(current.GetType().FullName);
            sb.Append("Сообщение: ").AppendLine(current.Message);
            if (!string.IsNullOrWhiteSpace(current.StackTrace))
            {
                sb.AppendLine("Стек:").AppendLine(current.StackTrace);
            }

            depth++;
            if (depth > 10)
            {
                // Зацикленные InnerException встречаются у агрегатов — не разворачиваем бесконечно.
                sb.AppendLine("... [цепочка исключений обрезана]");
                break;
            }
        }

        return Scrub(sb.ToString(), secret);
    }

    /// <summary>Прячет ключ API, если он просочился в сообщение или в стек.</summary>
    public static string Scrub(string text, string? secret)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        // Короткая строка в роли «секрета» вырезала бы куски обычного текста.
        if (!string.IsNullOrWhiteSpace(secret) && secret.Length >= 8)
        {
            text = text.Replace(secret, "***", StringComparison.Ordinal);
        }

        return BearerToken().Replace(text, "Bearer ***");
    }

    internal static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-._~+/]{8,}=*", RegexOptions.IgnoreCase)]
    private static partial Regex BearerToken();
}
