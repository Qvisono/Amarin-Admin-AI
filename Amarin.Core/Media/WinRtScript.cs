using System.Globalization;
using Amarin.Tools;

namespace Amarin.Core;

/// <summary>
/// Скрипт Windows PowerShell 5.1 с типами WinRT — для того, что есть только в WinRT: распознавание
/// текста (<c>Windows.Media.Ocr</c>), страницы PDF картинкой (<c>Windows.Data.Pdf</c>) и
/// перекодирование звука (<c>Windows.Media.Transcoding</c>).
/// </summary>
/// <remarks>
/// <para>
/// У сборок программы нет проекций WinRT: целевая платформа без версии Windows SDK, а с ней exe
/// потяжелел бы на десятки мегабайт. PowerShell 5.1 грузит WinRT сам, как в
/// <see cref="BluetoothCommands"/>, а отдельный процесс заодно не даёт сбою кодека или драйвера
/// уронить программу.
/// </para>
/// <para>
/// Скрипт всегда идёт на этом ПК, а не на удалённой машине чата: он читает файл, который лежит
/// здесь. Поэтому процесс запускается напрямую, мимо <see cref="PowerShellHelper.RunAsync"/>.
/// </para>
/// </remarks>
internal static class WinRtScript
{
    /// <summary>Загрузка WinRT и ожидание его асинхронных операций с общим сроком на одно ожидание.</summary>
    private const string Prelude = """
        $ErrorActionPreference = 'Stop'
        Add-Type -AssemblyName System.Runtime.WindowsRuntime
        $winRtAsTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 }
        function WinRtMethod([string]$kind) { ($winRtAsTask | Where-Object { $_.GetParameters()[0].ParameterType.Name -eq $kind })[0] }
        function WinRtWait($task) { if (-not $task.Wait($script:WinRtWaitMs)) { throw 'WINRT_TIMEOUT' }; $task }
        function Await($operation, [Type]$type) { (WinRtWait ((WinRtMethod 'IAsyncOperation`1').MakeGenericMethod($type).Invoke($null, @($operation)))).Result }
        function AwaitAction($operation) { WinRtWait ((WinRtMethod 'IAsyncAction').Invoke($null, @($operation))) | Out-Null }
        function AwaitProgress($operation, [Type]$progress) { WinRtWait ((WinRtMethod 'IAsyncActionWithProgress`1').MakeGenericMethod($progress).Invoke($null, @($operation))) | Out-Null }
        [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime] | Out-Null
        [Windows.Storage.StorageFolder, Windows.Storage, ContentType = WindowsRuntime] | Out-Null
        [Windows.Storage.Streams.IRandomAccessStream, Windows.Storage.Streams, ContentType = WindowsRuntime] | Out-Null
        [Windows.Storage.Streams.InMemoryRandomAccessStream, Windows.Storage.Streams, ContentType = WindowsRuntime] | Out-Null
        """;

    /// <summary>Строка PowerShell в одинарных кавычках.</summary>
    public static string Literal(string value) => "'" + PowerShellHelper.QuoteLiteral(value) + "'";

    /// <summary>Исполняет тело скрипта; срок одного ожидания WinRT — весь срок скрипта.</summary>
    /// <returns>Вывод скрипта или отказ с текстом ошибки.</returns>
    /// <param name="longOperation">Срок больше десяти минут — перекодирование долгой записи.</param>
    public static async Task<(bool Success, string Output)> RunAsync(string body, int timeoutSeconds, CancellationToken cancellationToken, bool longOperation = false)
    {
        var script = "$script:WinRtWaitMs = " + (timeoutSeconds * 1000).ToString(CultureInfo.InvariantCulture) + "\n" + Prelude + "\n" + body;
        var result = await PowerShellProcessRunner.RunAsync(
            PowerShellHelper.WrapScript(script), timeoutSeconds, cancellationToken, maxStdout: 600_000, maxStderr: 8_000, longOperation).ConfigureAwait(false);
        var output = PowerShellHelper.ExtractStdout(result.Output);
        return (result.Success && !result.Output.Contains("WINRT_TIMEOUT", StringComparison.Ordinal), result.Success ? output : result.Output);
    }
}
