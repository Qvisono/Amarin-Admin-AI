using System.Windows;

namespace Amarin.UI;

/// <summary>
/// Словарь стилей, который разбирается один раз на поток и подключается всем, кто его просит.
/// </summary>
/// <remarks>
/// <para>
/// Общие стили нужны каждому UserControl: StaticResource ищется по словарям его же разметки, а не
/// по дереву окна, поэтому контрол подключает словарь сам. Обычное
/// <c>&lt;ResourceDictionary Source="…"/&gt;</c> при этом разбирает файл заново в каждом контроле:
/// <c>SettingsPageStyles.xaml</c> разбирался семнадцать раз за конструктор окна, по разу на блок
/// настроек, а <c>Resources.xaml</c> — ещё раз в каждом оверлее. Здесь файл грузится при первой
/// просьбе, а дальше в <c>MergedDictionaries</c> кладётся тот же экземпляр.
/// </para>
/// <para>
/// Делить можно только словари без элементов-одиночек: в обоих файлах лежат стили, шаблоны и
/// геометрии, а <c>UIElement</c> в ресурсах (у него один родитель) сломал бы второго потребителя.
/// Кэш — на поток, а не на процесс: стили принадлежат потоку, на котором созданы, а у оконных
/// тестов и у замера холодного запуска свои потоки интерфейса.
/// </para>
/// </remarks>
internal sealed class SharedDictionary : ResourceDictionary
{
    [ThreadStatic]
    private static Dictionary<string, ResourceDictionary>? _loaded;

    private string? _path;

    /// <summary>Путь к словарю от корня проекта, например <c>UI/SettingsPageStyles.xaml</c>.</summary>
    public string? Path
    {
        get => _path;
        set
        {
            _path = value;
            if (!string.IsNullOrEmpty(value))
            {
                MergedDictionaries.Add(Load(value));
            }
        }
    }

    /// <summary>Разобранный словарь по пути от корня проекта — один на поток.</summary>
    internal static ResourceDictionary Load(string path)
    {
        var loaded = _loaded ??= new Dictionary<string, ResourceDictionary>(StringComparer.OrdinalIgnoreCase);
        if (!loaded.TryGetValue(path, out var dictionary))
        {
            dictionary = (ResourceDictionary)Application.LoadComponent(
                new Uri("/Amarin Admin AI;component/" + path, UriKind.Relative));
            loaded[path] = dictionary;
        }

        return dictionary;
    }
}
