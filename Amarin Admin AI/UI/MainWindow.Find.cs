using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Поиск по открытому чату (D1): совпадения считает <see cref="ChatFind"/>, лента их
    /// показывает — достраивает сообщение, раскрывает блок инструментов, подсвечивает и доезжает.
    /// </summary>
    /// <remarks>
    /// Подсветка — фоном прямо в документе сообщения, а снимается пересборкой этого пузыря
    /// (<see cref="RefreshMessageView"/>): так от неё не остаётся следов ни при смене темы, ни
    /// при правке, а трогать документ обратно по сохранённым диапазонам не нужно.
    /// </remarks>
    public partial class MainWindow
    {
        private IReadOnlyList<ChatFindHit> _findHits = [];
        private int _findIndex = -1;
        private string? _findHighlighted;

        private void WireFind()
        {
            FindBar.QueryChanged += OnFindQueryChanged;
            FindBar.Older += () => StepFind(-1);
            FindBar.Newer += () => StepFind(+1);
            FindBar.CloseRequested += CloseFind;
        }

        /// <summary>Ctrl+F: открыть панель или, если открыта, выделить запрос для нового поиска.</summary>
        internal bool OpenFind()
        {
            FindBar.Visibility = Visibility.Visible;
            FindBar.FocusQuery();
            if (FindBar.Query.Trim().Length > 0)
            {
                OnFindQueryChanged(FindBar.Query);
            }

            return true;
        }

        private void CloseFind()
        {
            ClearFindHighlight();
            _findHits = [];
            _findIndex = -1;
            FindBar.Visibility = Visibility.Collapsed;
            FocusMessageInput();
        }

        /// <summary>Смена чата закрывает поиск: найденное относилось к прежнему.</summary>
        private void ResetFindForChat()
        {
            if (FindBar.Visibility == Visibility.Visible)
            {
                _findHighlighted = null;
                _findHits = [];
                _findIndex = -1;
                FindBar.Visibility = Visibility.Collapsed;
            }
        }

        private void OnFindQueryChanged(string query)
        {
            _findHits = ChatFind.Find(_session.Messages, query);
            // С низа: свежие сообщения ближе к тому, что человек сейчас читает.
            _findIndex = _findHits.Count - 1;
            FindBar.ShowCount(_findHits.Count == 0 ? 0 : _findIndex + 1, _findHits.Count);
            if (_findHits.Count > 0)
            {
                GoToHit(_findHits[_findIndex], query.Trim());
            }
            else
            {
                ClearFindHighlight();
            }
        }

        private void StepFind(int direction)
        {
            if (_findHits.Count == 0)
            {
                return;
            }

            _findIndex = (_findIndex + direction + _findHits.Count) % _findHits.Count;
            FindBar.ShowCount(_findIndex + 1, _findHits.Count);
            GoToHit(_findHits[_findIndex], FindBar.Query.Trim());
        }

        private void GoToHit(ChatFindHit hit, string needle)
        {
            if (hit.MessageIndex >= _messageHosts.Count)
            {
                return;
            }

            var host = _messageHosts[hit.MessageIndex];
            if (!string.Equals(host.Id, hit.MessageId, StringComparison.Ordinal))
            {
                return;
            }

            // Прежняя подсветка снимается всегда — и в другом сообщении, и в этом же: пересборка
            // пузыря даёт чистый документ, в котором подсветим следующее совпадение.
            ClearFindHighlight();

            MaterializeHost(host);
            _stickToBottom = false;

            // После раскладки: только что построенный пузырь ещё не знает своих размеров.
            Dispatcher.BeginInvoke(() =>
            {
                FrameworkElement target = host;
                if (hit.InTools)
                {
                    ExpandTools(host);
                }
                else if (host.Child is FrameworkElement view &&
                         HighlightOccurrence(view, needle, hit.Occurrence) is { } range)
                {
                    _findHighlighted = host.Id;
                    ScrollToRange(range, host);
                    return;
                }

                ScrollToElement(target);
            }, DispatcherPriority.Loaded);
        }

        private void ClearFindHighlight()
        {
            if (_findHighlighted is { } id)
            {
                _findHighlighted = null;
                RefreshMessageView(id);
            }
        }

        private static void ExpandTools(ChatMessageHost host)
        {
            foreach (var expander in VisualDescendants(host).OfType<Expander>())
            {
                expander.IsExpanded = true;
            }
        }

        /// <summary>
        /// Подсвечивает n-е совпадение в отрисованном тексте сообщения. Совпадения, которые
        /// разметка разрезала на разные куски (жирное посреди слова), не находятся — тогда
        /// лента просто доезжает до сообщения.
        /// </summary>
        internal TextRange? HighlightOccurrence(FrameworkElement view, string needle, int occurrence)
        {
            if (needle.Length == 0)
            {
                return null;
            }

            TextRange? last = null;
            var seen = 0;
            foreach (var box in VisualDescendants(view).OfType<RichTextBox>())
            {
                foreach (var run in Runs(box.Document))
                {
                    var text = run.Text;
                    var at = 0;
                    while ((at = text.IndexOf(needle, at, StringComparison.CurrentCultureIgnoreCase)) >= 0)
                    {
                        var start = run.ContentStart.GetPositionAtOffset(at);
                        var end = start?.GetPositionAtOffset(needle.Length);
                        if (start is not null && end is not null)
                        {
                            last = new TextRange(start, end);
                            if (seen == occurrence)
                            {
                                Paint(last);
                                return last;
                            }

                            seen++;
                        }

                        at += needle.Length;
                    }
                }
            }

            if (last is not null)
            {
                Paint(last);
            }

            return last;
        }

        private void Paint(TextRange range)
        {
            if (TryFindResource("Status.WarningSurface") is Brush brush)
            {
                range.ApplyPropertyValue(TextElement.BackgroundProperty, brush);
            }
        }

        private static IEnumerable<Run> Runs(FlowDocument document)
        {
            var stack = new Stack<object>(document.Blocks.Reverse());
            while (stack.Count > 0)
            {
                switch (stack.Pop())
                {
                    case Run run:
                        yield return run;
                        break;
                    case Paragraph paragraph:
                        foreach (var inline in paragraph.Inlines.Reverse())
                        {
                            stack.Push(inline);
                        }

                        break;
                    case Span span:
                        foreach (var inline in span.Inlines.Reverse())
                        {
                            stack.Push(inline);
                        }

                        break;
                    case List list:
                        foreach (var item in list.ListItems.Reverse())
                        {
                            foreach (var block in item.Blocks.Reverse())
                            {
                                stack.Push(block);
                            }
                        }

                        break;
                    case Section section:
                        foreach (var block in section.Blocks.Reverse())
                        {
                            stack.Push(block);
                        }

                        break;
                    case Table table:
                        foreach (var group in table.RowGroups.Reverse())
                        {
                            foreach (var row in group.Rows.Reverse())
                            {
                                foreach (var cell in row.Cells.Reverse())
                                {
                                    foreach (var block in cell.Blocks.Reverse())
                                    {
                                        stack.Push(block);
                                    }
                                }
                            }
                        }

                        break;
                }
            }
        }

        private void ScrollToRange(TextRange range, FrameworkElement fallback)
        {
            var rect = range.Start.GetCharacterRect(LogicalDirection.Forward);
            if (range.Start.Parent is FrameworkContentElement { Parent: not null } &&
                FindBox(range.Start) is { } box && !rect.IsEmpty)
            {
                var point = box.TranslatePoint(rect.TopLeft, ChatScrollViewer);
                ScrollChatBy(point.Y);
                return;
            }

            ScrollToElement(fallback);
        }

        private static RichTextBox? FindBox(TextPointer pointer)
        {
            DependencyObject? node = pointer.Parent;
            while (node is not null and not RichTextBox)
            {
                node = node switch
                {
                    // FlowDocument — тоже FrameworkContentElement: его Parent и есть RichTextBox.
                    FrameworkContentElement content => content.Parent,
                    Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(node),
                    _ => LogicalTreeHelper.GetParent(node)
                };
            }

            return node as RichTextBox;
        }

        private void ScrollToElement(FrameworkElement element)
        {
            var point = element.TranslatePoint(new Point(0, 0), ChatScrollViewer);
            ScrollChatBy(point.Y);
        }

        /// <summary>Ставит найденное на треть высоты — видно и его, и что было перед ним.</summary>
        private void ScrollChatBy(double viewportY)
        {
            SmoothScroll.Cancel(ChatScrollViewer);
            var target = ChatScrollViewer.VerticalOffset + viewportY - ChatScrollViewer.ViewportHeight / 3;
            ChatScrollViewer.ScrollToVerticalOffset(Math.Max(0, target));
        }

        private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
        {
            var stack = new Stack<DependencyObject>([root]);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                yield return node;
                if (node is Visual or System.Windows.Media.Media3D.Visual3D)
                {
                    for (var i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--)
                    {
                        stack.Push(VisualTreeHelper.GetChild(node, i));
                    }
                }
            }
        }
    }
}
