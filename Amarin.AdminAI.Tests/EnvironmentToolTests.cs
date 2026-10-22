using System.Text.Json;
using Amarin.Tools;
using Microsoft.Win32;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Переменные среды и PATH: разбор без диска, правка в своём разделе реестра — настоящие
/// переменные пользователя и машины тест не трогает и рассылку не шлёт.
/// </summary>
public sealed class EnvironmentToolTests : IDisposable
{
    private readonly string _root = @"Software\AmarinAdminAITests\" + Guid.NewGuid().ToString("N");
    private readonly List<string> _announced = [];
    private readonly EnvironmentStore _store;

    public EnvironmentToolTests()
    {
        _store = new EnvironmentStore(@"HKCU\" + _root + @"\User", @"HKCU\" + _root + @"\Machine",
            (_, names) => _announced.AddRange(names));
    }

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private static string Expand(string value) => value.Replace("%TOOLS%", @"C:\Tools", StringComparison.OrdinalIgnoreCase);

    private static PathExistence Exists(string value) =>
        value.Contains("missing", StringComparison.OrdinalIgnoreCase) ? PathExistence.Missing : PathExistence.Exists;

    private EnvironmentTool Tool(params string[] secrets) => new(() => secrets, _store, Exists);

    private string Run(EnvironmentTool tool, object arguments, bool success = true)
    {
        var result = tool.ExecuteAsync(Args(arguments)).GetAwaiter().GetResult();
        Assert.True(result.Success == success, result.Output);
        return result.Output;
    }

    [Fact]
    public void Analysis_finds_duplicates_across_scopes_missing_folders_and_undefined_variables()
    {
        var entries = PathAnalysis.Analyze(
            @"C:\Windows\system32;C:\Tools\;C:\Missing",
            @"%TOOLS%;;relative\bin;%NOT_DEFINED%\bin;C:\Apps;",
            Expand,
            Exists);

        Assert.Contains("duplicate of machine #2", entries.Single(entry => entry.Scope == EnvironmentScope.User && entry.Number == 1).Problems);
        Assert.Contains("folder does not exist", entries.Single(entry => entry.Raw == @"C:\Missing").Problems);
        Assert.Contains("empty entry", entries.Single(entry => entry.Scope == EnvironmentScope.User && entry.Number == 2).Problems);
        Assert.Contains("relative path", entries.Single(entry => entry.Raw == @"relative\bin").Problems);
        Assert.Contains("undefined variable %NOT_DEFINED%", entries.Single(entry => entry.Raw == @"%NOT_DEFINED%\bin").Problems);

        // «;» в самом конце — не пустая запись: так пишут установщики, и это никому не мешает.
        Assert.Equal(5, entries.Count(entry => entry.Scope == EnvironmentScope.User));
        Assert.Empty(entries.Single(entry => entry.Raw == @"C:\Apps").Problems);
    }

