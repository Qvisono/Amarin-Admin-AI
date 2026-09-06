using Amarin.Core;

namespace Amarin.Tools;

internal static class AgentTools
{
    public static ToolRegistry Create(VeniceClient venice, HttpClient downloadHttp, DownloadOptions download) =>
        new(
        [
            new PowerShellTool(),
            new RegistryTool(),
            new ServiceTool(),
            new FileSystemTool(),
            new SystemInfoTool(),
            new WebDownloadTool(downloadHttp, download),
            new ScreenshotTool(),
            new ClipboardTool(),
            new FolderAnalysisTool(),
            new WebSearchTool(venice.SearchWebAsync),
            new ScrapeUrlTool(venice.ScrapeUrlAsync),
            new FetchImageTool(),
            new EventLogTool(),
            new NetworkTool(),
            new ScheduledTaskTool(),
            new WmiTool(),
            new ProcessTool(),
            new VirtualizationTool(),
            new ReliabilityTool(),
            new WindowsUpdateTool(),
            new SecurityTool(),
            new DevicesTool(),
            new DnsConfigTool(),
            new PortListenerTool(),
            new RemoteAccessTool(),
            new ChangeRollbackTool(),
            new PerformanceTool(),
            new StartupProgramsTool(),
            new CredentialsTool(),
            new SystemRepairTool(),
            new RestorePointTool(),
            new DiskManagementTool(),
            new DiskSpaceTool(),
            new SoftwareInventoryTool(),
            new FirewallRulesTool(),
            new WindowsFeaturesTool(),
            new LocalUsersTool()
        ]);
}
