using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Сторож имён, по которым тесты добираются до закрытых членов программы отражением.
/// </summary>
/// <remarks>
/// <para>
/// Оконные тесты зовут приватное окна строкой (<c>Call(window, "OpenChat")</c>,
/// <c>Get&lt;T&gt;(window, "_session")</c>). Переименуй или вынеси такой член — компилятор промолчит,
/// а тест упадёт только на Windows, когда поднимется окно, и тридцать минут спустя. Этот тест идёт
/// без окна, в том числе на Linux, и сверяет каждое такое имя с метаданными сборок.
/// </para>
/// <para>
/// Цель вызова угадывается по исходнику: <c>typeof(X).GetField("…")</c> — тип X; помощник, первым
/// аргументом которого стоит окно (<c>window</c>, <c>harness.Window</c>), — <see cref="MainWindow"/>;
/// всё остальное — «есть ли такой член хоть у одного типа программы». Это сторож, а не компилятор:
/// лучшее лечение — перевести тест на <c>internal</c>-вход, тогда имя проверит сборка.
/// </para>
/// </remarks>
public sealed class ReflectionNameGuardTests
{
    /// <summary>Помощник вида <c>Call(цель, "Имя", …)</c>, <c>Get&lt;T&gt;(цель, "_поле")</c>.</summary>
    private static readonly Regex HelperCall = new(
        @"(?<![\w.])(?:Call|Invoke|Get|Set|Field)(?:<[^<>]*(?:<[^<>]*>[^<>]*)*>)?\(\s*((?:[^,()""]|\([^()]*\))+?)\s*,\s*""([A-Za-z_][A-Za-z0-9_]*)""",
        RegexOptions.Compiled);

    /// <summary><c>typeof(Тип).GetField("имя"</c> и родственные.</summary>
    private static readonly Regex TypedLookup = new(
        @"typeof\(\s*([A-Za-z0-9_.]+)\s*\)\s*\.\s*(?:GetField|GetMethod|GetProperty|GetMember|GetMethods)\(\s*""([A-Za-z_][A-Za-z0-9_]*)""",
        RegexOptions.Compiled);

    /// <summary><c>.GetType().GetField("имя"</c> — тип известен только во время выполнения.</summary>
    private static readonly Regex RuntimeLookup = new(
        @"(\w+)\.GetType\(\)\s*\.\s*(?:GetField|GetMethod|GetProperty)\(\s*""([A-Za-z_][A-Za-z0-9_]*)""",
        RegexOptions.Compiled);

    /// <summary>
    /// Члены чужих типов, до которых тесты добираются по делу: внутренности WPF, которые обходит
    /// <c>CursorGuard</c>. Сторожить их нечем — они не в программе.
    /// </summary>
    private static readonly HashSet<string> Foreign = new(StringComparer.Ordinal) { "HideCursor" };

    private sealed record Use(string File, int Line, string? Target, string Name);

    [Fact]
    public void Every_member_reached_by_name_still_exists()
    {
        var uses = Uses().ToList();
        var missing = uses
            .Where(use => !Exists(use))
            .Select(use => $"{use.File}:{use.Line} {use.Target ?? "?"}.{use.Name}")
            .ToList();

        Assert.True(
            missing.Count == 0,
            "Тесты зовут отражением члены, которых больше нет (переименованы или вынесены). " +
            "Переведите тест на internal-вход или поправьте имя:" + Environment.NewLine +
            string.Join(Environment.NewLine, missing));
    }

    [Theory]
    [InlineData("Call(window, \"OpenChat\", id);", "MainWindow", "OpenChat")]
    [InlineData("var s = Get<ChatSession>(harness.Window, \"_session\");", "MainWindow", "_session")]
    [InlineData("typeof(Agent).GetMethod(\"BuildMachinePathsPrompt\", Hidden)", "Agent", "BuildMachinePathsPrompt")]
    [InlineData("window.GetType().GetField(\"_session\", Hidden)!.SetValue(window, x);", "MainWindow", "_session")]
    [InlineData("Set(target, \"_field\", null);", null, "_field")]
    public void The_guard_reads_every_form_of_reflection_the_tests_use(string line, string? target, string name)
    {
        // Без этой проверки сломанное регулярное выражение превратило бы сторожа в вечно зелёный
        // тест, который не смотрит ни на что.
        var use = Assert.Single(Extract("sample.cs", [line]));
        Assert.Equal(target, use.Target);
        Assert.Equal(name, use.Name);
    }

    [Theory]
    [InlineData("AgentPlanSettings.Set(settings, \"lite\", true);")]
    [InlineData("Assert.Null(typeof(AppServices).GetProperty(\"Templates\"));")]
    [InlineData("var tool = defs.Single(d => d.Function.Name == \"run_powershell\");")]
    public void Ordinary_calls_and_absence_checks_are_not_mistaken_for_reflection(string line) =>
        Assert.Empty(Extract("sample.cs", [line]));

    [Fact]
    public void A_member_that_is_gone_is_reported()
    {
        Assert.False(Exists(new Use("sample.cs", 1, nameof(MainWindow), "NoSuchMemberAnywhere")));
        Assert.True(Exists(new Use("sample.cs", 1, nameof(MainWindow), "OnWindowLoaded")));
    }

    [Fact]
    public void The_source_tree_sees_every_working_project()
    {
        // Проверки, читающие «все исходники», обязаны видеть и Core, и окно: иначе вынос файла в
        // другой проект молча убирал бы его из-под проверки.
        var files = SourceTree.SourceFiles().Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("ToolGate.cs", files);
        Assert.Contains("MainWindow.xaml", files);
        Assert.True(File.Exists(SourceTree.Find("ToolGate.cs")));
        Assert.True(File.Exists(SourceTree.ProjectFile(Path.Combine("UI", "MainWindow.xaml"))));
    }

