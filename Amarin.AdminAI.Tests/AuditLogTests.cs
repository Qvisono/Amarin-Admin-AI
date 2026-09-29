using System.Text;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Журнал аудита: только дописывается, битую строку пропускает, секретов не хранит, а в таблицу
/// выгружается так, что её нельзя превратить в формулу.
/// </summary>
public sealed class AuditLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-audit-" + Guid.NewGuid().ToString("N"));

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

    private static AuditOrigin Chat => new("chat-1", "Разговор", null);

    private void Write(AuditLog log, string tool, ToolEffect effect, AuditOutcome outcome, string args = "{}") =>
        log.Record(Chat, "c1", tool, args, effect, outcome, ApprovalSource.Human, AuditGuard.Safe, "итог");

    [Fact]
    public void Writes_and_refusals_are_kept_but_a_successful_read_is_not()
    {
        var log = new AuditLog(_root);

        Write(log, "registry", ToolEffect.Write, AuditOutcome.Ok);
        Write(log, "read_file", ToolEffect.Read, AuditOutcome.Ok);
        Write(log, "read_file", ToolEffect.Read, AuditOutcome.Refused);

        var entries = log.ReadAll();
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, entry => entry.Tool == "registry" && entry.Outcome == AuditOutcome.Ok);
        Assert.Contains(entries, entry => entry.Tool == "read_file" && entry.Outcome == AuditOutcome.Refused);
    }

    [Fact]
    public void Lines_are_only_appended_one_file_per_month()
    {
        var log = new AuditLog(_root);
        Write(log, "registry", ToolEffect.Write, AuditOutcome.Ok);
        var first = File.ReadAllText(Assert.Single(log.Files()));

        Write(log, "windows_service", ToolEffect.Write, AuditOutcome.Failed);
        var second = File.ReadAllText(Assert.Single(log.Files()));

        Assert.StartsWith(first, second, StringComparison.Ordinal);
        Assert.Equal(AuditLog.FileName(DateTime.Now), Path.GetFileName(log.Files()[0]));
    }

    [Fact]
    public void A_broken_line_is_skipped_without_losing_its_neighbours()
    {
        var log = new AuditLog(_root);
        Write(log, "registry", ToolEffect.Write, AuditOutcome.Ok);
        File.AppendAllText(log.Files()[0], "{\"tool\":\"обры", Encoding.UTF8);
        File.AppendAllText(log.Files()[0], "\n", Encoding.UTF8);
        Write(log, "filesystem", ToolEffect.Write, AuditOutcome.Ok);

        Assert.Equal(["filesystem", "registry"], log.ReadAll().Select(entry => entry.Tool).Order());
    }

    [Fact]
    public void Secrets_never_reach_the_file()
    {
        var log = new AuditLog(_root, () => ["sk-live-1234567890abcdef"]);

        Write(log, "run_powershell", ToolEffect.Write, AuditOutcome.Ok,
            "{\"script\":\"curl -H 'Authorization: Bearer sk-live-1234567890abcdef'\",\"password\":\"hunter2hunter2\"}");

        var text = File.ReadAllText(log.Files()[0]);
        Assert.DoesNotContain("sk-live-1234567890abcdef", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2hunter2", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Very_long_arguments_are_cut_and_marked()
    {
        var log = new AuditLog(_root);

        Write(log, "run_powershell", ToolEffect.Write, AuditOutcome.Ok,
            "{\"script\":\"" + new string('x', AuditLog.ArgsLimit * 2) + "\"}");

        var entry = Assert.Single(log.ReadAll());
        Assert.True(entry.ArgsTruncated);
        Assert.Equal(AuditLog.ArgsLimit, entry.Args.Length);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\")")]
    [InlineData("+cmd|' /C calc'!A0")]
    [InlineData("-2+3")]
    [InlineData("@SUM(A1)")]
    public void A_cell_that_would_be_a_formula_is_neutralised(string value)
    {
        // Аргументы пишет модель, а журнал откроют в Excel.
        var cell = AuditLog.Cell(value);

        Assert.DoesNotMatch("^\"?[=+\\-@]", cell);
    }

    [Fact]
    public void Csv_opens_in_excel_with_cyrillic_intact_and_quotes_escaped()
    {
        var csv = AuditLog.ToCsv(
        [
            new AuditEntry
            {
                Id = "1",
                Time = new DateTime(2026, 9, 1, 10, 0, 0),
                ChatTitle = "Чат, с запятой",
                Tool = "registry",
                Args = "{\"path\":\"HKCU\\\\X\"}",
                Outcome = AuditOutcome.Refused,
                ApprovedBy = ApprovalSource.Human,
                Guard = AuditGuard.Flagged
            }
        ]);

        Assert.StartsWith("﻿", csv, StringComparison.Ordinal);
        Assert.Contains("\"Чат, с запятой\"", csv, StringComparison.Ordinal);
        Assert.Contains("\"{\"\"path\"\"", csv, StringComparison.Ordinal);
        Assert.Contains(",refused,human,flagged,", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void Merging_a_journal_twice_adds_nothing_the_second_time()
    {
        var source = new AuditLog(Path.Combine(_root, "a"));
        Write(source, "registry", ToolEffect.Write, AuditOutcome.Ok);
        Write(source, "filesystem", ToolEffect.Write, AuditOutcome.Refused);
        var target = new AuditLog(Path.Combine(_root, "b"));

        Assert.Equal(2, target.Merge(source.ReadAll()));
        Assert.Equal(0, target.Merge(source.ReadAll()));
        Assert.Equal(2, target.ReadAll().Count);
    }

    [Fact]
    public void Deleting_every_chat_leaves_the_journal_alone()
    {
        var log = new AuditLog(_root);
        Write(log, "registry", ToolEffect.Write, AuditOutcome.Ok);
        var store = new ChatStore(_root);
        var session = store.CreateNew("grok-4-6");
        session.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "u", Text = "x" });
        store.Save(session);
        store.Flush();

        store.DeleteAll();

        Assert.Single(log.ReadAll());
    }

    [Fact]
    public void An_import_only_adds_to_the_journal_even_when_it_replaces_everything_else()
    {
        var source = Path.Combine(_root, "source");
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(Path.Combine(source, "chats"));
        Directory.CreateDirectory(Path.Combine(target, "chats"));
        var archive = Path.Combine(_root, "bundle" + DataBundle.FileExtension);

        Write(new AuditLog(source), "registry", ToolEffect.Write, AuditOutcome.Ok);
        var mine = new AuditLog(target);
        Write(mine, "windows_service", ToolEffect.Write, AuditOutcome.Refused);

        new DataBundleExporter(source).Write(archive, DataCategory.All);
        var result = new DataBundleImporter(target).Apply(archive, DataCategory.All, DataImportMode.Replace);

        Assert.True(result.Ok, result.Error.ToString());
        Assert.Equal(["registry", "windows_service"], mine.ReadAll().Select(entry => entry.Tool).Order());
    }
}
