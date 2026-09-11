using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Окно необработанной ошибки: понятная строка человеку, полный отчёт под раскрывашкой,
/// кнопка копирования и выбор «продолжить или закрыть».
/// </summary>
/// <remarks>
/// Системный <c>MessageBox</c> здесь не годится по двум причинам: он выглядит как окно Windows,
/// а не как Amarin, и не умеет ни показать стек, ни отдать его в буфер обмена. При этом само
/// окно обязано работать даже тогда, когда тема ещё не подставлена — авария на старте случается
/// раньше <see cref="ThemeManager.Initialize"/>.
/// </remarks>
public partial class CrashWindow : Window
{
    private readonly bool _canContinue;
    private bool _continue;

    internal CrashWindow(string headline, string message, string report, string? logPath, bool canContinue)
    {
        InitializeComponent();
        EnsurePalette();

        _canContinue = canContinue;
        HeadlineText.Text = headline;
        MessageText.Text = message;
        DetailsText.Text = report;

        LogHintText.Text = string.IsNullOrWhiteSpace(logPath)
            ? Loc.Get("S.Crash.NoLog")
            : Loc.Format("S.Crash.LogPath", logPath);

        ContinueButton.Visibility = canContinue ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.Content = Loc.Get(canContinue ? "S.Crash.Quit" : "S.Crash.Close");

        DetailsToggle.Checked += (_, _) => DetailsBox.Visibility = Visibility.Visible;
        DetailsToggle.Unchecked += (_, _) => DetailsBox.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Показывает окно и возвращает <c>true</c>, если человек выбрал «Продолжить». При
    /// <paramref name="canContinue"/> = false выбора нет: продолжать после падения вне
    /// UI-потока некуда, процесс всё равно завершается.
    /// </summary>
    internal static bool Show(string headline, string message, string report, string? logPath, bool canContinue)
    {
        var window = new CrashWindow(headline, message, report, logPath, canContinue);
        window.ShowDialog();
        return window._continue;
    }

    /// <summary>
    /// Палитра живёт в словарях уровня Application, которые подставляет <see cref="ThemeManager"/>.
    /// Если авария случилась до него, приносим собственную — иначе прозрачное окно с прозрачной
    /// карточкой выглядит как «программа просто закрылась».
    /// </summary>
    private void EnsurePalette()
    {
        if (TryFindResource("Bg.Panel") is not null)
        {
            return;
        }

        try
        {
            // В начало: Resources.xaml идёт следом и перекрывает палитру только своими ключами,
            // а цветов в нём нет.
            Resources.MergedDictionaries.Insert(0, ThemeManager.LoadFallbackPalette());
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UriFormatException)
        {
            // Ресурс сборки недоступен — окно будет бледным, но показать его важнее.
        }
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Рамки у окна нет, тащим за карточку. Перетаскивание из текстового поля деталей
        // мешало бы выделять текст, поэтому только прямые нажатия по самой карточке.
        if (!ReferenceEquals(e.OriginalSource, Card) && !ReferenceEquals(e.OriginalSource, HeadlineText))
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // Кнопка уже отпущена — обычное состояние гонки, не авария.
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(DetailsText.Text);
            CopyButton.Content = Loc.Get("S.Crash.Copied");
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            // Буфер держит другой процесс — текст всё равно виден и выделяется вручную.
            CopyButton.Content = Loc.Get("S.Crash.ClipboardBusy");
        }
    }

    private void ContinueButton_Click(object sender, RoutedEventArgs e)
    {
        _continue = _canContinue;
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _continue = false;
        Close();
    }
}
