using Raylib_cs;
using LexArcana.Ui;

namespace LexArcana.Visione;

/// <summary>
/// I colori della scena, presi dai token del design system Lex Arcana:
/// pietra e marmo dai toni vellum, il basalto per basi e notte, il rosso pompeiano per i fregi, il gilt per i capitelli.
/// </summary>
internal static class Theme
{
    public static readonly Color Night = Hex(0x0c0a08);        // basalt (Night)
    public static readonly Color Fog = Hex(0x14100d);
    public static readonly Color Floor = Hex(0xcdbf9f);        // vellum-deep (Vellum), lastre di pietra
    public static readonly Color Stone = Hex(0xd8c8a6);        // muri
    public static readonly Color Marble = Hex(0xf4ecdc);       // vellum: colonne e statue
    public static readonly Color Basalt = Hex(0x26201c);       // basalt (Vellum): zoccoli, altare
    public static readonly Color Fresco = Hex(0x9a2b1e);       // pompeian (Vellum): fregi
    public static readonly Color Gilt = Hex(0xc4a058);
    public static readonly Color Flame = Hex(0xe8806a);        // pompeian (Night)

    public static readonly Color Panel = new(0x0c, 0x0a, 0x08, 0xc8);
    public static readonly Color PanelEdge = Hex(0x57493d);    // basalt-edge (Night)

    /// <summary>I colori delle tuniche: ogni nome ne riceve sempre lo stesso.</summary>
    private static readonly Color[] Tunics = [Hex(0xd3a95e), Hex(0x72bba8), Hex(0xe8806a), Hex(0x9a2b1e), Hex(0x2e6a5c), Hex(0x7d5a1c)];

    public static Color TunicFor(string name)
    {
        uint hash = 2166136261;
        foreach (var c in name.ToLowerInvariant()) hash = (hash ^ c) * 16777619;
        return Tunics[hash % (uint)Tunics.Length];
    }

    public static Color Of(Tone tone, byte alpha = 255)
    {
        var (r, g, b) = Palette.Rgb(tone);
        return new Color(r, g, b, alpha);
    }

    public static Color WithAlpha(Color c, byte alpha) => new(c.R, c.G, c.B, alpha);

    private static Color Hex(int rgb) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, (byte)255);
}
