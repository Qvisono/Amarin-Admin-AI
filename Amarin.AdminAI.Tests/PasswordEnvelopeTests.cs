using System.Buffers.Binary;
using System.Security.Cryptography;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Архив под паролем: открывается только тем паролем и только целым — обрезанный, переставленный
/// или испорченный файл не читается как «архив поменьше».
/// </summary>
/// <remarks>Итераций мало нарочно: заводские шестьсот тысяч стоили бы секунды на каждую проверку.</remarks>
public sealed class PasswordEnvelopeTests
{
    private const int FastIterations = 1_000;

    private static byte[] Sealed(byte[] plain, string password = "верный пароль")
    {
        using var input = new MemoryStream(plain);
        using var output = new MemoryStream();
        PasswordEnvelope.Seal(input, output, password, FastIterations);
        return output.ToArray();
    }

    private static byte[] Opened(byte[] envelope, string password = "верный пароль")
    {
        using var input = new MemoryStream(envelope);
        using var output = new MemoryStream();
        PasswordEnvelope.Open(input, output, password);
        return output.ToArray();
    }

    private static EnvelopeFailure FailureOf(byte[] envelope, string password = "верный пароль") =>
        Assert.Throws<EnvelopeException>(() => Opened(envelope, password)).Failure;

    /// <summary>Смещения блоков: заголовок 53 байта, у блока 1 + 4 + длина + 16.</summary>
    private static List<(int Start, int Length)> Blocks(byte[] envelope)
    {
        var blocks = new List<(int, int)>();
        var offset = 8 + 1 + 4 + 16 + 8 + 16;
        while (offset < envelope.Length)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(envelope.AsSpan(offset + 1, 4));
            var size = 1 + 4 + length + 16;
            blocks.Add((offset, size));
            offset += size;
        }

        return blocks;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(PasswordEnvelope.BlockSize)]
    [InlineData(PasswordEnvelope.BlockSize * 2 + 17)]
    public void What_was_sealed_opens_back_byte_for_byte(int size)
    {
        var plain = RandomNumberGenerator.GetBytes(size);

        Assert.Equal(plain, Opened(Sealed(plain)));
    }

    [Fact]
    public void The_sealed_file_does_not_carry_the_plain_text()
    {
        var plain = "секретная переписка про сервер"u8.ToArray();

        var envelope = Sealed(plain);

        Assert.True(envelope.AsSpan(0, 8).SequenceEqual("AMRNENC1"u8));
        Assert.Equal(-1, envelope.AsSpan().IndexOf(plain));
    }

    [Fact]
    public void A_wrong_password_is_told_apart_from_a_damaged_file()
    {
        var envelope = Sealed(RandomNumberGenerator.GetBytes(1000));

        Assert.Equal(EnvelopeFailure.WrongPassword, FailureOf(envelope, "неверный пароль"));
        Assert.Equal(EnvelopeFailure.WrongPassword, FailureOf(envelope, ""));
    }

    [Fact]
    public void A_file_cut_after_a_whole_block_is_not_read_as_a_shorter_archive()
    {
        // Отрезанный ровно по границе блока файл без проверки признака последнего блока
        // прочитался бы без единой ошибки — просто без хвоста.
        var envelope = Sealed(RandomNumberGenerator.GetBytes(PasswordEnvelope.BlockSize * 2 + 5));
        var blocks = Blocks(envelope);
        var cut = envelope[..(blocks[1].Start + blocks[1].Length)];

        Assert.Equal(EnvelopeFailure.Damaged, FailureOf(cut));
    }

    [Fact]
    public void Swapped_blocks_are_caught()
    {
        var envelope = Sealed(RandomNumberGenerator.GetBytes(PasswordEnvelope.BlockSize * 2 + 5));
        var blocks = Blocks(envelope);
        var first = envelope.AsSpan(blocks[0].Start, blocks[0].Length).ToArray();
        var second = envelope.AsSpan(blocks[1].Start, blocks[1].Length).ToArray();
        var swapped = envelope.ToArray();
        second.CopyTo(swapped, blocks[0].Start);
        first.CopyTo(swapped, blocks[0].Start + second.Length);

        Assert.Equal(EnvelopeFailure.Damaged, FailureOf(swapped));
    }

    [Fact]
    public void A_flipped_bit_is_caught()
    {
        var envelope = Sealed(RandomNumberGenerator.GetBytes(4000));
        envelope[^100] ^= 0x01;

        Assert.Equal(EnvelopeFailure.Damaged, FailureOf(envelope));
    }

    [Fact]
    public void Anything_after_the_last_block_is_caught()
    {
        var envelope = Sealed(RandomNumberGenerator.GetBytes(4000));

        Assert.Equal(EnvelopeFailure.Damaged, FailureOf([.. envelope, 0, 1, 2]));
    }

    [Fact]
    public void A_final_flag_moved_to_an_earlier_block_is_caught()
    {
        // Признак последнего блока входит в проверяемые данные: поднять его у первого блока,
        // чтобы отбросить остальные, нельзя.
        var envelope = Sealed(RandomNumberGenerator.GetBytes(PasswordEnvelope.BlockSize + 5));
        var blocks = Blocks(envelope);
        var forged = envelope[..(blocks[0].Start + blocks[0].Length)];
        forged[blocks[0].Start] = 1;

        Assert.Equal(EnvelopeFailure.Damaged, FailureOf(forged));
    }

    [Fact]
    public void An_absurd_iteration_count_is_refused_before_any_work()
    {
        // Число итераций задаёт сам файл: подсунутый архив с миллиардом итераций подвешивал бы
        // программу на часы ещё до вопроса о пароле.
        var envelope = Sealed([1, 2, 3]);
        BinaryPrimitives.WriteInt32LittleEndian(envelope.AsSpan(9, 4), int.MaxValue);

        Assert.Equal(EnvelopeFailure.Damaged, FailureOf(envelope));
    }

    [Fact]
    public void Not_an_envelope_at_all_is_damaged_not_a_crash()
    {
        Assert.Equal(EnvelopeFailure.Damaged, FailureOf("PK\u0003\u0004 обычный zip"u8.ToArray()));
        Assert.Equal(EnvelopeFailure.Damaged, FailureOf([]));
    }

    [Fact]
    public void A_sealed_file_is_recognised_by_its_mark()
    {
        var folder = Path.Combine(Path.GetTempPath(), "amarin-envelope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var plain = Path.Combine(folder, "data.zip");
            var sealedPath = Path.Combine(folder, "data.amrnbak");
            File.WriteAllBytes(plain, "PK\u0003\u0004"u8.ToArray());

            PasswordEnvelope.SealFile(plain, sealedPath, "пароль", FastIterations);
            var opened = Path.Combine(folder, "opened.zip");
            PasswordEnvelope.OpenFile(sealedPath, opened, "пароль");

            Assert.True(PasswordEnvelope.IsSealed(sealedPath));
            Assert.False(PasswordEnvelope.IsSealed(plain));
            Assert.Equal(File.ReadAllBytes(plain), File.ReadAllBytes(opened));

            // Неудачное открытие не оставляет полуфабриката под именем результата.
            var failed = Path.Combine(folder, "failed.zip");
            Assert.Throws<EnvelopeException>(() => PasswordEnvelope.OpenFile(sealedPath, failed, "не тот"));
            Assert.False(File.Exists(failed));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
