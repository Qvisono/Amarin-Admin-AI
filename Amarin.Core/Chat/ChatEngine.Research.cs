using System.Diagnostics;

namespace Amarin.Core;

internal sealed partial class ChatEngine
{
    private const string ResearchRules = """
        The user requested deep research. Carry out the displayed plan and revise it if the evidence calls for it.
        Break the question into material subquestions. Check plausible alternatives and counterevidence.
        Use the available tools to investigate, vary the queries, and verify key claims against independent sources.
        Before giving a final answer, use the tools that can substantiate the relevant claims.
        Prefer primary sources where available. Cite the sources actually consulted and say what makes each source
        reliable, limited, or unverified for the claim it supports. Distinguish facts, inferences and conflicting evidence.
        Never invent findings, quotations or URLs. A search snippet alone is weak evidence.
        Try alternative approaches before leaving a gap. If evidence still cannot resolve a material question within
        the available tools and limits, give the supported findings, state the specific gap and what would resolve it.
        """;

    private async Task PrepareResearchAsync(
        ChatSession session,
        IReadOnlyList<Instruction> instructions,
        ChatDisplayMessage assistant,
        IChatTurnObserver observer,
        Stopwatch clock,
        VeniceTurnContext turn,
        CancellationToken cancellationToken)
    {
        var messages = BuildApiMessages(session, turn.ModelId, instructions);
        AddResearchInstructions(messages, """
            Write only a short preliminary research plan in the user's language.
            Identify the material questions, the sources or evidence to seek, and how to test alternatives.
            Do not give findings, use tools, or ask for approval.
            """);

        var response = await StreamOnceAsync(messages, tools: null, toolChoice: null,
                assistant, observer, clock, turn, cancellationToken)
            .ConfigureAwait(false);
        var plan = response.Text.Trim();
        if (plan.Length == 0 || response.ToolCalls.Count > 0)
        {
            throw new VeniceApiException(Loc.Get("S.Command.ResearchPlanFailed"));
        }

        assistant.ResearchPlan = plan;
        assistant.Text = "";
        observer.OnAssistantText(assistant);
        observer.OnToolsChanged(assistant);
    }

    private static void AddResearchInstructions(List<ChatMessage> messages, string instructions)
    {
        var system = messages.FirstOrDefault();
        var hasSystem = system?.Role == "system";
        var content = hasSystem ? ChatContent.ReadText(system!.Content) + "\n\n" + instructions : instructions;
        var message = new ChatMessage { Role = "system", Content = ChatContent.Text(content) };
        if (hasSystem)
        {
            messages[0] = message;
        }
        else
        {
            messages.Insert(0, message);
        }
    }
}
