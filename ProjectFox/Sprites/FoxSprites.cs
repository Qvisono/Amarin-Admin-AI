using System.Globalization;

namespace Amarin.Fox;

/// <summary>Цвет символа палитры: тёплый и холодный (у всех, кроме радужки, одинаковые). 0 — прозрачно.</summary>
internal readonly record struct FoxColor(uint Warm, uint Cold);

/// <summary>Кадр анимации: сколько тиков держится, где глаз (якорь мордочек) и сама сетка 48×48.</summary>
internal sealed record FoxFrame(int Hold, (int X, int Y)? Head, string[] Rows);

/// <summary>Анимация: кадры, скорость и повторяется ли она.</summary>
internal sealed record FoxAnimation(string Name, int Fps, bool Loop, IReadOnlyList<FoxFrame> Frames)
{
    public TimeSpan HoldOf(int frame) => TimeSpan.FromSeconds((double)Frames[frame].Hold / Math.Max(1, Fps));
}

/// <summary>Заплатка мордочки или предмета: сдвиг от якоря головы и строки.</summary>
internal sealed record FoxPatch(int X, int Y, string[] Rows);

/// <summary>
/// Кадры Розы из <c>Sprites/*.fox</c>: палитра, анимации, мордочки, предметы, частицы.
/// </summary>
/// <remarks>
/// <para>
/// Формат текстовый, чтобы рисунок читался глазами в диффе, а программа и страница-образец рисовали
/// из одних и тех же файлов. Палитра — строки «символ роль #тёплый [#холодный]». Анимация — «anim»,
/// «fps», «loop» и кадры «frame &lt;тиков&gt; [head x y]» с сеткой ниже. Мордочки и предметы —
/// «face»/«item» и заплатки «at x y» от якоря головы; частицы — «particle» и сетка. Точка —
/// прозрачно (в заплатке — «оставить как в кадре»), запятая — стереть до прозрачного.
/// </para>
/// <para>
/// Разбор без WPF: картинку из сетки собирает <c>FoxBitmaps</c>, а здесь всё проверяется тестом.
/// </para>
/// </remarks>
internal sealed class FoxSprites
{
    public const int FrameSize = 48;

    private const string ResourcePrefix = "Amarin.Fox.Sprites.";

    private FoxSprites(
        IReadOnlyDictionary<char, FoxColor> palette,
        IReadOnlyDictionary<string, FoxAnimation> animations,
        IReadOnlyDictionary<string, IReadOnlyList<FoxPatch>> faces,
        IReadOnlyDictionary<string, IReadOnlyList<FoxPatch>> items,
        IReadOnlyDictionary<string, string[]> particles)
    {
        Palette = palette;
        Animations = animations;
        Faces = faces;
        Items = items;
        Particles = particles;
    }

    public IReadOnlyDictionary<char, FoxColor> Palette { get; }

    public IReadOnlyDictionary<string, FoxAnimation> Animations { get; }

    public IReadOnlyDictionary<string, IReadOnlyList<FoxPatch>> Faces { get; }

    public IReadOnlyDictionary<string, IReadOnlyList<FoxPatch>> Items { get; }

    public IReadOnlyDictionary<string, string[]> Particles { get; }

