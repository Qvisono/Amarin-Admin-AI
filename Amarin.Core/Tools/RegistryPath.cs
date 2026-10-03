namespace Amarin.Tools;

/// <summary>
/// Путь раздела реестра в каноническом виде: <c>HKLM\SOFTWARE\Vendor</c>.
/// </summary>
/// <remarks>
/// Прежде <c>include_registry_paths</c> уходил в команду <c>reg.exe</c> и в имя файла как есть:
/// кавычка в пути добавляла reg свои аргументы, а <c>..</c> или <c>/</c> в имени файла уводили
/// экспорт из папки снимка. Теперь снимок читается API реестра, а файл называется хэшем пути,
/// так что от имени раздела требуется немногое: известный корень, никаких управляющих символов
/// и пустых частей. Звёздочку и кавычки разделы носить вправе — <c>HKCR\*\shell</c> настоящий.
/// </remarks>
internal readonly record struct RegistryPath(string Root, string SubKey)
{
    /// <summary>Каноническая запись: корень, обратная косая, подраздел.</summary>
    public string Canonical => SubKey.Length == 0 ? Root : $@"{Root}\{SubKey}";

    public override string ToString() => Canonical;

    private static readonly (string Prefix, string Root)[] Roots =
    [
        ("HKEY_LOCAL_MACHINE", "HKLM"),
        ("HKLM", "HKLM"),
        ("HKEY_CURRENT_USER", "HKCU"),
        ("HKCU", "HKCU"),
        ("HKEY_CLASSES_ROOT", "HKCR"),
        ("HKCR", "HKCR"),
        ("HKEY_USERS", "HKU"),
        ("HKU", "HKU"),
        ("HKEY_CURRENT_CONFIG", "HKCC"),
        ("HKCC", "HKCC")
    ];

    /// <summary>
    /// Разбирает путь в любой из привычных записей: <c>HKLM\…</c>, <c>HKEY_LOCAL_MACHINE\…</c>,
    /// <c>HKLM:\…</c> (PowerShell), <c>Registry::HKEY_LOCAL_MACHINE\…</c>.
    /// </summary>
    /// <param name="requireSubKey">Весь куст целиком снимать нельзя — он огромен.</param>
    public static bool TryParse(string? input, out RegistryPath path, bool requireSubKey = true)
    {
        path = default;
        var value = (input ?? "").Trim();
        if (value.Length == 0 || value.Length > 1024 || value.Any(char.IsControl))
        {
            return false;
        }

        // Косая «/» — законная часть имени раздела (HKCR\MIME\Database\Content Type\text/html),
        // разделителем она не считается.
        const string provider = "Registry::";
        if (value.StartsWith(provider, StringComparison.OrdinalIgnoreCase))
        {
            value = value[provider.Length..];
        }

        foreach (var (prefix, root) in Roots)
        {
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rest = value[prefix.Length..];
            if (rest.StartsWith(':'))
            {
                rest = rest[1..];
            }

            if (rest.Length > 0 && rest[0] != '\\')
            {
                continue;
            }

            var parts = rest.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Any(part => part is "." or ".." || part.Trim().Length == 0))
            {
                return false;
            }

            var subKey = string.Join('\\', parts);
            if (requireSubKey && subKey.Length == 0)
            {
                return false;
            }

            path = new RegistryPath(root, subKey);
            return true;
        }

        return false;
    }

    /// <summary>Лежит ли <paramref name="other"/> внутри этого раздела (или совпадает с ним).</summary>
    public bool Contains(RegistryPath other) =>
        string.Equals(Root, other.Root, StringComparison.OrdinalIgnoreCase) &&
        (SubKey.Length == 0 ||
         string.Equals(SubKey, other.SubKey, StringComparison.OrdinalIgnoreCase) ||
         other.SubKey.StartsWith(SubKey + "\\", StringComparison.OrdinalIgnoreCase));
}
