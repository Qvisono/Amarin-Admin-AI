using System.Globalization;
using System.Security;
using System.Text;

namespace Amarin.Core;

/// <summary>
/// Задача Планировщика Windows, которая запускает программу к сроку отложенной задачи и при входе
/// в Windows. Чистая сборка XML — значения-ловушки проверяются тестами без Планировщика.
/// </summary>
/// <remarks>
/// <para>
/// Каждое значение выбрано против умолчания Планировщика, которое здесь вредит:
/// <list type="bullet">
/// <item><c>ExecutionTimeLimit=PT0S</c> — по умолчанию 72 часа, и через трое суток Планировщик
/// убил бы работающую программу.</item>
/// <item><c>Priority=4</c> — по умолчанию 7, ниже обычного: программа с окном тормозила бы.</item>
/// <item>Работа от батареи разрешена — по умолчанию ноутбук без сети задачу не запускает вовсе.</item>
/// <item><c>StartWhenAvailable</c> — срок, пропущенный выключенным ПК, наверстается при включении.</item>
/// <item><c>InteractiveToken</c> и <c>LeastPrivilege</c> — только в сеансе человека и с его
/// правами: без входа запускать окно некому, а пароль Windows программе не нужен.</item>
/// <item><c>IgnoreNew</c> — второй запуск поверх работающего не нужен: он и так отдал бы запрос
/// работающему и вышел.</item>
/// </list>
/// </para>
/// <para>
/// Вход в Windows — второй повод: задачи «при следующем включении», «когда ПК проработает» и
/// условия ждут работающую программу, а не момент времени.
/// </para>
/// </remarks>
internal static class DeferredWakeTask
{
    /// <summary>Аргумент, с которым Планировщик запускает программу.</summary>
    public const string WakeArgument = "--wake";

    /// <summary>Имя задачи: одна на пользователя Windows, у каждого — свой SID в имени.</summary>
    public static string NameFor(string userSid) => @"\Amarin Admin AI deferred (" + userSid + ")";

    /// <summary>Пауза после входа: программа не спорит за диск с остальным автозапуском.</summary>
    public const string LogonDelay = "PT20S";

    public static string Build(string exePath, string userSid, DateTime? dueUtc)
    {
        var sid = SecurityElement.Escape(userSid);
        var xml = new StringBuilder()
            .AppendLine("<?xml version=\"1.0\" encoding=\"UTF-16\"?>")
            .AppendLine("<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">")
            .AppendLine("  <RegistrationInfo>")
            .AppendLine("    <Description>Starts Amarin Admin AI when a deferred task is due and when you sign in to Windows.</Description>")
            .Append("    <URI>").Append(SecurityElement.Escape(NameFor(userSid))).AppendLine("</URI>")
            .AppendLine("  </RegistrationInfo>")
            .AppendLine("  <Triggers>");
        if (dueUtc is { } due)
        {
            xml.AppendLine("    <TimeTrigger>")
                .Append("      <StartBoundary>")
                .Append(DateTime.SpecifyKind(due, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))
                .AppendLine("</StartBoundary>")
                .AppendLine("      <Enabled>true</Enabled>")
                .AppendLine("    </TimeTrigger>");
        }

        return xml
            .AppendLine("    <LogonTrigger>")
            .AppendLine("      <Enabled>true</Enabled>")
            .Append("      <UserId>").Append(sid).AppendLine("</UserId>")
            .Append("      <Delay>").Append(LogonDelay).AppendLine("</Delay>")
            .AppendLine("    </LogonTrigger>")
            .AppendLine("  </Triggers>")
            .AppendLine("  <Principals>")
            .AppendLine("    <Principal id=\"Author\">")
            .Append("      <UserId>").Append(sid).AppendLine("</UserId>")
            .AppendLine("      <LogonType>InteractiveToken</LogonType>")
            .AppendLine("      <RunLevel>LeastPrivilege</RunLevel>")
            .AppendLine("    </Principal>")
            .AppendLine("  </Principals>")
            .AppendLine("  <Settings>")
            .AppendLine("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>")
            .AppendLine("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>")
            .AppendLine("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>")
            .AppendLine("    <AllowHardTerminate>false</AllowHardTerminate>")
            .AppendLine("    <StartWhenAvailable>true</StartWhenAvailable>")
            .AppendLine("    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>")
            .AppendLine("    <IdleSettings>")
            .AppendLine("      <StopOnIdleEnd>false</StopOnIdleEnd>")
            .AppendLine("      <RestartOnIdle>false</RestartOnIdle>")
            .AppendLine("    </IdleSettings>")
            .AppendLine("    <AllowStartOnDemand>true</AllowStartOnDemand>")
            .AppendLine("    <Enabled>true</Enabled>")
            .AppendLine("    <Hidden>false</Hidden>")
            .AppendLine("    <RunOnlyIfIdle>false</RunOnlyIfIdle>")
            .AppendLine("    <WakeToRun>false</WakeToRun>")
            .AppendLine("    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>")
            .AppendLine("    <Priority>4</Priority>")
            .AppendLine("  </Settings>")
            .AppendLine("  <Actions Context=\"Author\">")
            .AppendLine("    <Exec>")
            .Append("      <Command>").Append(SecurityElement.Escape(exePath)).AppendLine("</Command>")
            .Append("      <Arguments>").Append(WakeArgument).AppendLine("</Arguments>")
            .AppendLine("    </Exec>")
            .AppendLine("  </Actions>")
            .AppendLine("</Task>")
            .ToString();
    }
}
