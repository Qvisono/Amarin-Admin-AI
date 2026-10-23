using System.Globalization;
using System.Text;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.UI
{
    /// <summary>
    /// Запись совещания или видео во вложениях (1.37.0): звук расшифровывается сразу при вложении и
    /// прикрепляется текстом, из которого модель пишет протокол.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Сам звук в чат не попадает: вложения хранятся в переписке, час записи — это сотня мегабайт, а
    /// модели чата звук не читают. Расшифровка — тем же движком, что голосовой ввод
    /// (<see cref="RecognizeAsync"/>: на этом ПК или в облаке, по настройке), частями по
    /// <see cref="WavFile.MaxLength"/> — столько принимает распознавание. У каждой части в тексте
    /// метка времени её начала: по ним протокол ссылается на место в записи.
    /// </para>
    /// <para>
    /// Перекодированный звук лежит во временном файле только на время расшифровки. Esc и смена
    /// чата расшифровку отменяют: текст для другого чата в этот попасть не должен.
    /// </para>
    /// </remarks>
    public partial class MainWindow
    {
        /// <summary>Дольше — уже не совещание, а архив: часы облачной расшифровки по ошибке не тратим.</summary>
        private static readonly TimeSpan MaxMediaLength = TimeSpan.FromHours(4);

        private CancellationTokenSource? _transcribeCancel;

        /// <summary>Esc во время расшифровки — отменить её.</summary>
        internal bool CancelTranscription()
        {
            if (_transcribeCancel is not { IsCancellationRequested: false } cancel)
            {
                return false;
            }

            cancel.Cancel();
            return true;
        }

        private async Task TranscribeMediaAsync(string path)
        {
            if (_services is null || !OperatingSystem.IsWindows())
            {
                return;
            }

            var name = Path.GetFileName(path);
            if (_pendingFiles.Count >= MaxAttachedFiles)
            {
                ShowTransientNotice(_session.Id, Loc.Format("S.Attach.TooManyFiles", MaxAttachedFiles));
                return;
            }

            if (MicHost.Visibility != System.Windows.Visibility.Visible)
            {
                await OfferVoiceSetupAsync();
                return;
            }

            _transcribeCancel?.Cancel();
            var cancel = _transcribeCancel = new CancellationTokenSource();
            var sessionId = _session.Id;
            string? wav = null;

            // Итог — строкой под полем после того, как снята строка хода расшифровки.
            string? outcome = null;
            try
            {
                ShowComposerNotice(Loc.Format("S.Transcribe.Converting", name));
                wav = await MediaAudio.ToWavAsync(path, cancel.Token);
                var length = TimeSpan.FromSeconds(Math.Max(0, new FileInfo(wav).Length - 44) / (double)WavFile.BytesPerSecond);
                if (length > MaxMediaLength)
                {
                    outcome = Loc.Format("S.Transcribe.TooLong", name);
                    return;
                }

                var parts = Math.Max(1, (int)Math.Ceiling(length / WavFile.MaxLength));
                var text = new StringBuilder();
                var part = 0;
                await using (var stream = File.OpenRead(wav))
                {
                    foreach (var (start, piece) in WavFile.Pieces(stream, WavFile.MaxLength))
                    {
                        if (_session.Id != sessionId)
                        {
                            cancel.Cancel();
                        }

                        cancel.Token.ThrowIfCancellationRequested();
                        part++;
                        ShowComposerNotice(Loc.Format("S.Transcribe.Progress", name, part, parts));
                        var words = (await RecognizeAsync(piece, cancel.Token)).Trim();
                        if (words.Length > 0)
                        {
                            text.Append('[').Append(Stamp(start)).Append("] ").Append(words).Append("\n\n");
                        }
                    }
                }

                if (_session.Id != sessionId || cancel.IsCancellationRequested)
                {
                    return;
                }

                if (text.Length == 0)
                {
                    outcome = Loc.Format("S.Transcribe.Nothing", name);
                    return;
                }

                var bytes = Encoding.UTF8.GetBytes(text.ToString().TrimEnd());
                _attachmentNotes.Clear();
                _pendingFiles.Add(new FileAttachment(
                    Convert.ToBase64String(bytes),
                    "text/plain",
                    Loc.Format("S.Transcribe.FileName", Path.GetFileNameWithoutExtension(path)),
                    bytes.Length));
                WarmPendingDocuments();
                RefreshAttachments();
                _fox?.AttachmentAdded();
            }
            catch (OperationCanceledException)
            {
                outcome = Loc.Get("S.Transcribe.Cancelled");
            }
            catch (InvalidDataException)
            {
                outcome = Loc.Format("S.Transcribe.CannotRead", name);
            }
            catch (VoiceUnavailableException ex)
            {
                ClearComposerNotice(sessionId);
                await ShowNoticeAsync(Loc.Get("S.Voice.UnavailableTitle"), ex.Message, Loc.Get("S.Common.Close"), null, NoticeTone.Info);
            }
            catch (Exception ex) when (ex is HttpRequestException or VeniceApiException or TimeoutException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                outcome = Loc.Format("S.Transcribe.Failed", name, ex.Message);
            }
            finally
            {
                if (wav is not null)
                {
                    MediaAudio.TryDelete(wav);
                }

                if (ReferenceEquals(_transcribeCancel, cancel))
                {
                    _transcribeCancel = null;
                }

                cancel.Dispose();
                ClearComposerNotice(sessionId);
                if (outcome is not null)
                {
                    ShowTransientNotice(sessionId, outcome);
                }
            }
        }

        /// <summary>Метка части: «0:05:00».</summary>
        private static string Stamp(TimeSpan start) => start.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);
    }
}
