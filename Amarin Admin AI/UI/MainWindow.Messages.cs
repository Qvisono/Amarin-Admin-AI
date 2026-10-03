using System.Diagnostics;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Threading;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Лента сообщений: сообщения строятся по мере надобности, а не все разом.
    /// </summary>
    /// <remarks>
    /// Открытие чата ставит в ленту по «хосту» на сообщение (<see cref="ChatMessageHost"/>) и
    /// строит только те, что видно, плюс запас. Остальные достраиваются в фоне, снизу вверх,
    /// пока человек смотрит на конец переписки: всё дорисованное растёт выше видимой области,
    /// а низ держит на месте автопрокрутка, так что под глазом ничего не едет. Если человек
    /// начал листать раньше, чем фон догнал, нужное достраивает сама прокрутка — с запасом
    /// в несколько экранов, чтобы сообщения не появлялись на кромке.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public partial class MainWindow
    {
        /// <summary>Хосты в том же порядке, в каком они стоят в ленте.</summary>
        private readonly List<ChatMessageHost> _messageHosts = [];

        /// <summary>
        /// Измеренные высоты сообщений по их идентификаторам — вместе с шириной, на которой мерили.
        /// </summary>
        /// <remarks>
        /// Переживает переключение чатов: вернувшись в чат, лента резервирует ровно те высоты,
        /// что были в прошлый раз, и достраивание уже ничего не двигает. Оценка по длине текста
        /// нужна только при самом первом показе. Высота при другой ширине — уже не та: окно
        /// растянули, и текст лёг в меньше строк, — поэтому тогда берётся оценка.
        /// </remarks>
        private readonly Dictionary<string, (double Height, double Width)> _messageHeights = new(StringComparer.Ordinal);

        /// <summary>Потолок памяти высот. За сеанс столько сообщений не открывают.</summary>
        private const int MessageHeightsLimit = 4000;

        /// <summary>Запас вокруг видимой области, в пикселях. Примерно два-три сообщения.</summary>
        private const double MaterializeLead = 700;

        /// <summary>
        /// Сколько времени отдаётся одной порции фоновой дорисовки. По времени, а не по числу
        /// сообщений: ответ с кодом строится вдесятеро дольше простого, и порция «три сообщения»
        /// то пролетала незаметно, то держала поток интерфейса по сотне миллисекунд.
        /// </summary>
        private static readonly long BackgroundFillBudget = Stopwatch.Frequency * 6 / 1000;

        /// <summary>Как часто проверять, кончились ли жест лупы или инерция, пока дорисовка стоит.</summary>
        private static readonly TimeSpan BackgroundFillRetry = TimeSpan.FromMilliseconds(120);

        /// <summary>Высота видимой области, пока настоящая неизвестна (окно ещё не разложено).</summary>
        private const double AssumedViewport = 800;

        private bool _fillScheduled;
        private DispatcherTimer? _fillRetry;

        /// <summary>Откуда фоновая дорисовка продолжит поиск недостроенного (идёт к началу ленты).</summary>
        private int _fillCursor = -1;

        /// <summary>Построенные в текущем заходе — им после раскладки запоминается высота.</summary>
        private readonly List<ChatMessageHost> _justBuilt = [];

        /// <summary>Идёт материализация: прокрутка, которую она вызовет, — наша, а не человека.</summary>
        private bool _materializing;

        /// <summary>
        /// Сколько сообщений ещё не построено. Счётчиком, а не проходом по списку: обе проверки
        /// «осталось ли что-нибудь» случаются на каждое движение прокрутки.
        /// </summary>
        private int _unbuiltMessages;

        /// <summary>
        /// Прокрутка ленты сейчас чужая: едет масштаб лупы, ленту тащат или доезжает бросок. Любая
        /// постройка выше видимой области сдвинула бы текст под жестом — отсюда дрожь и прыжки
        /// лупы в первые секунды после открытия большого чата.
        /// </summary>
        private bool TranscriptInMotion => ChatZoomBusy || SmoothScroll.IsAnimating(ChatScrollViewer);

        /// <summary>Ставит в ленту хосты под все сообщения чата и строит только видимую часть.</summary>
        private void BuildMessageHosts()
        {
            HideReplyPill();

            // Высоты уходящей ленты — на случай возврата: тогда резерв встанет ровно, и
            // достройка ничего не сдвинет.
            RememberHeights(_messageHosts);
            _justBuilt.Clear();
            _fillCursor = -1;
            ForgetThaw();
            MessagesPanel.Children.Clear();
            _messageViews.Clear();
            _messageHosts.Clear();
            _unbuiltMessages = 0;
            _liveAssistant = null;

            var actions = CreateMessageActions(_session);
            foreach (var message in _session.Messages)
            {
                AddMessageHost(message, actions);
            }

            MaterializeTail();
        }

        private ChatMessageHost AddMessageHost(ChatDisplayMessage message, MessageActions actions)
        {
            var host = new ChatMessageHost { Message = message, Actions = actions };
            host.Reserve(ReservedHeight(message));

            MessagesPanel.Children.Add(host);
            _messageHosts.Add(host);
            _unbuiltMessages++;
            if (!string.IsNullOrEmpty(message.Id))
            {
                _messageViews[message.Id] = host;
            }

            return host;
        }

        /// <summary>Добавляет уже построенное сообщение в конец ленты.</summary>
        private void AppendMessage(ChatDisplayMessage message, FrameworkElement view)
        {
            var host = AddMessageHost(message, CreateMessageActions(_session));
            FillHost(host, view);
        }

        /// <summary>
        /// Отдаёт хосту вьюшку. Единственный путь, которым хост становится построенным.
        /// </summary>
        /// <remarks>
        /// Счётчик недостроенных ведётся здесь, а не у каждого, кто наполняет хост: сообщение,
        /// добавленное сразу готовым (новый ответ, якорь варианта), прежде увеличивало его и
        /// никогда не уменьшало. Фоновая дорисовка тогда не находила что строить и заводила
        /// себя снова — до следующей пересборки ленты, на каждом кадре простоя.
        /// </remarks>
        private void FillHost(ChatMessageHost host, FrameworkElement view)
        {
            if (!host.IsMaterialized)
            {
                _unbuiltMessages--;
            }

            host.Fill(view);
            _justBuilt.Add(host);
        }

        /// <summary>
        /// Строит хвост переписки — то, что человек увидит в момент открытия чата.
        /// </summary>
        private void MaterializeTail()
        {
            var budget = DocViewportHeight() + MaterializeLead;
            var filled = 0.0;

            for (var i = _messageHosts.Count - 1; i >= 0 && filled <= budget; i--)
            {
                // Высоту берём до постройки: после неё резерв снимается, и у хоста её уже нет.
                filled += _messageHosts[i].Reserved;
                MaterializeHost(_messageHosts[i]);
            }
        }

        private double ViewportHeight()
        {
            var viewport = ChatScrollViewer.ViewportHeight;
            if (viewport > 1)
            {
                return viewport;
            }

            return ChatScrollViewer.ActualHeight > 1 ? ChatScrollViewer.ActualHeight : AssumedViewport;
        }

        /// <summary>
        /// Во сколько раз лента увеличена лупой.
        /// </summary>
        /// <remarks>
        /// Единицы у прокрутки и у ленты разные, и всё достраивание живёт на этой границе.
        /// <see cref="ChatScrollViewer"/> считает в увеличенных пикселях — ими меряются
        /// <c>VerticalOffset</c>, <c>ViewportHeight</c> и <c>ExtentHeight</c>. А <c>OffsetOf</c>
        /// и <c>Reserved</c> живут в исходных: лупа их не трогает, в том и смысл. Смешав их,
        /// на двукратном приближении мы достраивали бы вдвое меньше, чем видно, и сообщения
        /// появлялись бы прямо на кромке.
        /// </remarks>
        private double ChatScale => ChatZoomLayer.Scale;

        /// <summary>Верхняя кромка видимой области в координатах ленты.</summary>
        private double DocOffset() => ChatScrollViewer.VerticalOffset / ChatScale;

        /// <summary>Высота видимой области в координатах ленты.</summary>
        private double DocViewportHeight() => ViewportHeight() / ChatScale;

        /// <summary>Строит вьюшку сообщения и отдаёт хосту.</summary>
        private void MaterializeHost(ChatMessageHost host)
        {
            if (host.IsMaterialized)
            {
                return;
            }

            BuildInto(host);
        }

        /// <summary>
        /// Пересобирает вьюшку одного, уже построенного сообщения.
        /// </summary>
        /// <remarks>
        /// Ради этого метода он и заведён: цену сводки и цену заголовка приписывают ответу,
        /// который давно закрыт, и ценник надо перерисовать. Раньше ради него звали
        /// <c>RenderSession</c> — то есть сносили и строили заново всю ленту, а заодно сбрасывали
        /// лупу. На экране это выглядело так, будто приближение слетает само по себе через
        /// секунду после каждого ответа.
        /// <para>
        /// Живой ответ не трогаем: его вьюшку держит <c>_liveAssistant</c> и дописывает поток,
        /// а подменённая из-под него вьюшка осталась бы без остатка текста.
        /// </para>
        /// </remarks>
        private void RefreshMessageView(string? messageId)
        {
            // Сюда приезжают поздние цены (заголовок, сводка, сжатие) — цена чата (E3) следом.
            UpdateChatCostChip();
            if (string.IsNullOrEmpty(messageId) ||
                !_messageViews.TryGetValue(messageId, out var host) ||
                !host.IsMaterialized)
            {
                return;
            }

            if (FindTurn(_session.Id) is { Finished: false } live &&
                string.Equals(live.AssistantId, messageId, StringComparison.Ordinal))
            {
                return;
            }

            BuildInto(host);
        }

        /// <summary>
        /// Приводит ленту в соответствие с чатом: снимает пузыри убранных сообщений и дописывает
        /// хвост, не трогая остальные.
        /// </summary>
        /// <param name="refreshId">Сообщение, изменённое на месте, — его пузырь пересобирается.</param>
        /// <remarks>
        /// Правка, удаление и перегенерация раньше звали <c>RenderSession</c>: лента строилась
        /// заново, лупа сбрасывалась, а человек, читавший середину переписки, оказывался в
        /// другом месте. Все три только вырезают сообщения (правка ещё и меняет текст одного),
        /// поэтому достаточно снять лишние хосты. Варианты ответа ещё и меняют хвост: вместо
        /// спрятанного продолжения встаёт другое, — его хосты дописываются в конец. Если
        /// расхождение другое (сообщение переехало в середине) — строим заново, но с
        /// сохранённой лупой.
        /// </remarks>
        private void ReconcileTranscript(string? refreshId)
        {
            var present = new HashSet<ChatDisplayMessage>(_session.Messages, ReferenceEqualityComparer.Instance);
            var kept = 0;
            foreach (var host in _messageHosts)
            {
                if (!present.Contains(host.Message))
                {
                    continue;
                }

                if (kept >= _session.Messages.Count || !ReferenceEquals(_session.Messages[kept], host.Message))
                {
                    RebuildTranscript(resetZoom: false);
                    return;
                }

                kept++;
            }

            for (var i = _messageHosts.Count - 1; i >= 0; i--)
            {
                var host = _messageHosts[i];
                if (present.Contains(host.Message))
                {
                    continue;
                }

                if (!host.IsMaterialized)
                {
                    _unbuiltMessages--;
                }

                if (!string.IsNullOrEmpty(host.Id) &&
                    _messageViews.TryGetValue(host.Id, out var mapped) &&
                    ReferenceEquals(mapped, host))
                {
                    _messageViews.Remove(host.Id);
                }

                MessagesPanel.Children.Remove(host);
                _messageHosts.RemoveAt(i);
            }

            // Хвост, которого в ленте ещё нет: показанный вариант после переключения или новый
            // якорь после развилки. Строится по мере надобности, как при открытии чата.
            if (kept < _session.Messages.Count)
            {
                var actions = CreateMessageActions(_session);
                for (var i = kept; i < _session.Messages.Count; i++)
                {
                    AddMessageHost(_session.Messages[i], actions);
                }
            }

            RefreshMessageView(refreshId);
            UpdateAttachmentWarning();

            // Подпись источника у прикреплённой цитаты («из последнего ответа», «удалён») могла
            // стать неправдой: ответ, на который она ссылалась, убрали или спрятали в вариант.
            // Плашка «Ответить» держала выделение в пузыре, которого, возможно, уже нет.
            RefreshQuoteRows();
            HideReplyPill();
            MaybeAutoscroll();
            MaterializeAroundViewport();
            ScheduleBackgroundFill();
        }

        private void BuildInto(ChatMessageHost host)
        {
            // Вьюшку сейчас заменят новой: выделение, под которое открыта «Ответить», исчезнет
            // вместе со старой — а цена сводки как раз и приезжает через секунду после ответа.
            CloseReplyPillFor(host);

            var message = host.Message;
            if (message.Role == "user")
            {
                FillHost(host, WithCompactionMark(message, ChatMessageViews.CreateUser(this, message, host.Actions, ActiveDateFormat).Root));
                return;
            }

            var view = ChatMessageViews.CreateAssistant(this, message, host.Actions, ActiveDateFormat);
            FillHost(host, WithCompactionMark(message, view.Root));

            // Вернулись в чат, который ещё отвечает, — подхватываем его вьюшку заново.
            var live = FindTurn(_session.Id);
            if (live is { Finished: false } && message.Id == live.AssistantId)
            {
                _liveAssistant = view;
            }
        }

        /// <summary>
        /// Достраивает всё, что попало в видимую область с запасом, не сдвигая того, что человек
        /// сейчас читает.
        /// </summary>
        private void MaterializeAroundViewport()
        {
            if (_materializing || _unbuiltMessages == 0)
            {
                return;
            }

            var top = DocOffset() - MaterializeLead;
            var bottom = DocOffset() + DocViewportHeight() + MaterializeLead;

            MaterializeAnchored(() =>
            {
                var built = 0;
                foreach (var host in _messageHosts)
                {
                    if (host.IsMaterialized)
                    {
                        continue;
                    }

                    var y = OffsetOf(host);
                    if (y + host.Reserved < top || y > bottom)
                    {
                        continue;
                    }

                    MaterializeHost(host);
                    built++;
                }

                return built;
            });
        }

        /// <summary>
        /// Заводит фоновую дорисовку остатка, если она ещё не идёт. Той же очередью отпускаются
        /// сообщения, замороженные на время изменения размера окна (<see cref="ThawNextChunk"/>).
        /// </summary>
        private void ScheduleBackgroundFill()
        {
            if (_fillScheduled || (_unbuiltMessages == 0 && !ThawPending))
            {
                return;
            }

            _fillScheduled = true;
            Dispatcher.BeginInvoke(FillNextChunk, DispatcherPriority.Background);
        }

        /// <summary>
        /// Достраивает очередную порцию — с конца ленты к началу.
        /// </summary>
        /// <remarks>
        /// Именно снизу вверх: человек смотрит на конец чата, и всё достраиваемое растёт выше
        /// видимой области. Порциями по времени и фоновым приоритетом — чтобы между ними
        /// успевали пройти кадры, ввод и прокрутка. Пока лента в движении (лупа, инерция),
        /// дорисовка ждёт: постройка выше видимого сдвинула бы текст из-под жеста.
        /// </remarks>
        private void FillNextChunk()
        {
            _fillScheduled = false;
            if (_unbuiltMessages == 0 && !ThawPending)
            {
                return;
            }

            // Окно тянут: порция стоила бы раскладки, которую никто не просил, а якорь у ленты
            // сейчас занят. Конец жеста заведёт очередь сам (OnWindowResizeEnded).
            if (_windowSizing)
            {
                return;
            }

            if (TranscriptInMotion)
            {
                RetryBackgroundFillLater();
                return;
            }

            // Сперва — отпустить замороженное: оно уже на экране рядом, а недостроенное подождёт.
            if (ThawPending)
            {
                ThawNextChunk();
                ScheduleBackgroundFill();
                return;
            }

            var built = 0;
            MaterializeAnchored(() =>
            {
                var deadline = Stopwatch.GetTimestamp() + BackgroundFillBudget;
                if (_fillCursor < 0 || _fillCursor >= _messageHosts.Count)
                {
                    _fillCursor = _messageHosts.Count - 1;
                }

                for (; _fillCursor >= 0; _fillCursor--)
                {
                    if (built > 0 && Stopwatch.GetTimestamp() > deadline)
                    {
                        break;
                    }

                    var host = _messageHosts[_fillCursor];
                    if (!host.IsMaterialized)
                    {
                        MaterializeHost(host);
                        built++;
                    }
                }

                return built;
            });

            // Курсор дошёл до начала, а недостроенное осталось — его добавили позже позади
            // курсора (хвост после варианта ответа). Следующий заход начнёт с конца ленты.
            if (built == 0)
            {
                _fillCursor = -1;
                _unbuiltMessages = _messageHosts.Count(host => !host.IsMaterialized);
            }

            ScheduleBackgroundFill();
        }

        /// <summary>
        /// Дорисовка встала из-за движения ленты. У инерции прокрутки нет события «доехала», поэтому
        /// проверяем снова через короткую паузу; конец жеста лупы будит дорисовку сам
        /// (<see cref="OnChatZoomSettled"/>).
        /// </summary>
        private void RetryBackgroundFillLater()
        {
            if (_fillRetry is null)
            {
                _fillRetry = new DispatcherTimer(DispatcherPriority.Background) { Interval = BackgroundFillRetry };
                _fillRetry.Tick += (_, _) =>
                {
                    _fillRetry.Stop();
                    ScheduleBackgroundFill();
                };
            }

            _fillRetry.Stop();
            _fillRetry.Start();
        }

        /// <summary>
        /// Выполняет постройку так, чтобы то, что человек сейчас читает, осталось на месте.
        /// </summary>
        /// <remarks>
        /// Якорь — первое сообщение на границе видимой области. Насколько оно уехало после
        /// постройки, настолько же двигается прокрутка. Пока лента пришпилена к низу, за это
        /// отвечает автопрокрутка, и вмешиваться нельзя; пока лента в движении, прокрутка
        /// принадлежит лупе или <see cref="SmoothScroll"/> — тоже нельзя: два хозяина смещения
        /// перетягивали бы его кадр за кадром.
        /// </remarks>
        private void MaterializeAnchored(Func<int> build)
        {
            // В обоих случаях якорь не нужен, и искать его (проход по ленте с пересчётом
            // координат) незачем.
            var anchor = !_stickToBottom && !TranscriptInMotion
                ? AnchorHost()
                : null;
            var before = anchor is null ? 0 : OffsetOf(anchor);

            _materializing = true;
            try
            {
                if (build() == 0)
                {
                    return;
                }

                MessagesPanel.UpdateLayout();
                RememberHeights(_justBuilt);
                _justBuilt.Clear();

                if (anchor is null)
                {
                    return;
                }

                // Сдвиг замерен в координатах ленты, а прокрутке его отдают в увеличенных.
                var shift = (OffsetOf(anchor) - before) * ChatScale;
                if (Math.Abs(shift) > 0.5)
                {
                    _autoScrolling = true;
                    ChatScrollViewer.ScrollToVerticalOffset(ChatScrollViewer.VerticalOffset + shift);
                    Dispatcher.BeginInvoke(() => _autoScrolling = false, DispatcherPriority.Background);
                }
            }
            finally
            {
                _materializing = false;
            }
        }

        /// <summary>Первое сообщение, начинающееся не выше верхней кромки видимой области.</summary>
        private ChatMessageHost? AnchorHost()
        {
            var top = DocOffset();
            foreach (var host in _messageHosts)
            {
                if (OffsetOf(host) >= top)
                {
                    return host;
                }
            }

            return _messageHosts.Count > 0 ? _messageHosts[^1] : null;
        }

        private double OffsetOf(ChatMessageHost host)
        {
            try
            {
                return host.TranslatePoint(default, MessagesPanel).Y;
            }
            catch (InvalidOperationException)
            {
                // Хост ещё не в общем дереве визуалов — до первой раскладки это нормально.
                return 0;
            }
        }

        /// <summary>
        /// Запоминает высоты построенных хостов из списка. Зовут её с только что построенными —
        /// проход по всей ленте на каждую порцию дорисовки делал её квадратичной — и со всей
        /// лентой перед сменой чата.
        /// </summary>
        private void RememberHeights(IReadOnlyList<ChatMessageHost> hosts)
        {
            if (_messageHeights.Count > MessageHeightsLimit)
            {
                _messageHeights.Clear();
            }

            foreach (var host in hosts)
            {
                if (host.IsMaterialized && host.ActualHeight > 1 && !string.IsNullOrEmpty(host.Id))
                {
                    _messageHeights[host.Id] = (host.ActualHeight, host.LaidOutWidth);
                }
            }
        }

        /// <summary>
        /// Сколько места занять под ещё не построенное сообщение: измеренная высота, если она
        /// известна, иначе прикидка по разметке (<see cref="MessageHeightEstimate"/>).
        /// </summary>
        private double ReservedHeight(ChatDisplayMessage message)
        {
            // Ширина ленты в её собственных координатах: лупа её не меняет.
            var width = MessagesPanel.ActualWidth > 1 ? MessagesPanel.ActualWidth : ChatScrollViewer.ViewportWidth / ChatScale;
            if (!string.IsNullOrEmpty(message.Id) &&
                _messageHeights.TryGetValue(message.Id, out var measured) &&
                (double.IsNaN(measured.Width) || Math.Abs(measured.Width - width) < 1))
            {
                return measured.Height;
            }

            return MessageHeightEstimate.For(message, width);
        }
    }
}
