using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Команды «/» (D9): подсказка у поля, пока набирается имя, и исполнение без модели.
    /// </summary>
    /// <remarks>
    /// Команда — только имя из <see cref="ChatCommands.All"/> целиком: всё прочее, что начинается
    /// с «/», уходит модели как текст — люди пишут пути и код. <c>/agent</c> по-прежнему
    /// разбирает <see cref="ChatCommands.TryParse"/>: это ход, а не действие окна.
    /// </remarks>
    public partial class MainWindow
    {
        private IReadOnlyList<ChatCommandInfo> _commandItems = [];
        private int _commandIndex;

        private void WireCommands()
        {
            PopupManager.Register(CommandSuggestPopup);
            // И на смену выделения — как у подсказки «@»: подсказка нужна, только пока каретка
            // стоит в конце набираемого имени.
            MessageTextBox.TextChanged += (_, _) => UpdateCommandSuggest();
            MessageTextBox.SelectionChanged += (_, _) => UpdateCommandSuggest();
        }

        /// <summary>Выполняет команду окна. Поле уже очищено.</summary>
        private void RunLocalCommand(LocalCommand command)
        {
            switch (command.Name)
            {
                case ChatCommands.New:
                    StartNewChatFromUi();
                    if (command.Argument.Length > 0)
                    {
                        PlaceIncomingPrompt(command.Argument, send: false);
                    }

                    break;
                case ChatCommands.Model:
                    PickModelByName(command.Argument);
                    break;
                case ChatCommands.Compact:
                    Detached.Run(CompactContextAsync(), "compact_command");
                    break;
                case ChatCommands.Export:
                    ExportByCommand(command.Argument);
                    break;
                case ChatCommands.Instruction:
                    OpenInstructionByName(command.Argument);
                    break;
                case ChatCommands.Recipe:
                    OpenRecipeByName(command.Argument);
                    break;
                case ChatCommands.ReadOnly:
                    ToggleReadOnly();
                    break;
            }
        }

        private void PickModelByName(string name)
        {
            if (_services is null)
            {
                return;
            }

            if (name.Length == 0)
            {
                ModelButton.IsChecked = true;
                return;
            }

            var candidates = new List<(string Id, string Name)> { ("auto", VeniceModelCatalog.GetDisplayName("auto")) };
            foreach (var provider in new[] { LlmProvider.Venice, LlmProvider.OpenRouter })
            {
                foreach (var model in _services.Models.CachedFor(provider) ?? [])
                {
                    var id = ModelRef.Qualify(provider, model.Id);
                    candidates.Add((id, VeniceModelCatalog.GetDisplayName(id)));
                }
            }

            if (ChatCommands.MatchModel(name, candidates) is not { } found)
            {
                ShowTransientNotice(_session.Id, Loc.Format("S.Command.NoModel", name));
                ModelButton.IsChecked = true;
                return;
            }

            ChatModelPicker_ModelPicked(this, new ModelBinding(found, _session.SelectedKeyId));
            ShowTransientNotice(_session.Id, Loc.Format("S.Command.ModelSet", VeniceModelCatalog.GetDisplayName(found)));
        }

        private void ExportByCommand(string format)
        {
            switch (format.Trim().ToLowerInvariant())
            {
                case "md" or "markdown":
                    Detached.Run(ExportAsAsync(_session, null, ExportFormat.Markdown), "export_command");
                    break;
                case "html":
                    Detached.Run(ExportAsAsync(_session, null, ExportFormat.Html), "export_command");
                    break;
                case "pdf":
                    PrintChat(_session, null, pdf: true);
                    break;
                case "print":
                    PrintChat(_session, null, pdf: false);
                    break;
                case "json":
                    ExportSession(_session, null);
                    break;
                default:
                    OpenExportMenu(SendButton, _session, null);
                    break;
            }
        }

        private void OpenInstructionByName(string name)
        {
            if (_services is null)
            {
                return;
            }

            var all = _services.Instructions.Snapshot();
            var found = all.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        ?? all.FirstOrDefault(item => name.Length > 0 && item.Name.Contains(name, StringComparison.CurrentCultureIgnoreCase));
            if (found is not null)
            {
                OpenInstruction(found.Id);
                return;
            }

            if (name.Length > 0)
            {
                ShowTransientNotice(_session.Id, Loc.Format("S.Command.NoInstruction", name));
            }

            OpenSettingsPage(NavInstructions);
        }

        private void OpenRecipeByName(string name)
        {
            if (_services is null)
            {
                return;
            }

            var all = _services.Recipes.All();
            var found = all.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        ?? all.FirstOrDefault(item => name.Length > 0 && item.Name.Contains(name, StringComparison.CurrentCultureIgnoreCase));
            OpenSettingsPage(NavAutomation);
            if (found is not null)
            {
                AutomationPage.Run(found.Id);
            }
            else if (name.Length > 0)
            {
                ShowTransientNotice(_session.Id, Loc.Format("S.Command.NoRecipe", name));
            }
        }

        /// <summary>
        /// «Только чтение» у этого чата: его ходы и агенты идут под <see cref="ToolGate.ForceReadOnly"/>,
        /// что бы ни стояло в режиме подтверждений. Меняется только между ходами.
        /// </summary>
        private void ToggleReadOnly()
        {
            if (IsBusy(_session.Id))
            {
                ShowTransientNotice(_session.Id, Loc.Get("S.Command.ReadOnlyBusy"));
                return;
            }

            _session.ReadOnly = !_session.ReadOnly;
            PersistCurrent();
            UpdateReadOnlyChip();
            ShowTransientNotice(_session.Id, Loc.Get(_session.ReadOnly ? "S.Command.ReadOnlyOn" : "S.Command.ReadOnlyOff"));
        }

        private void UpdateReadOnlyChip()
        {
            if (ReadOnlyChip is not null)
            {
                ReadOnlyChip.Visibility = _session.ReadOnly ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void ReadOnlyChip_Click(object sender, RoutedEventArgs e) => ToggleReadOnly();

        // ───────────────────────── Подсказка ─────────────────────────

        private void UpdateCommandSuggest()
        {
            var items = ChatCommands.Suggest(MessageTextBox.Text);
            if (items.Count == 0 || MessageTextBox.CaretIndex != MessageTextBox.Text.Length)
            {
                CloseCommandSuggest();
                return;
            }

            if (!items.SequenceEqual(_commandItems))
            {
                _commandIndex = 0;
            }

            _commandItems = items;
            RenderCommandSuggest();
            if (!CommandSuggestPopup.IsOpen)
            {
                CommandSuggestPopup.IsOpen = true;
            }
        }

        private void CloseCommandSuggest()
        {
            _commandItems = [];
            if (CommandSuggestPopup is { IsOpen: true })
            {
                CommandSuggestPopup.IsOpen = false;
            }
        }

        private void RenderCommandSuggest()
        {
            CommandSuggestList.Children.Clear();
            for (var i = 0; i < _commandItems.Count; i++)
            {
                CommandSuggestList.Children.Add(BuildCommandRow(_commandItems[i], i == _commandIndex));
            }
        }

        private Button BuildCommandRow(ChatCommandInfo command, bool selected)
        {
            var name = new TextBlock
            {
                Text = "/" + command.Name,
                FontFamily = new System.Windows.Media.FontFamily("Consolas, Cascadia Mono"),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");

            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(name);
            if (command.ArgumentKey is { } argument)
            {
                var hint = new TextBlock { Text = " " + Loc.Get(argument), FontSize = 12, VerticalAlignment = VerticalAlignment.Bottom };
                hint.SetResourceReference(TextBlock.ForegroundProperty, "Text.Faint");
                head.Children.Add(hint);
            }

            var description = new TextBlock { Text = Loc.Get(command.DescriptionKey), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) };
            description.SetResourceReference(TextBlock.ForegroundProperty, "Text.Muted");

            var body = new StackPanel();
            body.Children.Add(head);
            body.Children.Add(description);

            var row = new Button
            {
                Content = body,
                Template = TextHitTemplate(),
                Cursor = Cursors.Hand,
                Focusable = false,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 2)
            };
            if (selected)
            {
                row.Loaded += (_, _) =>
                {
                    if (row.Template.FindName("Bg", row) is Border plate)
                    {
                        plate.SetResourceReference(Border.BackgroundProperty, "Bg.Selected");
                    }
                };
            }

            System.Windows.Automation.AutomationProperties.SetName(row, "/" + command.Name + " — " + description.Text);
            row.Click += (_, _) => CommitCommandSuggest(command, runIfComplete: false);
            return row;
        }

        /// <summary>Стрелки, Tab и Enter, пока открыта подсказка. Esc закрывает её.</summary>
        private bool TryHandleCommandSuggestKey(KeyEventArgs e)
        {
            if (!CommandSuggestPopup.IsOpen || _commandItems.Count == 0 ||
                (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift)) != 0)
            {
                return false;
            }

            switch (e.Key)
            {
                case Key.Down:
                    _commandIndex = (_commandIndex + 1) % _commandItems.Count;
                    RenderCommandSuggest();
                    return true;
                case Key.Up:
                    _commandIndex = (_commandIndex - 1 + _commandItems.Count) % _commandItems.Count;
                    RenderCommandSuggest();
                    return true;
                case Key.Tab:
                    CommitCommandSuggest(_commandItems[Math.Clamp(_commandIndex, 0, _commandItems.Count - 1)], runIfComplete: false);
                    return true;
                case Key.Enter:
                    CommitCommandSuggest(_commandItems[Math.Clamp(_commandIndex, 0, _commandItems.Count - 1)], runIfComplete: true);
                    return true;
                case Key.Escape:
                    CloseCommandSuggest();
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Подставляет команду. Без аргумента Enter сразу её выполняет — дописывать нечего; с
        /// аргументом ставит пробел и ждёт продолжения.
        /// </summary>
        private void CommitCommandSuggest(ChatCommandInfo command, bool runIfComplete)
        {
            var text = "/" + command.Name + (command.ArgumentKey is null ? "" : " ");
            MessageTextBox.Text = text;
            MessageTextBox.CaretIndex = text.Length;
            CloseCommandSuggest();
            MessageTextBox.Focus();
            if (runIfComplete && command.ArgumentKey is null)
            {
                Detached.Run(SendAsync(), "command");
            }
        }
    }
}
