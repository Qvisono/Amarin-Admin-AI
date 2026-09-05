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
                MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!TrySetClipboardText(code))
            {
                MessageBox.Show(
                    this,
                    "Не удалось записать код в буфер обмена — его удерживает другое приложение.",
                    Title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var upTo = upToMessageId is null
                ? session.Messages.Count
                : session.Messages.FindIndex(m => m.Id == upToMessageId) + 1;
            var note = $"Код скопирован в буфер обмена ({code.Length} символов, сообщений: {upTo}).\n" +
                       "Вставьте его в поиск в боковой панели другого экземпляра, чтобы открыть чат.";

            // A multi-thousand character clipboard payload does not survive every chat app,
            // so hand over a file as well once it gets long.
            if (code.Length > ShareCodeFileThreshold && TrySaveShareFile(code, session.Title) is { } path)
            {
                note += $"\n\nКод длинный, поэтому также сохранён в файл:\n{path}";
            }

            MessageBox.Show(this, note, Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ExportMessage(ChatDisplayMessage message) => ExportSession(_session, message.Id);

        /// <summary>Writes <paramref name="session"/> out as plain JSON, whole or up to a message.</summary>
        private void ExportSession(ChatSession session, string? upToMessageId)
        {
            if (_services is null || !SharingEnabled())
            {
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Экспорт диалога",
                Filter = "JSON|*.json|Все файлы|*.*",
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
                MessageBox.Show(
                    this,
                    "Диалог сохранён:\n" + dialog.FileName,
                    Title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
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

            // The buttons are built per message, so redraw the transcript to apply the change.
            RenderSession();
        }

        private void ImportChatButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Импорт чата",
                Filter = $"Чаты Amarin|*.json;*{ChatShareCodec.FileExtension}|JSON|*.json|Все файлы|*.*"
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
                MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // One file dialog handles both formats: a share code and a plain JSON export.
            var session = ChatShareCodec.LooksLikeShareCode(content)
                ? ChatShareCodec.TryDecode(content)
                : ChatShareCodec.TryImportJson(content);

            if (session is null)
            {
                MessageBox.Show(
                    this,
                    "Файл не распознан как чат Amarin.",
                    Title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
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

        private static bool TrySetClipboardText(string text)
        {
            // The clipboard is a shared OS resource; another process can hold it briefly.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Clipboard.SetText(text);
                    return true;
                }
                catch (System.Runtime.InteropServices.ExternalException)
                {
                    Thread.Sleep(60);
                }
            }

            return false;
        }

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
