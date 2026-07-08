using Spectre.Console;
using Spectre.Console.Rendering;
using Amarin.UI;

var sample = """
- **run_powershell** Ч выполнить PowerShell-команду или скрипт
- **registry** Ч читать/писать реестр (HKLM, HKCU и др.)
- **filesystem** Ч читать, писать, копировать, перемещать файлы и папки (удаление запрещено)
""";

try {
    var markup = MarkdownFormatter.ToSpectreMarkup(sample);
    Console.WriteLine("MARKUP:");
    Console.WriteLine(markup);
    var m = new Markup(markup);
    AnsiConsole.Write(UiTheme.CreatePanel("[cyan]Amarin[/]", m, UiTheme.Border, UiTheme.Primary));
    Console.WriteLine("OK");
} catch (Exception ex) {
    Console.WriteLine("FAIL: " + ex.Message);
}
