using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Сжатие контекста (D10): меню у кольца, подсказка при заполнении окна, отметка в ленте и
    /// предложение сжать, когда провайдер ответил «контекст превышен».
    /// </summary>
    public partial class MainWindow
    {
        private bool _compacting;

        /// <summary>Сжимает старую часть открытого чата. Идёт, только когда чат не отвечает.</summary>
        private async Task CompactContextAsync()
        {
            if (_services is null || _compacting)
            {
                return;
            }

            var session = _session;
            if (IsBusy(session.Id))
            {
                ShowTransientNotice(session.Id, Loc.Get("S.Compact.Busy"));
                return;
            }

            if (ContextCompaction.Plan(session) is not { } plan)
            {
                ShowTransientNotice(session.Id, Loc.Get("S.Compact.Nothing"));
                return;
            }

            if (!HasUsableKey())
            {
                ShowNoKeyNotice();
                return;
            }

            _compacting = true;
            UpdateCompactHint();
            ShowComposerNotice(Loc.Get("S.Compact.Working"));
            try
            {
                var transcript = ContextCompaction.Transcript(session, plan);
                var draft = await _services.Summaries.CompactAsync(transcript);
                if (draft.Summary is null)
                {
                    ShowTransientNotice(session.Id, Loc.Get("S.Compact.Failed"));
                    return;
                }

                // Пока модель пересказывала, в чат могли написать: граница та же, но проверяем,
                // что сжатая часть не изменилась под нами.
                if (IsBusy(session.Id) || session.ApiMessages.Count < plan.Cut)
                {
                    ShowTransientNotice(session.Id, Loc.Get("S.Compact.Failed"));
                    return;
                }

                ContextCompaction.Apply(session, plan, draft.Summary);
                var priced = BookCompactCost(session, draft.Cost);
                Persist(session);

                if (ReferenceEquals(session, _session))
                {
                    RefreshMessageView(plan.AnchorMessageId);
                    if (priced is not null && priced != plan.AnchorMessageId)
                    {
                        RefreshMessageView(priced);
                    }

                    RefreshContextRing();
                }

                ShowTransientNotice(session.Id, Loc.Get("S.Compact.Done"));
            }
            catch (Exception ex) when (ex is HttpRequestException or VeniceApiException or TimeoutException)
            {
                ShowTransientNotice(session.Id, Loc.Get("S.Compact.Failed"));
            }
            finally
            {
                _compacting = false;
                UpdateCompactHint();
            }
        }

        /// <summary>
        /// Цена сжатия — последнему ответу чата, как и цена сводки: деньги потрачены на этот
        /// разговор, и в сумме чата они обязаны быть. Возвращает id переоценённого ответа.
        /// </summary>
        private static string? BookCompactCost(ChatSession session, VeniceCost? cost)
        {
            if (cost is not { HasData: true })
            {
                return null;
            }

            lock (session.Gate)
            {
                var last = session.Messages.LastOrDefault(message => message.Role == "assistant" && message.Cost is not null);
                if (last is null)
                {
                    return null;
                }

                last.CompactCost = last.CompactCost is null ? cost : last.CompactCost.Add(cost);
                last.Cost = last.Cost!.Add(cost);
                return last.Id;
            }
        }

        private void UndoCompaction()
        {
            string? anchor;
            lock (_session.Gate)
            {
                anchor = _session.CompactedAfterMessageId;
                _session.CompactedThrough = 0;
                _session.CompactSummary = null;
                _session.CompactFingerprint = null;
                _session.CompactedAfterMessageId = null;
                _session.LastPromptTokens = 0;
                _session.LastPromptTokensApiIndex = 0;
            }

            Persist(_session);
            RefreshMessageView(anchor);
            RefreshContextRing();
        }

        private void ContextBadge_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_services is null)
            {
                return;
            }

            e.Handled = true;
            var menu = NewMenu(ContextBadge);
            var plan = ContextCompaction.Plan(_session);
            var compact = MenuItemFor(Loc.Get("S.Compact.MenuItem"), () => Detached.Run(CompactContextAsync(), "compact"));
            compact.IsEnabled = plan is not null && !IsBusy(_session.Id) && !_compacting;
            menu.Items.Add(compact);
            if (ContextCompaction.IsActive(_session))
            {
                menu.Items.Add(MenuItemFor(Loc.Get("S.Compact.Undo"), UndoCompaction));
            }

            menu.IsOpen = true;
        }

        private void ContextHintButton_Click(object sender, RoutedEventArgs e) =>
            Detached.Run(CompactContextAsync(), "compact_hint");

        /// <summary>
        /// «Сжать» у кольца — когда окно заполнено на <see cref="ContextCompaction.SuggestAt"/>
        /// и сжимать есть что. Не всплывает и не мешает: просто кнопка рядом с цифрой.
        /// </summary>
        private void UpdateCompactHint(ContextUsage? usage = null)
        {
            if (ContextHintButton is null)
            {
                return;
            }

            var fraction = usage?.Fraction ?? _lastContextFraction;
            _lastContextFraction = fraction;
            var show = _services is not null &&
                       !_compacting &&
                       fraction >= ContextCompaction.SuggestAt &&
                       !IsBusy(_session.Id) &&
                       ContextCompaction.Plan(_session) is not null;
            ContextHintButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private double _lastContextFraction;

        /// <summary>
        /// Подпись у поля на несколько секунд. Обычная подпись композера живёт до конца хода, а у
        /// сжатия хода нет — она висела бы до следующего ответа.
        /// </summary>
        private void ShowTransientNotice(string sessionId, string text)
        {
            if (!string.Equals(sessionId, _session.Id, StringComparison.Ordinal))
            {
                return;
            }

            ShowComposerNotice(text);
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (_composerNotices.TryGetValue(sessionId, out var shown) && shown == text && !IsBusy(sessionId))
                {
                    ClearComposerNotice(sessionId);
                }
            };
            timer.Start();
        }

        /// <summary>Отметка под сообщением, до которого модель видит только сводку.</summary>
        private FrameworkElement WithCompactionMark(ChatDisplayMessage message, FrameworkElement view)
        {
            if (!string.Equals(message.Id, _session.CompactedAfterMessageId, StringComparison.Ordinal) ||
                !ContextCompaction.IsActive(_session))
            {
                return view;
            }

            var line = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Center };
            line.SetResourceReference(Border.BackgroundProperty, "Border.Default");
            var label = new TextBlock
            {
                Text = Loc.Get("S.Compact.Mark"),
                FontSize = 11,
                Margin = new Thickness(10, 0, 10, 0),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 460,
                TextAlignment = TextAlignment.Center
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Text.Faint");
            var right = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Center };
            right.SetResourceReference(Border.BackgroundProperty, "Border.Default");

            var mark = new Grid { Margin = new Thickness(0, 14, 0, 6), ToolTip = Loc.Get("S.Compact.MarkTip") };
            mark.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            mark.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            mark.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(label, 1);
            Grid.SetColumn(right, 2);
            mark.Children.Add(line);
            mark.Children.Add(label);
            mark.Children.Add(right);
            System.Windows.Automation.AutomationProperties.SetName(mark, Loc.Get("S.Compact.Mark"));

            var stack = new StackPanel();
            stack.Children.Add(view);
            stack.Children.Add(mark);
            return stack;
        }

        /// <summary>
        /// Провайдер отказал: переписка не помещается. Вместо голого текста ошибки — предложение
        /// сжать, если есть что.
        /// </summary>
        private async Task OfferCompactionAsync(string message)
        {
            if (ContextCompaction.Plan(_session) is null)
            {
                await ShowNoticeAsync(Loc.Get("S.Turn.ErrorTitle"), message, Loc.Get("S.Common.Close"), null, NoticeTone.Danger);
                return;
            }

            var compact = await ShowNoticeAsync(
                Loc.Get("S.Compact.OverflowTitle"),
                Loc.Get("S.Compact.OverflowText"),
                Loc.Get("S.Compact.MenuItem"),
                Loc.Get("S.Common.Close"));
            if (compact)
            {
                await CompactContextAsync();
            }
        }
    }
}
