using System.Text.Json;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Звук: чтение устройств и сеансов через Core Audio на этом ПК (описание COM-интерфейсов
/// проверяется самим вызовом) и отказы до записи. Громкость и устройства тест не меняет.
/// </summary>
public sealed class AudioToolTests
{
    private static ToolResult Run(object arguments) =>
        new AudioTool().ExecuteAsync(JsonSerializer.SerializeToElement(arguments)).GetAwaiter().GetResult();

    [Fact]
    public void Devices_and_sessions_are_read_without_errors()
    {
        // Без звуковых устройств (раннер CI) список пуст, но вызов всё равно проходит.
        var devices = CoreAudio.Devices();
        Assert.All(devices, device => Assert.False(string.IsNullOrEmpty(device.Id)));
        Assert.True(devices.Count(device => device.Output && device.IsDefault) <= 1);

        Assert.True(Run(new { action = "devices" }).Success);
        Assert.True(Run(new { action = "sessions" }).Success);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void A_volume_outside_the_range_is_refused_before_anything_changes(int volume) =>
        Assert.False(Run(new { action = "set_volume", volume }).Success);

    [Fact]
    public void Mute_needs_an_explicit_true_or_false()
    {
        Assert.False(Run(new { action = "set_mute" }).Success);
        Assert.False(Run(new { action = "set_mute", mute = "maybe" }).Success);
    }

    [Fact]
    public void An_unknown_device_or_program_is_named_in_the_refusal()
    {
        var device = Run(new { action = "set_default", device = "no such device 4e7a" });
        Assert.False(device.Success);
        Assert.Contains("no such device 4e7a", device.Output, StringComparison.Ordinal);

        var app = Run(new { action = "set_volume", volume = 50, app = "no_such_program_4e7a" });
        Assert.False(app.Success);
        Assert.Contains("no_such_program_4e7a", app.Output, StringComparison.Ordinal);
    }
}
