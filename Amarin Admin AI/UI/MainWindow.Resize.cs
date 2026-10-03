using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Лента, пока окно тянут за край или разворачивают: перекладывается только полоса вокруг
    /// видимого, остальное держит прежнюю ширину и отпускается порциями, когда размер устоялся.
    /// </summary>
    /// <remarks>
    /// Лента — <see cref="System.Windows.Controls.StackPanel"/> без виртуализации, и каждое
    /// построенное сообщение — свой документ. До 1.30.0 смена ширины перекладывала их все: на
    /// чате в 1200 сообщений шаг перетаскивания края стоил 1,7 с, разворот — полторы, и Windows
    /// всё это время показывала на месте новой части окна чёрное. Что держать живым, решает
    /// <see cref="TranscriptReflow"/>; как сообщение держит ширину — <see cref="ChatMessageHost.Freeze"/>.
    /// </remarks>
    public partial class MainWindow
    {
        private WindowResizeWatch? _resizeWatch;

        /// <summary>Окно тянут или разворачивают — между началом и концом жеста.</summary>
        private bool _windowSizing;

        /// <summary>Замороженные сообщения, которые осталось отпустить, в порядке отпускания.</summary>
        private readonly List<ChatMessageHost> _thawQueue = [];

        private int _thawCursor;

        /// <summary>
        /// Сколько сообщений отпускать за порцию. Подстраивается по времени порции: отпустить —
        /// это только пометить, вся цена в раскладке после, и заранее её не узнать.
        /// </summary>
        private int _thawBatch = 4;

        /// <summary>Сколько времени отдаётся одной порции отпускания, в миллисекундах.</summary>
        private const double ThawBudgetMs = 8;

        private bool ThawPending => _thawCursor < _thawQueue.Count;

        private void WireTranscriptResize()
        {
            _resizeWatch = WindowResizeWatch.Attach(this);
            _resizeWatch.Began += OnWindowResizeBegan;
            _resizeWatch.Ended += OnWindowResizeEnded;
        }

        private void OnWindowResizeBegan()
        {
            _windowSizing = true;
            var (first, last) = LiveRange(ResizeReach());
            for (var i = 0; i < _messageHosts.Count; i++)
            {
                if (i < first || i > last)
                {
                    _messageHosts[i].Freeze();
                }
            }
        }

        private void OnWindowResizeEnded()
        {
            _windowSizing = false;
            QueueThaw();

            // Достройка ждала конца жеста — и отпускание идёт её же очередью.
            ScheduleBackgroundFill();
        }

        /// <summary>
        /// Видимая область сдвинулась или выросла посреди жеста: замороженное, что в неё попало,
        /// отпускается сразу, чтобы на глазах не стояло текста прежней ширины.
        /// </summary>
        private void ThawAroundViewport()
        {
            var (first, last) = LiveRange(MaterializeLead);
            for (var i = first; i <= last; i++)
            {
                _messageHosts[i].Thaw();
            }
        }

        /// <summary>Номера сообщений в живой полосе (см. <see cref="TranscriptReflow.LiveBand"/>).</summary>
        private (int First, int Last) LiveRange(double reach)
        {
            var (from, to) = TranscriptReflow.LiveBand(DocOffset(), DocViewportHeight(), reach, _stickToBottom);
            return TranscriptReflow.Overlapping(_messageHosts.Count, i => TopOf(_messageHosts[i]), from, to);
        }

        /// <summary>Верх сообщения в координатах ленты — по раскладке, без пересчёта.</summary>
        private static double TopOf(ChatMessageHost host) => VisualTreeHelper.GetOffset(host).Y;

        /// <summary>
        /// Насколько может вырасти видимая область, пока размер меняется, — высота всех экранов в
        /// координатах ленты. Больше окно не станет, сколько его ни тяни.
        /// </summary>
        private double ResizeReach() => Math.Max(SystemParameters.VirtualScreenHeight, SystemParameters.PrimaryScreenHeight) / ChatScale;

        /// <summary>Ставит в очередь всё замороженное: от живой полосы наружу, ближнее первым.</summary>
        private void QueueThaw()
        {
            _thawQueue.Clear();
            _thawCursor = 0;
            var (first, last) = LiveRange(MaterializeLead);

            // Замороженное внутри полосы (видимая область могла уехать) — раньше всего остального.
            for (var i = first; i <= last; i++)
            {
                if (_messageHosts[i].IsFrozen)
                {
                    _thawQueue.Add(_messageHosts[i]);
                }
            }

            foreach (var index in TranscriptReflow.Outward(_messageHosts.Count, first, last))
            {
                if (_messageHosts[index].IsFrozen)
                {
                    _thawQueue.Add(_messageHosts[index]);
                }
            }
        }

        /// <summary>Очередь отпускания забыта: лента собрана заново, замороженных в ней нет.</summary>
        private void ForgetThaw()
        {
            _thawQueue.Clear();
            _thawCursor = 0;
        }

        /// <summary>
        /// Отпускает очередную порцию так, чтобы то, что человек читает, осталось на месте, — тем же
        /// якорем, что и достройка (<see cref="MaterializeAnchored"/>).
        /// </summary>
        private void ThawNextChunk()
        {
            var watch = Stopwatch.StartNew();
            MaterializeAnchored(() =>
            {
                var thawed = 0;
                while (_thawCursor < _thawQueue.Count && thawed < _thawBatch)
                {
                    var host = _thawQueue[_thawCursor++];
                    if (host.Thaw())
                    {
                        _justBuilt.Add(host);
                        thawed++;
                    }
                }

                return thawed;
            });

            var spent = watch.Elapsed.TotalMilliseconds;
            _thawBatch = spent < ThawBudgetMs / 2
                ? Math.Min(64, _thawBatch * 2)
                : spent > ThawBudgetMs * 1.5 ? Math.Max(1, _thawBatch / 2) : _thawBatch;

            if (!ThawPending)
            {
                ForgetThaw();
            }
        }
    }
}
