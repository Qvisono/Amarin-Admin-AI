using System.Text.RegularExpressions;

namespace Amarin.Tools;

/// <summary>Сборка команд Hyper-V и Docker; имена от модели проверяются здесь.</summary>
/// <remarks>
/// До 1.28.0 имя ВМ вставлялось в <c>Get-VM -Name '…'</c> без удвоения кавычек, а имя
/// контейнера — в строку аргументов docker: «x' ; Remove-Item …» дописывало скрипту свою
/// команду, причём у чтения (<c>hyperv_vm_status</c>) — без вопроса человеку.
/// </remarks>
internal static partial class VirtualizationCommands
{
    public const string ListVms =
        "Get-VM | Select-Object Name,State,CPUUsage,MemoryAssigned | Format-Table -AutoSize";

    public static string VmStatus(string name) =>
        $"Get-VM -Name '{PowerShellHelper.QuoteLiteral(name)}' | Format-List *";

    public static string StartVm(string name) =>
        $"$ErrorActionPreference = 'Stop'; Start-VM -Name '{PowerShellHelper.QuoteLiteral(name)}'";

    public static string StopVm(string name) =>
        $"$ErrorActionPreference = 'Stop'; Stop-VM -Name '{PowerShellHelper.QuoteLiteral(name)}' -Force";

    /// <summary>Имя ВМ Hyper-V: без управляющих знаков и подстановочных <c>* ? [ ]</c>.</summary>
    /// <remarks>Подстановка превратила бы «останови ВМ» в «останови все подходящие».</remarks>
    public static bool IsVmName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 100 && VmName().IsMatch(name);

    /// <summary>Имя или идентификатор контейнера в правилах самого docker.</summary>
    public static bool IsContainerName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 128 && ContainerName().IsMatch(name);

    [GeneratedRegex(@"^[^\p{C}*?\[\]`]+$")]
    private static partial Regex VmName();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]*$")]
    private static partial Regex ContainerName();
}
