using System.Buffers.Binary;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Длинная запись режется на части для распознавания с метками времени, а звук чужого формата
/// Windows перекодирует в WAV, который принимает распознавание.
/// </summary>
public sealed class MediaAudioTests
{
    [Fact]
    public void A_long_recording_is_cut_into_wav_pieces_with_their_start()
    {
        var pcm = new byte[WavFile.BytesPerSecond * 25];
        using var stream = new MemoryStream(WavFile.Build(pcm));

        var pieces = WavFile.Pieces(stream, TimeSpan.FromSeconds(10)).ToList();

        Assert.Equal([TimeSpan.Zero, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)], pieces.Select(piece => piece.Start));
        Assert.Equal(44 + WavFile.BytesPerSecond * 10, pieces[0].Wav.Length);
        Assert.Equal(44 + WavFile.BytesPerSecond * 5, pieces[2].Wav.Length);
        Assert.Equal(WavFile.SampleRate, BinaryPrimitives.ReadInt32LittleEndian(pieces[1].Wav.AsSpan(24)));
    }

    [Fact]
    public void Blocks_before_the_data_and_a_stereo_file_are_handled()
    {
        // Перекодировщик Windows кладёт перед данными свои блоки: данные — не всегда на 44-м байте.
        var plain = WavFile.Build(new byte[3200]);
        var withList = new List<byte>(plain[..36]);
        withList.AddRange("LIST"u8.ToArray());
        withList.AddRange(BitConverter.GetBytes(6));
        withList.AddRange(new byte[6]);
        withList.AddRange(plain[36..]);
        Assert.Single(WavFile.Pieces(new MemoryStream([.. withList]), TimeSpan.FromMinutes(5)));

        var stereo = WavFile.Build(new byte[3200]);
        BinaryPrimitives.WriteInt16LittleEndian(stereo.AsSpan(22), 2);
        Assert.Throws<InvalidDataException>(() => WavFile.Pieces(new MemoryStream(stereo), TimeSpan.FromMinutes(5)).ToList());
    }

    [Fact]
    public void Media_files_are_recognized_by_extension()
    {
        Assert.True(MediaAudio.IsMedia(@"C:\meeting.MP3"));
        Assert.True(MediaAudio.IsMedia("video.mp4"));
        Assert.False(MediaAudio.IsMedia("notes.txt"));
    }

    [Fact]
    public async Task Windows_converts_a_stereo_sound_into_mono_16_kHz()
    {
        var source = Directory.EnumerateFiles(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media"), "*.wav")
            .FirstOrDefault();
        if (source is null)
        {
            // Сервер без звуковой схемы Windows: проверять нечего.
            return;
        }

        var wav = await MediaAudio.ToWavAsync(source, CancellationToken.None);
        try
        {
            await using var stream = File.OpenRead(wav);
            var pieces = WavFile.Pieces(stream, WavFile.MaxLength).ToList();
            var piece = Assert.Single(pieces);
            Assert.Equal(WavFile.SampleRate, BinaryPrimitives.ReadInt32LittleEndian(piece.Wav.AsSpan(24)));
            Assert.True(piece.Wav.Length > 44 + WavFile.BytesPerSecond / 10);
        }
        finally
        {
            MediaAudio.TryDelete(wav);
        }
    }
}
