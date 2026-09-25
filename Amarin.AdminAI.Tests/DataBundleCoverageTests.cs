using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Сторожа полноты архива данных: он обязан увозить всё, что у человека есть, а не всё, о чём
/// помнили в день, когда его писали.
/// </summary>
/// <remarks>
/// Оба теста написаны так, чтобы падать от чужой правки, а не от своей. Первый обходит
/// <see cref="AppSettings"/> отражением и потому узнаёт о новом поле в настройках сам; второй
/// перечисляет виды файлов, которые программа кладёт под корень данных, и требует категорию
/// каждому — новый вид файла без категории молча не уехал бы в архив, и заметили бы это только
/// развернув копию на другой машине.
/// </remarks>
public sealed class DataBundleCoverageTests
{
    /// <summary>
    /// Эти два поля импорт намеренно оставляет свои — см. <c>DataBundleImporter.ApplySettings</c>.
    /// Белый список загрузок и режим подтверждений это настройки безопасности, и приезжать из
    /// чужого файла они не должны.
    /// </summary>
    private static readonly string[] NeverImported =
    [
        nameof(AppSettings.DownloadAllowedDomains),
        nameof(AppSettings.ApprovalMode)
    ];

    [Fact]
    public void Every_setting_survives_an_export_and_an_import()
    {
        var source = NewRoot();
        var target = NewRoot();
        var archive = Path.Combine(Path.GetTempPath(), "amarin-all-" + Guid.NewGuid().ToString("N") + DataBundle.FileExtension);

        try
        {
            // Каждому полю — значение, отличное от умолчания: иначе потерянное поле совпало бы
            // с тем, что на другой стороне и так лежит, и тест бы этого не заметил.
            var mutated = AppSettings.CreateDefault();
            Mutate(mutated);
            new AppSettingsStore(source).Save(mutated);

            // Считаем от того, что легло на диск, а не от того, что мы насочиняли: часть полей
            // хранилище правит при чтении (цвет обязан быть цветом, масштаб — из списка), и
            // сравнивать надо два одинаково приведённых файла, иначе тест ловил бы нормализацию,
            // а не потерю.
            var original = new AppSettingsStore(source).Load();

            new DataBundleExporter(source).Write(archive, DataCategory.All);
            var result = new DataBundleImporter(target).Apply(archive, DataCategory.All, DataImportMode.Replace);
            Assert.True(result.Ok, result.Error.ToString());

            var restored = new AppSettingsStore(target).Load();
            foreach (var property in NeverImported)
            {
                var field = typeof(AppSettings).GetProperty(property)!;
                field.SetValue(restored, field.GetValue(original));
            }

            // Сравниваем целиком, а не по списку полей: список пришлось бы дописывать руками,
            // и ровно то поле, о котором забыли, из него бы и выпало.
            Assert.Equal(Json(original), Json(restored));
        }
        finally
        {
            Cleanup(source);
            Cleanup(target);
            TryDeleteFile(archive);
        }
    }

    [Theory]
    [InlineData("settings.json", DataCategory.Settings)]
    [InlineData("prompts.json", DataCategory.Settings)]
    [InlineData("instructions/a1b2c3d4.md", DataCategory.Settings)]
    [InlineData("profiles.json", DataCategory.Profiles)]
    [InlineData("chats/index.json", DataCategory.Chats)]
    [InlineData("chats/abc.json", DataCategory.Chats)]
    [InlineData("languages/ru.json", DataCategory.Languages)]
    [InlineData("avatar.png", DataCategory.Appearance)]
    [InlineData("background.jpg", DataCategory.Appearance)]
    public void Every_kind_of_file_the_app_writes_has_a_category(string relative, DataCategory expected) =>
        Assert.Equal(expected, CategoryOf(relative));

    [Theory]
    // Кэш баланса Venice: перетрётся при первом же ходе, везти его на другую машину незачем.
    [InlineData("balance.json")]
    // Готовые выгрузки чатов — это уже копии, и класть копии в копию смысла нет.
    [InlineData("shared/chat-1.json")]
    // Записка от второго запуска программы: живёт секунды.
    [InlineData("handoff/aaa.json")]
    // Недописанный файл от AppDataFile.WriteAtomic.
    [InlineData("settings.json.tmp")]
    public void What_stays_out_of_the_archive_stays_out(string relative) =>
        Assert.Equal(DataCategory.None, CategoryOf(relative));

