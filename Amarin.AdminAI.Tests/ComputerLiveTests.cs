using System.Text.Json;
using System.Text.RegularExpressions;
using Amarin.Computer;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Живой прогон исполнения на настоящей Windows: настоящий <c>WindowsComputer</c> через настоящий
/// <see cref="ComputerTool"/> (с правилами безопасности и сеансом) находит открытое приложение
/// ChatGPT, находит его поле ввода деревом UI Automation, кликает в него настоящей мышью,
/// печатает и снимает экран. Модель тут не участвует — проверяется ровно то, что подделка
/// <c>FakeComputer</c> покрыть не может: <c>SendInput</c>, фокус окна, дерево и захват.
/// </summary>
/// <remarks>
/// Двигает настоящие мышь и клавиатуру, поэтому идёт только по явной просьбе человека:
/// <c>AMARIN_COMPUTER_LIVE=1</c> (и <c>AMARIN_COMPUTER_LIVE_OUT=&lt;папка&gt;</c> для снимков).
/// Без переменной тест сразу выходит, как живые тесты маршрутизатора.
/// <para>
/// Enter не нажимается: человек согласился на «напечатать», а не на «отправить». Блокнотом
/// прогон не пользуется: Блокнот Windows 11 сам открывает прежние несохранённые вкладки
/// человека, и первая версия прогона дописала слово в его заметку.
/// </para>
/// </remarks>
public sealed class ComputerLiveTests
{
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public async Task The_real_layer_types_hello_into_the_chatgpt_app_without_sending()
    {
        if (Environment.GetEnvironmentVariable("AMARIN_COMPUTER_LIVE") != "1")
        {
            return;
        }

        var outDir = Environment.GetEnvironmentVariable("AMARIN_COMPUTER_LIVE_OUT")
                     ?? Path.Combine(Path.GetTempPath(), "amarin-computer-live");
        Directory.CreateDirectory(outDir);
        var log = new List<string>();

        try
        {
            var computer = new WindowsComputer();
            var session = new ComputerSession(
                computer,
                null,
                new ComputerStart(Unattended: false, Task: "type hello in ChatGPT"),
                new ComputerLease());
            var tool = new ComputerTool(session);

            // 1. Список окон — ищем ChatGPT тем же путём, что и оператор.
            var windows = await tool.ExecuteAsync(Args(new { action = "windows" }));
            log.Add("windows:\n" + windows.Output);
            Assert.True(windows.Success, windows.Output);

            var chat = computer.Windows().FirstOrDefault(w =>
                w.ProcessName.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase));
            Assert.True(chat is not null, "ChatGPT app window not found; open the app first.");
            var id = chat.Id;

            // 2. Выводим его вперёд (свёрнутое разворачивается).
            var focus = await tool.ExecuteAsync(Args(new { action = "focus", window = id, explanation = "bring ChatGPT to front" }));
            log.Add("focus -> " + Short(focus));
            Assert.True(focus.Success, focus.Output);
            await Task.Delay(800);

            var before = await tool.ExecuteAsync(Args(new { action = "screenshot", window = id }));
            Assert.True(before.Success, before.Output);
            Save(outDir, "chatgpt-before.jpg", before.GetImages().Single().Base64);

            // 3. Поле ввода — из дерева UI Automation, а не по пикселям.
            var find = await tool.ExecuteAsync(Args(new { action = "find", window = id, role = "edit" }));
            log.Add("find edit:\n" + find.Output);
            var element = Regex.Match(find.Output, @"\b(e\d+) edit\b");
            if (!element.Success)
            {
                var tree = await tool.ExecuteAsync(Args(new { action = "inspect", window = id, limit = 400 }));
                log.Add("inspect:\n" + tree.Output);
                element = Regex.Match(tree.Output, @"\b(e\d+) (?:edit|document)\b");
            }

            Assert.True(element.Success, "No input field in the ChatGPT tree; see log.txt.");

            // 4. Настоящий клик в поле и печать туда, где фокус.
            var click = await tool.ExecuteAsync(Args(new { action = "click", window = id, element = element.Groups[1].Value, explanation = "click the message box" }));
            log.Add("click -> " + Short(click));
            Assert.True(click.Success, click.Output);
            await Task.Delay(300);

            var typed = await tool.ExecuteAsync(Args(new { action = "type", text = "hello", explanation = "type hello" }));
            log.Add("type -> " + Short(typed));
            Assert.True(typed.Success, typed.Output);
            await Task.Delay(500);

            // 5. Снимок после и чтение поля деревом — без Enter.
            var after = await tool.ExecuteAsync(Args(new { action = "screenshot", window = id }));
            Assert.True(after.Success, after.Output);
            Save(outDir, "chatgpt-after.jpg", after.GetImages().Single().Base64);

            var check = await tool.ExecuteAsync(Args(new { action = "find", window = id, name = "hello" }));
            log.Add("find hello:\n" + check.Output);
            var fields = await computer.InspectAsync(chat.Handle, 400, TimeSpan.FromSeconds(5), CancellationToken.None);
            var seen = fields.Elements.Any(e => (e.Value ?? "").Contains("hello", StringComparison.Ordinal));
            log.Add("input value has hello via UIA: " + seen);
        }
        finally
        {
            File.WriteAllText(Path.Combine(outDir, "log.txt"), string.Join("\n\n", log));
        }
    }

    private static string Short(ToolResult result) =>
        result.Output.ReplaceLineEndings(" ").Length <= 160
            ? result.Output.ReplaceLineEndings(" ")
            : result.Output.ReplaceLineEndings(" ")[..160] + "…";

    private static void Save(string dir, string name, string base64) =>
        File.WriteAllBytes(Path.Combine(dir, name), Convert.FromBase64String(base64));
}
