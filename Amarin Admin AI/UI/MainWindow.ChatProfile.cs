using System.Windows;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Профиль чата и шаблоны новых чатов (D11): свой промпт и набор инструкций у одного чата,
    /// заготовки «промпт + модель + размышление + инструкции» для новых.
    /// </summary>
    public partial class MainWindow
    {
        private void WireChatProfile()
        {
            ChatSettings.Saved += profile =>
            {
                _session.Profile = profile;
                PersistCurrent();
                UpdateProfileChip();
                RefreshContextRing();
            };
            ChatSettings.TemplateRequested += profile => SaveTemplate(profile);
        }

        internal void OpenChatSettings()
        {
            if (_services is null)
            {
                return;
            }

            ChatSettings.Show(_session.Profile, _services.Instructions.EnabledSnapshot());
        }

        private void UpdateProfileChip()
        {
            if (ProfileChip is not null)
            {
                ProfileChip.Visibility = _session.Profile is { IsEmpty: false } ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void ProfileChip_Click(object sender, RoutedEventArgs e) => OpenChatSettings();

        /// <summary>
        /// Шаблон из того, что набрано в окне настроек (или из профиля чата), плюс модель и
        /// размышление открытого чата.
        /// </summary>
        private void SaveTemplate(ChatProfile? profile)
        {
            OpenNameDialog(
                Loc.Get("S.Templates.NameTitle"),
                Loc.Get("S.Templates.NameDesc"),
                "",
                name =>
                {
                    if (_services is null)
                    {
                        return;
                    }

                    var template = ChatTemplate.From(name, _session);
                    template.Prompt = profile?.Prompt;
                    template.InstructionIds = profile?.InstructionIds is { } ids ? [.. ids] : null;
                    _services.Templates.Save(template);
                    ShowTransientNotice(_session.Id, Loc.Format("S.Templates.Saved", template.Name));
                });
        }

        private void TemplatesButton_Click(object sender, RoutedEventArgs e)
        {
            if (_services is null)
            {
                return;
            }

            var menu = NewMenu(TemplatesButton);
            var templates = _services.Templates.All();
            foreach (var template in templates.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var chosen = template;
                menu.Items.Add(MenuItemFor(template.Name, () => StartChatFromTemplate(chosen)));
            }

            if (templates.Count == 0)
            {
                var empty = MenuItemFor(Loc.Get("S.Templates.Empty"), () => { });
                empty.IsEnabled = false;
                menu.Items.Add(empty);
            }

            menu.Items.Add(Divider());
            menu.Items.Add(MenuItemFor(Loc.Get("S.Templates.FromCurrent") + "…", () => SaveTemplate(_session.Profile)));
            menu.Items.Add(MenuItemFor(Loc.Get("S.ChatProfile.Title") + "…", OpenChatSettings));
            if (templates.Count > 0)
            {
                menu.Items.Add(MenuItemFor(Loc.Get("S.Templates.Delete") + "…", () => OpenTemplateDeleteMenu(templates)));
            }

            menu.IsOpen = true;
        }

        private void OpenTemplateDeleteMenu(IReadOnlyList<ChatTemplate> templates)
        {
            var menu = NewMenu(TemplatesButton);
            foreach (var template in templates.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var chosen = template;
                menu.Items.Add(MenuItemFor(template.Name, () => _services?.Templates.Delete(chosen.Id), danger: true));
            }

            menu.IsOpen = true;
        }

        /// <summary>Новый чат по шаблону: модель, размышление, промпт и инструкции — сразу.</summary>
        internal void StartChatFromTemplate(ChatTemplate template)
        {
            StartNewChatFromUi();
            template.ApplyTo(_session);
            UpdateProfileChip();
            UpdateModelButton();
            ShowTransientNotice(_session.Id, Loc.Format("S.Templates.Applied", template.Name));
        }
    }
}
