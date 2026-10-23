namespace Amarin.Core;

/// <summary>
/// Встроенный браузер агента (1.37.0): окно WebView2 с отдельным профилем, управление через DOM.
/// </summary>
/// <remarks>
/// Порт, а не сам браузер: WebView2 живёт в приложении (ядро о WPF не знает), а правила — что
/// открывать, куда вводить — решают <see cref="BrowserSafety"/> и <see cref="Tools.BrowserTool"/>.
/// Реализация только честно отвечает и исполняет. Все методы ждут конца загрузки страницы, но не
/// дольше своего срока: зависшая страница — это ответ, а не повод держать прогон.
/// </remarks>
internal interface IWebBrowser
{
    /// <summary>Окно закрыл человек: в этом прогоне браузера больше нет.</summary>
    bool ClosedByHuman { get; }

    /// <summary>Открывает адрес (окно — при первом обращении) и читает страницу.</summary>
    Task<BrowserPage> OpenAsync(Uri url, CancellationToken cancellationToken);

    /// <summary>
    /// Текст страницы и её элементы с новыми id. Прежние id после этого не действуют.
    /// </summary>
    Task<BrowserPage> ReadAsync(int maxText, int maxElements, CancellationToken cancellationToken);

    /// <summary>Что за элемент под id последнего чтения; <c>null</c> — такого больше нет.</summary>
    Task<BrowserElement?> FindAsync(string id, CancellationToken cancellationToken);

    /// <summary>Настоящее нажатие мышью в середину элемента. False — элемента больше нет.</summary>
    Task<bool> ClickAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Фокус в поле и ввод текста, как с клавиатуры. <paramref name="replace"/> — сначала очистить.
    /// False — поля больше нет, и ничего не введено.
    /// </summary>
    Task<bool> TypeAsync(string id, string text, bool replace, CancellationToken cancellationToken);

    /// <summary>Выбор пункта списка по его значению или подписи. False — такого пункта нет.</summary>
    Task<bool> SelectAsync(string id, string option, CancellationToken cancellationToken);

    /// <summary>Клавиша там, где сейчас фокус: <see cref="BrowserSafety.Keys"/>.</summary>
    Task PressAsync(string key, CancellationToken cancellationToken);

    /// <summary>Прокрутка страницы на экран вверх или вниз.</summary>
    Task ScrollAsync(bool down, CancellationToken cancellationToken);

    /// <summary>Назад по истории. False — назад некуда.</summary>
    Task<bool> BackAsync(CancellationToken cancellationToken);

    /// <summary>Снимок видимой части страницы, JPEG.</summary>
    Task<byte[]?> ScreenshotAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Плашка в окне браузера с просьбой к человеку и кнопкой «Готово»: войти на сайт, пройти
    /// проверку. True — человек нажал «Готово», false — вышел срок.
    /// </summary>
    Task<bool> HandoffAsync(string message, TimeSpan limit, CancellationToken cancellationToken);

    /// <summary>
    /// Что случилось с прошлого вопроса и модель должна знать: скачанные и отклонённые файлы,
    /// переходы, которые окно не пустило.
    /// </summary>
    IReadOnlyList<string> TakeNotes();

    /// <summary>Закрывает окно. Входы на сайты остаются в профиле браузера.</summary>
    void Close();
}

/// <summary>Элемент страницы, который можно нажать, заполнить или выбрать.</summary>
/// <param name="Password">Поле пароля: туда агент не вводит ничего.</param>
internal sealed record BrowserElement(
    string Id,
    string Tag,
    string Role,
    string Name,
    string? Type,
    string? Value,
    string? Href,
    bool Password);

/// <summary>Прочитанная страница.</summary>
internal sealed record BrowserPage(
    string Url,
    string Title,
    string Text,
    IReadOnlyList<BrowserElement> Elements,
    bool TextTruncated,
    bool ElementsTruncated);
