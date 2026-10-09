using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Автоблокировка и «Заблокировать сейчас»: окно закрывается экраном с паролем профиля, а
    /// ходы и вопросы под ним продолжают жить.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Простой — время без ввода в окне программы: клавиши, мышь, колесо. Таймер спрашивает раз в
    /// полминуты, решение — за <see cref="AutoLock.IsDue"/>.
    /// </para>
    /// <para>
    /// Слои под экраном на время блокировки становятся невидимыми и неактивными. Одной подложки
    /// мало: заливка окна в оформлении «стекло» полупрозрачна, и переписка просвечивала бы, а
    /// вопрос об опасном действии, пришедший уже под блокировкой, лёг бы поверх неё. Неактивные
    /// слои не берут и фокус клавиатуры — Tab из поля пароля в поле сообщения не уведёт.
    /// </para>
    /// </remarks>
    public partial class MainWindow
    {
        private readonly DispatcherTimer _lockTimer = new() { Interval = TimeSpan.FromSeconds(30) };
        private readonly List<UIElement> _hiddenByLock = [];
        private DateTime _lastInputUtc = DateTime.UtcNow;
        private Point _lastMousePosition;
        private bool _autoLockWired;

        /// <summary>Окно закрыто экраном блокировки.</summary>
        internal bool IsLocked => LockOverlay.Visibility == Visibility.Visible;

        /// <summary>Заводит учёт ввода и таймер. Один раз, из <c>AttachServices</c>.</summary>
        private void WireAutoLock()
        {
            if (_autoLockWired)
            {
                return;
            }

            _autoLockWired = true;
            _lockTimer.Tick += (_, _) => CheckAutoLock();
            PreviewMouseDown += (_, _) => NoteInput();
            PreviewMouseWheel += (_, _) => NoteInput();
            PreviewMouseMove += (_, e) =>
            {
                // Окно шлёт MouseMove и без движения — при перерисовке под курсором; такое за ввод
                // не считаем, иначе блокировка не наступала бы, пока идёт ответ.
                var position = e.GetPosition(this);
                if (position != _lastMousePosition)
                {
                    _lastMousePosition = position;
                    NoteInput();
                }
            };

            LockOverlay.UnlockRequested += (_, password) => Detached.Run(TryUnlockAsync(password), "unlock");
            LockOverlay.MinimizeRequested += (_, _) => WindowState = WindowState.Minimized;
            LockOverlay.MaximizeRequested += (_, _) => MaximizeButton_Click(this, new RoutedEventArgs());
            LockOverlay.CloseRequested += (_, _) => RequestExit();

            _lockTimer.Start();
            Closed += (_, _) => _lockTimer.Stop();
        }

        private void NoteInput() => _lastInputUtc = DateTime.UtcNow;

        private bool ActiveProfileHasPassword => _services is not null && ActiveProfile.HasPassword;

        private void CheckAutoLock()
        {
            if (_services is null || IsLocked)
            {
                return;
            }

            if (AutoLock.IsDue(_services.Settings.AutoLockMinutes, ActiveProfileHasPassword, _lastInputUtc, DateTime.UtcNow))
            {
                LockNow();
            }
        }

        /// <summary>Закрывает окно экраном блокировки. Без пароля у профиля — ничего не делает.</summary>
        internal void LockNow()
        {
            if (IsLocked || !ActiveProfileHasPassword)
            {
                return;
            }

            // Выпадашки — отдельные окна поверх нашего: под блокировкой они показывали бы то,
            // что человек в них оставил.
            PopupManager.CloseAll();

            foreach (UIElement layer in ScaledRoot.Children)
            {
                if (ReferenceEquals(layer, LockOverlay) || layer.Opacity == 0)
                {
                    continue;
                }

                _hiddenByLock.Add(layer);
                layer.Opacity = 0;
                layer.IsEnabled = false;
            }

            LockOverlay.Prepare(ActiveProfile.Name);
            LockOverlay.Visibility = Visibility.Visible;
            LockOverlay.FocusPassword();
            RefillReminders();
        }

        /// <remarks>
        /// Проверка на рабочем потоке: PBKDF2 на двести тысяч итераций — это заметная доля
        /// секунды, и интерфейс, включая идущий ответ, стоял бы. Неверный пароль отвечает с
        /// задержкой: подбирать его вслепую на этом экране дольше.
        /// </remarks>
        private async Task TryUnlockAsync(string password)
        {
            if (!IsLocked || _services is null)
            {
                return;
            }

            var profile = ActiveProfile;
            var hash = profile.PasswordHash;
            var salt = profile.PasswordSalt;
            var ok = await Task.Run(() => PasswordHash.Verify(password, hash, salt));
            if (!ok)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(700));
                LockOverlay.ShowError(Loc.Get("S.Lock.WrongPassword"));
                return;
            }

            Unlock();
        }

        private void Unlock()
        {
            foreach (var layer in _hiddenByLock)
            {
                layer.Opacity = 1;
                layer.IsEnabled = true;
            }

            _hiddenByLock.Clear();
            LockOverlay.Visibility = Visibility.Collapsed;
            RefillReminders();
            NoteInput();
            FocusMessageInput();
        }

        /// <summary>
        /// Клавиатура под блокировкой: всё — полю пароля, окну — ничего.
        /// </summary>
        /// <returns>
        /// <c>true</c> — окну разбирать нажатие дальше не надо: либо оно адресовано экрану
        /// блокировки, либо погашено здесь.
        /// </returns>
        /// <remarks>
        /// Без этого набранное под блокировкой уходило бы в поле сообщения (окно само переводит
        /// туда случайный ввод), а горячая клавиша завела бы новый чат за экраном.
        /// </remarks>
        private bool KeepKeyboardOnLock(RoutedEventArgs e)
        {
            if (!IsLocked)
            {
                return false;
            }

            if (!LockOverlay.IsKeyboardFocusWithin)
            {
                e.Handled = true;
                LockOverlay.FocusPassword();
            }

            return true;
        }

        /// <summary>
        /// Текст уведомления о готовом ответе: под блокировкой — без самого ответа.
        /// </summary>
        /// <remarks>
        /// Уведомление — отдельное окно в углу экрана, и первая строка ответа в нём была бы видна
        /// любому, кто подошёл к заблокированной программе.
        /// </remarks>
        private string ToastText(string text) => IsLocked ? Loc.Get("S.Lock.ToastHidden") : text;
    }
}
