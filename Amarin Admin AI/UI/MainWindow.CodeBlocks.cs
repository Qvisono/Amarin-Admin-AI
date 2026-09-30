using System.Windows;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>Кнопки блоков кода, которым нужно окно (D13).</summary>
    public partial class MainWindow
    {
        private void WireCodeBlocks()
        {
            CodeBlockView.ShowLineNumbers = () => _services?.Settings.CodeLineNumbers == true;
            AddHandler(CodeBlockView.RunScriptEvent, new RoutedEventHandler(OnRunScriptRequested));
        }

        /// <summary>
        /// «Выполнить через агента»: задача ложится в поле ввода открытого чата, а не уходит сама —
        /// человек видит, что поручает, и отправляет сам. Скрипт при выполнении проходит обычное
        /// подтверждение шлюза.
        /// </summary>
        private void OnRunScriptRequested(object sender, RoutedEventArgs e)
        {
            if (e is not CodeBlockView.RunScriptEventArgs request)
            {
                return;
            }

            e.Handled = true;
            // Ограда длиннее самой длинной серии обратных апострофов в коде — иначе код её закрыл бы.
            var longest = 0;
            var run = 0;
            foreach (var symbol in request.Code)
            {
                run = symbol == '`' ? run + 1 : 0;
                longest = Math.Max(longest, run);
            }

            var fence = new string('`', Math.Max(3, longest + 1));
            var task = "/agent " + Loc.Get("S.Code.AgentTask") + "\n" + fence + request.Language + "\n" + request.Code + "\n" + fence;
            PlaceIncomingPrompt(task, send: false);
        }
    }
}