    /// <summary>Читает все файлы из ресурсов сборки. Для рабочего потока: это сотни килобайт текста.</summary>
    public static FoxSprites Load()
    {
        var assembly = typeof(FoxSprites).Assembly;
        var files = new List<(string Name, string Text)>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal) || !resource.EndsWith(".fox", StringComparison.Ordinal))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream);
            files.Add((resource[ResourcePrefix.Length..^".fox".Length], reader.ReadToEnd()));
        }

        return Parse(files);
    }

    /// <summary>Разбирает файлы: имя без расширения → текст.</summary>
    public static FoxSprites Parse(IEnumerable<(string Name, string Text)> files)
    {
        var palette = new Dictionary<char, FoxColor>();
        var animations = new Dictionary<string, FoxAnimation>(StringComparer.Ordinal);
        var faces = new Dictionary<string, IReadOnlyList<FoxPatch>>(StringComparer.Ordinal);
        var items = new Dictionary<string, IReadOnlyList<FoxPatch>>(StringComparer.Ordinal);
        var particles = new Dictionary<string, string[]>(StringComparer.Ordinal);

        foreach (var (name, text) in files)
        {
            var lines = text.Replace("\r", "", StringComparison.Ordinal).Split('\n');
            switch (name)
            {
                case "palette":
                    ParsePalette(lines, palette);
                    break;
                case "faces":
                    ParsePatches(lines, "face", faces);
                    break;
                case "items":
                    ParsePatches(lines, "item", items);
                    break;
                case "particles":
                    ParseParticles(lines, particles);
                    break;
                default:
                    var animation = ParseAnimation(name, lines);
                    animations[animation.Name] = animation;
                    break;
            }
        }

        return new FoxSprites(palette, animations, faces, items, particles);
    }

    /// <summary>
    /// Кадр с мордочкой и предметом поверх — сетка символов. Мордочка и предмет ложатся только на
    /// кадр с якорем головы: у клубка во сне и у стоячих поз с 7-й картинки лицо нарисовано своё.
    /// </summary>
    public char[][] Compose(FoxFrame frame, string? face, string? item)
    {
        var rows = frame.Rows.Select(row => row.PadRight(FrameSize, '.').ToCharArray()).ToArray();
        if (frame.Head is { } head)
        {
            if (face is not null && Faces.TryGetValue(face, out var facePatches))
            {
                Stamp(rows, facePatches, head);
            }

            if (item is not null && Items.TryGetValue(item, out var itemPatches))
            {
                Stamp(rows, itemPatches, head);
            }
        }

        return rows;
    }

    /// <summary>Предмет без кадра: заплатки, сдвинутые к своему левому верхнему углу.</summary>
    public char[][] Alone(string item)
    {
        if (!Items.TryGetValue(item, out var patches) || patches.Count == 0)
        {
            return [['.']];
        }

        var left = patches.Min(patch => patch.X);
        var top = patches.Min(patch => patch.Y);
        var width = patches.Max(patch => patch.X - left + patch.Rows.Max(row => row.Length));
        var height = patches.Max(patch => patch.Y - top + patch.Rows.Length);
        var rows = Enumerable.Range(0, height).Select(_ => Enumerable.Repeat('.', width).ToArray()).ToArray();
        Stamp(rows, patches, (-left, -top));
        return rows;
    }

    /// <summary>
    /// Нижняя строка рисунка у анимации — та, на которой стоят лапы. Берётся по всем кадрам: у
    /// прыжка на месте средние кадры выше, а опора у него та же, что у первого.
    /// </summary>
    public static int GroundOf(FoxAnimation animation)
    {
        var ground = 0;
        foreach (var frame in animation.Frames)
        {
            for (var y = frame.Rows.Length - 1; y > ground; y--)
            {
                if (frame.Rows[y].AsSpan().ContainsAnyExcept('.'))
                {
                    ground = y;
                    break;
                }
            }
        }

        return ground;
    }

    private static void Stamp(char[][] rows, IReadOnlyList<FoxPatch> patches, (int X, int Y) head)
    {
        foreach (var patch in patches)
        {
            for (var dy = 0; dy < patch.Rows.Length; dy++)
            {
                var y = head.Y + patch.Y + dy;
                if (y < 0 || y >= rows.Length)
                {
                    continue;
                }

                var line = patch.Rows[dy];
                for (var dx = 0; dx < line.Length; dx++)
                {
                    var x = head.X + patch.X + dx;
                    if (line[dx] == '.' || x < 0 || x >= rows[y].Length)
                    {
                        continue;
                    }

                    rows[y][x] = line[dx] == ',' ? '.' : line[dx];
                }
            }
        }
    }

    private static void ParsePalette(string[] lines, Dictionary<char, FoxColor> palette)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var warm = parts.Length > 2 ? Argb(parts[2]) : 0u;
            var cold = parts.Length > 3 ? Argb(parts[3]) : warm;
            palette[parts[0][0]] = new FoxColor(warm, cold);
        }
    }

    private static uint Argb(string hex) =>
        0xFF000000u | uint.Parse(hex.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static FoxAnimation ParseAnimation(string fileName, string[] lines)
    {
        var name = fileName;
        var fps = 8;
        var loop = false;
        var frames = new List<FoxFrame>();
        List<string>? rows = null;
        var hold = 1;
        (int X, int Y)? head = null;

        void Close()
        {
            if (rows is not null)
            {
                frames.Add(new FoxFrame(hold, head, [.. rows]));
            }
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (words[0])
            {
                case "anim":
                    name = words[1];
                    break;
                case "fps":
                    fps = int.Parse(words[1], CultureInfo.InvariantCulture);
                    break;
                case "loop":
                    loop = true;
                    break;
                case "frame":
                    Close();
                    rows = [];
                    hold = int.Parse(words[1], CultureInfo.InvariantCulture);
                    var at = Array.IndexOf(words, "head");
                    head = at > 0
                        ? (int.Parse(words[at + 1], CultureInfo.InvariantCulture), int.Parse(words[at + 2], CultureInfo.InvariantCulture))
                        : null;
                    break;
                default:
                    rows?.Add(line);
                    break;
            }
        }

        Close();
        return new FoxAnimation(name, fps, loop, frames);
    }

    private static void ParsePatches(string[] lines, string word, Dictionary<string, IReadOnlyList<FoxPatch>> into)
    {
        List<FoxPatch>? current = null;
        int x = 0, y = 0;
        List<string>? rows = null;

        void Close()
        {
            if (current is not null && rows is { Count: > 0 })
            {
                current.Add(new FoxPatch(x, y, [.. rows]));
            }

            rows = null;
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words[0] == word)
            {
                Close();
                current = [];
                into[words[1]] = current;
            }
            else if (words[0] == "at" && current is not null)
            {
                Close();
                x = int.Parse(words[1], CultureInfo.InvariantCulture);
                y = int.Parse(words[2], CultureInfo.InvariantCulture);
                rows = [];
            }
            else
            {
                rows?.Add(line);
            }
        }

        Close();
    }

    private static void ParseParticles(string[] lines, Dictionary<string, string[]> into)
    {
        string? name = null;
        var rows = new List<string>();

        void Close()
        {
            if (name is not null && rows.Count > 0)
            {
                into[name] = [.. rows];
            }

            rows = [];
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("particle ", StringComparison.Ordinal))
            {
                Close();
                name = line["particle ".Length..].Trim();
            }
            else
            {
                rows.Add(line);
            }
        }

        Close();
    }
}
