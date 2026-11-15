namespace Amarin.Core;

/// <summary>Остаток одного ключа. Ключ назван отпечатком — самого секрета здесь нет.</summary>
public sealed record KeyBalance(
    string Fingerprint,
    decimal? Usd,
    decimal? Diem,
    DateTime SeenUtc,
    decimal? RublesPerUsd = null);

/// <summary>Строка разбивки: чей остаток и сколько.</summary>
/// <param name="Fingerprint">Отпечаток ключа — по нему ищется порог остатка этого ключа (E4).</param>
public readonly record struct BalanceRow(string Label, LlmProvider Provider, decimal? Usd, decimal? Diem, string? Fingerprint = null);

/// <summary>Сумма остатков и то, чего в ней не хватает.</summary>
/// <param name="Unknown">Сколько ключей ещё не ответили: без них сумма занижена.</param>
public readonly record struct BalanceTotal(decimal? Usd, decimal? Diem, int Unknown);

/// <summary>
/// Остатки всех ключей разом — то, из чего плашка в композере складывает сумму.
/// </summary>
/// <remarks>
/// До версии 1.23.0 остаток был один: платил один ключ, и число приезжало даром на заголовках
/// ответа Venice. Теперь платят несколько, и одно число перестало отвечать на вопрос «сколько
/// у меня осталось».
/// <para>
/// Venice кладёт остаток в заголовки каждого ответа, поэтому его ключи наполняются здесь
/// бесплатно. У OpenRouter таких заголовков нет вовсе — его остаток спрашивают отдельным
/// запросом, и до появления этой книги плашка на ключе OpenRouter оставалась пустой навсегда.
/// Кого дозапрашивать, решает <see cref="Stale"/>.
/// </para>
/// <para>
/// Ключ назван отпечатком, как и файлы трат в <c>usage/</c>: по нему секрет не восстановить,
/// зато книгу можно сохранить между запусками. Читают и пишут её из разных потоков — ходов
/// чата до трёх сразу плюс страница настроек, — поэтому доступ под замком.
/// </para>
/// </remarks>
internal sealed class BalanceBook
{
    private readonly Func<decimal?> _rublesPerUsd;
    private readonly Func<LlmProvider, ApiCredential>? _defaultCredential;

    public BalanceBook(Func<decimal?>? rublesPerUsd = null, Func<LlmProvider, ApiCredential>? defaultCredential = null)
    {
        _rublesPerUsd = rublesPerUsd ?? (() => null);
        _defaultCredential = defaultCredential;
    }

    /// <summary>Чаще этого один и тот же ключ не дозапрашиваем: остаток не новости.</summary>
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, KeyBalance> _byFingerprint = new(StringComparer.Ordinal);

    /// <summary>Отпечатки ключей, чей провайдер ответил, что остатка не назовёт.</summary>
    private readonly HashSet<string> _silent = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Поднимается, когда хоть одна цифра изменилась. На него подписана плашка.</summary>
    public event Action? Changed;

