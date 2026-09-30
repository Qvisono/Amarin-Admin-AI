using System.Globalization;
using System.Text.RegularExpressions;

namespace Amarin.Core;

/// <summary>
/// Служебные строки раунда инструментов: что пишется в файл чата и как это показать человеку.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ToolRound.InfoLine"/> и <see cref="ToolCallRecord.ResultPreview"/> лежат в файлах
/// переписок, и эти строки не только показываются, но и сравниваются: адаптер агента узнаёт по
/// ним заведённый раунд, лента — строку «идёт запуск», которую не надо дублировать. Поэтому на
/// диск они пишутся по-прежнему русскими маркерами — так читаются и все старые чаты без
/// миграции, — а переводится только показ (<see cref="Display"/>), как у
/// <c>ChatTitle.Default</c>.
/// </para>
/// <para>
/// До 1.28.0 лента сравнивала строку с переводом («Running tools»), а в файле лежало
/// «Запускаю инструменты» — в английском интерфейсе русская строка показывалась как есть.
/// </para>
/// </remarks>
internal static partial class EngineLines
{
    public const string RunningTools = "Запускаю инструменты";
    public const string ToolsDone = "Инструменты завершены - запрашиваю ответ модели";
    public const string ToolsStopped = "Инструменты прерваны";
    public const string StartingAgent = "Запускаю агента";
    public const string AgentDone = "Агент завершил работу - готовлю отчёт";
    public const string Cancelled = "отменено";

    /// <summary>Строка шага агента: те же слова с номером шага.</summary>
    public static string ToolsDoneStep(int step) =>
        string.Create(CultureInfo.InvariantCulture, $"{ToolsDone} (шаг {step})…");

    /// <summary>Строка раунда или итог вызова — на языке интерфейса.</summary>
    /// <remarks>Незнакомая строка показывается как есть: её уже записали на нужном языке.</remarks>
    public static string Display(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return line ?? "";
        }

        switch (line)
        {
            case RunningTools:
                return Loc.Get("S.Tools.Running");
            case ToolsDone:
                return Loc.Get("S.Engine.ToolsDone");
            case ToolsStopped:
                return Loc.Get("S.Engine.ToolsStopped");
            case StartingAgent:
                return Loc.Get("S.Engine.StartingAgent");
            case AgentDone:
                return Loc.Get("S.Engine.AgentDone");
            case Cancelled:
                return Loc.Get("S.Engine.Cancelled");
        }

        var step = StepLine().Match(line);
        return step.Success
            ? Loc.Format("S.Engine.ToolsDoneStep", int.Parse(step.Groups[1].Value, CultureInfo.InvariantCulture))
            : line;
    }

    [GeneratedRegex(@"^Инструменты завершены - запрашиваю ответ модели \(шаг (\d+)\)…$")]
    private static partial Regex StepLine();
}
