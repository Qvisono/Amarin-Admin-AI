using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Меню «⋯» чата в боковой панели: переименовать, закрепить, поделиться, экспорт, удалить. Чат
    /// адресуется по id и загружается по требованию, поэтому каждое действие работает, открыт он
    /// сейчас или нет.
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

            // Ctrl и Shift выбирают, а не открывают (D5): так же, как в Проводнике.
            var modifiers = System.Windows.Input.Keyboard.Modifiers;
            if (modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control))
            {
                ToggleChatSelection(id);
                return;
            }

            if (modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift))
            {
                SelectChatRange(id);
                return;
            }

            ClearChatSelection();
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

            menu.Items.Add(MenuItemFor(Loc.Get("S.ChatList.Rename"), () => RenameChat(id), icon: "Icon.Menu.Rename"));
            menu.Items.Add(MenuItemFor(
                Loc.Get(pinned ? "S.ChatList.Unpin" : "S.ChatList.Pin"),
                () => PinChat(id, !pinned),
                icon: pinned ? "Icon.Menu.Unpin" : "Icon.Menu.Pin"));

            // Вложенных меню у AppMenuItem нет — выбор открывается вторым меню на том же месте.
            menu.Items.Add(MenuItemFor(Loc.Get("S.ChatProfile.Title") + "…", () =>
            {
                OpenChat(id);
                if (id == _session.Id)
                {
                    OpenChatSettings();
                }
            }, icon: "Icon.Menu.ChatSettings"));
            menu.Items.Add(MenuItemFor(Loc.Get("S.ChatList.MoveToFolder") + "…", () => OpenFolderPicker(anchor, [id]), icon: "Icon.Menu.MoveToFolder"));
            menu.Items.Add(MenuItemFor(Loc.Get("S.ChatList.Tags") + "…", () => OpenTagPicker(anchor, [id]), icon: "Icon.Menu.Tags"));
            var archived = _services?.Organizer.PlacementOf(id).Archived == true;
            menu.Items.Add(MenuItemFor(
                Loc.Get(archived ? "S.ChatList.Unarchive" : "S.ChatList.Archive"),
                () => ArchiveChats([id], !archived),
                icon: archived ? "Icon.Menu.Unarchive" : "Icon.Menu.Archive"));

            if (SharingEnabled())
            {
                menu.Items.Add(Divider());
                menu.Items.Add(MenuItemFor(Loc.Get("S.ChatList.Share"), () => WithChat(id, s => ShareSession(s, null)), icon: "Icon.Menu.Share"));
                menu.Items.Add(MenuItemFor(Loc.Get("S.ChatList.Export") + "…", () => WithChat(id, s => OpenExportMenu(anchor, s, null)), icon: "Icon.Menu.Export"));
            }

            menu.Items.Add(Divider());
            menu.Items.Add(MenuItemFor(
                Loc.Get("S.Common.Delete"),
                () => Detached.Run(DeleteChatsAsync([id]), "delete_chat"),
                danger: true,
                icon: "Icon.Menu.Delete"));

            menu.IsOpen = true;
        }

        /// <param name="icon">Ключ геометрии значка из <c>Resources.xaml</c> (<c>Icon.Menu.*</c>).</param>
        private MenuItem MenuItemFor(string header, Action invoke, bool danger = false, string? icon = null)
        {
            var entry = new MenuItem
            {
                Header = header,
                Style = (Style)FindResource(danger ? "DangerMenuItem" : "AppMenuItem")
            };
            if (icon is not null)
            {
                MenuIcon.SetData(entry, (Geometry)FindResource(icon));
            }

            entry.Click += (_, _) => invoke();
            return entry;
        }

        private Separator Divider() => new() { Style = (Style)FindResource("AppMenuSeparator") };

        /// <summary>
        /// Выполняет действие над сохранённым чатом. Открытая сессия передаётся как есть, а не
        /// перечитывается, — несохранённые правки тоже учитываются.
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
                current,
                title =>
                {
                    if (_services is null)
                    {
                        return;
                    }

                    if (id == _session.Id)
                    {
                        // Копию в памяти обновляем тоже, иначе следующее сохранение вернуло бы
                        // старый заголовок.
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

        /// <summary>
        /// Спрашивает и удаляет один или несколько чатов. Вопрос — своим окном, а не
        /// <see cref="MessageBox"/>: системное рисовалось чужим стилем и в системном масштабе.
        /// </summary>
        private async Task DeleteChatsAsync(IReadOnlyList<string> ids)
        {
            if (_services is null || ids.Count == 0)
            {
                return;
            }

            string text;
            if (ids.Count == 1)
            {
                var entry = _services.ChatStore.List().FirstOrDefault(item => item.Id == ids[0]);
                var title = string.IsNullOrWhiteSpace(entry?.Title) ? Loc.Get("S.ChatList.ThisChat") : $"«{DisplayTitle(entry!.Title)}»";
                text = Loc.Format("S.ChatList.DeleteConfirm", title);
            }
            else
            {
                text = Loc.Format("S.ChatList.DeleteManyConfirm", ids.Count);
            }

            var confirmed = await ShowNoticeAsync(
                Loc.Get(ids.Count == 1 ? "S.ChatList.DeleteTitle" : "S.ChatList.DeleteManyTitle"),
                text,
                Loc.Get("S.Common.Delete"),
                Loc.Get("S.Common.Cancel"),
                NoticeTone.Danger);
            if (!confirmed || _services is null)
            {
                return;
            }

            var deletingOpen = false;
            foreach (var id in ids)
            {
                // Останавливаем ход удаляемого чата всегда, а не только когда он открыт: фоновый
                // ход по завершении сохранил бы себя обратно на диск, и чат «воскрес» бы.
                CancelTurn(id);
                deletingOpen |= id == _session.Id;
                _services.ChatStore.Delete(id);
                _services.Confirmations.ForgetSession(id);
                ForgetAttention(id);
                _selectedChats.Remove(id);
            }

            if (deletingOpen)
            {
                StartNewSession(persist: false);
            }

            RefreshChatList();
        }
    }
}
