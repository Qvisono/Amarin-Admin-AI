using System.Globalization;
using System.Net;
using System.Text;
using Amarin.Tools;
using Markdig;
using Markdig.Extensions.Mathematics;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Amarin.Core;

/// <summary>Что положить в выгрузку чата (D6).</summary>
/// <param name="IncludeTools">Раунды инструментов: вызовы, аргументы и итоги.</param>
/// <param name="IncludeCosts">Цена у каждого ответа и итог в шапке.</param>
/// <param name="UpToMessageId">Выгрузить до этого сообщения включительно; null — весь чат.</param>
public sealed record ChatExportOptions(bool IncludeTools = false, bool IncludeCosts = false, string? UpToMessageId = null);

internal sealed record ExportTool(string Name, string Arguments, string Result, bool Success);

internal sealed record ExportMessage(
    string Role,
    DateTime Created,
    string Text,
    string? Model,
    decimal? CostUsd,
    IReadOnlyList<MessageQuote> Quotes,
    IReadOnlyList<ImageAttachment> Images,
    IReadOnlyList<string> Files,
    IReadOnlyList<ExportTool> Tools);

internal sealed record ChatExportDocument(
    string Title,
    DateTime Created,
    DateTime Exported,
    IReadOnlyList<ExportMessage> Messages,
    decimal TotalUsd,
    bool IncludeCosts);

/// <summary>
/// Выгрузка чата в Markdown и самодостаточный HTML (D6). Печать и PDF собирает окно из того же
/// документа (<c>ChatPrint</c>).
/// </summary>
/// <remarks>
/// <para>
/// Только показанная ветка — как у «Поделиться»: спрятанные варианты человек в этот момент не
/// видит, и выгрузка «всего» перемешала бы разговоры, которые друг друга исключают.
/// </para>
/// <para>
/// HTML открывают в браузере, а текст в нём — от модели, то есть из сети. Поэтому разметка
/// HTML в сообщениях не пропускается (<c>DisableHtml</c>), ссылки берутся только http(s) и
/// mailto, картинки — только вложенные (data:), а политика безопасности страницы запрещает любые скрипты.
/// </para>
/// </remarks>
internal static class ChatExport
{
    /// <summary>Сколько знаков результата инструмента класть в выгрузку: дальше — журнал.</summary>
    internal const int ToolResultLimit = 4000;

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UsePipeTables()
        .UseGridTables()
        .UseEmphasisExtras()
        .UseTaskLists()
        .UseListExtras()
        .UseAutoLinks()
        .UseMathematics()
        .Build();

    public static ChatExportDocument Build(ChatSession session, ChatExportOptions options, DateTime now)
    {
        lock (session.Gate)
        {
            var source = session.Messages;
            var cut = source.Count;
            if (!string.IsNullOrWhiteSpace(options.UpToMessageId))
            {
                var at = source.FindIndex(message => message.Id == options.UpToMessageId);
                if (at >= 0)
                {
                    cut = at + 1;
                }
            }

            var messages = new List<ExportMessage>(cut);
            var total = 0m;
            foreach (var message in source.Take(cut))
            {
                if (message.Role is not ("user" or "assistant"))
                {
                    continue;
                }

                var cost = message.Cost is { HasData: true } priced ? priced.Usd : (decimal?)null;
                total += cost ?? 0;
                var images = new List<ImageAttachment>(message.Images);
                var files = message.Files.Select(file => file.FileName).ToList();
                var tools = new List<ExportTool>();
                foreach (var call in message.ToolRounds.SelectMany(round => round.Calls))
                {
                    // Картинки инструмента (нарисованная, снимок экрана) — часть ответа, их видно в
                    // ленте и без раскрытия блока; поэтому они едут всегда, а не только с инструментами.
                    images.AddRange(call.Images);
                    files.AddRange(call.SavedFiles.Select(file => file.Path));
                    if (options.IncludeTools)
                    {
                        var result = string.IsNullOrWhiteSpace(call.ResultText) ? call.ResultPreview : call.ResultText;
                        tools.Add(new ExportTool(call.Name, call.ArgumentsJson, Clip(result), call.Success));
                    }
                }

                var model = message.Role == "assistant"
                    ? message.ResolvedModelId ?? message.RequestedModelId
                    : null;
                messages.Add(new ExportMessage(
                    message.Role,
                    message.CreatedAt,
                    message.Text,
                    string.IsNullOrWhiteSpace(model) ? null : VeniceModelCatalog.GetDisplayName(model),
                    options.IncludeCosts ? cost : null,
                    [.. message.Quotes],
                    images,
                    files.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    tools));
            }

            return new ChatExportDocument(
                ChatTitle.IsDefault(session.Title) ? Loc.Get("S.ChatList.NewChat") : session.Title,
                session.CreatedAt,
                now,
                messages,
                total,
                options.IncludeCosts);
        }
    }