    [Fact]
    public void A_full_data_folder_arrives_in_the_archive_whole()
    {
        // Сквозная проверка того же, что перечислено по одному выше: кладём на диск по файлу
        // каждого вида и смотрим, что доехало. Опись сравнивается множеством целиком — лишний
        // файл в архиве такой же баг, как потерянный.
        var root = NewRoot();
        var archive = Path.Combine(Path.GetTempPath(), "amarin-whole-" + Guid.NewGuid().ToString("N") + DataBundle.FileExtension);

        try
        {
            new AppSettingsStore(root).Save(AppSettings.CreateDefault());
            new ProfileStore(root).Save(new ProfileRegistry());
            File.WriteAllText(Path.Combine(root, "prompts.json"), "[]");
            Directory.CreateDirectory(Path.Combine(root, "instructions"));
            File.WriteAllText(Path.Combine(root, "instructions", "a1b2c3d4.md"), "текст");
            File.WriteAllText(Path.Combine(root, "chats", "index.json"), "[]");
            File.WriteAllText(Path.Combine(root, "chats", "one.json"), "{}");
            Directory.CreateDirectory(Path.Combine(root, "languages"));
            File.WriteAllText(Path.Combine(root, "languages", "de.json"), "{}");
            File.WriteAllBytes(Path.Combine(root, "avatar.png"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(root, "background.jpg"), [4, 5, 6]);

            // А это остаётся дома, и каждое по своей причине — см. What_stays_out_of_the_archive.
            File.WriteAllText(Path.Combine(root, "balance.json"), "{}");
            Directory.CreateDirectory(Path.Combine(root, "shared"));
            File.WriteAllText(Path.Combine(root, "shared", "chat-1.json"), "{}");
            Directory.CreateDirectory(Path.Combine(root, "handoff"));
            File.WriteAllText(Path.Combine(root, "handoff", "aaa.json"), "{}");
            File.WriteAllText(Path.Combine(root, "settings.json.tmp"), "{}");

            var manifest = new DataBundleExporter(root).Write(archive, DataCategory.All);

            Assert.Equal(
                new SortedSet<string>(StringComparer.Ordinal)
                {
                    "data/avatar.png",
                    "data/background.jpg",
                    "data/chats/index.json",
                    "data/chats/one.json",
                    "data/instructions/a1b2c3d4.md",
                    "data/languages/de.json",
                    "data/profiles.json",
                    "data/prompts.json",
                    "data/settings.json"
                },
                new SortedSet<string>(manifest.Entries.Select(entry => entry.Path), StringComparer.Ordinal));
        }
        finally
        {
            Cleanup(root);
            TryDeleteFile(archive);
        }
    }

    [Fact]
    public void A_backdrop_the_archive_cannot_carry_is_never_stored()
    {
        // Выбор фона предлагает и фильтр «Все файлы». Файл с расширением вне закрытого списка
        // уехал бы мимо архива молча, поэтому до диска он теперь не доходит вовсе.
        Assert.True(DataBundle.IsSupportedImageName("background.png"));
        Assert.True(DataBundle.IsSupportedImageName("background.JPEG"));
        Assert.False(DataBundle.IsSupportedImageName("background.webp"));
        Assert.False(DataBundle.IsSupportedImageName("background.img"));
        Assert.False(DataBundle.IsSupportedImageName("background"));
        Assert.False(DataBundle.IsSupportedImageName(null));
    }

    /// <summary>Классификатор архива — тот же, что решает судьбу файла при экспорте.</summary>
    private static DataCategory CategoryOf(string relative)
    {
        var method = typeof(DataBundle).GetMethod(
            "CategoryOf",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (DataCategory)method.Invoke(null, [relative])!;
    }

    private static string Json(AppSettings settings) =>
        JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>
    /// Меняет каждое свойство на что-нибудь непохожее на умолчание, вглубь по вложенным объектам.
    /// </summary>
    private static void Mutate(object target)
    {
        foreach (var property in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            var current = property.GetValue(target);

            if (!property.CanWrite)
            {
                continue;
            }

            if (type == typeof(bool))
            {
                property.SetValue(target, !(current as bool? ?? false));
            }
            else if (type == typeof(int))
            {
                property.SetValue(target, (current as int? ?? 0) + 7);
            }
            else if (type == typeof(double))
            {
                property.SetValue(target, (current as double? ?? 0) + 0.25);
            }
            else if (type == typeof(string))
            {
                property.SetValue(target, (current as string ?? "") + "-" + property.Name);
            }
            else if (type == typeof(DateTime))
            {
                property.SetValue(target, new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc));
            }
            else if (type.IsEnum)
            {
                property.SetValue(target, Other(type, current));
            }
            else if (typeof(IList).IsAssignableFrom(type) && type.IsGenericType)
            {
                var list = (IList)Activator.CreateInstance(type)!;
                list.Add("example.test");
                property.SetValue(target, list);
            }
            else if (type.IsClass)
            {
                current ??= Activator.CreateInstance(type);
                if (current is not null)
                {
                    Mutate(current);
                    property.SetValue(target, current);
                }
            }
        }
    }

    private static object Other(Type enumType, object? current)
    {
        foreach (var value in Enum.GetValues(enumType))
        {
            if (!Equals(value, current))
            {
                return value;
            }
        }

        return current!;
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-cover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "chats"));
        return root;
    }

    private static void Cleanup(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Временная папка, уборка по возможности.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
