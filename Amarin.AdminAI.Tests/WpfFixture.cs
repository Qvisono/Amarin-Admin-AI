using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// WPF allows exactly one <c>Application</c> per AppDomain, so every test that needs a live
/// UI thread shares this one instance through the <see cref="WpfCollection"/> collection.
/// </summary>
public sealed class WpfFixture : IDisposable
{
    public WpfFixture()
    {
        Ui = WpfUi.Start();

        // Язык прибиваем к русскому. WpfUi поднимает окно по настоящим настройкам из %APPDATA%,
        // а там у человека может стоять любой язык — в том числе переведённый моделью. Без этого
        // проверки подписей падали бы на чужой машине, и ровно это однажды и случилось: интерфейс
        // оказался японским. Словарь живёт в памяти процесса, на диск ничего не пишется.
        Ui.Invoke<object?>(() =>
        {
            LanguageManager.Apply(LanguageManager.DefaultCode);
            return null;
        });
    }

    internal WpfUi Ui { get; }

    public void Dispose() => Ui.Dispose();
}

[CollectionDefinition(Name)]
public sealed class WpfCollection : ICollectionFixture<WpfFixture>
{
    public const string Name = "wpf";

    /// <summary>
    /// Метка оконных тестов: CI на каждый пуш гоняет всё, кроме них (<c>Category!=Wpf</c>).
    /// </summary>
    /// <remarks>
    /// На раннере GitHub общий поток интерфейса то не стартует, то умирает, и красная отметка
    /// на каждом пуше была бы шумом; оконные тесты гоняет ручной workflow «Tests». Коллекцию
    /// фильтр dotnet test не видит — нужна метка, а её наличие сторожит
    /// <c>TestCategoryTests</c>.
    /// </remarks>
    public const string Category = "Category";

    public const string Trait = "Wpf";
}
