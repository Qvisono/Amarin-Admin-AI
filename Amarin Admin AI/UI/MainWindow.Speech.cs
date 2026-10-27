using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Чтение ответа вслух кнопкой под ним (1.37.0): синтезатором Windows или моделью озвучки
    /// Venice и OpenRouter — как выбрано на подстранице «Голос».
    /// </summary>
    public partial class MainWindow
    {
        private SpeechOutput? _speech;

        private SpeechOutput Speech
        {
            get
            {
                if (_speech is null)
                {
                    _speech = new SpeechOutput();
                    _speech.Failed += message => ShowTransientNotice(_session.Id, Loc.Format("S.Speech.Failed", message));
                }

                return _speech;
            }
        }

        /// <summary>Кнопка под ответом: прочитать вслух, а на читаемом — замолчать.</summary>
        private void SpeakMessage(ChatDisplayMessage message)
        {
            if (_services is not { } services || !OperatingSystem.IsWindows())
            {
                return;
            }

            if (_speech?.Speaking == message.Id)
            {
                _speech.Stop();
                return;
            }

            var settings = services.Settings;
            var text = SpeechText.From(message.Text);

            // «Авто» без ключа OpenRouter читает Windows (1.37.0-beta.8): голос заводской модели
            // синтезатору не подходит, поэтому ему — только голос, выбранный человеком.
            if (VoiceAvailability.SpeechModel(settings, HasKeyFor) is not { } model)
            {
                Speech.Speak(message.Id, text, settings.SpeechVoice, settings.SpeechRate);
                return;
            }

            var credential = services.Keys.CredentialFor(model, null);
            var speed = SpeechOutput.Speed(settings.SpeechRate);
            var chosenVoice = VoiceAvailability.SpeechVoice(settings);
            Speech.SpeakWithModel(message.Id, text, async (chunk, token) =>
            {
                // Каталог — за ценой знака и голосами: без него траты не записались бы, а голос,
                // оставшийся от другой модели, она бы не приняла.
                await services.AudioModels.LoadAsync(AudioKind.Speech, ModelRef.Of(model), token);
                var info = services.AudioModels.Find(AudioKind.Speech, model);
                var voice = info is { Voices.Count: > 0 } && !info.Voices.Contains(chosenVoice ?? "", StringComparer.Ordinal)
                    ? info.Voices[0]
                    : chosenVoice;
                return await services.Venice.SpeakAsync(chunk, model, voice, speed, info?.UsdPerChar, credential, token);
            });
        }
    }
}