    [Fact]
    public void Cleaning_keeps_what_cannot_be_proven_missing()
    {
        static PathExistence Probe(string value) => value switch
        {
            @"C:\Gone" => PathExistence.Missing,
            @"\\server\tools" or @"E:\usb" => PathExistence.Unknown,
            _ => PathExistence.Exists
        };

        var (value, removed) = PathAnalysis.Clean(
            EnvironmentScope.User,
            @"C:\Apps;;C:\Gone;C:\Apps\;\\server\tools;E:\usb;C:\Windows",
            @"C:\Windows",
            item => item,
            Probe);

        Assert.Equal(@"C:\Apps;\\server\tools;E:\usb", value);
        Assert.Equal(["(empty)", @"C:\Gone", @"C:\Apps\", @"C:\Windows"], removed);
    }

    [Fact]
    public void Adding_and_removing_compare_expanded_folders_without_a_trailing_slash()
    {
        Assert.True(PathAnalysis.Contains(@"%TOOLS%;C:\Other", @"C:\Tools\", Expand));
        Assert.Equal(@"C:\New;C:\A", PathAnalysis.Add(@"C:\A;", @"C:\New", atStart: true));

        var (value, removed) = PathAnalysis.Remove(@"%TOOLS%;C:\A;C:\Tools\", @"C:\Tools", Expand);
        Assert.Equal(@"C:\A", value);
        Assert.Equal(2, removed);
    }

    [Fact]
    public void Path_of_the_process_is_machine_then_user_and_other_variables_take_the_user_value()
    {
        var machine = new EnvironmentVariable("Path", @"C:\M", false);
        var user = new EnvironmentVariable("Path", @"C:\U", false);

        Assert.Equal(@"C:\M;C:\U", EnvironmentStore.Effective("Path", machine, user));
        Assert.Equal("user", EnvironmentStore.Effective("X", new EnvironmentVariable("X", "machine", false), new EnvironmentVariable("X", "user", false)));
        Assert.Equal("machine", EnvironmentStore.Effective("X", new EnvironmentVariable("X", "machine", false), null));
    }

    [Fact]
    public void A_change_keeps_the_expandable_type_and_announces_only_the_changed_name()
    {
        var tool = Tool();
        Run(tool, new { action = "set", name = "AMARIN_TEST_HOME", value = @"%USERPROFILE%\x" });

        var stored = _store.Get(EnvironmentScope.User, "AMARIN_TEST_HOME");
        Assert.NotNull(stored);
        Assert.True(stored.Expandable);
        Assert.Equal(@"%USERPROFILE%\x", stored.Value);
        Assert.Equal(["AMARIN_TEST_HOME"], _announced);

        Run(tool, new { action = "set", name = "AMARIN_TEST_HOME", value = "plain" });
        Assert.True(_store.Get(EnvironmentScope.User, "AMARIN_TEST_HOME")!.Expandable);

        Run(tool, new { action = "delete", name = "AMARIN_TEST_HOME" });
        Assert.Null(_store.Get(EnvironmentScope.User, "AMARIN_TEST_HOME"));
        Run(tool, new { action = "delete", name = "AMARIN_TEST_HOME" }, success: false);
    }

    [Fact]
    public void Path_is_edited_by_entries_and_a_variable_turns_it_expandable()
    {
        _store.Write(EnvironmentScope.User, "Path", @"C:\A;C:\B", expandable: false);
        var tool = Tool();

        Run(tool, new { action = "path_add", path = @"%TOOLS%\bin" });
        var path = _store.Get(EnvironmentScope.User, "Path")!;
        Assert.Equal(@"C:\A;C:\B;%TOOLS%\bin", path.Value);
        Assert.True(path.Expandable);

        Assert.Contains("already contains", Run(tool, new { action = "path_add", path = @"C:\A\" }), StringComparison.Ordinal);
        Run(tool, new { action = "path_add", path = @"relative\bin" }, success: false);
        Run(tool, new { action = "path_add", path = @"C:\A;C:\Evil" }, success: false);

        Run(tool, new { action = "path_remove", path = @"C:\B" });
        Assert.Equal(@"C:\A;%TOOLS%\bin", _store.Get(EnvironmentScope.User, "Path")!.Value);
        Run(tool, new { action = "path_remove", path = @"C:\Never" }, success: false);
    }

    [Fact]
    public void Cleaning_through_the_tool_writes_only_when_there_is_something_to_remove()
    {
        _store.Write(EnvironmentScope.Machine, "Path", @"C:\Shared", expandable: true);
        _store.Write(EnvironmentScope.User, "Path", @"C:\Shared;C:\missing\x;C:\Mine", expandable: true);
        var tool = Tool();

        var output = Run(tool, new { action = "path_clean" });
        Assert.Contains(@"C:\missing\x", output, StringComparison.Ordinal);
        Assert.Equal(@"C:\Mine", _store.Get(EnvironmentScope.User, "Path")!.Value);

        _announced.Clear();
        Assert.Contains("nothing to clean", Run(tool, new { action = "path_clean" }), StringComparison.Ordinal);
        Assert.Empty(_announced);
    }

    [Fact]
    public void Values_that_look_like_secrets_never_reach_the_model()
    {
        _store.Write(EnvironmentScope.User, "SERVICE_TOKEN", "abc", expandable: false);
        _store.Write(EnvironmentScope.User, "PLAIN_NAME", "sk-or-v1-" + new string('a', 40), expandable: false);
        _store.Write(EnvironmentScope.User, "PROGRAM_KNOWN", "known-secret-value-1234", expandable: false);
        _store.Write(EnvironmentScope.User, "VISIBLE", "hello", expandable: false);
        var tool = Tool("known-secret-value-1234");

        var list = Run(tool, new { action = "list", scope = "user" });
        Assert.DoesNotContain("abc", list, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-or-v1-", list, StringComparison.Ordinal);
        Assert.DoesNotContain("known-secret-value-1234", list, StringComparison.Ordinal);
        Assert.Contains("hello", list, StringComparison.Ordinal);

        var get = Run(tool, new { action = "get", name = "SERVICE_TOKEN", scope = "user" });
        Assert.Contains("<hidden", get, StringComparison.Ordinal);
    }

    [Fact]
    public void The_check_reports_types_lengths_and_a_setx_cut()
    {
        _store.Write(EnvironmentScope.User, "Path", @"%TOOLS%;C:\" + new string('a', PathAnalysis.SetxLimit - 11), expandable: false);
        var output = Run(Tool(), new { action = "path_check" });

        Assert.Contains("REG_SZ", output, StringComparison.Ordinal);
        Assert.Contains("does not expand", output, StringComparison.Ordinal);
        Assert.Contains($"exactly {PathAnalysis.SetxLimit} chars", output, StringComparison.Ordinal);
        Assert.Contains("Combined expanded PATH", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Hard_refusals_hold_in_the_tool_too()
    {
        var tool = Tool();

        Run(tool, new { action = "set", name = "ComSpec", value = "x" }, success: false);
        Run(tool, new { action = "set", name = "Path", value = "x" }, success: false);
        Run(tool, new { action = "list", scope = "everyone" }, success: false);
        Assert.Empty(_announced);
    }

    [Fact]
    public void A_rollback_of_an_environment_key_announces_its_values()
    {
        RegistryChange[] changes =
        [
            new(RegistryChangeKind.SetValue, EnvironmentStore.UserKey, "Path"),
            new(RegistryChangeKind.DeleteValue, EnvironmentStore.MachineKey, "JAVA_HOME"),
            new(RegistryChangeKind.SetValue, @"HKCU\Software\Other", "Path"),
            new(RegistryChangeKind.SetValue, EnvironmentStore.UserKey, "path")
        ];

        Assert.Equal(["Path", "JAVA_HOME"], EnvironmentStore.RestoredNames(changes));
    }
}
