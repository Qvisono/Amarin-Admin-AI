using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Горячие клавиши окна: строки в настройках и разбор нажатия.
    /// </summary>
    /// <remarks>
    /// Сочетания переназначаемые, поэтому <c>InputBindings</c> не годятся: их пришлось бы
    /// пересобирать на каждое изменение, а тогда проще сравнить нажатие прямо на месте — тем
    /// более что всю клавиатуру окно и так разбирает руками (<c>Window_PreviewKeyDown</c>).
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public partial class MainWindow
    {
        /// <summary>Строит строки раздела «Hotkeys» и наполняет их из настроек.</summary>
        private void LoadHotkeysUi(AppSettings settings)
        {
            if (HotkeyList.Children.Count == 0)
            {
                foreach (var action in HotkeyMap.All)
                {
                    HotkeyList.Children.Add(BuildHotkeyRow(action));
                }
            }

            foreach (var field in HotkeyList.Children.OfType<Grid>()
                         .Select(row => row.Children.OfType<HotkeyField>().First()))
            {
                var action = (HotkeyAction)field.Tag;
                field.SetGesture(HotkeyMap.Gesture(settings.Hotkeys, action.Id), action.DefaultGesture);
            }
        }

        /// <summary>
        /// Строка списка: подпись с пояснением слева, поле сочетания справа — та же форма, что
        /// у прочих настроек на этой странице.
        /// </summary>
        private Grid BuildHotkeyRow(HotkeyAction action)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var labels = new StackPanel
            {
                Margin = new Thickness(0, 0, 16, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var title = new TextBlock();
            title.SetResourceReference(StyleProperty, "SettingTitle");
            title.SetResourceReference(TextBlock.TextProperty, action.TitleKey);
            labels.Children.Add(title);

            var description = new TextBlock();
            description.SetResourceReference(StyleProperty, "SettingDesc");
            description.SetResourceReference(TextBlock.TextProperty, action.DescriptionKey);
            labels.Children.Add(description);

            row.Children.Add(labels);

            var field = new HotkeyField
            {
                Tag = action,
                VerticalAlignment = VerticalAlignment.Center
            };
            field.GestureChanged += (_, gesture) => SaveHotkey(action, gesture);
            Grid.SetColumn(field, 1);
            row.Children.Add(field);

            return row;
        }

        /// <remarks>
        /// Заводское сочетание из словаря убирается, а не записывается: тогда назначения,
        /// которых человек не трогал, поедут вместе с заводскими, если те когда-нибудь
        /// поменяются.
        /// </remarks>
        private void SaveHotkey(HotkeyAction action, string gesture)
        {
            if (_settingsUiLoading || _services is null)
            {
                return;
            }

            var settings = _services.Settings;
            if (string.Equals(gesture, action.DefaultGesture, StringComparison.Ordinal))
            {
                settings.Hotkeys?.Remove(action.Id);
            }
            else
            {
                settings.Hotkeys ??= new Dictionary<string, string>(StringComparer.Ordinal);
                settings.Hotkeys[action.Id] = gesture;
            }

            _services.SettingsStore.Save(settings);
        }

        /// <summary>
        /// Нажатие — это горячая клавиша: разбираем и выполняем. Отдаёт <c>true</c>, если
        /// событие забрали себе.
        /// </summary>
        /// <remarks>
        /// Зовётся из <c>Window_PreviewKeyDown</c> раньше <c>ShouldKeepKeyboardFocus</c>: тот
        /// уступает событие полю ввода при любом Ctrl или Alt, и ниже него сочетание с
        /// модификатором не дожило бы.
        /// </remarks>
        private bool TryRunHotkey(KeyEventArgs e)
        {
            // Обычный набор текста приходит сюда на каждую букву, а разбор сочетания — это
            // разрезание строки со списком. Ctrl или Alt обязателен (см. HotkeyMap), и дешёвая
            // проверка спереди снимает эту работу со всего набора.
            var modifiers = Keyboard.Modifiers;
            if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == ModifierKeys.None)
            {
                return false;
            }

            // Поверх чата стоит вопрос, настройки или просмотр картинки — клавиатура их.
            // Заводить новый чат из-под вопроса о запуске скрипта тем более нельзя.
            if (_services is null || ChatCoveredByOverlay())
            {
                return false;
            }

            // Alt приезжает под Key.System, настоящая клавиша — в SystemKey.
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var assignments = _services.Settings.Hotkeys;

            foreach (var action in HotkeyMap.All)
            {
                if (!Hotkeys.Matches(HotkeyMap.Gesture(assignments, action.Id), key, modifiers))
                {
                    continue;
                }

                // Отказавшееся действие отдаёт сочетание следующему, а не глотает его: «Ответить»
                // без выделения не должно отнимать клавишу у того, кому её назначили тоже.
                if (Run(action.Id))
                {
                    return true;
                }
            }

            return false;
        }

        private bool Run(string actionId)
        {
            switch (actionId)
            {
                case HotkeyMap.NewChat:
                    StartNewChatFromUi();
                    return true;
                case HotkeyMap.ReplyToSelection:
                    return TryReplyToSelection();
                default:
                    return false;
            }
        }
    }
}
