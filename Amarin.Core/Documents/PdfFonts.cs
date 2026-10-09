using System.Globalization;
using System.Text;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;
using AddedFont = UglyToad.PdfPig.Writer.PdfDocumentBuilder.AddedFont;

namespace Amarin.Core;

/// <summary>
/// Шрифты PDF: системные шрифты Windows, встроенные в файл, — кириллица и всё прочее, что в них
/// есть, выходит в файле таким же, как на экране.
/// </summary>
/// <remarks>
/// <para>
/// Встроенные в формат PDF шрифты (Helvetica и соседи) кириллицы не знают, поэтому шрифт берётся
/// из папки шрифтов Windows и встраивается. Встраивается он целиком — подмножеств библиотека не
/// делает, — поэтому каждое начертание добавляется, только когда его впервые просят.
/// </para>
/// <para>
/// Знак, которого в шрифте нет, библиотека не пропускает, а роняет всю сборку. Поэтому каждый
/// знак проверяется один раз и запоминается: нет в основном — берётся из Segoe UI Symbol, нет и
/// там — эмодзи выпадает, остальное становится «?». Табуляция — четыре пробела.
/// </para>
/// </remarks>
internal sealed class PdfFonts
{
    private static readonly string[] RegularFiles = ["segoeui.ttf", "arial.ttf", "calibri.ttf"];
    private static readonly string[] BoldFiles = ["segoeuib.ttf", "arialbd.ttf", "calibrib.ttf"];
    private static readonly string[] ItalicFiles = ["segoeuii.ttf", "ariali.ttf", "calibrii.ttf"];
    private static readonly string[] BoldItalicFiles = ["segoeuiz.ttf", "arialbi.ttf", "calibriz.ttf"];
    private static readonly string[] MonoFiles = ["consola.ttf", "cour.ttf"];
    private static readonly string[] SymbolFiles = ["seguisym.ttf"];

    private readonly PdfPageBuilder _probe;
    private readonly PdfDocumentBuilder _builder;
    private readonly string _folder;
    private readonly Dictionary<(AddedFont Font, char Character), bool> _supported = [];
    private readonly Dictionary<(AddedFont Font, double Size, string Text), double> _widths = [];
    private AddedFont? _bold;
    private AddedFont? _italic;
    private AddedFont? _boldItalic;
    private AddedFont? _mono;
    private AddedFont? _symbol;
    private bool _symbolTried;

    private PdfFonts(PdfDocumentBuilder builder, string folder, PdfPageBuilder probe, AddedFont regular)
    {
        _builder = builder;
        _folder = folder;
        _probe = probe;
        Regular = regular;
    }

    public AddedFont Regular { get; }

    // Начертания добавляются в документ при первой надобности: библиотека кладёт в файл каждый
    // добавленный шрифт целиком, и страница обычного текста весила бы как шесть шрифтов.
    public AddedFont Bold => _bold ??= Add(BoldFiles) ?? Regular;

    public AddedFont Italic => _italic ??= Add(ItalicFiles) ?? Regular;

    public AddedFont BoldItalic => _boldItalic ??= Add(BoldItalicFiles) ?? Bold;

    public AddedFont Mono => _mono ??= Add(MonoFiles) ?? Regular;

    public AddedFont? Symbol
    {
        get
        {
            if (!_symbolTried)
            {
                _symbolTried = true;
                _symbol = Add(SymbolFiles);
            }

            return _symbol;
        }
    }

    /// <param name="probe">Страница, на которой меряют текст: мерка не рисует, а страница у библиотеки — единственный способ мерить.</param>
    public static PdfFonts Load(PdfDocumentBuilder builder, PdfPageBuilder probe)
    {
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var regular = Add(builder, folder, RegularFiles) ?? throw new DocumentException(
            "No usable font was found in the Windows Fonts folder (Segoe UI, Arial or Calibri), so the PDF cannot be made.");
        return new PdfFonts(builder, folder, probe, regular);
    }

    private AddedFont? Add(string[] files) => Add(_builder, _folder, files);

    private static AddedFont? Add(PdfDocumentBuilder builder, string folder, string[] files)
    {
        foreach (var file in files)
        {
            var path = Path.Combine(folder, file);
            if (!File.Exists(path))
            {
                continue;
            }

            var bytes = File.ReadAllBytes(path);
            if (builder.CanUseTrueTypeFont(bytes, out _))
            {
                return builder.AddTrueTypeFont(bytes);
            }
        }

        return null;
    }

    public AddedFont For(bool bold, bool italic, bool code) =>
        code ? Mono : (bold, italic) switch
        {
            (true, true) => BoldItalic,
            (true, false) => Bold,
            (false, true) => Italic,
            _ => Regular
        };

    /// <summary>
    /// Текст, разложенный по шрифтам, которые его знают: основной, затем символьный; чего нет нигде —
    /// вопросительный знак, эмодзи — выпадает.
    /// </summary>
    public List<(string Text, AddedFont Font)> Split(string text, AddedFont font)
    {
        var pieces = new List<(string Text, AddedFont Font)>();
        var current = new StringBuilder();
        var currentFont = font;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c))
            {
                // Знак вне основной плоскости — почти всегда эмодзи; его нет ни в одном из шрифтов.
                i++;
                continue;
            }

            if (c == '\t')
            {
                Push("    ", font);
                continue;
            }

            if (char.IsControl(c) || char.IsLowSurrogate(c))
            {
                continue;
            }

            if (Has(font, c))
            {
                Push(c.ToString(), font);
            }
            else if (Symbol is { } symbol && Has(symbol, c))
            {
                Push(c.ToString(), symbol);
            }
            else
            {
                Push("?", font);
            }
        }

        if (current.Length > 0)
        {
            pieces.Add((current.ToString(), currentFont));
        }

        return pieces;

        void Push(string value, AddedFont with)
        {
            if (!ReferenceEquals(with, currentFont) && current.Length > 0)
            {
                pieces.Add((current.ToString(), currentFont));
                current.Clear();
            }

            currentFont = with;
            current.Append(value);
        }
    }

    /// <summary>Ширина куска, который этот шрифт точно знает (см. <see cref="Split"/>), в пунктах.</summary>
    public double Width(string text, AddedFont font, double size)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        if (_widths.TryGetValue((font, size, text), out var cached))
        {
            return cached;
        }

        var letters = _probe.MeasureText(text, size, new PdfPoint(0, 0), font);
        var width = letters.Count == 0 ? 0 : letters[^1].EndBaseLine.X - letters[0].StartBaseLine.X;
        if (_widths.Count < 50_000)
        {
            _widths[(font, size, text)] = width;
        }

        return width;
    }

    private bool Has(AddedFont font, char c)
    {
        if (c == ' ')
        {
            return true;
        }

        if (_supported.TryGetValue((font, c), out var known))
        {
            return known;
        }

        bool has;
        try
        {
            _ = _probe.MeasureText(c.ToString(CultureInfo.InvariantCulture), 10, new PdfPoint(0, 0), font);
            has = true;
        }
        catch (InvalidOperationException)
        {
            has = false;
        }

        _supported[(font, c)] = has;
        return has;
    }
}
