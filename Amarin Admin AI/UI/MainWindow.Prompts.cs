using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Библиотека заготовок основного промпта на странице Customize.
    /// </summary>
    /// <remarks>
    /// Плитки строятся кодом, как карточки тем: их число известно только на месте, из
    /// <c>prompts.json</c>. Разметка держит пустой <c>WrapPanel</c> и две модалки.
    /// </remarks>
    public partial class MainWindow
    {
        private List<PromptPreset> _presets = [];

        /// <summary>Заготовка, открытая в модалке. <c>null</c> — создаётся новая.</summary>
        private PromptPreset? _editingPreset;

        /// <summary>Значения, с которыми модалку открыли: к ним возвращает «Сбросить».</summary>
        private (string Name, string Text) _editingOriginal;

        /// <summary>Что сделать по «Да». Одно поле на оба вопроса — применить и удалить.</summary>
        private Action? _promptApplyCommit;

        private void LoadPromptLibrary()
        {
            if (_services is null)
            {
                return;
            }

            _presets = _services.Prompts.Load();
            RefreshPromptLibrary();
        }

        private void RefreshPromptLibrary()
        {
            PromptLibraryHost.Children.Clear();
            PromptLibraryEmpty.Visibility = _presets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (var preset in _presets)
            {
                PromptLibraryHost.Children.Add(BuildPromptCard(preset));
            }
        }

        private Button BuildPromptCard(PromptPreset preset)
        {
            // Стиль берётся у хоста, а не у окна: он лежит в Resources сетки настроек, куда
            // Window.FindResource не достаёт.
            var card = new Button
            {
                Style = (Style)PromptLibraryHost.FindResource("PromptCard"),
                Tag = preset.Id,
                ToolTip = PromptLibrary.Preview(preset.Text, 400)
            };

            var name = new TextBlock
            {
                Text = preset.Name,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Text.Body");

            var preview = new TextBlock
            {
                Text = PromptLibrary.Preview(preset.Text),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 28,
                Margin = new Thickness(0, 2, 0, 0)
            };
            preview.SetResourceReference(TextBlock.ForegroundProperty, "Text.Faint");

            var actionStyle = (Style)PromptLibraryHost.FindResource("PromptCardAction");
            var edit = new Button { Style = actionStyle, Content = "✎" };
            var remove = new Button { Style = actionStyle, Content = "🗑" };

            // Кнопка внутри кнопки: без гашения клик по карандашу дошёл бы и до плитки, и
            // человек получил бы окно правки вместе с вопросом «заменить промпт?».
            edit.Click += (sender, e) =>
            {
                e.Handled = true;
                OpenPromptEditor(preset);
            };
            remove.Click += (sender, e) =>
            {
                e.Handled = true;
                AskDeletePreset(preset);
            };

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top
            };
            actions.Children.Add(edit);
            actions.Children.Add(remove);

            // Показываются только по наведению: две иконки на каждой плитке забивают собой
            // название, ради которого плитка и нужна.
            actions.Visibility = Visibility.Hidden;
            card.MouseEnter += (_, _) => actions.Visibility = Visibility.Visible;
            card.MouseLeave += (_, _) => actions.Visibility = Visibility.Hidden;

            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(name);
            Grid.SetColumn(actions, 1);
            header.Children.Add(actions);

            var column = new StackPanel();
            column.Children.Add(header);
            column.Children.Add(preview);
            card.Content = column;

            card.Click += (_, _) => AskApplyPreset(preset);
            return card;
        }

        // ───────────────────────── модалка правки ─────────────────────────

        private void PromptCreateButton_Click(object sender, RoutedEventArgs e) => OpenPromptEditor(null);

        private void OpenPromptEditor(PromptPreset? preset)
        {
            _editingPreset = preset;
            _editingOriginal = preset is null
                ? ("", MainPromptTextBox.Text ?? "")
                : (preset.Name, preset.Text);

            PromptPresetTitle.Text = Loc.Get(
                preset is null ? "S.Customize.PromptNew" : "S.Customize.PromptEdit");
            PromptPresetName.Text = _editingOriginal.Name;
            PromptPresetText.Text = _editingOriginal.Text;
            PromptPresetError.Visibility = Visibility.Collapsed;
            PromptPresetDeleteButton.Visibility = preset is null ? Visibility.Collapsed : Visibility.Visible;
            PromptPresetOverlay.Visibility = Visibility.Visible;

            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    PromptPresetName.Focus();
                    PromptPresetName.SelectAll();
                }),
                System.Windows.Threading.DispatcherPriority.Input);
        }

        private void ClosePromptEditor()
        {
            PromptPresetOverlay.Visibility = Visibility.Collapsed;
            _editingPreset = null;
        }

        private void PromptPresetCancelButton_Click(object sender, RoutedEventArgs e) => ClosePromptEditor();

        /// <summary>Возвращает поля к тому, с чем модалку открыли — саму заготовку не трогает.</summary>
        private void PromptPresetResetButton_Click(object sender, RoutedEventArgs e)
        {
            PromptPresetName.Text = _editingOriginal.Name;
            PromptPresetText.Text = _editingOriginal.Text;
            PromptPresetError.Visibility = Visibility.Collapsed;
        }

        private void PromptPresetSaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_services is null)
            {
                return;
            }

            var name = PromptLibrary.TrimName(PromptPresetName.Text);
            if (name.Length == 0)
            {
                ShowPresetError("S.Customize.PromptNameEmpty");
                return;
            }

            var text = (PromptPresetText.Text ?? "").Trim();
            if (text.Length == 0)
            {
                ShowPresetError("S.Customize.PromptTextEmpty");
                return;
            }

            if (_editingPreset is { } editing)
            {
                editing.Name = name;
                editing.Text = text;
            }
            else
            {
                _presets.Add(new PromptPreset { Name = name, Text = text });
            }

            _services.Prompts.Save(_presets);
            ClosePromptEditor();
            RefreshPromptLibrary();
        }

        private void ShowPresetError(string key)
        {
            PromptPresetError.Text = Loc.Get(key);
            PromptPresetError.Visibility = Visibility.Visible;
        }

        private void PromptPresetName_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                PromptPresetSaveButton_Click(sender, e);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                ClosePromptEditor();
            }
        }

        private void PromptPresetDeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (_editingPreset is { } preset)
            {
                AskDeletePreset(preset);
            }
        }

        // ───────────────────────── подтверждение ─────────────────────────

        private void AskApplyPreset(PromptPreset preset) =>
            AskPrompt(
                Loc.Format("S.Customize.PromptApply", preset.Name),
                Loc.Get("S.Customize.PromptApplyDesc"),
                () => ApplyPreset(preset));

        private void AskDeletePreset(PromptPreset preset) =>
            AskPrompt(
                Loc.Format("S.Customize.PromptDeleteConfirm", preset.Name),
                Loc.Get("S.Customize.PromptDeleteDesc"),
                () => DeletePreset(preset));

        private void AskPrompt(string title, string text, Action commit)
        {
            _promptApplyCommit = commit;
            PromptApplyTitle.Text = title;
            PromptApplyText.Text = text;
            PromptApplyOverlay.Visibility = Visibility.Visible;
        }

        private void PromptApplyNoButton_Click(object sender, RoutedEventArgs e)
        {
            PromptApplyOverlay.Visibility = Visibility.Collapsed;
            _promptApplyCommit = null;
        }

        private void PromptApplyYesButton_Click(object sender, RoutedEventArgs e)
        {
            var commit = _promptApplyCommit;
            PromptApplyOverlay.Visibility = Visibility.Collapsed;
            _promptApplyCommit = null;
            commit?.Invoke();
        }

        /// <summary>
        /// Подменяет основной промпт и тут же сохраняет — тем же путём, что кнопка «Сохранить»
        /// под полем: иначе человек увидел бы новый текст в поле, а работала бы старая роль.
        /// </summary>
        private void ApplyPreset(PromptPreset preset)
        {
            if (_services is null)
            {
                return;
            }

            MainPromptTextBox.Text = preset.Text;
            _services.Settings.MainPrompt = preset.Text;
            _services.SettingsStore.Save(_services.Settings);
        }

        private void DeletePreset(PromptPreset preset)
        {
            if (_services is null)
            {
                return;
            }

            _presets.RemoveAll(item => string.Equals(item.Id, preset.Id, StringComparison.Ordinal));
            _services.Prompts.Save(_presets);

            // Модалка правки могла остаться открытой на удалённой заготовке.
            if (_editingPreset is { } editing &&
                string.Equals(editing.Id, preset.Id, StringComparison.Ordinal))
            {
                ClosePromptEditor();
            }

            RefreshPromptLibrary();
        }
    }
}
