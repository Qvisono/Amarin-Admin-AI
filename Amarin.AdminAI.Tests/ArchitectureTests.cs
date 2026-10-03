using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Направление зависимостей: окно → логика → хранилища и сеть. Компилятор сторожит только
/// границу сборок; что внутри <c>Amarin.Core</c> нижние папки не знают верхних, сторожит этот класс.
/// </summary>
public sealed partial class ArchitectureTests
{
    /// <summary>
    /// Типы, которые ведут ход, агента, обновление или перенос данных. Хранилища, клиенты
    /// провайдеров, настройки и служебное их не знают: иначе «записать файл» или «спросить модель»
    /// тянули бы за собой весь движок, и проверить их отдельно стало бы нельзя.
    /// </summary>
    private static readonly string[] Engines =
    [
        "ChatEngine", "ChatEngineInfographic", "Agent", "AgentHost", "AgentRegistry", "AgentUiAdapter",
        "AgentTierRouter", "ChatTurnRouter", "RunningTurn", "FollowUpDirector", "ToolGate", "SynGuardChecker",
        "ConfirmationQueue", "ToolRegistry", "UpdateController", "UpdateExit", "DataBundleImporter",
        "DataBundleExporter", "Backups", "ProfileDataWiper", "ChatTitleGenerator", "ChatSummaryGenerator",
        "LanguageTranslator", "SpendGuard", "McpHost"
    ];

    [Fact]
    public void The_core_knows_nothing_of_the_window()
    {
        // Ссылка на WPF или на сборку приложения — и «логика без окна» перестала бы быть правдой:
        // такой тип не создать в тесте без окна, а на Linux — вовсе.
        var references = AssemblyReferences(typeof(ChatEngine).Assembly);

        string[] forbidden = ["PresentationFramework", "PresentationCore", "WindowsBase", "System.Xaml", "System.Windows.Forms", "Amarin Admin AI"];
        Assert.Empty(references.Intersect(forbidden, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("Storage")]
    [InlineData("Providers")]
    [InlineData("Settings")]
    [InlineData("Platform")]
    public void Lower_layers_do_not_know_the_engines(string folder)
    {
        var offenders = new List<string>();
        foreach (var path in SourceTree.CoreFolder(folder))
        {
            var code = CodeOnly(File.ReadAllText(path));
            offenders.AddRange(Engines
                .Where(engine => Regex.IsMatch(code, $@"\b{engine}\b"))
                .Select(engine => $"{folder}/{Path.GetFileName(path)} → {engine}"));
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    [Theory]
    [InlineData("UpdateMachine.cs")]
    [InlineData("UpdateView.cs")]
    [InlineData("UpdateState.cs")]
    [InlineData("UpdateEvents.cs")]
    public void The_update_rules_touch_no_network_disk_or_process(string file)
    {
        // Автомат обновлений проверяется без окна и без сети ровно потому, что его переходы чистые:
        // всё внешнее делают порты контроллера.
        var code = CodeOnly(File.ReadAllText(Path.Combine(SourceTree.CoreDirectory, "Updates", file)));

        Assert.DoesNotMatch(SideEffects(), code);
    }

    [Fact]
    public void The_core_and_the_app_carry_one_version()
    {
        // Версия живёт в csproj приложения, а Core берёт её оттуда. Разойдись они — отчёт об
        // аварии и архив данных назвали бы не ту версию, а «Что нового» искал бы не те заметки.
        Assert.Equal(RuntimeContext.AppVersion, UpdateChecker.VersionOf(typeof(ChatEngine).Assembly));
    }

    private static HashSet<string> AssemblyReferences(Assembly assembly)
    {
        using var stream = File.OpenRead(assembly.Location);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        return metadata.AssemblyReferences
            .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Код без комментариев и строк: упоминание в документации — не зависимость.</summary>
    private static string CodeOnly(string source) => Strings().Replace(Comments().Replace(source, ""), "\"\"");

    [GeneratedRegex(@"//.*?$|/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Multiline)]
    private static partial Regex Comments();

    [GeneratedRegex(@"@?""(?:[^""\\\n]|\\.)*""")]
    private static partial Regex Strings();

    /// <summary>Обращения к миру: статические File/Directory/Process, сеть, потоки, таймеры, часы.</summary>
    [GeneratedRegex(@"\b(?:HttpClient|File\.|Directory\.|Process\.|Task\.Run|Thread\.|ThreadPool|new\s+Timer|DateTime\.(?:Utc)?Now|Environment\.)")]
    private static partial Regex SideEffects();
}
