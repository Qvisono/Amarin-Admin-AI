using System.Text.Json;
using System.Text.Json.Serialization;

namespace Amarin.Core;

/// <summary>
/// Тема в <c>settings.json</c>: старые — именем, как писал <c>JsonStringEnumConverter</c>, новые —
/// числом. Чтение терпимо: незнакомое имя — тёмная тема, а не исключение.
/// </summary>
/// <remarks>
/// <para>
/// Ради отката (1.36.0). Версия 1.35 и раньше читают файл <c>JsonStringEnumConverter</c>'ом, и
/// незнакомое имя темы у них бросает — а <c>AppSettingsStore.Load</c> на любой ошибке отдаёт
/// заводские настройки. Выбери человек Onyx и нажми «Вернуть v1.35», он потерял бы все настройки
/// разом, а следующее сохранение затёрло бы и файл. Число же тот конвертер читает в любое значение
/// перечисления без вопросов: прежняя версия рисует тёмную или светлую тему по системе и пишет
/// число обратно, так что после возврата на новую версию выбор на месте.
/// </para>
/// <para>
/// Отсюда и правило перечисления <see cref="AppTheme"/>: только дописывается в конец, а граница
/// «известно старым версиям» — <see cref="LastNamed"/>. Тема новее следующей версии, прочитанная
/// этой, становится тёмной — без сброса остальных настроек.
/// </para>
/// </remarks>
public sealed class AppThemeJsonConverter : JsonConverter<AppTheme>
{
    /// <summary>Последняя тема, которую знают версии до 1.36.0: её и всё раньше пишем именем.</summary>
    internal const AppTheme LastNamed = AppTheme.Almond;

    public override AppTheme Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Number when reader.TryGetInt32(out var number) => (AppTheme)number,
            JsonTokenType.String when Enum.TryParse<AppTheme>(reader.GetString(), ignoreCase: true, out var theme) => theme,
            _ => AppTheme.Dark
        };

    public override void Write(Utf8JsonWriter writer, AppTheme value, JsonSerializerOptions options)
    {
        if (value <= LastNamed && Enum.IsDefined(value))
        {
            writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
        }
        else
        {
            writer.WriteNumberValue((int)value);
        }
    }
}
