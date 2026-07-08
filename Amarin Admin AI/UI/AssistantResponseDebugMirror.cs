using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Amarin.UI;

/// <summary>
/// Временный тест: дублирует каждый ответ ИИ в MessageBox, чтобы отделить ответ API от рендера консоли.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class AssistantResponseDebugMirror
{
    internal const bool Enabled = false;

    private const int MaxChars = 4000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    public static void Show(string content)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            var text = string.IsNullOrWhiteSpace(content)
                ? "(пустой ответ от модели)"
                : content.Trim();

            if (text.Length > MaxChars)
            {
                text = text[..MaxChars] + "\n\n… [обрезано для MessageBox]";
            }

            MessageBoxW(IntPtr.Zero, text, "Amarin — ответ ИИ (тест)", 0);
        }
        catch
        {
            // не ломаем основной поток из-за отладочного окна
        }
    }
}