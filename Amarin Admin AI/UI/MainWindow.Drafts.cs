using System.Windows.Threading;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Черновик у каждого чата (D12): набранное, цитаты и вложения остаются со своим чатом при
    /// переключении и переживают перезапуск.
    /// </summary>
    /// <remarks>
    /// Раньше набранный текст «переезжал» в открытый следом чат, а прикреплённое пропадало. Запись
    /// на диск — после паузы в наборе и в фоне по очереди: вложения бывают по нескольку мегабайт,
    /// а порядок записей одного чата обязан сохраниться, иначе поздний пустой черновик затёр бы
    /// свежий.
    /// </remarks>
    public partial class MainWindow
    {
        private readonly DispatcherTimer _draftTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
        private Task _draftWrites = Task.CompletedTask;
        private bool _restoringDraft;

        private void WireDrafts()
        {
            _draftTimer.Tick += (_, _) =>
            {
                _draftTimer.Stop();
                SaveDraft(_session);
            };
            MessageTextBox.TextChanged += (_, _) => NoteDraftChanged();
        }

        /// <summary>Поле, цитаты или вложения поменялись — черновик запишется после паузы.</summary>
        private void NoteDraftChanged()
        {
            if (_restoringDraft || _services is null)
            {
                return;
            }

            _draftTimer.Stop();
            _draftTimer.Start();
        }

        /// <summary>Ключ черновика: id сохранённого чата, а у нового — общий ключ нового чата.</summary>
        private string DraftKey(ChatSession session) =>
            _services?.ChatStore.List().Any(entry => entry.Id == session.Id) == true ? session.Id : DraftStore.NewChatKey;

        private ChatDraftContent CaptureDraft() =>
            new(MessageTextBox.Text, [.. _pendingQuotes], [.. _pendingImages], [.. _pendingFiles]);

        /// <summary>Записывает черновик чата в фоне, в очередь за прежними записями.</summary>
        private void SaveDraft(ChatSession session)
        {
            if (_services is null)
            {
                return;
            }

            var store = _services.Drafts;
            var key = DraftKey(session);
            var draft = CaptureDraft();
            _draftWrites = _draftWrites.ContinueWith(_ => store.Save(key, draft), TaskScheduler.Default);
        }

        /// <summary>Уходим из чата: его черновик — на диск сейчас же, поле — чистое для следующего.</summary>
        private void StashDraft()
        {
            if (_services is null)
            {
                return;
            }

            _draftTimer.Stop();
            SaveDraft(_session);
            _restoringDraft = true;
            try
            {
                MessageTextBox.Clear();
                ClearPendingAttachments();
            }
            finally
            {
                _restoringDraft = false;
            }
        }

        /// <summary>Открыли чат: возвращаем его черновик, если он есть.</summary>
        private void RestoreDraft(ChatSession session)
        {
            if (_services is null)
            {
                return;
            }

            // Запись этого же черновика могла ещё не доехать до диска — дождёмся её.
            _draftWrites.Wait(TimeSpan.FromSeconds(2));
            if (_services.Drafts.TryLoad(DraftKey(session)) is not { } draft)
            {
                return;
            }

            _restoringDraft = true;
            try
            {
                _pendingQuotes.Clear();
                _pendingQuotes.AddRange(draft.Quotes);
                _pendingImages.Clear();
                _pendingImages.AddRange(draft.Images);
                _pendingFiles.Clear();
                _pendingFiles.AddRange(draft.Files);
                RefreshAttachments();
                MessageTextBox.Text = draft.Text;
                MessageTextBox.CaretIndex = draft.Text.Length;
            }
            finally
            {
                _restoringDraft = false;
            }
        }

        /// <summary>Сообщение ушло: черновик этого чата больше не нужен.</summary>
        private void ForgetDraft(ChatSession session)
        {
            if (_services is null)
            {
                return;
            }

            _draftTimer.Stop();
            var store = _services.Drafts;
            var key = DraftKey(session);
            // У нового чата ключ — общий ключ нового чата: он и очищается.
            _draftWrites = _draftWrites.ContinueWith(_ => store.Delete(key), TaskScheduler.Default);
        }

        /// <summary>Выход: набранное — на диск, не дожидаясь паузы.</summary>
        private void FlushDraft()
        {
            if (_services is null)
            {
                return;
            }

            if (_draftTimer.IsEnabled)
            {
                _draftTimer.Stop();
                SaveDraft(_session);
            }

            _draftWrites.Wait(TimeSpan.FromSeconds(5));
        }
    }
}