    /// <summary>Запоминает остаток ключа. Пустую цифру не пишем: она стёрла бы известную.</summary>
    public void Remember(ApiCredential credential, VeniceBalance? balance)
    {
        if (balance is null || (balance.Usd is null && balance.Diem is null) || credential.IsEmpty)
        {
            return;
        }

        var fingerprint = ApiKeyStore.Fingerprint(credential.Secret);
        var entry = new KeyBalance(fingerprint, balance.Usd, balance.Diem, DateTime.UtcNow,
            credential.Provider == LlmProvider.Polza ? balance.RublesPerUsd ?? _rublesPerUsd() : null);

        bool changed;
        lock (_gate)
        {
            changed = !_byFingerprint.TryGetValue(fingerprint, out var known) ||
                      known.Usd != entry.Usd ||
                      known.Diem != entry.Diem ||
                      known.RublesPerUsd != entry.RublesPerUsd;
            _byFingerprint[fingerprint] = entry;
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Разбивка в том порядке, в каком ключи стоят в списке.</summary>
    public IReadOnlyList<BalanceRow> Breakdown(IReadOnlyList<ApiKeyEntry> keys)
    {
        var rows = new List<BalanceRow>();
        lock (_gate)
        {
            foreach (var key in keys)
            {
                if (!key.IsBroken &&
                    _byFingerprint.TryGetValue(ApiKeyStore.Fingerprint(key.Secret), out var known) &&
                    HasCurrentRate(key, known))
                {
                    rows.Add(new BalanceRow(key.Label, key.Provider, known.Usd, known.Diem, known.Fingerprint));
                }
            }
        }

        return rows;
    }

    /// <summary>
    /// Сумма по названным ключам. Считается по ним, а не по всей книге: удалённый ключ остаётся
    /// в ней до конца запуска, и его деньги завысили бы итог.
    /// </summary>
    public BalanceTotal Total(IReadOnlyList<ApiKeyEntry> keys)
    {
        decimal? usd = null;
        decimal? diem = null;
        var unknown = 0;
        var polzaSecret = _defaultCredential?.Invoke(LlmProvider.Polza).Secret;
        if (string.IsNullOrEmpty(polzaSecret) || !keys.Any(key => !key.IsBroken && key.Provider == LlmProvider.Polza && key.Secret == polzaSecret))
        {
            polzaSecret = keys.FirstOrDefault(key => !key.IsBroken && key.Provider == LlmProvider.Polza)?.Secret;
        }

        lock (_gate)
        {
            foreach (var key in keys)
            {
                if (key.IsBroken || !ReportsDollars(key) ||
                    (key.Provider == LlmProvider.Polza && key.Secret != polzaSecret))
                {
                    continue;
                }

                if (!_byFingerprint.TryGetValue(ApiKeyStore.Fingerprint(key.Secret), out var known) || !HasCurrentRate(key, known))
                {
                    unknown++;
                    continue;
                }

                if (known.Usd is { } money)
                {
                    usd = (usd ?? 0m) + money;
                }

                if (known.Diem is { } diemMoney)
                {
                    diem = (diem ?? 0m) + diemMoney;
                }
            }
        }

        return new BalanceTotal(usd, diem, unknown);
    }

    /// <summary>
    /// Ключи, чей остаток пора спросить у провайдера: неизвестен либо устарел.
    /// </summary>
    /// <remarks>
    /// Порог живёт здесь, а не у вызывающего: дозапрос идёт и после каждого хода, и при смене
    /// набора ключей, и без порога человек с тремя ключами платил бы тремя лишними запросами
    /// за каждое сообщение.
    /// </remarks>
    public IReadOnlyList<ApiKeyEntry> Stale(IReadOnlyList<ApiKeyEntry> keys)
    {
        var due = new List<ApiKeyEntry>();
        var now = DateTime.UtcNow;

        lock (_gate)
        {
            foreach (var key in keys)
            {
                if (key.IsBroken || !ReportsDollars(key))
                {
                    continue;
                }

                if (!_byFingerprint.TryGetValue(ApiKeyStore.Fingerprint(key.Secret), out var known) ||
                    !HasCurrentRate(key, known) || now - known.SeenUtc > Fresh)
                {
                    due.Add(key);
                }
            }
        }

        return due;
    }

    /// <summary>
    /// Провайдер этого ключа остатка не сообщает — проверено ответом. Спрашивать снова незачем,
    /// и в сумме такой ключ не числится «неизвестным».
    /// </summary>
    /// <remarks>
    /// Только в памяти: перепродавец мог за это время научиться, и следующий запуск спросит снова.
    /// </remarks>
    public void MarkSilent(ApiCredential credential)
    {
        if (credential.IsEmpty)
        {
            return;
        }

        bool added;
        lock (_gate)
        {
            added = _silent.Add(ApiKeyStore.Fingerprint(credential.Secret));
        }

        if (added)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Можно ли ждать от ключа остаток в долларах.
    /// </summary>
    /// <remarks>
    /// Нет — у провайдера без такого запроса (OpenAI, Anthropic и прочие), у ключа, на который
    /// провайдер уже ответил, что остатка не назовёт, и у ключа, выданного пакетом токенов: его
    /// расход виден в токенах, а «долларовые» поля перепродавцы таких ключей заполняют токенами.
    /// Без этого правила такие ключи спрашивались бы после каждого ответа и вечно числились
    /// «ещё N — цифры пока нет».
    /// </remarks>
    private bool ReportsDollars(ApiKeyEntry key) =>
        key.TokenLimit is null &&
        (key.Provider != LlmProvider.Polza || _rublesPerUsd() is > 0m) &&
        ProviderSpec.Of(key.Credential).Balance != BalanceSource.None &&
        !_silent.Contains(ApiKeyStore.Fingerprint(key.Secret));

    private bool HasCurrentRate(ApiKeyEntry key, KeyBalance known) =>
        key.Provider != LlmProvider.Polza ||
        (_rublesPerUsd() is > 0m and var rate && known.RublesPerUsd == rate);

    /// <summary>После изменения курса прежняя оценка остатка больше не годится.</summary>
    public void Invalidate(IReadOnlyList<ApiKeyEntry> keys, LlmProvider provider)
    {
        lock (_gate)
        {
            foreach (var key in keys.Where(key => key.Provider == provider))
            {
                var fingerprint = ApiKeyStore.Fingerprint(key.Secret);
                _byFingerprint.Remove(fingerprint);
                _silent.Remove(fingerprint);
            }
        }

        Changed?.Invoke();
    }

    /// <summary>Книга целиком — для сохранения между запусками.</summary>
    public IReadOnlyList<KeyBalance> Snapshot()
    {
        lock (_gate)
        {
            return [.. _byFingerprint.Values];
        }
    }

    /// <summary>Кладёт сохранённое между запусками.</summary>
    public void Restore(IReadOnlyList<KeyBalance> saved)
    {
        if (saved.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var row in saved)
            {
                if (!string.IsNullOrWhiteSpace(row.Fingerprint))
                {
                    _byFingerprint[row.Fingerprint] = row;
                }
            }
        }

        Changed?.Invoke();
    }
}
