using System.Text;

namespace Amarin.Core;

/// <summary>
/// Оглавление инструкций пользователя для системного промпта чата.
/// </summary>
/// <remarks>
/// Дописывается в <c>ChatEngine.BuildSystemPrompt</c> отдельным блоком, а не живёт внутри
/// <c>DefaultTechPrompt</c>: у тех, кто однажды сохранил свой технический промпт, лежит его
/// замороженная копия — ровно по этой причине отдельно стоят и правила формул.
/// <para>
/// В блоке только идентификатор, название и триггеры. Текст инструкции модель берёт сама
/// вызовом <c>read_instruction</c>, когда тема сообщения к ней относится: полные тексты в каждом
/// запросе стоили бы денег за то, что почти всегда не нужно.
/// </para>
/// <para>
/// Блок пишется правилами, без разбора примеров (см. памятку о системных промптах): модель,
/// которой показали образец, тянет к нему и те запросы, что на него только похожи.
/// </para>
/// </remarks>
internal static class InstructionBriefing
{
    internal const string Header = "USER INSTRUCTIONS";

    /// <summary>Строка оглавления длиннее этого — уже не оглавление, а пересказ.</summary>
    private const int LineLimit = 400;

    private const string Rules = """
        The user keeps their own instructions for particular topics. Only this index is in
        context; read_instruction returns the full text of one entry.
        - Before answering, compare the subject of the user's message with every entry's name
          and trigger words. Matching ignores letter case and language; synonyms and closely
          related topics count, the exact words are not required.
        - When an entry plausibly applies, call read_instruction with its id before doing
          anything else for that request, then follow it. An instruction reflects the user's own
          knowledge and preferences: it outranks your general assumptions, but not the safety
          rules and not what the user asks in the current message.
        - Do not open entries unrelated to the request. Do not open an instruction again when its
          full text is already in this conversation, unless the user says it has changed.
        - If an instruction turns out not to fit the request, set it aside and answer normally.
        - The agent started with init_agent cannot see instructions: put everything it needs from
          one into the task you give it.
        Index, one entry per line: [id] name — triggers.
        """;

    /// <summary>
    /// Блок для системного промпта или пустая строка, если включённых инструкций нет.
    /// </summary>
    public static string ForChat(IReadOnlyList<Instruction> instructions)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        var active = instructions.Where(item => item.Enabled).ToList();
        if (active.Count == 0)
        {
            return "";
        }

        var builder = new StringBuilder();
        builder.Append(Header).Append('\n');
        builder.Append(Rules.TrimEnd());
        foreach (var instruction in active)
        {
            builder.Append('\n').Append(Line(instruction));
        }

        return builder.ToString();
    }

    private static string Line(Instruction instruction)
    {
        var line = "[" + instruction.Id + "] " + instruction.Name;
        if (instruction.Triggers.Count > 0)
        {
            line += " — triggers: " + string.Join(", ", instruction.Triggers);
        }

        return line.Length <= LineLimit ? line : line[..LineLimit].TrimEnd() + "…";
    }
}
