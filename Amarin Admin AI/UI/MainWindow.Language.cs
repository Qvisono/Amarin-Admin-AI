using System.Runtime.Versioning;
using System.Windows;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Язык интерфейса: выбор из списка и кнопка «new language», по которой модель переводит
    /// весь каталог строк.
    /// </summary>
    /// <remarks>
    /// Разметка берёт строки через <c>{DynamicResource S.*}</c> и обновляется сама. А вот всё,
    /// что окно выставляет из кода — подписи кнопок, меню, пузыри сообщений, — держит уже
    /// готовый текст: ту же беду <see cref="ThemeManager"/> имеет с картинками, которые
    /// присвоены из кода. Поэтому смена языка гонит <see cref="RelocalizeUi"/>.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public partial class MainWindow
    {
        private CancellationTokenSource? _translationCts;

        private void LanguagePicker_LanguagePicked(object? sender, string code)
        {
            if (_services is null)
            {
                return;
            }

            _services.Settings.LanguageCode = code;
            _services.SettingsStore.Save(_services.Settings);
            LanguageManager.Apply(code);
        }

        private void LanguagePicker_NewLanguageRequested(object? sender, EventArgs e)
        {
            if (_services is null)
            {
                return;
            }

            OpenNameDialog(
                Loc.Get("S.Language.NameTitle"),
                Loc.Get("S.Language.NameDesc"),
                "",
                name => _ = TranslateLanguageAsync(name));
        }

        private async Task TranslateLanguageAsync(string languageName)
        {
            if (_services is null || string.IsNullOrWhiteSpace(languageName))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_services.Options.ApiKey))
            {
                LanguagePicker.ShowProgress(Loc.Get("S.Turn.NoApiKey"));
                return;
            }

            _translationCts?.Cancel();
            _translationCts = new CancellationTokenSource();
            var token = _translationCts.Token;

            var translator = new LanguageTranslator(_services.Venice);
            LanguagePicker.ShowProgress(Loc.Format("S.Language.Progress", 0, 1));

            try
            {
                var result = await translator.TranslateAsync(
                    StringsRu.Values,
                    languageName.Trim(),
                    step => Ui(() => LanguagePicker.ShowProgress(
                        Loc.Format("S.Language.Progress", step.Done, step.Total))),
                    token);

                if (!result.Success)
                {
                    LanguagePicker.ShowProgress(Loc.Format("S.Language.Failed", result.Error ?? ""));
                    return;
                }

                var code = LanguageTranslator.CodeFor(languageName);
                var strings = new Dictionary<string, string>(result.Strings, StringComparer.Ordinal)
                {
                    // Имя языка на нём самом — по нему его и выбирают в списке.
                    [UserLanguageStore.NameKey] = languageName.Trim()
                };

                UserLanguageStore.Save(code, strings);
                _services.Settings.LanguageCode = code;
                _services.SettingsStore.Save(_services.Settings);
                LanguageManager.Apply(code);
                LanguagePicker.Rebuild();
                LanguagePicker.ShowProgress(Loc.Get("S.Language.Done"));
            }
            catch (OperationCanceledException)
            {
                LanguagePicker.ShowProgress(null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LanguagePicker.ShowProgress(Loc.Format("S.Language.Failed", ex.Message));
            }
        }

        /// <summary>
        /// Перечитывает всё, что окно выставило из кода. Разметка обновляется сама.
        /// </summary>
        private void RelocalizeUi()
        {
            if (!IsLoaded || _services is null)
            {
                return;
            }

            LanguagePicker.SetSelected(LanguageManager.Current);
            UpdateModelButton();
            UpdateReasoningPicker();

            // Панель аккаунта и подпись под именем в боковой колонке пишутся из кода —
            // без этого «Локальный режим» и «Задан» остаются на прежнем языке до перезапуска.
            LoadAccountUi();
            if (ProfileOverlay.Visibility == Visibility.Visible)
            {
                RefreshProfileList();
            }

            // Список собирается заново по слепку; после смены языка он обязан пересобраться,
            // иначе заголовки групп останутся на прежнем.
            _chatListSignature = "";
            RefreshChatList();
            RenderSession();
        }
    }
}