    private static string Clip(string text) =>
        text.Length <= ToolResultLimit ? text : text[..ToolResultLimit] + "\n…";

    internal static string Stamp(DateTime at) => at == default ? "" : at.ToString("g", CultureInfo.CurrentCulture);

    internal static string Money(decimal usd) =>
        "$" + usd.ToString(usd < 0.01m ? "0.0000" : "0.00", CultureInfo.InvariantCulture);

    internal static string RoleLabel(ExportMessage message) =>
        message.Role == "user" ? Loc.Get("S.Export.You") : message.Model ?? Loc.Get("S.Export.Assistant");

    /// <summary>Подпись под заголовком: когда выгружено, сколько сообщений, во что обошлось.</summary>
    internal static string Subtitle(ChatExportDocument document)
    {
        var parts = new List<string>
        {
            Loc.Format("S.Export.ExportedAt", Stamp(document.Exported)),
            Loc.Format("S.Export.MessageCount", document.Messages.Count)
        };
        if (document.IncludeCosts && document.TotalUsd > 0)
        {
            parts.Add(Money(document.TotalUsd));
        }

        return string.Join(" · ", parts);
    }

    internal static string MessageMeta(ExportMessage message, bool includeCosts)
    {
        var parts = new List<string> { RoleLabel(message) };
        if (message.Created != default)
        {
            parts.Add(Stamp(message.Created));
        }

        if (includeCosts && message.CostUsd is { } cost)
        {
            parts.Add(Money(cost));
        }

        return string.Join(" · ", parts);
    }

    // ───────────────────────── Markdown ─────────────────────────

    public static string ToMarkdown(ChatExportDocument document)
    {
        var builder = new StringBuilder();
        builder.Append("# ").AppendLine(document.Title.ReplaceLineEndings(" ")).AppendLine();
        builder.Append('_').Append(Subtitle(document)).AppendLine("_").AppendLine();

        foreach (var message in document.Messages)
        {
            builder.AppendLine("---").AppendLine();
            builder.Append("### ").AppendLine(MessageMeta(message, document.IncludeCosts)).AppendLine();

            foreach (var quote in message.Quotes)
            {
                foreach (var line in quote.Text.ReplaceLineEndings("\n").Split('\n'))
                {
                    builder.Append("> ").AppendLine(line);
                }

                builder.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(message.Text))
            {
                builder.AppendLine(message.Text.Trim()).AppendLine();
            }

            foreach (var image in message.Images)
            {
                builder.Append("![").Append(EscapeLabel(image.Label ?? Loc.Get("S.Export.Image"))).Append("](")
                    .Append(DataUri(image)).AppendLine(")").AppendLine();
            }

            if (message.Files.Count > 0)
            {
                builder.Append("**").Append(Loc.Get("S.Export.Files")).AppendLine("**").AppendLine();
                foreach (var file in message.Files)
                {
                    builder.Append("- `").Append(file.Replace('`', '\'')).AppendLine("`");
                }

                builder.AppendLine();
            }

            foreach (var tool in message.Tools)
            {
                builder.Append("**").Append(Loc.Get("S.Export.Tool")).Append(":** `").Append(tool.Name).Append('`')
                    .AppendLine(tool.Success ? "" : " - " + Loc.Get("S.Export.ToolFailed")).AppendLine();
                AppendFence(builder, "json", tool.Arguments);
                if (!string.IsNullOrWhiteSpace(tool.Result))
                {
                    AppendFence(builder, "text", tool.Result);
                }
            }
        }

        return builder.ToString();
    }

