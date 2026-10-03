using System.Windows;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.UI
{
    /// <summary>
    /// «Пересказ видео» в меню действий поля ввода. Два режима устроены по-разному намеренно:
    /// «Субтитры» — обычный ход чата с инструментом <c>youtube_transcript</c>, а «Инфографика» —
    /// постоянная цепочка движка без инструментов вовсе, см.
    /// <see cref="Amarin.Core.ChatEngine.RunVideoInfographicAsync"/>.
    /// </summary>
    public partial class MainWindow
    {
        private void SummarizeStartButton_Click(object sender, RoutedEventArgs e)
        {
            if (_services is null)
            {
                return;
            }

            if (IsBusy(_session.Id))
            {
                ShowSummarizeError(Loc.Get("S.Summarize.WaitTurn"));
                return;
            }

            var url = YoutubeUrlBox.Text.Trim();
            if (url.Length == 0)
            {
                ShowSummarizeError(Loc.Get("S.Summarize.PasteLink"));
                return;
            }

            if (YouTubeTranscriptTool.TryReadVideoId(url) is not { } videoId)
            {
                ShowSummarizeError(Loc.Get("S.Summarize.NotYoutube"));
                return;
            }

            var infographic = InfographicOption.IsChecked == true;
            var canonical = $"https://www.youtube.com/watch?v={videoId}";

            // Закрываем и сбрасываем меню до начала хода: оно лежит поверх ленты, где сейчас
            // появится ответ.
            SummarizeError.Visibility = Visibility.Collapsed;
            YoutubeUrlBox.Clear();
            SummarizeToggle.IsChecked = false;
            AttachButton.IsChecked = false;

            if (infographic)
            {
                Detached.Run(RunInfographicAsync(canonical), "run_infographic");
                return;
            }

            MessageTextBox.Text = TranscriptPrompt(canonical);
            Detached.Run(SendAsync(), "send");
        }

        /// <summary>
        /// Инфографика идёт своей постоянной цепочкой — субтитры, текстовый запрос, рисование, — а
        /// не ходом чата. Инструментов модели не дают, решать ей нечего: итог — картинка, и
        /// рассказывать о ней нечего.
        /// </summary>
        private async Task RunInfographicAsync(string videoUrl)
        {
            if (_services is null || IsBusy(_session.Id))
            {
                return;
            }

            if (!HasUsableKey())
            {
                ShowNoKeyNotice();
                return;
            }

            await RunTurnAsync(_session, TurnKind.Infographic, (chat, observer, token) =>
                _services.Chat.RunVideoInfographicAsync(chat, videoUrl, observer, token));
        }

        private static string TranscriptPrompt(string url) =>
            $"""
             Получи расшифровку этого видео через youtube_transcript: {url}

             Верни её полностью, как есть, разбив на читаемые абзацы и не пересказывая своими
             словами. В конце добавь короткое резюме на 3–5 пунктов.
             """;

        private void ShowSummarizeError(string message)
        {
            SummarizeError.Text = message;
            SummarizeError.Visibility = Visibility.Visible;
        }
    }
}
