using System.Windows;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Чтение ответов вслух и разговорный режим (1.37.0): сказал — фраза кончилась паузой — ушла
    /// модели — ответ прочитан вслух — снова слушает.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Режим держится за один чат: смена чата, блокировка, Esc и кнопка его выключают, и запись с
    /// чтением останавливаются вместе с ним. Распознавание — то же, что у кнопки микрофона
    /// (<see cref="RecognizeAsync"/>), отправка — обычная (<see cref="SendAsync"/>, со всеми
    /// проверками перед ходом).
    /// </para>
    /// <para>
    /// <see cref="SendAsync"/> ждёт конца хода, поэтому ответ читается сразу за ним, без отдельной
    /// подписки на конец хода. Ответ, который не дошёл до конца (остановили, ошибка), режим
    /// выключает: читать нечего, а слушать дальше — значит отправлять в чат, где что-то не так.
    /// </para>
    /// </remarks>
    public partial class MainWindow
    {
        private SpeechOutput? _speech;
        private string? _conversationSession;
        private SilenceDetector? _phrase;

        internal bool InConversation => _conversationSession is not null;

        private SpeechOutput Speech
        {
            get
            {
                if (_speech is null)
                {
                    _speech = new SpeechOutput();
                    _speech.Finished += OnSpeechFinished;
                }

                return _speech;
            }
        }

        /// <summary>Кнопка под ответом: прочитать вслух, а на читаемом — замолчать.</summary>
        private void SpeakMessage(ChatDisplayMessage message)
        {
            if (_services is null || !OperatingSystem.IsWindows())
            {
                return;
            }

            if (_speech?.Speaking == message.Id)
            {
                _speech.Stop();
                return;
            }

            Speech.Speak(message.Id, SpeechText.From(message.Text), _services.Settings.SpeechVoice, _services.Settings.SpeechRate);
        }

        private void ConversationButton_Click(object sender, RoutedEventArgs e) => ToggleConversation();

        /// <summary>Кнопка и сочетание клавиш: включить или выключить разговорный режим.</summary>
        internal bool ToggleConversation()
        {
            if (InConversation)
            {
                return StopConversation();
            }

            if (_services is null || !OperatingSystem.IsWindows())
            {
                return false;
            }

            if (MicHost.Visibility != Visibility.Visible)
            {
                Detached.Run(OfferVoiceSetupAsync(), "voice_setup");
                return false;
            }

            if (IsRecording)
            {
                CancelRecording(notify: false);
            }

            _conversationSession = _session.Id;
            ShowConversation(on: true);
            Listen();
            return true;
        }

        /// <summary>Выключает режим: запись выбрасывается, чтение обрывается.</summary>
        internal bool StopConversation()
        {
            if (!InConversation)
            {
                return false;
            }

            _conversationSession = null;
            _phrase = null;
            _speech?.Stop();
            if (IsRecording)
            {
                CancelRecording(notify: false);
            }

            ShowConversation(on: false);
            return true;
        }

        /// <summary>Слушает следующую фразу; чат сменился — режим кончается.</summary>
        private void Listen()
        {
            if (!InConversation || IsRecording)
            {
                return;
            }

            if (_session.Id != _conversationSession || !StartRecording())
            {
                StopConversation();
                return;
            }

            var phrase = _phrase = new SilenceDetector();
            if (_recorder is { } recorder)
            {
                recorder.LevelChanged += level =>
                {
                    if (ReferenceEquals(phrase, _phrase) && phrase.Add(level, MicRecorder.ChunkLength))
                    {
                        _phrase = null;
                        Detached.Run(FinishRecordingAsync(), "conversation_phrase");
                    }
                };
            }
        }

        /// <summary>Распознанная фраза разговора: в поле и сразу отправить, ответ — прочитать.</summary>
        private async Task SendSpokenAsync(string text)
        {
            var session = _session;
            if (session.Id != _conversationSession)
            {
                StopConversation();
                return;
            }

            var before = session.Messages.LastOrDefault(message => message.Role == "assistant")?.Id;
            MessageTextBox.Text = text.Trim();
            MessageTextBox.CaretIndex = MessageTextBox.Text.Length;
            await SendAsync();
            if (!InConversation || session.Id != _conversationSession)
            {
                return;
            }

            var answer = session.Messages.LastOrDefault(message => message.Role == "assistant");
            if (answer is null || answer.Id == before || answer.Status != AssistantStatus.Complete || _services is null)
            {
                StopConversation();
                return;
            }

            Speech.Speak(answer.Id, SpeechText.From(answer.Text), _services.Settings.SpeechVoice, _services.Settings.SpeechRate);
        }

        /// <summary>Фраза в разговоре не распозналась или была пустой — слушать дальше.</summary>
        private void ListenAgain()
        {
            if (InConversation)
            {
                Listen();
            }
        }

        private void OnSpeechFinished(bool completed)
        {
            if (completed && InConversation)
            {
                Listen();
            }
        }

        private void ShowConversation(bool on)
        {
            ConversationButton.SetResourceReference(BackgroundProperty, on ? "Accent.Fill" : "Bg.Raised");
            ConversationButton.SetResourceReference(ForegroundProperty, on ? "Text.OnAccent" : "Text.Dim");
            ConversationButton.SetResourceReference(ToolTipProperty, on ? "S.Conversation.Stop" : "S.Conversation.Start");
        }
    }
}
