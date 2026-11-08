using System.Text.Json;
using System.Windows;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.UI
{
    /// <summary>Кнопки блоков кода, которым нужно окно (D13).</summary>
    public partial class MainWindow
    {
        private void WireCodeBlocks()
        {
            CodeBlockView.ShowLineNumbers = () => _services?.Settings.CodeLineNumbers == true;
            AddHandler(CodeBlockView.RunScriptEvent, new RoutedEventHandler(OnRunScriptRequested));
        }

        /// <summary>
        /// PowerShell исполняется на этом ПК через общий шлюз. Командные файлы, как прежде,
        /// становятся задачей агенту в поле ввода.
        /// </summary>
        private void OnRunScriptRequested(object sender, RoutedEventArgs e)
        {
            if (e is not CodeBlockView.RunScriptEventArgs request)
            {
                return;
            }

            e.Handled = true;
            if (CodeLanguages.Normalize(request.Language) == "powershell")
            {
                var session = _session;
                request.Cancel = () => CancelTurn(session.Id);
                request.Completion = RunCodeBlockAsync(session, request.Code);
                return;
            }

            // Ограда длиннее самой длинной серии обратных апострофов в коде — иначе код её закрыл бы.
            var longest = 0;
            var run = 0;
            foreach (var symbol in request.Code)
            {
                run = symbol == '`' ? run + 1 : 0;
                longest = Math.Max(longest, run);
            }

            var fence = new string('`', Math.Max(3, longest + 1));
            var task = "/agent " + Loc.Get("S.Code.AgentTask") + "\n" + fence + request.Language + "\n" + request.Code + "\n" + fence;
            PlaceIncomingPrompt(task, send: false);
        }

        private async Task RunCodeBlockAsync(ChatSession session, string code)
        {
            if (await ExecuteCodeBlockAsync(session, code) is not { } outcome || _windowClosed || !IsLoaded)
            {
                return;
            }

            var (result, cancelled) = outcome;
            void ShowResult() => Detached.Run(ShowCodeBlockResultAsync(result, cancelled), "code_block_result");
            if (CanShowCodeBlockResult(session, IsVisible && IsForeground()))
            {
                ShowResult();
            }
            else
            {
                Notify("", ToastText(CodeBlockResultTitle(result, cancelled)), IsLocked ? "" : DisplayTitle(session.Title),
                    ShowResult, warning: !result.Success && !cancelled, title: Loc.Get("S.Code.RunOrigin"));
            }
        }

        /// <summary>Ручное выполнение без агента, с отменой и ограничениями обычного хода.</summary>
        internal async Task<(ToolResult Result, bool Cancelled)?> ExecuteCodeBlockAsync(ChatSession session, string code)
        {
            if (_services is not { } services)
            {
                return null;
            }

            if (IsBusy(session.Id))
            {
                ShowComposerNotice(Loc.Get("S.Code.RunBusy"));
                return null;
            }

            ToolResult? result = null;
            var cancelled = false;
            var started = await TryRunTurnAsync(session, TurnKind.CodeBlock, async (chat, _, token) =>
            {
                // Кнопка обещает этот ПК даже у чата с удалённой целью. Режим чтения чата остаётся.
                using var local = ExecutionTarget.Push(null);
                try
                {
                    var outcome = await GateRun.RunAsync(
                        services.RunTools(),
                        "run_powershell",
                        JsonSerializer.SerializeToElement(new { command = code }),
                        services.Settings,
                        (info, cancellation) => services.Confirmations.ConfirmDetailedAsync(
                            Loc.Get("S.Code.RunOrigin"), info, chat.Id, cancellation),
                        new AuditOrigin(chat.Id, chat.Title, Loc.Get("S.Code.RunOrigin")),
                        "code_" + Guid.NewGuid().ToString("N")[..8],
                        () => new SessionUndoTracker(),
                        services.Audit,
                        token);
                    result = outcome.Result;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    cancelled = true;
                    result = ToolResult.Fail(Loc.Get("S.Code.RunCancelled"));
                }
            }, ShowTurnLimitNotice);

            return started && result is not null ? (result, cancelled) : null;
        }

        // Проверка перед показом сохраняет вопрос, на который человек ещё не ответил, и фокус другого экрана.
        internal bool CanShowCodeBlockResult(ChatSession session, bool foreground) =>
            foreground && WindowState != WindowState.Minimized &&
            string.Equals(_session.Id, session.Id, StringComparison.Ordinal) &&
            !ChatCoveredByOverlay() && !IsNoticeOpen && !ChatBlocked;

        internal Task<bool> ShowCodeBlockResultAsync(ToolResult result, bool cancelled) =>
            ShowNoticeAsync(
                CodeBlockResultTitle(result, cancelled),
                "",
                Loc.Get("S.Common.Close"),
                null,
                result.Success || cancelled ? NoticeTone.Info : NoticeTone.Danger,
                Scrolled(CodeBlockView.Create(this, result.Output, null, copyOnly: true)));

        private static string CodeBlockResultTitle(ToolResult result, bool cancelled) =>
            Loc.Get(cancelled ? "S.Code.RunCancelled" : result.Success ? "S.Code.RunDone" : "S.Code.RunFailed");
    }
}
