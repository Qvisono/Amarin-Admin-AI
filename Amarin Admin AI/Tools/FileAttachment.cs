namespace Amarin.Tools;

/// <summary>
/// Документ, прикреплённый к сообщению: PDF, таблица, текст, исходник.
/// </summary>
/// <remarks>
/// Venice принимает такие вложения частью содержимого сообщения (<c>type: "file"</c>) и сам
/// извлекает из них текст на своей стороне — читать PDF в программе не нужно. Содержимое
/// хранится base64 прямо в сообщении, как и у картинок: так вложение переживает перезапуск и
/// уезжает вместе с экспортом или ссылкой на чат.
/// </remarks>
/// <param name="Base64">Содержимое файла.</param>
/// <param name="MimeType">MIME по расширению — Venice выбирает по нему разборщик.</param>
/// <param name="FileName">Имя с расширением; показывается в карточке и уходит в запрос.</param>
/// <param name="SizeBytes">Размер исходного файла, до кодирования.</param>
public sealed record FileAttachment(string Base64, string MimeType, string FileName, long SizeBytes);
