using System.Text.RegularExpressions;
using Amarin.Fox;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Кадры Розы (1.35.0): файлы <c>Sprites/*.fox</c> читаются, каждый кадр — сетка 48×48 из символов
/// палитры, лапы стоят на линии опоры, и всё, что называют мозг и сцена, нарисовано.
/// </summary>
public sealed class FoxSpriteTests
{
    private static readonly FoxSprites Sprites = FoxSprites.Load();

    /// <summary>Позы не на кромке: в воздухе, в руках и за полем ввода. У них своя опора или её нет.</summary>
    private static readonly HashSet<string> Unground = ["fly", "hang", "peek", "peekup", "peekdown", "hide"];

    [Fact]
    public void Every_animation_is_a_48_by_48_grid_of_palette_symbols()
    {
        Assert.True(Sprites.Animations.Count >= 39);
        foreach (var (name, animation) in Sprites.Animations)
        {
            Assert.True(animation.Frames.Count > 0, name);
            Assert.True(animation.Fps > 0, name);
            foreach (var frame in animation.Frames)
            {
                Assert.True(frame.Hold > 0, name);
                Assert.Equal(FoxSprites.FrameSize, frame.Rows.Length);
                foreach (var row in frame.Rows)
                {
                    Assert.True(row.Length <= FoxSprites.FrameSize, name);
                    Assert.All(row, symbol => Assert.True(Sprites.Palette.ContainsKey(symbol), $"{name}: «{symbol}»"));
                }
            }
        }
    }

    [Fact]
    public void Faces_items_and_particles_use_only_palette_symbols()
    {
        var patches = Sprites.Faces.Values.Concat(Sprites.Items.Values).SelectMany(list => list).SelectMany(patch => patch.Rows);
        var particles = Sprites.Particles.Values.SelectMany(rows => rows);
        Assert.All(patches.Concat(particles), row => Assert.All(row, symbol => Assert.True(Sprites.Palette.ContainsKey(symbol), $"«{symbol}»")));
    }

    [Fact]
    public void Paws_of_every_pose_on_an_edge_stand_on_the_bottom_rows()
    {
        foreach (var (name, animation) in Sprites.Animations.Where(pair => !Unground.Contains(pair.Key)))
        {
            Assert.InRange(FoxSprites.GroundOf(animation), 44, 47);
        }
    }

    /// <summary>Лица кладутся на якорь головы: у сидячих реакций он обязан быть, иначе мордочка не ляжет.</summary>
    [Fact]
    public void Reactions_that_carry_a_face_have_a_head_anchor()
    {
        foreach (var name in (string[])["sit", "tail", "earflick", "wash", "sniff", "nod", "nuzzle", "knead", "bite", "sneeze", "wave", "think", "stomp", "peek", "hang", "fly", "walk", "run"])
        {
            Assert.All(Sprites.Animations[name].Frames, frame => Assert.NotNull(frame.Head));
        }
    }

    [Fact]
    public void Everything_the_brain_and_the_stage_name_is_drawn()
    {
        var sources = Directory.EnumerateFiles(Path.Combine(SourceTree.RepositoryRoot, "ProjectFox"), "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToList();
        string[] Names(string pattern) =>
            [.. sources.SelectMany(text => Regex.Matches(text, pattern).Select(match => match.Groups[1].Value)).Distinct()];

        var anims = Names(@"(?:FoxAction\.Anim|Play|React)\(""([a-z]+)""")
            .Concat(Names(@"Play\(""[a-z]+"", ""([a-z]+)""\)"));
        var faces = Names(@"FoxAction\.Face\(""([a-z]+)""").Concat(Names(@"_face = ""([a-z]+)"""));
        var particles = Names(@"(?:FoxAction\.Particle|Burst)\(""([a-z]+)""").Concat(Names(@"Particle\(""([a-z]+)""\)"));
        var items = Names(@"FoxAction\.Item\(""([a-z]+)""").Concat(Names(@"_item = ""([a-z]+)""")).Concat(Names(@"\.Item\(""([a-z]+)""\)"));

        Assert.All(anims, name => Assert.True(Sprites.Animations.ContainsKey(name), "анимация " + name));
        Assert.All(faces, name => Assert.True(Sprites.Faces.ContainsKey(name), "мордочка " + name));
        Assert.All(particles, name => Assert.True(Sprites.Particles.ContainsKey(name), "частица " + name));
        Assert.All(items, name => Assert.True(Sprites.Items.ContainsKey(name), "предмет " + name));
        Assert.Contains("hang", anims);
        Assert.Contains("purr", faces);
    }

    [Fact]
    public void A_ball_on_its_own_is_cut_to_its_patch()
    {
        var ball = Sprites.Alone("ball");
        Assert.Equal(6, ball.Length);
        Assert.All(ball, row => Assert.Equal(6, row.Length));
        Assert.Equal('.', ball[0][0]);
        Assert.NotEqual('.', ball[2][2]);
    }

    /// <summary>Части тела меряются от глаза сидящего Розы: нос слева ниже глаза, уши над ним, хвост обвивает лапы спереди.</summary>
    [Theory]
    [InlineData(7, 18, "Nose")]
    [InlineData(10, 4, "Ear")]
    [InlineData(12, 20, "Chin")]
    [InlineData(16, 13, "Head")]
    [InlineData(22, 30, "Back")]
    [InlineData(25, 42, "Tail")]
    public void Regions_of_the_sitting_fox_are_measured_from_the_eye(int x, int y, string expected)
    {
        var sit = Sprites.Animations["sit"].Frames[0];
        Assert.NotEqual('.', sit.Rows[y][x]);
        Assert.Equal(Enum.Parse<FoxRegion>(expected), FoxStage.RegionOf((x, y), sit.Head));
    }

    [Fact]
    public void A_pose_without_a_head_anchor_is_all_back()
    {
        Assert.Equal(FoxRegion.Back, FoxStage.RegionOf((5, 30), null));
    }
}
