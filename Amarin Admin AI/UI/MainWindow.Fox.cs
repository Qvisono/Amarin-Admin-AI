using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Amarin.Core;

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

        /// <summary>Сменились настройки Розы (размер): пересесть.</summary>
        void SettingsChanged();

        /// <summary>Отложенное в памяти Розы — на диск: окна дружбы и дневника читают файлы.</summary>
        void Flush();

        /// <summary>Позвать спрятавшегося или убежавшего раньше срока.</summary>
        void Unhide();

        /// <summary>«Начать дружбу заново»: память с нуля, дневник остаётся.</summary>
        void ResetFriendship();

        /// <summary>«Забыть» в «Нашей дружбе»: заметки и разговоры, дружба остаётся.</summary>
        void ForgetMemory();

        /// <summary>Открыт другой чат: заголовок (его читает только инстинкт) и сколько в нём сообщений.</summary>
        void ChatOpened(string title, int messages);

        /// <summary>Началась запись голоса.</summary>
        void RecordingStarted();

        /// <summary>К сообщению прикрепили файл или картинку.</summary>
        void AttachmentAdded();

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
        private bool _foxCloseHooked;

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
            if (!_foxCloseHooked)
            {
                _foxCloseHooked = true;
                Closed += (_, _) => StopFox();
            }
        }

        /// <summary>Настройки Розы поменялись: выключили — уходит, включили — приходит, размер — пересаживается.</summary>
        internal void ApplyFoxSettings()
        {
            if (_services is null)
            {
                return;
            }

            if (!_services.Settings.Fox.Enabled)
            {
                StopFox();
                return;
            }

            if (_fox is null)
            {
                StartFox(delay: TimeSpan.FromMilliseconds(1));
            }
            else
            {
                _fox.SettingsChanged();
            }
        }

        /// <summary>«Наша дружба»: уровень, привязанность, дни вместе, счётчики дня.</summary>
        internal async Task ShowFoxFriendshipAsync()
        {
            if (_services is null)
            {
                return;
            }

            await ShowNoticeAsync(
                Loc.Get("S.FoxSettings.Ours"), "", Loc.Get("S.Common.Close"), null, NoticeTone.Info, FoxPanels.Friendship(this, _services));
        }

        /// <summary>Дневник Розы: записи по дням, свежие сверху.</summary>
        internal async Task ShowFoxDiaryAsync()
        {
            if (_services is null)
            {
                return;
            }

            await ShowNoticeAsync(
                Loc.Get("S.FoxSettings.Diary"), "", Loc.Get("S.Common.Close"), null, NoticeTone.Info, FoxPanels.Diary(this, _services));
        }

        /// <summary>«Начать дружбу заново» — с вопросом: привязанность и памятные даты не вернуть.</summary>
        internal async Task ResetFoxAsync()
        {
            if (_services is null ||
                !await ShowNoticeAsync(
                    Loc.Get("S.FoxSettings.ResetTitle"),
                    Loc.Get("S.FoxSettings.ResetText"),
                    Loc.Get("S.FoxSettings.ResetButton"),
                    Loc.Get("S.Common.Cancel"),
                    NoticeTone.Danger))
            {
                return;
            }

            if (_fox is not null)
            {
                _fox.ResetFriendship();
            }
            else
            {
                FoxPanels.ResetOnDisk(_services);
            }
        }

        /// <summary>«Забыть» в «Нашей дружбе» — с вопросом: заметки и разговоры не вернуть.</summary>
        internal async Task ForgetFoxMemoryAsync()
        {
            if (_services is null ||
                !await ShowNoticeAsync(
                    Loc.Get("S.FoxSettings.ForgetTitle"),
                    Loc.Get("S.FoxSettings.ForgetText"),
                    Loc.Get("S.FoxSettings.Forget"),
                    Loc.Get("S.Common.Cancel"),
                    NoticeTone.Danger))
            {
                return;
            }

            if (_fox is not null)
            {
                _fox.ForgetMemory();
            }
            else
            {
                FoxPanels.ForgetOnDisk(_services);
            }
        }

        /// <summary>«Настройки Розы» из его меню: «Модели», подстраница «Лисёнок Роза».</summary>
        internal void OpenFoxSettings()
        {
            OpenSettings(SettingsUi.NavModels);
            var models = SettingsUi.Page<SettingsModelsPage>();
            SettingsDrill.Open(models.ModelsFoxSub, models.FoxLinkRow);
        }

        /// <summary>Значение у строки «Лисёнок Роза ›»: включён или нет.</summary>
        private void RefreshFoxLink()
        {
            if (_services is not null && BuiltPage<SettingsModelsPage>() is { } models)
            {
                models.FoxLinkRow.Tag = FoxSettingsBlock.Summary(_services.Settings);
            }
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
