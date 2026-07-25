using Spectre.Console;

namespace Amarin.UI;

public sealed record PaletteCommand(string Command, string Title, string Description);

public static class CommandPalette
{
    public static readonly PaletteCommand[] AllCommands =
    [
        new("/clear", "Очистить сессию", "Сбросить историю и журнал действий"),
        new("/history", "Журнал действий", "Таблица вызовов инструментов"),
        new("/export", "Экспорт отчёта", "HTML, Markdown и PDF"),
        new("/undo", "Откат изменений", "Восстановить состояние до последнего запроса"),
        new("/readonly", "Режим диагностики", "Только чтение, без записи"),
        new("/session", "Режим сессии", "Непрерывная или изолированная история"),
        new("/model", "Сменить модель", "Выбрать модель Venice вручную"),
        new("/help", "Справка", "Команды и подсказки"),
        new("balance", "Баланс Venice", "Показать остаток API"),
        new("exit", "Выход", "Закрыть Amarin")
    ];

    private const string HintMarkup = "[dim]↑↓ выбор · Enter — выполнить · Esc — отмена[/]";

    public static string? Run(string initialFilter = "")
    {
        var filter = initialFilter;
        var selected = 0;
        var drawnLines = 0;

        try
        {
            while (true)
            {
                if (drawnLines > 0)
                {
                    ConsoleOverlayLines.Clear(drawnLines);
                    drawnLines = 0;
                }

                var matches = FilterCommands(filter).ToList();
                if (matches.Count == 0)
                {
                    matches = AllCommands.ToList();
                }

                selected = Math.Clamp(selected, 0, matches.Count - 1);
                ConsoleInputRestore.PrepareForInput();

                var overlayStart = ConsoleOverlayLines.CaptureTop();

                var title = string.IsNullOrWhiteSpace(filter)
                    ? "[cyan]Команды[/] [dim](введите для поиска)[/]"
                    : $"[cyan]Команды[/] [dim]· фильтр:[/] [yellow]{Markup.Escape(filter)}[/]";

                var rows = new Table()
                    .Border(TableBorder.Rounded)
                    .BorderColor(UiTheme.Border)
                    .HideHeaders()
                    .AddColumn(new TableColumn("").NoWrap())
                    .AddColumn(new TableColumn(""))
                    .AddColumn(new TableColumn(""));

                for (var i = 0; i < matches.Count; i++)
                {
                    var item = matches[i];
                    var marker = i == selected ? "[bold cyan]›[/]" : " ";
                    var cmdStyle = i == selected ? "[bold cyan]" : "[cyan]";
                    rows.AddRow(
                        marker,
                        $"{cmdStyle}{Markup.Escape(item.Command)}[/]",
                        Markup.Escape(item.Description));
                }

                var panel = UiTheme.CreatePanel(title, rows, UiTheme.Accent, UiTheme.Accent);
                AnsiConsole.Write(panel);
                AnsiConsole.MarkupLine(HintMarkup);
                Console.Out.Flush();

                drawnLines = Math.Max(
                    ConsoleOverlayLines.CountLines(overlayStart),
                    EstimatePaletteLines(matches.Count));

                var key = Console.ReadKey(intercept: true);

                if (key.Key == ConsoleKey.Escape)
                {
                    return null;
                }

                if (key.Key == ConsoleKey.Enter)
                {
                    return matches[selected].Command;
                }

                if (key.Key == ConsoleKey.UpArrow)
                {
                    selected = Math.Max(0, selected - 1);
                    continue;
                }

                if (key.Key == ConsoleKey.DownArrow)
                {
                    selected = Math.Min(matches.Count - 1, selected + 1);
                    continue;
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (filter.Length > 0)
                    {
                        filter = filter[..^1];
                    }

                    continue;
                }

                if (char.IsControl(key.KeyChar))
                {
                    continue;
                }

                filter += key.KeyChar;
            }
        }
        finally
        {
            if (drawnLines > 0)
            {
                ConsoleOverlayLines.Clear(drawnLines);
            }

            ConsoleInputRestore.Restore();
            Console.Out.Flush();
        }
    }

    private static int EstimatePaletteLines(int commandCount) => commandCount + 8;

    private static IEnumerable<PaletteCommand> FilterCommands(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return AllCommands;
        }

        var terms = filter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return AllCommands.Where(cmd =>
            terms.All(term =>
                cmd.Command.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                cmd.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                cmd.Description.Contains(term, StringComparison.OrdinalIgnoreCase)));
    }
}