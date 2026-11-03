using System.Text;

namespace LexArcana.Ui;

/// <summary>
/// I colori del design system Lex Arcana (tema Night, pensato per sfondi scuri).
/// Per il terminale li trasforma in sequenze ANSI a 24 bit; con <c>enabled: false</c> il testo esce senza colori.
/// </summary>
public sealed class Palette(bool enabled)
{
    public bool Enabled => enabled;

    /// <summary>Il colore di un tono nel tema Night, come (r, g, b).</summary>
    public static (byte R, byte G, byte B) Rgb(Tone tone) => tone switch
    {
        Tone.Ink => (0xee, 0xe4, 0xd2),
        Tone.Muted => (0xb8, 0xa8, 0x92),
        Tone.Pompeian => (0xe8, 0x80, 0x6a),
        Tone.Bronze => (0xd3, 0xa9, 0x5e),
        Tone.Verdigris => (0x72, 0xbb, 0xa8),
        Tone.Gilt => (0xc4, 0xa0, 0x58),
        _ => (0xee, 0xe4, 0xd2),
    };

    public string Format(StyledText text)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < text.Lines.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            foreach (var span in text.Lines[i]) Append(sb, span);
        }
        return sb.ToString();
    }

    public string Format(string text, Tone tone, bool bold = false, bool italic = false) =>
        Format(new StyledText().Add(text, tone, bold, italic));

    public string Ink(string s) => Format(s, Tone.Ink);
    public string Muted(string s) => Format(s, Tone.Muted);
    public string Pompeian(string s) => Format(s, Tone.Pompeian);
    public string Bronze(string s) => Format(s, Tone.Bronze);
    public string Gilt(string s) => Format(s, Tone.Gilt);
    public string Bold(string s) => enabled ? $"\e[1m{s}\e[22m" : s;

    private void Append(StringBuilder sb, Span span)
    {
        if (!enabled)
        {
            sb.Append(span.Text);
            return;
        }
        var (r, g, b) = Rgb(span.Tone);
        sb.Append($"\e[38;2;{r};{g};{b}m");
        if (span.Bold) sb.Append("\e[1m");
        if (span.Italic) sb.Append("\e[3m");
        sb.Append(span.Text).Append("\e[0m");
    }

    /// <summary>Colori attivi se l'uscita è un terminale e la variabile NO_COLOR non è impostata.</summary>
    public static bool ShouldUseColor() =>
        !Console.IsOutputRedirected && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));
}
