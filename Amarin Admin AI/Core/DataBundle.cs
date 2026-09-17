using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Amarin.Core;

/// <summary>
/// Что кладём в архив данных. Флаги, а не одно значение: окно экспорта — это набор галочек.
/// </summary>
[Flags]
public enum DataCategory
{
    None = 0,
    Chats = 1,
    Settings = 2,
    Profiles = 4,
    Appearance = 8,
    Languages = 16,
    All = Chats | Settings | Profiles | Appearance | Languages
}

/// <summary>Что импорт делает с тем, что у человека уже есть.</summary>
public enum DataImportMode
{
    /// <summary>Добавить недостающее, своё оставить.</summary>
    Merge,

    /// <summary>Заменить своё содержимым архива.</summary>
    Replace
}

/// <summary>
/// Почему архив не взяли.
/// </summary>
/// <remarks>
/// Перечисление, а не текст исключения: человеку показывают понятную строку по ключу, и
/// «Unhandled exception of type System.IO.InvalidDataException» в интерфейс попасть не должно.
/// </remarks>
public enum DataBundleError
{
    None,
    NotAZip,
    NotOurArchive,
    NewerFormat,
    EmptyArchive,
    TooLarge,
    Unreadable,
    WriteFailed
}

/// <summary>
/// Потолки распаковки — защита от архива, который разворачивается в терабайт.
/// </summary>
/// <remarks>
/// Параметр конструктора, а не константы: иначе проверить их тестом можно было бы только собрав
/// настоящую zip-бомбу и распаковав гигабайты на диск разработчика.
/// </remarks>
/// <param name="MaxUnpackedBytes">Сколько всего разрешено распаковать.</param>
/// <param name="MaxEntryBytes">Потолок на одну запись.</param>
/// <param name="MaxManifestBytes">Потолок на опись: сама сводка не должна стать бомбой.</param>
/// <param name="MaxEntries">Сколько записей вообще разрешено видеть в архиве.</param>
public sealed record DataBundleLimits(
    long MaxUnpackedBytes = 2L * 1024 * 1024 * 1024,
    long MaxEntryBytes = 256L * 1024 * 1024,
    long MaxManifestBytes = 8L * 1024 * 1024,
    int MaxEntries = 100_000)
{
    public static DataBundleLimits Default { get; } = new();
}

/// <summary>Одна запись описи.</summary>
public sealed class DataBundleEntry
{
    /// <summary>Путь внутри архива, всегда через «/».</summary>
    public string Path { get; set; } = "";

    public DataCategory Category { get; set; }

    /// <summary>Пустая строка — активный профиль, он лежит в корне архива.</summary>
    public string ProfileId { get; set; } = "";

    public long Bytes { get; set; }
}

/// <summary>
/// Опись архива: что внутри, чем и когда собрано.
/// </summary>
/// <remarks>
/// На импорте <see cref="Entries"/> работает белым списком — файла, которого нет в описи, на диск
/// не попадёт вовсе. Это сильнее любой проверки пути на «..» и заодно позволяет показать человеку
/// сводку, не распаковав ни байта.
/// </remarks>
public sealed class DataBundleManifest
{
    public string Format { get; set; } = DataBundle.FormatId;

    public int FormatVersion { get; set; } = DataBundle.FormatVersion;

    public string AppVersion { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    /// <summary>Чей профиль лежал в корне архива. Справочно: импорт его не применяет.</summary>
    public string ActiveProfileId { get; set; } = "";

    public List<DataCategory> Categories { get; set; } = [];

    public List<DataBundleEntry> Entries { get; set; } = [];
}

/// <summary>
/// Формат архива данных: имена, потолки и раскладка файлов по категориям.
/// </summary>
/// <remarks>
/// <para>
/// Корень архива (<c>data/…</c>) — это всегда активный профиль, default он или нет. На импорте
/// всё из корня ложится в папку активного профиля получателя, поэтому «мои чаты» приезжают к нему
/// как его чаты и не зависят от того, кто под каким профилем работал.
/// </para>
/// <para>
/// Правило двух осей: категория решает, <b>что</b> берём, а галочка «Профили» — <b>у кого</b>.
/// Без «Профилей» уезжает только активный профиль; с ними — ещё и реестр вместе с папками
/// остальных, но их содержимое всё равно разложено по своим категориям. Иначе чаты неосновного
/// профиля приехали бы в папку, на которую никто не ссылается.
/// </para>
/// </remarks>
public static class DataBundle
{
    public const string FormatId = "amarin-data";

    public const int FormatVersion = 1;

    public const string FileExtension = ".amrnbak";

    public const string ManifestName = "manifest.json";

    /// <summary>Приставка, под которой в архиве лежат сами данные.</summary>
    public const string DataPrefix = "data/";

    /// <summary>Папка неосновных профилей — и в архиве, и на диске.</summary>
    public const string ProfilesFolder = "profiles";

    /// <summary>
    /// Картинки оформления кладутся байтами, поэтому список расширений закрытый.
    /// </summary>
    /// <remarks>
    /// На импорте это единственное, что мешает приехать под именем <c>background.exe</c> чему
    /// угодно: категорию такому файлу классификатор даёт по началу имени, а не по содержимому.
    /// Поэтому список закрыт и он же служит правилом для выбора фона и аватара — файл, который
    /// экспорт не заберёт, на диск попадать не должен вовсе. <c>.webp</c> сюда не входит
    /// намеренно: его кодек есть не на каждой установке Windows.
    /// </remarks>
    public static readonly string[] ImageExtensions =
        [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"];

    /// <summary>Возьмёт ли архив картинку оформления с таким именем.</summary>
    public static bool IsSupportedImageName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        ImageExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);

