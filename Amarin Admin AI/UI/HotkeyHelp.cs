using Spectre.Console;
using Spectre.Console.Rendering;

namespace Amarin.UI;

internal static class HotkeyHelp
{
    public static IRenderable BuildPanel()
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(UiTheme.Border)
            .AddColumn(new TableColumn("[grey]Клавиша[/]").Centered())
            .AddColumn("[grey]Действие[/]")
            .AddColumn("[grey]Команда[/]");

        AddRow(table, "Enter", "Отправить запрос", "—");
        AddRow(table, "Ctrl+C", "Отменить текущий запрос", "—");
        AddRow(table, "—", "Палитра команд", "/");
        AddRow(table, "—", "Справка", "/help");
        AddRow(table, "—", "Очистить сессию", "/clear");
        AddRow(table, "—", "Экспорт отчёта", "/export");
        AddRow(table, "—", "Режим диагностики", "/readonly");
        AddRow(table, "—", "Журнал действий", "/history");
        AddRow(table, "—", "Откат изменений", "/undo");
        AddRow(table, "—", "Режим сессии", "/session");
        AddRow(table, "—", "Сменить модель", "/model");
        AddRow(table, "—", "Баланс Venice", "balance");
        AddRow(table, "—", "Выход", "exit");

        return UiTheme.CreatePanel(
            $"[cyan]{UiTheme.IconInfo} Справка[/]",
            table,
            UiTheme.Accent,
            UiTheme.Accent);
    }

    private static void AddRow(Table table, string key, string action, string command)
    {
        table.AddRow(
            $"[cyan]{Markup.Escape(key)}[/]",
            Markup.Escape(action),
            string.IsNullOrWhiteSpace(command) ? "[dim]—[/]" : $"[dim]{Markup.Escape(command)}[/]");
    }
}