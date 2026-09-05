using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// WPF allows exactly one <c>Application</c> per AppDomain, so every test that needs a live
/// UI thread shares this one instance through the <see cref="WpfCollection"/> collection.
/// </summary>
public sealed class WpfFixture : IDisposable
{
    public WpfFixture() => Ui = WpfUi.Start();

    internal WpfUi Ui { get; }

    public void Dispose() => Ui.Dispose();
}

[CollectionDefinition(Name)]
public sealed class WpfCollection : ICollectionFixture<WpfFixture>
{
    public const string Name = "wpf";
}
