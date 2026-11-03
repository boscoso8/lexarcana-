namespace LexArcana.Ui;

/// <summary>
/// I colori del design system Lex Arcana (tema Night, per terminali scuri) come sequenze ANSI a 24 bit.
/// Con <c>enabled: false</c> il testo esce senza colori.
/// </summary>
public sealed class Palette(bool enabled)
{
    public bool Enabled => enabled;

    public string Ink(string s) => Rgb(s, 0xee, 0xe4, 0xd2);
    public string Muted(string s) => Rgb(s, 0xb8, 0xa8, 0x92);
    public string Pompeian(string s) => Rgb(s, 0xe8, 0x80, 0x6a);
    public string Bronze(string s) => Rgb(s, 0xd3, 0xa9, 0x5e);
    public string Verdigris(string s) => Rgb(s, 0x72, 0xbb, 0xa8);
    public string Gilt(string s) => Rgb(s, 0xc4, 0xa0, 0x58);
    public string Bold(string s) => enabled ? $"\e[1m{s}\e[22m" : s;
    public string Italic(string s) => enabled ? $"\e[3m{s}\e[23m" : s;

    private string Rgb(string s, int r, int g, int b) => enabled ? $"\e[38;2;{r};{g};{b}m{s}\e[39m" : s;

    /// <summary>Colori attivi se l'uscita è un terminale e la variabile NO_COLOR non è impostata.</summary>
    public static bool ShouldUseColor() =>
        !Console.IsOutputRedirected && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));
}
