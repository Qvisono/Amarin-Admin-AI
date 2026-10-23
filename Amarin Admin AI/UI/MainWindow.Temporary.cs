using System.Windows;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Временные чаты (1.37.0): не пишутся на диск, не попадают в поиск, архив и копии.
    /// </summary>
    /// <remarks>
    /// Запись охраняет само хранилище (<see cref="ChatStore.Save"/>), здесь — только то, что видит
    /// человек: плашка посреди пустого чата, отметка у поля ввода и срок жизни. В режиме «до
    /// закрытия программы» временный чат стоит в своей группе списка и открывается из неё, пока
    /// программа не закрыта; в режиме «сразу» уход из него — конец разговора, как в веб-чатах.
    /// </remarks>
    public partial class MainWindow
    {
        private readonly TemporaryChats _temporaryChats = new();

        /// <summary>Плашка посреди пустого чата: сделать его временным или вернуть обычным.</summary>
        private void TemporaryChatPlaque_Click(object sender, RoutedEventArgs e)
        {
            if (_services is null || _session.Messages.Count > 0)
            {
                RefreshTemporaryUi();
                return;
            }

            // Черновик пустого чата лежит под общим ключом нового чата и пишется на диск; у
            // временного он только в памяти. Набранное в поле остаётся и ложится под новый ключ.
            ForgetDraft(_session);
            _session.IsTemporary = !_session.IsTemporary;
            if (_session.IsTemporary && _services.Settings.TemporaryChatsUntilClose)
            {
                _temporaryChats.Keep(_session);
            }
            else
            {
                _temporaryChats.Remove(_session.Id);
            }

            SaveDraft(_session);
            RefreshTemporaryUi();
            FocusMessageInput();
        }

        /// <summary>Плашка — у пустого чата, отметка у поля — у временного с сообщениями.</summary>
        private void RefreshTemporaryUi()
        {
            if (TemporaryChatPlaque is null || TemporaryChatChip is null)
            {
                return;
            }

            var empty = _session.Messages.Count == 0;
            TemporaryChatPlaque.IsChecked = _session.IsTemporary;
            TemporaryChatPlaque.Visibility = empty && _services is not null ? Visibility.Visible : Visibility.Collapsed;
            TemporaryChatChip.Visibility = _session.IsTemporary && !empty ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Человек уходит из чата в другой. Временный без сообщений и временный в режиме «сразу»
        /// исчезают; в режиме «до закрытия» он остаётся в своей группе списка.
        /// </summary>
        private void LeaveTemporary(ChatSession leaving)
        {
            if (_services is null || !leaving.IsTemporary)
            {
                return;
            }

            if (_services.Settings.TemporaryChatsUntilClose && leaving.Messages.Count > 0)
            {
                return;
            }

            DiscardTemporary(leaving.Id);
        }

        /// <summary>
        /// Сменился срок жизни временных чатов: при «исчезает сразу» убираются те, из которых
        /// человек уже ушёл; открытый остаётся до ухода из него.
        /// </summary>
        private void OnTemporaryModeChanged()
        {
            if (_services is null)
            {
                return;
            }

            if (_services.Settings.TemporaryChatsUntilClose)
            {
                if (_session.IsTemporary)
                {
                    _temporaryChats.Keep(_session);
                }
            }
            else
            {
                foreach (var id in _temporaryChats.Ids.Where(id => id != _session.Id))
                {
                    DiscardTemporary(id);
                }

                _temporaryChats.Remove(_session.Id);
            }

            RefreshChatList();
        }

        /// <summary>Временные чаты уходящего профиля: у другого профиля их нет.</summary>
        private void DiscardTemporaryChats()
        {
            foreach (var id in _temporaryChats.Ids)
            {
                DiscardTemporary(id);
            }
        }

        /// <summary>Временный чат уходит насовсем: ход, вопросы, метка, агенты и черновик — с ним.</summary>
        private void DiscardTemporary(string id)
        {
            if (_services is null)
            {
                return;
            }

            CancelTurn(id);
            _services.Confirmations.ForgetSession(id);
            ForgetAttention(id);
            _services.AgentBook.Forget(id);
            _services.Drafts.Note(id, null);
            _temporaryChats.Remove(id);
        }
    }
}
