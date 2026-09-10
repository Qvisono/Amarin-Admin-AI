namespace Amarin.Core;

/// <summary>
/// Таблица команд LaTeX. Здесь только то, что реально встречается в ответах моделей:
/// греческие буквы, знаки операций и отношений, стрелки, крупные операторы и имена функций.
/// Всё, чего в таблице нет, разметка покажет прямым шрифтом как есть — формула не исчезнет.
/// </summary>
public static class MathSymbols
{
    private static readonly Dictionary<string, (string Text, MathTokenKind Kind)> Table = Build();

    /// <summary>Имена функций: набираются прямым шрифтом, а не курсивом.</summary>
    private static readonly HashSet<string> Functions =
    [
        "sin", "cos", "tan", "cot", "sec", "csc",
        "arcsin", "arccos", "arctan", "sinh", "cosh", "tanh", "coth",
        "exp", "log", "ln", "lg", "det", "dim", "ker", "deg", "arg",
        "gcd", "hom", "Pr", "min", "max", "sup", "inf", "mod", "bmod"
    ];

    /// <summary>Функции, у которых индекс ставится снизу: <c>lim</c>, <c>max</c> в выключной формуле.</summary>
    private static readonly HashSet<string> LimitFunctions =
    [
        "lim", "limsup", "liminf", "max", "min", "sup", "inf", "argmax", "argmin"
    ];

    public static bool TryGet(string command, out string text, out MathTokenKind kind)
    {
        if (Table.TryGetValue(command, out var entry))
        {
            text = entry.Text;
            kind = entry.Kind;
            return true;
        }

        if (Functions.Contains(command) || LimitFunctions.Contains(command))
        {
            text = command;
            kind = MathTokenKind.Upright;
            return true;
        }

        text = "";
        kind = MathTokenKind.Variable;
        return false;
    }

    /// <summary>Имя функции, у которой индекс ставится снизу: <c>lim</c>, <c>max</c>, <c>sup</c>.</summary>
    public static bool TryGetLimitFunction(string text) => LimitFunctions.Contains(text);

