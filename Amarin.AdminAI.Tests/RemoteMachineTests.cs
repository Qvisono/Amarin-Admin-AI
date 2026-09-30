using System.Text;
using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Удалённые машины (C10). Главные обещания: пароль не попадает ни в командную строку, ни в
/// файл открытым текстом; инструмент, который выполнился бы здесь, для удалённой цели отклоняется;
/// снимок и пробный прогон не делаются для машины, которую они не видят.
/// </summary>
public sealed class RemoteMachineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-machines-" + Guid.NewGuid().ToString("N"));

    public RemoteMachineTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static RemoteMachine WinRm(string? password = "Secret-Pass-42") => new()
    {
        Id = "m1",
        Name = "Сервер",
        Address = "srv-01",
        User = "admin",
        ProtectedPassword = password is null ? null : DataProtector.Protect(password)
    };

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    // ───────────────────────── обёртка скрипта ─────────────────────────

    [Fact]
    public void The_script_travels_inside_as_base64_and_the_password_does_not_travel_at_all()
    {
        var script = "Get-Service | Where-Object Status -eq 'Running'";
        var wrapped = RemoteScript.Wrap(script, WinRm());

        Assert.Contains("Invoke-Command -ComputerName 'srv-01'", wrapped, StringComparison.Ordinal);
        Assert.Contains("[Console]::In.ReadLine()", wrapped, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret-Pass-42", wrapped, StringComparison.Ordinal);
        Assert.DoesNotContain(script, wrapped, StringComparison.Ordinal);

        var inner = wrapped.Split('\'')[1];
        Assert.Equal(script, Encoding.Unicode.GetString(Convert.FromBase64String(inner)));
    }

    [Fact]
    public void Ssh_uses_the_key_and_reads_nothing_from_stdin()
    {
        var machine = WinRm(null);
        machine.Method = RemoteMethod.Ssh;
        machine.KeyFile = "C:\\Users\\me\\.ssh\\id_ed25519";

        var wrapped = RemoteScript.Wrap("Get-Date", machine);

        Assert.Contains("-HostName 'srv-01' -UserName 'admin' -KeyFilePath 'C:\\Users\\me\\.ssh\\id_ed25519'", wrapped, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadLine", wrapped, StringComparison.Ordinal);
        Assert.Null(RemoteScript.Input(machine));
    }

    [Fact]
    public async Task A_line_given_on_stdin_reaches_the_script_and_never_its_command_line()
    {
        // Так пароль уходит дочернему powershell.exe: проверяем, что с -EncodedCommand ввод
        // действительно читается, иначе WinRM молча получал бы пустой пароль.
        var result = await PowerShellProcessRunner.RunAsync(
            "$line = [Console]::In.ReadLine(); \"got:$line\"", 30, CancellationToken.None, input: "Secret-Pass-42");

        Assert.True(result.Success, result.Output);
        Assert.Contains("got:Secret-Pass-42", result.Output, StringComparison.Ordinal);
    }

    // ───────────────────────── шлюз ─────────────────────────

    [Fact]
    public void Only_powershell_and_machine_free_tools_reach_a_remote_target()
    {
        var settings = new AppSettings { ApprovalMode = ApprovalMode.AlwaysApprove };
        using var _ = ExecutionTarget.Push(WinRm());

        var registry = ToolGate.Check("registry", Args(new { action = "read", path = "HKLM\\SOFTWARE" }), settings);
        var script = ToolGate.Check("run_powershell", Args(new { command = "Get-Date" }), settings);
        var search = ToolGate.Check("web_search", Args(new { query = "x" }), settings);

        Assert.Contains("run_powershell", registry.Refusal, StringComparison.Ordinal);
        Assert.Null(script.Refusal);
        Assert.Null(search.Refusal);
    }

    [Fact]
    public void A_remote_write_is_asked_about_with_the_machine_named_and_no_snapshot()
    {
        using var _ = ExecutionTarget.Push(WinRm());

        var check = ToolGate.Check("run_powershell", Args(new { command = "Stop-Service Spooler" }), new AppSettings());

        Assert.NotNull(check.Question);
        Assert.Equal("Сервер", check.Question.Target);
        Assert.False(check.NeedsSnapshot, "снимок снял бы этот ПК, а меняется другой");
    }

    [Fact]
    public async Task No_dry_run_is_made_for_a_remote_target()
    {
        using var _ = ExecutionTarget.Push(WinRm());

        Assert.Null(await WhatIfProbe.RunAsync("run_powershell", Args(new { command = "Stop-Service Spooler" }), CancellationToken.None));
    }

    [Fact]
    public void The_model_is_told_where_its_commands_go()
    {
        Assert.Equal("", RemoteBriefing.For(null));
        var text = RemoteBriefing.For(WinRm());
        Assert.Contains("srv-01", text, StringComparison.Ordinal);
        Assert.Contains("run_powershell", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_audit_line_names_the_machine()
    {
        var audit = new AuditLog(_root);
        using (ExecutionTarget.Push(WinRm()))
        {
            audit.Record(null, "c1", "run_powershell", "{}", ToolEffect.Write, AuditOutcome.Ok, ApprovalSource.Human, AuditGuard.Off, "ok");
        }

        Assert.Equal("Сервер (srv-01)", Assert.Single(audit.ReadAll()).Target);
    }

    // ───────────────────────── список машин ─────────────────────────

    [Fact]
    public void The_password_is_stored_encrypted_and_read_back()
    {
        var book = new MachineBook(_root);
        book.Save([WinRm()]);

        var file = File.ReadAllText(Path.Combine(_root, MachineBook.FileName));
        Assert.DoesNotContain("Secret-Pass-42", file, StringComparison.Ordinal);
        Assert.Equal("Secret-Pass-42", book.Find("m1")!.Password);
        Assert.DoesNotContain("\"password\"", file, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("srv-01'; Remove-Item C:\\", "admin")]
    [InlineData("srv 01", "admin")]
    [InlineData("srv-01", "admin'; calc")]
    [InlineData("srv-01", "$env:x")]
    [InlineData("srv-01", "")]
    public void Anything_that_is_not_a_host_and_an_account_is_refused(string address, string user)
    {
        var machine = WinRm();
        machine.Address = address;
        machine.User = user;

        Assert.NotNull(MachineBook.Validate(machine));
    }

    [Fact]
    public void A_domain_account_and_an_ip_are_fine()
    {
        var machine = WinRm();
        machine.Address = "10.0.0.15";
        machine.User = "CORP\\admin";

        Assert.Null(MachineBook.Validate(machine));
    }

    [Fact]
    public void Machines_never_travel_in_the_data_archive_and_go_with_the_profile()
    {
        Assert.Equal(DataCategory.None, DataBundle.CategoryOf(MachineBook.FileName));
        Assert.Contains(MachineBook.FileName, ProfileDataWiper.DefaultProfileFiles);
    }

    [Fact]
    public void A_chat_without_the_field_targets_this_pc() =>
        Assert.Null(JsonSerializer.Deserialize<ChatSession>("{\"id\":\"x\"}", AppJson.Options)!.TargetMachineId);
}
