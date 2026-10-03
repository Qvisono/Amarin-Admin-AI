namespace Amarin.Tools;

/// <summary>
/// Разделы реестра одного снимка. Снимок дополняется по ходу запроса, а не создаётся заново.
/// </summary>
/// <remarks>
/// <para>
/// Снимок один на запрос: первая правка его создаёт, следующие переиспользуют. Поэтому вторая
/// запись в реестр за тот же запрос — в другой раздел — раньше в снимок не попадала вовсе.
/// Теперь перед каждой правкой в снимок дописывается её раздел, если его там ещё нет.
/// </para>
/// <para>
/// Главное правило: <b>первый снятый вид раздела побеждает</b>. Второй снимок того же раздела
/// сделан уже после правки, и заменить им первый значило бы откатывать к изменённому. Поэтому
/// раздел, уже лежащий в снимке, повторно не снимается, а при снятии объемлющего раздела ранее
/// снятые вложенные вживляются в него прежними.
/// </para>
/// </remarks>
internal sealed class RegistrySnapshotSet
{
    private readonly List<RegistryKeyState> _roots;

    public RegistrySnapshotSet(IEnumerable<RegistryKeyState>? roots = null)
    {
        _roots = roots?.ToList() ?? [];
    }

    public IReadOnlyList<RegistryKeyState> Roots => _roots;

    /// <summary>
    /// Нужно ли снимать раздел: нет ли его уже в снимке в нужной полноте.
    /// </summary>
    public bool Covers(string path, bool deep)
    {
        foreach (var root in _roots)
        {
            if (!Contains(root.Path, path))
            {
                continue;
            }

            // Раздела не было — значит, откат уберёт его целиком вместе со всем, что под ним.
            if (!root.Exists)
            {
                return true;
            }

            if (root.Shallow)
            {
                // Снятые значения покрывают только сам раздел и только запись значений.
                if (string.Equals(root.Path, path, StringComparison.OrdinalIgnoreCase) && !deep)
                {
                    return true;
                }

                continue;
            }

            // Глубокий снимок видел всё поддерево: вложенного раздела в нём нет — значит, его
            // тогда и не было, и откат его уберёт. Кроме неполного снимка: там «нет» не значит
            // «не было».
            var node = root.Find(path);
            if (node is not null || !AnyTruncatedOnTheWay(root, path))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Дописывает снятый раздел. Возвращает false, если он уже был покрыт.</summary>
    public bool Add(RegistryKeyState capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var deep = !capture.Shallow;
        if (Covers(capture.Path, deep))
        {
            return false;
        }

        // Тот же раздел снят раньше только значениями, теперь нужен целиком (удаление раздела
        // после записи в него): берём новое поддерево, но значения — прежние.
        var sameShallow = _roots.FindIndex(root =>
            root.Shallow && string.Equals(root.Path, capture.Path, StringComparison.OrdinalIgnoreCase));
        if (sameShallow >= 0)
        {
            var earlier = _roots[sameShallow];
            _roots.RemoveAt(sameShallow);
            Graft(capture, earlier);
        }

        // Внутри нового раздела уже лежат снятые раньше — вживляем их прежний вид.
        if (deep)
        {
            for (var i = _roots.Count - 1; i >= 0; i--)
            {
                if (Contains(capture.Path, _roots[i].Path) &&
                    !string.Equals(capture.Path, _roots[i].Path, StringComparison.OrdinalIgnoreCase))
                {
                    Graft(capture, _roots[i]);
                    _roots.RemoveAt(i);
                }
            }
        }

        _roots.Add(capture);
        return true;
    }

    /// <summary>Кладёт прежний вид <paramref name="earlier"/> на его место внутри <paramref name="tree"/>.</summary>
    private static void Graft(RegistryKeyState tree, RegistryKeyState earlier)
    {
        if (string.Equals(tree.Path, earlier.Path, StringComparison.OrdinalIgnoreCase))
        {
            tree.Exists = earlier.Exists;
            tree.Values = earlier.Values;
            tree.Truncated |= earlier.Truncated;
            if (!earlier.Shallow)
            {
                tree.SubKeys = earlier.SubKeys;
            }

            return;
        }

        var node = tree;
        var parts = earlier.Path[(tree.Path.Length + 1)..].Split('\\');
        for (var i = 0; i < parts.Length; i++)
        {
            var childPath = $@"{node.Path}\{parts[i]}";
            var child = node.SubKeys.FirstOrDefault(key =>
                string.Equals(RegistryDiff.LastPart(key.Path), parts[i], StringComparison.OrdinalIgnoreCase));

            if (child is null)
            {
                if (!earlier.Exists)
                {
                    // Раздела не было тогда и нет в новом снимке — вживлять нечего.
                    return;
                }

                // Промежуточный раздел исчез после раннего снимка. Он вернётся пустым, а его
                // прежние значения неизвестны — поэтому «неполный»: лишнего в нём откат не тронет.
                child = i == parts.Length - 1
                    ? new RegistryKeyState { Path = childPath }
                    : new RegistryKeyState { Path = childPath, Exists = true, Truncated = true };
                node.SubKeys.Add(child);
            }

            node = child;
        }

        node.Exists = earlier.Exists;
        node.Values = earlier.Values;
        node.Truncated = earlier.Truncated || (earlier.Shallow && node.Truncated);
        node.Shallow = false;
        if (!earlier.Shallow)
        {
            node.SubKeys = earlier.SubKeys;
        }
        else if (!earlier.Exists)
        {
            node.SubKeys = [];
        }
    }

    private static bool AnyTruncatedOnTheWay(RegistryKeyState root, string path)
    {
        var node = root;
        if (node.Truncated)
        {
            return true;
        }

        foreach (var part in path[(root.Path.Length + 1)..].Split('\\'))
        {
            node = node.SubKeys.FirstOrDefault(child =>
                string.Equals(RegistryDiff.LastPart(child.Path), part, StringComparison.OrdinalIgnoreCase));
            if (node is null)
            {
                return false;
            }

            if (node.Truncated || node.Shallow)
            {
                return true;
            }
        }

        return false;
    }

    private static bool Contains(string ancestor, string path) =>
        string.Equals(ancestor, path, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(ancestor + "\\", StringComparison.OrdinalIgnoreCase);
}
