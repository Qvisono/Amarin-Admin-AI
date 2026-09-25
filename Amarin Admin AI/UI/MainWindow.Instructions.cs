using System.Windows;

namespace Amarin.UI
{
    /// <summary>
    /// Мост между страницей «Инструкции» и главным окном.
    /// </summary>
    /// <remarks>
    /// Вопросы «удалить?» и «отменить правки?» страница задаёт оверлеем окна, а не своим окном:
    /// <see cref="UiScale"/> подменяет DPI одному HWND, и второе окно рисовалось бы в системном
    /// масштабе, разъезжаясь с остальной программой.
    /// </remarks>
    public partial class MainWindow
    {
        private void NavInstructions_Checked(object sender, RoutedEventArgs e)
        {
            if (_services is null)
            {
                return;
            }

            InstructionsPage.Attach(_services);
            InstructionsPage.Activate();
        }

        /// <summary>
        /// Общий вопрос «да/нет» поверх настроек. Тот же оверлей, что у заготовок промпта:
        /// заводить третий одинаковый незачем.
        /// </summary>
        internal void AskConfirm(string title, string text, Action commit) =>
            AskPrompt(title, text, commit);

        /// <summary>
        /// Открывает настройки на странице «Инструкции» и в ней — нужную инструкцию.
        /// Зовётся отметкой «по инструкции» под ответом модели.
        /// </summary>
        internal void OpenInstruction(string id)
        {
            if (_services is null || string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            if (SettingsOverlay.Visibility != Visibility.Visible)
            {
                SettingsOverlay.Visibility = Visibility.Visible;
                LoadSettingsUi();
            }

            // Checked заводит и наполняет страницу; если она уже выбрана, событие не придёт,
            // но страница и так привязана.
            NavInstructions.IsChecked = true;
            InstructionsPage.Attach(_services);
            InstructionsPage.OpenInstruction(id);
        }

        /// <summary>Есть ли у инструкции файл — для отметки в чате, ведущей к удалённой.</summary>
        internal bool InstructionExists(string id) =>
            _services?.Instructions.Find(id) is { } found &&
            string.Equals(found.Id, id, StringComparison.OrdinalIgnoreCase);
    }
}
