using System.Windows;
using System.Windows.Controls;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.UI
{
    /// <summary>
    /// Тело окна «требуется подтверждение»: слова самой модели, код, который сейчас запустится, и
    /// технический разбор.
    /// </summary>
    /// <remarks>
    /// Вынесено из <c>ShowNextConfirmation</c>: у трёх частей разные правила показа, а это окно —
    /// последнее место, где человек ещё может сказать «нет», и читаться оно должно ясно.
    /// </remarks>
    public partial class MainWindow
    {
        /// <summary>
        /// Скрипт в окне читают, а не листают минутами; выше этого он перерастает кнопки под ним, и
        /// карточка начинает спорить со своим Viewbox.
        /// </summary>
        private const double ConfirmationCodeMaxHeight = 220;

        /// <summary>
        /// Раскладка тела диалога. Нарочно синхронная и без сети: запрос к модели за разъяснением
        /// живёт отдельно (<see cref="ExplainConfirmation"/>), иначе окно нельзя было бы ни
        /// разложить, ни проверить, не сходив за ответом.
        /// </summary>
        private void FillConfirmationBody(DangerousActionInfo info)
        {
            // Пояснение и подробности делили одну строку, и подробности были запасным вариантом —
            // на месте фразы оказывалась многострочная техническая выгрузка. Теперь у каждого своё
            // место, а строка пропадает, если модель ничего не сказала.
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
        private CancellationTokenSource? _confirmWhatIf;

        /// <summary>
        /// Пробный прогон с -WhatIf и его итог над текстом команды.
        /// </summary>
        /// <remarks>
        /// По образцу <see cref="ExplainConfirmationAsync"/>: свой токен, сверка «окно показывает
        /// тот же вопрос», и кнопки не ждут. Прогона нет (внешняя программа в скрипте, у командлета
        /// нет -WhatIf) — нет и блока: пустая рамка «здесь могло быть» только сбивает.
        /// </remarks>
        private async Task ProbeConfirmationAsync(ConfirmationRequest request)
        {
            CancelConfirmationWhatIf();
            ConfirmationWhatIfHost.Content = null;
            ConfirmationWhatIfHost.Visibility = Visibility.Collapsed;

            var info = request.Info;
            // Пробный прогон идёт на этом ПК — для удалённой машины он показал бы чужое.
            if (info.Target is not null || info.Arguments is not { } arguments ||
                WhatIfProbe.ScriptFor(info.ToolName, arguments) is null)
            {
                return;
            }

            var cancellation = new CancellationTokenSource();
            _confirmWhatIf = cancellation;
            var body = new TextBlock { Text = Loc.Get("S.Confirm.WhatIfRunning"), FontSize = 12, TextWrapping = TextWrapping.Wrap };
            body.SetResourceReference(TextBlock.ForegroundProperty, "Text.Muted");
            ConfirmationWhatIfHost.Content = BuildConfirmationExpander(Loc.Get("S.Confirm.WhatIf"), body, expanded: true);
            ConfirmationWhatIfHost.Visibility = Visibility.Visible;

            WhatIfOutcome? outcome;
            try
            {
                outcome = await WhatIfProbe.RunAsync(info.ToolName, arguments, cancellation.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (cancellation.IsCancellationRequested || !ReferenceEquals(_confirmWhatIf, cancellation) ||
                !ReferenceEquals(_shownConfirmation, request))
            {
                return;
            }

            if (outcome is not { Supported: true } shown)
            {
                ConfirmationWhatIfHost.Content = null;
                ConfirmationWhatIfHost.Visibility = Visibility.Collapsed;
                return;
            }

            var code = CodeBlockView.Create(this, shown.Text, null);
            ConfirmationWhatIfHost.Content = BuildConfirmationExpander(Loc.Get("S.Confirm.WhatIf"), code, expanded: true);
        }

        private void CancelConfirmationWhatIf()
        {
            try
            {
                _confirmWhatIf?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _confirmWhatIf?.Dispose();
            _confirmWhatIf = null;
        }

        private void FillCodeHost(DangerousActionInfo info)
        {
            var code = info.CodeText ?? "";
            if (string.IsNullOrWhiteSpace(code))
            {
                ConfirmationCodeHost.Content = null;
                ConfirmationCodeHost.Visibility = Visibility.Collapsed;
                return;
            }

            // Раскрыто сразу: смысл в том, чтобы скрипт не одобряли не глядя.
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

            // CodeBlockView берёт ширину по самой длинной строке, и длинная команда вылезла бы за
            // карточку; прокрутка держит окно той формы, под которую разложены кнопки.
            var scroller = new ScrollViewer
            {
                Content = body,
                MaxHeight = ConfirmationCodeMaxHeight,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            // Тем же плавным колесом, что и вся программа. Заодно это и есть уговор с внешней
            // прокруткой карточки: докрутив блок кода до края, колесо уходит наружу само.
            SmoothScroll.SetIsEnabled(scroller, true);

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

        /// <summary>
        /// Сводка над кодом: что за действие и что в нём меняет систему. Сводка PowerShell
        /// начинается с эха команды («Выполнение PowerShell: Get-Service …»), а та же команда
        /// целиком стоит ниже блоком кода — в окне эхо только раздувало верх и путало, где
        /// начинается сам скрипт. Остальные сводки (путь, служба, домен) остаются как есть.
        /// </summary>
        internal static string ConfirmationSummary(DangerousActionInfo info)
        {
            var summary = string.IsNullOrWhiteSpace(info.ChangeSummary) ? info.ToolName : info.ChangeSummary.Trim();
            var code = info.CodeText?.Trim() ?? "";
            var lines = summary.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            var head = lines[0];
            var colon = head.IndexOf(": ", StringComparison.Ordinal);
            if (code.Length == 0 || colon <= 0)
            {
                return summary;
            }

            var echoed = head[(colon + 2)..].TrimStart();
            var start = code.Split('\n')[0].Trim();
            start = start[..Math.Min(start.Length, 24)];
            if (start.Length == 0 || !echoed.StartsWith(start, StringComparison.Ordinal))
            {
                return summary;
            }

            var changesPrefix = Loc.Get("S.Confirm.ScriptChanges").Split("{0}")[0];
            var changes = lines.Skip(1).Where(line => line.StartsWith(changesPrefix, StringComparison.Ordinal));
            return string.Join("\n", new[] { head[..colon] }.Concat(changes));
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
