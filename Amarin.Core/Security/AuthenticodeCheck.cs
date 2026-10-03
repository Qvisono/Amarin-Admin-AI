using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Amarin.Core;

/// <summary>Подпись файла: есть ли, цела ли и кто подписал.</summary>
/// <param name="Signed">В файле есть подпись Authenticode.</param>
/// <param name="Valid">Windows проверила подпись и цепочку сертификатов.</param>
/// <param name="Publisher">Субъект сертификата подписавшего; null — подписи нет.</param>
public sealed record SignatureInfo(bool Signed, bool Valid, string? Publisher)
{
    public static readonly SignatureInfo None = new(false, false, null);
}

/// <summary>
/// Проверка подписи Authenticode через WinVerifyTrust — та же, что делает Проводник.
/// </summary>
/// <remarks>
/// Вне Windows подписи не бывает: там программа не работает, а тесты видят «не подписан».
/// </remarks>
public static class AuthenticodeCheck
{
    /// <summary>Подменяется в тестах: настоящая проверка ходит в Windows.</summary>
    internal static Func<string, SignatureInfo> Inspector { get; set; } = InspectWithWindows;

    public static SignatureInfo Inspect(string path) => Inspector(path);

    private static SignatureInfo InspectWithWindows(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return SignatureInfo.None;
        }

        string? publisher;
        try
        {
#pragma warning disable SYSLIB0057 // Подписанный файл, а не сертификат из файла: замены у этого вызова нет.
            using var certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            publisher = certificate.Subject;
        }
        catch (CryptographicException)
        {
            return SignatureInfo.None;
        }

        return new SignatureInfo(true, Verify(path) == 0, publisher);
    }

    /// <summary>
    /// Можно ли ставить новый файл вместо текущего — по правилу подписи.
    /// </summary>
    /// <remarks>
    /// Текущий exe подписан — новый обязан быть подписан, цел и подписан тем же издателем:
    /// иначе подменённый релиз на GitHub с правильной суммой из того же подменённого релиза
    /// прошёл бы все проверки. Текущий не подписан — решает сумма SHA-256; требовать подписи,
    /// которой у программы никогда не было, значило бы запретить обновление вовсе.
    /// </remarks>
    /// <returns>Текст отказа для человека; null — можно.</returns>
    public static string? Refusal(SignatureInfo current, SignatureInfo incoming)
    {
        if (!current.Signed || !current.Valid)
        {
            return null;
        }

        if (!incoming.Signed || !incoming.Valid)
        {
            return Loc.Get("S.Updates.NotSigned");
        }

        return string.Equals(current.Publisher, incoming.Publisher, StringComparison.Ordinal)
            ? null
            : Loc.Get("S.Updates.OtherPublisher");
    }

    // ───────────────────────── WinVerifyTrust ─────────────────────────

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint UiNone = 2;
    private const uint RevokeWholeChain = 1;
    private const uint ChoiceFile = 1;
    private const uint StateActionVerify = 1;
    private const uint StateActionClose = 2;

    private static int Verify(string path)
    {
        var file = new WinTrustFileInfo
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = path
        };

        var fileHandle = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(file, fileHandle, fDeleteOld: false);
            var data = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = UiNone,
                RevocationChecks = RevokeWholeChain,
                UnionChoice = ChoiceFile,
                FileInfo = fileHandle,
                StateAction = StateActionVerify
            };

            var action = GenericVerifyV2;
            var result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);

            // Состояние, открытое проверкой, закрывается вторым вызовом — иначе утечка.
            data.StateAction = StateActionClose;
            _ = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return result;
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(fileHandle);
            Marshal.FreeHGlobal(fileHandle);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string FilePath;

        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }
}
