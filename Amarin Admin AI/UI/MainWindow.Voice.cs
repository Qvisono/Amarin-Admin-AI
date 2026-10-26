using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
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

        /// <summary>Подсказка кнопки во время записи: время записи, обновляется каждую четверть секунды.</summary>
        private ToolTip? _micTip;

        /// <summary>Высота точки записи в тишине и на полной громкости, точек.</summary>
        private const double DotLow = 3;
        private const double DotHigh = 14;

        /// <summary>Крайние точки ниже средней: ряд читается волной, а не забором.</summary>
        private static readonly double[] DotWeights = [0.55, 0.8, 1, 0.8, 0.55];

        internal bool IsRecording => _recorder is not null;

        private void WireVoice()
        {
            _voiceClock.Tick += (_, _) => UpdateRecordingTip();
            ShowMicIdle();
            Closed += (_, _) => _speech?.Dispose();

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
                HasKeyFor);

        /// <summary>Есть ли ключ провайдера этой модели — по нему облачная модель годится или нет.</summary>
        private bool HasKeyFor(string model) =>
            _services is { } services && !string.IsNullOrWhiteSpace(services.Keys.CredentialFor(model, null).Secret);

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

            recorder.ChunkRecorded += chunk => ShowVoiceLevels(WavFile.Levels(chunk, MicDots.Children.Count));
            recorder.LimitReached += () => Detached.Run(FinishRecordingAsync(), "voice_limit");
            _recorder = recorder;
            _fox?.RecordingStarted();
            ShowMicRecording();
            _voiceClock.Start();
            return true;
        }

        /// <summary>
        /// Запись идёт: кнопка залита акцентом, вместо значка — точки громкости, время — в подсказке.
        /// </summary>
        /// <remarks>
        /// До 1.37.0-beta.7 запись показывали красная кнопка, дышащий круг за ней и время рядом,
        /// которое сдвигало ряд показаний у поля ввода. Нынешний вид выбрал человек.
        /// </remarks>
        private void ShowMicRecording()
        {
            MicButton.SetResourceReference(BackgroundProperty, "Accent.Fill");
            MicButton.SetResourceReference(BorderBrushProperty, "Accent.Fill");
            MicIcon.Visibility = Visibility.Collapsed;
            MicDots.Visibility = Visibility.Visible;
            ShowVoiceLevels(new double[MicDots.Children.Count]);
            _micTip ??= new ToolTip { Placement = PlacementMode.Top, PlacementTarget = MicButton };
            MicButton.ToolTip = _micTip;
            UpdateRecordingTip();
        }

        /// <summary>
        /// Время записи в подсказке. Пока кнопку держат (рация), подсказка открыта сама: отпустить —
        /// значит закончить, и сколько записано, видно, не уводя мышь.
        /// </summary>
        private void UpdateRecordingTip()
        {
            if (_recorder is not { } recorder || _micTip is null)
            {
                return;
            }

            var holding = _micStartedByPress && Mouse.LeftButton == MouseButtonState.Pressed &&
                          DateTime.UtcNow - _micPressedAt >= HoldThreshold;
            var time = recorder.Elapsed.ToString(@"m\:ss", CultureInfo.InvariantCulture);
            _micTip.Content = Loc.Format(holding ? "S.Voice.RecordingHold" : "S.Voice.RecordingTime", time);
            if (holding && !_micTip.IsOpen)
            {
                _micTip.IsOpen = true;
            }
        }

        /// <summary>Высота каждой точки — по громкости своей доли куска записи.</summary>
        private void ShowVoiceLevels(IReadOnlyList<double> levels)
        {
            for (var i = 0; i < MicDots.Children.Count && i < levels.Count; i++)
            {
                var dot = (FrameworkElement)MicDots.Children[i];
                var weight = i < DotWeights.Length ? DotWeights[i] : 1;

                // Корень: тихая речь поднимает точки заметно, громкая не упирается в потолок сразу.
                var height = DotLow + ((DotHigh - DotLow) * Math.Clamp(Math.Sqrt(levels[i]) * weight, 0, 1));
                if (UiMotion.Enabled)
                {
                    dot.BeginAnimation(HeightProperty, new DoubleAnimation(height, TimeSpan.FromMilliseconds(90)));
                }
                else
                {
                    dot.BeginAnimation(HeightProperty, null);
                    dot.Height = height;
                }
            }
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
            if (_micTip is not null)
            {
                _micTip.IsOpen = false;
            }

            MicIcon.Visibility = Visibility.Visible;
            MicDots.Visibility = Visibility.Collapsed;
            MicButton.SetResourceReference(BackgroundProperty, "Bg.Raised");
            MicButton.SetResourceReference(BorderBrushProperty, "Border.Default");
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
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                ClearComposerNotice(sessionId);
            }
            catch (VoiceUnavailableException ex)
            {
                ClearComposerNotice(sessionId);
                await ShowNoticeAsync(Loc.Get("S.Voice.UnavailableTitle"), ex.Message, Loc.Get("S.Common.Close"), null, NoticeTone.Info);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // До 1.37.0-beta.7 тайм-аут запроса (TaskCanceledException без отмены) уходил в
                // ветку отмены и пропадал молча, а прочие сбои не ловились вовсе: под полем
                // так и висело «Распознаю…».
                ClearComposerNotice(sessionId);
                ShowTransientNotice(sessionId, Loc.Format("S.Voice.Failed", VoiceError(ex)));
            }
        }

        /// <summary>Причина сбоя распознавания для человека.</summary>
        private static string VoiceError(Exception ex) => ex switch
        {
            OperationCanceledException or TimeoutException => Loc.Get("S.Voice.TimedOut"),
            _ => ex.Message
        };

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
                    installed.Count == 0 ? "-" : string.Join(", ", installed.Select(item => item.Name))));
            }

            var model = VoiceAvailability.CloudModel(settings, HasKeyFor);
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
