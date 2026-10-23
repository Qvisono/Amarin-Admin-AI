using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
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
    /// <summary>Подпись файла по мнению Windows.</summary>
    /// <remarks>
    /// До 1.30.0 здесь стояло общее изменяемое <c>Inspector</c>, которое тесты подменяли на время
    /// проверки, — и держали ради этого отдельную коллекцию. Теперь подмена — параметр
    /// <see cref="UpdateInstaller.VerifyBeforeSwap"/>.
    /// </remarks>
    public static SignatureInfo Inspect(string path)
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

        return new SignatureInfo(true, Verify(path, online: true) == 0, publisher);
    }

    /// <summary>Издатель у файла, подписанного каталогом Windows: самого сертификата в файле нет.</summary>
    public const string CatalogPublisher = "Microsoft Windows (catalog)";

    /// <summary>
    /// Подпись файла без сети и с каталогами Windows (1.37.0, аудит автозапуска).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Без сети: отзыв сертификата проверяется по кэшу, иначе аудит сотен файлов ходил бы в
    /// интернет за каждым. Для обновления программы это не годится — там остаётся <see cref="Inspect"/>.
    /// </para>
    /// <para>
    /// С каталогами: большая часть файлов Windows (svchost и библиотеки служб) подписана не
    /// внутри файла, а хешем в каталоге системы, и без этой проверки выглядела бы неподписанной.
    /// Найденный в каталоге хеш и есть подпись: каталоги ставит и проверяет сама Windows, а лежат
    /// они там, куда пишет только администратор.
    /// </para>
    /// </remarks>
    public static SignatureInfo InspectLocal(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return SignatureInfo.None;
        }

        try
        {
#pragma warning disable SYSLIB0057 // Подписанный файл, а не сертификат из файла: замены у этого вызова нет.
            using var certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return new SignatureInfo(true, Verify(path, online: false) == 0, certificate.Subject);
        }
        catch (CryptographicException)
        {
            return InCatalog(path) ? new SignatureInfo(true, true, CatalogPublisher) : SignatureInfo.None;
        }
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
    private const uint RevokeNone = 0;
    private const uint RevokeWholeChain = 1;
    private const uint CacheOnlyUrlRetrieval = 0x1000;
    private const uint ChoiceFile = 1;
    private const uint StateActionVerify = 1;
    private const uint StateActionClose = 2;

    private static int Verify(string path, bool online)
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
                RevocationChecks = online ? RevokeWholeChain : RevokeNone,
                UnionChoice = ChoiceFile,
                FileInfo = fileHandle,
                StateAction = StateActionVerify,
                ProvFlags = online ? 0u : CacheOnlyUrlRetrieval
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

    // ───────────────────────── каталоги ─────────────────────────

    /// <summary>Есть ли хеш файла в каталоге Windows: SHA-256, а у старых каталогов — SHA-1.</summary>
    private static bool InCatalog(string path)
    {
        foreach (var algorithm in new[] { "SHA256", null })
        {
            if (!CryptCATAdminAcquireContext2(out var admin, IntPtr.Zero, algorithm, IntPtr.Zero, 0))
            {
                continue;
            }

            try
            {
                using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                uint size = 0;
                _ = CryptCATAdminCalcHashFromFileHandle2(admin, file, ref size, null, 0);
                if (size == 0)
                {
                    continue;
                }

                var hash = new byte[size];
                if (!CryptCATAdminCalcHashFromFileHandle2(admin, file, ref size, hash, 0))
                {
                    continue;
                }

                var catalog = CryptCATAdminEnumCatalogFromHash(admin, hash, size, 0, IntPtr.Zero);
                if (catalog != IntPtr.Zero)
                {
                    _ = CryptCATAdminReleaseCatalogContext(admin, catalog, 0);
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
            finally
            {
                _ = CryptCATAdminReleaseContext(admin, 0);
            }
        }

        return false;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminAcquireContext2(
        out IntPtr admin, IntPtr subsystem, string? hashAlgorithm, IntPtr strongHashPolicy, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle2(
        IntPtr admin, SafeFileHandle file, ref uint hashSize, byte[]? hash, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern IntPtr CryptCATAdminEnumCatalogFromHash(
        IntPtr admin, byte[] hash, uint hashSize, uint flags, IntPtr previous);

    [DllImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminReleaseCatalogContext(IntPtr admin, IntPtr catalog, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminReleaseContext(IntPtr admin, uint flags);

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
