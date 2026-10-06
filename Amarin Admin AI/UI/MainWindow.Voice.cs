using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Голосовой ввод (D14): запись у поля ввода, распознавание на ПК или в облаке, текст — в поле.
    /// Сам текст не отправляется: распознанное человек сначала видит и правит.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>Нажатие дольше этого — «говорю, пока держу»: отпускание останавливает запись.</summary>
        private static readonly TimeSpan HoldThreshold = TimeSpan.FromMilliseconds(450);

        private MicRecorder? _recorder;
        private DateTime _micPressedAt;
        private bool _micStartedByPress;
        private readonly DispatcherTimer _voiceClock = new() { Interval = TimeSpan.FromMilliseconds(250) };
        private CancellationTokenSource? _voiceCancel;

        internal bool IsRecording => _recorder is not null;

        private void WireVoice()
        {
            _voiceClock.Tick += (_, _) =>
            {
                if (_recorder is { } recorder)
                {
                    VoiceTimer.Text = recorder.Elapsed.ToString(@"m\:ss", CultureInfo.InvariantCulture);
                }
            };
            ShowMicIdle();

            // Движок, модель, язык и ключи меняются в настройках — по их закрытию и пересчёт.
            SettingsOverlay.IsVisibleChanged += (_, e) =>
            {
                if (!(bool)e.NewValue)
                {
                    UpdateMicAvailability();
                }
            };
        }

        private void MicButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _micPressedAt = DateTime.UtcNow;
            _micStartedByPress = false;
            if (!IsRecording)
            {
                _micStartedByPress = StartRecording();
            }
        }

        private void MicButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // Долгое нажатие — рация: отпустил, и запись ушла на распознавание.
            if (IsRecording && _micStartedByPress && DateTime.UtcNow - _micPressedAt >= HoldThreshold)
            {
                e.Handled = true;
                _micStartedByPress = false;
                Detached.Run(FinishRecordingAsync(), "voice_finish");
            }
        }

        private void MicButton_Click(object sender, RoutedEventArgs e)
        {
            // Короткий щелчок: запись уже пошла по нажатию и идёт дальше; второй — останавливает.
            if (_micStartedByPress)
            {
                _micStartedByPress = false;
                return;
            }

            ToggleRecording();
        }

        /// <summary>Сочетание клавиш и щелчок: начать или закончить запись.</summary>
        internal bool ToggleRecording()
        {
            if (IsRecording)
            {
                Detached.Run(FinishRecordingAsync(), "voice_finish");
                return true;
            }

            return StartRecording();
        }

        /// <summary>
        /// Показывает микрофон, только если есть чем распознать. Зовётся при подключении служб и
        /// при закрытии настроек: там меняются движок, модель, язык и ключи.
        /// </summary>
        private void UpdateMicAvailability() =>
            MicHost.Visibility = IsVoiceAvailable() || IsRecording ? Visibility.Visible : Visibility.Collapsed;

        private bool IsVoiceAvailable() =>
            _services is { } services && OperatingSystem.IsWindows() &&
            VoiceAvailability.IsAvailable(
                services.Settings,
                VoiceAvailability.SpeechCulture(services.Settings, LanguageManager.Current),
                culture => LocalSpeech.Find(culture) is not null,
                model => !string.IsNullOrWhiteSpace(services.Keys.CredentialFor(model, null).Secret));

        private bool StartRecording()
        {
            if (_services is null || !OperatingSystem.IsWindows())
            {
                return false;
            }

            // Кнопки нет, но сочетание клавиш осталось: вместо записи впустую — куда идти настраивать.
            if (MicHost.Visibility != Visibility.Visible)
            {
                Detached.Run(OfferVoiceSetupAsync(), "voice_setup");
                return false;
            }

            var recorder = new MicRecorder();
            if (!recorder.Start())
            {
                recorder.Dispose();
                ShowTransientNotice(_session.Id, Loc.Get("S.Voice.NoMic"));
                return false;
            }

            recorder.LevelChanged += level =>
            {
                VoiceLevel.Opacity = 0.18 + level * 0.3;
                VoiceLevelScale.ScaleX = VoiceLevelScale.ScaleY = 1 + level * 0.45;
            };
            recorder.LimitReached += () => Detached.Run(FinishRecordingAsync(), "voice_limit");
            _recorder = recorder;
            VoiceTimer.Text = "0:00";
            VoiceTimer.Visibility = Visibility.Visible;
            MicButton.SetResourceReference(BackgroundProperty, "Status.DangerStrong");
            MicButton.SetResourceReference(ForegroundProperty, "Text.Primary");
            MicButton.SetResourceReference(ToolTipProperty, "S.Voice.Recording");
            _voiceClock.Start();
            return true;
        }

        /// <summary>Esc во время записи: всё записанное выбрасывается.</summary>
        internal bool CancelRecording()
        {
            _voiceCancel?.Cancel();
            if (_recorder is not { } recorder)
            {
                return false;
            }

            _recorder = null;
            recorder.Dispose();
            ShowMicIdle();
            ShowTransientNotice(_session.Id, Loc.Get("S.Voice.Cancelled"));
            return true;
        }

        private void ShowMicIdle()
        {
            _voiceClock.Stop();
            VoiceTimer.Visibility = Visibility.Collapsed;
            VoiceLevel.Opacity = 0;
            MicButton.SetResourceReference(BackgroundProperty, "Bg.Raised");
            MicButton.SetResourceReference(ForegroundProperty, "Text.Dim");
            MicButton.SetResourceReference(ToolTipProperty, "S.Voice.Button");
        }

        private async Task FinishRecordingAsync()
        {
            if (_recorder is not { } recorder || _services is null)
            {
                return;
            }

            _recorder = null;
            var wav = recorder.Stop();
            recorder.Dispose();
            ShowMicIdle();

            // Меньше трети секунды — щелчок мимо, а не фраза: не тратим на него запрос.
            if (wav.Length < 44 + WavFile.BytesPerSecond / 3)
            {
                ShowTransientNotice(_session.Id, Loc.Get("S.Voice.TooShort"));
                return;
            }

            var sessionId = _session.Id;
            _voiceCancel?.Cancel();
            var cancel = _voiceCancel = new CancellationTokenSource();
            ShowComposerNotice(Loc.Get("S.Voice.Recognizing"));
            try
            {
                var text = await RecognizeAsync(wav, cancel.Token);
                if (cancel.IsCancellationRequested)
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    ShowTransientNotice(sessionId, Loc.Get("S.Voice.Nothing"));
                    return;
                }

                ClearComposerNotice(sessionId);
                if (sessionId == _session.Id)
                {
                    InsertRecognized(text);
                }
            }
            catch (OperationCanceledException)
            {
                ClearComposerNotice(sessionId);
            }
            catch (VoiceUnavailableException ex)
            {
                ClearComposerNotice(sessionId);
                await ShowNoticeAsync(Loc.Get("S.Voice.UnavailableTitle"), ex.Message, Loc.Get("S.Common.Close"), null, NoticeTone.Info);
            }
            catch (Exception ex) when (ex is HttpRequestException or VeniceApiException or TimeoutException or InvalidOperationException)
            {
                ClearComposerNotice(sessionId);
                ShowTransientNotice(sessionId, Loc.Format("S.Voice.Failed", ex.Message));
            }
        }

        /// <summary>Распознанное — в поле, на место каретки, с пробелом, если нужен.</summary>
        private void InsertRecognized(string text)
        {
            var box = MessageTextBox;
            var caret = box.CaretIndex;
            var before = box.Text[..caret];
            var joined = (before.Length > 0 && !char.IsWhiteSpace(before[^1]) ? " " : "") + text.Trim();
            box.Text = before + joined + box.Text[caret..];
            box.CaretIndex = caret + joined.Length;
            FocusMessageInput();
        }

        private sealed class VoiceUnavailableException(string message) : Exception(message);

        private async Task OfferVoiceSetupAsync()
        {
            var open = await ShowNoticeAsync(
                Loc.Get("S.Voice.UnavailableTitle"),
                Loc.Get("S.Voice.SetupText"),
                Loc.Get("S.Voice.SetupOpen"),
                Loc.Get("S.Common.Close"),
                NoticeTone.Info);
            if (open)
            {
                OpenSettings(SettingsUi.NavGeneral);
                SettingsDrill.Open(GeneralPage.GeneralVoiceSub, GeneralPage.VoiceLinkRow);
            }
        }

        /// <summary>
        /// Где распознавать — по настройке: «Авто» берёт распознаватель Windows, если он есть для
        /// языка, иначе облако. Ни того, ни другого — понятный отказ с тем, что поправить.
        /// </summary>
        private async Task<string> RecognizeAsync(byte[] wav, CancellationToken cancellationToken)
        {
            var settings = _services!.Settings;
            var culture = VoiceAvailability.SpeechCulture(settings, LanguageManager.Current);

            if (settings.VoiceEngine != VoiceEngine.Cloud && OperatingSystem.IsWindows() && LocalSpeech.Find(culture) is { } recognizer)
            {
                return await LocalSpeech.RecognizeAsync(wav, recognizer, cancellationToken);
            }

            if (settings.VoiceEngine == VoiceEngine.Local)
            {
                var installed = OperatingSystem.IsWindows() ? LocalSpeech.Languages() : [];
                throw new VoiceUnavailableException(Loc.Format(
                    "S.Voice.NoLocal",
                    culture.DisplayName,
                    installed.Count == 0 ? "—" : string.Join(", ", installed.Select(item => item.Name))));
            }

            var model = settings.VoiceModel?.Trim();
            if (string.IsNullOrEmpty(model))
            {
                throw new VoiceUnavailableException(Loc.Format("S.Voice.NoCloudModel", culture.DisplayName));
            }

            var credential = _services.Keys.CredentialFor(model, null);
            if (string.IsNullOrWhiteSpace(credential.Secret))
            {
                throw new VoiceUnavailableException(Loc.Get("S.Voice.NoKey"));
            }

            return await _services.Venice.TranscribeAsync(wav, model, culture.TwoLetterISOLanguageName, credential, cancellationToken);
        }
    }
}
