using System.Diagnostics;
using System.Windows;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Плашка «версия и репозиторий» внизу настроек и открытие внешних ссылок со страницы Info.
    /// </summary>
    /// <remarks>
    /// Ссылки живут одним методом, а не по <see cref="Process"/> в каждом обработчике: браузера
    /// в системе может не быть вовсе, и тогда нужен понятный текст, а не окно с исключением.
    /// </remarks>
    public partial class MainWindow
    {
        private void GithubLinkButton_Click(object sender, RoutedEventArgs e) =>
            OpenExternalLink(UpdateChecker.RepositoryUrl);

        /// <summary>Открывает адрес в браузере пользователя.</summary>
        /// <remarks>
        /// Перехватываются ровно те два исключения, что бросает оболочка, когда открывать
        /// http-ссылку нечем или пользователь закрыл диалог выбора программы: и то и другое —
        /// обычный ответ системы, а не авария приложения.
        /// </remarks>
        internal void OpenExternalLink(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                MessageBox.Show(
                    this,
                    Loc.Format("S.Updates.BrowserFailed", ex.Message),
                    Title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }
}
