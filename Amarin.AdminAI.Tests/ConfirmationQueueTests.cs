using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Очередь подтверждений стала общей на несколько одновременных ходов: отмена одного чата
/// больше не имеет права убивать вопросы соседнего, а порядок оставшихся обязан уцелеть.
/// </summary>
public sealed class ConfirmationQueueTests
{
    private static DangerousActionInfo Action(string tool) => new(
        tool,
        "изменение " + tool,
        "подробности",
        DangerousRiskLevel.Medium,
        "пояснение");

    private static ConfirmationQueue Queue() => new(() => new AppSettings());

    [Fact]
    public async Task Approving_everything_automatically_does_not_answer_the_guards_question()
    {
        // «Подтверждать всё автоматически» — про удобство на обычных изменениях. Вопрос SynGuard
        // обычным не бывает: это последняя преграда перед тем, что признано атакой, и отвечать
        // на неё настройкой удобства нельзя — ровно как с белым списком загрузок.
        var settings = new AppSettings { ApprovalMode = ApprovalMode.AlwaysApprove };
        var queue = new ConfirmationQueue(() => settings);

        var ordinary = queue.ConfirmAsync("Агент", Action("registry"), "s1");
        Assert.True(await ordinary);
        Assert.False(queue.TryPeek(out _));

        var guard = queue.ConfirmAsync(
            "Агент",
            SynGuard.DescribeBlock("run_powershell", """{"command":"Get-Process"}"""),
            "s1");

        Assert.False(guard.IsCompleted);
        Assert.True(queue.TryPeek(out var asked));
        Assert.Equal("run_powershell", asked.Info.ToolName);

        queue.CompleteCurrent(false);
        Assert.False(await guard);
    }

    [Fact]
    public async Task Cancelling_one_chat_leaves_the_other_chats_question_waiting()
    {
        var queue = Queue();
        var mine = queue.ConfirmAsync("Агент", Action("powershell"), "s1");
        var neighbour = queue.ConfirmAsync("Агент", Action("registry"), "s2");

        queue.CancelForSession("s1");

        // Прежде CancelAll() бил по всем, и соседний чат терял свой вопрос вместе с ходом.
        await Assert.ThrowsAsync<TaskCanceledException>(() => mine);
        Assert.False(neighbour.IsCompleted);

        Assert.True(queue.TryPeek(out var head));
        Assert.Equal("registry", head.Info.ToolName);

        queue.CompleteCurrent(true);
        Assert.True(await neighbour);
    }

    [Fact]
    public async Task The_order_of_the_rest_survives_a_dropped_session()
    {
        var queue = Queue();
        var first = queue.ConfirmAsync("Агент", Action("a"), "s1");
        var second = queue.ConfirmAsync("Агент", Action("b"), "s2");
        var third = queue.ConfirmAsync("Агент", Action("c"), "s1");
        var fourth = queue.ConfirmAsync("Агент", Action("d"), "s3");

        queue.CancelForSession("s1");

        Assert.True(queue.TryPeek(out var head));
        Assert.Equal("b", head.Info.ToolName);
        queue.CompleteCurrent(true);

        Assert.True(queue.TryPeek(out var next));
        Assert.Equal("d", next.Info.ToolName);
        queue.CompleteCurrent(false);

        await Assert.ThrowsAsync<TaskCanceledException>(() => first);
        await Assert.ThrowsAsync<TaskCanceledException>(() => third);
        Assert.True(await second);
        Assert.False(await fourth);
    }

    [Fact]
    public async Task Cancel_all_still_clears_everything()
    {
        // Закрытие программы и смена профиля по-прежнему сносят очередь целиком.
        var queue = Queue();
        var first = queue.ConfirmAsync("Агент", Action("a"), "s1");
        var second = queue.ConfirmAsync("Агент", Action("b"), "s2");

        queue.CancelAll();

        await Assert.ThrowsAsync<TaskCanceledException>(() => first);
        await Assert.ThrowsAsync<TaskCanceledException>(() => second);
        Assert.False(queue.TryPeek(out _));
    }

    [Fact]
    public async Task Approve_everything_mode_never_asks()
    {
        var settings = new AppSettings { ApprovalMode = ApprovalMode.AlwaysApprove };
        var queue = new ConfirmationQueue(() => settings);

        Assert.True(await queue.ConfirmAsync("Агент", Action("powershell"), "s1"));
        Assert.False(queue.TryPeek(out _));
    }

    [Fact]
    public async Task A_question_without_a_session_is_not_swept_up_by_a_cancel()
    {
        // Подтверждения умеют приходить и не из хода чата — их отмена одного чата не касается.
        var queue = Queue();
        var loose = queue.ConfirmAsync("Агент", Action("a"), sessionId: null);

        queue.CancelForSession("s1");

        Assert.False(loose.IsCompleted);
        queue.CompleteCurrent(true);
        Assert.True(await loose);
    }
}
