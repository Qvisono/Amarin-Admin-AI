namespace Amarin.Core;

/// <summary>Чем кончилась отправка ключа в другой профиль.</summary>
internal enum KeyTransferResult
{
    /// <summary>Ключ лёг в профиль.</summary>
    Added,

    /// <summary>Такой ключ там уже есть (свой, из окружения или убранный ключ окружения) — ничего не тронуто.</summary>
    AlreadyThere,

    /// <summary>Ключ не расшифровался здесь — отправлять нечего.</summary>
    Unreadable,

    /// <summary>Профиля больше нет (удалили, пока было открыто меню).</summary>
    NoProfile,

    /// <summary>Windows отказалась шифровать или диск не записал.</summary>
    Failed
}

/// <summary>
/// «Отправить» в меню ключа (1.32.0): ключ этого профиля ложится в другой профиль программы.
/// </summary>
/// <remarks>
/// <para>
/// Пароль профиля не спрашивается: он закрывает только вход в программу, а <c>keys.json</c> всех
/// профилей зашифрован DPAPI одного пользователя Windows. Чужой профиль сейчас не открыт, поэтому
/// в его папку пишет своё, отдельное хранилище; открытое держит файл в памяти, и два хранилища на
/// одном пути затёрли бы друг другу правки — отсюда и запрет отправлять в свой же профиль у вызывающего.
/// </para>
/// <para>
/// Выбор ключа в чужом профиле — не дело отправителя: <see cref="ApiKeyStore.Add"/> делает новый
/// ключ активным, и прежний активный возвращается на место. Профиль без ключей получает
/// присланный активным — иначе им нечем было бы платить.
/// </para>
/// </remarks>
internal static class ApiKeyTransfer
{
    /// <param name="targetRoot">Папка данных профиля-получателя (<see cref="ProfileStore.DataRootFor"/>).</param>
    /// <param name="environmentKey">Ключ Venice из окружения: он и так есть в каждом профиле.</param>
    /// <param name="openRouterEnvironmentKey">То же для OpenRouter.</param>
    public static KeyTransferResult Send(ApiKeyEntry entry, string targetRoot, string environmentKey, string openRouterEnvironmentKey)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Secret is not { Length: > 0 } secret)
        {
            return KeyTransferResult.Unreadable;
        }

        if (!Directory.Exists(targetRoot))
        {
            return KeyTransferResult.NoProfile;
        }

        var target = new ApiKeyStore(targetRoot, environmentKey, openRouterEnvironmentKey);
        target.Load();
        if (target.Contains(secret))
        {
            return KeyTransferResult.AlreadyThere;
        }

        var active = target.List().FirstOrDefault(key => key.IsActive)?.Id;
        if (target.Add(entry.Label, secret, entry.Provider) is null)
        {
            return KeyTransferResult.Failed;
        }

        if (active is not null)
        {
            _ = target.SetActive(active);
        }

        return KeyTransferResult.Added;
    }
}
