using System.Text;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Разбивка «занято на диске» для вкладки Data Controls.
/// </summary>
/// <remarks>
/// Считается по временным папкам: файлы пользователя в <c>%APPDATA%</c> тесты не трогают.
/// </remarks>
public sealed class DataUsageTests
{
    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-usage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
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

    private static void Write(string path, int bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    private static long BytesOf(UsageReport report, string key) =>
        report.Entries.FirstOrDefault(e => e.LabelKey == key).Bytes;

    [Fact]
    public void Files_land_in_the_category_a_person_would_name()
    {
        var app = NewTempRoot();
        var local = NewTempRoot();
        try
        {
            Write(Path.Combine(app, "chats", "a.json"), 1000);
            Write(Path.Combine(app, "chats", "index.json"), 200);
            Write(Path.Combine(app, "settings.json"), 300);
            Write(Path.Combine(app, "profiles.json"), 100);
            Write(Path.Combine(app, "languages", "de.json"), 400);
            Write(Path.Combine(app, "shared", "чат-20260101-120000.amrnchat"), 500);
            Write(Path.Combine(app, "avatar.png"), 600);
            Write(Path.Combine(app, "handoff", "x.json"), 50);
            Write(Path.Combine(local, "snapshots", "s1", "meta.json"), 700);
            Write(Path.Combine(local, "crash.log"), 800);

            var report = DataUsage.Measure(app, local, exePath: null);

            Assert.Equal(1200, BytesOf(report, DataUsage.ChatsKey));
            Assert.Equal(400, BytesOf(report, DataUsage.SettingsKey));
            Assert.Equal(400, BytesOf(report, DataUsage.LanguagesKey));
            Assert.Equal(500, BytesOf(report, DataUsage.SharedKey));
            Assert.Equal(600, BytesOf(report, DataUsage.AppearanceKey));
            Assert.Equal(700, BytesOf(report, DataUsage.SnapshotsKey));
            Assert.Equal(800, BytesOf(report, DataUsage.LogsKey));
            Assert.Equal(50, BytesOf(report, DataUsage.OtherKey));
            Assert.Equal(4650, report.TotalBytes);

            // Крупная категория идёт первой: список читается сверху, и там должно быть то,
            // из-за чего место и кончилось.
            Assert.Equal(DataUsage.ChatsKey, report.Entries[0].LabelKey);
            Assert.Equal(2, report.Entries[0].Files);
        }
        finally
        {
            Cleanup(app);
            Cleanup(local);
        }
    }

    [Fact]
    public void A_profiles_chats_count_as_chats_and_not_as_something_else()
    {
        // Профили отдельной категорией не выделены: «сколько весят чаты» — вопрос про все
        // профили сразу. Если бы классификация шла по папке, а не по сути файла, вложенные
        // чаты утекли бы в «прочее».
        var app = NewTempRoot();
        var local = NewTempRoot();
        try
        {
            Write(Path.Combine(app, "profiles", "работа", "chats", "b.json"), 900);
            Write(Path.Combine(app, "profiles", "работа", "settings.json"), 120);
            Write(Path.Combine(app, "profiles", "работа", "background.jpg"), 640);

            var report = DataUsage.Measure(app, local, exePath: null);

            Assert.Equal(900, BytesOf(report, DataUsage.ChatsKey));
            Assert.Equal(120, BytesOf(report, DataUsage.SettingsKey));
            Assert.Equal(640, BytesOf(report, DataUsage.AppearanceKey));
            Assert.Equal(0, BytesOf(report, DataUsage.OtherKey));
        }
        finally
        {
            Cleanup(app);
            Cleanup(local);
        }
    }

    [Fact]
    public void Attachments_are_found_inside_the_chat_file_that_holds_them()
    {
        // Папки вложений не существует — картинки и документы лежат base64 прямо в chats/*.json,
        // в двух видах сразу: data:-URI для модели и голая строка для экрана. Считаться должны оба.
        var app = NewTempRoot();
        var local = NewTempRoot();
        try
        {
            var payload = new string('A', 4096);
            var json = $$"""
                {
                  "title": "Разговор про диски",
                  "apiMessages": [
                    { "role": "user", "content": [
                        { "type": "text", "text": "что на этой картинке" },
                        { "type": "image_url", "image_url": { "url": "data:image/png;base64,{{payload}}" } }
                    ] }
                  ],
                  "display": [ { "base64": "{{payload}}", "mimeType": "image/png" } ]
                }
                """;

            var path = Path.Combine(app, "chats", "a.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var report = DataUsage.Measure(app, local, exePath: null);

            // Оба вхождения плюс префикс data:image/png;base64, у первого.
            Assert.Equal((4096 * 2) + "data:image/png;base64,".Length, report.AttachmentBytes);

            // И вложения — часть чатов, а не отдельное слагаемое общей суммы.
            Assert.True(report.AttachmentBytes < BytesOf(report, DataUsage.ChatsKey));
            Assert.Equal(BytesOf(report, DataUsage.ChatsKey), report.TotalBytes);
        }
        finally
        {
            Cleanup(app);
            Cleanup(local);
        }
    }

    [Fact]
    public void Ordinary_prose_is_not_mistaken_for_an_attachment()
    {
        var app = NewTempRoot();
        var local = NewTempRoot();
        try
        {
            var path = Path.Combine(app, "chats", "a.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(
                path,
                """{ "title": "Длинный разговор", "text": "%TEXT%" }""".Replace("%TEXT%", new string('щ', 3000)),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var report = DataUsage.Measure(app, local, exePath: null);

            Assert.Equal(0, report.AttachmentBytes);
            Assert.True(BytesOf(report, DataUsage.ChatsKey) > 3000);
        }
        finally
        {
            Cleanup(app);
            Cleanup(local);
        }
    }

    [Fact]
    public void A_damaged_chat_is_still_counted_as_a_chat()
    {
        // Повреждённый файл — обычный ответ, а не авария: вес его известен, состав — нет.
        var app = NewTempRoot();
        var local = NewTempRoot();
        try
        {
            var path = Path.Combine(app, "chats", "broken.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ \"title\": \"обрыв", new UTF8Encoding(false));

            var report = DataUsage.Measure(app, local, exePath: null);

            Assert.True(BytesOf(report, DataUsage.ChatsKey) > 0);
            Assert.Equal(0, report.AttachmentBytes);
        }
        finally
        {
            Cleanup(app);
            Cleanup(local);
        }
    }

    [Fact]
    public void A_missing_folder_is_an_empty_report_and_not_a_crash()
    {
        var report = DataUsage.Measure(
            Path.Combine(Path.GetTempPath(), "amarin-usage-" + Guid.NewGuid().ToString("N")),
            Path.Combine(Path.GetTempPath(), "amarin-usage-" + Guid.NewGuid().ToString("N")),
            exePath: null);

        Assert.Empty(report.Entries);
        Assert.Equal(0, report.TotalBytes);
        Assert.Equal(0, report.AppBytes);
    }

    [Fact]
    public void The_application_is_measured_apart_from_the_data()
    {
        var app = NewTempRoot();
        var local = NewTempRoot();
        try
        {
            Write(Path.Combine(app, "chats", "a.json"), 100);
            var exe = Path.Combine(local, "fake.exe");
            Write(exe, 4096);

            var report = DataUsage.Measure(app, local, exe);

            Assert.Equal(4096, report.AppBytes);

            // Программа лежит в той же временной папке, но в сумму по данным входит только то,
            // что действительно является данными: 100 байт чата плюс 4096 «прочего» из local.
            Assert.Equal(100, BytesOf(report, DataUsage.ChatsKey));
        }
        finally
        {
            Cleanup(app);
            Cleanup(local);
        }
    }

    [Fact]
    public void Sizes_climb_past_the_megabyte()
    {
        // Без разряда гигабайтов сама программа плюс история давали «1234,5 МБ».
        Assert.Equal("512 Б", AttachmentTypes.FormatSize(512));
        Assert.Equal("2 МБ", AttachmentTypes.FormatSize(2 * 1024 * 1024));
        Assert.Equal("1,5 ГБ", AttachmentTypes.FormatSize((long)(1.5 * 1024 * 1024 * 1024)).Replace('.', ','));
        Assert.DoesNotContain("МБ", AttachmentTypes.FormatSize(3L * 1024 * 1024 * 1024), StringComparison.Ordinal);
    }
    [Fact]
    public void A_small_chat_after_a_big_one_is_counted_by_its_own_length()
    {
        // Буфер чтения теперь один на весь обход и дорастает до самого большого файла: иначе
        // каждый чат с вложениями уезжал в кучу больших объектов отдельным массивом, а её сборка
        // останавливает и поток интерфейса. Ловушка ровно одна — прочитать из общего буфера
        // больше, чем в файле, и приписать маленькому чату хвост предыдущего.
        var app = NewTempRoot();
        var local = NewTempRoot();
        try
        {
            void WriteChat(string name, int payload) =>
                WriteText(
                    Path.Combine(app, "chats", name),
                    $$"""{ "display": [ { "base64": "{{new string('A', payload)}}" } ] }""");

            WriteChat("a.json", 64);
            WriteChat("b.json", 8192);
            WriteChat("c.json", 64);

            var report = DataUsage.Measure(app, local, exePath: null);

            Assert.Equal(64 + 8192 + 64, report.AttachmentBytes);
        }
        finally
        {
            Cleanup(app);
            Cleanup(local);
        }
    }

    private static void WriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

}
