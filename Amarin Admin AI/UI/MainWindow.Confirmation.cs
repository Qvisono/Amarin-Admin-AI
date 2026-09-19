using System.Windows;
using System.Windows.Controls;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.UI
{
    /// <summary>
    /// The body of the «требуется подтверждение» dialog: the model's own words, the code that is
    /// about to run, and the technical breakdown.
    /// </summary>
    /// <remarks>
    /// Split out of <c>ShowNextConfirmation</c> because the three parts have different rules about
    /// when they appear, and the dialog is the last place where a person can still say no — it is
    /// worth keeping legible.
    /// </remarks>
    public partial class MainWindow
    {
        /// <summary>
        /// A script in a dialog is read, not scrolled through for minutes; past this it is taller
        /// than the buttons below it and the card starts fighting its own Viewbox.
        /// </summary>
        private const double ConfirmationCodeMaxHeight = 220;

        /// <summary>
        /// Раскладка тела диалога. Нарочно синхронная и без сети: запрос к модели за разъяснением
        /// живёт отдельно (<see cref="ExplainConfirmation"/>), иначе окно нельзя было бы ни
        /// разложить, ни проверить, не сходив за ответом.
        /// </summary>
        private void FillConfirmationBody(DangerousActionInfo info)
        {
            // Explanation and details used to share one line, with Details as the fallback. That
            // put a multi-line technical dump where a sentence belonged; now each has its place and
            // the line simply disappears when the model said nothing.
            var explanation = info.Explanation?.Trim() ?? "";
            var hasExplanation = explanation.Length > 0 &&
                                 !explanation.Equals(info.ChangeSummary?.Trim(), StringComparison.Ordinal);
            ConfirmationExplanationText.Text = hasExplanation ? explanation : "";
            ConfirmationExplanationText.Visibility = hasExplanation ? Visibility.Visible : Visibility.Collapsed;

            FillCodeHost(info);
            FillDetailsHost(info);
        }

        /// <summary>
        /// Заказывает у модели разъяснение к скрипту и дописывает его в уже открытое окно.
        /// </summary>
        /// <remarks>
        /// Кнопки «Да» и «Нет» живые с первого кадра: ответ доезжает сам по себе и ни на что,
        /// кроме этой строки, не влияет. Отмена — по образцу <c>AskAboutEntryAsync</c> в журнале,
        /// но с одной добавкой: очередь подтверждений адресная, и пока ответ идёт, в окне уже
        /// может стоять вопрос из соседнего чата. Поэтому пришедший текст сверяется с самим
        /// запросом, а не только с токеном.
        /// </remarks>
        private async Task ExplainConfirmationAsync(ConfirmationRequest request)
        {
            CancelConfirmationExplain();

            ConfirmationAiText.Text = "";
            ConfirmationAiText.Visibility = Visibility.Collapsed;

            if (_services is null || !ActionExplainer.IsWorthExplaining(request.Info))
            {
                return;
            }

            var cancellation = new CancellationTokenSource();
            _confirmExplain = cancellation;
            _confirmExplainFor = request;

            ConfirmationAiText.Text = Loc.Get("S.Confirm.Explaining");
            ConfirmationAiText.Visibility = Visibility.Visible;

            string answer;
            try
            {
                answer = await ActionExplainer.ExplainAsync(
                        _services.Venice,
                        _services.Settings.AgentFastModelId,
                        request.Info,
                        cancellation.Token,
                        _services.Keys.CredentialFor(
                            _services.Settings.AgentFastModelId,
                            ModelSlots.ReadKey(_services.Settings, ModelSlot.AgentFast)))
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (cancellation.IsCancellationRequested ||
                !ReferenceEquals(_confirmExplain, cancellation) ||
                !ReferenceEquals(_confirmExplainFor, request))
            {
                return;
            }

            ConfirmationAiText.Text = answer;
        }

        private void CancelConfirmationExplain()
        {
            try
            {
                _confirmExplain?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Запрос успел закончиться сам и освободить токен. Обычная гонка, не сбой.
            }

            _confirmExplain?.Dispose();
            _confirmExplain = null;
            _confirmExplainFor = null;
        }

        private CancellationTokenSource? _confirmExplain;
        private ConfirmationRequest? _confirmExplainFor;

        private void FillCodeHost(DangerousActionInfo info)
        {
            var code = info.CodeText ?? "";
            if (string.IsNullOrWhiteSpace(code))
            {
                ConfirmationCodeHost.Content = null;
                ConfirmationCodeHost.Visibility = Visibility.Collapsed;
                return;
            }

            // Expanded on open: the whole point is that nobody approves a script sight unseen.
            ConfirmationCodeHost.Content = BuildConfirmationExpander(
                Loc.Format("S.Confirm.Code", CountLines(code)),
                CodeBlockView.Create(this, code, string.IsNullOrWhiteSpace(info.CodeLanguage) ? null : info.CodeLanguage),
                expanded: true);
            ConfirmationCodeHost.Visibility = Visibility.Visible;
        }

        private void FillDetailsHost(DangerousActionInfo info)
        {
            var details = info.Details?.Trim() ?? "";
            if (details.Length == 0)
            {
                ConfirmationDetailsHost.Content = null;
                ConfirmationDetailsHost.Visibility = Visibility.Collapsed;
                return;
            }

            var text = new TextBlock
            {
                Text = details,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, "Text.Muted");

            ConfirmationDetailsHost.Content = BuildConfirmationExpander(
                Loc.Get("S.Confirm.Details"), text, expanded: false);
            ConfirmationDetailsHost.Visibility = Visibility.Visible;
        }

        private Expander BuildConfirmationExpander(string header, FrameworkElement body, bool expanded)
        {
            var headerText = new TextBlock { Text = header };
            headerText.SetResourceReference(StyleProperty, "ExpanderHeaderText");

            // CodeBlockView sizes itself to its widest line, which on a long command runs past the
            // card; the scroller is what keeps the dialog the shape the buttons were laid out for.
            var scroller = new ScrollViewer
            {
                Content = body,
                MaxHeight = ConfirmationCodeMaxHeight,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            return new Expander
            {
                Style = (Style)FindResource("ToolsExpander"),
                Header = headerText,
                Content = scroller,
                IsExpanded = expanded,
                Margin = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinWidth = 0
            };
        }

        private static int CountLines(string text)
        {
            var lines = 1;
            foreach (var symbol in text)
            {
                if (symbol == '\n')
                {
                    lines++;
                }
            }

            return lines;
        }
    }
}
