using System.Text.Json;

namespace Amarin.Core;

/// <summary>
/// Собственный журнал трат программы: то, что она сама заплатила, по дням и по моделям.
/// </summary>
/// <remarks>
/// Venice ведёт свой, куда более полный журнал, но отдаёт его только админ-ключу
/// (<c>billing/usage-history</c> отвечает обычному ключу <c>401 Admin API key required</c>),
/// а для запросов к моделям человек держит обычный. Поэтому основным источником графика
/// служит этот журнал, а журнал Venice подхватывается, когда ключ его всё-таки отдаёт.
/// <para>
/// Пишется из единственной точки учёта денег — <c>VeniceClient.AddCost</c>, — через которую
/// проходят ответы, придуманные заголовки чатов, скрытые сводки, поиск в сети, чтение страниц,
/// картинки, агент и SynGuard. Чего он не видит: трат вне программы и неудачных попыток из
/// цепочки замен модели — те списываются, а до <c>AddCost</c> не доходят.
/// </para>
/// <para>
/// Запись отложенная. Деньги учитываются на каждый ответ, а то и чаще, и писать файл на
/// каждое списание значило бы дёргать диск посреди печати ответа.
/// </para>
/// </remarks>
internal sealed class SpendLedger
{
    /// <summary>Реже трогать диск незачем, чаще — уже слышно на ходу.</summary>
    private static readonly TimeSpan WriteEvery = TimeSpan.FromSeconds(20);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, SpendHistoryFile> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);

    private string _root;
    private DateTime _lastWrite = DateTime.MinValue;

    public SpendLedger(string root) => _root = root;

    /// <summary>Переезд на другой профиль: у него своя папка и свой журнал.</summary>
    public void UseRoot(string root)
    {
        Flush();
        lock (_gate)
        {
            _root = root;
            _files.Clear();
            _dirty.Clear();
        }
    }

    private string PathFor(string fingerprint) =>
        Path.Combine(_root, "usage", fingerprint + ".local.json");

    /// <summary>
    /// Записывает списание в сегодняшнюю корзину.
    /// </summary>
    /// <remarks>
    /// Зовётся из потока хода, а ходов до трёх сразу плюс инструменты в параллель — отсюда замок.
    /// Ничего не бросает: потерянная копейка в журнале не повод ронять ответ.
    /// </remarks>
    public void Record(string? secret, VeniceCost cost, string sku)
    {
        if (cost is null || (cost.Usd <= 0 && cost.Diem <= 0))
        {
            return;
        }

        var fingerprint = VeniceKeyStore.Fingerprint(secret);
        var today = DateTime.Now.Date;
        var label = string.IsNullOrWhiteSpace(sku) ? SpendFold.OtherSku : sku.Trim();

        lock (_gate)
        {
            var file = Loaded(fingerprint);
            var day = file.Days.FirstOrDefault(item => item.Date == today);
            if (day is null)
            {
                day = new SpendDay { Date = today };
                file.Days.Add(day);
                file.Days.Sort((left, right) => left.Date.CompareTo(right.Date));
            }

            var bucket = day.Skus.FirstOrDefault(
                item => string.Equals(item.Sku, label, StringComparison.Ordinal));
            if (bucket is null)
            {
                bucket = new SpendSkuBucket { Sku = label };
                day.Skus.Add(bucket);
            }

            bucket.Requests++;
            bucket.Usd += cost.Usd;
            bucket.Diem += cost.Diem;
            day.Usd += cost.Usd;
            day.Diem += cost.Diem;
            _dirty.Add(fingerprint);
        }

        if (DateTime.UtcNow - _lastWrite > WriteEvery)
        {
            Flush();
        }
    }

    /// <summary>Журнал этого ключа со всем, что ещё не легло на диск.</summary>
    public SpendHistoryFile Read(string? secret)
    {
        lock (_gate)
        {
            return Loaded(VeniceKeyStore.Fingerprint(secret));
        }
    }

    /// <summary>
    /// Переносит в журнал траты, уже записанные в переписках.
    /// </summary>
    /// <remarks>
    /// Иначе у человека, обновившегося на эту версию, график был бы пуст, хотя цена каждого
    /// ответа давно лежит в его же файлах чатов. Делается ровно один раз: отметка
    /// <see cref="SpendHistoryFile.BackfilledAt"/> закрывает эту дверь навсегда, и живой учёт
    /// после неё не удваивает те же деньги — он пишет только то, что платится сейчас.
    /// </remarks>
    /// <param name="sessions">Переписки с диска; читаются вызывающим, ему же известен их корень.</param>
    public bool Backfill(string? secret, IEnumerable<ChatSession> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        var fingerprint = VeniceKeyStore.Fingerprint(secret);

        // Отметка ставится сразу и под коротким замком: обход диска долгий, и второй заход,
        // начатый пока идёт первый, не должен перенести те же деньги второй раз.
        lock (_gate)
        {
            var claimed = Loaded(fingerprint);
            if (claimed.BackfilledAt is not null)
            {
                return false;
            }

            claimed.BackfilledAt = DateTime.Now;
            _dirty.Add(fingerprint);
        }

        // Чтение переписок — вне замка. Внутри него оно заморозило бы учёт денег у идущих
        // ходов: файлы чатов бывают в мегабайты, и ответ ждал бы конца обхода.
        var collected = new Dictionary<DateTime, Dictionary<string, SpendSkuBucket>>();
        var today = DateTime.Now.Date;
        foreach (var message in sessions.SelectMany(session => session.Messages))
        {
            if (!string.Equals(message.Role, "assistant", StringComparison.Ordinal) ||
                message.Cost is not { HasData: true } cost ||
                (cost.Usd <= 0 && cost.Diem <= 0))
            {
                continue;
            }

            var date = message.CreatedAt.Date;
            if (date == default || date > today)
            {
                continue;
            }

            if (!collected.TryGetValue(date, out var buckets))
            {
                buckets = new Dictionary<string, SpendSkuBucket>(StringComparer.Ordinal);
                collected[date] = buckets;
            }

            // Цена сообщения — весь его счёт целиком: ответ, инструменты, заголовок чата,
            // сводка и защитник. Это ровно то, что ушло с ключа за этот ход.
            var sku = message.ResolvedModelId ?? message.RequestedModelId ?? SpendFold.OtherSku;
            if (!buckets.TryGetValue(sku, out var bucket))
            {
                bucket = new SpendSkuBucket { Sku = sku };
                buckets[sku] = bucket;
            }

            bucket.Requests++;
            bucket.Usd += cost.Usd;
            bucket.Diem += cost.Diem;
        }

        lock (_gate)
        {
            var file = Loaded(fingerprint);
            foreach (var (date, buckets) in collected)
            {
                var day = file.Days.FirstOrDefault(item => item.Date == date);
                if (day is null)
                {
                    day = new SpendDay { Date = date };
                    file.Days.Add(day);
                }

                foreach (var bucket in buckets.Values)
                {
                    var existing = day.Skus.FirstOrDefault(
                        item => string.Equals(item.Sku, bucket.Sku, StringComparison.Ordinal));
                    if (existing is null)
                    {
                        day.Skus.Add(bucket);
                    }
                    else
                    {
                        existing.Requests += bucket.Requests;
                        existing.Usd += bucket.Usd;
                        existing.Diem += bucket.Diem;
                    }

                    day.Usd += bucket.Usd;
                    day.Diem += bucket.Diem;
                }
            }

            file.Days.Sort((left, right) => left.Date.CompareTo(right.Date));
            _dirty.Add(fingerprint);
        }

        Flush();
        return true;
    }

    /// <summary>Кладёт на диск всё накопленное. Best-effort, как и остальные файлы рядом.</summary>
    public void Flush()
    {
        List<(string Fingerprint, string Json)> pending;
        lock (_gate)
        {
            if (_dirty.Count == 0)
            {
                return;
            }

            pending = [];
            foreach (var fingerprint in _dirty)
            {
                var file = _files[fingerprint];
                file.TimeZoneId = TimeZoneInfo.Local.Id;
                SpendHistoryStore.Trim(file);
                pending.Add((fingerprint, JsonSerializer.Serialize(file, AppJson.Options)));
            }

            _dirty.Clear();
            _lastWrite = DateTime.UtcNow;
        }

        foreach (var (fingerprint, json) in pending)
        {
            try
            {
                Directory.CreateDirectory(Path.Combine(_root, "usage"));
                AppDataFile.WriteAtomic(PathFor(fingerprint), json);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
            }
        }
    }

    /// <summary>Читает файл ключа с диска один раз за запуск и дальше держит его в памяти.</summary>
    private SpendHistoryFile Loaded(string fingerprint)
    {
        if (_files.TryGetValue(fingerprint, out var cached))
        {
            return cached;
        }

        var file = ReadFile(PathFor(fingerprint));
        _files[fingerprint] = file;
        return file;
    }

    private static SpendHistoryFile ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new SpendHistoryFile();
            }

            var file = JsonSerializer.Deserialize<SpendHistoryFile>(
                File.ReadAllText(path), AppJson.Options) ?? new SpendHistoryFile();
            file.Days ??= [];

            // Пояс сменился — «вчера» в корзинах уже не то «вчера», которое увидит человек.
            return string.Equals(file.TimeZoneId, TimeZoneInfo.Local.Id, StringComparison.Ordinal)
                ? file
                : new SpendHistoryFile();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new SpendHistoryFile();
        }
    }
}
