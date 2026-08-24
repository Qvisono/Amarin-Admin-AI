using System.Net;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Amarin.Core;

public enum ReportExportFormat
{
    Html,
    Markdown,
    Pdf,
    All
}

public sealed record ReportExportResult(bool Success, string Message, IReadOnlyList<string> Files)
{
    public static ReportExportResult Ok(string message, IReadOnlyList<string> files) =>
        new(true, message, files);

    public static ReportExportResult Fail(string message) =>
        new(false, message, []);
}

public static class SessionReportExporter
{
    private static string ReportsRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AmarinAdminAI", "reports");

    public static ReportExportResult Export(
        ReportExportFormat format,
        SessionReportCollector collector,
        SessionActionLog actionLog,
        SessionUndoTracker undoTracker,
        string model,
        SessionMode sessionMode,
        bool readOnlyMode)
    {
        if (collector.Turns.Count == 0 && actionLog.Entries.Count == 0)
        {
            return ReportExportResult.Fail("Нечего экспортировать — сессия пуста.");
        }

        Directory.CreateDirectory(ReportsRoot);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var folder = Path.Combine(ReportsRoot, stamp);
        Directory.CreateDirectory(folder);

        var context = BuildContext(collector, actionLog, undoTracker, model, sessionMode, readOnlyMode);
        var files = new List<string>();
        var errors = new List<string>();

        if (format is ReportExportFormat.Html or ReportExportFormat.All)
        {
            TryExport("HTML", () =>
            {
                var path = Path.Combine(folder, "report.html");
                File.WriteAllText(path, BuildHtml(context), Encoding.UTF8);
                files.Add(path);
            }, errors);
        }

        if (format is ReportExportFormat.Markdown or ReportExportFormat.All)
        {
            TryExport("Markdown", () =>
            {
                var path = Path.Combine(folder, "report.md");
                File.WriteAllText(path, BuildMarkdown(context), Encoding.UTF8);
                files.Add(path);
            }, errors);
        }

        if (format is ReportExportFormat.Pdf or ReportExportFormat.All)
        {
            TryExport("PDF", () =>
            {
                var path = Path.Combine(folder, "report.pdf");
                BuildPdf(context, path);
                files.Add(path);
            }, errors);
        }

        if (files.Count == 0)
        {
            var details = errors.Count > 0 ? string.Join("; ", errors) : "неизвестная ошибка";
            return ReportExportResult.Fail($"Ошибка экспорта: {details}");
        }

        if (errors.Count > 0)
        {
            return ReportExportResult.Ok(
                $"Отчёт частично сохранён: {folder} ({string.Join("; ", errors)})",
                files);
        }

        return ReportExportResult.Ok($"Отчёт сохранён: {folder}", files);
    }

