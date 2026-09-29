namespace Amarin.Tools;

/// <summary>
/// Раздел реестра, снятый в снимок: значения и подразделы, как они были.
/// </summary>
/// <remarks>
/// <para>
/// Прежде снимок хранил только экспорт <c>.reg</c>, а откат его импортировал. Импорт умеет лишь
/// дописывать: значение, добавленное после снимка, переживало откат, и ровно то, что стоило
/// убрать (новая запись в Run, подложенный параметр), оставалось на месте. По этому состоянию
/// откат сравнивает «было» с «стало» и трогает только отличия — в обе стороны.
/// </para>
/// <para>
/// Лишь чистые данные: сравнение живёт в <see cref="RegistryDiff"/> и проверяется тестами без
/// Windows, а чтение и запись реестра — в <see cref="RegistryStateIo"/>.
/// </para>
/// </remarks>
internal sealed class RegistryKeyState
{
    /// <summary>Каноническая запись пути: <c>HKLM\SOFTWARE\Vendor</c>.</summary>
    public string Path { get; set; } = "";

    /// <summary>Раздела не было — откат удалит его, если он появился.</summary>
    public bool Exists { get; set; }

    public List<RegistryValueState> Values { get; set; } = [];

    public List<RegistryKeyState> SubKeys { get; set; } = [];

    /// <summary>
    /// Снимок раздела неполон: упёрся в предел или какой-то подраздел не открылся. Тогда
    /// «нет в снимке» не значит «добавлено после», и появившееся откат не удаляет.
    /// </summary>
    public bool Truncated { get; set; }

    /// <summary>
    /// Сняты только значения, без подразделов. Так снимается раздел перед записью значения:
    /// поддерево <c>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion</c> огромно, а меняется в нём
    /// одно значение. Подразделы такого раздела откат не трогает ни в какую сторону.
    /// </summary>
    public bool Shallow { get; set; }

    public static RegistryKeyState Missing(string path) => new() { Path = path, Exists = false };

