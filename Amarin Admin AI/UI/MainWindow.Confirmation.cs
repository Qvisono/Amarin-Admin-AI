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
        private const double ConfirmationCodeMaxHeight = 200;

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
            ConfirmationAiText.Text = "";
            ShowConfirmationLead();

            FillConfirmationReasons(info);
            FillCodeHost(info);
            FillDetailsHost(info);
        }

        /// <summary>
        /// Главная фраза окна — одна (1.37.0-beta.5): разъяснение ИИ, пока его нет — слова модели,
        /// нет и их — сводка. Три абзаца разного цвета подряд заставляли разбираться, какой главный.
        /// </summary>
        private void ShowConfirmationLead()
        {
            var ai = ConfirmationAiText.Text.Length > 0;
            var spoken = !ai && ConfirmationExplanationText.Text.Length > 0;
            ConfirmationAiText.Visibility = ai ? Visibility.Visible : Visibility.Collapsed;
            ConfirmationExplanationText.Visibility = spoken ? Visibility.Visible : Visibility.Collapsed;
            ConfirmationSummaryText.Visibility = ai || spoken ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>
        /// Пункты разбора PowerShell плашками: «Меняет систему:» и по плашке на команду. Подпись —
        /// начало той же строки, что идёт в сводку и аудит, чтобы перевод был один.
        /// </summary>
        private void FillConfirmationReasons(DangerousActionInfo info)
        {
            ConfirmationReasons.Children.Clear();
            if (info.Reasons is not { Count: > 0 } reasons)
            {
                ConfirmationReasons.Visibility = Visibility.Collapsed;
                return;
            }

            var label = new TextBlock
            {
                Text = ChangesPrefix().TrimEnd(),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 3)
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Text.Dim");
            ConfirmationReasons.Children.Add(label);

            foreach (var reason in reasons)
            {
                var text = new TextBlock
                {
                    Text = reason,
                    FontSize = 11,
                    FontFamily = ChatFonts.Mono,
                    VerticalAlignment = VerticalAlignment.Center
                };
                text.SetResourceReference(TextBlock.ForegroundProperty, "Status.Warning");
                var chip = new Border
                {
                    Child = text,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 1, 6, 1),
                    Margin = new Thickness(0, 0, 4, 3)
                };
                chip.SetResourceReference(Border.BackgroundProperty, "Status.WarningSurface");
                chip.SetResourceReference(Border.BorderBrushProperty, "Status.WarningBorder");
                ConfirmationReasons.Children.Add(chip);
            }

            ConfirmationReasons.Visibility = Visibility.Visible;
        }

        private static string ChangesPrefix() => Loc.Get("S.Confirm.ScriptChanges").Split("{0}")[0];

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
            ShowConfirmationLead();

            if (_services is null || !ActionExplainer.IsWorthExplaining(request.Info))
            {
                return;
            }

            var cancellation = new CancellationTokenSource();
            _confirmExplain = cancellation;
            _confirmExplainFor = request;

            // Пока ответ идёт, на месте главной фразы остаётся сводка: строка «Разбираю…» была бы
            // ещё одним абзацем, который приходится прочесть и отбросить.
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

            // Неудача — не главная фраза: «объяснение не пришло» вытеснило бы сводку, которая
            // говорит хоть что-то. Остаётся то, что было.
            if (answer == Loc.Get("S.Confirm.ExplainFailed") || answer == Loc.Get("S.Confirm.ExplainEmpty"))
            {
                return;
            }

            ConfirmationAiText.Text = answer;
            ShowConfirmationLead();
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
            ShowConfirmationMore();

            var info = request.Info;
            // Пробный прогон идёт на этом ПК — для удалённой машины он показал бы чужое.
            if (info.Target is not null || info.Arguments is not { } arguments ||
                WhatIfProbe.ScriptFor(info.ToolName, arguments) is null)
            {
                return;
            }

            var cancellation = new CancellationTokenSource();
            _confirmWhatIf = cancellation;

            // Пока прогон идёт, под «Подробнее» стоит строка об этом: показывать ещё нечего.
            ConfirmationWhatIfHost.Content = WhatIfCaption(Loc.Get("S.Confirm.WhatIfRunning"));
            ShowConfirmationMore();

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
                ShowConfirmationMore();
                return;
            }

            var block = new StackPanel();
            block.Children.Add(WhatIfCaption(Loc.Get("S.Confirm.WhatIf")));
            var code = Scrolled(CodeBlockView.Create(this, shown.Text, null, copyOnly: true));
            code.Margin = new Thickness(0, 4, 0, 0);
            block.Children.Add(code);
            ConfirmationWhatIfHost.Content = block;
            ShowConfirmationMore();
        }

        private static TextBlock WhatIfCaption(string text)
        {
            var caption = new TextBlock { Text = text, FontSize = 11.5 };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "Text.Dim");
            return caption;
        }

        /// <summary>
        /// «Подробнее» под кодом: видна, если есть что раскрыть (разбор или пробный прогон), и
        /// раскрывает оба сразу. Отдельная строка на каждый блок снова дала бы окну лишние ручки.
        /// </summary>
        private void ShowConfirmationMore()
        {
            var hasDetails = ConfirmationDetailsHost.Content is not null;
            var hasWhatIf = ConfirmationWhatIfHost.Content is not null;
            var open = ConfirmationDetailsToggle.IsChecked == true;
            ConfirmationDetailsToggle.Visibility = hasDetails || hasWhatIf ? Visibility.Visible : Visibility.Collapsed;
            ConfirmationDetailsHost.Visibility = open && hasDetails ? Visibility.Visible : Visibility.Collapsed;
            ConfirmationWhatIfHost.Visibility = open && hasWhatIf ? Visibility.Visible : Visibility.Collapsed;
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

            // Открыто всегда и без экспандера вокруг: смысл в том, чтобы скрипт не одобряли не
            // глядя, а вторая рамка с заголовком над шапкой самого блока только утяжеляла окно.
            var language = string.IsNullOrWhiteSpace(info.CodeLanguage) ? null : info.CodeLanguage;
            var caption = (language ?? "text").Trim().ToLowerInvariant() + " · " +
                          Loc.Format("S.Confirm.Code", CountLines(code));
            ConfirmationCodeHost.Content = Scrolled(CodeBlockView.Create(this, code, language, caption: caption, copyOnly: true));
            ConfirmationCodeHost.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Технический разбор вызова. Свёрнут: раскрывает его строка «Подробнее» под кодом —
        /// решающий читает его последним, если главной фразы и кода не хватило.
        /// </summary>
        private void FillDetailsHost(DangerousActionInfo info)
        {
            ConfirmationDetailsToggle.IsChecked = false;

            var details = info.Details?.Trim() ?? "";
            if (details.Length == 0)
            {
                ConfirmationDetailsHost.Content = null;
                ShowConfirmationMore();
                return;
            }

            var text = new TextBlock
            {
                Text = details,
                FontSize = 11.5,
                LineHeight = 17,
                TextWrapping = TextWrapping.Wrap
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, "Text.Muted");

            ConfirmationDetailsHost.Content = text;
            ShowConfirmationMore();
        }

        private void ConfirmationDetailsToggle_Click(object sender, RoutedEventArgs e)
        {
            ShowConfirmationMore();
            if (ConfirmationDetailsToggle.IsChecked == true)
            {
                // Подробности встают в конец тела — без прокрутки к ним нажатие казалось бы пустым.
                ConfirmationDetailsHost.BringIntoView();
            }
        }

        /// <summary>
        /// CodeBlockView берёт ширину по самой длинной строке, и длинная команда вылезла бы за
        /// карточку; прокрутка держит окно той формы, под которую разложены кнопки.
        /// </summary>
        private static ScrollViewer Scrolled(FrameworkElement body)
        {
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
            return scroller;
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
            var changesPrefix = ChangesPrefix();

            // Пункты разбора окно показывает плашками (FillConfirmationReasons) — строкой в сводке
            // они повторились бы.
            var plates = info.Reasons is { Count: > 0 };
            var lines = summary.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
                .Where(line => !plates || !line.StartsWith(changesPrefix, StringComparison.Ordinal))
                .ToList();
            var head = lines[0];
            var colon = head.IndexOf(": ", StringComparison.Ordinal);
            if (code.Length == 0 || colon <= 0)
            {
                return string.Join("\n", lines);
            }

            var echoed = head[(colon + 2)..].TrimStart();
            var start = code.Split('\n')[0].Trim();
            start = start[..Math.Min(start.Length, 24)];
            if (start.Length == 0 || !echoed.StartsWith(start, StringComparison.Ordinal))
            {
                return string.Join("\n", lines);
            }

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
