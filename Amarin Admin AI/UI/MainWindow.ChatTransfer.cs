using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// «Отправить» и «Переместить» в меню чата (1.33.0): копия переписки или сама переписка
    /// уходит в другой профиль программы.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Тем же порядком, что «Отправить» у ключа: пункт меню открывает второе меню на том же
    /// месте со списком профилей — вложенных меню у <c>AppMenuItem</c> нет. Других профилей нет —
    /// пункты стоят приглушёнными: меню не меняет вида от чата к чату.
    /// </para>
    /// <para>
    /// Чат, который сейчас отвечает, не отправляется и не переносится: копия посреди раунда несла
    /// бы вызовы инструментов без ответов, а перенос с отменой хода гонялся с сохранением
    /// отменённого ответа — переписка оставалась в обоих профилях. Успех молчит, как у ключей
    /// (так попросил человек), ошибка — строкой под полем ввода.
    /// </para>
    /// </remarks>
    public partial class MainWindow
    {
        /// <summary>Перенос идёт в фоне: второй не начинается, пока не кончился первый.</summary>
        private bool _transferring;

        private void AddTransferActions(
            ContextMenu menu,
            FrameworkElement anchor,
            IReadOnlyList<string> ids,
            IReadOnlyList<string> folders,
            PlacementMode placement)
        {
            var reason = TransferBlocker(ids);
            menu.Items.Add(Divider());
            menu.Items.Add(TransferItem("S.ChatList.SendTo", "Icon.Menu.SendTo", move: false));
            menu.Items.Add(TransferItem("S.ChatList.MoveTo", "Icon.Menu.MoveTo", move: true));

            MenuItem TransferItem(string key, string icon, bool move)
            {
                var item = AppMenu.Item(this, Loc.Get(key) + "…", () => OpenTransferMenu(anchor, ids, folders, placement, move),
                    icon: icon, enabled: reason is null);
                if (reason is not null)
                {
                    // Приглушённый пункт без объяснения выглядит сломанным.
                    item.ToolTip = Loc.Get(reason);
                    ToolTipService.SetShowOnDisabled(item, true);
                }

                return item;
            }
        }

        /// <summary>Почему перенос сейчас невозможен (ключ строки), или null.</summary>
        private string? TransferBlocker(IReadOnlyList<string> ids)
        {
            if (TransferTargets().Count == 0)
            {
                return "S.ChatList.NoOtherProfiles";
            }

            return _transferring || ids.Any(Turns.IsBusy) ? "S.ChatList.TransferBusy" : null;
        }

        /// <summary>Второе меню на том же месте — профили, куда отправить или перенести.</summary>
        private void OpenTransferMenu(
            FrameworkElement anchor,
            IReadOnlyList<string> ids,
            IReadOnlyList<string> folders,
            PlacementMode placement,
            bool move)
        {
            var menu = AppMenu.At(anchor, placement);
            menu.Items.Add(AppMenu.Section(this, Loc.Get(move ? "S.ChatList.MoveToSection" : "S.ChatList.SendToSection")));
            foreach (var profile in TransferTargets())
            {
                var id = profile.Id;
                menu.Items.Add(AppMenu.Item(this, profile.Name,
                    () => Detached.Run(TransferChatsAsync(ids, folders, id, move), move ? "move_chats" : "send_chats")));
            }

            menu.IsOpen = true;
        }

        private List<UserProfile> TransferTargets() => _services?.ProfileRegistry.Others() ?? [];

        /// <summary>
        /// Отправляет копии выбранных чатов в профиль или переносит их туда.
        /// </summary>
        /// <remarks>
        /// Чтение, копирование и запись — в фоне, одним пакетом (<see cref="ChatTransfer.Send"/>).
        /// Перенос — копия и удаление здесь тем же путём, что и «Удалить», но без вопроса: переписка
        /// не пропадает, а переезжает. Удаляются только чаты, которые там действительно легли и
        /// видны в списке: неудачная запись не должна стоить человеку переписки. Недописанное в
        /// поле ввода открытого чата уезжает с ним черновиком.
        /// </remarks>
        /// <param name="folders">Папки, выбранные целиком: перенесённые без остатка, они убираются, как при «Удалить».</param>
        internal async Task TransferChatsAsync(IReadOnlyList<string> ids, IReadOnlyList<string> folders, string profileId, bool move)
        {
            if (_services is not { } services || ids.Count == 0 || _transferring ||
                services.ProfileRegistry.Others().FirstOrDefault(profile => profile.Id == profileId) is not { } target)
            {
                return;
            }

            if (ids.Any(Turns.IsBusy))
            {
                ShowStatusNote(Loc.Get("S.ChatList.TransferBusy"));
                return;
            }

            var root = services.Profiles.DataRootFor(profileId);
            var store = services.ChatStore;
            var drafts = services.Drafts;
            var open = _session;
            var openDraft = move && ids.Contains(open.Id) ? CaptureDraft() : null;

            IReadOnlyList<ChatTransferOutcome> outcomes;
            _transferring = true;
            try
            {
                outcomes = await Task.Run(() => ChatTransfer.Send(
                    [.. ids.Select(id => new ChatTransferItem(
                        id,
                        id == open.Id ? () => open : () => store.TryLoad(id),
                        !move ? null : id == open.Id ? openDraft : drafts.TryLoad(id)))],
                    root,
                    keepIds: move,
                    DateTime.Now));
            }
            finally
            {
                _transferring = false;
            }

            if (move)
            {
                // Чат, в котором за время переноса начался ответ, остаётся и здесь: удаление
                // оборвало бы ход, а оборванный ответ сохранился бы обратно.
                var moved = outcomes.Where(outcome => outcome.Sent).Select(outcome => outcome.SourceId).Where(id => !Turns.IsBusy(id)).ToList();
                if (moved.Count > 0)
                {
                    if (moved.Contains(open.Id) && ReferenceEquals(open, _session))
                    {
                        MessageTextBox.Text = "";
                        ClearPendingAttachments();
                    }

                    RemoveChats(moved, moved.Count == ids.Count ? folders : []);
                }

                if (moved.Count < outcomes.Count(outcome => outcome.Sent))
                {
                    ShowStatusNote(Loc.Get("S.ChatList.TransferBusy"));
                }
            }

            if (outcomes.FirstOrDefault(outcome => !outcome.Sent) is { SourceId: not null } failure)
            {
                ShowStatusNote(Loc.Format(
                    failure.Result == ChatTransferResult.TargetUnreadable ? "S.ChatList.TransferUnreadable" : "S.ChatList.TransferFailed",
                    target.Name));
            }
        }
    }
}
