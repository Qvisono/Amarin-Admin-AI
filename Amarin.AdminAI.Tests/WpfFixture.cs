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
}
