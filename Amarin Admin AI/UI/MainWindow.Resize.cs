using System.Diagnostics;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Лента, пока меняется её ширина — окно тянут за край или разворачивают, двигают край
    /// боковой панели, сворачивают её: перекладывается только полоса вокруг видимого, остальное
    /// держит прежнюю ширину и отпускается порциями, когда размер устоялся.
    /// </summary>
    /// <remarks>
    /// Лента — <see cref="System.Windows.Controls.StackPanel"/> без виртуализации, и каждое
    /// построенное сообщение — свой документ. До 1.30.0 смена ширины перекладывала их все: на
    /// чате в 1200 сообщений шаг перетаскивания края стоил 1,7 с, разворот — полторы, и Windows
    /// всё это время показывала на месте новой части окна чёрное. До 1.32.0 то же оставалось у
    /// края боковой панели: заморозку заводило только окно, а панель меняет ширину ленты, не
    /// трогая окна, — шаг ручки на большом чате перекладывал шестьсот документов. Что держать
    /// живым, решает <see cref="TranscriptReflow"/>; как сообщение держит ширину —
    /// <see cref="ChatMessageHost.Freeze"/>.
    /// </remarks>
    public partial class MainWindow
    {
        private WindowResizeWatch? _resizeWatch;

        /// <summary>
        /// Сколько жестов сейчас меняет ширину ленты. Счётчик, а не признак: край окна и край
        /// панели могли бы перекрыться, и конец одного не должен отпускать ленту посреди другого.
        /// </summary>
        private int _reflowGestures;

        /// <summary>Ширина ленты меняется — между началом и концом жеста.</summary>
        private bool TranscriptReflowing => _reflowGestures > 0;

        /// <summary>
        /// Жест ручки панели начался с заморозкой — конец обязан её снять, даже если панель тем
        /// временем свернули (Ctrl+B посреди жеста).
        /// </summary>
        private bool _sidebarGripReflow;

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

            // Окно может вырасти до всех экранов, сколько его ни тяни, — живой полосе нужна
            // эта высота. Конец жеста наблюдатель и так подаёт после раскладки нового размера.
            _resizeWatch.Began += () => BeginTranscriptReflow(ResizeReach());
            _resizeWatch.Ended += EndTranscriptReflow;
        }

        /// <summary>
        /// Ширина ленты начала меняться: всё вне живой полосы замораживается на прежней ширине.
        /// </summary>
        /// <param name="reach">
        /// Насколько может вырасти видимая область за время жеста (см. <see cref="TranscriptReflow.LiveBand"/>).
        /// </param>
        private void BeginTranscriptReflow(double reach)
        {
            if (_reflowGestures++ > 0)
            {
                return;
            }

            var (first, last) = LiveRange(reach);
            for (var i = 0; i < _messageHosts.Count; i++)
            {
                if (i < first || i > last)
                {
                    _messageHosts[i].Freeze();
                }
            }
        }

        /// <summary>Размер устоялся: замороженное отпускается фоновой очередью достройки.</summary>
        private void EndTranscriptReflow()
        {
            if (_reflowGestures == 0 || --_reflowGestures > 0)
            {
                return;
            }

            QueueThaw();

            // Достройка ждала конца жеста — и отпускание идёт её же очередью.
            ScheduleBackgroundFill();
        }

        /// <summary>
        /// Конец жеста, который ширину меняет сам, а не через окно: после того, как последняя
        /// ширина разложена и нарисована. Отпусти раньше — последняя раскладка застала бы
        /// отпущенные сообщения и переложила бы их все разом, как до заморозки.
        /// </summary>
        private void EndTranscriptReflowLater() =>
            Dispatcher.BeginInvoke(EndTranscriptReflow, DispatcherPriority.ContextIdle);

        // ───────────────────────── край и сворачивание боковой панели ─────────────────────────

        /// <remarks>
        /// Высота видимой области за этот жест не меняется — живой полосе хватает обычного запаса
        /// достройки, а не высоты всех экранов, как у окна.
        /// </remarks>
        private void SidebarGrip_DragStarted(object sender, DragStartedEventArgs e)
        {
            if (!_sidebarCollapsed)
            {
                BeginTranscriptReflow(MaterializeLead);
                _sidebarGripReflow = true;
            }
        }

        /// <summary>Ручку отпустили (или жест сорвался): ширина запоминается, лента отпускается.</summary>
        private void SidebarGrip_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (_sidebarGripReflow)
            {
                _sidebarGripReflow = false;
                EndTranscriptReflowLater();
            }

            if (_services is null || _sidebarCollapsed)
            {
                return;
            }

            // Сохраняется по отпусканию, а не на каждый сдвиг: иначе запись файла на каждый пиксель.
            _services.Settings.SidebarWidth = Math.Round(SidebarColumn.ActualWidth);
            _services.SettingsStore.Save(_services.Settings);
        }

        /// <summary>
        /// Сворачивание и разворот панели меняют ширину ленты рывком на полтораста точек — тот же
        /// жест, что у края, только в один шаг.
        /// </summary>
        private void ReflowTranscriptOnce(Action change)
        {
            // Пустая лента (запуск, смена профиля) — замораживать нечего, а заведённый жест лишь
            // придержал бы достройку чата, открытого следом.
            if (_messageHosts.Count == 0)
            {
                change();
                return;
            }

            BeginTranscriptReflow(MaterializeLead);
            try
            {
                change();
            }
            finally
            {
                EndTranscriptReflowLater();
            }
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
