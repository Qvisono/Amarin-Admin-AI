using System.Text;
using Amarin.Core;
using Amarin.Tools;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Amarin.UI;

public sealed class ConsolePresenter
{
    public void ShowBanner()
    {
        var isAdmin = RuntimeContext.IsAdministrator();
        var adminLabel = isAdmin
            ? $"[green]{UiTheme.IconSuccess}  администратор[/]"
            : $"[yellow]{UiTheme.IconWarning}  обычный пользователь[/]";

        AnsiConsole.Write(new FigletText("Amarin").Color(UiTheme.Primary));
        AnsiConsole.MarkupLine(
            "[grey]Помощник системного администратора Windows[/]\n" +
            $"[dim]v{Markup.Escape(RuntimeContext.AppVersion)}[/]  " +
            $"[grey]│[/] [cyan]{Markup.Escape(Environment.UserName)}@{Markup.Escape(Environment.MachineName)}[/]  " +
            $"[grey]│[/] {adminLabel}");
        AnsiConsole.WriteLine();
    }

    public void ShowStatusBar(
        string model,
        SessionMode sessionMode,
        bool readOnly,
        bool undoAvailable,
        string? balanceText = null)
    {
        var access = readOnly
            ? $"[yellow]{UiTheme.IconWarning}  диагностика[/]"
            : $"[green]{UiTheme.IconSuccess}  полный[/]";

        var undo = undoAvailable
            ? $"  [grey]│[/] [yellow]{UiTheme.IconUndo}  откат[/]"
            : string.Empty;

        var balance = string.IsNullOrWhiteSpace(balanceText)
            ? string.Empty
            : $"  [grey]│[/] [cyan]{Markup.Escape(balanceText)}[/]";

        AnsiConsole.MarkupLine(
            $"[grey]Модель: [cyan]{Markup.Escape(model)}[/]  [grey]│[/] " +
            $"Сессия: [cyan]{Markup.Escape(SessionModeParser.ToShortName(sessionMode))}[/]  [grey]│[/] " +
            $"Доступ: {access}{undo}{balance}[/]");
        AnsiConsole.MarkupLine(
            "[dim]Enter — отправить · / — команды · /help[/]");
        AnsiConsole.WriteLine();
    }

