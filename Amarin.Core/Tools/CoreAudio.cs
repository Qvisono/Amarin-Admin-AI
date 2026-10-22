using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Amarin.Tools;

/// <summary>Устройство вывода или ввода звука.</summary>
internal sealed record AudioDevice(
    string Id,
    string Name,
    bool Output,
    string State,
    bool IsDefault,
    bool IsCommunications,
    int? Volume,
    bool? Muted);

/// <summary>Сеанс звука программы на устройстве вывода.</summary>
internal sealed record AudioSession(string DeviceName, int Pid, string App, bool Active, bool SystemSounds, int Volume, bool Muted);

/// <summary>
/// Core Audio Windows: устройства, громкость и сеансы программ — через COM в процессе.
/// </summary>
/// <remarks>
/// <para>
/// Интерфейсы описаны целиком в порядке их таблиц методов, включая те методы, что здесь не
/// зовутся: COM вызывает по номеру в таблице, и пропущенный метод сдвинул бы все следующие.
/// </para>
/// <para>
/// Устройство по умолчанию назначает <c>IPolicyConfig</c>. Он не документирован, но им
/// пользуется сама панель «Звук» Windows, и открытого способа нет: без него «сделай наушники
/// устройством по умолчанию» агент мог бы только объяснить словами.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class CoreAudio
{
    private const uint ClsctxAll = 23;
    private const uint StateActive = 1, StateDisabled = 2, StateUnplugged = 8;
    private const int ENotFound = unchecked((int)0x80070490);

    private static readonly Guid AudioEndpointVolumeId = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private static readonly Guid AudioSessionManager2Id = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private static readonly PropertyKey FriendlyNameKey = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

    /// <summary>Устройства, кроме удалённых: включённые, выключенные и отсоединённые.</summary>
    public static List<AudioDevice> Devices()
    {
        var enumerator = Enumerator();
        try
        {
            var defaults = new Dictionary<(DataFlow, Role), string?>();
            foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
            {
                foreach (var role in new[] { Role.Multimedia, Role.Communications })
                {
                    defaults[(flow, role)] = DefaultId(enumerator, flow, role);
                }
            }

            var result = new List<AudioDevice>();
            Check(enumerator.EnumAudioEndpoints(DataFlow.All, StateActive | StateDisabled | StateUnplugged, out var collection));
            try
            {
                Check(collection.GetCount(out var count));
                for (uint i = 0; i < count; i++)
                {
                    Check(collection.Item(i, out var device));
                    try
                    {
                        result.Add(Describe(device, defaults));
                    }
                    finally
                    {
                        Release(device);
                    }
                }
            }
            finally
            {
                Release(collection);
            }

            return result;
        }
        finally
        {
            Release(enumerator);
        }
    }

    /// <summary>Сеансы программ на всех включённых устройствах вывода.</summary>
    public static List<AudioSession> Sessions() => WithSessions((_, _, _) => { });

    /// <summary>Громкость и/или звук программы на всех её сеансах. Возвращает число тронутых сеансов.</summary>
    public static int SetApp(Func<int, string, bool> matches, int? volume, bool? mute)
    {
        var changed = 0;
        _ = WithSessions((session, control, simple) =>
        {
            if (!matches(session.Pid, session.App))
            {
                return;
            }

            var context = Guid.Empty;
            if (volume is { } level)
            {
                Check(simple.SetMasterVolume(level / 100f, ref context));
            }

            if (mute is { } muted)
            {
                Check(simple.SetMute(muted, ref context));
            }

            changed++;
        });
        return changed;
    }

    public static void SetDevice(string id, int? volume, bool? mute)
    {
        var enumerator = Enumerator();
        try
        {
            Check(enumerator.GetDevice(id, out var device));
            try
            {
                var endpoint = EndpointVolume(device);
                try
                {
                    var context = Guid.Empty;
                    if (volume is { } level)
                    {
                        Check(endpoint.SetMasterVolumeLevelScalar(level / 100f, ref context));
                    }

                    if (mute is { } muted)
                    {
                        Check(endpoint.SetMute(muted, ref context));
                    }
                }
                finally
                {
                    Release(endpoint);
                }
            }
            finally
            {
                Release(device);
            }
        }
        finally
        {
            Release(enumerator);
        }
    }

    /// <summary>Назначает устройство по умолчанию для ролей: обычной (Console и Multimedia) и/или связи.</summary>
    public static void SetDefault(string id, bool normal, bool communications)
    {
        var policy = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            if (normal)
            {
                Check(policy.SetDefaultEndpoint(id, Role.Console));
                Check(policy.SetDefaultEndpoint(id, Role.Multimedia));
            }

            if (communications)
            {
                Check(policy.SetDefaultEndpoint(id, Role.Communications));
            }
        }
        finally
        {
            Release(policy);
        }
    }

    /// <summary>Обходит сеансы и даёт каждому действие над его громкостью.</summary>
    private static List<AudioSession> WithSessions(Action<AudioSession, IAudioSessionControl2, ISimpleAudioVolume> act)
    {
        var result = new List<AudioSession>();
        var enumerator = Enumerator();
        try
        {
            Check(enumerator.EnumAudioEndpoints(DataFlow.Render, StateActive, out var collection));
            try
            {
                Check(collection.GetCount(out var count));
                for (uint i = 0; i < count; i++)
                {
                    Check(collection.Item(i, out var device));
                    try
                    {
                        var name = FriendlyName(device);
                        var iid = AudioSessionManager2Id;
                        Check(device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out var managerObject));
                        var manager = (IAudioSessionManager2)managerObject;
                        try
                        {
                            Check(manager.GetSessionEnumerator(out var sessions));
                            try
                            {
                                Check(sessions.GetCount(out var sessionCount));
                                for (var s = 0; s < sessionCount; s++)
                                {
                                    Check(sessions.GetSession(s, out var raw));
                                    try
                                    {
                                        if (raw is not IAudioSessionControl2 control || raw is not ISimpleAudioVolume simple)
                                        {
                                            continue;
                                        }

                                        if (Read(name, control, simple) is { } session)
                                        {
                                            act(session, control, simple);
                                            result.Add(session);
                                        }
                                    }
                                    finally
                                    {
                                        Release(raw);
                                    }
                                }
                            }
                            finally
                            {
                                Release(sessions);
                            }
                        }
                        finally
                        {
                            Release(manager);
                        }
                    }
                    finally
                    {
                        Release(device);
                    }
                }
            }
            finally
            {
                Release(collection);
            }
        }
        finally
        {
            Release(enumerator);
        }

        return result;
    }

    /// <returns>null — сеанс закончился (программа уже не играет и не откроет его снова).</returns>
    private static AudioSession? Read(string device, IAudioSessionControl2 control, ISimpleAudioVolume simple)
    {
        _ = control.GetState(out var state);
        if (state == 2)
        {
            return null;
        }

        _ = control.GetProcessId(out var pid);
        _ = control.GetDisplayName(out var display);
        var systemSounds = control.IsSystemSoundsSession() == 0;
        _ = simple.GetMasterVolume(out var level);
        _ = simple.GetMute(out var muted);

        // Имя сеанса часто пустое или ссылка на ресурс («@%SystemRoot%\…»): тогда — имя процесса.
        var app = systemSounds ? "System sounds" : !string.IsNullOrWhiteSpace(display) && !display.StartsWith('@') ? display : ProcessName((int)pid);
        return new AudioSession(device, (int)pid, app, state == 1, systemSounds, (int)Math.Round(level * 100), muted);
    }

    private static AudioDevice Describe(IMMDevice device, Dictionary<(DataFlow, Role), string?> defaults)
    {
        Check(device.GetId(out var id));
        Check(device.GetState(out var state));
        var output = true;
        if (device is IMMEndpoint endpointFlow && endpointFlow.GetDataFlow(out var flow) == 0)
        {
            output = flow == DataFlow.Render;
        }

        var direction = output ? DataFlow.Render : DataFlow.Capture;

        int? volume = null;
        bool? muted = null;
        if (state == StateActive)
        {
            var endpoint = EndpointVolume(device);
            try
            {
                if (endpoint.GetMasterVolumeLevelScalar(out var level) == 0)
                {
                    volume = (int)Math.Round(level * 100);
                }

                if (endpoint.GetMute(out var mute) == 0)
                {
                    muted = mute;
                }
            }
            finally
            {
                Release(endpoint);
            }
        }

        return new AudioDevice(
            id,
            FriendlyName(device),
            output,
            state switch { StateActive => "active", StateDisabled => "disabled", StateUnplugged => "unplugged", _ => "not present" },
            string.Equals(defaults[(direction, Role.Multimedia)], id, StringComparison.OrdinalIgnoreCase),
            string.Equals(defaults[(direction, Role.Communications)], id, StringComparison.OrdinalIgnoreCase),
            volume,
            muted);
    }

    private static IAudioEndpointVolume EndpointVolume(IMMDevice device)
    {
        var iid = AudioEndpointVolumeId;
        Check(device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out var value));
        return (IAudioEndpointVolume)value;
    }

    private static string? DefaultId(IMMDeviceEnumerator enumerator, DataFlow flow, Role role)
    {
        var code = enumerator.GetDefaultAudioEndpoint(flow, role, out var device);
        if (code == ENotFound || device is null)
        {
            return null;
        }

        Check(code);
        try
        {
            return device.GetId(out var id) == 0 ? id : null;
        }
        finally
        {
            Release(device);
        }
    }

    private static string FriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(0, out var store) != 0)
        {
            return "";
        }

        try
        {
            var key = FriendlyNameKey;
            if (store.GetValue(ref key, out var value) != 0)
            {
                return "";
            }

            try
            {
                return value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) ?? "" : "";
            }
            finally
            {
                _ = PropVariantClear(ref value);
            }
        }
        finally
        {
            Release(store);
        }
    }

    private static string ProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return $"PID {pid}";
        }
    }

    private static IMMDeviceEnumerator Enumerator() => (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

    /// <summary>Код ошибки COM — исключением, которое инструмент превратит в ответ модели.</summary>
    private static void Check(int code)
    {
        if (code < 0)
        {
            Marshal.ThrowExceptionForHR(code);
        }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.ReleaseComObject(value);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    internal enum DataFlow
    {
        Render = 0,
        Capture = 1,
        All = 2
    }

    internal enum Role
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey(Guid format, int id)
    {
        public Guid Format = format;
        public int Id = id;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropVariant
    {
        public ushort Type;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public IntPtr Pointer;
        public IntPtr Extra;
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class PolicyConfigClient
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(DataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(DataFlow dataFlow, Role role, out IMMDevice endpoint);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int Item(uint index, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object value);

        [PreserveSig]
        int OpenPropertyStore(uint access, out IPropertyStore properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out uint state);
    }

    [ComImport]
    [Guid("1BE09788-6894-4089-8586-9A2A6C265AC5")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMEndpoint
    {
        [PreserveSig]
        int GetDataFlow(out DataFlow flow);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetAt(uint index, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int Commit();
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolume
    {
        [PreserveSig]
        int RegisterControlChangeNotify(IntPtr notify);

        [PreserveSig]
        int UnregisterControlChangeNotify(IntPtr notify);

        [PreserveSig]
        int GetChannelCount(out uint count);

        [PreserveSig]
        int SetMasterVolumeLevel(float levelDb, ref Guid context);

        [PreserveSig]
        int SetMasterVolumeLevelScalar(float level, ref Guid context);

        [PreserveSig]
        int GetMasterVolumeLevel(out float levelDb);

        [PreserveSig]
        int GetMasterVolumeLevelScalar(out float level);

        [PreserveSig]
        int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);

        [PreserveSig]
        int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);

        [PreserveSig]
        int GetChannelVolumeLevel(uint channel, out float levelDb);

        [PreserveSig]
        int GetChannelVolumeLevelScalar(uint channel, out float level);

        [PreserveSig]
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);

        [PreserveSig]
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);

        [PreserveSig]
        int GetVolumeStepInfo(out uint step, out uint stepCount);

        [PreserveSig]
        int VolumeStepUp(ref Guid context);

        [PreserveSig]
        int VolumeStepDown(ref Guid context);

        [PreserveSig]
        int QueryHardwareSupport(out uint mask);

        [PreserveSig]
        int GetVolumeRange(out float minDb, out float maxDb, out float incrementDb);
    }

    /// <summary>
    /// IAudioSessionManager2 с методами IAudioSessionManager впереди: интерфейсы COM в .NET
    /// таблицу предка не наследуют, её повторяют.
    /// </summary>
    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionManager2
    {
        [PreserveSig]
        int GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IntPtr control);

        [PreserveSig]
        int GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out IntPtr volume);

        [PreserveSig]
        int GetSessionEnumerator(out IAudioSessionEnumerator sessions);

        [PreserveSig]
        int RegisterSessionNotification(IntPtr notification);

        [PreserveSig]
        int UnregisterSessionNotification(IntPtr notification);

        [PreserveSig]
        int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr notification);

        [PreserveSig]
        int UnregisterDuckNotification(IntPtr notification);
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionEnumerator
    {
        [PreserveSig]
        int GetCount(out int count);

        [PreserveSig]
        int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    /// <summary>IAudioSessionControl2 с методами IAudioSessionControl впереди.</summary>
    [ComImport]
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl2
    {
        [PreserveSig]
        int GetState(out int state);

        [PreserveSig]
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);

        [PreserveSig]
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);

        [PreserveSig]
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);

        [PreserveSig]
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);

        [PreserveSig]
        int GetGroupingParam(out Guid grouping);

        [PreserveSig]
        int SetGroupingParam(ref Guid grouping, ref Guid context);

        [PreserveSig]
        int RegisterAudioSessionNotification(IntPtr notification);

        [PreserveSig]
        int UnregisterAudioSessionNotification(IntPtr notification);

        [PreserveSig]
        int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetProcessId(out uint pid);

        /// <returns>0 (S_OK) — сеанс системных звуков, 1 (S_FALSE) — нет.</returns>
        [PreserveSig]
        int IsSystemSoundsSession();

        [PreserveSig]
        int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport]
    [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISimpleAudioVolume
    {
        [PreserveSig]
        int SetMasterVolume(float level, ref Guid context);

        [PreserveSig]
        int GetMasterVolume(out float level);

        [PreserveSig]
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);

        [PreserveSig]
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    /// <summary>
    /// Недокументированный интерфейс панели «Звук» (Windows 7 и новее). Вызывается только
    /// SetDefaultEndpoint; остальные методы стоят ради своего места в таблице.
    /// </summary>
    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        [PreserveSig]
        int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr format);

        [PreserveSig]
        int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int useDefault, IntPtr format);

        [PreserveSig]
        int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

        [PreserveSig]
        int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);

        [PreserveSig]
        int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int useDefault, IntPtr defaultPeriod, IntPtr minimumPeriod);

        [PreserveSig]
        int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr period);

        [PreserveSig]
        int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);

        [PreserveSig]
        int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);

        [PreserveSig]
        int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int fxStore, IntPtr key, IntPtr value);

        [PreserveSig]
        int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int fxStore, IntPtr key, IntPtr value);

        [PreserveSig]
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, Role role);

        [PreserveSig]
        int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }
}
