using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Amarin.Core;

/// <summary>Откуда ключ взялся.</summary>
public enum VeniceKeySource
{
    /// <summary>Человек ввёл его на странице «Key &amp; Info».</summary>
    Stored,

    /// <summary>Переменная окружения <c>VENICE_API_KEY</c> или user-secrets.</summary>
    Environment
}

/// <summary>Запись из <c>keys.json</c>. Сам ключ в ней лежит только зашифрованным.</summary>
public sealed class VeniceKeyRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Label { get; set; } = "";

    /// <summary>Блоб DPAPI в base64. Открытым текстом ключ на диск не попадает никогда.</summary>
    public string Protected { get; set; } = "";

    public DateTime AddedAt { get; set; } = DateTime.Now;
}

/// <summary>Содержимое <c>keys.json</c>.</summary>
public sealed class VeniceKeyFile
{
    public string? ActiveKeyId { get; set; }

    public List<VeniceKeyRecord> Keys { get; set; } = [];
}

/// <summary>
/// Строка списка ключей — то, что видит страница настроек.
/// </summary>
/// <param name="Id">
/// У ключа из окружения — <see cref="VeniceKeyStore.EnvironmentId"/>: его в файле нет.
/// </param>
/// <param name="Secret">
/// Ключ открытым текстом либо <c>null</c>, если блоб не расшифровался. Живёт только в памяти.
/// </param>
public sealed record VeniceKeyEntry(
    string Id,
    string Label,
    string? Secret,
    VeniceKeySource Source,
    bool IsActive)
{
    /// <summary>Блоб зашифрован не этой учётной записью Windows — показать нечего.</summary>
    public bool IsBroken => Secret is null;

    /// <summary>Замаскированный вид для списка: <c>vk-•••••••••ab12</c>.</summary>
    public string Masked => VeniceKeyStore.Mask(Secret);

    /// <summary>Удалить можно только то, что человек сам и добавил.</summary>
    public bool CanRemove => Source == VeniceKeySource.Stored;
}

/// <summary>
/// Ключи Venice: несколько на выбор, зашифрованные на диске.
/// </summary>
/// <remarks>
/// До версии 1.22.0 ключ был один и брался только из переменной окружения — поля для него
/// в интерфейсе не было намеренно, чтобы не хранить секрет. Теперь ключей может быть
/// несколько, и хранятся они зашифрованными средствами Windows
/// (см. <see cref="DataProtector"/>): расшифровать их сможет только та учётная запись, под
/// которой их вводили.
/// <para>
/// Ключ из окружения остаётся и всегда стоит в списке первым, неудаляемой строкой. В файл он
/// не переписывается: человек сознательно держал его снаружи программы, и копировать его на
/// диск за него никто не вправе. Если своих ключей нет, работает он — у обновившегося
/// пользователя ничего не меняется.
/// </para>
/// <para>
/// Файл лежит в папке профиля, как <c>settings.json</c>: профили разделены паролем, и чужой
/// ключ вместе с его тратами в соседнем профиле видеть незачем.
/// </para>
/// </remarks>
internal sealed class VeniceKeyStore
{
    /// <summary>Идентификатор строки ключа из окружения. В файле такого никогда нет.</summary>
    public const string EnvironmentId = "environment";

    private readonly string _path;
    private readonly string _environmentKey;
    private VeniceKeyFile _file = new();

    public VeniceKeyStore(string? root = null, string environmentKey = "")
    {
        _path = root is null ? AppPaths.KeysFile : Path.Combine(root, "keys.json");
        _environmentKey = environmentKey ?? "";
    }

    /// <summary>Есть ли ключ в переменной окружения — от этого зависит вид пустого списка.</summary>
    public bool HasEnvironmentKey => !string.IsNullOrWhiteSpace(_environmentKey);

    /// <summary>Никогда не бросает: повреждённый или отсутствующий файл — это пустой список.</summary>
    public void Load()
    {
        try
        {
            _file = File.Exists(_path)
                ? JsonSerializer.Deserialize<VeniceKeyFile>(File.ReadAllText(_path), AppJson.Options)
                  ?? new VeniceKeyFile()
                : new VeniceKeyFile();
            _file.Keys ??= [];
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _file = new VeniceKeyFile();
        }
    }

