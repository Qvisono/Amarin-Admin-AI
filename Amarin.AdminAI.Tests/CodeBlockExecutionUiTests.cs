using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using Amarin.Core;
using Amarin.Tools;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>Ручной PowerShell проходит шлюз без агента, реальных процессов и сети.</summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class CodeBlockExecutionUiTests(WpfFixture wpf)
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("PS1")]
    [InlineData("ps")]
    [InlineData("pwsh")]
    [InlineData("posh")]
    public async Task PowerShell_runs_locally_without_a_key_an_agent_or_chat_messages(string language)
    {
        JsonElement? arguments = null;
        var local = false;
        await With(async (window, services, handler) =>
        {
            var session = Session(window);
            var machine = new RemoteMachine { Id = "remote", Name = "Другой ПК", Address = "example.invalid", User = "test" };
            Assert.True(services.Machines.Save([machine]));
            session.TargetMachineId = machine.Id;
            var input = (TextBox)window.FindName("MessageTextBox");
            input.Text = "черновик";

            var request = Request(window, "Get-Date -Format o", language);
            Assert.NotNull(request.Completion);
            await request.Completion;

            Assert.Equal("Get-Date -Format o", arguments?.GetProperty("command").GetString());
            Assert.True(local);
            Assert.Equal("черновик", input.Text);
            Assert.Empty(session.Messages);
            Assert.Empty(session.ApiMessages);
            Assert.False(window.IsBusy(session.Id));
            Assert.False(window.Turns.NeedsAttention(session.Id));
            Assert.Equal(0, handler.Generations);
            // Успешное чтение не засоряет журнал аудита, как и у остальных путей инструментов.
            Assert.Empty(services.Audit!.ReadAll());
        }, (args, _) =>
        {
            arguments = args.Clone();
            local = ExecutionTarget.Current is null;
            return Task.FromResult(ToolResult.Ok("Exit code: 0\n--- stdout ---\nготово"));
        }, apiKey: "");
    }

    [Fact]
    public async Task A_running_command_keeps_drafts_and_rejects_a_second_run_after_a_repaint()
    {
        var completion = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        await With(async (window, services, handler) =>
        {
            var session = Session(window);
            var first = Request(window, "Get-Date");
            var input = (TextBox)window.FindName("MessageTextBox");
            input.Text = "сохрани сообщение";
            var files = Field<List<FileAttachment>>(window, "_pendingFiles");
            var quotes = Field<List<MessageQuote>>(window, "_pendingQuotes");
            files.Add(new FileAttachment("eA==", "text/plain", "note.txt", 1));
            quotes.Add(new MessageQuote { Number = 1, SourceMessageId = "earlier", Text = "цитата" });

            await window.SendAsync();
            var repeated = Request(window, "Get-Date");
            await repeated.Completion!;
            Assert.Equal("сохрани сообщение", input.Text);
            Assert.Single(files);
            Assert.Single(quotes);
            Assert.Empty(session.Messages);
            Assert.Empty(session.ApiMessages);
            Assert.Equal(1, runs);
            Assert.Equal(TurnKind.CodeBlock, window.FindTurn(session.Id)?.Kind);

            completion.SetResult(ToolResult.Ok("Exit code: 0"));
            await first.Completion!;
            Assert.Equal("сохрани сообщение", input.Text);
            Assert.Empty(session.Messages);
            Assert.Empty(session.ApiMessages);
            Assert.Equal(0, handler.Generations);
        }, (_, token) =>
        {
            runs++;
            return completion.Task.WaitAsync(token);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_write_obeys_both_the_access_mode_and_the_chat_read_only_flag(bool chatReadOnly)
    {
        var runs = 0;
        await With(async (window, services, handler) =>
        {
            if (chatReadOnly)
            {
                Session(window).ReadOnly = true;
            }
            else
            {
                services.Settings.ApprovalMode = ApprovalMode.ReadOnly;
            }

            var outcome = Assert.IsType<(ToolResult Result, bool Cancelled)>(
                await window.ExecuteCodeBlockAsync(Session(window), "Set-Service -Name Spooler -StartupType Automatic"));
            _ = window.ShowCodeBlockResultAsync(outcome.Result, outcome.Cancelled);
            Assert.Equal(0, runs);
            Assert.Equal(Loc.Get("S.Code.RunFailed"), ((TextBlock)window.FindName("NoticeTitle")).Text);
            Assert.Contains("чтение", ResultText(window), StringComparison.OrdinalIgnoreCase);
        }, (_, _) =>
        {
            runs++;
            return Task.FromResult(ToolResult.Ok("не должно исполниться"));
        });
    }

    [Fact]
    public async Task A_disabled_PowerShell_tool_cannot_be_run_from_a_code_block()
    {
        var runs = 0;
        await With(async (window, services, handler) =>
        {
            var session = Session(window);
            var machine = new RemoteMachine { Id = "remote", Name = "Другой ПК", Address = "example.invalid", User = "test" };
            Assert.True(services.Machines.Save([machine]));
            session.TargetMachineId = machine.Id;
            services.Settings.DisabledTools = ["run_powershell"];
            var outcome = Assert.IsType<(ToolResult Result, bool Cancelled)>(await window.ExecuteCodeBlockAsync(session, "Get-Date"));
            _ = window.ShowCodeBlockResultAsync(outcome.Result, outcome.Cancelled);
            Assert.Equal(0, runs);
            Assert.Contains("выключен", ResultText(window), StringComparison.OrdinalIgnoreCase);
            var audit = Assert.Single(services.Audit!.ReadAll());
            Assert.Equal(AuditOutcome.Refused, audit.Outcome);
            Assert.Equal("run_powershell", audit.Tool);
            Assert.Equal(session.Id, audit.ChatId);
            Assert.Null(audit.Target);
        }, (_, _) =>
        {
            runs++;
            return Task.FromResult(ToolResult.Ok("не должно исполниться"));
        });
    }

    [Fact]
    public async Task A_write_waits_for_its_own_chat_confirmation_and_a_refusal_does_not_execute_it()
    {
        var runs = 0;
        await With(async (window, services, handler) =>
        {
            var execution = window.ExecuteCodeBlockAsync(Session(window), "Set-Service -Name Spooler -StartupType Automatic");
            Assert.False(execution.IsCompleted);
            Assert.True(services.Confirmations.TryPeek(out var question));
            Assert.Equal(Session(window).Id, question!.SessionId);
            services.Confirmations.Complete(question, approved: false);
            var outcome = Assert.IsType<(ToolResult Result, bool Cancelled)>(await execution);
            _ = window.ShowCodeBlockResultAsync(outcome.Result, outcome.Cancelled);
            Assert.Equal(0, runs);
            Assert.Contains(ToolGate.DeniedReply, ResultText(window), StringComparison.Ordinal);
            Assert.False(window.AnyTurnRunning);
        }, (_, _) =>
        {
            runs++;
            return Task.FromResult(ToolResult.Ok("не должно исполниться"));
        });
    }

    [Fact]
    public async Task The_stop_action_still_belongs_to_the_original_chat_after_switching()
    {
        var stopped = false;
        await With(async (window, services, handler) =>
        {
            var original = Session(window);
            var running = Request(window, "Get-Date");
            var other = new ChatSession { Id = "other", SelectedModelId = "grok-4-6" };
            SetSession(window, other);
            var otherTurn = window.Turns.TryStart(other, TurnKind.Send, DateTime.Now).Turn!;
            running.Cancel!();
            Assert.False(otherTurn.Cancellation.IsCancellationRequested);
            SetSession(window, original);
            await running.Completion!;
            Assert.True(stopped);
            Assert.False(window.IsBusy(original.Id));
            window.FinishTurn(otherTurn);
        }, async (_, token) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return ToolResult.Ok("не должно завершиться само");
            }
            catch (OperationCanceledException)
            {
                stopped = true;
                throw;
            }
        });
    }

    [Fact]
    public async Task A_failed_command_shows_its_actual_output_without_an_assistant_message()
    {
        await With(async (window, services, handler) =>
        {
            var outcome = Assert.IsType<(ToolResult Result, bool Cancelled)>(
                await window.ExecuteCodeBlockAsync(Session(window), "Get-Date"));
            _ = window.ShowCodeBlockResultAsync(outcome.Result, outcome.Cancelled);
            Assert.Equal(Loc.Get("S.Code.RunFailed"), ((TextBlock)window.FindName("NoticeTitle")).Text);
            Assert.Contains("Exit code: 1", ResultText(window), StringComparison.Ordinal);
            Assert.Contains("failure detail", ResultText(window), StringComparison.Ordinal);
            Assert.Empty(Session(window).Messages);
        }, (_, _) => Task.FromResult(ToolResult.Fail("Exit code: 1\n--- stderr ---\nfailure detail")));
    }

    [Fact]
    public async Task A_finished_command_preserves_an_existing_notice_and_does_not_take_focus_from_an_overlay()
    {
        await With(async (window, services, handler) =>
        {
            var session = Session(window);
            Assert.True(window.CanShowCodeBlockResult(session, foreground: true));
            Assert.False(window.CanShowCodeBlockResult(session, foreground: false));
            var notice = window.ShowNoticeAsync("Прежний вопрос", "Сохранить ответ?", "Да", "Нет");
            var execution = await window.ExecuteCodeBlockAsync(session, "Get-Date");
            Assert.NotNull(execution);
            Assert.False(window.CanShowCodeBlockResult(session, foreground: true));
            Assert.False(notice.IsCompleted);
            Assert.Equal("Прежний вопрос", ((TextBlock)window.FindName("NoticeTitle")).Text);
            ((Button)window.FindName("NoticePrimaryButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.True(await notice);

            var settings = (FrameworkElement)window.FindName("SettingsOverlay");
            settings.Visibility = Visibility.Visible;
            Assert.False(window.CanShowCodeBlockResult(session, foreground: true));
            settings.Visibility = Visibility.Collapsed;
            Assert.False(window.CanShowCodeBlockResult(new ChatSession { Id = "other" }, foreground: true));
        }, (_, _) => Task.FromResult(ToolResult.Ok("Exit code: 0")));
    }

    private async Task With(
        Func<MainWindow, AppServices, CountingHandler, Task> body,
        Func<JsonElement, CancellationToken, Task<ToolResult>> execute,
        string apiKey = "test")
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-code-run-" + Guid.NewGuid().ToString("N"));
        try
        {
            await wpf.Ui.Invoke(async () =>
            {
                using var handler = new CountingHandler();
                using var services = UiServices.Build(root, apiKey, handler, new AuditLog(root));
                var tools = new ToolRegistry([new CapturingPowerShell(execute)]);
                typeof(AppServices).GetProperty("RunTools", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(services, (Func<ToolRegistry>)(() => tools));
                var previous = CodeBlockView.ShowLineNumbers;
                var window = new MainWindow();
                try
                {
                    window.AttachServices(services);
                    SetSession(window, new ChatSession
                    {
                        Id = "script-chat",
                        Title = "Проверка команды",
                        SelectedModelId = "grok-4-6",
                        CreatedAt = DateTime.Now,
                        UpdatedAt = DateTime.Now
                    });
                    await body(window, services, handler);
                }
                finally
                {
                    if (window.IsNoticeOpen)
                    {
                        ((Button)window.FindName("NoticePrimaryButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    }

                    window.Close();
                    CodeBlockView.ShowLineNumbers = previous;
                }
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static CodeBlockView.RunScriptEventArgs Request(MainWindow window, string command, string language = "powershell")
    {
        var request = new CodeBlockView.RunScriptEventArgs(CodeBlockView.RunScriptEvent, window, command, language);
        window.RaiseEvent(request);
        return request;
    }

    private static T Field<T>(MainWindow window, string name) =>
        (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static ChatSession Session(MainWindow window) => Field<ChatSession>(window, "_session");

    private static void SetSession(MainWindow window, ChatSession session) =>
        typeof(MainWindow).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, session);

    private static string ResultText(MainWindow window)
    {
        var extra = (ContentControl)window.FindName("NoticeExtra");
        var box = Logical((DependencyObject)extra.Content).OfType<RichTextBox>().Single();
        return new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text;
    }

    private static IEnumerable<DependencyObject> Logical(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            foreach (var item in Logical(child))
            {
                yield return item;
            }
        }
    }

    private sealed class CapturingPowerShell(Func<JsonElement, CancellationToken, Task<ToolResult>> execute) : ITool
    {
        public string Name => "run_powershell";

        public string Description => "Подставной PowerShell без системных эффектов";

        public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new { type = "object" });

        public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default) =>
            execute(arguments, cancellationToken);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Generations { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal) == true)
            {
                Generations++;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{}") });
        }
    }
}
