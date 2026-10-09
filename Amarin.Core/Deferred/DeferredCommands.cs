using System.Text.Json;
using Amarin.Tools;

namespace Amarin.Core;

/// <summary>
/// Команды и условия отложенных задач — через тот же шлюз, что и вызовы из чата.
/// </summary>
/// <remarks>
/// <para>
/// Команда выполняется с согласием, которое человек дал при постановке, но только если печать
/// задачи сходится (<see cref="DeferredBook.Verify"/>): команду из чужого архива или поправленную
/// руками программа не выполнит. Жёсткие запреты, режим «только чтение» и снимок перед записью
/// действуют как всегда — согласие отвечает только на вопрос.
/// </para>
/// <para>
/// Условие — скрипт только на чтение: проверка идёт под <see cref="ToolGate.ForceReadOnly"/> и
/// никого не спрашивает. Отвечает он последней строкой True или False; что-то другое — «не
/// выяснилось», и условие проверится в следующий раз.
/// </para>
/// </remarks>
internal static class DeferredCommands
{
    private const string PowerShell = "run_powershell";

    /// <summary>Сколько текста вывода команды хранить в задаче и показывать в чате.</summary>
    internal const int OutputKeep = 4000;

    public static async Task<DeferredOutcome> RunAsync(
        DeferredTask task,
        DeferredBook book,
        ToolRegistry tools,
        AppSettings settings,
        AuditLog? audit,
        Func<SessionUndoTracker> undo,
        CancellationToken cancellationToken)
    {
        if (task.Command is not { Length: > 0 } command || !book.Verify(task))
        {
            return new DeferredOutcome(false, Loc.Get("S.Deferred.Result.Unsealed"), 0m, null);
        }

        using var readOnly = task.ReadOnly ? ToolGate.ForceReadOnly() : null;
        var arguments = JsonSerializer.SerializeToElement(new { command, timeout_seconds = 600 });
        var run = await GateRun.RunAsync(
                tools,
                PowerShell,
                arguments,
                settings,
                (_, _) => Task.FromResult(new ConfirmationAnswer(true, ApprovalSource.Deferred)),
                new AuditOrigin(task.ChatId, task.Title, "deferred:" + task.Id),
                "deferred_" + Guid.NewGuid().ToString("N")[..8],
                undo,
                audit,
                cancellationToken)
            .ConfigureAwait(false);

        var output = Keep(run.Result.Output);
        return new DeferredOutcome(run.Result.Success, run.Result.Success ? null : output, 0m, null, output);
    }

    public static async Task<bool?> ProbeAsync(string script, ToolRegistry tools, AppSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return null;
        }

        using (ToolGate.ForceReadOnly())
        {
            var check = ToolGate.Check(PowerShell, JsonSerializer.SerializeToElement(new { command = script, timeout_seconds = 30 }), settings);
            if (check.Refusal is not null || check.Effect == ToolEffect.Write)
            {
                return null;
            }

            var result = await tools.ExecuteAsync(PowerShell, check.Arguments, cancellationToken).ConfigureAwait(false);
            return result.Success ? Verdict(result.Output) : null;
        }
    }

    /// <summary>Последняя непустая строка вывода — True или False; иначе null.</summary>
    internal static bool? Verdict(string? output)
    {
        var last = (output ?? "").Split('\n').Select(line => line.Trim()).LastOrDefault(line => line.Length > 0);
        return last?.ToLowerInvariant() switch
        {
            "true" or "$true" or "1" => true,
            "false" or "$false" or "0" => false,
            _ => null
        };
    }

    private static string Keep(string? output)
    {
        var text = (output ?? "").Trim();
        return text.Length <= OutputKeep ? text : text[..OutputKeep] + "…";
    }
}
