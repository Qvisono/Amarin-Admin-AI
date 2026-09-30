using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Что делают сочетания D8, и две клавиши вне правила Ctrl/Alt: Esc останавливает ответ,
    /// ↑ в пустом поле возвращает последнее отправленное.
    /// </summary>
    public partial class MainWindow
    {
        private void WireShortcuts() => HotkeySheet.EditRequested += () => OpenSettingsPage(NavBehavior);

        /// <summary>
        /// Esc останавливает ход открытого чата — только когда больше ему нечего делать: поле
        /// ввода пустое (иначе Esc — «передумал печатать»), лупа снята (её Esc разобран раньше),
        /// фокус не в панели поиска и не в поиске по списку, у которых Esc свой.
        /// </summary>
        private bool TryStopByEscape(KeyEventArgs e)
        {
            if (e.Key != Key.Escape || Keyboard.Modifiers != ModifierKeys.None ||
                _services is null || ChatCoveredByOverlay() || !IsBusy(_session.Id))
            {
                return false;
            }

            if (MessageTextBox.Text.Length > 0 ||
                FindBar.IsKeyboardFocusWithin ||
                SearchBox.IsKeyboardFocusWithin ||
                QuoteSuggestPopup.IsOpen)
            {
                return false;
            }

            CancelTurn(_session.Id);
            return true;
        }

        /// <summary>
        /// ↑ в пустом поле — последнее отправленное в этом чате, курсор в конце. Как в оболочке:
        /// переспросить с поправкой, не набирая заново.
        /// </summary>
        private bool TryRecallLastSent(KeyEventArgs e)
        {
            if (e.Key != Key.Up || Keyboard.Modifiers != ModifierKeys.None ||
                MessageTextBox.Text.Length > 0 || QuoteSuggestPopup.IsOpen)
            {
                return false;
            }

            if (LastSentText(_session) is not { } text)
            {
                return false;
            }

            MessageTextBox.Text = text;
            MessageTextBox.CaretIndex = text.Length;
            return true;
        }

        internal static string? LastSentText(ChatSession session)
        {
            lock (session.Gate)
            {
                for (var i = session.Messages.Count - 1; i >= 0; i--)
                {
                    var message = session.Messages[i];
                    if (message.Role == "user" && !string.IsNullOrWhiteSpace(message.Text))
                    {
                        return message.Text;
                    }
                }
            }

            return null;
        }

        private bool FocusChatSearch()
        {
            if (_sidebarCollapsed)
            {
                SetSidebarCollapsed(false);
            }

            SearchBox.Focus();
            SearchBox.SelectAll();
            return true;
        }

        private bool RegenerateLast()
        {
            if (IsBusy(_session.Id))
            {
                return false;
            }

            var last = _session.Messages.LastOrDefault();
            if (last is not { Role: "assistant" })
            {
                return false;
            }

            RegenerateAssistant(last);
            return true;
        }

        private bool CopyLastAnswer()
        {
            var last = _session.Messages.LastOrDefault(message => message.Role == "assistant" && !string.IsNullOrWhiteSpace(message.Text));
            if (last is null)
            {
                return false;
            }

            CopyMessage(last);
            return true;
        }

        /// <summary>
        /// Следующий или предыдущий чат — в том порядке, в каком они стоят в списке сейчас (с
        /// папками, сортировкой и фильтром). Новый, ещё не сохранённый чат в списке не стоит —
        /// тогда шаг ведёт к первому или последнему.
        /// </summary>
        private bool StepChat(int direction)
        {
            var ids = ChatListPanel.Children.OfType<Button>()
                .Select(button => button.Tag as string)
                .OfType<string>()
                .ToList();
            if (ids.Count == 0)
            {
                return false;
            }

            var at = ids.IndexOf(_session.Id);
            var next = at < 0
                ? (direction > 0 ? 0 : ids.Count - 1)
                : (at + direction + ids.Count) % ids.Count;
            if (ids[next] == _session.Id)
            {
                return true;
            }

            ClearChatSelection();
            OpenChat(ids[next]);
            return true;
        }
    }
}
