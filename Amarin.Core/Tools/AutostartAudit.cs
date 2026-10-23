using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Amarin.Core;
using Microsoft.Management.Infrastructure;
using Microsoft.Win32;

namespace Amarin.Tools;

/// <summary>Откуда запускается пункт автозапуска.</summary>
internal enum AutostartSource
{
    Run,
    Folder,
    Task,
    Service,
    Wmi,
    Extension
}

/// <summary>
/// Пункт автозапуска: что запускается и, если это не файл (расширение браузера, скрипт WMI),
/// почему он рискован сам по себе.
/// </summary>
/// <param name="Command">Командная строка, из которой берётся файл; null — файла нет.</param>
/// <param name="Risk">Риск без проверки файла (не из магазина, скрипт); null — решает файл.</param>
internal sealed record AutostartItem(AutostartSource Source, string Name, string? Command, string? Risk = null);

/// <summary>Риск: пункт и почему он рискован.</summary>
internal sealed record AutostartRisk(AutostartItem Item, string? File, string Why, string? Signer);

/// <summary>
/// Аудит автозапуска (1.37.0): службы, задачи, Run-ключи и папки автозагрузки, подписки WMI и
/// расширения браузеров — со списком того, что запускается без подписи или из места, куда может
/// писать обычный пользователь.
/// </summary>
/// <remarks>
/// <para>
/// Два признака, и оба — про подмену: файл без подписи никто не ручался за, а файл в папке, куда
/// пишет пользователь, подменит любая программа этого пользователя — и запустится она потом с
/// правами службы или при каждом входе. У расширений браузера папка всегда пользовательская,
/// поэтому для них признак один: поставлено не из магазина (подписи магазина нет).
/// </para>
/// <para>
/// Данные для модели — английские строки, как у остальных инструментов. Подпись и права
/// проверяются через параметры: тесты подставляют свои и не зависят от того, что стоит на машине.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class AutostartAudit(Func<string, SignatureInfo> signature, Func<string, bool> userWritable)
{
    private readonly Dictionary<string, SignatureInfo> _signatures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _writable = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Аудит этой машины: подпись — с каталогом и без сети, права — по списку доступа.</summary>
    public static AutostartAudit ForThisMachine() => new(AuthenticodeCheck.InspectLocal, UserWritable);

    /// <summary>Собирает пункты со всей машины, проверяет и пишет отчёт.</summary>
    public string Run(CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        var items = new List<AutostartItem>();
        Collect(items, notes, "Run keys and startup folders", RunAndFolders);
        Collect(items, notes, "scheduled tasks", Tasks);
        Collect(items, notes, "services", Services);
        Collect(items, notes, "WMI subscriptions", WmiSubscriptions);
        Collect(items, notes, "browser extensions", () => BrowserExtensions(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)));

        var risks = Evaluate(items, cancellationToken);
        return Format(items.Count, risks, notes);
    }

    /// <summary>Риски среди пунктов: по одному на пункт.</summary>
    public List<AutostartRisk> Evaluate(IEnumerable<AutostartItem> items, CancellationToken cancellationToken)
    {
        var risks = new List<AutostartRisk>();
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Risk is { } risk)
            {
                risks.Add(new AutostartRisk(item, CommandLinePath.Resolve(item.Command), risk, null));
                continue;
            }

            if (CommandLinePath.Resolve(item.Command) is not { } file)
            {
                continue;
            }

            if (!File.Exists(file))
            {
                risks.Add(new AutostartRisk(item, file, "file not found", null));
                continue;
            }

            var why = new List<string>();
            var sign = Signature(file);
            if (!sign.Signed || !sign.Valid)
            {
                why.Add(sign.Signed ? "invalid signature" : "unsigned");
            }

            if (Writable(file))
            {
                why.Add("user-writable location");
            }

            if (why.Count > 0)
            {
                risks.Add(new AutostartRisk(item, file, string.Join(", ", why), sign.Publisher));
            }
        }

        return risks;
    }

    /// <summary>Отчёт для модели.</summary>
    internal static string Format(int checkedCount, IReadOnlyList<AutostartRisk> risks, IReadOnlyList<string> notes)
    {
        var text = new StringBuilder();
        text.Append("Autostart audit: ").Append(checkedCount).Append(" items checked, ")
            .Append(risks.Count).AppendLine(risks.Count == 1 ? " risk." : " risks.");
        if (risks.Count > 0)
        {
            text.AppendLine("source | name | file | why | signer");
            foreach (var risk in risks)
            {
                text.Append(Code(risk.Item.Source)).Append(" | ")
                    .Append(risk.Item.Name).Append(" | ")
                    .Append(risk.File ?? "-").Append(" | ")
                    .Append(risk.Why).Append(" | ")
                    .AppendLine(Signer(risk.Signer));
            }
        }

        foreach (var note in notes)
        {
            text.Append("Not checked: ").AppendLine(note);
        }

        return text.ToString().TrimEnd();
    }

    // ───────────────────────── источники ─────────────────────────

    private static List<AutostartItem> RunAndFolders()
    {
        var items = new List<AutostartItem>();
        foreach (var item in StartupApproval.List().Where(item => item.Enabled))
        {
            var folder = item.Location is StartupLocation.UserFolder or StartupLocation.CommonFolder;
            items.Add(new AutostartItem(
                folder ? AutostartSource.Folder : AutostartSource.Run,
                item.Name,
                folder ? ShortcutCommand(item.Command) : item.Command));
        }

        return items;
    }

    /// <summary>
    /// Включённые задачи Планировщика с действием «запустить программу» — тем же чтением через COM,
    /// что у <see cref="ScheduledTaskTool"/> (<see cref="ScheduledTasks"/>): пути в Unicode, без
    /// кодировки консоли, и скрытые задачи тоже.
    /// </summary>
    private static List<AutostartItem> Tasks() =>
        [.. ScheduledTasks.All()
            .Where(task => task.Enabled)
            .SelectMany(task => task.Actions
                .Where(action => action.Path is not null)
                .Select(action => new AutostartItem(
                    AutostartSource.Task, task.Path, Quote(action.Path ?? "") + " " + action.Arguments)))];

    /// <summary>
    /// Службы Windows со стартом при загрузке. У svchost запускается не он, а библиотека службы.
    /// </summary>
    private static List<AutostartItem> Services()
    {
        var items = new List<AutostartItem>();
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
            .OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (root is null)
        {
            return items;
        }

        foreach (var name in root.GetSubKeyNames())
        {
            using var key = OpenReadable(root, name);
            if (key is null ||
                key.GetValue("Type") is not int serviceType || (serviceType & 0x30) == 0 ||
                key.GetValue("Start") is not int start || start > 2 ||
                key.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string image)
            {
                continue;
            }

            using var parameters = OpenReadable(key, "Parameters");
            var dll = parameters?.GetValue("ServiceDll", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            var command = image.Contains("svchost", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(dll)
                ? Quote(dll)
                : image;
            items.Add(new AutostartItem(AutostartSource.Service, name, command));
        }

        return items;
    }

    /// <summary>
    /// Раздел, если его можно прочесть. У части служб раздел закрыт даже для чтения обычному
    /// пользователю, и один такой не должен оставлять без проверки все остальные.
    /// </summary>
    private static RegistryKey? OpenReadable(RegistryKey parent, string name)
    {
        try
        {
            return parent.OpenSubKey(name);
        }
        catch (System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// Постоянные подписки WMI — способ запуска, которого не видно ни в одном списке автозагрузки.
    /// </summary>
    private static List<AutostartItem> WmiSubscriptions()
    {
        var items = new List<AutostartItem>();
        using var session = CimSession.Create(null);
        foreach (var consumer in session.QueryInstances(@"root\subscription", "WQL", "SELECT * FROM CommandLineEventConsumer"))
        {
            using (consumer)
            {
                var name = consumer.CimInstanceProperties["Name"]?.Value as string ?? "?";
                var command = consumer.CimInstanceProperties["CommandLineTemplate"]?.Value as string;
                var executable = consumer.CimInstanceProperties["ExecutablePath"]?.Value as string;
                items.Add(new AutostartItem(AutostartSource.Wmi, name,
                    string.IsNullOrWhiteSpace(executable) ? command : Quote(executable)));
            }
        }

        foreach (var consumer in session.QueryInstances(@"root\subscription", "WQL", "SELECT * FROM ActiveScriptEventConsumer"))
        {
            using (consumer)
            {
                var name = consumer.CimInstanceProperties["Name"]?.Value as string ?? "?";
                items.Add(new AutostartItem(AutostartSource.Wmi, name, null, "script consumer (no file to sign)"));
            }
        }

        return items;
    }

    /// <summary>
    /// Расширения Chrome, Edge и Firefox во всех профилях, поставленные не из магазина.
    /// </summary>
    internal static List<AutostartItem> BrowserExtensions(string localAppData, string roamingAppData)
    {
        var items = new List<AutostartItem>();
        foreach (var (browser, folder) in new[]
                 {
                     ("Chrome", Path.Combine(localAppData, "Google", "Chrome", "User Data")),
                     ("Edge", Path.Combine(localAppData, "Microsoft", "Edge", "User Data"))
                 })
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (var profile in Directory.EnumerateDirectories(folder))
            {
                items.AddRange(ChromiumExtensions(browser, profile));
            }
        }

        var firefox = Path.Combine(roamingAppData, "Mozilla", "Firefox", "Profiles");
        if (Directory.Exists(firefox))
        {
            foreach (var profile in Directory.EnumerateDirectories(firefox))
            {
                items.AddRange(FirefoxExtensions(profile));
            }
        }

        return items;
    }

    /// <summary>
    /// Расширения профиля Chromium из <c>Preferences</c> и <c>Secure Preferences</c>: новые
    /// версии держат список во втором, старые — в первом.
    /// </summary>
    /// <remarks>
    /// Места установки (<c>location</c>): 4 — распакованное из папки, 8 — из командной строки:
    /// подписи магазина у них нет никогда. Остальные пользовательские места (1, 2, 3, 6) — риск,
    /// если не из магазина (<c>from_webstore: false</c>). Встроенные (5, 10) и поставленные
    /// политикой (7, 9) решал не пользователь, и они пропускаются.
    /// </remarks>
    internal static List<AutostartItem> ChromiumExtensions(string browser, string profile)
    {
        var items = new List<AutostartItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in new[] { "Secure Preferences", "Preferences" })
        {
            using var json = ReadJson(Path.Combine(profile, file));
            if (json is null ||
                !json.RootElement.TryGetProperty("extensions", out var extensions) ||
                !extensions.TryGetProperty("settings", out var settings) ||
                settings.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var extension in settings.EnumerateObject())
            {
                if (!seen.Add(extension.Name) || ChromiumRisk(extension.Value) is not { } risk)
                {
                    continue;
                }

                var name = extension.Value.TryGetProperty("manifest", out var manifest) &&
                           Text(manifest, "name") is { Length: > 0 } text && !text.StartsWith("__MSG_", StringComparison.Ordinal)
                    ? $"{text} ({extension.Name})"
                    : extension.Name;
                items.Add(new AutostartItem(AutostartSource.Extension,
                    $"{browser} / {Path.GetFileName(profile)} / {name}", null, risk));
            }
        }

        return items;
    }

    /// <summary>Расширения профиля Firefox: риск — без подписи Mozilla (<c>signedState</c> не больше нуля).</summary>
    internal static List<AutostartItem> FirefoxExtensions(string profile)
    {
        var items = new List<AutostartItem>();
        using var json = ReadJson(Path.Combine(profile, "extensions.json"));
        if (json is null || !json.RootElement.TryGetProperty("addons", out var addons) ||
            addons.ValueKind != JsonValueKind.Array)
        {
            return items;
        }

        foreach (var addon in addons.EnumerateArray())
        {
            var location = Text(addon, "location");
            if (Text(addon, "type") != "extension" ||
                location is "app-builtin" or "app-system-defaults" or "app-system-addons" ||
                !addon.TryGetProperty("signedState", out var state) || state.ValueKind != JsonValueKind.Number ||
                state.GetInt32() > 0)
            {
                continue;
            }

            var name = addon.TryGetProperty("defaultLocale", out var locale) && Text(locale, "name") is { } title
                ? $"{title} ({Text(addon, "id")})"
                : Text(addon, "id") ?? "?";
            items.Add(new AutostartItem(AutostartSource.Extension,
                $"Firefox / {Path.GetFileName(profile)} / {name}", null, "not signed by Mozilla"));
        }

        return items;
    }

    private static string? ChromiumRisk(JsonElement extension)
    {
        if (!extension.TryGetProperty("location", out var locationValue) || locationValue.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        var location = locationValue.GetInt32();
        if (location is 4 or 8)
        {
            return location == 4 ? "unpacked, not from the store" : "loaded from the command line";
        }

        var fromStore = extension.TryGetProperty("from_webstore", out var store) && store.ValueKind == JsonValueKind.True;
        return location is 1 or 2 or 3 or 6 && !fromStore ? "not from the store" : null;
    }

    // ───────────────────────── проверки ─────────────────────────

    private SignatureInfo Signature(string file)
    {
        if (!_signatures.TryGetValue(file, out var info))
        {
            info = signature(file);
            _signatures[file] = info;
        }

        return info;
    }

    private bool Writable(string file)
    {
        if (!_writable.TryGetValue(file, out var writable))
        {
            writable = userWritable(file);
            _writable[file] = writable;
        }

        return writable;
    }

    /// <summary>
    /// Может ли обычный пользователь подменить файл: запись в сам файл или создание в его папке.
    /// </summary>
    /// <remarks>
    /// Решает список доступа (<see cref="AccessExplain.Decide"/>) для текущего пользователя и групп,
    /// в которых состоит любой: Users, Authenticated Users, Everyone, INTERACTIVE. Администраторов
    /// в наборе нет: файл, который правит только администратор, обычному пользователю не подменить.
    /// Нечитаемый список доступа — не «можно писать»: доказательства нет.
    /// </remarks>
    internal static bool UserWritable(string file)
    {
        var sids = new HashSet<SecurityIdentifier>
        {
            new(WellKnownSidType.BuiltinUsersSid, null),
            new(WellKnownSidType.AuthenticatedUserSid, null),
            new(WellKnownSidType.WorldSid, null),
            new(WellKnownSidType.InteractiveSid, null)
        };
        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (identity.User is { } user)
            {
                sids.Add(user);
            }
        }

        const int writeData = 0x2; // у файла — запись данных, у папки — создание файла в ней.
        var folder = Path.GetDirectoryName(file);
        return CanWrite(new AclTarget(file, IsRegistry: false, IsDirectory: false, default), writeData, sids) ||
               (folder is not null && CanWrite(new AclTarget(folder, IsRegistry: false, IsDirectory: true, default), writeData, sids));
    }

    private static bool CanWrite(AclTarget target, int bit, HashSet<SecurityIdentifier> sids)
    {
        try
        {
            var aces = AccessExplain.Aces(AclIo.Read(target), registry: false);
            return AccessExplain.Decide(aces, bit, sids, new HashSet<SecurityIdentifier>()).Allowed == true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException
                                       or IdentityNotMappedException)
        {
            return false;
        }
    }

    // ───────────────────────── мелочи ─────────────────────────

    private static void Collect(List<AutostartItem> items, List<string> notes, string what, Func<List<AutostartItem>> source)
    {
        try
        {
            items.AddRange(source());
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException ||
                                   (ex is CimException cim && cim.NativeErrorCode == NativeErrorCode.AccessDenied))
        {
            notes.Add(what + " (administrator rights needed)");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            notes.Add($"{what} ({ex.Message})");
        }
    }

    /// <summary>Командная строка ярлыка из папки автозагрузки; не ярлык — сам файл.</summary>
    private static string ShortcutCommand(string path)
    {
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
            Type.GetTypeFromProgID("WScript.Shell") is not { } type)
        {
            return Quote(path);
        }

        try
        {
            if (Activator.CreateInstance(type) is not { } instance)
            {
                return Quote(path);
            }

            dynamic shell = instance;
            var shortcut = shell.CreateShortcut(path);
            string target = shortcut.TargetPath ?? "";
            string arguments = shortcut.Arguments ?? "";
            return target.Length == 0 ? Quote(path) : Quote(target) + " " + arguments;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return Quote(path);
        }
    }

    private static JsonDocument? ReadJson(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true })
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Quote(string path) => "\"" + path.Trim('"') + "\"";

    private static string Code(AutostartSource source) => source switch
    {
        AutostartSource.Run => "run key",
        AutostartSource.Folder => "startup folder",
        AutostartSource.Task => "scheduled task",
        AutostartSource.Service => "service",
        AutostartSource.Wmi => "wmi subscription",
        _ => "browser extension"
    };

    private static string Signer(string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher))
        {
            return "-";
        }

        // Из «CN=Издатель, O=…, C=…» модели нужно только имя.
        var cn = publisher.Split(',').Select(part => part.Trim())
            .FirstOrDefault(part => part.StartsWith("CN=", StringComparison.OrdinalIgnoreCase));
        return cn is null ? publisher : cn[3..].Trim('"');
    }
}
