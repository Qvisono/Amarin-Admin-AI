using System.Security.Principal;
using System.Text.Json;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Электропитание: разбор отчёта о батарее на образце и чтение схем этого ПК. Схему тест не
/// переключает.
/// </summary>
public sealed class PowerToolTests
{
    private const string Report = """
        <?xml version="1.0" encoding="utf-8"?>
        <BatteryReport xmlns="http://schemas.microsoft.com/battery/2012">
          <Batteries>
            <Battery>
              <Id>Pack A</Id>
              <Manufacturer>Maker</Manufacturer>
              <Chemistry>LION</Chemistry>
              <DesignCapacity>50000</DesignCapacity>
              <FullChargeCapacity>40000</FullChargeCapacity>
              <CycleCount>321</CycleCount>
            </Battery>
          </Batteries>
          <History>
            <HistoryEntry LocalStartDate="2025-01-01T00:00:00" FullChargeCapacity="49000" DesignCapacity="50000" />
            <HistoryEntry LocalStartDate="2025-03-01T00:00:00" FullChargeCapacity="47000" DesignCapacity="50000" />
            <HistoryEntry LocalStartDate="2025-05-01T00:00:00" FullChargeCapacity="46000" DesignCapacity="50000" />
            <HistoryEntry LocalStartDate="2025-07-01T00:00:00" FullChargeCapacity="45000" DesignCapacity="50000" />
            <HistoryEntry LocalStartDate="2025-09-01T00:00:00" FullChargeCapacity="44000" DesignCapacity="50000" />
            <HistoryEntry LocalStartDate="2025-11-01T00:00:00" FullChargeCapacity="43000" DesignCapacity="50000" />
            <HistoryEntry LocalStartDate="2026-01-01T00:00:00" FullChargeCapacity="42000" DesignCapacity="50000" />
            <HistoryEntry LocalStartDate="2026-03-01T00:00:00" FullChargeCapacity="40000" DesignCapacity="0" />
          </History>
        </BatteryReport>
        """;

    [Fact]
    public void The_battery_report_gives_wear_cycles_and_a_short_history()
    {
        var (batteries, history) = BatteryReport.Parse(Report);

        var battery = Assert.Single(batteries);
        Assert.Equal("Maker Pack A", battery.Name);
        Assert.Equal(20, battery.WearPercent!.Value, precision: 1);
        Assert.Equal(321, battery.Cycles);

        // Точка без паспортной ёмкости в историю не идёт.
        Assert.Equal(7, history.Count);
        var sample = BatteryReport.Sample(history).ToList();
        Assert.Equal(6, sample.Count);
        Assert.Equal("2025-01-01", sample[0].Date);
        Assert.Equal("2026-01-01", sample[^1].Date);

        var text = BatteryReport.Format(batteries, history, new PowerStatus(true, true, 80));
        Assert.Contains("Wear: 20%", text, StringComparison.Ordinal);
        Assert.Contains("80%, on AC power", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_report_without_a_battery_section_has_no_batteries()
    {
        var (batteries, history) = BatteryReport.Parse("<BatteryReport><Batteries /></BatteryReport>");

        Assert.Empty(batteries);
        Assert.Empty(history);
    }

    [Fact]
    public void This_pc_has_power_plans_and_exactly_one_is_active()
    {
        var schemes = PowerSchemes.List();

        Assert.NotEmpty(schemes);
        Assert.Single(schemes, scheme => scheme.Active);
        Assert.All(schemes, scheme => Assert.NotEqual(Guid.Empty, scheme.Id));
    }

    [Fact]
    public async Task An_unknown_plan_is_refused_without_switching_anything()
    {
        var before = PowerSchemes.Active();
        var result = await new PowerTool().ExecuteAsync(JsonSerializer.SerializeToElement(new { action = "set_scheme", scheme = "no such plan 7c1d" }));

        Assert.False(result.Success);
        Assert.Equal(before, PowerSchemes.Active());
    }

    [Fact]
    public async Task Sleep_blockers_need_administrator_rights()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            // Повышенный прогон увидел бы настоящий powercfg — это уже не проверка отказа.
            return;
        }

        var result = await new PowerTool().ExecuteAsync(JsonSerializer.SerializeToElement(new { action = "requests" }));

        Assert.False(result.Success);
        Assert.Contains("администратора", result.Output, StringComparison.Ordinal);
    }
}