    private static void TryExport(string label, Action action, List<string> errors)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            errors.Add($"{label}: {ex.Message}");
        }
    }

    public static bool TryParseFormat(string? arg, out ReportExportFormat format)
    {
        format = ReportExportFormat.All;
        if (string.IsNullOrWhiteSpace(arg))
        {
            return true;
        }

        format = arg.Trim().ToLowerInvariant() switch
        {
            "html" or "htm" => ReportExportFormat.Html,
            "md" or "markdown" => ReportExportFormat.Markdown,
            "pdf" => ReportExportFormat.Pdf,
            "all" => ReportExportFormat.All,
            _ => ReportExportFormat.All
        };

        return arg.Trim().ToLowerInvariant() is "html" or "htm" or "md" or "markdown" or "pdf" or "all";
    }

    private static ReportContext BuildContext(
        SessionReportCollector collector,
        SessionActionLog actionLog,
        SessionUndoTracker undoTracker,
        string model,
        SessionMode sessionMode,
        bool readOnlyMode)
    {
        var turns = collector.Turns.Select(turn => new ReportTurn(
            turn.TurnId,
            turn.StartedAt,
            turn.UserRequest,
            turn.AssistantResponse,
            turn.UndoSnapshotId,
            turn.RequestCost,
            actionLog.GetEntriesForTurn(turn.TurnId).ToList())).ToList();

        return new ReportContext(
            DateTime.Now,
            Environment.MachineName,
            Environment.UserName,
            model,
            SessionModeParser.ToDisplayName(sessionMode),
            readOnlyMode,
            undoTracker.HasUndoPoint,
            undoTracker.UndoSnapshotId,
            turns);
    }

    private static string BuildMarkdown(ReportContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Amarin Admin AI — отчёт сессии");
        sb.AppendLine();
        sb.AppendLine($"- **Дата:** {context.GeneratedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **Компьютер:** {context.MachineName}");
        sb.AppendLine($"- **Пользователь:** {context.UserName}");
        sb.AppendLine($"- **Модель:** {context.Model}");
        sb.AppendLine($"- **Режим сессии:** {context.SessionMode}");
        sb.AppendLine($"- **Доступ:** {(context.ReadOnlyMode ? "только диагностика" : "полный")}");

        if (context.HasUndoPoint)
        {
            sb.AppendLine($"- **Точка отката:** {context.UndoSnapshotId}");
        }

        sb.AppendLine();

        foreach (var turn in context.Turns)
        {
            sb.AppendLine($"## Запрос {turn.TurnId} — {turn.StartedAt:HH:mm:ss}");
            sb.AppendLine();
            sb.AppendLine("### Вопрос пользователя");
            sb.AppendLine();
            sb.AppendLine(turn.UserRequest);
            sb.AppendLine();

            if (turn.Actions.Count > 0)
            {
                sb.AppendLine("### Действия");
                sb.AppendLine();
                sb.AppendLine("| Время | Инструмент | Действие | Статус |");
                sb.AppendLine("|-------|------------|----------|--------|");

                foreach (var action in turn.Actions)
                {
                    var status = action.Success ? "✓" : "✗";
                    sb.AppendLine(
                        $"| {action.Time:HH:mm:ss} | {EscapeMdCell(action.Tool)} | {EscapeMdCell(action.Action ?? "—")} | {status} |");
                }

                sb.AppendLine();

                foreach (var action in turn.Actions)
                {
                    sb.AppendLine($"#### {action.Tool} — {action.Action ?? "—"} ({(action.Success ? "ok" : "ошибка")})");
                    sb.AppendLine();
                    sb.AppendLine("```");
                    sb.AppendLine(action.FullOutput);
                    sb.AppendLine("```");
                    sb.AppendLine();
                }
            }

            if (!string.IsNullOrWhiteSpace(turn.AssistantResponse))
            {
                sb.AppendLine("### Ответ Amarin");
                sb.AppendLine();
                sb.AppendLine(turn.AssistantResponse);
                sb.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(turn.UndoSnapshotId))
            {
                sb.AppendLine($"*Снимок для отката: `{turn.UndoSnapshotId}`*");
                sb.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(turn.RequestCost))
            {
                sb.AppendLine($"*Стоимость запроса: {turn.RequestCost}*");
                sb.AppendLine();
            }
        }

        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string BuildHtml(ReportContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"ru\"><head><meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.AppendLine("<title>Amarin Admin AI — отчёт</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("body{font-family:Segoe UI,system-ui,sans-serif;margin:2rem auto;max-width:960px;line-height:1.5;color:#1a1a1a;background:#f8f9fa}");
        sb.AppendLine("h1,h2,h3{color:#0d6efd}table{border-collapse:collapse;width:100%;margin:1rem 0;background:#fff}");
        sb.AppendLine("th,td{border:1px solid #dee2e6;padding:.5rem .75rem;text-align:left;vertical-align:top}");
        sb.AppendLine("th{background:#e9ecef}.ok{color:#198754}.fail{color:#dc3545}");
        sb.AppendLine("pre{background:#212529;color:#f8f9fa;padding:1rem;border-radius:6px;overflow:auto;white-space:pre-wrap}");
        sb.AppendLine(".meta{background:#fff;border:1px solid #dee2e6;border-radius:8px;padding:1rem;margin-bottom:1.5rem}");
        sb.AppendLine(".turn{background:#fff;border:1px solid #dee2e6;border-radius:8px;padding:1.25rem;margin-bottom:1.5rem}");
        sb.AppendLine(".answer{background:#e7f1ff;border-left:4px solid #0d6efd;padding:1rem;border-radius:4px}");
        sb.AppendLine("</style></head><body>");
        sb.AppendLine("<h1>Amarin Admin AI — отчёт сессии</h1>");
        sb.AppendLine("<div class=\"meta\"><ul>");
        sb.AppendLine($"<li><strong>Дата:</strong> {WebUtility.HtmlEncode(context.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss"))}</li>");
        sb.AppendLine($"<li><strong>Компьютер:</strong> {WebUtility.HtmlEncode(context.MachineName)}</li>");
        sb.AppendLine($"<li><strong>Пользователь:</strong> {WebUtility.HtmlEncode(context.UserName)}</li>");
        sb.AppendLine($"<li><strong>Модель:</strong> {WebUtility.HtmlEncode(context.Model)}</li>");
        sb.AppendLine($"<li><strong>Режим сессии:</strong> {WebUtility.HtmlEncode(context.SessionMode)}</li>");
        sb.AppendLine($"<li><strong>Доступ:</strong> {(context.ReadOnlyMode ? "только диагностика" : "полный")}</li>");

        if (context.HasUndoPoint)
        {
            sb.AppendLine($"<li><strong>Точка отката:</strong> {WebUtility.HtmlEncode(context.UndoSnapshotId ?? "")}</li>");
        }

        sb.AppendLine("</ul></div>");

        foreach (var turn in context.Turns)
        {
            sb.AppendLine($"<div class=\"turn\"><h2>Запрос {turn.TurnId} — {turn.StartedAt:HH:mm:ss}</h2>");
            sb.AppendLine("<h3>Вопрос пользователя</h3>");
            sb.AppendLine($"<p>{WebUtility.HtmlEncode(turn.UserRequest).Replace("\n", "<br>", StringComparison.Ordinal)}</p>");

            if (turn.Actions.Count > 0)
            {
                sb.AppendLine("<h3>Действия</h3><table><thead><tr><th>Время</th><th>Инструмент</th><th>Действие</th><th>Статус</th></tr></thead><tbody>");
                foreach (var action in turn.Actions)
                {
                    var statusClass = action.Success ? "ok" : "fail";
                    var status = action.Success ? "✓" : "✗";
                    sb.AppendLine(
                        $"<tr><td>{action.Time:HH:mm:ss}</td><td>{WebUtility.HtmlEncode(action.Tool)}</td>" +
                        $"<td>{WebUtility.HtmlEncode(action.Action ?? "—")}</td><td class=\"{statusClass}\">{status}</td></tr>");
                }

                sb.AppendLine("</tbody></table>");

                foreach (var action in turn.Actions)
                {
                    sb.AppendLine($"<h4>{WebUtility.HtmlEncode(action.Tool)} — {WebUtility.HtmlEncode(action.Action ?? "—")}</h4>");
                    sb.AppendLine($"<pre>{WebUtility.HtmlEncode(action.FullOutput)}</pre>");
                }
            }

            if (!string.IsNullOrWhiteSpace(turn.AssistantResponse))
            {
                sb.AppendLine("<h3>Ответ Amarin</h3>");
                sb.AppendLine($"<div class=\"answer\">{WebUtility.HtmlEncode(turn.AssistantResponse).Replace("\n", "<br>", StringComparison.Ordinal)}</div>");
            }

            if (!string.IsNullOrWhiteSpace(turn.UndoSnapshotId))
            {
                sb.AppendLine($"<p><em>Снимок для отката: {WebUtility.HtmlEncode(turn.UndoSnapshotId)}</em></p>");
            }

            if (!string.IsNullOrWhiteSpace(turn.RequestCost))
            {
                sb.AppendLine($"<p><em>Стоимость запроса: {WebUtility.HtmlEncode(turn.RequestCost)}</em></p>");
            }

            sb.AppendLine("</div>");
        }

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static void EnsurePdfFonts()
    {
        PdfSharp.Fonts.GlobalFontSettings.UseWindowsFontsUnderWindows = true;
    }

    private static void BuildPdf(ReportContext context, string path)
    {
        EnsurePdfFonts();
        var document = new PdfDocument();
        document.Info.Title = "Amarin Admin AI Report";
        document.Info.Author = "Amarin Admin AI";

        var font = new XFont("Arial", 10, XFontStyleEx.Regular);
        var fontBold = new XFont("Arial", 10, XFontStyleEx.Bold);
        var fontTitle = new XFont("Arial", 16, XFontStyleEx.Bold);
        var fontHeading = new XFont("Arial", 12, XFontStyleEx.Bold);

        var page = document.AddPage();
        var gfx = XGraphics.FromPdfPage(page);
        var y = XUnit.FromPoint(40);
        var left = XUnit.FromPoint(40);
        var right = page.Width - XUnit.FromPoint(40);
        var maxWidth = right - left;

        void NewPageIfNeeded(double height)
        {
            if (y.Point + height <= page.Height.Point - 40)
            {
                return;
            }

            page = document.AddPage();
            gfx = XGraphics.FromPdfPage(page);
            y = XUnit.FromPoint(40);
        }

        void DrawLines(string text, XFont f, double lineHeight, double gapAfter = 8)
        {
            foreach (var line in WrapText(text, f, maxWidth.Point))
            {
                NewPageIfNeeded(lineHeight);
                gfx.DrawString(line, f, XBrushes.Black, left, y);
                y += XUnit.FromPoint(lineHeight);
            }

            y += XUnit.FromPoint(gapAfter);
        }

        gfx.DrawString("Amarin Admin AI — отчёт сессии", fontTitle, XBrushes.Black, left, y);
        y += XUnit.FromPoint(28);

        DrawLines($"Дата: {context.GeneratedAt:yyyy-MM-dd HH:mm:ss}", font, 14, 2);
        DrawLines($"Компьютер: {context.MachineName} · Пользователь: {context.UserName}", font, 14, 2);
        DrawLines($"Модель: {context.Model} · Сессия: {context.SessionMode} · Доступ: {(context.ReadOnlyMode ? "диагностика" : "полный")}", font, 14, 6);

        if (context.HasUndoPoint)
        {
            DrawLines($"Точка отката: {context.UndoSnapshotId}", font, 14, 10);
        }

        foreach (var turn in context.Turns)
        {
            NewPageIfNeeded(24);
            gfx.DrawString($"Запрос {turn.TurnId} — {turn.StartedAt:HH:mm:ss}", fontHeading, XBrushes.Black, left, y);
            y += XUnit.FromPoint(20);

            DrawLines("Вопрос:", fontBold, 14, 2);
            DrawLines(turn.UserRequest, font, 14, 8);

            foreach (var action in turn.Actions)
            {
                var header = $"{action.Time:HH:mm:ss} · {action.Tool} · {action.Action ?? "—"} · {(action.Success ? "OK" : "FAIL")}";
                DrawLines(header, fontBold, 14, 2);
                DrawLines(action.FullOutput, font, 12, 6);
            }

            if (!string.IsNullOrWhiteSpace(turn.AssistantResponse))
            {
                DrawLines("Ответ Amarin:", fontBold, 14, 2);
                DrawLines(turn.AssistantResponse, font, 14, 8);
            }
        }

        document.Save(path);
    }

    private static IEnumerable<string> WrapText(string text, XFont font, double maxWidth)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield return string.Empty;
            yield break;
        }

        using var gfx = XGraphics.CreateMeasureContext(
            new XSize(maxWidth, 1000),
            XGraphicsUnit.Point,
            XPageDirection.Downwards);

        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(paragraph))
            {
                yield return string.Empty;
                continue;
            }

            var words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var line = new StringBuilder();

            foreach (var word in words)
            {
                var candidate = line.Length == 0 ? word : $"{line} {word}";
                var size = gfx.MeasureString(candidate, font);

                if (size.Width > maxWidth && line.Length > 0)
                {
                    yield return line.ToString();
                    line.Clear().Append(word);
                }
                else
                {
                    line.Clear().Append(candidate);
                }
            }

            if (line.Length > 0)
            {
                yield return line.ToString();
            }
        }
    }

    private static string EscapeMdCell(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal);

    private sealed record ReportContext(
        DateTime GeneratedAt,
        string MachineName,
        string UserName,
        string Model,
        string SessionMode,
        bool ReadOnlyMode,
        bool HasUndoPoint,
        string? UndoSnapshotId,
        IReadOnlyList<ReportTurn> Turns);

    private sealed record ReportTurn(
        int TurnId,
        DateTime StartedAt,
        string UserRequest,
        string? AssistantResponse,
        string? UndoSnapshotId,
        string? RequestCost,
        IReadOnlyList<SessionActionEntry> Actions);
}