using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

public sealed class ChangeRollbackStoreTests
{
    [Fact]
    public void CaptureCurrentServices_returns_named_entries()
    {
        var list = ChangeRollbackStore.CaptureCurrentServices();
        Assert.NotEmpty(list);
        Assert.Contains(list, s => !string.IsNullOrWhiteSpace(s.Name) && !string.IsNullOrWhiteSpace(s.Status));
    }

    [Fact]
    public void Installed_programs_catalog_reads_uninstall_keys()
    {
        var list = InstalledProgramsCatalog.Query(filter: null, max: 20);
        Assert.NotEmpty(list);
        Assert.All(list, e => Assert.False(string.IsNullOrWhiteSpace(e.Name)));
    }
}