    private static string EscapeLabel(string label) => label.Replace("[", "(").Replace("]", ")").ReplaceLineEndings(" ");

    /// <summary>Ограда длиннее самой длинной серии обратных апострофов внутри: иначе код из вывода её закрыл бы.</summary>
    private static void AppendFence(StringBuilder builder, string language, string body)
    {
        var longest = 0;
        var run = 0;
        foreach (var symbol in body)
        {
            run = symbol == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        var fence = new string('`', Math.Max(3, longest + 1));
        builder.Append(fence).AppendLine(language).AppendLine(body.TrimEnd()).AppendLine(fence).AppendLine();
    }

    internal static string DataUri(ImageAttachment image) =>
        "data:" + (SafeImageType(image.MimeType) ?? "image/png") + ";base64," + image.Base64;

    /// <summary>Только растровые типы: SVG в data: — это документ со своими скриптами.</summary>
    private static string? SafeImageType(string? mime) => mime?.ToLowerInvariant() switch
    {
        "image/png" or "image/jpeg" or "image/jpg" or "image/gif" or "image/webp" or "image/bmp" => mime.ToLowerInvariant(),
        _ => null
    };

    // ───────────────────────── HTML ─────────────────────────

    /// <param name="formula">
    /// Картинка формулы — data:-адрес PNG, или null, если нарисовать не вышло (тогда в выгрузке
    /// остаётся сам LaTeX). Рисует окно: движок формул живёт в WPF.
    /// </param>
    public static string ToHtml(ChatExportDocument document, Func<string, bool, string?>? formula = null)
    {
        var html = new StringBuilder();
        AppendHead(html, document.Title);
        html.AppendLine("<body><main>");
        html.Append("<header><h1>").Append(Encode(document.Title)).Append("</h1><p class=\"sub\">")
            .Append(Encode(Subtitle(document))).AppendLine("</p></header>");

        foreach (var message in document.Messages)
        {
            html.Append("<article class=\"msg ").Append(message.Role == "user" ? "user" : "assistant").AppendLine("\">");
            html.Append("<div class=\"meta\">").Append(Encode(MessageMeta(message, document.IncludeCosts))).AppendLine("</div>");

            foreach (var quote in message.Quotes)
            {
                html.Append("<blockquote>").Append(Encode(quote.Text).Replace("\n", "<br>")).AppendLine("</blockquote>");
            }

            if (!string.IsNullOrWhiteSpace(message.Text))
            {
                html.Append("<div class=\"body\">").Append(RenderMarkdown(message.Text, formula)).AppendLine("</div>");
            }

            foreach (var image in message.Images)
            {
                html.Append("<figure><img src=\"").Append(Encode(DataUri(image))).Append("\" alt=\"")
                    .Append(Encode(image.Label ?? Loc.Get("S.Export.Image"))).AppendLine("\"></figure>");
            }

            if (message.Files.Count > 0)
            {
                html.Append("<div class=\"files\"><b>").Append(Encode(Loc.Get("S.Export.Files"))).Append("</b><ul>");
                foreach (var file in message.Files)
                {
                    html.Append("<li><code>").Append(Encode(file)).Append("</code></li>");
                }

                html.AppendLine("</ul></div>");
            }

            if (message.Tools.Count > 0)
            {
                html.Append("<details class=\"tools\"><summary>")
                    .Append(Encode(Loc.Format("S.Export.ToolCount", message.Tools.Count))).AppendLine("</summary>");
                foreach (var tool in message.Tools)
                {
                    html.Append("<div class=\"tool").Append(tool.Success ? "" : " failed").Append("\"><div class=\"name\"><code>")
                        .Append(Encode(tool.Name)).Append("</code>");
                    if (!tool.Success)
                    {
                        html.Append(" - ").Append(Encode(Loc.Get("S.Export.ToolFailed")));
                    }

                    html.Append("</div><pre>").Append(Encode(tool.Arguments)).Append("</pre>");
                    if (!string.IsNullOrWhiteSpace(tool.Result))
                    {
                        html.Append("<pre class=\"result\">").Append(Encode(tool.Result)).Append("</pre>");
                    }

                    html.AppendLine("</div>");
                }

                html.AppendLine("</details>");
            }

            html.AppendLine("</article>");
        }

        html.AppendLine("</main></body></html>");
        return html.ToString();
    }

    /// <summary>
    /// Страница из готового Markdown — для отчёта о работе (D7): та же оболочка, стили и политика
    /// безопасности, что у выгрузки чата.
    /// </summary>
    public static string MarkdownPage(string title, string markdown, Func<string, bool, string?>? formula = null)
    {
        var html = new StringBuilder();
        AppendHead(html, title);
        html.AppendLine("<body><main><article class=\"doc body\">");
        html.Append(RenderMarkdown(markdown, formula));
        html.AppendLine("</article></main></body></html>");
        return html.ToString();
    }

    private static void AppendHead(StringBuilder html, string title)
    {
        html.AppendLine("<!DOCTYPE html>");
        html.Append("<html lang=\"").Append(Encode(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName)).AppendLine("\">");
        html.AppendLine("<head>");
        html.AppendLine("<meta charset=\"utf-8\">");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        // Ни одного скрипта и ни одного внешнего запроса: выгрузка открывается без сети и не
        // исполняет ничего, что могло приехать в тексте модели.
        html.AppendLine("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src data:; style-src 'unsafe-inline'\">");
        html.Append("<title>").Append(Encode(title)).AppendLine("</title>");
        html.Append("<style>").Append(Css).AppendLine("</style>");
        html.AppendLine("</head>");
    }

    /// <summary>Разметка одного сообщения в HTML — тем же набором расширений, что и лента.</summary>
    internal static string RenderMarkdown(string text, Func<string, bool, string?>? formula)
    {
        var normalized = MathDelimiterNormalizer.ToDollars(text.Replace("\r\n", "\n").Replace('\r', '\n'));
        var document = Markdown.Parse(normalized, Pipeline);
        foreach (var link in document.Descendants<LinkInline>())
        {
            if (!IsSafeUrl(link.Url, link.IsImage))
            {
                link.Url = "#";
            }
        }

        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.ObjectRenderers.ReplaceOrAdd<HtmlMathInlineRenderer>(new MathInlineRenderer(formula));
        renderer.ObjectRenderers.ReplaceOrAdd<HtmlMathBlockRenderer>(new MathBlockRenderer(formula));
        renderer.Render(document);
        writer.Flush();
        return writer.ToString();
    }

    internal static bool IsSafeUrl(string? url, bool image)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return true;
        }

