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
public sealed class VeniceKeyStoreTests : IDisposable
{
    private const string KeyOne = "vk-first-key-0123456789";
    private const string KeyTwo = "vk-second-key-9876543210";
    private const string FromEnvironment = "vk-environment-key-42424242";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "amarin-keys-" + Guid.NewGuid().ToString("N"));

    public VeniceKeyStoreTests() => Directory.CreateDirectory(_root);

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

    private VeniceKeyStore Store(string environmentKey = FromEnvironment)
    {
        var store = new VeniceKeyStore(_root, environmentKey);
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

        var entry = Assert.Single(Store().List(), item => item.Source == VeniceKeySource.Stored);
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

        Assert.Equal(VeniceKeySource.Environment, entry.Source);
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

    [Fact]
    public void The_environment_key_cannot_be_removed()
    {
        var store = Store();

        Assert.False(store.Remove(VeniceKeyStore.EnvironmentId));
        Assert.Contains(store.List(), entry => entry.Source == VeniceKeySource.Environment);
        Assert.False(Assert.Single(store.List()).CanRemove);
    }

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
        Assert.Single(store.List(), entry => entry.Source == VeniceKeySource.Stored);
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

        var other = new VeniceKeyStore(second, FromEnvironment);
        other.Load();

        Assert.DoesNotContain(other.List(), entry => entry.Source == VeniceKeySource.Stored);
        Assert.Equal(FromEnvironment, other.ActiveSecret());
    }

    /// <summary>Длина ключа — тоже сведение о секрете: маска её не выдаёт.</summary>
    [Fact]
    public void The_mask_shows_the_ends_but_not_the_length()
    {
        var shortKey = VeniceKeyStore.Mask("vk-abcdefgh");
        var longKey = VeniceKeyStore.Mask("vk-" + new string('x', 200) + "tail");

        Assert.StartsWith("vk-", shortKey, StringComparison.Ordinal);
        Assert.EndsWith("efgh", shortKey, StringComparison.Ordinal);
        Assert.EndsWith("tail", longKey, StringComparison.Ordinal);
        Assert.Equal(shortKey.Length, longKey.Length);
    }

    [Fact]
    public void A_tiny_key_shows_nothing_at_all() => Assert.Equal("•••••••••", VeniceKeyStore.Mask("vk-12"));

    [Fact]
    public void A_missing_key_masks_to_a_dash() => Assert.Equal("—", VeniceKeyStore.Mask(null));

    [Fact]
    public void The_fingerprint_is_stable_and_is_not_the_key()
    {
        var fingerprint = VeniceKeyStore.Fingerprint(KeyOne);

        Assert.Equal(fingerprint, VeniceKeyStore.Fingerprint(KeyOne));
        Assert.NotEqual(fingerprint, VeniceKeyStore.Fingerprint(KeyTwo));
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
