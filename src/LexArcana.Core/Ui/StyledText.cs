using System.Text;

namespace LexArcana.Ui;

/// <summary>I colori del design system Lex Arcana con cui si scrive il testo.</summary>
public enum Tone { Ink, Muted, Pompeian, Bronze, Verdigris, Gilt }

/// <summary>Un pezzo di testo con il suo stile.</summary>
public readonly record struct Span(string Text, Tone Tone = Tone.Ink, bool Bold = false, bool Italic = false);

/// <summary>Un messaggio da mostrare: una o più righe fatte di <see cref="Span"/>.</summary>
public sealed class StyledText
{
    private readonly List<List<Span>> _lines = [[]];

    public IReadOnlyList<IReadOnlyList<Span>> Lines => _lines;

    public StyledText Add(string text, Tone tone = Tone.Ink, bool bold = false, bool italic = false)
    {
        if (text.Length > 0) _lines[^1].Add(new(text, tone, bold, italic));
        return this;
    }

    public StyledText NewLine()
    {
        _lines.Add([]);
        return this;
    }

    public string PlainText => string.Join('\n', _lines.Select(l => string.Concat(l.Select(s => s.Text))));

    public override string ToString() => PlainText;
}