    /// <summary>Узел поддерева по полному пути или null, если такого в снимке нет.</summary>
    public RegistryKeyState? Find(string path)
    {
        if (string.Equals(Path, path, StringComparison.OrdinalIgnoreCase))
        {
            return this;
        }

        if (!path.StartsWith(Path + "\\", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var node = this;
        foreach (var part in path[(Path.Length + 1)..].Split('\\'))
        {
            node = node.SubKeys.FirstOrDefault(child =>
                string.Equals(RegistryDiff.LastPart(child.Path), part, StringComparison.OrdinalIgnoreCase));
            if (node is null)
            {
                return null;
            }
        }

        return node;
    }
}

/// <summary>Одно значение раздела. Заполнено ровно одно поле данных — по <see cref="Kind"/>.</summary>
internal sealed class RegistryValueState
{
    /// <summary>Имя; пустая строка — значение «по умолчанию».</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Имя <c>RegistryValueKind</c>: String, ExpandString, MultiString, DWord, QWord, Binary,
    /// None, Unknown. Строкой, а не перечислением: модель данных живёт вне Windows.
    /// </summary>
    public string Kind { get; set; } = "";

    public string? Text { get; set; }

    public List<string>? Lines { get; set; }

    public long? Number { get; set; }

    /// <summary>Сырые байты в base64 — для Binary, None и типов, которых .NET не различает.</summary>
    public string? Bytes { get; set; }

    /// <summary>
    /// Можно ли записать значение обратно тем же типом. Типы вроде REG_RESOURCE_LIST .NET
    /// читает байтами, а записать умеет только как Binary — такое откат не подменяет.
    /// </summary>
    public bool Restorable => Kind is "String" or "ExpandString" or "MultiString" or "DWord" or "QWord"
        or "Binary" or "None";

    public bool SameAs(RegistryValueState other) =>
        string.Equals(Kind, other.Kind, StringComparison.Ordinal) &&
        string.Equals(Text, other.Text, StringComparison.Ordinal) &&
        Number == other.Number &&
        string.Equals(Bytes, other.Bytes, StringComparison.Ordinal) &&
        (Lines ?? []).SequenceEqual(other.Lines ?? [], StringComparer.Ordinal);

    /// <summary>Коротко, для строки плана: «"C:\app.exe" (String)».</summary>
    public string Preview(int max = 80)
    {
        var text = Kind switch
        {
            "String" or "ExpandString" => $"\"{Text}\"",
            "MultiString" => string.Join(" | ", Lines ?? []),
            "DWord" or "QWord" => Number?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            _ => Bytes is { Length: > 0 } ? $"{Convert.FromBase64String(Bytes).Length} B" : "0 B"
        };

        return (text.Length <= max ? text : text[..max] + "…") + $" ({Kind})";
    }
}

internal enum RegistryChangeKind
{
    /// <summary>Вернуть значение (его не стало или оно изменилось).</summary>
    SetValue,

    /// <summary>Убрать значение, которого в снимке не было.</summary>
    DeleteValue,

    /// <summary>Вернуть удалённый раздел (значения идут отдельными шагами SetValue).</summary>
    CreateKey,

    /// <summary>Убрать раздел, которого в снимке не было, вместе с поддеревом.</summary>
    DeleteKey
}

/// <summary>Один шаг отката реестра.</summary>
internal sealed record RegistryChange(
    RegistryChangeKind Kind,
    string KeyPath,
    string? ValueName = null,
    RegistryValueState? Value = null,
    RegistryValueState? Current = null);

/// <summary>Сравнение снятого раздела с нынешним.</summary>
internal static class RegistryDiff
{
    /// <summary>
    /// Шаги, которые вернут раздел к снимку. Порядок: сначала раздел, потом его значения, потом
    /// подразделы — применять можно подряд.
    /// </summary>
    public static IReadOnlyList<RegistryChange> Plan(RegistryKeyState snapshot, RegistryKeyState current)
    {
        var changes = new List<RegistryChange>();
        Walk(snapshot, current, changes);
        return changes;
    }

    private static void Walk(RegistryKeyState was, RegistryKeyState now, List<RegistryChange> changes)
    {
        if (!was.Exists)
        {
            // Раздела не было, а теперь он есть: убрать целиком. «Не было» — факт, а не пробел
            // в снимке, поэтому неполнота родителя тут не мешает.
            if (now.Exists)
            {
                changes.Add(new RegistryChange(RegistryChangeKind.DeleteKey, was.Path));
            }

            return;
        }

        if (!now.Exists)
        {
            changes.Add(new RegistryChange(RegistryChangeKind.CreateKey, was.Path));
        }

        var current = now.Exists
            ? now.Values
                .GroupBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, RegistryValueState>(StringComparer.OrdinalIgnoreCase);

        foreach (var value in was.Values)
        {
            current.TryGetValue(value.Name, out var present);
            if (present is not null && present.SameAs(value))
            {
                continue;
            }

            if (!value.Restorable)
            {
                continue;
            }

            changes.Add(new RegistryChange(RegistryChangeKind.SetValue, was.Path, value.Name, value, present));
        }

        if (now.Exists && !was.Truncated)
        {
            var before = was.Values.Select(value => value.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var value in now.Values)
            {
                if (!before.Contains(value.Name))
                {
                    changes.Add(new RegistryChange(
                        RegistryChangeKind.DeleteValue, was.Path, value.Name, Current: value));
                }
            }
        }

        if (was.Shallow)
        {
            return;
        }

        var nowKeys = now.Exists
            ? now.SubKeys
                .GroupBy(key => LastPart(key.Path), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, RegistryKeyState>(StringComparer.OrdinalIgnoreCase);

        foreach (var child in was.SubKeys)
        {
            var name = LastPart(child.Path);
            Walk(child, nowKeys.GetValueOrDefault(name) ?? RegistryKeyState.Missing(child.Path), changes);
        }

        if (now.Exists && !was.Truncated)
        {
            var before = was.SubKeys.Select(key => LastPart(key.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var child in now.SubKeys)
            {
                if (!before.Contains(LastPart(child.Path)))
                {
                    changes.Add(new RegistryChange(RegistryChangeKind.DeleteKey, $@"{was.Path}\{LastPart(child.Path)}"));
                }
            }
        }
    }

    internal static string LastPart(string path)
    {
        var slash = path.LastIndexOf('\\');
        return slash < 0 ? path : path[(slash + 1)..];
    }
}
