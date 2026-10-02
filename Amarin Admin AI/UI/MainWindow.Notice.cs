using System.Windows;
using System.Windows.Threading;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>Чем окрашена метка уведомления: сведение, предупреждение, необратимое.</summary>
    internal enum NoticeTone
    {
        Info,
        Warning,
        Danger
    }

    /// <summary>
    /// Одно окно на программу для «сообщить» и «переспросить».
    /// </summary>
    /// <remarks>
    /// Раньше это делал системный <see cref="MessageBox"/>: чужой стиль, системный масштаб
    /// (<see cref="UiScale"/> подменяет DPI только нашему окну) и никакой возможности положить
    /// рядом кнопку, которая ведёт туда, где проблему решают. Вопрос асинхронный: обработчик
    /// кнопки ждёт ответа, не держа поток интерфейса.
    /// </remarks>
    public partial class MainWindow
    {
        private TaskCompletionSource<bool>? _notice;

        /// <summary>
        /// Показывает уведомление и ждёт ответа. True — нажата основная кнопка.
        /// </summary>
        /// <param name="secondary">Подпись второй кнопки; null — кнопка одна.</param>
        /// <param name="extra">Своё содержимое под текстом: галочка, поле для слова-подтверждения.</param>
        /// <remarks>
        /// Новый вопрос поверх незакрытого снимает прежний ответом «нет»: два окна одно над другим
        /// путали бы, на какой вопрос отвечает нажатая кнопка.
        /// </remarks>
        /// <summary>Сообщить без вопроса: одна кнопка «Закрыть».</summary>
        internal void Inform(string title, string text, NoticeTone tone = NoticeTone.Warning) =>
            Detached.Run(ShowNoticeAsync(title, text, Loc.Get("S.Common.Close"), null, tone), "notice");

        /// <summary>
        /// Сообщить из кода, у которого есть только элемент, а не окно (вложение в ленте). В
        /// главном окне — своим окном; вне его (тесты, отдельные окна) — системным, как раньше.
        /// </summary>
        internal static void Inform(FrameworkElement host, string title, string text)
        {
            switch (Window.GetWindow(host))
            {
                case MainWindow main:
                    main.Inform(title, text);
                    break;
                case { } other:
                    MessageBox.Show(other, text, title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    break;
                default:
                    MessageBox.Show(text, title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    break;
            }
        }

        internal Task<bool> ShowNoticeAsync(
            string title,
            string text,
            string primary,
            string? secondary,
            NoticeTone tone = NoticeTone.Warning,
            FrameworkElement? extra = null)
        {
            _notice?.TrySetResult(false);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _notice = completion;

            NoticeTitle.Text = title;
            NoticeText.Text = text;
            NoticeText.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
            // Необратимое («Удалить») — красной подписью; вопрос с одной кнопкой и обычный — главной.
            NoticePrimaryButton.Style = (Style)FindResource(
                tone == NoticeTone.Danger && secondary is not null ? "DialogDangerButton" : "DialogPrimaryButton");

            NoticeExtra.Content = extra;
            NoticeExtra.Visibility = extra is null ? Visibility.Collapsed : Visibility.Visible;

            NoticePrimaryButton.Content = primary;
            NoticePrimaryButton.IsEnabled = true;
            NoticeSecondaryButton.Content = secondary ?? "";
            NoticeSecondaryButton.Visibility = secondary is null ? Visibility.Collapsed : Visibility.Visible;

            NoticeOverlay.Visibility = Visibility.Visible;
            Chat.IsHitTestVisible = false;

            // Фокус на основную кнопку, а не на поле ввода: Enter отвечает на вопрос, а не
            // отправляет сообщение, набранное под окном.
            Dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(_notice, completion))
                {
                    NoticePrimaryButton.Focus();
                }
            }, DispatcherPriority.Input);

            return completion.Task;
        }

        /// <summary>Открыто ли уведомление: пока открыто, горячие клавиши и набор идут не в чат.</summary>
        internal bool IsNoticeOpen => NoticeOverlay.Visibility == Visibility.Visible;

        /// <summary>Есть ли чем платить за ответ. Одна проверка на все места, где начинается ход.</summary>
        private bool HasUsableKey() => !string.IsNullOrWhiteSpace(_services?.Options.ApiKey);

        /// <summary>
        /// Ключа нет: говорит об этом и ведёт на страницу, где его добавляют.
        /// </summary>
        /// <remarks>
        /// Прежний текст отправлял в переменную окружения <c>VENICE_API_KEY</c> — то есть мимо
        /// страницы «Key &amp; Info», где ключ добавляется за секунду, и мимо OpenRouter вовсе.
        /// </remarks>
        private void ShowNoKeyNotice() => Detached.Run(NoKeyNoticeAsync(), "no_key_notice");

        private async Task NoKeyNoticeAsync()
        {
            var open = await ShowNoticeAsync(
                Loc.Get("S.Notice.NoKeyTitle"),
                Loc.Get("S.Turn.NoApiKey"),
                Loc.Get("S.Notice.OpenKeyInfo"),
                Loc.Get("S.Common.Close"));
            if (open)
            {
                OpenSettingsPage(NavKey);
            }
        }

        /// <summary>Открывает настройки на нужной странице.</summary>
        private void OpenSettingsPage(System.Windows.Controls.RadioButton page)
        {
            if (SettingsOverlay.Visibility != Visibility.Visible)
            {
                SettingsOverlay.Visibility = Visibility.Visible;
                LoadSettingsUi();
            }

            // Checked заводит и наполняет страницу.
            page.IsChecked = true;
        }

        private void NoticePrimaryButton_Click(object sender, RoutedEventArgs e) => CloseNotice(true);

        private void NoticeSecondaryButton_Click(object sender, RoutedEventArgs e) => CloseNotice(false);

        private void CloseNotice(bool confirmed)
        {
            var completion = _notice;
            _notice = null;

            NoticeOverlay.Visibility = Visibility.Collapsed;
            NoticeExtra.Content = null;
            Chat.IsHitTestVisible =
                ConfirmationOverlay.Visibility != Visibility.Visible &&
                DomainOverlay.Visibility != Visibility.Visible &&
                JournalOverlay.Visibility != Visibility.Visible;

            completion?.TrySetResult(confirmed);
        }
    }
}