    private static Dictionary<string, (string, MathTokenKind)> Build()
    {
        var table = new Dictionary<string, (string, MathTokenKind)>(StringComparer.Ordinal);

        void Add(string name, string text, MathTokenKind kind) => table[name] = (text, kind);

        // ── Греческий алфавит ──
        var lower = new (string Name, string Text)[]
        {
            ("alpha", "α"), ("beta", "β"), ("gamma", "γ"), ("delta", "δ"), ("epsilon", "ϵ"),
            ("varepsilon", "ε"), ("zeta", "ζ"), ("eta", "η"), ("theta", "θ"), ("vartheta", "ϑ"),
            ("iota", "ι"), ("kappa", "κ"), ("lambda", "λ"), ("mu", "μ"), ("nu", "ν"), ("xi", "ξ"),
            ("pi", "π"), ("varpi", "ϖ"), ("rho", "ρ"), ("varrho", "ϱ"), ("sigma", "σ"),
            ("varsigma", "ς"), ("tau", "τ"), ("upsilon", "υ"), ("phi", "ϕ"), ("varphi", "φ"),
            ("chi", "χ"), ("psi", "ψ"), ("omega", "ω")
        };
        foreach (var (name, text) in lower)
        {
            Add(name, text, MathTokenKind.Variable);
        }

        var upper = new (string Name, string Text)[]
        {
            ("Gamma", "Γ"), ("Delta", "Δ"), ("Theta", "Θ"), ("Lambda", "Λ"), ("Xi", "Ξ"),
            ("Pi", "Π"), ("Sigma", "Σ"), ("Upsilon", "Υ"), ("Phi", "Φ"), ("Psi", "Ψ"), ("Omega", "Ω")
        };
        foreach (var (name, text) in upper)
        {
            Add(name, text, MathTokenKind.Upright);
        }

        // ── Крупные операторы ──
        Add("sum", "∑", MathTokenKind.BigOperator);
        Add("prod", "∏", MathTokenKind.BigOperator);
        Add("coprod", "∐", MathTokenKind.BigOperator);
        Add("int", "∫", MathTokenKind.BigOperator);
        Add("iint", "∬", MathTokenKind.BigOperator);
        Add("iiint", "∭", MathTokenKind.BigOperator);
        Add("oint", "∮", MathTokenKind.BigOperator);
        Add("bigcup", "⋃", MathTokenKind.BigOperator);
        Add("bigcap", "⋂", MathTokenKind.BigOperator);
        Add("bigoplus", "⨁", MathTokenKind.BigOperator);
        Add("bigotimes", "⨂", MathTokenKind.BigOperator);
        Add("bigvee", "⋁", MathTokenKind.BigOperator);
        Add("bigwedge", "⋀", MathTokenKind.BigOperator);

        // ── Бинарные операции ──
        Add("pm", "±", MathTokenKind.Binary);
        Add("mp", "∓", MathTokenKind.Binary);
        Add("times", "×", MathTokenKind.Binary);
        Add("div", "÷", MathTokenKind.Binary);
        Add("cdot", "·", MathTokenKind.Binary);
        Add("ast", "∗", MathTokenKind.Binary);
        Add("star", "⋆", MathTokenKind.Binary);
        Add("circ", "∘", MathTokenKind.Binary);
        Add("bullet", "∙", MathTokenKind.Binary);
        Add("oplus", "⊕", MathTokenKind.Binary);
        Add("ominus", "⊖", MathTokenKind.Binary);
        Add("otimes", "⊗", MathTokenKind.Binary);
        Add("oslash", "⊘", MathTokenKind.Binary);
        Add("odot", "⊙", MathTokenKind.Binary);
        Add("cap", "∩", MathTokenKind.Binary);
        Add("cup", "∪", MathTokenKind.Binary);
        Add("setminus", "∖", MathTokenKind.Binary);
        Add("wedge", "∧", MathTokenKind.Binary);
        Add("land", "∧", MathTokenKind.Binary);
        Add("vee", "∨", MathTokenKind.Binary);
        Add("lor", "∨", MathTokenKind.Binary);
        Add("neg", "¬", MathTokenKind.Binary);
        Add("lnot", "¬", MathTokenKind.Binary);

        // ── Отношения ──
        Add("leq", "≤", MathTokenKind.Relation);
        Add("le", "≤", MathTokenKind.Relation);
        Add("geq", "≥", MathTokenKind.Relation);
        Add("ge", "≥", MathTokenKind.Relation);
        Add("neq", "≠", MathTokenKind.Relation);
        Add("ne", "≠", MathTokenKind.Relation);
        Add("approx", "≈", MathTokenKind.Relation);
        Add("equiv", "≡", MathTokenKind.Relation);
        Add("sim", "∼", MathTokenKind.Relation);
        Add("simeq", "≃", MathTokenKind.Relation);
        Add("cong", "≅", MathTokenKind.Relation);
        Add("propto", "∝", MathTokenKind.Relation);
        Add("ll", "≪", MathTokenKind.Relation);
        Add("gg", "≫", MathTokenKind.Relation);
        Add("subset", "⊂", MathTokenKind.Relation);
        Add("subseteq", "⊆", MathTokenKind.Relation);
        Add("supset", "⊃", MathTokenKind.Relation);
        Add("supseteq", "⊇", MathTokenKind.Relation);
        Add("in", "∈", MathTokenKind.Relation);
        Add("notin", "∉", MathTokenKind.Relation);
        Add("ni", "∋", MathTokenKind.Relation);
        Add("mid", "∣", MathTokenKind.Relation);
        Add("parallel", "∥", MathTokenKind.Relation);
        Add("perp", "⊥", MathTokenKind.Relation);
        Add("models", "⊨", MathTokenKind.Relation);
        Add("doteq", "≐", MathTokenKind.Relation);

        // ── Стрелки ──
        Add("to", "→", MathTokenKind.Relation);
        Add("rightarrow", "→", MathTokenKind.Relation);
        Add("Rightarrow", "⇒", MathTokenKind.Relation);
        Add("leftarrow", "←", MathTokenKind.Relation);
        Add("Leftarrow", "⇐", MathTokenKind.Relation);
        Add("leftrightarrow", "↔", MathTokenKind.Relation);
        Add("Leftrightarrow", "⇔", MathTokenKind.Relation);
        Add("iff", "⟺", MathTokenKind.Relation);
        Add("implies", "⟹", MathTokenKind.Relation);
        Add("mapsto", "↦", MathTokenKind.Relation);
        Add("uparrow", "↑", MathTokenKind.Relation);
        Add("downarrow", "↓", MathTokenKind.Relation);
        Add("longrightarrow", "⟶", MathTokenKind.Relation);
        Add("longleftarrow", "⟵", MathTokenKind.Relation);

        // ── Прочие знаки ──
        Add("infty", "∞", MathTokenKind.Number);
        Add("partial", "∂", MathTokenKind.Variable);
        Add("nabla", "∇", MathTokenKind.Upright);
        Add("forall", "∀", MathTokenKind.Upright);
        Add("exists", "∃", MathTokenKind.Upright);
        Add("nexists", "∄", MathTokenKind.Upright);
        Add("emptyset", "∅", MathTokenKind.Upright);
        Add("varnothing", "∅", MathTokenKind.Upright);
        Add("aleph", "ℵ", MathTokenKind.Upright);
        Add("hbar", "ℏ", MathTokenKind.Variable);
        Add("ell", "ℓ", MathTokenKind.Variable);
        Add("Re", "ℜ", MathTokenKind.Upright);
        Add("Im", "ℑ", MathTokenKind.Upright);
        Add("degree", "°", MathTokenKind.Upright);
        Add("prime", "′", MathTokenKind.Upright);
        Add("angle", "∠", MathTokenKind.Upright);
        Add("triangle", "△", MathTokenKind.Upright);
        Add("square", "□", MathTokenKind.Upright);
        Add("checkmark", "✓", MathTokenKind.Upright);
        Add("dots", "…", MathTokenKind.Punctuation);
        Add("ldots", "…", MathTokenKind.Punctuation);
        Add("cdots", "⋯", MathTokenKind.Punctuation);
        Add("vdots", "⋮", MathTokenKind.Punctuation);
        Add("ddots", "⋱", MathTokenKind.Punctuation);
        Add("therefore", "∴", MathTokenKind.Relation);
        Add("because", "∵", MathTokenKind.Relation);
        Add("percent", "%", MathTokenKind.Upright);

        // ── Скобки и разделители ──
        Add("lbrace", "{", MathTokenKind.Fence);
        Add("rbrace", "}", MathTokenKind.Fence);
        Add("langle", "⟨", MathTokenKind.Fence);
        Add("rangle", "⟩", MathTokenKind.Fence);
        Add("lceil", "⌈", MathTokenKind.Fence);
        Add("rceil", "⌉", MathTokenKind.Fence);
        Add("lfloor", "⌊", MathTokenKind.Fence);
        Add("rfloor", "⌋", MathTokenKind.Fence);
        Add("vert", "|", MathTokenKind.Fence);
        Add("Vert", "‖", MathTokenKind.Fence);
        Add("|", "‖", MathTokenKind.Fence);

        return table;
    }
}
