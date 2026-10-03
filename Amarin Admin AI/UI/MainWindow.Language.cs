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
            UpdateTranslationEditButton();
        }

        /// <summary>«Править перевод» — только у языка, переведённого моделью: встроенные правят в коде.</summary>
        private void UpdateTranslationEditButton() =>
            EditTranslationButton.Visibility = LanguageManager.IsBuiltIn(LanguageManager.Current) ||
                                               !UserLanguageStore.Exists(LanguageManager.Current)
                ? Visibility.Collapsed
                : Visibility.Visible;

        private void EditTranslationButton_Click(object sender, RoutedEventArgs e)
        {
            if (_services is null)
            {
                return;
            }

            var code = LanguageManager.Current;
            var name = UserLanguageStore.NameOf(code) ?? code;
            TranslationEditor.Retranslate ??= RetranslateOneAsync;
            if (!_translationEditorWired)
            {
                _translationEditorWired = true;
                TranslationEditor.Saved += saved =>
                {
                    // force: код тот же, а строки в словаре уже новые.
                    LanguageManager.Apply(saved, force: true);
                };
            }

            TranslationEditor.Show(code, name);
        }

        private bool _translationEditorWired;

        /// <summary>Перевести заново одну строку — тем же переводчиком, что и весь язык.</summary>
        private async Task<string?> RetranslateOneAsync(string key, string original)
        {
            if (_services is null || string.IsNullOrWhiteSpace(_services.Options.ApiKey))
            {
                return null;
            }

            var code = LanguageManager.Current;
            var translator = new LanguageTranslator(_services.Venice, () => _services.Models.Cached);
            try
            {
                var result = await translator.TranslateAsync(
                    new Dictionary<string, string> { [key] = original },
                    UserLanguageStore.NameOf(code) ?? code,
                    null,
                    CancellationToken.None);
                return result.Success ? result.Strings.GetValueOrDefault(key) : null;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return null;
            }
        }

        private void LanguagePicker_LanguageDeleteRequested(object? sender, UiLanguage language)
        {
            if (_services is null || language.BuiltIn)
            {
                return;
            }

            // Перевод стоит модели минуты работы и денег, поэтому спрашиваем, а не стираем
            // молча, — и по умолчанию отвечаем «нет», как при удалении всех чатов.
            Detached.Run(DeleteLanguageAsync(language), "delete_language");
        }

        private async Task DeleteLanguageAsync(UiLanguage language)
        {
            var confirmed = await ShowNoticeAsync(
                Loc.Format("S.Language.DeleteTitle", language.NativeName),
                Loc.Get("S.Language.DeleteConfirm"),
                Loc.Get("S.Common.Delete"),
                Loc.Get("S.Common.Cancel"),
                NoticeTone.Danger);
            if (!confirmed || _services is null)
            {
                return;
            }

            if (!UserLanguageStore.Delete(language.Code))
            {
                LanguagePicker.ShowProgress(Loc.Get("S.Language.DeleteFailed"));
                return;
            }

            // Удалили тот язык, на котором сейчас интерфейс. Normalize вернул бы русский сам, но
            // только при следующем запуске: до него в settings.json лежал бы мёртвый код, а на
            // экране — надписи удалённого языка.
            if (string.Equals(_services.Settings.LanguageCode, language.Code, StringComparison.OrdinalIgnoreCase))
            {
                _services.Settings.LanguageCode = LanguageManager.DefaultCode;
                _services.SettingsStore.Save(_services.Settings);
                LanguageManager.Apply(LanguageManager.DefaultCode);
            }

            LanguagePicker.ShowProgress(null);
            LanguagePicker.Rebuild();
        }

        private void LanguagePicker_NewLanguageRequested(object? sender, EventArgs e)
        {
            if (_services is null)
            {
                return;
            }

            OpenNameDialog(
                Loc.Get("S.Language.NameTitle"),
                "",
                name => Detached.Run(TranslateLanguageAsync(name), "translate_language"),
                Loc.Get("S.Language.Translate"),
                Loc.Get("S.Language.NamePlaceholder"),
                Loc.Get("S.Language.NameDesc"));
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

            var translator = new LanguageTranslator(_services.Venice, () => _services.Models.Cached);
            LanguagePicker.ShowProgress(Loc.Format("S.Language.Progress", 0, 1));

            // Язык уже переводили: поправленное руками (I4) модели не отправляется и остаётся как есть.
            var existing = UserLanguageStore.ReadMap(LanguageTranslator.CodeFor(languageName));

            try
            {
                var result = await translator.TranslateAsync(
                    TranslationEdits.ToTranslate(StringsRu.Values, existing),
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
                var strings = TranslationEdits.Merge(existing, result.Strings);

                // Имя языка на нём самом — по нему его и выбирают в списке.
                strings[UserLanguageStore.NameKey] = languageName.Trim();

                UserLanguageStore.Save(code, strings);
                _services.Settings.LanguageCode = code;
                _services.SettingsStore.Save(_services.Settings);

                // force: перевод мог лечь поверх того же кода, и по одному коду словарь
                // выглядел бы прежним — а строки в нём уже новые.
                LanguageManager.Apply(code, force: true);
                LanguagePicker.Rebuild();
                UpdateTranslationEditButton();
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
            UpdateTranslationEditButton();
            UpdateModelButton();
            UpdateReasoningPicker();

            // Панель аккаунта и подпись под именем в боковой колонке пишутся из кода —
            // без этого «Локальный режим» и «Задан» остаются на прежнем языке до перезапуска.
            LoadAccountUi();

            // Та же беда на странице обновлений: и статус, и подпись кнопки «Обновить» заполнены
            // из кода, причём подпись перекрывает DynamicResource насовсем. Плашку рисует автомат
            // по своему состоянию, поэтому перерисовать её можно в любой момент — и посреди загрузки.
            LoadUpdatesUi();
            if (ProfileOverlay.Visibility == Visibility.Visible)
            {
                RefreshProfileList();
            }

            // Список собирается заново по слепку; после смены языка он обязан пересобраться,
            // иначе заголовки групп останутся на прежнем.
            InvalidateChatListSignature();
            RefreshChatList();

            // Чат перед глазами тот же, поменялись только подписи, — приближение остаётся.
            RebuildTranscript(resetZoom: false);
        }
    }
}
