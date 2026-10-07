using System.Windows;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// «Поделиться» кодом для вставки и экспорт в открытый JSON. Оба несут всю переписку без
    /// шифрования, и переключатель на странице «Данные» их выключает.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>Длиннее этого буфер обмена уже не годится для передачи.</summary>
        private const int ShareCodeFileThreshold = 4000;

        private bool SharingEnabled() => _services?.Settings.ChatSharingEnabled != false;

        private void ShareMessage(ChatDisplayMessage message) => ShareSession(_session, message.Id);

        /// <summary>
        /// «Поделиться» у чатов в списке: один чат — его код, несколько — один код на все
        /// (<see cref="ChatShareCodec.EncodeMany"/>): вставка его в другой копии добавит их все.
        /// </summary>
        private void ShareChats(IReadOnlyList<string> ids)
        {
            if (ids.Count == 1)
            {
                WithChat(ids[0], session => ShareSession(session, null));
                return;
            }

            Detached.Run(ShareManyAsync(ids), "share_many");
        }

        private async Task ShareManyAsync(IReadOnlyList<string> ids)
        {
            if (_services is null || !SharingEnabled())
            {
                return;
            }

            var sessions = await LoadChatsAsync(ids);
            if (sessions.Count == 0)
            {
                return;
            }

            string code;
            try
            {
                code = await Task.Run(() => ChatShareCodec.EncodeMany(sessions));
            }
            catch (Exception ex)
            {
                Inform(Loc.Get("S.Share.FailedTitle"), ex.Message);
                return;
            }

            if (!TrySetClipboardText(code))
            {
                Inform(Loc.Get("S.Common.ClipboardBusyTitle"), Loc.Get("S.Share.ClipboardBusy"));
                return;
            }

            var note = Loc.Format("S.Share.CodeCopiedMany", sessions.Count, code.Length);

            // Многотысячная строка в буфере переживает не каждый мессенджер — длинный код
            // отдаём ещё и файлом, как и у одного чата.
            if (code.Length > ShareCodeFileThreshold && TrySaveShareFile(code, DisplayTitle(sessions[0].Title)) is { } path)
            {
                note += "\n\n" + Loc.Format("S.Share.SavedToFile", path);
            }

            Inform(Loc.Get("S.Share.CopiedManyTitle"), note, NoticeTone.Info);
        }

        /// <summary>
        /// Копирует код для <paramref name="session"/>. <paramref name="upToMessageId"/> = null —
        /// вся переписка; так просит меню чата в боковой панели.
        /// </summary>
        private void ShareSession(ChatSession session, string? upToMessageId)
        {
            if (_services is null || !SharingEnabled())
            {
                return;
            }

            string code;
            try
            {
                code = ChatShareCodec.Encode(session, upToMessageId);
            }
            catch (Exception ex)
            {
                Inform(Loc.Get("S.Share.FailedTitle"), ex.Message);
                return;
            }

            if (!TrySetClipboardText(code))
            {
                Inform(Loc.Get("S.Common.ClipboardBusyTitle"), Loc.Get("S.Share.ClipboardBusy"));
                return;
            }

            var upTo = upToMessageId is null
                ? session.Messages.Count
                : session.Messages.FindIndex(m => m.Id == upToMessageId) + 1;
            var note = Loc.Format("S.Share.CodeCopied", code.Length, upTo);

            // Многотысячная строка в буфере переживает не каждый мессенджер — длинный код
            // отдаём ещё и файлом.
            if (code.Length > ShareCodeFileThreshold && TrySaveShareFile(code, session.Title) is { } path)
            {
                note += "\n\n" + Loc.Format("S.Share.SavedToFile", path);
            }

            Inform(Loc.Get("S.Share.CopiedTitle"), note, NoticeTone.Info);
        }

        private void ExportMessage(ChatDisplayMessage message, FrameworkElement anchor) => OpenExportMenu(anchor, _session, message.Id);

        /// <summary>Writes <paramref name="session"/> out as plain JSON, whole or up to a message.</summary>
        private void ExportSession(ChatSession session, string? upToMessageId)
        {
            if (_services is null || !SharingEnabled())
            {
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = Loc.Get("S.Share.ExportTitle"),
                Filter = $"JSON|*.json|{Loc.Get("S.Common.AllFiles")}|*.*",
                DefaultExt = ".json",
                FileName = SafeFileName(session.Title) + ".json"
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            try
            {
                File.WriteAllText(dialog.FileName, ChatShareCodec.ExportJson(session, upToMessageId));
                Inform(Loc.Get("S.Export.SavedTitle"), dialog.FileName, NoticeTone.Info);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Inform(Loc.Get("S.Spend.ExportFailedTitle"), ex.Message);
            }
        }

        /// <summary>
        /// Сохраняет разобранные чаты как обычные свои и открывает первый — дальше они ведут себя
        /// как любые другие. Код нескольких чатов (<see cref="ChatShareCodec.BundlePrefix"/>)
        /// добавляет их все.
        /// </summary>
        private void OpenSharedSessions(IReadOnlyList<ChatSession> shared)
        {
            if (_services is null || shared.Count == 0)
            {
                return;
            }

            PersistCurrent();
            foreach (var session in shared)
            {
                // Поделиться могли посреди ответа — у нас этот ход не идёт и не дойдёт до конца.
                ChatEngine.CloseInterruptedReplies(session);
                _services.ChatStore.Save(session);
            }

            LoadSession(shared[0]);
            RefreshChatList();
        }

        private void ChatSharingToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingsUiLoading || _services is null)
            {
                return;
            }

            _services.Settings.ChatSharingEnabled = DataPage.ChatSharingToggle.IsChecked == true;
            _services.SettingsStore.Save(_services.Settings);

            // Кнопки строятся у каждого сообщения — перерисовываем ленту; чат тот же, лупа остаётся.
            RebuildTranscript(resetZoom: false);
        }

        private void ImportChatButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = Loc.Get("S.Data.Import"),
                Filter = $"{Loc.Get("S.Share.FileKind")}|*.json;*{ChatShareCodec.FileExtension}|JSON|*.json|{Loc.Get("S.Common.AllFiles")}|*.*"
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            string content;
            try
            {
                content = File.ReadAllText(dialog.FileName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Inform(Loc.Get("S.Share.OpenFailedTitle"), ex.Message);
                return;
            }

            // Одно окно выбора файла на оба формата: код «Поделиться» (одного чата или нескольких)
            // и открытый JSON.
            IReadOnlyList<ChatSession>? sessions = ChatShareCodec.LooksLikeShareCode(content)
                ? ChatShareCodec.TryDecodeAll(content)
                : ChatShareCodec.TryImportJson(content) is { } imported ? [imported] : null;

            if (sessions is null)
            {
                Inform(Loc.Get("S.Share.OpenFailedTitle"), Loc.Get("S.Share.NotAChat"));
                return;
            }

            SettingsOverlay.Visibility = Visibility.Collapsed;
            OpenSharedSessions(sessions);
        }

        private static string? TrySaveShareFile(string code, string? title)
        {
            try
            {
                var directory = Path.Combine(AppPaths.Root, "shared");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(
                    directory,
                    $"{SafeFileName(title)}-{DateTime.Now:yyyyMMdd-HHmmss}{ChatShareCodec.FileExtension}");
                File.WriteAllText(path, code);
                return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static bool TrySetClipboardText(string text) =>
            ClipboardWrite.Try(() => Clipboard.SetText(text));

        private static string SafeFileName(string? title)
        {
            var name = string.IsNullOrWhiteSpace(title) ? "chat" : title.Trim();
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalid, '-');
            }

            return name.Length > 60 ? name[..60].TrimEnd() : name;
        }
    }
}
