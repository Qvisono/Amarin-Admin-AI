using System.Text.Json;
using System.Windows;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.UI
{
    /// <summary>
    /// Задачи по расписанию (C3): таймер, прогон агента только на чтение и чат с отчётом.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Прогон — это агент без чата вокруг: та же запись хода (<see cref="AgentRunRecord"/>), тот же
    /// блок в ленте, но задание пишет расписание, а не модель чата. Поэтому и чат с итогом
    /// собирается здесь, а не движком: сообщение человека — задание, ответ — отчёт агента.
    /// </para>
    /// <para>
    /// Весь прогон идёт под <see cref="ToolGate.ForceReadOnly"/> и без плана: человека рядом нет,
    /// спросить его о записи или о плане некому. Запись отклоняется шлюзом, и агент пишет в
    /// отчёте, что надо бы сделать.
    /// </para>
    /// <para>
    /// Деньги — так же: потолок прогона (<see cref="ScheduledJob.MaxCostUsd"/>) и лимиты трат
    /// (E1) действуют без вопроса — дойдя до них, прогон обрывается отказом провайдера.
    /// </para>
    /// </remarks>
    public partial class MainWindow
    {
        private ScheduleRunner? _scheduleRunner;

        /// <summary>Заводит таймер расписания. Зовётся после первого кадра.</summary>
        private void StartSchedule()
        {
            if (_services?.AgentHost is null || _scheduleRunner is not null)
            {
                return;
            }

            _scheduleRunner = new ScheduleRunner(_services.Schedule, RunScheduledJobAsync);
            _scheduleRunner.Completed += (job, outcome) => Ui(() => OnScheduledJobCompleted(job, outcome));
            _scheduleRunner.Start(TimeSpan.FromSeconds(30));
            Closed += (_, _) => _scheduleRunner?.Dispose();
        }

        /// <summary>«Запустить сейчас» со страницы.</summary>
        internal Task<bool> RunScheduledJobNowAsync(string id) =>
            _scheduleRunner?.RunNowAsync(id) ?? Task.FromResult(false);

        /// <summary>Один прогон: чат, агент под «только чтением», отчёт и отметка статуса.</summary>
        private async Task<ScheduleRunOutcome> RunScheduledJobAsync(ScheduledJob job, CancellationToken cancellationToken)
        {
            var services = _services!;
            var session = BuildScheduleChat(services.ChatStore.CreateNew(), job, DateTime.Now, ActiveDateFormat, out var assistant, out var call);

            ToolResult result;
            using (AgentRunScope.Push(new AgentRunContext
            {
                Call = call,
                Assistant = assistant,
                Observer = SilentTurnObserver.Instance,
                SessionId = session.Id,
                ChatTitle = session.Title,
                PlanAllowed = false
            }))
            using (ToolGate.ForceReadOnly())
            using (SpendScope.Push(new SpendMeter { Unattended = true, Cap = job.MaxCostUsd }))
            {
                result = await services.AgentHost!.RunAsync(job.Prompt + ScheduleReport.Rule, null, cancellationToken)
                    .ConfigureAwait(false);
            }

            var outcome = CompleteScheduleChat(session, assistant, call, result);
            await Dispatcher.InvokeAsync(() => Persist(session));
            return outcome;
        }

        /// <summary>Заготовка чата прогона: задание от человека и ответ с блоком агента.</summary>
        internal static ChatSession BuildScheduleChat(
            ChatSession session,
            ScheduledJob job,
            DateTime now,
            DateFormat dateFormat,
            out ChatDisplayMessage assistant,
            out ToolCallRecord call)
        {
            session.Title = Loc.Format("S.Schedule.ChatTitle", job.Name, ChatFormat.DateTimeShort(now, dateFormat));
            session.ScheduleJobId = job.Id;
            session.Messages.Add(new ChatDisplayMessage
            {
                Role = "user",
                Id = Guid.NewGuid().ToString("N"),
                CreatedAt = now,
                Text = job.Prompt
            });

            call = new ToolCallRecord
            {
                Id = "schedule_" + Guid.NewGuid().ToString("N")[..8],
                Name = "init_agent",
                ArgumentsJson = JsonSerializer.Serialize(new { task = job.Prompt }),
                Status = ToolCallStatus.Running,
                StartedAt = now
            };
            assistant = new ChatDisplayMessage
            {
                Role = "assistant",
                Id = Guid.NewGuid().ToString("N"),
                CreatedAt = now,
                Status = AssistantStatus.Streaming,
                ToolRounds = [new ToolRound { Calls = [call] }]
            };
            session.Messages.Add(assistant);
            return session;
        }

        /// <summary>Итог прогона в чат: отчёт без служебной строки, цена, статус.</summary>
        internal static ScheduleRunOutcome CompleteScheduleChat(
            ChatSession session,
            ChatDisplayMessage assistant,
            ToolCallRecord call,
            ToolResult result)
        {
            var report = call.NestedAgent?.ReportText is { Length: > 0 } text ? text : result.Output;
            var status = result.Success ? ScheduleReport.Parse(report) : ScheduleStatus.Failed;
            var cost = call.NestedAgent?.Cost ?? VeniceCost.Zero;

            call.Status = result.Success ? ToolCallStatus.Done : ToolCallStatus.Failed;
            call.Success = result.Success;
            call.ResultPreview = ChatToolPreview.Summarize(result);
            call.ResultText = ChatToolPreview.ForJournal(result);
            call.Duration = DateTime.Now - call.StartedAt;

            assistant.Text = ScheduleReport.Strip(report);
            assistant.Status = result.Success ? AssistantStatus.Complete : AssistantStatus.Error;
            assistant.Cost = cost;
            assistant.ResolvedModelId = call.NestedAgent?.ModelId;

            // История для модели: если человек продолжит разговор, чат знает, о чём был отчёт.
            var user = session.Messages[0];
            session.ApiMessages.Add(new ChatMessage { Role = "user", Content = ChatContent.Text(user.Text) });
            session.ApiMessages.Add(new ChatMessage { Role = "assistant", Content = ChatContent.Text(assistant.Text) });
            session.UpdatedAt = DateTime.Now;

            return new ScheduleRunOutcome(status, cost.Usd, session.Id, result.Success ? null : result.Output);
        }

        private void OnScheduledJobCompleted(ScheduledJob job, ScheduleRunOutcome outcome)
        {
            if (AutomationPage.IsVisible)
            {
                AutomationPage.RefreshSchedule();
            }

            if (!ScheduleReport.Notable(outcome.Status) || outcome.ChatId is not { } chatId)
            {
                return;
            }

            Notify(
                "",
                IsLocked ? Loc.Get("S.Schedule.ToastLocked") : Loc.Format("S.Schedule.Toast", job.Name),
                Loc.Get(outcome.Status switch
                {
                    ScheduleStatus.Problem => "S.Schedule.Status.Problem",
                    ScheduleStatus.Failed => "S.Schedule.Status.Failed",
                    _ => "S.Schedule.Status.Attention"
                }),
                () => OpenChat(chatId),
                warning: outcome.Status != ScheduleStatus.Attention);
        }
    }
}