    /// <summary>
    /// Best-effort, как и у остальных файлов рядом: не записавшийся список — потеря одной
    /// строки, а не повод показывать окно аварии посреди настроек.
    /// </summary>
    public void Save()
    {
        try
        {
            AppDataFile.WriteAtomic(_path, JsonSerializer.Serialize(_file, AppJson.Options));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }

    /// <summary>Строки для списка: ключ из окружения первым, остальные в порядке добавления.</summary>
    public IReadOnlyList<VeniceKeyEntry> List()
    {
        var activeId = ResolveActiveId();
        var entries = new List<VeniceKeyEntry>();

        if (HasEnvironmentKey)
        {
            entries.Add(new VeniceKeyEntry(
                EnvironmentId,
                "VENICE_API_KEY",
                _environmentKey,
                VeniceKeySource.Environment,
                string.Equals(activeId, EnvironmentId, StringComparison.Ordinal)));
        }

        foreach (var record in _file.Keys)
        {
            entries.Add(new VeniceKeyEntry(
                record.Id,
                record.Label,
                DataProtector.Unprotect(record.Protected),
                VeniceKeySource.Stored,
                string.Equals(activeId, record.Id, StringComparison.Ordinal)));
        }

        return entries;
    }

    /// <summary>Ключ, которым платить. Пустая строка — платить нечем.</summary>
    public string ActiveSecret()
    {
        var activeId = ResolveActiveId();
        if (string.Equals(activeId, EnvironmentId, StringComparison.Ordinal))
        {
            return _environmentKey;
        }

        var record = _file.Keys.FirstOrDefault(
            item => string.Equals(item.Id, activeId, StringComparison.Ordinal));
        return DataProtector.Unprotect(record?.Protected) ?? _environmentKey;
    }

    /// <summary>
    /// Все ключи, какие есть, — для вырезания из отчёта об аварии. Именно все, а не только
    /// активный: в стек попадает тот, которым отправляли запрос, а он мог быть и прежним.
    /// </summary>
    public IReadOnlyList<string?> AllSecrets()
    {
        var secrets = new List<string?>();
        if (HasEnvironmentKey)
        {
            secrets.Add(_environmentKey);
        }

        secrets.AddRange(_file.Keys.Select(record => DataProtector.Unprotect(record.Protected)));
        return secrets;
    }

    /// <summary>
    /// Добавляет ключ и делает его активным. <c>null</c> — Windows отказалась шифровать либо
    /// такой ключ уже есть.
    /// </summary>
    public VeniceKeyRecord? Add(string label, string secret)
    {
        secret = (secret ?? "").Trim();
        if (secret.Length == 0 || Contains(secret))
        {
            return null;
        }

        var blob = DataProtector.Protect(secret);
        if (blob is null)
        {
            return null;
        }

        var record = new VeniceKeyRecord
        {
            Label = string.IsNullOrWhiteSpace(label) ? Mask(secret) : label.Trim(),
            Protected = blob
        };

        _file.Keys.Add(record);
        _file.ActiveKeyId = record.Id;
        Save();
        return record;
    }

    /// <summary>Уже есть такой ключ? Сравнение по значению, не по подписи.</summary>
    public bool Contains(string secret) =>
        (HasEnvironmentKey && string.Equals(_environmentKey, secret, StringComparison.Ordinal)) ||
        _file.Keys.Any(record => string.Equals(
            DataProtector.Unprotect(record.Protected), secret, StringComparison.Ordinal));

    /// <summary>Ключ из окружения не удаляется: программа им не распоряжается.</summary>
    public bool Remove(string id)
    {
        if (string.Equals(id, EnvironmentId, StringComparison.Ordinal))
        {
            return false;
        }

        if (_file.Keys.RemoveAll(record => string.Equals(record.Id, id, StringComparison.Ordinal)) == 0)
        {
            return false;
        }

        if (string.Equals(_file.ActiveKeyId, id, StringComparison.Ordinal))
        {
            _file.ActiveKeyId = null;
        }

        Save();
        return true;
    }

    public bool SetActive(string id)
    {
        if (!string.Equals(id, EnvironmentId, StringComparison.Ordinal) &&
            !_file.Keys.Any(record => string.Equals(record.Id, id, StringComparison.Ordinal)))
        {
            return false;
        }

        _file.ActiveKeyId = id;
        Save();
        return true;
    }

    /// <summary>
    /// Кто активен на самом деле. Записанный выбор может указывать в пустоту — ключ удалили
    /// в другом запуске программы, — и тогда работает первый годный.
    /// </summary>
    private string ResolveActiveId()
    {
        var chosen = _file.ActiveKeyId;
        if (!string.IsNullOrWhiteSpace(chosen))
        {
            if (string.Equals(chosen, EnvironmentId, StringComparison.Ordinal) && HasEnvironmentKey)
            {
                return EnvironmentId;
            }

            if (_file.Keys.Any(record => string.Equals(record.Id, chosen, StringComparison.Ordinal)))
            {
                return chosen;
            }
        }

        // Свой ключ вперёд переменной окружения: человек добавил его сознательно и позже.
        return _file.Keys.Count > 0 ? _file.Keys[0].Id : EnvironmentId;
    }

    /// <summary>
    /// Ключ для списка: начало, хвост и ровно столько точек, сколько их в маске.
    /// </summary>
    /// <remarks>
    /// Число точек постоянное, а не по длине ключа: длина — тоже сведение о секрете, и
    /// показывать её незачем.
    /// </remarks>
    public static string Mask(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return "—";
        }

        const string Dots = "•••••••••";
        return secret.Length <= 8
            ? Dots
            : secret[..3] + Dots + secret[^4..];
    }

    /// <summary>
    /// Короткая подпись ключа — имя файла с его тратами. Сам ключ в имя файла попасть не должен.
    /// </summary>
    public static string Fingerprint(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return "none";
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexStringLower(hash)[..16];
    }
}
