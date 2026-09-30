using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Threading;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Запись с микрофона по умолчанию (D14) — waveIn из winmm, 16 кГц, 16 бит, моно. Без пакетов:
/// NAudio ради одной записи раздул бы программу.
/// </summary>
/// <remarks>
/// Буферы опрашиваются таймером диспетчера по флагу WHDR_DONE, а не обратным вызовом драйвера:
/// тот приходит на чужом потоке, и из него нельзя звать сам waveIn — легко получить взаимную
/// блокировку. Буферы и заголовки лежат в неуправляемой памяти: драйвер пишет в них, пока
/// сборщик мусора двигал бы управляемые массивы.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class MicRecorder : IDisposable
{
    private const int BufferCount = 6;
    private const int BufferBytes = WavFile.BytesPerSecond / 10;
    private const uint WhdrDone = 1;
    private const uint CallbackNull = 0;
    private static readonly UIntPtr WaveMapper = new(0xFFFFFFFF);

    private readonly List<(IntPtr Header, IntPtr Data)> _buffers = [];
    private readonly MemoryStream _pcm = new();
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private IntPtr _device;
    private bool _stopped;

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public IntPtr User;
        public uint Flags;
        public uint Loops;
        public IntPtr Next;
        public IntPtr Reserved;
    }

    [DllImport("winmm.dll")]
    private static extern uint waveInOpen(out IntPtr device, UIntPtr deviceId, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);

    [DllImport("winmm.dll")]
    private static extern uint waveInPrepareHeader(IntPtr device, IntPtr header, uint size);

    [DllImport("winmm.dll")]
    private static extern uint waveInUnprepareHeader(IntPtr device, IntPtr header, uint size);

    [DllImport("winmm.dll")]
    private static extern uint waveInAddBuffer(IntPtr device, IntPtr header, uint size);

    [DllImport("winmm.dll")]
    private static extern uint waveInStart(IntPtr device);

    [DllImport("winmm.dll")]
    private static extern uint waveInStop(IntPtr device);

    [DllImport("winmm.dll")]
    private static extern uint waveInReset(IntPtr device);

    [DllImport("winmm.dll")]
    private static extern uint waveInClose(IntPtr device);

    private static readonly uint HeaderSize = (uint)Marshal.SizeOf<WaveHeader>();

    /// <summary>Громкость последнего куска, 0…1 — для индикатора.</summary>
    public event Action<double>? LevelChanged;

    /// <summary>Запись упёрлась в <see cref="WavFile.MaxLength"/> и остановилась сама.</summary>
    public event Action? LimitReached;

    public TimeSpan Elapsed => WavFile.Duration((int)_pcm.Length);

    /// <summary>Открывает микрофон. False — микрофона нет или доступ к нему закрыт.</summary>
    public bool Start()
    {
        var format = new WaveFormat
        {
            FormatTag = 1,
            Channels = 1,
            SamplesPerSec = WavFile.SampleRate,
            AvgBytesPerSec = WavFile.BytesPerSecond,
            BlockAlign = 2,
            BitsPerSample = 16
        };
        if (waveInOpen(out _device, WaveMapper, ref format, IntPtr.Zero, IntPtr.Zero, CallbackNull) != 0)
        {
            _device = IntPtr.Zero;
            return false;
        }

        for (var i = 0; i < BufferCount; i++)
        {
            var data = Marshal.AllocHGlobal(BufferBytes);
            var header = Marshal.AllocHGlobal((int)HeaderSize);
            Marshal.StructureToPtr(new WaveHeader { Data = data, BufferLength = BufferBytes }, header, false);
            _buffers.Add((header, data));
            waveInPrepareHeader(_device, header, HeaderSize);
            waveInAddBuffer(_device, header, HeaderSize);
        }

        if (waveInStart(_device) != 0)
        {
            Release();
            return false;
        }

        _poll.Tick += (_, _) => Collect(requeue: true);
        _poll.Start();
        return true;
    }

    /// <summary>Готовые буферы — в запись; снова в очередь драйвера, пока пишем.</summary>
    private void Collect(bool requeue)
    {
        foreach (var (header, data) in _buffers)
        {
            var state = Marshal.PtrToStructure<WaveHeader>(header);
            if ((state.Flags & WhdrDone) == 0)
            {
                continue;
            }

            if (state.BytesRecorded > 0)
            {
                var chunk = new byte[state.BytesRecorded];
                Marshal.Copy(data, chunk, 0, chunk.Length);
                _pcm.Write(chunk);
                LevelChanged?.Invoke(WavFile.Level(chunk));
            }

            if (requeue && !_stopped)
            {
                // Флаг «готово» снимается повторной подготовкой — так требует waveIn.
                waveInUnprepareHeader(_device, header, HeaderSize);
                Marshal.StructureToPtr(new WaveHeader { Data = data, BufferLength = BufferBytes }, header, false);
                waveInPrepareHeader(_device, header, HeaderSize);
                waveInAddBuffer(_device, header, HeaderSize);
            }
        }

        if (requeue && Elapsed >= WavFile.MaxLength)
        {
            LimitReached?.Invoke();
        }
    }

    /// <summary>Останавливает запись и отдаёт WAV. Пустой — ничего не записалось.</summary>
    public byte[] Stop()
    {
        if (_device == IntPtr.Zero)
        {
            return [];
        }

        _stopped = true;
        _poll.Stop();
        waveInStop(_device);
        waveInReset(_device);
        Collect(requeue: false);
        Release();
        return _pcm.Length == 0 ? [] : WavFile.Build(_pcm.GetBuffer().AsSpan(0, (int)_pcm.Length));
    }

    private void Release()
    {
        if (_device != IntPtr.Zero)
        {
            foreach (var (header, _) in _buffers)
            {
                waveInUnprepareHeader(_device, header, HeaderSize);
            }

            waveInClose(_device);
            _device = IntPtr.Zero;
        }

        foreach (var (header, data) in _buffers)
        {
            Marshal.FreeHGlobal(header);
            Marshal.FreeHGlobal(data);
        }

        _buffers.Clear();
    }

    public void Dispose()
    {
        _stopped = true;
        _poll.Stop();
        if (_device != IntPtr.Zero)
        {
            waveInStop(_device);
            waveInReset(_device);
        }

        Release();
        _pcm.Dispose();
    }
}
