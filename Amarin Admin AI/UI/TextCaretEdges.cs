using System.Windows.Controls;
using System.Windows.Input;

namespace Amarin.UI
{
    /// <summary>
    /// Стрелка вверх на первой строке поля уводит каретку в самое начало текста, стрелка вниз на
    /// последней — в самый конец. Так ведёт себя поле сообщения в Discord.
    /// </summary>
    /// <remarks>
    /// Обычный <see cref="TextBox"/> на крайней строке стрелку просто глотает: каретка стоит,
    /// где стояла, и чтобы дописать в начало длинного сообщения, приходилось тянуться к Ctrl+Home
    /// или к мыши. Строкой считается строка на экране, с учётом переноса, — ровно та, по которой
    /// каретку водят сами стрелки. Сочетания с Shift, Ctrl и Alt не трогаются: у них своя работа
    /// (выделение, перемещение по словам).
    /// </remarks>
    internal static class TextCaretEdges
    {
        public static void Attach(TextBox box) => box.PreviewKeyDown += OnPreviewKeyDown;

        private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox box ||
                e.Key is not (Key.Up or Key.Down) ||
                Keyboard.Modifiers != ModifierKeys.None)
            {
                return;
            }

            if (TryJump(box, up: e.Key == Key.Up))
            {
                e.Handled = true;
            }
        }

        /// <summary>
        /// Уводит каретку к краю текста, если она стоит на крайней строке в ту сторону.
        /// </summary>
        /// <returns><c>false</c> — каретка не на крайней строке или уже на краю: пусть стрелка работает как обычно.</returns>
        internal static bool TryJump(TextBox box, bool up)
        {
            var text = box.Text ?? "";
            var target = up ? 0 : text.Length;
            var caret = box.CaretIndex;
            if (text.Length == 0 || (caret == target && box.SelectionLength == 0))
            {
                return false;
            }

            if (!OnEdgeLine(box, text, caret, up))
            {
                return false;
            }

            box.Select(target, 0);
            return true;
        }

        private static bool OnEdgeLine(TextBox box, string text, int caret, bool up)
        {
            var line = box.GetLineIndexFromCharacterIndex(caret);
            var count = box.LineCount;
            if (line >= 0 && count > 0)
            {
                return up ? line == 0 : line == count - 1;
            }

            // Поле ещё не разложено — строки на экране неизвестны, судим по переводам строк.
            return up
                ? text.LastIndexOf('\n', Math.Max(0, caret - 1)) < 0 || caret == 0
                : text.IndexOf('\n', caret) < 0;
        }
    }
}
