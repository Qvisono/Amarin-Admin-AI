using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Остатки нескольких ключей и сумма по ним.
/// </summary>
/// <remarks>
/// До версии 1.23.0 платил один ключ, и остаток был один: Venice называл его на заголовках
/// ответа. Теперь платят несколько, и одно число перестало отвечать на вопрос «сколько
/// у меня осталось» — а у ключа OpenRouter такого числа не было вовсе, заголовков он не шлёт.
/// </remarks>
public sealed class BalanceBookTests
{
    private const string VeniceSecret = "vk-first-secret";
    private const string SecondSecret = "vk-second-secret";
    private const string RouterSecret = "sk-or-v1-secret";

    private static ApiKeyEntry Key(string id, string label, string secret, LlmProvider provider) =>
        new(id, label, secret, ApiKeySource.Stored, false, provider);

    private static IReadOnlyList<ApiKeyEntry> Keys() =>
    [
        Key("v1", "Основной", VeniceSecret, LlmProvider.Venice),
        Key("v2", "Запасной", SecondSecret, LlmProvider.Venice),
        Key("o1", "router", RouterSecret, LlmProvider.OpenRouter)
    ];

    private static VeniceBalance Usd(decimal amount) => new() { CanConsume = true, Usd = amount };

    [Fact]
    public void The_total_adds_every_key_up()
    {
        var book = new BalanceBook();
        book.Remember(new ApiCredential(LlmProvider.Venice, VeniceSecret), Usd(4.86m));
        book.Remember(new ApiCredential(LlmProvider.Venice, SecondSecret), Usd(1.14m));
        book.Remember(new ApiCredential(LlmProvider.OpenRouter, RouterSecret), Usd(4.00m));

        var total = book.Total(Keys());

        Assert.Equal(10.00m, total.Usd);
        Assert.Equal(0, total.Unknown);
    }

    /// <summary>
    /// Пока не все ключи ответили, сумма занижена — и плашка обязана об этом сказать, иначе
    /// человек прочитает её как «денег меньше, чем есть».
    /// </summary>
    [Fact]
    public void A_key_that_has_not_answered_is_counted_as_unknown()
    {
        var book = new BalanceBook();
        book.Remember(new ApiCredential(LlmProvider.Venice, VeniceSecret), Usd(4.86m));

        var total = book.Total(Keys());

        Assert.Equal(4.86m, total.Usd);
        Assert.Equal(2, total.Unknown);
    }

    /// <summary>
    /// Удалённый ключ остаётся в книге до конца запуска, но в сумму не идёт: она считается
    /// по живому списку, иначе его деньги завысили бы итог.
    /// </summary>
    [Fact]
    public void A_removed_key_leaves_the_total()
    {
        var book = new BalanceBook();
        book.Remember(new ApiCredential(LlmProvider.Venice, VeniceSecret), Usd(4.86m));
        book.Remember(new ApiCredential(LlmProvider.Venice, SecondSecret), Usd(5.14m));

        var total = book.Total([Key("v1", "Основной", VeniceSecret, LlmProvider.Venice)]);

        Assert.Equal(4.86m, total.Usd);
        Assert.Equal(0, total.Unknown);
    }

    /// <summary>Нерасшифрованный ключ не в счёт: платить им всё равно нечем.</summary>
    [Fact]
    public void A_broken_key_is_not_counted()
    {
        var book = new BalanceBook();
        var broken = new ApiKeyEntry("v3", "Сломанный", null, ApiKeySource.Stored, false, LlmProvider.Venice);

        var total = book.Total([broken]);

        Assert.Null(total.Usd);
        Assert.Equal(0, total.Unknown);
    }

    /// <summary>
    /// Пустая цифра не пишется: ответ без заголовков стёр бы известный остаток, и плашка
    /// мигала бы прочерком на каждом таком ответе.
    /// </summary>
    [Fact]
    public void An_empty_figure_does_not_erase_a_known_one()
    {
        var book = new BalanceBook();
        var credential = new ApiCredential(LlmProvider.Venice, VeniceSecret);
        book.Remember(credential, Usd(4.86m));
        book.Remember(credential, new VeniceBalance { CanConsume = true });
        book.Remember(credential, null);

        Assert.Equal(4.86m, book.Total(Keys()).Usd);
    }

    /// <summary>Разбивка берёт имена из живого списка: ключ могли переименовать.</summary>
    [Fact]
    public void The_breakdown_names_keys_from_the_live_list()
    {
        var book = new BalanceBook();
        book.Remember(new ApiCredential(LlmProvider.Venice, VeniceSecret), Usd(4.86m));
        book.Remember(new ApiCredential(LlmProvider.OpenRouter, RouterSecret), Usd(4.00m));

        var rows = book.Breakdown(Keys());

        Assert.Equal(2, rows.Count);
        Assert.Equal("Основной", rows[0].Label);
        Assert.Equal("router", rows[1].Label);
        Assert.Equal(LlmProvider.OpenRouter, rows[1].Provider);
    }

    /// <summary>
    /// Дозапрашивать нужно тех, кто молчал: у OpenRouter это единственный способ узнать
    /// остаток вообще.
    /// </summary>
    [Fact]
    public void Only_unknown_keys_are_due_for_a_request()
    {
        var book = new BalanceBook();
        book.Remember(new ApiCredential(LlmProvider.Venice, VeniceSecret), Usd(4.86m));

        var due = book.Stale(Keys());

        Assert.Equal(2, due.Count);
        Assert.DoesNotContain(due, key => key.Id == "v1");
    }

    /// <summary>
    /// Свежую цифру повторно не спрашиваем: иначе человек с тремя ключами платил бы тремя
    /// лишними запросами за каждое сообщение.
    /// </summary>
    [Fact]
    public void A_fresh_figure_is_not_asked_again()
    {
        var book = new BalanceBook();
        foreach (var key in Keys())
        {
            book.Remember(key.Credential, Usd(1m));
        }

        Assert.Empty(book.Stale(Keys()));
    }

    /// <summary>Сохранённое между запусками возвращается на место по отпечатку.</summary>
    [Fact]
    public void A_restored_book_answers_for_the_same_keys()
    {
        var book = new BalanceBook();
        book.Remember(new ApiCredential(LlmProvider.Venice, VeniceSecret), Usd(4.86m));

        var restored = new BalanceBook();
        restored.Restore(book.Snapshot());

        Assert.Equal(4.86m, restored.Total(Keys()).Usd);
    }

    /// <summary>Изменившаяся цифра будит подписчиков — на событии висит перерисовка плашки.</summary>
    [Fact]
    public void Only_a_changed_figure_wakes_the_plate()
    {
        var book = new BalanceBook();
        var credential = new ApiCredential(LlmProvider.Venice, VeniceSecret);
        var seen = 0;
        book.Changed += () => seen++;

        book.Remember(credential, Usd(4.86m));
        book.Remember(credential, Usd(4.86m));
        book.Remember(credential, Usd(3.00m));

        Assert.Equal(2, seen);
    }
}
