using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Цепочка хешей журнала аудита (1.37.0): подменённая, удалённая или вставленная посреди файла
/// строка видна, а импорт архива, шифрование и параллельная запись цепочку не рвут. И копия в
/// журнал Windows: без аргументов и только для своих действий.
/// </summary>
public sealed class AuditChainTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-audit-chain-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Временная папка, уборка по возможности.
        }
    }

    private static void Write(AuditLog log, string tool, string args = """{"path":"secret-path"}""") =>
        log.Record(new AuditOrigin("chat-1", "Topic of the chat", "a3"), "c1", tool, args,
            ToolEffect.Write, AuditOutcome.Ok, ApprovalSource.Human, AuditGuard.Safe, "done");

    private string File0(AuditLog log) => Assert.Single(log.Files());

    private static List<string> Lines(string file) =>
        File.ReadAllLines(file).Where(line => line.Length > 0).ToList();

    [Fact]
    public void Every_line_names_the_hash_of_the_line_before_it()
    {
        var log = new AuditLog(_root);
        Write(log, "registry");
        Write(log, "windows_service");
        Write(log, "write_file");

        var lines = Lines(File0(log));
        var entries = lines.Select(line => AuditLog.Parse(line)!).ToList();
        Assert.Equal(AuditLog.ChainStart, entries[0].Prev);
        Assert.Equal(AuditLog.LinkOf(lines[0]), entries[1].Prev);
        Assert.Equal(AuditLog.LinkOf(lines[1]), entries[2].Prev);

        var report = log.VerifyChain();
        Assert.True(report.Started);
        Assert.True(report.Intact);
    }

    [Fact]
    public void An_edited_line_breaks_the_chain_at_the_line_after_it()
    {
        var log = new AuditLog(_root);
        Write(log, "registry");
        Write(log, "windows_service");
        Write(log, "write_file");

        var file = File0(log);
        var lines = Lines(file);
        lines[1] = lines[1].Replace("windows_service", "read_file", StringComparison.Ordinal);
        File.WriteAllLines(file, lines);

        var report = log.VerifyChain();
        Assert.False(report.Intact);
        Assert.Equal(Path.GetFileNameWithoutExtension(file), report.BrokenFile);
        Assert.Equal(3, report.BrokenLine);
    }

    [Fact]
    public void A_deleted_line_breaks_the_chain()
    {
        var log = new AuditLog(_root);
        Write(log, "registry");
        Write(log, "windows_service");
        Write(log, "write_file");

        var file = File0(log);
        var lines = Lines(file);
        lines.RemoveAt(1);
        File.WriteAllLines(file, lines);

        Assert.Equal(2, log.VerifyChain().BrokenLine);
    }

    [Fact]
    public void A_deleted_first_line_breaks_the_chain_too()
    {
        var log = new AuditLog(_root);
        Write(log, "registry");
        Write(log, "windows_service");

        var file = File0(log);
        File.WriteAllLines(file, Lines(file).Skip(1));

        Assert.Equal(1, log.VerifyChain().BrokenLine);
    }

    [Fact]
    public void Lines_from_before_the_chain_are_not_judged()
    {
        // Журнал прежней версии: строки без звена. Новая строка цепляется к последней из них.
        var log = new AuditLog(_root);
        var file = Path.Combine(log.Directory, AuditLog.FileName(DateTime.Now));
        Directory.CreateDirectory(log.Directory);
        File.WriteAllText(file,
            """{"id":"old1","time":"2026-10-01T10:00:00","tool":"registry","args":"{}","outcome":"ok"}""" + "\n" +
            """{"id":"old2","time":"2026-10-01T10:01:00","tool":"registry","args":"{}","outcome":"ok"}""" + "\n");

        Write(log, "write_file");
        Assert.True(log.VerifyChain() is { Started: true, Intact: true });
    }

    [Fact]
    public void A_line_written_after_a_rollback_to_an_older_version_does_not_break_the_chain()
    {
        // «Вернуть v1.36» пишет строки без звена посреди цепочки; вернувшись, 1.37 цепляется к ним.
        var log = new AuditLog(_root);
        Write(log, "registry");
        var file = File0(log);
        File.AppendAllText(file,
            """{"id":"old","time":"2026-10-01T10:02:00","tool":"registry","args":"{}","outcome":"ok"}""" + "\n");
        Write(log, "write_file");

        Assert.True(log.VerifyChain() is { Started: true, Intact: true });
    }

    [Fact]
    public void A_line_without_a_link_inserted_between_links_breaks_the_chain_after_it()
    {
        var log = new AuditLog(_root);
        Write(log, "registry");
        Write(log, "write_file");

        var file = File0(log);
        var lines = Lines(file);
        lines.Insert(1, """{"id":"fake","time":"2026-10-01T10:02:00","tool":"registry","args":"{}","outcome":"ok"}""");
        File.WriteAllLines(file, lines);

        Assert.Equal(3, log.VerifyChain().BrokenLine);
    }

    [Fact]
    public void A_journal_without_any_chain_is_reported_as_not_started()
    {
        var log = new AuditLog(_root);
        Assert.False(log.VerifyChain().Started);
        Assert.True(log.VerifyChain().Intact);
    }

    [Fact]
    public void Imported_lines_hook_onto_the_local_tail_whatever_link_they_brought()
    {
        var source = new AuditLog(Path.Combine(_root, "source"));
        Write(source, "registry");
        Write(source, "windows_service");

        var target = new AuditLog(Path.Combine(_root, "target"));
        Write(target, "write_file");

        Assert.Equal(2, target.Merge(source.ReadAll()));
        Assert.True(target.VerifyChain().Intact);
        Assert.Equal(3, target.ReadAll().Count);
    }

    [Fact]
    public void Encrypted_and_open_lines_chain_on_their_plain_text()
    {
        var encrypt = true;
        var log = new AuditLog(_root) { Encrypt = () => encrypt };
        Write(log, "registry");
        Write(log, "windows_service");
        encrypt = false;
        Write(log, "write_file");

        var lines = Lines(File0(log));
        Assert.StartsWith("!amrn-dpapi1:", lines[0], StringComparison.Ordinal);
        Assert.False(lines[2].StartsWith('!'));
        Assert.True(log.VerifyChain().Intact);
    }

    [Fact]
    public void Parallel_records_keep_one_unbroken_chain()
    {
        var log = new AuditLog(_root);
        Parallel.For(0, 40, index => Write(log, "tool_" + index));

        Assert.Equal(40, Lines(File0(log)).Count);
        Assert.True(log.VerifyChain().Intact);
    }

    [Fact]
    public void A_torn_last_line_stays_its_own_line_and_the_chain_goes_on()
    {
        var log = new AuditLog(_root);
        Write(log, "registry");
        File.AppendAllText(File0(log), """{"id":"torn","tool":"reg""");

        Write(log, "write_file");

        Assert.Equal(2, log.ReadAll().Count);
        Assert.True(log.VerifyChain().Intact);
    }

    [Fact]
    public void Only_own_records_go_to_the_mirror_with_the_hash_of_their_line()
    {
        var mirrored = new List<(AuditEntry Entry, string Hash)>();
        var log = new AuditLog(_root) { Mirror = (entry, hash) => mirrored.Add((entry, hash)) };
        Write(log, "registry");

        var (entry, hash) = Assert.Single(mirrored);
        Assert.Equal("registry", entry.Tool);
        Assert.Equal(AuditLog.LinkOf(Lines(File0(log))[0]), hash);

        // Импорт чужого журнала — действия другой машины: в журнал Windows этой они не идут.
        var source = new AuditLog(Path.Combine(_root, "source"));
        Write(source, "windows_service");
        log.Merge(source.ReadAll());
        Assert.Single(mirrored);
    }

    [Fact]
    public void A_failing_mirror_does_not_lose_the_line()
    {
        var log = new AuditLog(_root) { Mirror = (_, _) => throw new InvalidOperationException("event log down") };
        Write(log, "registry");

        Assert.Single(log.ReadAll());
    }

    [Fact]
    public void The_windows_event_carries_no_arguments_and_no_chat_title()
    {
        var log = new AuditLog(_root);
        Write(log, "registry", """{"path":"C:\\Users\\someone\\secret-path"}""");
        var entry = Assert.Single(log.ReadAll());

        var text = AuditEvents.Format(entry, "abc123");

        Assert.Contains("Tool: registry", text, StringComparison.Ordinal);
        Assert.Contains("Agent: a3", text, StringComparison.Ordinal);
        Assert.Contains("Line hash: abc123", text, StringComparison.Ordinal);
        Assert.Contains(entry.Id, text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-path", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Topic of the chat", text, StringComparison.Ordinal);
        Assert.DoesNotContain("done", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AuditOutcome.Ok, 1000, false)]
    [InlineData(AuditOutcome.Failed, 1001, true)]
    [InlineData(AuditOutcome.Refused, 1002, true)]
    [InlineData(AuditOutcome.Cancelled, 1003, true)]
    public void The_event_id_and_level_follow_the_outcome(AuditOutcome outcome, int id, bool warning)
    {
        Assert.Equal(id, AuditEvents.EventId(outcome));
        Assert.Equal(warning, AuditEvents.IsWarning(outcome));
    }
}
