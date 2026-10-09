using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Меню «⋯» чата в боковой панели: переименовать, закрепить, поделиться, экспорт, отправить или
    /// перенести в другой профиль (<c>MainWindow.ChatTransfer.cs</c>), удалить. Чат
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
                OpenChatActionsMenu(actions, id, System.Windows.Controls.Primitives.PlacementMode.Bottom);
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

        /// <summary>
        /// Меню строки чата — по «⋯» или правому щелчку. Строка в выборе из нескольких — меню
        /// всего выбора: действие идёт по всем выбранным, даже переименование.
        /// </summary>
        /// <remarks>
        /// Правый щелчок по невыбранной строке снимает выбор, как в Проводнике: меню тогда про эту
        /// строку, и оставлять подсвеченными чаты, к которым оно не относится, значило бы путать.
        /// </remarks>
        private void OpenChatActionsMenu(FrameworkElement anchor, string id, System.Windows.Controls.Primitives.PlacementMode placement)
        {
            if (_selection.Contains(id) && (_selection.Count > 1 || SelectedFolders().Count > 0))
            {
                OpenSelectionMenu(anchor, placement);
                return;
            }

            ClearChatSelection();
            OpenChatsMenu(anchor, [id], [], placement);
        }

        /// <summary>Меню всего выбора: выбранные чаты и папки, выбранные целиком.</summary>
        private void OpenSelectionMenu(FrameworkElement anchor, System.Windows.Controls.Primitives.PlacementMode placement) =>
            OpenChatsMenu(anchor, SelectedChats(), SelectedFolders(), placement);

        /// <summary>
        /// Меню действий над чатами — одно на один чат и на много. «Настройки чата» — только у
        /// одного; у выбора из одних пустых папок — только «Удалить».
        /// </summary>
        /// <param name="folders">Папки, выбранные целиком: «Удалить» уносит и их.</param>
        private void OpenChatsMenu(
            FrameworkElement anchor,
            IReadOnlyList<string> ids,
            IReadOnlyList<string> folders,
            System.Windows.Controls.Primitives.PlacementMode placement)
        {
            if (_services is null || (ids.Count == 0 && folders.Count == 0))
            {
                return;
            }

            var menu = NewMenu(anchor, placement);
            if (ids.Count > 0)
            {
                AddChatActions(menu, anchor, ids, placement);
                AddTransferActions(menu, anchor, ids, placement);
                menu.Items.Add(Divider());
            }

            menu.Items.Add(MenuItemFor(
                Loc.Get("S.Common.Delete"),
                () => Detached.Run(DeleteChatsAsync(ids, folders), "delete_chats"),
                danger: true,
                icon: "Icon.Menu.Delete"));

            menu.IsOpen = true;
        }

        private void AddChatActions(
            ContextMenu menu,
            FrameworkElement anchor,
            IReadOnlyList<string> ids,
            System.Windows.Controls.Primitives.PlacementMode placement)
        {
            var single = ids.Count == 1;
            menu.Items.Add(MenuItemFor(Loc.Get("S.ChatList.Rename"), () => RenameChats(ids), icon: "Icon.Menu.Rename"));

            var pinned = PinnedIds();
            var allPinned = ids.All(pinned.Contains);
            menu.Items.Add(MenuItemFor(
                Loc.Get(allPinned ? "S.ChatList.Unpin" : "S.ChatList.Pin"),
                () => PinChats(ids, !allPinned),
                icon: allPinned ? "Icon.Menu.Unpin" : "Icon.Menu.Pin"));

            // Вложенных меню у AppMenuItem нет — выбор открывается вторым меню на том же месте.
            if (single)
            {
                var id = ids[0];
                menu.Items.Add(MenuItemFor(Loc.Get("S.ChatProfile.Title") + "…", () =>
                {
                    OpenChat(id);
                    if (id == _session.Id)
                    {
                        OpenChatSettings();
                    }
                }, icon: "Icon.Menu.ChatSettings"));
            }

            menu.Items.Add(MenuItemFor(Loc.Get("S.ChatList.MoveToFolder") + "…", () => OpenFolderPicker(anchor, ids, placement), icon: "Icon.Menu.MoveToFolder"));
            menu.Items.Add(MenuItemFor(Loc.Get("S.ChatList.Tags") + "…", () => OpenTagPicker(anchor, ids, placement), icon: "Icon.Menu.Tags"));
            var allArchived = ids.All(id => _services!.Organizer.PlacementOf(id).Archived);
            menu.Items.Add(MenuItemFor(
                Loc.Get(allArchived ? "S.ChatList.Unarchive" : "S.ChatList.Archive"),
                () => ArchiveChats(ids, !allArchived),
                icon: allArchived ? "Icon.Menu.Unarchive" : "Icon.Menu.Archive"));

            if (SharingEnabled())
            {
                menu.Items.Add(Divider());
                menu.Items.Add(MenuItemFor(Loc.Get("S.ChatList.Share"), () => ShareChats(ids), icon: "Icon.Menu.Share"));
                menu.Items.Add(MenuItemFor(
                    Loc.Get("S.ChatList.Export") + "…",
                    () =>
                    {
                        if (single)
                        {
                            WithChat(ids[0], s => OpenExportMenu(anchor, s, null, placement));
                        }
                        else
                        {
                            OpenExportManyMenu(anchor, ids, placement);
                        }
                    },
                    icon: "Icon.Menu.Export"));
            }
        }

        /// <summary>Закреплённые чаты — из описи: признак строки есть только у построенных строк.</summary>
        private HashSet<string> PinnedIds() =>
            _services!.ChatStore.List().Where(entry => entry.IsPinned).Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);

        /// <param name="icon">Ключ геометрии значка из <c>Resources.xaml</c> (<c>Icon.Menu.*</c>).</param>
        private MenuItem MenuItemFor(string header, Action invoke, bool danger = false, string? icon = null) =>
            AppMenu.Item(this, header, invoke, danger, icon);

        private Separator Divider() => AppMenu.Divider(this);

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

        /// <summary>
        /// Переименовать чат или все выбранные — одно название на всех. В поле — нынешнее
        /// название, если оно у всех одинаковое, иначе пусто.
        /// </summary>
        private void RenameChats(IReadOnlyList<string> ids)
        {
            if (_services is null || ids.Count == 0)
            {
                return;
            }

            var titles = _services.ChatStore.List().ToDictionary(item => item.Id, item => item.Title, StringComparer.Ordinal);
            string TitleOf(string id) => id == _session.Id ? _session.Title : titles.GetValueOrDefault(id) ?? "";
            var distinct = ids.Select(TitleOf).Distinct(StringComparer.Ordinal).ToList();
            var current = distinct.Count == 1 && !ChatTitle.IsDefault(distinct[0]) ? distinct[0] : "";

            OpenNameDialog(
                Loc.Get(ids.Count == 1 ? "S.ChatList.NameTitle" : "S.ChatList.NameTitleMany"),
                current,
                title =>
                {
                    if (_services is null)
                    {
                        return;
                    }

                    foreach (var id in ids)
                    {
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
                    }

                    RefreshChatList();
                });
        }

        /// <summary>Закрепить или открепить чат или все выбранные.</summary>
        private void PinChats(IReadOnlyList<string> ids, bool pinned)
        {
            if (_services is null)
            {
                return;
            }

            foreach (var id in ids)
            {
                _services.ChatStore.SetPinned(id, pinned);
            }

            RefreshChatList();
        }

        /// <summary>
        /// Спрашивает и удаляет чаты — один, несколько или выбранные вместе с папками. Вопрос —
        /// своим окном, а не <see cref="MessageBox"/>: системное рисовалось чужим стилем и в
        /// системном масштабе.
        /// </summary>
        /// <param name="folders">Папки, выбранные целиком: удаляются после своих чатов.</param>
        private async Task DeleteChatsAsync(IReadOnlyList<string> ids, IReadOnlyList<string>? folders)
        {
            folders ??= [];
            if (_services is null || (ids.Count == 0 && folders.Count == 0))
            {
                return;
            }

            string title;
            string text;
            if (folders.Count > 0)
            {
                title = Loc.Get("S.ChatList.DeleteWithFoldersTitle");
                text = Loc.Format("S.ChatList.DeleteWithFoldersConfirm", ids.Count, folders.Count);
            }
            else if (ids.Count == 1)
            {
                var entry = _services.ChatStore.List().FirstOrDefault(item => item.Id == ids[0]);
                var name = string.IsNullOrWhiteSpace(entry?.Title) ? Loc.Get("S.ChatList.ThisChat") : $"«{DisplayTitle(entry!.Title)}»";
                title = Loc.Get("S.ChatList.DeleteTitle");
                text = Loc.Format("S.ChatList.DeleteConfirm", name);
            }
            else
            {
                title = Loc.Get("S.ChatList.DeleteManyTitle");
                text = Loc.Format("S.ChatList.DeleteManyConfirm", ids.Count);
            }

            var confirmed = await ShowNoticeAsync(title, text, Loc.Get("S.Common.Delete"), Loc.Get("S.Common.Cancel"), NoticeTone.Danger);
            if (!confirmed || _services is null)
            {
                return;
            }

            RemoveChats(ids, folders);
        }

        /// <summary>
        /// Убирает чаты и папки без вопроса — после «Удалить» и после переноса в другой профиль.
        /// </summary>
        private void RemoveChats(IReadOnlyList<string> ids, IReadOnlyList<string> folders)
        {
            if (_services is null)
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
                _services.Confirmations.ForgetSession(id);
                ForgetAttention(id);
            }

            // Пачкой: опись и раскладка переписываются один раз, а не по разу на чат.
            _ = _services.ChatStore.DeleteMany(ids);
            foreach (var folder in folders)
            {
                _services.Organizer.DeleteFolder(folder);
            }

            _selection.Remove(ids);
            _selection.RemoveFolders(folders);
            if (deletingOpen)
            {
                StartNewSession(persist: false);
            }

            RefreshChatList();
        }
    }
}
