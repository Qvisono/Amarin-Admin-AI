using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Amarin.Core;

namespace Amarin.Tools;

/// <summary>
/// Мышь и клавиатура для агента: посмотреть окна, их элементы и снимок, нажать, напечатать,
/// прокрутить, сделать несколько действий подряд, показать человеку.
/// </summary>
/// <remarks>
/// <para>
/// Цель действия — id элемента из последнего дерева окна или точка последнего снимка, а не
/// координаты, которые модель придумала сама. Устаревший id и окно, сдвинутое после снимка, —
/// отказ «посмотри заново»: нажатие наугад в окне, которое уже не то, хуже честного отказа.
/// </para>
/// <para>
/// Пишущее действие возвращает короткое дерево переднего окна, а не снимок: снимок стоит
/// дороже десятка деревьев, и по дереву видно главное — что открылось и что теперь в фокусе.
/// Снимок модель просит сама, когда без картинки не понять, и только если она видит картинки.
/// </para>
/// <para>
/// <c>sequence</c> — несколько действий одним вызовом: каждый вызов — это запрос к модели и
/// проверка SynGuard, а поле, текст и Enter редко требуют смотреть между ними. Защитник видит
/// цепочку целиком и судит о ней сразу, а дерево читается один раз, в конце.
/// </para>
/// <para>
/// Что модели трогать нельзя, решает <see cref="ComputerSafety"/> — до шлюза дело доходит уже
/// с разрешённым вызовом, а запрет здесь проверяется в момент нажатия: окно под точкой могло
/// смениться между проверкой и исполнением.
/// </para>
/// </remarks>
/// <param name="canSee">
/// Видит ли модель картинки. Нет — снимок отказывает: картинку такая модель не примет, и запрос
/// упал бы целиком.
/// </param>
internal sealed class ComputerTool(ComputerSession session, bool canSee = true) : ITool
{
    public const string ToolName = "computer";

    /// <summary>Длинная сторона снимка: крупнее модели не разглядят, а платить придётся.</summary>
    public const int MaxShotSide = 1280;

    /// <summary>Начало выдачи дерева окна. По нему окно истории узнаёт устаревшие деревья.</summary>
    public const string TreeMark = "[tree ";

    public const int InspectDefault = 150;
    public const int InspectMax = 400;
    public const int AfterActionTree = 60;
    public const int MaxText = 4000;

    /// <summary>Сколько действий в одной цепочке: дальше без взгляда на экран идти незачем.</summary>
    public const int MaxSequence = 12;

    private const int FindScan = 1500;
    private const int FindLimit = 30;

    internal static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(1.5);

    /// <summary>Затишье между шагами цепочки: короче, чем после всей цепочки.</summary>
    internal static readonly TimeSpan StepSettle = TimeSpan.FromSeconds(0.6);

    internal static readonly TimeSpan AfterActionBudget = TimeSpan.FromSeconds(1.5);
    internal static readonly TimeSpan InspectBudget = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan ShowWait = TimeSpan.FromMinutes(2);

    /// <summary>Действия, которые ничего не меняют. Остальные — запись; это же знает шлюз.</summary>
    internal static readonly FrozenSet<string> ReadActions =
        new[] { "windows", "inspect", "find", "screenshot", "wait", "show" }.ToFrozenSet(StringComparer.Ordinal);

    internal static readonly FrozenSet<string> WriteActions =
        new[] { "click", "double_click", "right_click", "type", "key", "scroll", "drag", "focus", "sequence" }
            .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Что можно в шаге цепочки: действия и пауза, но не взгляд и не вложенная цепочка.</summary>
    internal static readonly FrozenSet<string> StepActions =
        new[] { "click", "double_click", "right_click", "type", "key", "scroll", "drag", "focus", "wait" }
            .ToFrozenSet(StringComparer.Ordinal);

