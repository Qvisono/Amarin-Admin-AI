using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Amarin.Tools;

/// <summary>Батарея из отчёта <c>powercfg /batteryreport /xml</c>.</summary>
internal sealed record BatteryInfo(string Name, string Chemistry, long? DesignMwh, long? FullMwh, long? Cycles)
{
    /// <summary>Износ в процентах: сколько ёмкости по паспорту батарея уже не держит.</summary>
    public double? WearPercent =>
        DesignMwh is > 0 && FullMwh is { } full ? Math.Max(0, 100.0 * (DesignMwh.Value - full) / DesignMwh.Value) : null;
}

/// <summary>Точка истории полной ёмкости.</summary>
internal sealed record BatteryHistoryPoint(string Date, long FullMwh, long DesignMwh);

/// <summary>
/// Разбор XML-отчёта о батарее. Без системы — проверяется тестами на образце.
/// </summary>
/// <remarks>
/// Имена берутся без пространства имён и из атрибута или вложенного элемента: у версий отчёта
/// они лежат по-разному, а на английской и русской системе разметка одна и та же — XML, в
/// отличие от HTML-отчёта, не переводится.
/// </remarks>
internal static class BatteryReport
{
    /// <summary>Сколько точек истории показывать: первая, последняя и равномерно между ними.</summary>
    private const int HistoryPoints = 6;

    public static (List<BatteryInfo> Batteries, List<BatteryHistoryPoint> History) Parse(string xml)
    {
        var document = XDocument.Parse(xml);
        var batteries = document.Descendants()
            .Where(element => element.Name.LocalName == "Battery")
            .Select(element => new BatteryInfo(
                string.Join(' ', new[] { Text(element, "Manufacturer"), Text(element, "Id") }.Where(part => part.Length > 0)),
                Text(element, "Chemistry"),
                Number(element, "DesignCapacity"),
                Number(element, "FullChargeCapacity"),
                Number(element, "CycleCount")))
            .ToList();

        var history = document.Descendants()
            .Where(element => element.Name.LocalName == "HistoryEntry")
            .Select(element => (Date: Text(element, "LocalStartDate"), Full: Number(element, "FullChargeCapacity"), Design: Number(element, "DesignCapacity")))
            .Where(entry => entry.Full is > 0 && entry.Design is > 0)
            .Select(entry => new BatteryHistoryPoint(entry.Date.Length >= 10 ? entry.Date[..10] : entry.Date, entry.Full!.Value, entry.Design!.Value))
            .ToList();
        return (batteries, history);
    }

    public static string Format(IReadOnlyList<BatteryInfo> batteries, IReadOnlyList<BatteryHistoryPoint> history, PowerStatus status)
    {
        var sb = new StringBuilder();
        if (status.Percent is { } percent)
        {
            sb.AppendLine($"Now: {percent}%{(status.OnAc switch { true => ", on AC power", false => ", on battery", _ => "" })}");
        }

        sb.AppendLine($"Batteries: {batteries.Count}");
        for (var i = 0; i < batteries.Count; i++)
        {
            var battery = batteries[i];
            sb.AppendLine($"  {i + 1}. {(battery.Name.Length > 0 ? battery.Name : "battery")}{(battery.Chemistry.Length > 0 ? $" ({battery.Chemistry})" : "")}");
            sb.AppendLine($"     Design capacity: {Mwh(battery.DesignMwh)}");
            sb.AppendLine($"     Full charge capacity: {Mwh(battery.FullMwh)}");
            if (battery.WearPercent is { } wear)
            {
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"     Wear: {wear:0.#}% (holds {100 - wear:0.#}% of design)"));
            }

            sb.AppendLine($"     Cycle count: {(battery.Cycles is { } cycles ? cycles.ToString(CultureInfo.InvariantCulture) : "not reported")}");
        }

        if (history.Count > 0)
        {
            sb.AppendLine("Full charge capacity over time:");
            foreach (var point in Sample(history))
            {
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"  {point.Date}: {point.FullMwh} of {point.DesignMwh} mWh ({100.0 * point.FullMwh / point.DesignMwh:0.#}%)"));
            }
        }

        return sb.ToString().TrimEnd();
    }

    internal static IEnumerable<BatteryHistoryPoint> Sample(IReadOnlyList<BatteryHistoryPoint> history)
    {
        if (history.Count <= HistoryPoints)
        {
            return history;
        }

        return Enumerable.Range(0, HistoryPoints)
            .Select(i => history[(int)Math.Round(i * (history.Count - 1) / (double)(HistoryPoints - 1))]);
    }

    private static string Mwh(long? value) => value is { } mwh ? mwh.ToString(CultureInfo.InvariantCulture) + " mWh" : "not reported";

    private static string Text(XElement element, string name) =>
        (element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == name)?.Value ??
         element.Elements().FirstOrDefault(child => child.Name.LocalName == name)?.Value ?? "").Trim();

    private static long? Number(XElement element, string name) =>
        long.TryParse(Text(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
}
