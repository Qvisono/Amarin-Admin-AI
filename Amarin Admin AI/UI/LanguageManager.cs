using System.Text.Json;
using System.Windows;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>Язык интерфейса: встроенный или переведённый моделью.</summary>
/// <param name="Code">Код вроде <c>ru</c>, <c>en</c>, <c>de</c>.</param>
/// <param name="NativeName">Название на самом языке — так его и показывают в списке.</param>
/// <param name="BuiltIn">Поставляется с программой, а не переведён на этом компьютере.</param>
internal sealed record UiLanguage(string Code, string NativeName, bool BuiltIn);

/// <summary>
/// Подменяет словарь строк интерфейса — по образцу <see cref="ThemeManager"/>.
/// </summary>
/// <remarks>
/// Словарь обязан жить на уровне <see cref="Application"/>, а не окна: словарь уровня окна
/// перекрыл бы общий, и переключение перестало бы работать — ровно та же причина, по которой
/// <c>MainWindow.xaml</c> намеренно не мерджит палитру у себя.
/// </remarks>
internal static class LanguageManager
{
    public const string DefaultCode = "ru";

    private static Application? _application;
    private static string _code = DefaultCode;

    /// <summary>Действующий язык.</summary>
    public static string Current => _code;

    /// <summary>Поднимается на потоке интерфейса после подмены словаря.</summary>
    public static event Action? LanguageChanged;

    private static readonly UiLanguage[] BuiltIn =
    [
        new("ru", "Русский", true),
        new("en", "English", true)
    ];

    public static void Initialize(Application application, string code)
    {
        ArgumentNullException.ThrowIfNull(application);
        _application = application;
        Apply(code);
    }

    /// <summary>Языки для списка: встроенные плюс переведённые на этом компьютере.</summary>
    public static IReadOnlyList<UiLanguage> Available()
    {
        var list = new List<UiLanguage>(BuiltIn);
        foreach (var code in UserLanguageStore.List())
        {
            if (list.Any(item => string.Equals(item.Code, code, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            list.Add(new UiLanguage(code, UserLanguageStore.NameOf(code) ?? code, false));
        }

        return list;
    }

    public static void Apply(string? code)
    {
        var normalized = Normalize(code);
        _code = normalized;

        if (_application is null)
        {
            return;
        }

        var dictionary = Build(normalized);
        ThemeManager.EnsureSlots(_application);
        _application.Resources.MergedDictionaries[ThemeManager.StringsSlot] = dictionary;

        // Тот же набор — коду: разметка читает ресурсы, C# читает Loc, источник у них один.
        Loc.Use(Flatten(dictionary));
        LanguageChanged?.Invoke();
    }

    /// <summary>Неизвестный код тихо падает в русский, а не оставляет интерфейс пустым.</summary>
    internal static string Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return DefaultCode;
        }

        var trimmed = code.Trim().ToLowerInvariant();
        if (BuiltIn.Any(item => item.Code == trimmed) || UserLanguageStore.Exists(trimmed))
        {
            return trimmed;
        }

        return DefaultCode;
    }

    /// <summary>
    /// Собирает словарь языка: встроенный русский как основа, поверх — выбранный язык.
    /// </summary>
    /// <remarks>
    /// Русский снизу всегда: у перевода может не хватать ключа — нового или потерянного при
    /// машинном переводе, — и тогда подпись останется русской, а не исчезнет.
    /// </remarks>
    internal static ResourceDictionary Build(string code)
    {
        var merged = new ResourceDictionary();
        merged.MergedDictionaries.Add(LoadBuiltIn(DefaultCode));

        if (code == DefaultCode)
        {
            return merged;
        }

        if (BuiltIn.Any(item => item.Code == code))
        {
            merged.MergedDictionaries.Add(LoadBuiltIn(code));
            return merged;
        }

        if (UserLanguageStore.TryLoad(code) is { } user)
        {
            merged.MergedDictionaries.Add(user);
        }

        return merged;
    }

    // Assembly-qualified: голый "/UI/Lang/..." разрешается относительно
    // Application.ResourceAssembly, а это не наша сборка под тест-раннером и в конструкторе.
    private static readonly string PackPrefix =
        "pack://application:,,,/" +
        Uri.EscapeDataString(typeof(LanguageManager).Assembly.GetName().Name ?? "") +
        ";component/UI/Lang/";

    internal static ResourceDictionary LoadBuiltIn(string code) => new()
    {
        Source = new Uri(PackPrefix + "Strings." + code + ".xaml", UriKind.Absolute)
    };

    internal static Dictionary<string, string> Flatten(ResourceDictionary dictionary)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        Collect(dictionary, map);
        return map;
    }

    private static void Collect(ResourceDictionary dictionary, Dictionary<string, string> map)
    {
        // Сперва вложенные, потом свои: последний победивший словарь должен остаться сверху,
        // как это делает и сам WPF.
        foreach (var nested in dictionary.MergedDictionaries)
        {
            Collect(nested, map);
        }

        foreach (var key in dictionary.Keys)
        {
            if (key is string name && dictionary[key] is string value)
            {
                map[name] = value;
            }
        }
    }
}

/// <summary>
/// Языки, переведённые моделью на этом компьютере: <c>%APPDATA%\Amarin Admin AI\languages</c>.
/// </summary>
/// <remarks>
/// В корне программы, а не в папке профиля: перевод — не личные данные, и делать его заново
/// для каждого профиля бессмысленно. Сам выбор языка при этом у каждого профиля свой, как тема.
/// </remarks>
internal static class UserLanguageStore
{
    private const string FolderName = "languages";

    public static string Directory => Path.Combine(AppPaths.Root, FolderName);

    public static string FileFor(string code) => Path.Combine(Directory, code + ".json");

    public static bool Exists(string code) => File.Exists(FileFor(code));

    public static IReadOnlyList<string> List()
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory))
            {
                return [];
            }

            return [.. System.IO.Directory
                .GetFiles(Directory, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Название языка на нём самом — оно лежит в файле под служебным ключом.</summary>
    public static string? NameOf(string code) => Read(code)?.GetValueOrDefault(NameKey);

    /// <summary>Служебный ключ: не подпись интерфейса, а имя самого языка для списка.</summary>
    public const string NameKey = "S.Language.NativeName";

    public static ResourceDictionary? TryLoad(string code)
    {
        var map = Read(code);
        if (map is null)
        {
            return null;
        }

        var dictionary = new ResourceDictionary();
        foreach (var (key, value) in map)
        {
            if (key.StartsWith("S.", StringComparison.Ordinal))
            {
                dictionary[key] = value;
            }
        }

        return dictionary;
    }

    public static void Save(string code, IReadOnlyDictionary<string, string> strings)
    {
        AppDataFile.WriteAtomic(FileFor(code), JsonSerializer.Serialize(strings, AppJson.Options));
    }

    private static Dictionary<string, string>? Read(string code)
    {
        try
        {
            var path = FileFor(code);
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(path), AppJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Битый файл языка — не повод оставлять человека без интерфейса.
            return null;
        }
    }
}