    private static IEnumerable<Use> Uses() =>
        Directory.EnumerateFiles(SourceTree.TestsDirectory, "*.cs")
            .Where(file => Path.GetFileName(file) != nameof(ReflectionNameGuardTests) + ".cs")
            .SelectMany(file => Extract(Path.GetFileName(file), File.ReadAllLines(file)));

    private static IEnumerable<Use> Extract(string file, IReadOnlyList<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];

            // Проверка «такого члена больше нет» (Assert.Null(typeof(X).GetProperty("…"))) —
            // законный поиск отсутствующего имени, а не обращение к нему.
            if (line.Contains("Assert.Null(", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match match in HelperCall.Matches(line))
            {
                yield return new Use(file, i + 1, TargetOf(match.Groups[1].Value), match.Groups[2].Value);
            }

            foreach (Match match in TypedLookup.Matches(line))
            {
                yield return new Use(file, i + 1, match.Groups[1].Value.Split('.')[^1], match.Groups[2].Value);
            }

            foreach (Match match in RuntimeLookup.Matches(line))
            {
                if (!Foreign.Contains(match.Groups[2].Value))
                {
                    yield return new Use(file, i + 1, TargetOf(match.Groups[1].Value), match.Groups[2].Value);
                }
            }
        }
    }

    /// <summary>Окно — если первым аргументом стоит оно; иначе цель неизвестна.</summary>
    private static string? TargetOf(string expression) =>
        expression.Contains("window", StringComparison.OrdinalIgnoreCase) ? nameof(MainWindow) : null;

    private static bool Exists(Use use)
    {
        var types = ProgramTypes();
        if (use.Target is { } target)
        {
            var declared = types.Where(type => type.Name == target).ToList();

            // Тип не из программы (WPF, BCL): его внутренности не наши, и сторожить их незачем.
            return declared.Count == 0 || declared.Any(type => Declares(type, use.Name, types));
        }

        return types.Any(type => Declares(type, use.Name, types));
    }

    /// <summary>Член с этим именем у типа или у его предков из программы.</summary>
    private static bool Declares(ProgramType type, string name, IReadOnlyList<ProgramType> types)
    {
        for (var current = type; current is not null;
             current = current.BaseType is { } baseName ? types.FirstOrDefault(other => other.FullName == baseName) : null)
        {
            if (current.Members.Contains(name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Тип программы, прочитанный из метаданных: имя, имена членов и имя предка.</summary>
    private sealed record ProgramType(string Name, string FullName, HashSet<string> Members, string? BaseType);

    /// <summary>
    /// Типы сборок программы по метаданным, без загрузки.
    /// </summary>
    /// <remarks>
    /// Не через <see cref="System.Reflection.Assembly.GetTypes"/>: разбор членов окна тянет инициализатор модуля
    /// PresentationCore, а тот на Linux падает на родной части WPF. Метаданные читаются одинаково
    /// везде, и сторож идёт там же, где и прочие тесты без окон.
    /// </remarks>
    private static IReadOnlyList<ProgramType> ProgramTypes() => _types ??=
    [
        .. new[] { typeof(AppServices).Assembly.Location, typeof(ChatEngine).Assembly.Location }
            .Distinct(StringComparer.Ordinal)
            .SelectMany(ReadTypes)
    ];

    private static IReadOnlyList<ProgramType>? _types;

    private static IEnumerable<ProgramType> ReadTypes(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var result = new List<ProgramType>();
        foreach (var handle in metadata.TypeDefinitions)
        {
            var definition = metadata.GetTypeDefinition(handle);
            var members = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in definition.GetFields())
            {
                members.Add(metadata.GetString(metadata.GetFieldDefinition(field).Name));
            }

            foreach (var method in definition.GetMethods())
            {
                members.Add(metadata.GetString(metadata.GetMethodDefinition(method).Name));
            }

            foreach (var property in definition.GetProperties())
            {
                members.Add(metadata.GetString(metadata.GetPropertyDefinition(property).Name));
            }

            foreach (var @event in definition.GetEvents())
            {
                members.Add(metadata.GetString(metadata.GetEventDefinition(@event).Name));
            }

            foreach (var nested in definition.GetNestedTypes())
            {
                members.Add(metadata.GetString(metadata.GetTypeDefinition(nested).Name));
            }

            result.Add(new ProgramType(
                metadata.GetString(definition.Name),
                FullName(metadata, definition.Namespace, definition.Name),
                members,
                BaseName(metadata, definition.BaseType)));
        }

        return result;
    }

    private static string FullName(MetadataReader metadata,
        StringHandle space, StringHandle name) =>
        space.IsNil ? metadata.GetString(name) : metadata.GetString(space) + "." + metadata.GetString(name);

    // У интерфейсов и <Module> предка нет, а пустой дескриптор притворяется TypeDefinition.
    private static string? BaseName(MetadataReader metadata, EntityHandle handle) =>
        handle.IsNil ? null : handle.Kind switch
        {
            HandleKind.TypeDefinition =>
                metadata.GetTypeDefinition((TypeDefinitionHandle)handle) is var definition
                    ? FullName(metadata, definition.Namespace, definition.Name)
                    : null,
            HandleKind.TypeReference =>
                metadata.GetTypeReference((TypeReferenceHandle)handle) is var reference
                    ? FullName(metadata, reference.Namespace, reference.Name)
                    : null,
            _ => null
        };
}
