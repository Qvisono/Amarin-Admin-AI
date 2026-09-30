using System.Windows;
using System.Windows.Threading;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Оценка цены следующего ответа у кольца контекста (E2).
    /// </summary>
    /// <remarks>
    /// Пересчёт с паузой в 300 мс после набора: считать на каждый символ незачем, а подпись,
    /// прыгающая под пальцами, отвлекает. Считается на потоке интерфейса — там же, где кольцо:
    /// это сумма длин уже подсчитанных частей, а чтение переписки с другого потока шло бы
    /// наперегонки с идущим ходом.
    /// </remarks>
    public partial class MainWindow
    {
        private readonly DispatcherTimer _estimateTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
        private ContextUsage _estimateUsage;
        private IReadOnlyList<VeniceModelInfo?> _estimateModels = [];

        private void WireCostEstimate()
        {
            _estimateTimer.Tick += (_, _) =>
            {
                _estimateTimer.Stop();
                UpdateCostEstimate();
            };
            MessageTextBox.TextChanged += (_, _) => RestartEstimateTimer();
        }

        /// <summary>Кольцо пересчитано — запомнить, чем мерили, и пересчитать цену следом.</summary>
        private void ScheduleCostEstimate(ContextUsage usage, IReadOnlyList<VeniceModelInfo?> models)
        {
            _estimateUsage = usage;
            _estimateModels = models;
            RestartEstimateTimer();
        }

        private void RestartEstimateTimer()
        {
            _estimateTimer.Stop();
            _estimateTimer.Start();
        }

        private void UpdateCostEstimate()
        {
            var draft = CostEstimator.DraftTokens(MessageTextBox.Text) + PendingDocumentTokens();
            var input = _estimateUsage.Used + draft;
            var output = CostEstimator.TypicalOutputTokens(_session);
            var prices = _estimateModels.Select(CostEstimator.PriceOf).ToList();
            var estimate = CostEstimator.Estimate(input, output, prices);

            if (estimate is not { } shown)
            {
                CostEstimateText.Visibility = Visibility.Collapsed;
                return;
            }

            CostEstimateText.Text = CostEstimator.Format(shown);
            var tip = Loc.Format("S.Estimate.Tip", input.ToString("N0"), output.ToString("N0"));
            if (shown.IsRange)
            {
                tip += "\n" + Loc.Get("S.Estimate.AutoTip");
            }

            if (prices.Any(price => price is null))
            {
                tip += "\n" + Loc.Get("S.Estimate.Unpriced");
            }

            if (_pendingImages.Count > 0 || _pendingFiles.Any(file => !IsTextDocument(file.MimeType)))
            {
                tip += "\n" + Loc.Get("S.Estimate.Images");
            }

            tip += "\n" + Loc.Get("S.Estimate.Agent");
            CostEstimateText.ToolTip = tip;
            CostEstimateText.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Токены прикреплённых текстовых документов — по размеру файла. PDF и прочие двоичные
        /// разбирает провайдер, и сколько текста в них окажется, заранее не знает никто: они не
        /// считаются, как и картинки, и подсказка об этом говорит.
        /// </summary>
        private int PendingDocumentTokens()
        {
            long bytes = 0;
            foreach (var file in _pendingFiles)
            {
                if (IsTextDocument(file.MimeType))
                {
                    bytes += file.SizeBytes;
                }
            }

            return (int)Math.Min(int.MaxValue, bytes / 4);
        }

        private static bool IsTextDocument(string mimeType) =>
            mimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
            mimeType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
            mimeType.Contains("xml", StringComparison.OrdinalIgnoreCase);
    }
}
