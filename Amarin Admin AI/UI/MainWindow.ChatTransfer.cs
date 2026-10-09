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
    /// Тем же порядком, что «Отправить» у ключа: пункт меню открывает второе меню на том же
    /// месте со списком профилей — вложенных меню у <c>AppMenuItem</c> нет. Других профилей нет —
    /// пункты стоят приглушёнными: меню не меняет вида от чата к чату.
    /// </remarks>
    public partial class MainWindow
    {
        private void AddTransferActions(
            ContextMenu menu,
            FrameworkElement anchor,
            IReadOnlyList<string> ids,
            PlacementMode placement)
        {
            var can = TransferTargets().Count > 0;
            menu.Items.Add(Divider());
            menu.Items.Add(TransferItem("S.ChatList.SendTo", "Icon.Menu.SendTo", move: false));
            menu.Items.Add(TransferItem("S.ChatList.MoveTo", "Icon.Menu.MoveTo", move: true));

            MenuItem TransferItem(string key, string icon, bool move)
            {
                var item = AppMenu.Item(this, Loc.Get(key) + "…", () => OpenTransferMenu(anchor, ids, placement, move), icon: icon, enabled: can);
                if (!can)
                {
                    // Приглушённый пункт без объяснения выглядит сломанным.
                    item.ToolTip = Loc.Get("S.ChatList.NoOtherProfiles");
                    ToolTipService.SetShowOnDisabled(item, true);
                }

                return item;
            }
        }

        /// <summary>Второе меню на том же месте — профили, куда отправить или перенести.</summary>
        private void OpenTransferMenu(FrameworkElement anchor, IReadOnlyList<string> ids, PlacementMode placement, bool move)
        {
            var menu = AppMenu.At(anchor, placement);
            menu.Items.Add(AppMenu.Section(this, Loc.Get(move ? "S.ChatList.MoveToSection" : "S.ChatList.SendToSection")));
            foreach (var profile in TransferTargets())
            {
                var id = profile.Id;
                menu.Items.Add(AppMenu.Item(this, profile.Name, () => TransferChats(ids, id, move)));
            }

            menu.IsOpen = true;
        }

        /// <summary>Профили, кроме открытого: в свой профиль переписка и так входит.</summary>
        /// <remarks>
        /// Открытый профиль исключён не ради порядка: его чаты держит в памяти своё хранилище,
        /// и второе хранилище на той же папке затёрло бы его правки.
        /// </remarks>
        private List<UserProfile> TransferTargets() =>
            _services is null
                ? []
                : [.. _services.ProfileRegistry.Profiles.Where(profile => profile.Id != _services.ProfileRegistry.ActiveProfileId)];

        /// <summary>
        /// Отправляет копии выбранных чатов в профиль или переносит их туда.
        /// </summary>
        /// <remarks>
        /// Перенос — копия и удаление здесь тем же путём, что и «Удалить», но без вопроса: переписка
        /// не пропадает, а переезжает. Удаляются только чаты, которые там действительно легли:
        /// неудачная запись не должна стоить человеку переписки.
        /// </remarks>
        internal void TransferChats(IReadOnlyList<string> ids, string profileId, bool move)
        {
            if (_services is null || ids.Count == 0 ||
                _services.ProfileRegistry.Profiles.FirstOrDefault(profile => profile.Id == profileId) is not { } target ||
                profileId == _services.ProfileRegistry.ActiveProfileId)
            {
                return;
            }

            var root = _services.Profiles.DataRootFor(profileId);
            var moved = new List<string>();
            var sent = 0;
            var failed = 0;
            foreach (var id in ids)
            {
                // Ход переносимого чата обрывается раньше копии: иначе он дописывал бы ответ в
                // переписку, которой здесь вот-вот не станет. Копия закроет оборванный ответ сама.
                if (move)
                {
                    CancelTurn(id);
                }

                var session = FindTurn(id)?.Session ?? (id == _session.Id ? _session : _services.ChatStore.TryLoad(id));
                if (session is null)
                {
                    continue;
                }

                var outcome = ChatTransfer.Send(session, root, keepId: move);
                if (!outcome.Sent)
                {
                    failed++;
                    continue;
                }

                sent++;
                if (move)
                {
                    moved.Add(id);
                }
            }

            if (moved.Count > 0)
            {
                RemoveChats(moved, []);
            }

            if (failed > 0)
            {
                ShowStatusNote(Loc.Format("S.ChatList.TransferFailed", target.Name));
            }
            else if (sent > 0)
            {
                ShowStatusNote(Loc.Format(move ? "S.ChatList.MovedNote" : "S.ChatList.SentNote", target.Name, sent));
            }
        }
    }
}
