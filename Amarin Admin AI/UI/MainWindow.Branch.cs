using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>Ветка в новый чат с выбранного ответа (1.37.0).</summary>
    public partial class MainWindow
    {
        /// <summary>
        /// Новый чат — разговор до этого ответа включительно; открывается сразу, исходный не меняется.
        /// </summary>
        /// <remarks>
        /// Ход исходного чата не мешает: копия берётся под замком чата и кончается на ответе, который
        /// уже закрыт (у пишущегося ответа ряда кнопок нет). Ветка встаёт в ту же папку, что исходный
        /// чат, — продолжение того же разговора ищут рядом с ним.
        /// </remarks>
        private void BranchFromAnswer(ChatSession source, ChatDisplayMessage answer)
        {
            if (_services is null)
            {
                return;
            }

            var title = Loc.Format("S.Chat.BranchTitle", DisplayTitle(source.Title));
            if (ChatBranchCopy.FromAnswer(source, answer.Id, title, DateTime.Now) is not { } branch)
            {
                return;
            }

            PersistCurrent();
            StashDraft();
            if (branch.IsTemporary)
            {
                // Ветка временного чата тоже временная: на диск она не ляжет, как и он.
                if (_services.Settings.TemporaryChatsUntilClose)
                {
                    _temporaryChats.Keep(branch);
                }
            }
            else
            {
                _services.ChatStore.Save(branch);
                if (_services.Organizer.PlacementOf(source.Id).FolderId is { } folder)
                {
                    _services.Organizer.MoveToFolder([branch.Id], folder);
                }
            }

            LoadSession(branch);
            RestoreDraft(branch);
            RefreshChatList();
        }
    }
}