    public void ShowHelp()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(HotkeyHelp.BuildPanel());
        AnsiConsole.WriteLine();
    }

    public void ClearScreen()
    {
        try
        {
            AnsiConsole.Clear();
        }
        catch
        {
            ConsoleEncoding.WriteAnsi("\x1b[2J\x1b[H");
        }

        ConsoleInputRestore.Restore();
        Console.Out.Flush();
    }

    public Task<T> RunWithSpinnerAsync<T>(string message, Func<Task<T>> action) =>
        Task.FromResult(ConsoleInputRestore.RunWithSpinner(message, action));

    public void ShowStatus(string content)
    {
        var line = ToSingleLine(content, 100);
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        ThreadSafeConsole.WriteStyledLine(UiTheme.IconTool, $"[grey]{Markup.Escape(line)}[/]");
    }

    public void ShowAssistantMessage(string content)
    {
        var text = string.IsNullOrWhiteSpace(content)
            ? "Модель вернула пустой ответ."
            : content;

        ThreadSafeConsole.WriteAssistantMarkdown(text);
    }

    public void ShowToolCall(string toolName, string arguments)
    {
        if (toolName.Equals(AskUserTool.ToolName, StringComparison.OrdinalIgnoreCase) &&
            TryGetAskUserQuestion(arguments, out var question))
        {
            ThreadSafeConsole.WriteStyledLine(
                UiTheme.IconAsk,
                $"[magenta1]{Markup.Escape(toolName)} — {Markup.Escape(Truncate(question, 80))}[/]");
            return;
        }

        var icon = ToolIcon(toolName);
        var args = FormatToolArguments(arguments);
        ThreadSafeConsole.WriteStyledLine(
            icon,
            $"[yellow]{Markup.Escape(toolName)} {Markup.Escape(args)}[/]");
    }

    public void ShowToolResult(string toolName, ToolResult result)
    {
        if (result.Success)
        {
            if (result.HasImages)
            {
                var imageCount = result.GetImages().Count == 1
                    ? string.Empty
                    : $" {result.GetImages().Count}";
                var imageSummary = Truncate(result.Output, 120);
                ThreadSafeConsole.WriteStyledLine(
                    UiTheme.IconSuccess,
                    $"[green]{Markup.Escape(toolName)}[/] {UiTheme.IconImage}{imageCount} [green]{Markup.Escape(imageSummary)}[/]");
                return;
            }

            var summary = SummarizeOutput(result.Output);
            ThreadSafeConsole.WriteStyledLine(
                UiTheme.IconSuccess,
                $"[green]{Markup.Escape(toolName)} — {Markup.Escape(summary)}[/]");
            return;
        }

        ThreadSafeConsole.WriteFramed($"{UiTheme.IconError} {toolName}", Truncate(result.Output, 600));
    }

    public void ShowWarning(string message)
    {
        ThreadSafeConsole.WriteLine();
        ThreadSafeConsole.WriteStyledLine(UiTheme.IconWarning, $"[yellow]{Markup.Escape(message)}[/]");
        ThreadSafeConsole.WriteLine();
    }

    public void ShowError(string message)
    {
        ThreadSafeConsole.WriteLine();
        ThreadSafeConsole.WriteStyledLine(UiTheme.IconError, $"[red]{Markup.Escape(message)}[/]");
        ThreadSafeConsole.WriteLine();
    }

    public void ShowInfo(string message) =>
        ThreadSafeConsole.WriteStyledLine(UiTheme.IconInfo, $"[grey]{Markup.Escape(message)}[/]");

    public void ShowRequestCost(VeniceCost cost)
    {
        if (!cost.HasData)
        {
            ShowInfo("Стоимость: Venice не вернула данные (поле cost в ответе API).");
            return;
        }

        ThreadSafeConsole.WriteColoredLine($"Стоимость: {cost.Format()}", ConsoleColor.Cyan);
    }

    public void ShowBalance(VeniceBalance balance)
    {
        var amount = Markup.Escape(balance.Format());
        var currency = string.IsNullOrWhiteSpace(balance.ConsumptionCurrency)
            ? ""
            : $" [dim]({Markup.Escape(balance.ConsumptionCurrency)})[/]";

        if (!balance.CanConsume)
        {
            AnsiConsole.MarkupLine($"[yellow]Баланс:[/] {amount}{currency} [red]· недостаточно[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[grey]Баланс:[/] [cyan]{amount}[/]{currency}");
    }

    public void ShowSeparator() =>
        AnsiConsole.WriteLine();

    public void ShowUndoResult(UndoResult result)
    {
        AnsiConsole.WriteLine();

        if (!result.Success)
        {
            ShowError(result.RestoreOutput);
            return;
        }

        AnsiConsole.Write(UiTheme.CreatePanel(
            $"[green]{UiTheme.IconUndo} Откат выполнен[/]",
            FormatContent(result.RestoreOutput),
            UiTheme.Success,
            UiTheme.Success));

        if (!string.IsNullOrWhiteSpace(result.CompareOutput))
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(UiTheme.CreatePanel(
                "[cyan]Сравнение после отката[/]",
                FormatContent(result.CompareOutput),
                UiTheme.Border,
                UiTheme.Primary));
        }

        AnsiConsole.WriteLine();
    }

    public void ShowExportResult(ReportExportResult result)
    {
        if (!result.Success)
        {
            ShowError(result.Message);
            return;
        }

        ShowInfo(result.Message);
        foreach (var file in result.Files)
        {
            ShowInfo($"  → {file}");
        }
    }

    public void ShowSessionHistory(IReadOnlyList<SessionActionEntry> entries)
    {
        AnsiConsole.WriteLine();

        if (entries.Count == 0)
        {
            ShowInfo("Журнал действий пуст — инструменты ещё не вызывались в этой сессии.");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(UiTheme.Border)
            .AddColumn(new TableColumn("[grey]Время[/]").Centered())
            .AddColumn("[grey]Инструмент[/]")
            .AddColumn("[grey]Действие[/]")
            .AddColumn(new TableColumn("[grey]Статус[/]").Centered())
            .AddColumn("[grey]Результат[/]");

        foreach (var entry in entries.AsEnumerable().Reverse())
        {
            var status = entry.Success
                ? $"[green]{UiTheme.IconSuccess}[/]"
                : $"[red]{UiTheme.IconError}[/]";
            table.AddRow(
                entry.Time.ToString("HH:mm:ss"),
                Markup.Escape(entry.Tool),
                Markup.Escape(entry.Action ?? "—"),
                status,
                Markup.Escape(entry.Summary));
        }

        AnsiConsole.Write(UiTheme.CreatePanel(
            $"[cyan]Журнал действий[/] [dim]({entries.Count})[/]",
            table,
            UiTheme.Border,
            UiTheme.Primary));
        AnsiConsole.WriteLine();
    }

    public Task<bool> ConfirmDangerousActionAsync(
        DangerousActionInfo info,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        AnsiConsole.WriteLine();

        var explanation = string.IsNullOrWhiteSpace(info.Explanation)
            ? info.ChangeSummary
            : info.Explanation;

        var body = new Markup(
            $"[bold]Что изменится:[/] {Markup.Escape(info.ChangeSummary)}\n\n" +
            $"[bold]Влияние на систему:[/] {UiTheme.ImpactBadge(info.RiskLevel)}\n\n" +
            $"[bold]Объяснение:[/] {Markup.Escape(explanation)}\n\n" +
            $"[dim]{Markup.Escape(info.Details)}[/]\n\n" +
            "[grey]Проверьте детали и подтвердите, если согласны с выполнением.[/]");

        AnsiConsole.Write(UiTheme.CreatePanel(
            $"[yellow]{UiTheme.IconInfo} Требуется подтверждение[/]",
            body,
            UiTheme.Warning,
            UiTheme.Warning));
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  [cyan]1[/] / [green]да[/] — выполнить");
        AnsiConsole.MarkupLine("  [cyan]2[/] / [red]нет[/] — отменить");
        AnsiConsole.WriteLine();
        ConsoleInputRestore.PrepareForInput();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = ConsolePrompt.ReadSimpleLine(
                $"[bold yellow]{UiTheme.IconInfo} Подтверждение[/] [grey]›[/] ");

            var decision = ConfirmationInput.TryParse(input);
            if (decision is true)
            {
                return Task.FromResult(true);
            }

            if (decision is false)
            {
                return Task.FromResult(false);
            }

            AnsiConsole.MarkupLine("[yellow]Введите 1, да, 2 или нет[/]");
        }
    }

    public Task<string> PromptModelAsync(string current, IReadOnlyList<string> presets)
    {
        var overlayStart = ConsoleOverlayLines.CaptureTop();

        try
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[grey]Модель Venice[/] [dim](сохраняется в appsettings.json)[/]");
            AnsiConsole.WriteLine();

            // Numbered list aligned with presets order; tier headers for known presets.
            var numberById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < presets.Count; i++)
            {
                numberById[presets[i]] = i + 1;
            }

            var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Models not in catalog tiers (e.g. custom current) — show first without tier label.
            foreach (var id in presets)
            {
                if (VeniceModelCatalog.PresetModels.Contains(id, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                WriteModelChoice(numberById[id], id, current);
                shown.Add(id);
            }

            foreach (var tier in VeniceModelCatalog.Tiers)
            {
                AnsiConsole.MarkupLine(
                    $"[bold cyan]{Markup.Escape(tier.Title)}[/] [dim]({Markup.Escape(tier.Subtitle)})[/]");

                foreach (var id in tier.Models)
                {
                    if (!numberById.TryGetValue(id, out var num))
                    {
                        continue;
                    }

                    WriteModelChoice(num, id, current);
                    shown.Add(id);
                }

                AnsiConsole.WriteLine();
            }

            // Any remaining selectable IDs not covered above.
            foreach (var id in presets)
            {
                if (shown.Contains(id))
                {
                    continue;
                }

                WriteModelChoice(numberById[id], id, current);
            }

            AnsiConsole.MarkupLine("  [dim]или введите ID модели вручную · /model имя[/]");
            AnsiConsole.WriteLine();

            while (true)
            {
                var input = ConsolePrompt.ReadSimpleLine("[bold green]Модель[/] [grey]›[/] ").Trim();

                if (int.TryParse(input, out var index) && index >= 1 && index <= presets.Count)
                {
                    return Task.FromResult(presets[index - 1]);
                }

                if (!string.IsNullOrWhiteSpace(input))
                {
                    return Task.FromResult(input);
                }

                AnsiConsole.MarkupLine("[yellow]Введите номер или ID модели[/]");
            }
        }
        finally
        {
            ConsoleOverlayLines.ClearFrom(overlayStart);
            ConsoleInputRestore.Restore();
            Console.Out.Flush();
        }
    }

    private static void WriteModelChoice(int number, string id, string current)
    {
        var marker = id.Equals(current, StringComparison.OrdinalIgnoreCase)
            ? " [green](текущая)[/]"
            : string.Empty;
        AnsiConsole.MarkupLine($"  [cyan]{number}.[/] {Markup.Escape(id)}{marker}");
    }

    public Task<SessionMode> PromptSessionModeAsync(SessionMode current)
    {
        var overlayStart = ConsoleOverlayLines.CaptureTop();

        try
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[grey]Режим сессии[/] [dim](сохраняется в appsettings.json)[/]");
            AnsiConsole.MarkupLine($"  [cyan]1.[/] История сессии — Amarin помнит предыдущие вопросы{(current == SessionMode.Continuous ? " [green](текущий)[/]" : "")}");
            AnsiConsole.MarkupLine($"  [cyan]2.[/] Новая сессия — каждый вопрос с чистого листа{(current == SessionMode.Isolated ? " [green](текущий)[/]" : "")}");
            AnsiConsole.MarkupLine("  [dim]Сменить позже: /session[/]");
            AnsiConsole.WriteLine();

            while (true)
            {
                var input = ConsolePrompt.ReadSimpleLine("[bold green]Режим[/] [grey]›[/] ").Trim();

                if (input == "1")
                {
                    return Task.FromResult(SessionMode.Continuous);
                }

                if (input == "2")
                {
                    return Task.FromResult(SessionMode.Isolated);
                }

                AnsiConsole.MarkupLine("[yellow]Введите 1 или 2[/]");
            }
        }
        finally
        {
            ConsoleOverlayLines.ClearFrom(overlayStart);
            ConsoleInputRestore.Restore();
            Console.Out.Flush();
        }
    }

    public Task<string> PromptUserChoiceAsync(
        string question,
        IReadOnlyList<string> options,
        bool allowCustomAnswer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        AnsiConsole.WriteLine();
        AnsiConsole.Write(UiTheme.CreatePanel(
            $"[mediumpurple1]{UiTheme.IconAsk} Amarin спрашивает[/]",
            new Markup(MarkdownFormatter.ToSpectreMarkup(question)),
            UiTheme.AskUser,
            UiTheme.AskUser));
        AnsiConsole.WriteLine();

        for (var i = 0; i < options.Count; i++)
        {
            AnsiConsole.MarkupLine($"  [mediumpurple1]{i + 1}.[/] {MarkdownFormatter.ToSpectreMarkup(options[i])}");
        }

        if (allowCustomAnswer)
        {
            AnsiConsole.MarkupLine("  [grey]или введите свой ответ текстом[/]");
        }

        AnsiConsole.WriteLine();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var input = ConsolePrompt.ReadSimpleLine(
                $"[bold mediumpurple1]{UiTheme.IconAsk} Выбор[/] [grey]›[/] ").Trim();

            if (int.TryParse(input, out var number) && number >= 1 && number <= options.Count)
            {
                return Task.FromResult(options[number - 1]);
            }

            if (allowCustomAnswer && !string.IsNullOrWhiteSpace(input))
            {
                return Task.FromResult(input);
            }

            var hint = allowCustomAnswer
                ? $"[yellow]Введите номер от 1 до {options.Count} или свой ответ[/]"
                : $"[yellow]Введите номер от 1 до {options.Count}[/]";
            AnsiConsole.MarkupLine(hint);
        }
    }

    private static string ToolIcon(string toolName) => toolName.ToLowerInvariant() switch
    {
        "search_web" or "scrape_url" => UiTheme.IconSearch,
        "capture_screenshot" or "read_clipboard" or "analyze_folder" => UiTheme.IconImage,
        "change_rollback" => UiTheme.IconUndo,
        "ask_user" => UiTheme.IconAsk,
        _ => UiTheme.IconTool
    };

    private static bool TryGetAskUserQuestion(string arguments, out string question)
    {
        question = string.Empty;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(arguments);
            if (doc.RootElement.TryGetProperty("question", out var prop))
            {
                question = prop.GetString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(question);
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static IRenderable FormatContent(string content) =>
        SafeRenderable.FromMarkdown(content);

    private static string FormatToolArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments) || arguments == "{}")
        {
            return "";
        }

        return Truncate(ToSingleLine(arguments, 200), 80);
    }

    private static string SummarizeOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return "готово";
        }

        var lines = output
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith("---", StringComparison.Ordinal))
            .ToList();

        if (lines.Count == 0)
        {
            return "готово";
        }

        var first = ToSingleLine(lines[0], 70);
        return lines.Count == 1 ? first : $"{first} (+ещё {lines.Count - 1})";
    }

    private static string ToSingleLine(string text, int maxLength)
    {
        var line = text.Replace("\r\n", " ").Replace('\n', ' ').Trim();
        while (line.Contains("  ", StringComparison.Ordinal))
        {
            line = line.Replace("  ", " ", StringComparison.Ordinal);
        }

        return Truncate(line, maxLength);
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "…";
}