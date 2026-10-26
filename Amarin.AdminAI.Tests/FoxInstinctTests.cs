using System.Globalization;
using Amarin.Fox;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Инстинкт Розы (1.35.0): мгновенная реакция по словам, без модели. Осторожен: лучше не заметить
/// тему, чем напугать Розу рекламой или назвать бредом команду.
/// </summary>
public sealed class FoxInstinctTests
{
    [Theory]
    [InlineData("Кажется, у меня на компьютере вирус, всё тормозит", "virus")]
    [InlineData("Is my PC infected with malware?", "virus")]
    [InlineData("Объясни квантовую запутанность простыми словами", "science")]
    [InlineData("Help me solve this integral", "science")]
    [InlineData("```csharp\nvar x = 1;\n```", "code")]
    [InlineData("Почему падает мой скрипт на PowerShell?", "code")]
    [InlineData("Меня обманули на сайте, украли деньги", "scam")]
    [InlineData("I got scammed yesterday", "scam")]
    [InlineData("Мне сегодня грустно и одиноко", "sad")]
    [InlineData("Роза, спасибо большое!", "thanks")]
    [InlineData("Ура, заработало!", "happy")]
    [InlineData("Роза, ты такая милая", "praise")]
    [InlineData("Ты такая милая, Роза!", "praise")]
    [InlineData("Роза, ты тупой", "rude-to-fox")]
    public void Topics_are_recognized(string message, string topic) =>
        Assert.Contains(topic, FoxInstinct.Read(message).Topics);

    /// <summary>
    /// Сообщения ассистенту — не Розе: благодарность, похвала и грубость в них на его счёт не идут,
    /// даже если в тексте есть слово из его имени.
    /// </summary>
    [Theory]
    [InlineData("Спасибо большое!")]
    [InlineData("Спасибо, ты молодец, очень мило")]
    [InlineData("Напиши стих про розу, ты умница")]
    [InlineData("Роза Люксембург была хорошим оратором?")]
    [InlineData("Где растут лисички, ты же умный?")]
    [InlineData("Ты тупой, даже про лису ответить не можешь")]
    public void Words_to_the_assistant_are_not_taken_personally(string message)
    {
        var topics = FoxInstinct.Read(message).Topics;
        Assert.DoesNotContain(FoxTopic.Thanks, topics);
        Assert.DoesNotContain(FoxTopic.Praise, topics);
        Assert.DoesNotContain(FoxTopic.RudeToFox, topics);
        Assert.False(FoxInstinct.Addressed(message));
    }

    [Theory]
    [InlineData("Роза, привет")]
    [InlineData("привет, Роза")]
    [InlineData("Ну что, лисёнок?")]
    [InlineData("Rosa!")]
    public void Calling_him_by_name_addresses_him(string message) =>
        Assert.True(FoxInstinct.Addressed(message));

    /// <summary>«Вирус» не про компьютер: реклама, популярность, болезни.</summary>
    [Theory]
    [InlineData("Как сделать вирусную рекламу для ТикТока?")]
    [InlineData("У меня грипп, какой это вирус?")]
    [InlineData("Расскажи, как вирус размножается в клетке организма")]
    [InlineData("How do I make a viral video?")]
    public void Viruses_outside_the_computer_do_not_scare_rosa(string message) =>
        Assert.DoesNotContain(FoxTopic.Virus, FoxInstinct.Read(message).Topics);

    [Theory]
    [InlineData("фывапролдж")]
    [InlineData("asdfghjkl")]
    [InlineData("аааааааааа")]
    [InlineData("кщшгнщцзх рлщзшгнт")]
    public void Keyboard_mash_is_nonsense(string message)
    {
        var reading = FoxInstinct.Read(message);
        Assert.Contains(FoxTopic.Nonsense, reading.Topics);
        Assert.False(reading.WrongLayout);
    }

    /// <summary>Русское, набранное в английской раскладке, — бред особого рода: Роза так и скажет.</summary>
    [Theory]
    [InlineData("ghbdtn")]
    [InlineData("ghbdtn rfr ltkf")]
    [InlineData("руддщ")]
    public void Text_in_the_wrong_layout_is_named_so(string message)
    {
        var reading = FoxInstinct.Read(message);
        Assert.Contains(FoxTopic.Nonsense, reading.Topics);
        Assert.True(reading.WrongLayout);
    }

    /// <summary>Пути, команды, код, короткие ответы и обычная речь на любом языке — не бред.</summary>
    [Theory]
    [InlineData("ок")]
    [InlineData("ага")]
    [InlineData("C:\\Windows\\System32\\drivers")]
    [InlineData("/usr/local/bin")]
    [InlineData("Get-ChildItem -Recurse -Force")]
    [InlineData("lsblk")]
    [InlineData("ipconfig /all")]
    [InlineData("https://example.com/path")]
    [InlineData("12345 67890")]
    [InlineData("привет, как дела?")]
    [InlineData("Hello, how are you today?")]
    [InlineData("Guten Morgen, wie geht es dir?")]
    [InlineData("你好世界你好世界你好世界")]
    [InlineData("var x = Foo(); return x;")]
    public void Ordinary_input_is_not_nonsense(string message) =>
        Assert.DoesNotContain(FoxTopic.Nonsense, FoxInstinct.Read(message).Topics);

    [Fact]
    public void A_clean_scan_reply_brings_relief()
    {
        Assert.True(FoxInstinct.SoundsClean("Проверил: угроз не обнаружено, всё в порядке."));
        Assert.True(FoxInstinct.SoundsClean("Scan finished, no threats found."));
        Assert.False(FoxInstinct.SoundsClean("Нашёл троян в автозагрузке."));
    }

    /// <summary>
    /// Раннер CI — английская Windows, а у турецкой культуры своё «i»: разбор не должен зависеть ни
    /// от той, ни от другой.
    /// </summary>
    [Theory]
    [InlineData("en-US")]
    [InlineData("tr-TR")]
    [InlineData("ru-RU")]
    public void Reading_does_not_depend_on_the_machine_culture(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            Assert.Contains(FoxTopic.Virus, FoxInstinct.Read("MALWARE INFECTION ON MY PC").Topics);
            Assert.Contains(FoxTopic.Science, FoxInstinct.Read("PHYSICS IS FUN").Topics);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void A_long_message_is_noticed() =>
        Assert.Contains(FoxTopic.Long, FoxInstinct.Read(new string('а', 10) + " " + string.Join(' ', Enumerable.Repeat("слово", 120))).Topics);
}
