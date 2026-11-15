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
        /// <summary>Чаты, которые сейчас сжимаются: сжатие одного не мешает сжать другой.</summary>
        private readonly HashSet<string> _compacting = new(StringComparer.Ordinal);

        /// <summary>Сжатые сообщения открытого чата; null — пересчитать при следующем обращении.</summary>
        private HashSet<string>? _compactedIds;

        /// <summary>Сжимает старую часть открытого чата. Идёт, только когда чат не отвечает.</summary>
        /// <param name="options">Настройки с экрана сжатия; null — два последних обмена остаются дословно.</param>
        /// <param name="modelId">Модель с экрана сжатия; null — модель слота «Сводка».</param>
        private async Task CompactContextAsync(
            CompactionOptions? options = null, string? modelId = null, string? keyId = null, string? instructions = null)
        {
            var session = _session;
            if (_services is null || _compacting.Contains(session.Id))
            {
                return;
            }

            if (IsBusy(session.Id))
            {
                ShowTransientNotice(session.Id, Loc.Get("S.Compact.Busy"));
                return;
            }

            if (ContextCompaction.Plan(session, options) is not { } plan)
            {
                ShowTransientNotice(session.Id, Loc.Get("S.Compact.Nothing"));
                return;
            }

            if (!HasUsableKey())
            {
                ShowNoKeyNotice();
                return;
            }

            _compacting.Add(session.Id);
            UpdateCompactHint();
            RefreshSessionRail();
            ShowSessionNotice(session.Id, Loc.Get("S.Compact.Working"));
            try
            {
                IReadOnlyList<string> chunks;
                string sourceFingerprint;
                lock (session.Gate)
                {
                    chunks = ContextCompaction.Chunks(session, plan);
                    sourceFingerprint = ContextCompaction.SourceFingerprint(session, plan);
                }

                var draft = await _services.Summaries.CompactAsync(chunks, modelId, keyId, instructions);

                // Деньги потрачены и при неудаче: цена записывается до проверки результата.
                var priced = BookCompactCost(session, draft.Cost);

                // Пока модель пересказывала, в чат могли написать: граница та же, но проверяем,
                // что сжатая часть не изменилась под нами.
                var before = ReferenceEquals(session, _session) ? CompactedIds() : [];
                var applied = draft.Summary is not null && !IsBusy(session.Id) &&
                              ContextCompaction.TryApply(session, plan, sourceFingerprint, draft.Summary);
                if (applied || priced is not null)
                {
                    Persist(session);
                }

                if (ReferenceEquals(session, _session))
                {
                    RefreshMessageView(priced);
                    if (applied)
                    {
                        RefreshCompactionMarks(before);
                        RefreshContextRing();
                    }
                }

                ShowTransientNotice(session.Id, Loc.Get(applied ? "S.Compact.Done" : "S.Compact.Failed"));
            }
            catch (Exception ex) when (ex is HttpRequestException or VeniceApiException or TimeoutException)
            {
                ShowTransientNotice(session.Id, Loc.Get("S.Compact.Failed"));
            }
            finally
            {
                _compacting.Remove(session.Id);
                if (ReferenceEquals(session, _session))
                {
                    // Панель рисовалась, пока сжатие ещё шло: без этого её кнопки оставались выключенными.
                    UpdateCompactHint();
                    RefreshSessionRail();
                }
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
                // Ответ с ценой, а если такого нет (цены не назвал ни один) — просто последний ответ:
                // иначе цена сжатия не попадала в сумму чата вовсе.
                var last = session.Messages.LastOrDefault(message => message.Role == "assistant" && message.Cost is not null)
                           ?? session.Messages.LastOrDefault(message => message.Role == "assistant");
                if (last is null)
                {
                    return null;
                }

                last.CompactCost = last.CompactCost is null ? cost : last.CompactCost.Add(cost);
                last.Cost = last.Cost is null ? cost : last.Cost.Add(cost);
                return last.Id;
            }
        }

        private void UndoCompaction()
        {
            var before = CompactedIds();
            ContextCompaction.Clear(_session);
            Persist(_session);
            RefreshCompactionMarks(before);
            RefreshContextRing();
        }

        private HashSet<string> CompactedIds() => _compactedIds ??= ContextCompaction.CompactedMessageIds(_session);

        /// <summary>Перерисовывает сообщения, у которых сжатие поставило или сняло значок.</summary>
        private void RefreshCompactionMarks(HashSet<string> before)
        {
            _compactedIds = null;
            foreach (var id in before.Union(CompactedIds()).ToList())
            {
                RefreshMessageView(id);
            }
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
            compact.IsEnabled = plan is not null && !IsBusy(_session.Id) && !_compacting.Contains(_session.Id);
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
            // При панели сеанса сжатие открывается из неё: вторая кнопка в поле ввода её повторяла бы.
            var show = _services is not null &&
                       SessionRail.Visibility != Visibility.Visible &&
                       !_compacting.Contains(_session.Id) &&
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
            // Подпись ставится и чату, из которого уже ушли: иначе «Сжимаю…» оставалась на нём до
            // следующего ответа.
            ShowSessionNotice(sessionId, text);
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (Turns.NoticeFor(sessionId) == text && !IsBusy(sessionId))
                {
                    ClearComposerNotice(sessionId);
                }
            };
            timer.Start();
        }

        private void ShowSessionNotice(string sessionId, string text)
        {
            Turns.ShowNotice(sessionId, text);
            if (string.Equals(sessionId, _session.Id, StringComparison.Ordinal))
            {
                UpdateAttachmentWarning();
            }
        }

        /// <summary>
        /// Значок сжатого сообщения в его строке сведений: у ответа — за ценой, у вопроса — перед
        /// временем. Щелчок открывает отмену сжатия.
        /// </summary>
        /// <param name="first">True — значок встаёт в начало строки, иначе в конец.</param>
        private void AddCompactionIcon(ChatDisplayMessage message, Panel meta, bool first)
        {
            if (!CompactedIds().Contains(message.Id))
            {
                return;
            }

            var glyph = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse("M4.5,1.5 L8,5 L11.5,1.5 M4.5,14.5 L8,11 L11.5,14.5 M1.5,8 H14.5"),
                Width = 10,
                Height = 10,
                Stretch = System.Windows.Media.Stretch.Uniform,
                StrokeThickness = 1.2,
                StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
                StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
                StrokeLineJoin = System.Windows.Media.PenLineJoin.Round
            };
            glyph.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Text.Dim");
            var icon = new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(4),
                Margin = first ? new Thickness(0, 0, 4, 0) : new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = System.Windows.Media.Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = Loc.Get("S.Compact.MessageTip"),
                Child = glyph
            };
            icon.MouseEnter += (_, _) => icon.SetResourceReference(Border.BackgroundProperty, "Bg.Hover");
            icon.MouseLeave += (_, _) => icon.Background = System.Windows.Media.Brushes.Transparent;

            // Нажатие гасится здесь: иначе окно начало бы перетаскивание и отпускание не дошло бы.
            icon.MouseLeftButtonDown += (_, e) => e.Handled = true;
            icon.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                var menu = NewMenu(icon);
                menu.Items.Add(MenuItemFor(Loc.Get("S.Compact.Undo"), UndoCompaction));
                menu.IsOpen = true;
            };
            System.Windows.Automation.AutomationProperties.SetName(icon, Loc.Get("S.Compact.MessageTip"));
            if (first)
            {
                meta.Children.Insert(0, icon);
            }
            else
            {
                meta.Children.Add(icon);
            }
        }

        /// <summary>Отметка под последним сжатым сообщением: выше неё модель видит только сводку.</summary>
        private FrameworkElement WithCompactionMark(ChatDisplayMessage message, FrameworkElement view)
        {
            if (!string.Equals(message.Id, _session.CompactedAfterMessageId, StringComparison.Ordinal) ||
                !CompactedIds().Contains(message.Id))
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
