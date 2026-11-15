using System.Reflection;
using System.Runtime.InteropServices;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class MicRecorderTests
{
    private readonly WpfFixture _wpf;

    public MicRecorderTests(WpfFixture wpf) => _wpf = wpf;

    /// <summary>
    /// Фоновый опрос уступал очередь анимации точек и отставал от записи: куски приходили парами,
    /// индикатор замирал (1.38.0-beta.4).
    /// </summary>
    [Fact]
    public void Buffers_are_polled_ahead_of_rendering_so_the_level_animation_cannot_starve_capture()
    {
        var priority = _wpf.Ui.Invoke(() =>
        {
            using var recorder = new MicRecorder();
            var poll = (System.Windows.Threading.DispatcherTimer)typeof(MicRecorder)
                .GetField("_poll", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(recorder)!;
            return (System.Windows.Threading.DispatcherPriority)typeof(System.Windows.Threading.DispatcherTimer)
                .GetField("_priority", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(poll)!;
        });

        Assert.True(priority > System.Windows.Threading.DispatcherPriority.Render, priority.ToString());
    }

    [Fact]
    public void Completed_buffers_are_collected_in_capture_order_across_the_ring_boundary()
    {
        var recorded = _wpf.Ui.Invoke(() =>
        {
            using var recorder = new MicRecorder();
            var type = typeof(MicRecorder);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var headerType = type.GetNestedType("WaveHeader", BindingFlags.NonPublic)!;
            var headerSize = Marshal.SizeOf(headerType);
            var bytesOffset = (int)Marshal.OffsetOf(headerType, "BytesRecorded");
            var flagsOffset = (int)Marshal.OffsetOf(headerType, "Flags");
            var buffers = (List<(IntPtr Header, IntPtr Data)>)type.GetField("_buffers", flags)!.GetValue(recorder)!;
            for (var i = 0; i < 6; i++)
            {
                var header = Marshal.AllocHGlobal(headerSize);
                var data = Marshal.AllocHGlobal(2);
                Marshal.Copy(new byte[headerSize], 0, header, headerSize);
                Marshal.WriteByte(data, (byte)i);
                Marshal.WriteByte(data, 1, 0);
                if (i is 5 or 0 or 1)
                {
                    Marshal.WriteInt32(header, bytesOffset, 2);
                    Marshal.WriteInt32(header, flagsOffset, 1);
                }

                buffers.Add((header, data));
            }

            type.GetField("_nextBuffer", flags)!.SetValue(recorder, 5);
            type.GetMethod("Collect", flags)!.Invoke(recorder, [false]);
            Assert.Equal(2, (int)type.GetField("_nextBuffer", flags)!.GetValue(recorder)!);
            return ((MemoryStream)type.GetField("_pcm", flags)!.GetValue(recorder)!).ToArray();
        });

        Assert.Equal(new byte[] { 5, 0, 0, 0, 1, 0 }, recorded);
    }
}