    /// <summary>Потолок на картинку оформления: аватар и фон столько не весят никогда.</summary>
    public const long MaxImageBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Опись пишется с UnsafeRelaxedJsonEscaping: имена профилей бывают кириллическими, и человек,
    /// заглянувший в архив, должен читать их глазами, а не как «\u041f».
    /// </summary>
    internal static JsonSerializerOptions Json { get; } = CreateJson();

    /// <summary>Порядок показа категорий в окне экспорта — от самого важного к служебному.</summary>
    public static IReadOnlyList<DataCategory> All { get; } =
    [
        DataCategory.Chats,
        DataCategory.Settings,
        DataCategory.Profiles,
        DataCategory.Appearance,
        DataCategory.Languages
    ];

    public static string LabelKeyOf(DataCategory category) => category switch
    {
        DataCategory.Chats => "S.Bundle.Cat.Chats",
        DataCategory.Settings => "S.Bundle.Cat.Settings",
        DataCategory.Profiles => "S.Bundle.Cat.Profiles",
        DataCategory.Appearance => "S.Bundle.Cat.Appearance",
        DataCategory.Languages => "S.Bundle.Cat.Languages",
        _ => ""
    };

    public static string DescriptionKeyOf(DataCategory category) => category switch
    {
        DataCategory.Chats => "S.Bundle.Cat.ChatsDesc",
        DataCategory.Settings => "S.Bundle.Cat.SettingsDesc",
        DataCategory.Profiles => "S.Bundle.Cat.ProfilesDesc",
        DataCategory.Appearance => "S.Bundle.Cat.AppearanceDesc",
        DataCategory.Languages => "S.Bundle.Cat.LanguagesDesc",
        _ => ""
    };

    public static string ErrorKey(DataBundleError error) => error switch
    {
        DataBundleError.NotAZip => "S.Bundle.Error.NotAZip",
        DataBundleError.NotOurArchive => "S.Bundle.Error.NotOurArchive",
        DataBundleError.NewerFormat => "S.Bundle.Error.NewerFormat",
        DataBundleError.EmptyArchive => "S.Bundle.Error.EmptyArchive",
        DataBundleError.TooLarge => "S.Bundle.Error.TooLarge",
        DataBundleError.Unreadable => "S.Bundle.Error.Unreadable",
        DataBundleError.WriteFailed => "S.Bundle.Error.WriteFailed",
        _ => ""
    };

    /// <summary>Имя по умолчанию в диалоге сохранения: «amarin-data-20260914-1203.amrnbak».</summary>
    public static string SuggestedFileName(DateTime now) =>
        $"amarin-data-{now:yyyyMMdd-HHmm}{FileExtension}";

    /// <summary>
    /// Куда отнести файл под корнем данных. Путь — относительный, через «/».
    /// </summary>
    /// <remarks>
    /// Основную работу делает <see cref="DataUsage.ClassifyAppFile"/> — та же раскладка, по которой
    /// считается «Занято на диске». Здесь она лишь доразделена там, где экспорту нужно различать
    /// то, что отчёту о месте различать незачем.
    /// </remarks>
    internal static DataCategory CategoryOf(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
        {
            return DataCategory.None;
        }

        // В нижний регистр приводим здесь, а не в Relative: тот отдаёт путь для самого архива, и
        // подмена регистра в имени профиля увела бы файл в чужую папку.
        relative = relative.ToLowerInvariant();
        var name = LastSegment(relative);

        // profiles.json — реестр аккаунтов, а не настройки: без галочки «Профили» он уехать не
        // должен. balance.json — кэш баланса Venice, он перетрётся при первом же ходе, и везти его
        // на другую машину бессмысленно.
        if (name == "profiles.json")
        {
            return DataCategory.Profiles;
        }

        if (name == "balance.json")
        {
            return DataCategory.None;
        }

        // Недописанный файл: AppDataFile.WriteAtomic держит «.tmp» рядом с настоящим, и попасть в
        // архив он может только по случайности обхода.
        if (name.EndsWith(".tmp", StringComparison.Ordinal))
        {
            return DataCategory.None;
        }

        return DataUsage.ClassifyAppFile(relative) switch
        {
            DataUsage.SettingsKey => DataCategory.Settings,
            DataUsage.AppearanceKey => DataCategory.Appearance,
            DataUsage.ChatsKey => DataCategory.Chats,
            DataUsage.LanguagesKey => DataCategory.Languages,

            // shared/ — свалка уже сделанных экспортов, handoff/ — записка от второго запуска,
            // всё незнакомое — тем более мимо. Чего нет ни в одной категории, того нет в архиве,
            // а значит нет и в описи, а значит импорт его не примет.
            _ => DataCategory.None
        };
    }

    /// <summary>Относительный путь через «/» — в том виде, в каком он ляжет в архив.</summary>
    internal static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>
    /// Годится ли имя записи архива на роль пути. Проверка до всякой распаковки.
    /// </summary>
    /// <remarks>
    /// Здесь отлавливается zip-slip: запись вида <c>data/../../evil.exe</c> или <c>C:/evil.exe</c>
    /// в обычном ZIP совершенно законна, и наивный ExtractToFile запишет её ровно туда, куда
    /// указано, — за пределы папки данных.
    /// </remarks>
    internal static bool IsSafeEntryName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var normalized = name.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':'))
        {
            return false;
        }

        foreach (var segment in normalized.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                return false;
            }

            foreach (var symbol in segment)
            {
                if (char.IsControl(symbol) || symbol is '*' or '?' or '"' or '<' or '>' or '|')
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static string LastSegment(string relative)
    {
        var cut = relative.LastIndexOf('/');
        return cut < 0 ? relative : relative[(cut + 1)..];
    }

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