        var trimmed = url.Trim();
        if (trimmed.StartsWith('#'))
        {
            return true;
        }

        if (image)
        {
            // Картинка из сети — это запрос наружу при каждом открытии файла, то есть метка
            // «прочитано»; в выгрузке остаются только вложенные.
            return trimmed.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) &&
                   !trimmed.StartsWith("data:image/svg", StringComparison.OrdinalIgnoreCase);
        }

        return trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);
    }

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    /// <param name="block">Формула — отдельный блок: без картинки остаётся блоком кода, а не строчным.</param>
    private static string FormulaHtml(string latex, bool display, bool block, Func<string, bool, string?>? formula)
    {
        var picture = formula?.Invoke(latex, display);
        if (picture is not null && picture.StartsWith("data:image/png;base64,", StringComparison.Ordinal))
        {
            return "<img class=\"math" + (display ? " display" : "") + "\" src=\"" + picture + "\" alt=\"" + Encode(latex) + "\">";
        }

        return block
            ? "<pre class=\"math\">" + Encode(latex) + "</pre>"
            : "<code class=\"math\">" + Encode(latex) + "</code>";
    }

    /// <summary>Формула в строке — картинкой; одиночные доллары вокруг не-математики — как было.</summary>
    private sealed class MathInlineRenderer(Func<string, bool, string?>? formula) : HtmlObjectRenderer<MathInline>
    {
        protected override void Write(HtmlRenderer renderer, MathInline math)
        {
            var content = math.Content.ToString();
            if (math.DelimiterCount < 2 && !MathDetection.LooksLikeMath(content))
            {
                var delimiters = new string(math.Delimiter, math.DelimiterCount);
                renderer.WriteEscape(delimiters + content + delimiters);
                return;
            }

            // $$…$$ посреди абзаца лента рисует выключной формулой — так же и здесь.
            renderer.Write(FormulaHtml(content, display: math.DelimiterCount >= 2, block: false, formula));
        }
    }

    private sealed class MathBlockRenderer(Func<string, bool, string?>? formula) : HtmlObjectRenderer<MathBlock>
    {
        protected override void Write(HtmlRenderer renderer, MathBlock block)
        {
            var latex = string.Join("\n", block.Lines.Lines.Take(block.Lines.Count).Select(line => line.ToString())).Trim();
            renderer.Write("<div class=\"formula\">").Write(FormulaHtml(latex, display: true, block: true, formula)).Write("</div>");
        }
    }

    private const string Css = """
        :root{color-scheme:light dark;--bg:#fff;--fg:#1d1d1f;--muted:#6e6e73;--line:#e3e3e8;--panel:#f5f5f7;--user:#eef3ff;--accent:#3a6df0;--bad:#c62828}
        @media (prefers-color-scheme:dark){:root{--bg:#161618;--fg:#ececf0;--muted:#9a9aa3;--line:#2c2c31;--panel:#1f1f23;--user:#1d2433;--accent:#7aa2ff;--bad:#ff8a80}img.math{filter:invert(1)}}
        body{margin:0;background:var(--bg);color:var(--fg);font:15px/1.6 "Segoe UI",system-ui,-apple-system,sans-serif}
        main{max-width:860px;margin:0 auto;padding:32px 20px 64px}
        header{border-bottom:1px solid var(--line);margin-bottom:24px}
        h1{font-size:26px;margin:0 0 6px}
        .sub{color:var(--muted);margin:0 0 16px;font-size:13px}
        .msg{margin:0 0 22px;padding:14px 18px;border-radius:12px;border:1px solid var(--line)}
        .msg.user{background:var(--user);border-color:transparent}
        .meta{color:var(--muted);font-size:12px;margin-bottom:6px}
        .body>:first-child{margin-top:0}.body>:last-child{margin-bottom:0}
        pre{background:var(--panel);border:1px solid var(--line);border-radius:8px;padding:10px 12px;overflow:auto;font:13px/1.45 Consolas,"Cascadia Mono",monospace;white-space:pre-wrap;word-break:break-word}
        code{font-family:Consolas,"Cascadia Mono",monospace;font-size:.92em;background:var(--panel);padding:1px 4px;border-radius:4px}
        pre code{background:none;padding:0}
        table{border-collapse:collapse;margin:10px 0}th,td{border:1px solid var(--line);padding:5px 9px;text-align:left}th{background:var(--panel)}
        blockquote{margin:0 0 10px;padding:4px 12px;border-left:3px solid var(--accent);color:var(--muted)}
        a{color:var(--accent)}
        img{max-width:100%}figure{margin:10px 0}
        img.math{vertical-align:middle;max-height:3em}img.math.display{display:block;margin:10px auto;max-height:none}
        .formula{text-align:center;overflow:auto}
        .files ul{margin:4px 0 0;padding-left:20px}
        details.tools{margin-top:10px;border-top:1px dashed var(--line);padding-top:8px}
        details.tools summary{cursor:pointer;color:var(--muted);font-size:13px}
        .tool{margin:8px 0}.tool .name{font-size:13px;margin-bottom:4px}.tool.failed .name{color:var(--bad)}
        pre.result{max-height:360px}
        @media print{body{background:#fff;color:#000}main{max-width:none;padding:0}.msg{break-inside:avoid-page}details.tools{display:block}}
        """;
}
