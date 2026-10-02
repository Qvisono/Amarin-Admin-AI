using System.Windows;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Sharing a chat as a pasteable code and exporting it as plain JSON. Both payloads are
    /// unencrypted and contain the full conversation — the Data Controls toggle turns them off.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>Longer than this and the clipboard stops being a sensible transport.</summary>
        private const int ShareCodeFileThreshold = 4000;

        private bool SharingEnabled() => _services?.Settings.ChatSharingEnabled != false;

        private void ShareMessage(ChatDisplayMessage message) => ShareSession(_session, message.Id);

        /// <summary>
        /// Copies a share code for <paramref name="session"/>. A null <paramref name="upToMessageId"/>
        /// shares the whole conversation, which is what the sidebar's chat menu asks for.
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

            // A multi-thousand character clipboard payload does not survive every chat app,
            // so hand over a file as well once it gets long.
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
        /// Opens a decoded chat as a normal local conversation, saved to this profile's store
        /// so it behaves exactly like any other chat from then on.
        /// </summary>
        private void OpenSharedSession(ChatSession shared)
        {
            if (_services is null)
            {
                return;
            }

            PersistCurrent();

            // Поделиться могли посреди ответа — у нас этот ход не идёт и не дойдёт до конца.
            ChatEngine.CloseInterruptedReplies(shared);
            _services.ChatStore.Save(shared);
            LoadSession(shared);
            RefreshChatList();
        }

        private void ChatSharingToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingsUiLoading || _services is null)
            {
                return;
            }

            _services.Settings.ChatSharingEnabled = ChatSharingToggle.IsChecked == true;
            _services.SettingsStore.Save(_services.Settings);

            // The buttons are built per message, so redraw the transcript to apply the change —
            // the same chat, so the zoom stays.
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

            // One file dialog handles both formats: a share code and a plain JSON export.
            var session = ChatShareCodec.LooksLikeShareCode(content)
                ? ChatShareCodec.TryDecode(content)
                : ChatShareCodec.TryImportJson(content);

            if (session is null)
            {
                Inform(Loc.Get("S.Share.OpenFailedTitle"), Loc.Get("S.Share.NotAChat"));
                return;
            }

            SettingsOverlay.Visibility = Visibility.Collapsed;
            OpenSharedSession(session);
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
