using System.Windows;
using Amarin.Tools;

namespace Amarin.UI
{
    /// <summary>
    /// "Summarize video" in the composer's actions popup. The two modes work differently on
    /// purpose: "Raw transcript" is an ordinary chat turn driven by the <c>youtube_transcript</c>
    /// tool, while "Create infographic" runs a fixed chain in the engine with no tools at all —
    /// see <see cref="Amarin.Core.ChatEngine.RunVideoInfographicAsync"/>.
    /// </summary>
    public partial class MainWindow
    {
        private void SummarizeStartButton_Click(object sender, RoutedEventArgs e)
        {
            if (_services is null)
            {
                return;
            }

            if (_busy)
            {
                ShowSummarizeError("Дождитесь окончания текущего ответа.");
                return;
            }

            var url = YoutubeUrlBox.Text.Trim();
            if (url.Length == 0)
            {
                ShowSummarizeError("Вставьте ссылку на видео.");
                return;
            }

            if (YouTubeTranscriptTool.TryReadVideoId(url) is not { } videoId)
            {
                ShowSummarizeError("Не похоже на ссылку YouTube.");
                return;
            }

            var infographic = InfographicOption.IsChecked == true;
            var canonical = $"https://www.youtube.com/watch?v={videoId}";

            // Close the popup and reset it before the turn starts — it sits over the transcript
            // the answer is about to appear in.
            SummarizeError.Visibility = Visibility.Collapsed;
            YoutubeUrlBox.Clear();
            SummarizeToggle.IsChecked = false;
            AttachButton.IsChecked = false;

            if (infographic)
            {
                _ = RunInfographicAsync(canonical);
                return;
            }

            MessageTextBox.Text = TranscriptPrompt(canonical);
            _ = SendAsync();
        }

        /// <summary>
        /// The infographic runs its own fixed chain — subtitles, then one text call, then one
        /// image call — rather than a chat turn. The model is offered no tools and decides
        /// nothing; the deliverable is a picture, so there is nothing for it to narrate.
        /// </summary>
        private async Task RunInfographicAsync(string videoUrl)
        {
            if (_services is null || _busy)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_services.Options.ApiKey))
            {
                MessageBox.Show(
                    this,
                    "Не задан API-ключ Venice.ai.\nЗадайте переменную окружения VENICE_API_KEY.",
                    Title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            _turnCts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                await _services.Chat.RunVideoInfographicAsync(_session, videoUrl, this, _turnCts.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
                _turnCts?.Dispose();
                _turnCts = null;
            }
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
