using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Amarin.Tools;

/// <summary>Чтение раздела реестра в <see cref="RegistryKeyState"/> и применение шагов отката.</summary>
/// <remarks>
/// Через API реестра, а не <c>reg export</c>/<c>reg import</c>: импорт умеет только дописывать,
/// а процесс на каждый раздел стоил по полсекунды. Пределы нужны потому, что раздел для снимка
/// может назвать модель, и <c>HKLM\SOFTWARE</c> целиком — это сотни тысяч значений.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class RegistryStateIo
{
    public const int MaxKeys = 5_000;
    public const int MaxValues = 50_000;
    public const int MaxDepth = 24;
    public const int MaxValueBytes = 1 << 20;

    private sealed class Budget
    {
        public int Keys;
        public int Values;
    }

    /// <summary>Снимает раздел: целиком или, при <paramref name="deep"/> = false, только значения.</summary>
    public static RegistryKeyState Capture(RegistryPath path, bool deep)
    {
        using var root = OpenRoot(path.Root);
        if (root is null)
        {
            return RegistryKeyState.Missing(path.Canonical);
        }

        RegistryKey? key;
        try
        {
            key = path.SubKey.Length == 0 ? root : root.OpenSubKey(path.SubKey, writable: false);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return new RegistryKeyState { Path = path.Canonical, Exists = true, Truncated = true, Shallow = !deep };
        }

        if (key is null)
        {
            return RegistryKeyState.Missing(path.Canonical);
        }

        using (key)
        {
            var state = Read(key, path.Canonical, deep ? 0 : -1, new Budget());
            state.Shallow = !deep;
            return state;
        }
    }

    /// <summary>
    /// Снимок перед записью значения. Если раздела нет, снимается самый верхний из
    /// недостающих: запись создаёт всю цепочку разом, и откат должен убрать её всю, а не только
    /// последний раздел.
    /// </summary>
    public static RegistryKeyState CaptureForWrite(RegistryPath path)
    {
        var target = path;
        while (!Exists(target) && target.SubKey.Contains('\\'))
        {
            var parent = new RegistryPath(target.Root, target.SubKey[..target.SubKey.LastIndexOf('\\')]);
            if (Exists(parent))
            {
                break;
            }

            target = parent;
        }

        return Capture(target, deep: false);
    }

    /// <summary>Нынешний вид раздела той же формы, что снятый: для сравнения при откате.</summary>
    public static RegistryKeyState CaptureLike(RegistryKeyState snapshot)
    {
        if (!RegistryPath.TryParse(snapshot.Path, out var path, requireSubKey: false))
        {
            return RegistryKeyState.Missing(snapshot.Path);
        }

        // Раздела не было: откату хватит знать, появился ли он, — читать выросшее поддерево
        // ради одного «удалить целиком» незачем.
        if (!snapshot.Exists)
        {
            return Exists(path)
                ? new RegistryKeyState { Path = snapshot.Path, Exists = true }
                : RegistryKeyState.Missing(snapshot.Path);
        }

        return Capture(path, deep: !snapshot.Shallow);
    }

    public static bool Exists(RegistryPath path)
    {
        try
        {
            using var root = OpenRoot(path.Root);
            if (root is null)
            {
                return false;
            }

            using var key = path.SubKey.Length == 0 ? null : root.OpenSubKey(path.SubKey, writable: false);
            return path.SubKey.Length == 0 || key is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Раздел есть, но закрыт — для снимка это «существует».
            return true;
        }
    }

    /// <param name="depth">−1 — только значения; иначе текущая глубина обхода.</param>
    private static RegistryKeyState Read(RegistryKey key, string path, int depth, Budget budget)
    {
        var state = new RegistryKeyState { Path = path, Exists = true };
        budget.Keys++;

        string[] names;
        try
        {
            names = key.GetValueNames();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            state.Truncated = true;
            return state;
        }

        foreach (var name in names)
        {
            if (budget.Values >= MaxValues)
            {
                state.Truncated = true;
                break;
            }

            if (ReadValue(key, name) is { } value)
            {
                state.Values.Add(value);
                budget.Values++;
            }
            else
            {
                state.Truncated = true;
            }
        }

        if (depth < 0)
        {
            return state;
        }

        string[] children;
        try
        {
            children = key.GetSubKeyNames();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            state.Truncated = true;
            return state;
        }

        if (depth >= MaxDepth && children.Length > 0)
        {
            state.Truncated = true;
            return state;
        }

        foreach (var name in children)
        {
            if (budget.Keys >= MaxKeys)
            {
                state.Truncated = true;
                break;
            }

            var childPath = $@"{path}\{name}";
            try
            {
                using var child = key.OpenSubKey(name, writable: false);
                if (child is null)
                {
                    continue;
                }

                state.SubKeys.Add(Read(child, childPath, depth + 1, budget));
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // Закрытый подраздел: есть, но что в нём — неизвестно. Такой откат не трогает.
                state.SubKeys.Add(new RegistryKeyState { Path = childPath, Exists = true, Truncated = true });
            }
        }

        return state;
    }

    private static RegistryValueState? ReadValue(RegistryKey key, string name)
    {
        try
        {
            var kind = key.GetValueKind(name);
            var data = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            var value = new RegistryValueState { Name = name, Kind = kind.ToString() };
            switch (kind)
            {
                case RegistryValueKind.String:
                case RegistryValueKind.ExpandString:
                    value.Text = data as string ?? "";
                    break;
                case RegistryValueKind.MultiString:
                    value.Lines = (data as string[] ?? []).ToList();
                    break;
                case RegistryValueKind.DWord:
                    value.Number = data is int dword ? dword : 0;
                    break;
                case RegistryValueKind.QWord:
                    value.Number = data is long qword ? qword : 0;
                    break;
                default:
                    var bytes = data as byte[] ?? [];
                    if (bytes.Length > MaxValueBytes)
                    {
                        return null;
                    }

                    value.Bytes = Convert.ToBase64String(bytes);
                    break;
            }

            return value;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>Применяет шаги по порядку. Сбой одного шага остальные не останавливает.</summary>
    public static IReadOnlyList<RollbackOutcome> Apply(IReadOnlyList<RegistryChange> changes)
    {
        var outcomes = new List<RollbackOutcome>(changes.Count);
        foreach (var change in changes)
        {
            outcomes.Add(ApplyOne(change));
        }

        return outcomes;
    }

    private static RollbackOutcome ApplyOne(RegistryChange change)
    {
        var line = RollbackText.Describe(change);
        if (!RegistryPath.TryParse(change.KeyPath, out var path) ||
            (path.Root == "HKLM" && SensitivePaths.IsSecretHive(path.SubKey)))
        {
            return new RollbackOutcome(line, false, Amarin.Core.Loc.Get("S.Rollback.BadPath"));
        }

        try
        {
            using var root = OpenRoot(path.Root) ?? throw new IOException(path.Root);
            switch (change.Kind)
            {
                case RegistryChangeKind.CreateKey:
                    root.CreateSubKey(path.SubKey, writable: false)?.Dispose();
                    break;

                case RegistryChangeKind.SetValue:
                {
                    using var key = root.CreateSubKey(path.SubKey, writable: true)
                                    ?? throw new IOException(path.Canonical);
                    var (data, kind) = ToRegistry(change.Value!);
                    key.SetValue(change.ValueName ?? "", data, kind);
                    break;
                }

                case RegistryChangeKind.DeleteValue:
                {
                    using var key = root.OpenSubKey(path.SubKey, writable: true);
                    key?.DeleteValue(change.ValueName ?? "", throwOnMissingValue: false);
                    break;
                }

                case RegistryChangeKind.DeleteKey:
                {
                    var slash = path.SubKey.LastIndexOf('\\');
                    if (slash < 0)
                    {
                        return new RollbackOutcome(line, false, Amarin.Core.Loc.Get("S.Rollback.BadPath"));
                    }

                    using var parent = root.OpenSubKey(path.SubKey[..slash], writable: true);
                    parent?.DeleteSubKeyTree(path.SubKey[(slash + 1)..], throwOnMissingSubKey: false);
                    break;
                }
            }

            return new RollbackOutcome(line, true, null);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException
                                       or IOException or ArgumentException or InvalidOperationException
                                       or FormatException)
        {
            return new RollbackOutcome(line, false, ex.Message);
        }
    }

    private static (object Data, RegistryValueKind Kind) ToRegistry(RegistryValueState value) =>
        value.Kind switch
        {
            "String" => (value.Text ?? "", RegistryValueKind.String),
            "ExpandString" => (value.Text ?? "", RegistryValueKind.ExpandString),
            "MultiString" => ((value.Lines ?? []).ToArray(), RegistryValueKind.MultiString),
            "DWord" => (unchecked((int)(value.Number ?? 0)), RegistryValueKind.DWord),
            "QWord" => (value.Number ?? 0L, RegistryValueKind.QWord),
            "None" => (Convert.FromBase64String(value.Bytes ?? ""), RegistryValueKind.None),
            _ => (Convert.FromBase64String(value.Bytes ?? ""), RegistryValueKind.Binary)
        };

    private static RegistryKey? OpenRoot(string root) => root switch
    {
        "HKLM" => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default),
        "HKCU" => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default),
        "HKCR" => RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Default),
        "HKU" => RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default),
        "HKCC" => RegistryKey.OpenBaseKey(RegistryHive.CurrentConfig, RegistryView.Default),
        _ => null
    };
}
