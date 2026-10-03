using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Порядок проверок перед ходом. До 1.30.0 он был выписан в окне трижды и проверялся только
/// оконными тестами; отказ после развилки оставлял прежнюю ветку спрятанной, а на её месте —
/// вопрос без ответа.
/// </summary>
public sealed class SendPlannerTests
{
    [Fact]
    public void Nothing_to_send_is_not_a_turn()
    {
        Assert.Equal(SendVerdict.Nothing, Composer(new OutgoingDraft("   ")).Verdict);

        // Цитата без слов говорит, о чём ответ, но не чего хотят: человек остаётся в поле.
        Assert.Equal(SendVerdict.NeedsText, Composer(new OutgoingDraft("", Quotes: 1)).Verdict);

        // А картинка без слов — сообщение: «посмотри» не нуждается в тексте.
        Assert.Equal(SendVerdict.Send, Composer(new OutgoingDraft("", Images: 1)).Verdict);
    }

    [Fact]
    public void Window_commands_need_no_key()
    {
        var plan = Composer(new OutgoingDraft("/new"), key: false);

        Assert.Equal(SendVerdict.LocalCommand, plan.Verdict);
        Assert.Equal("new", plan.Local?.Name);
    }

    [Fact]
    public void Without_a_key_nothing_reaches_the_model()
    {
        Assert.Equal(SendVerdict.NoKey, Composer(new OutgoingDraft("привет"), key: false).Verdict);
        Assert.Equal(SendVerdict.NoKey, Composer(new OutgoingDraft("/agent проверь диск", Images: 1), key: false).Verdict);
    }

    [Fact]
    public void The_agent_takes_neither_attachments_nor_quotes()
    {
        Assert.Equal(SendVerdict.AgentNoAttachments, Composer(new OutgoingDraft("/agent проверь диск", Files: 1)).Verdict);
        Assert.Equal(SendVerdict.AgentNoAttachments, Composer(new OutgoingDraft("/agent проверь диск", Images: 1, Quotes: 1)).Verdict);
        Assert.Equal(SendVerdict.AgentNoQuotes, Composer(new OutgoingDraft("/agent проверь диск", Quotes: 1)).Verdict);

        // Обычному сообщению — можно.
        Assert.Equal(SendVerdict.Send, Composer(new OutgoingDraft("что на снимке?", Images: 1, Quotes: 1)).Verdict);
    }

    [Fact]
    public void A_busy_chat_queues_the_message_after_the_same_refusals()
    {
        var plan = Composer(new OutgoingDraft("  и ещё вот что  "), busy: true);
        Assert.Equal(SendVerdict.FollowUp, plan.Verdict);
        Assert.Equal("и ещё вот что", plan.Text);

        // Отказ — раньше очереди: дописанное без ключа ушло бы в ход, который за него не заплатит.
        Assert.Equal(SendVerdict.NoKey, Composer(new OutgoingDraft("и ещё"), key: false, busy: true).Verdict);
        Assert.Equal(SendVerdict.AgentNoQuotes, Composer(new OutgoingDraft("/agent и ещё", Quotes: 1), busy: true).Verdict);
    }

    [Fact]
    public void A_chat_whose_machine_was_removed_sends_nothing_here()
    {
        Assert.Equal(SendVerdict.TargetGone, Composer(new OutgoingDraft("перезапусти службу"), targetGone: true).Verdict);

        // Дописать в идущий ход можно и тогда: он уже работает там, куда его отправили.
        Assert.Equal(SendVerdict.FollowUp, Composer(new OutgoingDraft("и ещё"), busy: true, targetGone: true).Verdict);
    }

    [Fact]
    public void An_agent_command_is_parsed_once()
    {
        var plan = Composer(new OutgoingDraft("/agent проверь диск"));

        Assert.Equal(SendVerdict.Agent, plan.Verdict);
        Assert.Equal(ChatCommands.Agent, plan.Command?.Name);
        Assert.Equal("проверь диск", plan.Command?.Argument);
    }

    [Fact]
    public void A_rerun_checks_the_key_the_agent_and_the_room_in_that_order()
    {
        Assert.Equal(SendVerdict.NoKey, Rerun(new OutgoingDraft("вопрос"), key: false, room: false).Verdict);
        Assert.Equal(SendVerdict.AgentNoAttachments, Rerun(new OutgoingDraft("/agent задача", Images: 1), room: false).Verdict);
        Assert.Equal(SendVerdict.LimitReached, Rerun(new OutgoingDraft("вопрос"), room: false).Verdict);
        Assert.Equal(SendVerdict.Send, Rerun(new OutgoingDraft(" вопрос ")).Verdict);
        Assert.Equal(SendVerdict.Agent, Rerun(new OutgoingDraft("/agent задача")).Verdict);
    }

    private static SendPlan Composer(OutgoingDraft draft, bool key = true, bool busy = false, bool targetGone = false) =>
        SendPlanner.ForComposer(draft, key, busy, targetGone);

    private static SendPlan Rerun(OutgoingDraft draft, bool key = true, bool room = true) =>
        SendPlanner.ForRerun(draft, key, room);
}
