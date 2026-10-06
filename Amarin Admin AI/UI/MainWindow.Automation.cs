using System.Windows;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>Мост между страницей «Автоматизация» (рецепты) и главным окном.</summary>
    public partial class MainWindow
    {
        private void NavAutomation_Checked(object sender, RoutedEventArgs e)
        {
            if (_services is null)
            {
                return;
            }

            AutomationPage.Attach(_services);
            AutomationPage.Load();
        }

        /// <summary>
        /// «Через агента»: новый чат, постановка в поле. Не отправляется — человек видит, что
        /// уходит модели, и решает сам.
        /// </summary>
        private void OnRecipeAgentRequested(string prompt)
        {
            SettingsOverlay.Visibility = Visibility.Collapsed;
            StartNewChatFromUi();
            PlaceIncomingPrompt(prompt, send: false);
        }

        /// <summary>
        /// «Сохранить как рецепт» в подробностях действия журнала: рецепт заводится сразу, а
        /// редактор открывается на нём — назвать и вынести параметры человек решит там.
        /// </summary>
        private void JournalSaveRecipeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_services is null || _journalDetail?.Entry is not { } entry || !RecipeRules.IsRunnable(entry.ToolName))
            {
                return;
            }

            var recipe = RecipeRules.FromCall(entry.ToolName, entry.ArgumentsJson, entry.ToolName);
            if (_services.Recipes.Save(recipe) is not { } saved)
            {
                JournalSaveRecipeButton.IsEnabled = false;
                return;
            }

            CloseJournal();
            OpenSettings(SettingsUi.NavAutomation);
            AutomationPage.Attach(_services);
            AutomationPage.Edit(saved.Id);
        }
    }
}
