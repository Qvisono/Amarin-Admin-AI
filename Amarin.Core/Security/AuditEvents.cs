using System.Text;
using System.Text.Json;

namespace Amarin.Core;

/// <summary>
/// Строка журнала аудита событием Windows (1.37.0): что видит администратор в журнале «Приложение».
/// </summary>
/// <remarks>
/// Аргументов и заголовка чата в событии нет — так решил человек: журнал «Приложение» читают все
/// пользователи компьютера и он не шифруется, а пути, скрипты и тема разговора остаются в своём
/// журнале, при <see cref="AppSettings.EncryptChats"/> — под шифром. Строку журнала программы
/// событие называет номером и хешем: по ним администратор найдёт её и сверит, что её не подменили.
/// Текст английский: это запись для журнала машины, а не подпись интерфейса.
/// </remarks>
internal static class AuditEvents
{
    /// <summary>Номер события по итогу: администратор фильтрует журнал по номеру.</summary>
    public static int EventId(AuditOutcome outcome) => outcome switch
    {
        AuditOutcome.Ok => 1000,
        AuditOutcome.Failed => 1001,
        AuditOutcome.Refused => 1002,
        _ => 1003
    };

    /// <summary>Предупреждение — всё, кроме исполненного успешно.</summary>
    public static bool IsWarning(AuditOutcome outcome) => outcome != AuditOutcome.Ok;

    /// <summary>Текст события.</summary>
    public static string Format(AuditEntry entry, string hash)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new StringBuilder()
            .AppendLine("AI tool call recorded by Amarin Admin AI.")
            .Append("Tool: ").AppendLine(entry.Tool)
            .Append("Outcome: ").AppendLine(Name(entry.Outcome))
            .Append("Approved by: ").AppendLine(Name(entry.ApprovedBy))
            .Append("SynGuard: ").AppendLine(Name(entry.Guard))
            .Append("Agent: ").AppendLine(entry.Agent ?? "chat")
            .Append("Target: ").AppendLine(entry.Target ?? "this PC")
            .Append("Chat: ").AppendLine(entry.ChatId ?? "-")
            .Append("Audit line: ").AppendLine(entry.Id)
            .Append("Line hash: ").Append(hash)
            .ToString();
    }

    /// <summary>
    /// Копия для <see cref="AuditLog.Mirror"/>: пишет, пока копия включена человеком или политикой.
    /// </summary>
    public static Action<AuditEntry, string> Mirror(Func<bool> enabled)
    {
        ArgumentNullException.ThrowIfNull(enabled);
        var warned = false;
        return (entry, hash) =>
        {
            if (!enabled() && !OrganizationPolicy.Current.WindowsEventLog)
            {
                return;
            }

            if (!WindowsEventLog.Write(EventId(entry.Outcome), IsWarning(entry.Outcome), Format(entry, hash)) &&
                !warned)
            {
                // Одна пометка на запуск: отказ Windows повторился бы на каждом действии.
                warned = true;
                CrashLog.Write("audit: Windows event log refused the copy");
            }
        };
    }

    private static string Name<T>(T value)
        where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
}