    private static readonly JsonElement Schema = JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["windows", "inspect", "find", "screenshot", "wait", "show",
                       "click", "double_click", "right_click", "type", "key", "scroll", "drag", "focus", "sequence"]
            },
            "window": { "type": "string", "description": "Window id from the windows list (w followed by hex digits)." },
            "element": { "type": "string", "description": "Element id from the latest tree of its window (e followed by digits)." },
            "x": { "type": "number", "description": "Horizontal pixel in the latest screenshot." },
            "y": { "type": "number", "description": "Vertical pixel in the latest screenshot." },
            "to_element": { "type": "string", "description": "drag: element id where the drag ends." },
            "to_x": { "type": "number", "description": "drag: horizontal pixel in the latest screenshot where the drag ends." },
            "to_y": { "type": "number", "description": "drag: vertical pixel in the latest screenshot where the drag ends." },
            "text": { "type": "string", "description": "type: text to enter where the keyboard focus is, or into element. It is added to what the field already holds." },
            "keys": {
              "type": "string",
              "description": "key: modifiers ctrl, alt, shift, win and at most one key, joined with +. A key is a letter, a digit, F1-F24 or one of enter, escape, tab, space, backspace, delete, insert, home, end, pageup, pagedown, up, down, left, right, apps."
            },
            "direction": { "type": "string", "enum": ["up", "down", "left", "right"] },
            "amount": { "type": "integer", "description": "scroll: wheel notches, 1-10." },
            "seconds": { "type": "number", "description": "wait: 0.5-10 seconds." },
            "name": { "type": "string", "description": "find: part of the element name." },
            "role": { "type": "string", "description": "find: element type as shown in trees." },
            "limit": { "type": "integer", "description": "inspect: most elements to list, 20-400." },
            "monitor": { "type": "integer", "description": "screenshot: monitor number from the windows list, when no window is given." },
            "caption": { "type": "string", "description": "show: what the person should do there, in the interface language." },
            "steps": {
              "type": "array",
              "items": { "type": "object" },
              "description": "sequence: up to 12 actions done in order, each an object with its own action (click, double_click, right_click, type, key, scroll, drag, focus or wait) and the fields that action takes."
            },
            "explanation": { "type": "string", "description": "Required for every action that is not a read: one short sentence in the interface language about what it does." }
          },
          "required": ["action"]
        }
        """);

    public string Name => ToolName;

    public string Description =>
        "Operate the windows of this PC with the mouse and keyboard. " +
        "Reads: windows lists top-level windows and monitors; inspect returns a window's element tree with ids; " +
        "find searches a window's elements by name and type; screenshot returns an image of a window or a monitor; " +
        "wait pauses; show frames a target with a caption and waits until the person presses Done. " +
        "sequence does several actions in order in one call and stops at the first refusal or when another window comes to the front. " +
        "A target is an element id from the latest tree of its window, or x,y in the latest screenshot. " +
        "Every new tree of a window replaces the ids of that window, and every new screenshot replaces the previous one. " +
        "After each action the tool returns the tree of the foreground window. " +
        "Every action that is not a read requires explanation.";

    public JsonElement ParametersSchema => Schema;

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var action = Action(arguments);
        var read = ReadActions.Contains(action);
        if (!read && !WriteActions.Contains(action))
        {
            return ToolResult.Fail(Loc.Format("S.Computer.UnknownAction", action));
        }

        if (ComputerSafety.Desk(session.Computer.Desk()) is { } desk)
        {
            return ToolResult.Fail(desk);
        }

        var explanation = Text(arguments, "explanation")?.Trim() ?? "";
        if (!read && explanation.Length == 0)
        {
            return ToolResult.Fail(Loc.Get("S.Computer.NoExplanation"));
        }

        if (action == "screenshot" && !canSee)
        {
            return ToolResult.Fail(Loc.Get("S.Computer.NoVision"));
        }

        if (action == "show" && !session.CanShow)
        {
            return ToolResult.Fail(Loc.Get("S.Computer.NobodyToShow"));
        }

        if ((!read || action == "show") && await session.EnsureStartedAsync(cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return ToolResult.Fail(refused);
        }

        try
        {
            switch (action)
            {
                case "windows":
                    return ListWindows();
                case "inspect":
                    return await InspectAsync(arguments, cancellationToken).ConfigureAwait(false);
                case "find":
                    return await FindAsync(arguments, cancellationToken).ConfigureAwait(false);
                case "screenshot":
                    return await ScreenshotAsync(arguments, cancellationToken).ConfigureAwait(false);
                case "wait":
                    return ToolResult.Ok(await PauseAsync(arguments, cancellationToken).ConfigureAwait(false));
                case "show":
                    return await ShowAsync(arguments, cancellationToken).ConfigureAwait(false);
                case "sequence":
                    return await SequenceAsync(arguments, explanation, cancellationToken).ConfigureAwait(false);
            }

            session.Step(explanation);
            var done = await ActAsync(action, arguments, cancellationToken).ConfigureAwait(false);
            return done.Refusal is { } refusal
                ? ToolResult.Fail(refusal)
                : ToolResult.Ok(await AfterActionAsync(done.Heading, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return ToolResult.Fail(Loc.Format("S.Computer.Failed", ex.Message));
        }
    }

    // ───────────────────────── чтение ─────────────────────────

    private ToolResult ListWindows()
    {
        var text = new StringBuilder("monitors:");
        foreach (var monitor in session.Computer.Monitors())
        {
            text.Append(CultureInfo.InvariantCulture, $" {monitor.Index} [{monitor.Bounds}]").Append(monitor.Primary ? " primary" : "").Append(';');
        }

        text.AppendLine().AppendLine("windows, topmost first:");
        var any = false;
        foreach (var window in session.Computer.Windows())
        {
            // Окна самой программы модель не видит вовсе: трогать их всё равно нельзя.
            if (window.ProcessId == session.OwnProcessId)
            {
                continue;
            }

            any = true;
            text.Append(window.Id).Append(" \"").Append(Clip(window.Title, 120)).Append("\" ")
                .Append(window.ProcessName)
                .Append(CultureInfo.InvariantCulture, $" monitor {window.Monitor}");
            if (window.Foreground)
            {
                text.Append(" foreground");
            }

            if (window.Minimized)
            {
                text.Append(" minimized");
            }

            if (window.Elevated)
            {
                text.Append(" administrator (no input)");
            }
            else if (window.Kind is ComputerWindowKind.Console or ComputerWindowKind.RunDialog)
            {
                text.Append(" runs commands (no input)");
            }

            text.AppendLine();
        }

        if (!any)
        {
            text.AppendLine("none");
        }

        return ToolResult.Ok(text.ToString().TrimEnd());
    }

    private async Task<ToolResult> InspectAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!TryWindow(arguments, foregroundIfMissing: true, out var window, out var refusal))
        {
            return ToolResult.Fail(refusal);
        }

        var limit = Math.Clamp(Integer(arguments, "limit") ?? InspectDefault, 20, InspectMax);
        session.Step(Loc.Format("S.Computer.Step.Look", Title(window)));
        return ToolResult.Ok(await TreeAsync(window, limit, InspectBudget, cancellationToken).ConfigureAwait(false));
    }

    private async Task<ToolResult> FindAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!TryWindow(arguments, foregroundIfMissing: true, out var window, out var refusal))
        {
            return ToolResult.Fail(refusal);
        }

        var name = Text(arguments, "name")?.Trim() ?? "";
        var role = Text(arguments, "role")?.Trim() ?? "";
        session.Step(Loc.Format("S.Computer.Step.Look", Title(window)));

        // Поиск выдаёт id всему прочитанному: найденное должно быть тем, на что можно сразу нажать.
        var tree = await session.Computer.InspectAsync(window.Handle, FindScan, InspectBudget, cancellationToken).ConfigureAwait(false);
        var named = session.Remember(window.Handle, tree.Elements);
        var matches = named
            .Where(item => (name.Length == 0 || item.Element.Name.Contains(name, StringComparison.OrdinalIgnoreCase)) &&
                           (role.Length == 0 || item.Element.Role.Contains(role, StringComparison.OrdinalIgnoreCase)))
            .Take(FindLimit + 1)
            .ToList();

        var text = new StringBuilder(Header(window));
        text.Append(CultureInfo.InvariantCulture, $", matches for name \"{name}\" type \"{role}\": ");
        if (matches.Count == 0)
        {
            return ToolResult.Ok(text.Append(tree.Cut
                ? "none in the part of the window read in time. Take a screenshot or scroll and search again."
                : "none. Inspect the window or take a screenshot.").ToString());
        }

        text.AppendLine(matches.Count > FindLimit ? $"first {FindLimit}" : matches.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var (id, element) in matches.Take(FindLimit))
        {
            AppendElement(text, id, element, indent: false);
        }

        return ToolResult.Ok(text.ToString().TrimEnd());
    }

    private async Task<ToolResult> ScreenshotAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        ComputerWindow? window = null;
        ScreenRect area;
        string what;
        if (Text(arguments, "window") is { Length: > 0 })
        {
            if (!TryWindow(arguments, foregroundIfMissing: false, out var found, out var refusal))
            {
                return ToolResult.Fail(refusal);
            }

            window = found;
            var monitor = session.Computer.Monitors().FirstOrDefault(item => item.Index == found.Monitor);
            area = monitor is null ? found.Bounds : found.Bounds.Intersect(monitor.Bounds);
            what = "window " + found.Id;
        }
        else
        {
            var monitors = session.Computer.Monitors();
            var wanted = Integer(arguments, "monitor") ?? session.Computer.Foreground()?.Monitor;
            var monitor = monitors.FirstOrDefault(item => item.Index == wanted) ??
                          monitors.FirstOrDefault(item => item.Primary) ??
                          monitors.FirstOrDefault();
            if (monitor is null)
            {
                return ToolResult.Fail(Loc.Get("S.Computer.NoWindow"));
            }

            area = monitor.Bounds;
            what = "monitor " + monitor.Index.ToString(CultureInfo.InvariantCulture);
        }

        if (area.IsEmpty)
        {
            return ToolResult.Fail(Loc.Get("S.Computer.NoWindow"));
        }

        session.Step(Loc.Get("S.Computer.Step.Screen"));
        var shot = await session.Computer.CaptureAsync(area, MaxShotSide, cancellationToken).ConfigureAwait(false);
        session.RememberShot(shot, window);
        return ToolResult.WithImage(
            $"Screenshot of {what}, {shot.Width}x{shot.Height} px. Its x,y are targets until the next screenshot.",
            shot.Base64,
            shot.Mime);
    }

    private static async Task<string> PauseAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var seconds = Math.Clamp(Number(arguments, "seconds") ?? 1, 0.5, 10);
        await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
        return string.Create(CultureInfo.InvariantCulture, $"Waited {seconds:0.#} s.");
    }

    private async Task<ToolResult> ShowAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var caption = Text(arguments, "caption")?.Trim() ?? "";
        if (caption.Length == 0)
        {
            return ToolResult.Fail(Loc.Get("S.Computer.NoCaption"));
        }

        var target = await TargetAsync(arguments, "element", "x", "y", cancellationToken).ConfigureAwait(false);
        if (target.Refusal is { } refusal)
        {
            return ToolResult.Fail(refusal);
        }

        ScreenRect area;
        if (target.Element is { Bounds.IsEmpty: false } element)
        {
            area = element.Bounds;
        }
        else if (target.Point is { } at)
        {
            area = new ScreenRect(at.X - 20, at.Y - 20, 40, 40);
        }
        else
        {
            return ToolResult.Fail(Loc.Get("S.Computer.NoPoint"));
        }

        session.Step(caption);
        var done = await session.ShowAsync(area, caption, ShowWait, cancellationToken).ConfigureAwait(false);
        var heading = done == true
            ? "The person pressed Done."
            : "The person did not press Done within 2 minutes.";
        return ToolResult.Ok(await AfterActionAsync(heading, cancellationToken).ConfigureAwait(false));
    }

    // ───────────────────────── запись ─────────────────────────

    /// <summary>Итог действия: отказ для модели или строка о том, что сделано.</summary>
    private readonly record struct Act(string? Refusal, string Heading)
    {
        public static Act Refuse(string refusal) => new(refusal, "");

        public static Act Done(string heading) => new(null, heading);
    }

    private Task<Act> ActAsync(string action, JsonElement arguments, CancellationToken cancellationToken) =>
        action switch
        {
            "click" => PointAsync(arguments, ComputerPointerKind.Click, cancellationToken),
            "double_click" => PointAsync(arguments, ComputerPointerKind.DoubleClick, cancellationToken),
            "right_click" => PointAsync(arguments, ComputerPointerKind.RightClick, cancellationToken),
            "type" => TypeAsync(arguments, cancellationToken),
            "key" => KeyAsync(arguments, cancellationToken),
            "scroll" => ScrollAsync(arguments, cancellationToken),
            "drag" => DragAsync(arguments, cancellationToken),
            _ => FocusAsync(arguments, cancellationToken)
        };

    /// <summary>
    /// Несколько действий подряд. Останавливается на первом отказе и когда вперёд вышло другое
    /// окно не шагом <c>focus</c>: дальше цепочка писала бы не туда, куда модель рассчитывала.
    /// </summary>
    private async Task<ToolResult> SequenceAsync(JsonElement arguments, string explanation, CancellationToken cancellationToken)
    {
        if (!arguments.TryGetProperty("steps", out var stepsValue) || stepsValue.ValueKind != JsonValueKind.Array ||
            stepsValue.GetArrayLength() is 0 or > MaxSequence)
        {
            return ToolResult.Fail(Loc.Format("S.Computer.SequenceSteps", MaxSequence));
        }

        var steps = stepsValue.EnumerateArray().ToList();
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].ValueKind != JsonValueKind.Object || !StepActions.Contains(Action(steps[i])))
            {
                return ToolResult.Fail(Loc.Format("S.Computer.SequenceBadStep", i + 1, Action(steps[i])));
            }
        }

        session.Step(explanation);
        var front = session.Computer.Foreground()?.Handle;
        for (var i = 0; i < steps.Count; i++)
        {
            var action = Action(steps[i]);
            if (i > 0)
            {
                await session.Computer.SettleAsync(StepSettle, cancellationToken).ConfigureAwait(false);
                var now = session.Computer.Foreground()?.Handle;
                if (now != front && Action(steps[i - 1]) != "focus")
                {
                    var changed = Loc.Format("S.Computer.SequenceWindowChanged", i, steps.Count);
                    return ToolResult.Ok(await AfterActionAsync(changed, cancellationToken).ConfigureAwait(false));
                }

                front = now;
            }

            var done = action == "wait"
                ? Act.Done(await PauseAsync(steps[i], cancellationToken).ConfigureAwait(false))
                : ComputerSafety.Desk(session.Computer.Desk()) is { } desk
                    ? Act.Refuse(desk)
                    : await ActAsync(action, steps[i], cancellationToken).ConfigureAwait(false);
            if (done.Refusal is { } refusal)
            {
                var stopped = Loc.Format("S.Computer.SequenceStopped", i + 1, steps.Count, action, refusal);
                return ToolResult.Fail(await AfterActionAsync(stopped, cancellationToken).ConfigureAwait(false));
            }
        }

        return ToolResult.Ok(await AfterActionAsync(
            string.Create(CultureInfo.InvariantCulture, $"Done all {steps.Count} steps."), cancellationToken).ConfigureAwait(false));
    }

    private async Task<Act> PointAsync(JsonElement arguments, ComputerPointerKind kind, CancellationToken cancellationToken)
    {
        var target = await TargetAsync(arguments, "element", "x", "y", cancellationToken).ConfigureAwait(false);
        if (target.Refusal is { } refusal)
        {
            return Act.Refuse(refusal);
        }

        if (target.Point is not { } point)
        {
            // Элемент есть, но точки на экране у него нет: закрыт или за краем окна. Обычное нажатие
            // — через UI Automation; двойное и правое так не сделать.
            if (kind != ComputerPointerKind.Click || target.Element is not { } unseen)
            {
                return Act.Refuse(Loc.Get("S.Computer.NoPoint"));
            }

            if (ComputerSafety.Target(session.Computer.Window(target.Window), session.OwnProcessId) is { } hidden)
            {
                return Act.Refuse(hidden);
            }

            return await session.Computer.InvokeAsync(target.Window, unseen.Key, cancellationToken).ConfigureAwait(false)
                ? Act.Done("Pressed.")
                : Act.Refuse(Loc.Get("S.Computer.NoPoint"));
        }

        if (CheckPoint(point) is { } blocked)
        {
            return Act.Refuse(blocked);
        }

        await session.Computer.PointerAsync(new ComputerPointer(kind, point), cancellationToken).ConfigureAwait(false);
        return Act.Done("Done.");
    }

    private async Task<Act> TypeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var text = Text(arguments, "text") ?? "";
        if (text.Length == 0)
        {
            return Act.Refuse(Loc.Get("S.Computer.NoText"));
        }

        if (text.Length > MaxText)
        {
            return Act.Refuse(Loc.Format("S.Computer.TextTooLong", MaxText));
        }

        if (Text(arguments, "element") is { Length: > 0 })
        {
            var target = await TargetAsync(arguments, "element", "x", "y", cancellationToken).ConfigureAwait(false);
            if (target.Refusal is { } refusal)
            {
                return Act.Refuse(refusal);
            }

            if (target.Element is not { } chosen)
            {
                return Act.Refuse(Loc.Get("S.Computer.NoTarget"));
            }

            if (ComputerSafety.Typing(session.Computer.Window(target.Window), chosen, text, session.OwnProcessId) is { } blocked)
            {
                return Act.Refuse(blocked);
            }

            if (!await session.Computer.FocusElementAsync(target.Window, chosen.Key, cancellationToken).ConfigureAwait(false))
            {
                return Act.Refuse(Loc.Get("S.Computer.NoFocus"));
            }
        }
        else if (ComputerSafety.Typing(session.Computer.Foreground(), session.Computer.Focused(), text, session.OwnProcessId) is { } blocked)
        {
            return Act.Refuse(blocked);
        }

        await session.Computer.TypeAsync(text, cancellationToken).ConfigureAwait(false);
        return Act.Done("Typed.");
    }

    private async Task<Act> KeyAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var keys = Text(arguments, "keys") ?? "";
        if (ComputerKeys.Parse(keys) is not { } chord)
        {
            return Act.Refuse(Loc.Format("S.Computer.BadKeys", keys));
        }

        if ((ComputerSafety.Keys(session.Computer.Foreground(), chord, session.OwnProcessId) ??
             ComputerSafety.Paste(chord, session.Computer.ClipboardText)) is { } blocked)
        {
            return Act.Refuse(blocked);
        }

        await session.Computer.KeysAsync(chord, cancellationToken).ConfigureAwait(false);
        return Act.Done("Pressed " + ComputerKeys.Join(chord) + ".");
    }

    private async Task<Act> ScrollAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var direction = (Text(arguments, "direction") ?? "").Trim().ToLowerInvariant();
        if (direction is not ("up" or "down" or "left" or "right"))
        {
            return Act.Refuse(Loc.Get("S.Computer.NoDirection"));
        }

        ScreenPoint point;
        if (Text(arguments, "element") is { Length: > 0 } || arguments.TryGetProperty("x", out _))
        {
            var target = await TargetAsync(arguments, "element", "x", "y", cancellationToken).ConfigureAwait(false);
            if (target.Refusal is { } refusal)
            {
                return Act.Refuse(refusal);
            }

            if (target.Point is not { } at)
            {
                return Act.Refuse(Loc.Get("S.Computer.NoPoint"));
            }

            point = at;
        }
        else if (session.Computer.Foreground() is { Minimized: false } foreground)
        {
            point = foreground.Bounds.Center;
        }
        else
        {
            return Act.Refuse(Loc.Get("S.Computer.NoTarget"));
        }

        if (CheckPoint(point) is { } blocked)
        {
            return Act.Refuse(blocked);
        }

        var amount = Math.Clamp(Integer(arguments, "amount") ?? 3, 1, 10);
        var scroll = direction switch
        {
            "up" => new ComputerPointer(ComputerPointerKind.Scroll, point, ScrollY: -amount),
            "down" => new ComputerPointer(ComputerPointerKind.Scroll, point, ScrollY: amount),
            "left" => new ComputerPointer(ComputerPointerKind.Scroll, point, ScrollX: -amount),
            _ => new ComputerPointer(ComputerPointerKind.Scroll, point, ScrollX: amount)
        };

        await session.Computer.PointerAsync(scroll, cancellationToken).ConfigureAwait(false);
        return Act.Done("Scrolled.");
    }

    private async Task<Act> DragAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var from = await TargetAsync(arguments, "element", "x", "y", cancellationToken).ConfigureAwait(false);
        if (from.Refusal is { } fromRefusal)
        {
            return Act.Refuse(fromRefusal);
        }

        var to = await TargetAsync(arguments, "to_element", "to_x", "to_y", cancellationToken).ConfigureAwait(false);
        if (to.Refusal is { } toRefusal)
        {
            return Act.Refuse(toRefusal);
        }

        if (from.Point is not { } start || to.Point is not { } end)
        {
            return Act.Refuse(Loc.Get("S.Computer.NoPoint"));
        }

        if ((CheckPoint(start) ?? CheckPoint(end)) is { } blocked)
        {
            return Act.Refuse(blocked);
        }

        await session.Computer.PointerAsync(new ComputerPointer(ComputerPointerKind.Drag, start, end), cancellationToken)
            .ConfigureAwait(false);
        return Act.Done("Dragged.");
    }

    private async Task<Act> FocusAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (Text(arguments, "element") is { Length: > 0 })
        {
            var target = await TargetAsync(arguments, "element", "x", "y", cancellationToken).ConfigureAwait(false);
            if (target.Refusal is { } refusal)
            {
                return Act.Refuse(refusal);
            }

            if (target.Element is not { } chosen)
            {
                return Act.Refuse(Loc.Get("S.Computer.NoTarget"));
            }

            if (ComputerSafety.Target(session.Computer.Window(target.Window), session.OwnProcessId) is { } blocked)
            {
                return Act.Refuse(blocked);
            }

            return await session.Computer.FocusElementAsync(target.Window, chosen.Key, cancellationToken).ConfigureAwait(false)
                ? Act.Done("Focused.")
                : Act.Refuse(Loc.Get("S.Computer.NoFocus"));
        }

        if (!TryWindow(arguments, foregroundIfMissing: false, out var window, out var missing, allowMinimized: true))
        {
            return Act.Refuse(missing);
        }

        if (ComputerSafety.Target(window, session.OwnProcessId) is { } refused)
        {
            return Act.Refuse(refused);
        }

        return await session.Computer.FocusWindowAsync(window.Handle, cancellationToken).ConfigureAwait(false)
            ? Act.Done("Focused.")
            : Act.Refuse(Loc.Get("S.Computer.NoFocus"));
    }

    // ───────────────────────── общее ─────────────────────────

    /// <summary>Цель действия: точка на экране и, если цель — элемент, сам элемент и его окно.</summary>
    private readonly record struct Target(string? Refusal, ScreenPoint? Point, long Window, ComputerElement? Element);

    private async Task<Target> TargetAsync(
        JsonElement arguments,
        string elementField,
        string xField,
        string yField,
        CancellationToken cancellationToken)
    {
        if (Text(arguments, elementField) is { Length: > 0 } id)
        {
            if (!session.TryElement(id, out var window, out var remembered))
            {
                return new Target(Loc.Format("S.Computer.Stale", id), null, 0, null);
            }

            // Свежее место элемента: окно могли сдвинуть или прокрутить после дерева.
            var fresh = await session.Computer.ElementAsync(window, remembered.Key, cancellationToken).ConfigureAwait(false);
            return fresh is null
                ? new Target(Loc.Format("S.Computer.Stale", id), null, 0, null)
                : new Target(null, fresh.Target, window, fresh);
        }

        if (Number(arguments, xField) is { } x && Number(arguments, yField) is { } y)
        {
            return session.FromShot(x, y, out var point) is { } refusal
                ? new Target(refusal, null, 0, null)
                : new Target(null, point, 0, null);
        }

        return new Target(Loc.Get("S.Computer.NoTarget"), null, 0, null);
    }

    /// <summary>
    /// Можно ли нажать в эту точку — по окну, которое под ней сейчас. Плашку сперва убирают из-под
    /// точки: она поверх всех окон и забрала бы нажатие себе.
    /// </summary>
    private string? CheckPoint(ScreenPoint point)
    {
        session.Avoid(point);
        return ComputerSafety.Target(session.Computer.WindowAt(point), session.OwnProcessId);
    }

    /// <summary>Окно из аргумента <c>window</c>; без него — переднее, если так можно.</summary>
    private bool TryWindow(
        JsonElement arguments,
        bool foregroundIfMissing,
        [NotNullWhen(true)] out ComputerWindow? window,
        out string refusal,
        bool allowMinimized = false)
    {
        window = null;
        refusal = "";
        ComputerWindow? found;
        if (Text(arguments, "window") is { Length: > 0 } id)
        {
            found = ComputerWindow.ParseId(id) is { } handle ? session.Computer.Window(handle) : null;
            if (found is null)
            {
                refusal = Loc.Get("S.Computer.WindowGone");
                return false;
            }
        }
        else if (foregroundIfMissing)
        {
            found = session.Computer.Foreground();
        }
        else
        {
            refusal = Loc.Get("S.Computer.NoWindowId");
            return false;
        }

        if (found is null)
        {
            refusal = Loc.Get("S.Computer.NoWindow");
            return false;
        }

        if (found.ProcessId == session.OwnProcessId)
        {
            refusal = Loc.Get("S.Computer.Own");
            return false;
        }

        if (found.Minimized && !allowMinimized)
        {
            refusal = Loc.Format("S.Computer.Minimized", Title(found));
            return false;
        }

        window = found;
        return true;
    }

    /// <summary>После действия: дождаться затишья и показать переднее окно свежим деревом.</summary>
    private async Task<string> AfterActionAsync(string heading, CancellationToken cancellationToken)
    {
        await session.Computer.SettleAsync(SettleTime, cancellationToken).ConfigureAwait(false);
        var foreground = session.Computer.Foreground();
        if (foreground is null)
        {
            return heading + " No window is in the foreground.";
        }

        if (foreground.ProcessId == session.OwnProcessId)
        {
            return heading + " The foreground window belongs to Amarin and cannot be operated.";
        }

        return heading + "\n" + await TreeAsync(foreground, AfterActionTree, AfterActionBudget, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> TreeAsync(ComputerWindow window, int limit, TimeSpan budget, CancellationToken cancellationToken)
    {
        var tree = await session.Computer.InspectAsync(window.Handle, limit, budget, cancellationToken).ConfigureAwait(false);
        var named = session.Remember(window.Handle, tree.Elements);
        var text = new StringBuilder(Header(window));
        text.Append(CultureInfo.InvariantCulture, $", {named.Count} elements");
        if (tree.Cut)
        {
            text.Append(", list cut: use find, a larger limit or a screenshot");
        }

        text.AppendLine();
        foreach (var (id, element) in named)
        {
            AppendElement(text, id, element, indent: true);
        }

        return text.ToString().TrimEnd();
    }

    private static string Header(ComputerWindow window) =>
        $"{TreeMark}{window.Id}] \"{Clip(window.Title, 120)}\" {window.ProcessName}";

    private static void AppendElement(StringBuilder text, string id, ComputerElement element, bool indent)
    {
        if (indent)
        {
            text.Append(' ', 2 * Math.Clamp(element.Depth, 0, 12));
        }

        text.Append(id).Append(' ').Append(element.Role);
        if (element.Name.Length > 0)
        {
            text.Append(" \"").Append(Clip(element.Name, 100)).Append('"');
        }

        if (element.IsPassword)
        {
            text.Append(" password");
        }
        else if (!string.IsNullOrEmpty(element.Value) && !string.Equals(element.Value, element.Name, StringComparison.Ordinal))
        {
            text.Append(" = \"").Append(Clip(element.Value, 80)).Append('"');
        }

        if (!element.Enabled)
        {
            text.Append(" disabled");
        }

        if (element.Focused)
        {
            text.Append(" focused");
        }

        if (element.Offscreen)
        {
            text.Append(" offscreen");
        }

        text.AppendLine();
    }

    private static string Title(ComputerWindow window) =>
        string.IsNullOrWhiteSpace(window.Title) ? window.ProcessName : Clip(window.Title, 80);

    private static string Clip(string text, int max)
    {
        var line = text.ReplaceLineEndings(" ");
        return line.Length <= max ? line : line[..max] + "…";
    }

    private static string Action(JsonElement arguments) => (Text(arguments, "action") ?? "").Trim().ToLowerInvariant();

    private static string? Text(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object &&
        arguments.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Число из аргумента — модели пишут его и числом, и строкой.</summary>
    private static double? Number(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String &&
               double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static int? Integer(JsonElement arguments, string name) =>
        Number(arguments, name) is { } number && double.IsFinite(number)
            ? (int)Math.Round(Math.Clamp(number, int.MinValue, int.MaxValue))
            : null;
}
