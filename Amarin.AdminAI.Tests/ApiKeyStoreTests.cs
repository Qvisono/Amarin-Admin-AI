using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Хранилище ключей Venice: несколько на выбор, зашифрованные на диске.
/// </summary>
/// <remarks>
/// До версии 1.22.0 ключ был один и брался только из переменной окружения — хранить его
/// программа отказывалась намеренно. Теперь она его хранит, и цена ошибки соответствующая:
/// ключ в открытом виде в файле, ключ в имени файла, ключ, уехавший в архив данных.
/// </remarks>
public sealed class ApiKeyStoreTests : IDisposable
{
    private const string KeyOne = "vk-first-key-0123456789";
    private const string KeyTwo = "vk-second-key-9876543210";
    private const string FromEnvironment = "vk-environment-key-42424242";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "amarin-keys-" + Guid.NewGuid().ToString("N"));

    public ApiKeyStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string File_ => Path.Combine(_root, "keys.json");

    private ApiKeyStore Store(string environmentKey = FromEnvironment)
    {
        var store = new ApiKeyStore(_root, environmentKey);
        store.Load();
        return store;
    }

    [Fact]
    public void A_stored_key_reads_back_after_a_restart()
    {
        Store().Add("Рабочий", KeyOne);

        Assert.Equal(KeyOne, Store().ActiveSecret());
    }

    /// <summary>Самое главное: открытым текстом ключ на диск попасть не должен.</summary>
    [Fact]
    public void The_file_never_holds_the_key_in_the_clear()
    {
        Store().Add("Рабочий", KeyOne);

        var text = System.IO.File.ReadAllText(File_);
        Assert.DoesNotContain(KeyOne, text, StringComparison.Ordinal);
        Assert.Contains("protected", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Блоб, зашифрованный другой учётной записью Windows. Строка остаётся в списке с пометкой,
    /// а не исчезает молча: пропавший ключ человек объяснить себе не сможет.
    /// </summary>
    [Fact]
    public void An_unreadable_blob_keeps_its_row_and_does_not_break_the_rest()
    {
        Store().Add("Рабочий", KeyOne);

        var damaged = System.IO.File.ReadAllText(File_)
            .Replace("\"protected\":", "\"protected\": \"bm90IGEgZHBhcGkgYmxvYg==\", \"ignored\":",
                     StringComparison.Ordinal);
        System.IO.File.WriteAllText(File_, damaged);

        var entry = Assert.Single(Store().List(), item => item.Source == ApiKeySource.Stored);
        Assert.True(entry.IsBroken);
        Assert.Equal("—", entry.Masked);

        // И программа не остаётся без ключа вовсе: работает тот, что в окружении.
        Assert.Equal(FromEnvironment, Store().ActiveSecret());
    }

    [Fact]
    public void A_damaged_file_is_an_empty_list()
    {
        System.IO.File.WriteAllText(File_, "{ это не json");

        var store = Store();
        Assert.Single(store.List());
        Assert.Equal(FromEnvironment, store.ActiveSecret());
    }

    /// <summary>Без своих ключей работает переменная окружения — как и до этой версии.</summary>
    [Fact]
    public void Without_stored_keys_the_environment_one_is_active()
    {
        var entry = Assert.Single(Store().List());

        Assert.Equal(ApiKeySource.Environment, entry.Source);
        Assert.True(entry.IsActive);
        Assert.Equal(FromEnvironment, Store().ActiveSecret());
    }

    /// <summary>
    /// Ключ из окружения человек сознательно держал снаружи программы. Копировать его на диск
    /// за него никто не вправе.
    /// </summary>
    [Fact]
    public void The_environment_key_is_never_written_to_the_file()
    {
        Store().Add("Рабочий", KeyOne);

        Assert.DoesNotContain(FromEnvironment, System.IO.File.ReadAllText(File_), StringComparison.Ordinal);
    }

    /// <summary>
    /// Ключ из окружения человек тоже вправе убрать: до версии, добавившей это, строка стояла
    /// в списке насмерть, и выкинуть её можно было только правкой переменных среды Windows.
    /// </summary>
    /// <remarks>
    /// «Убрать» здесь — не «удалить»: переменная принадлежит Windows, и лезть в неё программа
    /// не вправе. Она перестаёт брать этот ключ, и это всё, что она может обещать.
    /// </remarks>
    [Fact]
    public void The_environment_key_can_be_taken_out_of_the_list()
    {
        var store = Store();

        Assert.True(store.Remove(ApiKeyStore.EnvironmentId));
        Assert.Empty(store.List());

        // Главное: убранным ключом больше не платят. Иначе «удалил» значило бы «спрятал», и
        // деньги продолжали бы уходить с того ключа, которого человек в списке уже не видит.
        Assert.Equal("", store.ActiveSecret());
        Assert.Equal("", store.VeniceCredential().Secret);
    }

    [Fact]
    public void A_key_taken_out_stays_out_after_a_restart()
    {
        Store().Remove(ApiKeyStore.EnvironmentId);

        Assert.Empty(Store().List());

        // И секрет при этом на диск не попал: в файле лежит только отпечаток.
        Assert.DoesNotContain(FromEnvironment, System.IO.File.ReadAllText(File_), StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_taken_out_comes_back()
    {
        var store = Store();
        store.Remove(ApiKeyStore.EnvironmentId);

        var hidden = Assert.Single(store.HiddenEnvironment());
        Assert.Equal("VENICE_API_KEY", hidden.Label);

        Assert.True(store.Restore(hidden.Id));
        Assert.Equal(FromEnvironment, Store().ActiveSecret());
        Assert.Empty(Store().HiddenEnvironment());
    }

    /// <summary>
    /// Запрет записан по значению ключа, а не по строке списка.
    /// </summary>
    /// <remarks>
    /// Иначе человек, сменивший значение переменной, не увидел бы нового ключа вовсе: строка
    /// та же самая, и запрет на ней остался бы висеть. Объяснить себе такое молчание нельзя.
    /// </remarks>
    [Fact]
    public void Another_value_in_the_variable_is_another_key()
    {
        Store().Remove(ApiKeyStore.EnvironmentId);

        const string Rotated = "vk-environment-key-98765432";
        var entry = Assert.Single(Store(Rotated).List());
        Assert.Equal(Rotated, entry.Secret);
    }

    /// <summary>
    /// Вставить убранный ключ заново нельзя: программа положила бы копию переменной окружения
    /// себе на диск, чего она не делает никогда. Возвращают его <see cref="ApiKeyStore.Restore"/>.
    /// </summary>
    [Fact]
    public void A_key_taken_out_is_never_copied_to_the_file()
    {
        var store = Store();
        store.Remove(ApiKeyStore.EnvironmentId);

        Assert.True(store.IsRemovedEnvironmentKey(FromEnvironment));
        Assert.True(store.Contains(FromEnvironment));
        Assert.Null(store.Add("Обходной путь", FromEnvironment));
        Assert.DoesNotContain(FromEnvironment, System.IO.File.ReadAllText(File_), StringComparison.Ordinal);
    }

    /// <summary>Выбор, указывавший на убранную строку, снимается — иначе он указывал бы в пустоту.</summary>
    [Fact]
    public void Taking_out_the_chosen_key_hands_the_choice_to_another()
    {
        var store = Store();
        store.Add("Рабочий", KeyOne);
        store.SetActive(ApiKeyStore.EnvironmentId);

        store.Remove(ApiKeyStore.EnvironmentId);

        Assert.Equal(KeyOne, store.ActiveSecret());
        Assert.Equal(KeyOne, Store().ActiveSecret());
    }

    // ───────────────────────── названия ─────────────────────────

    /// <summary>
    /// Маска у всех ключей одинаковая, и между двумя рабочими человек выбирает по названию.
    /// </summary>
    [Fact]
    public void A_key_can_be_renamed_and_keeps_the_name_after_a_restart()
    {
        var store = Store();
        var added = store.Add("Рабочий", KeyOne)!;

        Assert.True(store.Rename(added.Id, "  Домашний  "));
        Assert.Equal("Домашний", Single(Store().List(), ApiKeySource.Stored).Label);
    }

    /// <summary>
    /// Строке окружения название хранится отдельно: сама переменная — не наша, и переписывать
    /// её ради подписи в списке программа не станет.
    /// </summary>
    [Fact]
    public void The_environment_row_can_be_renamed_too()
    {
        var store = Store();

        Assert.True(store.Rename(ApiKeyStore.EnvironmentId, "Рабочая переменная"));
        Assert.Equal("Рабочая переменная", Assert.Single(Store().List()).Label);
        Assert.Equal(ApiKeySource.Environment, Assert.Single(Store().List()).Source);
    }

    /// <summary>
    /// Название по месту, а не по значению: после смены значения переменной оно остаётся —
    /// «рабочая переменная» человек сказал про место, а не про конкретный ключ.
    /// </summary>
    [Fact]
    public void The_name_of_the_environment_row_survives_a_new_value()
    {
        Store().Rename(ApiKeyStore.EnvironmentId, "Рабочая переменная");

        Assert.Equal("Рабочая переменная", Assert.Single(Store("vk-rotated-0987654321").List()).Label);
    }

    [Fact]
    public void An_empty_name_goes_back_to_the_default()
    {
        var store = Store();
        var added = store.Add("Рабочий", KeyOne)!;

        store.Rename(ApiKeyStore.EnvironmentId, "Своё");
        store.Rename(added.Id, "Своё");
        store.Rename(ApiKeyStore.EnvironmentId, "   ");
        store.Rename(added.Id, "");

        var rows = Store().List();
        Assert.Equal("VENICE_API_KEY", Single(rows, ApiKeySource.Environment).Label);

        // У своего ключа умолчание — маска, как и у только что добавленного: строка совсем без
        // подписи не читается.
        Assert.Equal(ApiKeyStore.Mask(KeyOne), Single(rows, ApiKeySource.Stored).Label);
    }

    [Fact]
    public void Renaming_a_row_that_is_not_there_changes_nothing()
    {
        var store = Store();

        Assert.False(store.Rename("нет такого", "Название"));
        Assert.False(store.Rename(ApiKeyStore.OpenRouterEnvironmentId, "Название"));
    }

    private static ApiKeyEntry Single(IEnumerable<ApiKeyEntry> entries, ApiKeySource source) =>
        Assert.Single(entries, entry => entry.Source == source);

    [Fact]
    public void A_new_key_becomes_the_active_one()
    {
        var store = Store();
        store.Add("Первый", KeyOne);
        store.Add("Второй", KeyTwo);

        Assert.Equal(KeyTwo, store.ActiveSecret());
    }

    [Fact]
    public void The_choice_of_active_key_survives_a_restart()
    {
        var store = Store();
        var first = store.Add("Первый", KeyOne)!;
        store.Add("Второй", KeyTwo);

        store.SetActive(first.Id);

        Assert.Equal(KeyOne, Store().ActiveSecret());
    }

    /// <summary>
    /// Ключ могли удалить в другом запуске программы: записанный выбор указывает в пустоту.
    /// </summary>
    [Fact]
    public void A_choice_pointing_at_nothing_falls_back_to_a_working_key()
    {
        var store = Store();
        var first = store.Add("Первый", KeyOne)!;
        store.Add("Второй", KeyTwo);
        store.SetActive(first.Id);
        store.Remove(first.Id);

        Assert.Equal(KeyTwo, Store().ActiveSecret());
    }

    [Fact]
    public void The_same_key_is_not_added_twice()
    {
        var store = Store();
        store.Add("Первый", KeyOne);

        Assert.Null(store.Add("Тот же самый", KeyOne));
        Assert.Single(store.List(), entry => entry.Source == ApiKeySource.Stored);
    }

    [Fact]
    public void The_environment_key_is_not_added_again_as_a_stored_one() =>
        Assert.Null(Store().Add("Дубль", FromEnvironment));

    [Fact]
    public void An_empty_key_is_refused() => Assert.Null(Store().Add("Пусто", "   "));

    /// <summary>
    /// В отчёт об аварии попадает тот ключ, которым отправляли запрос, а он не обязан быть тем,
    /// что активен сейчас. Вырезать надо каждый.
    /// </summary>
    [Fact]
    public void Every_key_is_offered_for_scrubbing()
    {
        var store = Store();
        store.Add("Первый", KeyOne);
        store.Add("Второй", KeyTwo);

        var secrets = store.AllSecrets();
        Assert.Contains(FromEnvironment, secrets);
        Assert.Contains(KeyOne, secrets);
        Assert.Contains(KeyTwo, secrets);
    }

    [Fact]
    public void Two_profiles_keep_their_own_keys()
    {
        var second = Path.Combine(_root, "profiles", "other");
        Directory.CreateDirectory(second);
        Store().Add("Первый", KeyOne);

        var other = new ApiKeyStore(second, FromEnvironment);
        other.Load();

        Assert.DoesNotContain(other.List(), entry => entry.Source == ApiKeySource.Stored);
        Assert.Equal(FromEnvironment, other.ActiveSecret());
    }

    /// <summary>Длина ключа — тоже сведение о секрете: маска её не выдаёт.</summary>
    [Fact]
    public void The_mask_shows_the_ends_but_not_the_length()
    {
        var shortKey = ApiKeyStore.Mask("vk-abcdefgh");
        var longKey = ApiKeyStore.Mask("vk-" + new string('x', 200) + "tail");

        Assert.StartsWith("vk-", shortKey, StringComparison.Ordinal);
        Assert.EndsWith("efgh", shortKey, StringComparison.Ordinal);
        Assert.EndsWith("tail", longKey, StringComparison.Ordinal);
        Assert.Equal(shortKey.Length, longKey.Length);
    }

    [Fact]
    public void A_tiny_key_shows_nothing_at_all() => Assert.Equal("•••••••••", ApiKeyStore.Mask("vk-12"));

    [Fact]
    public void A_missing_key_masks_to_a_dash() => Assert.Equal("—", ApiKeyStore.Mask(null));

    [Fact]
    public void The_fingerprint_is_stable_and_is_not_the_key()
    {
        var fingerprint = ApiKeyStore.Fingerprint(KeyOne);

        Assert.Equal(fingerprint, ApiKeyStore.Fingerprint(KeyOne));
        Assert.NotEqual(fingerprint, ApiKeyStore.Fingerprint(KeyTwo));
        Assert.DoesNotContain(fingerprint, KeyOne, StringComparison.Ordinal);
        Assert.Equal(16, fingerprint.Length);
    }

    /// <summary>
    /// Круг шифрования средствами Windows. Он же дозор за упаковкой в один файл: соберись
    /// релиз без нужной сборки — этот тест упадёт первым.
    /// </summary>
    [Fact]
    public void Windows_encryption_round_trips()
    {
        var blob = DataProtector.Protect(KeyOne);

        Assert.NotNull(blob);
        Assert.DoesNotContain(KeyOne, blob, StringComparison.Ordinal);
        Assert.Equal(KeyOne, DataProtector.Unprotect(blob));
    }

    [Fact]
    public void A_foreign_blob_unprotects_to_nothing()
    {
        Assert.Null(DataProtector.Unprotect("bm90IGEgZHBhcGkgYmxvYg=="));
        Assert.Null(DataProtector.Unprotect("не base64 вовсе"));
        Assert.Null(DataProtector.Unprotect(null));
    }
}

/// <summary>Ключи и журнал трат не должны уезжать в архив данных, который человек пересылает.</summary>
public sealed class KeyExportSafetyTests
{
    [Theory]
    [InlineData("keys.json")]
    [InlineData("profiles/abc/keys.json")]
    [InlineData("usage/a1b2c3d4e5f60718.json")]
    [InlineData("profiles/abc/usage/a1b2c3d4e5f60718.json")]
    public void Secrets_are_left_out_of_the_archive(string relative) =>
        Assert.Equal(DataCategory.None, DataBundle.CategoryOf(relative));

    /// <summary>И это не «всё подряд None»: обычные файлы в архив по-прежнему попадают.</summary>
    [Fact]
    public void Ordinary_files_still_travel() =>
        Assert.Equal(DataCategory.Settings, DataBundle.CategoryOf("settings.json"));
}
