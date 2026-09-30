using System.Reflection;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// CI отделяет оконные тесты меткой, а не коллекцией: фильтр <c>dotnet test</c> коллекций не
/// видит. Класс, которому метку забыли, ушёл бы в прогон без окон и падал бы там на каждом
/// пуше; метка на обычном тесте, наоборот, спрятала бы его из этого прогона.
/// </summary>
public sealed class TestCategoryTests
{
    private static readonly Type[] TestTypes = typeof(TestCategoryTests).Assembly.GetTypes();

    [Fact]
    public void Every_test_that_needs_the_ui_thread_carries_the_wpf_trait()
    {
        var unmarked = TestTypes
            .Where(NeedsUiThread)
            .Where(type => !HasWpfTrait(type))
            .Select(type => type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(unmarked.Count == 0, "без метки Wpf: " + string.Join(", ", unmarked));
    }

    [Fact]
    public void The_trait_is_only_on_tests_of_the_ui_collection()
    {
        var stray = TestTypes
            .Where(HasWpfTrait)
            .Where(type => !NeedsUiThread(type))
            .Select(type => type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(stray.Count == 0, "метка Wpf вне коллекции: " + string.Join(", ", stray));
    }

    private static bool NeedsUiThread(Type type) =>
        type.GetCustomAttributesData().Any(data =>
            data.AttributeType == typeof(CollectionAttribute) &&
            data.ConstructorArguments.Count == 1 &&
            (string?)data.ConstructorArguments[0].Value == WpfCollection.Name) ||
        type.GetConstructors().Any(ctor =>
            ctor.GetParameters().Any(parameter => parameter.ParameterType == typeof(WpfFixture)));

    private static bool HasWpfTrait(Type type) =>
        type.GetCustomAttributesData().Any(data =>
            data.AttributeType == typeof(TraitAttribute) &&
            data.ConstructorArguments.Count == 2 &&
            (string?)data.ConstructorArguments[0].Value == WpfCollection.Category &&
            (string?)data.ConstructorArguments[1].Value == WpfCollection.Trait);
}
