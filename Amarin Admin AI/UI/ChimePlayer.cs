using System.Media;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>Играет мелодии отложенных задач (<see cref="ChimeSynth"/>).</summary>
/// <remarks>
/// Байты мелодии собираются при первом звуке и запоминаются: запуску программы синтез не нужен.
/// Проигрыватель живёт, пока звучит: <see cref="SoundPlayer.Play"/> играет в фоне, и собранный
/// сборщиком мусора проигрыватель оборвал бы звук.
/// </remarks>
internal static class ChimePlayer
{
    private static readonly Dictionary<ChimeTune, byte[]> Rendered = [];
    private static SoundPlayer? _playing;

    /// <summary>
    /// Играет мелодию, если это сейчас уместно. Возвращает, прозвучала ли она: тогда системное
    /// уведомление о том же событии должно молчать, иначе звука будет два.
    /// </summary>
    /// <param name="respectQuiet">Молчать в «тихие часы» и при презентации; «Послушать» в настройках звучит всегда.</param>
    public static bool Play(ChimeTune tune, bool enabled, bool respectQuiet = true)
    {
        if (!enabled || (respectQuiet && QuietState.IsBusy()))
        {
            return false;
        }

        try
        {
            if (!Rendered.TryGetValue(tune, out var wav))
            {
                wav = ChimeSynth.Render(tune);
                Rendered[tune] = wav;
            }

            var player = new SoundPlayer(new MemoryStream(wav, writable: false));
            player.Load();
            player.Play();
            var previous = _playing;
            _playing = player;
            previous?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException)
        {
            // Нет звуковой карты или она занята: карточка всё равно приходит.
            return false;
        }
    }
}
