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
        /// Измеренные высоты сообщений по их идентификаторам.
        /// </summary>
        /// <remarks>
        /// Переживает переключение чатов: вернувшись в чат, лента резервирует ровно те высоты,
        /// что были в прошлый раз, и достраивание уже ничего не двигает. Оценка по длине текста
        /// нужна только при самом первом показе.
        /// </remarks>
        private readonly Dictionary<string, double> _messageHeights = new(StringComparer.Ordinal);

        /// <summary>Потолок памяти высот. За сеанс столько сообщений не открывают.</summary>
        private const int MessageHeightsLimit = 4000;

        /// <summary>Запас вокруг видимой области, в пикселях. Примерно два-три сообщения.</summary>
        private const double MaterializeLead = 700;

        /// <summary>Сколько сообщений достраивать за один заход фоновой дорисовки.</summary>
        private const int BackgroundFillChunk = 3;

        /// <summary>Высота видимой области, пока настоящая неизвестна (окно ещё не разложено).</summary>
        private const double AssumedViewport = 800;

        private bool _fillScheduled;

        /// <summary>Идёт материализация: прокрутка, которую она вызовет, — наша, а не человека.</summary>
        private bool _materializing;

        /// <summary>
        /// Сколько сообщений ещё не построено. Счётчиком, а не проходом по списку: обе проверки
        /// «осталось ли что-нибудь» случаются на каждое движение прокрутки.
        /// </summary>
        private int _unbuiltMessages;

        /// <summary>Ставит в ленту хосты под все сообщения чата и строит только видимую часть.</summary>
        private void BuildMessageHosts()
        {
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
            host.Fill(view);
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

            _unbuiltMessages--;
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
        /// Приводит ленту в соответствие с чатом, из которого убрали сообщения, не трогая
        /// остальные.
        /// </summary>
        /// <param name="refreshId">Сообщение, изменённое на месте, — его пузырь пересобирается.</param>
        /// <remarks>
        /// Правка, удаление и перегенерация раньше звали <c>RenderSession</c>: лента строилась
        /// заново, лупа сбрасывалась, а человек, читавший середину переписки, оказывался в
        /// другом месте. Все три только вырезают сообщения (правка ещё и меняет текст одного),
        /// поэтому достаточно снять лишние хосты. Если расхождение оказалось не удалением —
        /// строим заново, но с сохранённой лупой.
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

            if (kept != _session.Messages.Count)
            {
                RebuildTranscript(resetZoom: false);
                return;
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

            RefreshMessageView(refreshId);
            UpdateAttachmentWarning();
            MaybeAutoscroll();
            MaterializeAroundViewport();
            ScheduleBackgroundFill();
        }

        private void BuildInto(ChatMessageHost host)
        {
            var message = host.Message;
            if (message.Role == "user")
            {
                host.Fill(ChatMessageViews.CreateUser(this, message, host.Actions).Root);
                return;
            }

            var view = ChatMessageViews.CreateAssistant(this, message, host.Actions, ActiveDateFormat);
            host.Fill(view.Root);

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

        /// <summary>Заводит фоновую дорисовку остатка, если она ещё не идёт.</summary>
        private void ScheduleBackgroundFill()
        {
            if (_fillScheduled || _unbuiltMessages == 0)
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
        /// видимой области. Порциями и фоновым приоритетом — чтобы между ними успевали пройти
        /// кадры, ввод и прокрутка.
        /// </remarks>
        private void FillNextChunk()
        {
            _fillScheduled = false;

            MaterializeAnchored(() =>
            {
                var built = 0;
                for (var i = _messageHosts.Count - 1; i >= 0 && built < BackgroundFillChunk; i--)
                {
                    if (_messageHosts[i].IsMaterialized)
                    {
                        continue;
                    }

                    MaterializeHost(_messageHosts[i]);
                    built++;
                }

                return built;
            });

            ScheduleBackgroundFill();
        }

        /// <summary>
        /// Выполняет постройку так, чтобы то, что человек сейчас читает, осталось на месте.
        /// </summary>
        /// <remarks>
        /// Якорь — первое сообщение на границе видимой области. Насколько оно уехало после
        /// постройки, настолько же двигается прокрутка. Пока лента пришпилена к низу, за это
        /// отвечает автопрокрутка, и вмешиваться нельзя; во время инерции прокрутка принадлежит
        /// <see cref="SmoothScroll"/> — тоже нельзя.
        /// </remarks>
        private void MaterializeAnchored(Func<int> build)
        {
            // Пока лента пришпилена к низу, место держит автопрокрутка, а во время инерции
            // прокрутка принадлежит SmoothScroll — в обоих случаях якорь не нужен, и искать
            // его (проход по ленте с пересчётом координат) незачем.
            var anchor = !_stickToBottom && !SmoothScroll.IsAnimating(ChatScrollViewer)
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
                RememberHeights();

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

        private void RememberHeights()
        {
            if (_messageHeights.Count > MessageHeightsLimit)
            {
                _messageHeights.Clear();
            }

            foreach (var host in _messageHosts)
            {
                if (host.IsMaterialized && host.ActualHeight > 1 && !string.IsNullOrEmpty(host.Id))
                {
                    _messageHeights[host.Id] = host.ActualHeight;
                }
            }
        }

        /// <summary>
        /// Сколько места занять под ещё не построенное сообщение: измеренная высота, если она
        /// известна, иначе прикидка по длине текста.
        /// </summary>
        private double ReservedHeight(ChatDisplayMessage message)
        {
            if (!string.IsNullOrEmpty(message.Id) &&
                _messageHeights.TryGetValue(message.Id, out var measured))
            {
                return measured;
            }

            var text = message.Text ?? "";
            var user = message.Role == "user";

            // Символов в строке и высота служебной обвязки — прикидка, а не расчёт: полоса
            // прокрутки должна быть примерно верной, а точные высоты приедут с постройкой.
            var perLine = user ? 70 : 95;
            var lines = (text.Length / perLine) + text.AsSpan().Count('\n') + 1;
            var chrome = user ? 34 : 96;
            var images = message.Images.Count > 0 ? 120 : 0;

            return Math.Clamp((lines * 21.0) + chrome + images, 40, 4000);
        }
    }
}
