using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// The per-chat "⋯" menu in the sidebar: rename, pin, share, export and delete. The chat a
    /// menu acts on is addressed by id and loaded on demand, so every action works whether or
    /// not that chat is the one currently open.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>
        /// Один обработчик нажатий на всю панель списка: открыть чат или показать его меню «⋯».
        /// </summary>
        /// <remarks>
        /// Прежде каждая строка подписывалась сама, а чтобы добраться до кнопки «⋯» внутри
        /// шаблона, приходилось звать <c>ApplyTemplate</c> — то есть разворачивать шаблон каждой
        /// строки в визуалы немедленно, вместо того чтобы дать WPF сделать это при раскладке.
        /// Нажатие на «⋯» всплывает сюда же, и отличить его можно по источнику события.
        /// </remarks>
        private void ChatListPanel_Click(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is not DependencyObject origin || FindChatRow(origin) is not { } row)
            {
                return;
            }

            if (row.Tag is not string id)
            {
                return;
            }

            // Иначе нажатие дойдёт и до строки, и чат откроется за спиной у меню.
            e.Handled = true;

            if (!ReferenceEquals(origin, row) && origin is Button actions)
            {
                OpenChatActionsMenu(actions, id, ChatRowState.GetIsPinned(row));
                return;
            }

            OpenChat(id);
        }

        /// <summary>Строка списка, внутри которой нажали. Узнаётся по идентификатору чата в Tag.</summary>
        private Button? FindChatRow(DependencyObject? node)
        {
            while (node is not null && !ReferenceEquals(node, ChatListPanel))
            {
                if (node is Button { Tag: string } row)
                {
                    return row;
                }

                node = VisualTreeHelper.GetParent(node);
            }

            return null;
        }

        private void OpenChatActionsMenu(Button anchor, string id, bool pinned)
        {
            var menu = new ContextMenu
            {
                PlacementTarget = anchor,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                Style = (Style)FindResource("AppContextMenu")
            };

            menu.Items.Add(MenuItemFor(Loc.Get("S.ChatList.Rename"), () => RenameChat(id)));
            menu.Items.Add(MenuItemFor(pinned ? Loc.Get("S.ChatList.Unpin") : Loc.Get("S.ChatList.Pin"), () => PinChat(id, !pinned)));

            if (SharingEnabled())
            {
                menu.Items.Add(Divider());
                menu.Items.Add(MenuItemFor(Loc.Get("S.ChatList.Share"), () => WithChat(id, s => ShareSession(s, null))));
                menu.Items.Add(MenuItemFor(Loc.Get("S.ChatList.ExportJson"), () => WithChat(id, s => ExportSession(s, null))));
            }

            menu.Items.Add(Divider());
            menu.Items.Add(MenuItemFor(Loc.Get("S.Common.Delete"), () => DeleteChat(id), danger: true));

            menu.IsOpen = true;
        }

        private MenuItem MenuItemFor(string header, Action invoke, bool danger = false)
        {
            var entry = new MenuItem
            {
                Header = header,
                Style = (Style)FindResource(danger ? "DangerMenuItem" : "AppMenuItem")
            };
            entry.Click += (_, _) => invoke();
            return entry;
        }

        private Separator Divider() => new() { Style = (Style)FindResource("AppMenuSeparator") };

        /// <summary>
        /// Runs an action against a stored chat. The open session is passed through as-is rather
        /// than re-read, so unsaved edits are included.
        /// </summary>
        private void WithChat(string id, Action<ChatSession> action)
        {
            if (_services is null)
            {
                return;
            }

            var session = id == _session.Id ? _session : _services.ChatStore.TryLoad(id);
            if (session is null)
            {
                RefreshChatList();
                return;
            }

            action(session);
        }

        private void RenameChat(string id)
        {
            if (_services is null)
            {
                return;
            }

            var current = id == _session.Id
                ? _session.Title
                : _services.ChatStore.Search("").FirstOrDefault(item => item.Id == id)?.Title ?? "";

            OpenNameDialog(
                Loc.Get("S.ChatList.NameTitle"),
                Loc.Get("S.ChatList.NameDesc"),
                current,
                title =>
                {
                    if (_services is null)
                    {
                        return;
                    }

                    if (id == _session.Id)
                    {
                        // Keep the in-memory copy in step, or the next save would put the old
                        // title straight back.
                        _session.Title = title.Trim();
                        _services.ChatStore.Save(_session);
                    }
                    else
                    {
                        _services.ChatStore.Rename(id, title);
                    }

                    RefreshChatList();
                });
        }

        private void PinChat(string id, bool pinned)
        {
            if (_services is null)
            {
                return;
            }

            _services.ChatStore.SetPinned(id, pinned);
            RefreshChatList();
        }

        private void DeleteChat(string id)
        {
            if (_services is null)
            {
                return;
            }

            var entry = _services.ChatStore.Search("").FirstOrDefault(item => item.Id == id);
            var title = string.IsNullOrWhiteSpace(entry?.Title) ? Loc.Get("S.ChatList.ThisChat") : $"«{entry!.Title}»";
            var answer = MessageBox.Show(
                this,
                Loc.Format("S.ChatList.DeleteConfirm", title),
                Title,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes)
            {
                return;
            }

            // Останавливаем ход удаляемого чата всегда, а не только когда он открыт: фоновый
            // ход по завершении сохранил бы себя обратно на диск, и чат «воскрес» бы.
            CancelTurn(id);

            var deletingOpen = id == _session.Id;
            _services.ChatStore.Delete(id);
            ForgetAttention(id);

            if (deletingOpen)
            {
                StartNewSession(persist: false);
            }

            RefreshChatList();
        }
    }
}
