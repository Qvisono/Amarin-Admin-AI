using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace Amarin.UI
{
    /// <summary>
    /// Что окно сообщает лисёнку Розе. Только простые типы и ни одного типа сборки <c>Amarin.Fox</c>.
    /// </summary>
    /// <remarks>
    /// Окно держит это поле с первого кадра, а Роза приходит через пять секунд после него. Тип из
    /// <c>Amarin.Fox</c> в поле или в сигнатуре окна загрузил бы сборку Розы на запуске, а с ней
    /// компиляцию её кода — ровно то время, которое запуск не должен терять. Сборку загружает
    /// только <see cref="MainWindow.CreateFox"/> — один метод, который зовёт таймер.
    /// </remarks>
    internal interface IFoxHooks
    {
        /// <summary>Роза в руках или гоняется за лазером: Esc отпускает его, а не останавливает ответ.</summary>
        bool Holding { get; }

        void LetGo();

        void Sent(string text);

        void AnswerStarted();

        void AnswerFinished(string text);

        void AnswerFailed();

        void AnswerCancelled();

        void AgentWorking();

        void SpendWarning();

        /// <summary>Заведён новый чат — кнопкой, клавишей или шалостью самого Розы.</summary>
        void ChatCreated();

        void ChatsDeleted(int count);

        void ThemeChanged();

        /// <summary>Показана страница настроек: <c>keys</c> — страница ключей, остальное — просто настройки.</summary>
        void SettingsPage(string page);

        void DangerAsked();

        void Locked(bool locked);

        void ProfileChanged();

        void BeginImport();

        void EndImport();

        /// <summary>Окно закрывается: отложенное — на диск.</summary>
        void Stop();
    }

    public partial class MainWindow
    {
        /// <summary>Через сколько после первого кадра приходит Роза.</summary>
        internal static readonly TimeSpan FoxDelay = TimeSpan.FromSeconds(5);

        private IFoxHooks? _fox;
        private DispatcherTimer? _foxTimer;

        /// <summary>Роза в окне; null — до пятой секунды, без служб, в тестах без явного старта.</summary>
        internal IFoxHooks? Fox => _fox;

        /// <summary>Заводит приход Розы через <see cref="FoxDelay"/>. Зовётся после первого кадра.</summary>
        /// <param name="inTests">
        /// Оконные тесты поднимают то же окно; Роза в них сама не приходит — иначе таймер сработал бы
        /// посреди чужого теста и положил в слой окна то, чего тест не ждёт. Тест, которому он нужен,
        /// заводит его явно.
        /// </param>
        /// <param name="delay">Тестам — без пяти секунд ожидания.</param>
        internal void StartFox(bool inTests = false, TimeSpan? delay = null)
        {
            if (_services is null || _fox is not null || _foxTimer is not null || (!IsRealApp && !inTests) ||
                !_services.Settings.Fox.Enabled)
            {
                return;
            }

            _foxTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = delay ?? FoxDelay };
            _foxTimer.Tick += (_, _) =>
            {
                _foxTimer?.Stop();
                _foxTimer = null;
                if (_fox is null && _services is not null && _services.Settings.Fox.Enabled)
                {
                    _fox = CreateFox();
                }
            };
            _foxTimer.Start();
            Closed += (_, _) => StopFox();
        }

        /// <summary>Убирает Розу из окна: выключили в настройках или окно закрывается.</summary>
        internal void StopFox()
        {
            _foxTimer?.Stop();
            _foxTimer = null;
            _fox?.Stop();
            _fox = null;
        }

        /// <summary>
        /// Единственное место, где окно знает тип с Розой. Не встраивается: иначе сборка
        /// <c>Amarin.Fox</c> грузилась бы вместе с вызывающим методом, а не через пять секунд.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private IFoxHooks CreateFox() => new FoxWindowHost(this);
    }
}
