using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Raylib_cs;
using LexArcana.Protocol;
using LexArcana.Ui;

namespace LexArcana.Visione;

/// <summary>
/// Tutto ciò che sta sopra la scena: la chat, il campo di scrittura, i nomi sopra le teste,
/// il mirino e i pannelli di aiuto. Il testo usa i font del design system (EB Garamond e Cinzel).
/// </summary>
internal sealed partial class Hud : IDisposable
{
    private const int MaxLog = 60;
    private const int VisibleMessages = 8;
    private const double FadeAfter = 14;
    private const float TextSize = 22, LineHeight = 27, Margin = 16;

    private readonly Font _text, _caps;
    private readonly List<(StyledText Text, double Time)> _log = [];
    private readonly StringBuilder _input = new();
    private (string Text, double Time)? _banner;

    public Hud(string assets)
    {
        int[] codepoints = [.. Enumerable.Range(32, 95), .. Enumerable.Range(160, 96),
            0x2013, 0x2014, 0x2018, 0x2020, 0x2021, 0x2019, 0x201C, 0x201D, 0x2026, 0x2022, 0x2192, 0x2212, 0x2766, 0x25C6];
        _text = Raylib.LoadFontEx(Path.Combine(assets, "Fonts", "EBGaramond.ttf"), 44, codepoints, codepoints.Length);
        _caps = Raylib.LoadFontEx(Path.Combine(assets, "Fonts", "Cinzel.ttf"), 56, codepoints, codepoints.Length);
        Raylib.SetTextureFilter(_text.Texture, TextureFilter.Bilinear);
        Raylib.SetTextureFilter(_caps.Texture, TextureFilter.Bilinear);
    }

    public bool Typing { get; private set; }

    /// <summary>Disposizione per schermi touch: chat in alto a sinistra, salute in alto a destra, il basso libero per i pollici.</summary>
    public bool Compact { get; init; }

    /// <summary>Un'etichetta in Cinzel centrata in un rettangolo (i pulsanti touch).</summary>
    public void DrawCentered(string text, Rectangle rect, Tone tone)
    {
        float size = Math.Clamp(rect.Height * 0.28f, 11f, 22f);
        var m = Raylib.MeasureTextEx(_caps, text, size, 1);
        if (m.X > rect.Width * 0.86f)
        {
            size *= rect.Width * 0.86f / m.X;   // l'etichetta si stringe per stare nel pulsante
            m = Raylib.MeasureTextEx(_caps, text, size, 1);
        }
        DrawCaps(text, new(rect.X + (rect.Width - m.X) / 2, rect.Y + (rect.Height - m.Y) / 2), size, Theme.Of(tone));
    }
    public string Input => _input.ToString();

    public void Add(StyledText text, double now)
    {
        _log.Add((text, now));
        if (_log.Count > MaxLog) _log.RemoveAt(0);
    }

    /// <summary>Una narrazione del Demiurgo: compare anche in grande al centro dello schermo.</summary>
    public void Banner(string text, double now) => _banner = (text, now);

    public void OpenInput(string prefix = "")
    {
        Typing = true;
        _input.Clear().Append(prefix);
        while (Raylib.GetCharPressed() != 0) { } // il tasto che ha aperto la chat non va scritto
    }

    /// <summary>Gestisce la tastiera mentre si scrive; restituisce la riga quando si preme Invio.</summary>
    public string? UpdateInput()
    {
        for (int c = Raylib.GetCharPressed(); c != 0; c = Raylib.GetCharPressed())
            if (c >= 32 && _input.Length < 500) _input.Append(char.ConvertFromUtf32(c));

        if ((Raylib.IsKeyPressed(KeyboardKey.Backspace) || Raylib.IsKeyPressedRepeat(KeyboardKey.Backspace)) && _input.Length > 0)
        {
            int remove = _input.Length >= 2 && char.IsLowSurrogate(_input[^1]) ? 2 : 1;
            _input.Remove(_input.Length - remove, remove);
        }
        if (Raylib.IsKeyPressed(KeyboardKey.Escape))
        {
            Typing = false;
            return null;
        }
        if (Raylib.IsKeyPressed(KeyboardKey.Enter) || Raylib.IsKeyPressed(KeyboardKey.KpEnter))
        {
            Typing = false;
            return _input.ToString();
        }
        return null;
    }

    public void DrawPlaceName(string name)
    {
        DrawCaps(name.ToUpperInvariant(), new(Margin + 2, Margin + 2), 22, Theme.WithAlpha(Theme.Night, 200));
        DrawCaps(name.ToUpperInvariant(), new(Margin, Margin), 22, Theme.Of(Tone.Gilt));
    }

    public static void DrawCrosshair()
    {
        int cx = Raylib.GetScreenWidth() / 2, cy = Raylib.GetScreenHeight() / 2;
        Raylib.DrawCircle(cx, cy, 3.5f, Theme.WithAlpha(Theme.Night, 160));
        Raylib.DrawCircle(cx, cy, 2f, Theme.Of(Tone.Ink, 220));
    }

    public void DrawNameTag(string name, Vector2 screen, float distance)
    {
        float size = Math.Clamp(26f - distance * 0.8f, 14f, 24f);
        var measure = Raylib.MeasureTextEx(_caps, name, size, 1);
        var at = new Vector2(screen.X - measure.X / 2, screen.Y - measure.Y);
        var rect = new Rectangle(at.X - 8, at.Y - 2, measure.X + 16, measure.Y + 4);
        Raylib.DrawRectangleRounded(rect, 0.3f, 6, Theme.WithAlpha(Theme.Night, 150));
        DrawCaps(name, at, size, Theme.Of(Tone.Ink));
    }

    public void Draw(double now, bool showHelp, IReadOnlyList<string> present, string? overlay)
    {
        int w = Raylib.GetScreenWidth(), h = Raylib.GetScreenHeight();
        float width = MathF.Min(720, w - 2 * Margin);

        // Narrazione in evidenza
        if (_banner is var (bannerText, bannerTime) && now - bannerTime < 9)
        {
            byte alpha = (byte)(255 * Math.Clamp((9 - (now - bannerTime)) / 1.5, 0, 1));
            var lines = Wrap(new StyledText().Add("❦ ", Tone.Gilt).Add(bannerText), MathF.Min(820, w - 80), 28);
            float y = h * 0.16f;
            float panelHeight = lines.Count * 36 + 24;
            Raylib.DrawRectangleRec(new(w / 2f - MathF.Min(860, w - 40) / 2, y - 12, MathF.Min(860, w - 40), panelHeight), Theme.WithAlpha(Theme.Panel, (byte)(alpha * 0.8)));
            foreach (var line in lines)
            {
                float lineWidth = line.Sum(p => Measure(p.Text, 28));
                DrawLine(line, new(w / 2f - lineWidth / 2, y), 28, alpha);
                y += 36;
            }
        }

        // Registro della chat, dal basso verso l'alto
        if (Compact) width = MathF.Min(width, MathF.Max(260, w * 0.46f));
        var recent = _log.Where(e => Typing || now - e.Time < FadeAfter + 2).TakeLast(Compact ? 4 : VisibleMessages).ToList();
        var laid = recent.Select(e => (Lines: Wrap(e.Text, width - 24, TextSize), e.Time)).ToList();
        float total = laid.Sum(l => l.Lines.Count) * LineHeight;
        float bottom = Compact ? 52 + total + 12 : h - Margin - (Typing ? LineHeight + 20 : 0);
        if (laid.Count > 0)
        {
            byte panelAlpha = (byte)(Typing ? 200 : 140);
            Raylib.DrawRectangleRec(new(Margin, bottom - total - 12, width, total + 16), Theme.WithAlpha(Theme.Panel, panelAlpha));
        }
        float yLog = bottom - total - 4;
        foreach (var (lines, time) in laid)
        {
            byte alpha = Typing ? (byte)255 : (byte)(255 * Math.Clamp((FadeAfter + 2 - (now - time)) / 2, 0, 1));
            foreach (var line in lines)
            {
                DrawLine(line, new(Margin + 12, yLog), TextSize, alpha);
                yLog += LineHeight;
            }
        }

        // Campo di scrittura
        if (Typing)
        {
            var box = new Rectangle(Margin, h - Margin - LineHeight - 12, width, LineHeight + 12);
            Raylib.DrawRectangleRec(box, Theme.Panel);
            Raylib.DrawRectangleLinesEx(box, 1, Theme.Of(Tone.Gilt));
            bool caret = (int)(now * 2) % 2 == 0;
            DrawText(_input + (caret ? "|" : " "), new(box.X + 12, box.Y + 5), TextSize, Theme.Of(Tone.Ink));
        }
        else if (!Compact)
        {
            DrawText("Invio per parlare · F1 aiuto", new(w - Margin - Measure("Invio per parlare · F1 aiuto", 18), h - Margin - 20), 18, Theme.Of(Tone.Muted, 190));
        }

        if (present.Count > 0 && Raylib.IsKeyDown(KeyboardKey.Tab)) DrawPresent(present, w);
        if (showHelp && !Compact) DrawHelp(w, h);
        if (showHelp && Compact)
        {
            const string tip = "Pollice sinistro: cammina · trascina a destra: guarda · COLPISCI e TIRA: combatti";
            var lines = Wrap(new StyledText().Add(tip, Tone.Ink), MathF.Min(620, w - 40), 20);
            float ty = h * 0.42f;
            foreach (var line in lines)
            {
                float lw = line.Sum(p => Measure(p.Text, 20));
                Raylib.DrawRectangleRec(new(w / 2f - lw / 2 - 10, ty - 2, lw + 20, 26), Theme.WithAlpha(Theme.Panel, 200));
                DrawLine(line, new(w / 2f - lw / 2, ty), 20, 255);
                ty += 28;
            }
        }
        if (overlay is not null) DrawOverlay(overlay, w, h);
    }

    private void DrawPresent(IReadOnlyList<string> present, int w)
    {
        float panelWidth = 300, y = 70;
        Raylib.DrawRectangleRec(new(w - panelWidth - Margin, y - 10, panelWidth, 50 + present.Count * LineHeight), Theme.Panel);
        DrawCaps("PRESENTI", new(w - panelWidth - Margin + 16, y), 18, Theme.Of(Tone.Gilt));
        y += 34;
        foreach (var name in present)
        {
            var tone = LexArcana.Game.Names.IsDemiurge(name) ? Tone.Gilt : Tone.Bronze;
            DrawText(name, new(w - panelWidth - Margin + 16, y), TextSize, Theme.Of(tone));
            y += LineHeight;
        }
    }

    /// <summary>La scheda del proprio personaggio: quella del server se è arrivata, altrimenti quella del file.</summary>
    public void DrawSheet(SheetShown? sheet, Characters.Character? character)
    {
        var fields = sheet?.Fields.Select(f => (f.Key, f.Value)).ToList()
                     ?? character?.ToSheet().ToList()
                     ?? [];
        string title = sheet?.Owner ?? character?.Player ?? "";
        int h = Raylib.GetScreenHeight();
        float width = 460, x = Margin, y = 64;
        float height = 70 + Math.Max(1, fields.Count) * 25 + 8;
        Raylib.DrawRectangleRec(new(x, y, width, MathF.Min(height, h - y - 120)), Theme.WithAlpha(Theme.Panel, 230));
        Raylib.DrawRectangleLinesEx(new(x, y, width, MathF.Min(height, h - y - 120)), 1, Theme.PanelEdge);
        DrawCaps($"SCHEDA DI {title.ToUpperInvariant()}", new(x + 18, y + 16), 18, Theme.Of(Tone.Gilt));
        float row = y + 52;
        if (fields.Count == 0) DrawText("Ancora vuota: scrivi /imposta <campo> <valore>.", new(x + 18, row), 19, Theme.Of(Tone.Muted));
        foreach (var (key, value) in fields)
        {
            if (row > h - 150) break;
            DrawText(key, new(x + 18, row), 19, Theme.Of(Tone.Muted));
            var lines = Wrap(new StyledText().Add(value), width - 170, 19);
            foreach (var line in lines)
            {
                DrawLine(line, new(x + 150, row), 19, 255);
                row += 25;
            }
        }
    }

    // ---- Combattimento

    /// <summary>Nome, livello e salute sopra un nemico. Il bersaglio del mirino ha il nome dorato.</summary>
    public void DrawEnemyTag(EnemyView e, Vector2 screen, float distance, bool targeted)
    {
        float size = Math.Clamp(22f - distance * 0.6f, 13f, 20f);
        var label = e.Label;
        var measure = Raylib.MeasureTextEx(_caps, label, size, 1);
        float barWidth = MathF.Max(measure.X, 70);
        var at = new Vector2(screen.X - measure.X / 2, screen.Y - measure.Y - 10);
        Raylib.DrawRectangleRounded(new(screen.X - barWidth / 2 - 8, at.Y - 3, barWidth + 16, measure.Y + 16), 0.25f, 6, Theme.WithAlpha(Theme.Night, 160));
        if (targeted) Raylib.DrawRectangleRoundedLinesEx(new(screen.X - barWidth / 2 - 8, at.Y - 3, barWidth + 16, measure.Y + 16), 0.25f, 6, 1.5f, Theme.Of(Tone.Gilt));
        DrawCaps(label, at, size, targeted ? Theme.Of(Tone.Gilt) : e.Rank == "Normal" ? Theme.Of(Tone.Ink) : Theme.Of(Tone.Pompeian));
        float fill = e.MaxHealth == 0 ? 0 : (float)e.Health / e.MaxHealth;
        var bar = new Rectangle(screen.X - barWidth / 2, at.Y + measure.Y + 3, barWidth, 5);
        Raylib.DrawRectangleRec(bar, Theme.Basalt);
        Raylib.DrawRectangleRec(bar with { Width = bar.Width * fill }, Theme.Of(Tone.Pompeian));
    }

    public void DrawFloating(FloatingText f, Vector2 screen, double now)
    {
        float age = (float)(now - f.Time);
        byte alpha = (byte)(255 * Math.Clamp(1.4 - age, 0, 1));
        var at = new Vector2(screen.X - Measure(f.Text, 26) / 2, screen.Y - age * 40);
        Raylib.DrawTextEx(_text, f.Text, at + new Vector2(1.5f, 1.5f), 26, 0, Theme.WithAlpha(Theme.Night, alpha));
        Raylib.DrawTextEx(_text, f.Text, at, 26, 0, Theme.Of(f.Tone, alpha));
    }

    /// <summary>Salute ed esperienza in basso a destra.</summary>
    public void DrawVitals(Vitals v)
    {
        int w = Raylib.GetScreenWidth(), h = Raylib.GetScreenHeight();
        float width = Compact ? MathF.Min(260, w * 0.3f) : 330, x = w - Margin - width, y = Compact ? Margin + 10 : h - Margin - 92;
        Raylib.DrawRectangleRec(new(x - 12, y - 10, width + 24, 72), Theme.WithAlpha(Theme.Panel, 170));
        DrawCaps($"LIVELLO {v.Level}", new(x, y - 2), 15, Theme.Of(Tone.Gilt));
        var health = $"{v.Health}/{v.MaxHealth}";
        DrawText(health, new(x + width - Measure(health, 18), y - 5), 18, Theme.Of(Tone.Ink));
        var hp = new Rectangle(x, y + 18, width, 12);
        Raylib.DrawRectangleRec(hp, Theme.Basalt);
        Raylib.DrawRectangleRec(hp with { Width = width * Math.Clamp((float)v.Health / Math.Max(1, v.MaxHealth), 0, 1) }, Theme.Of(Tone.Pompeian));
        Raylib.DrawRectangleLinesEx(hp, 1, Theme.PanelEdge);
        int from = LexArcana.Combat.Progression.ExperienceFor(v.Level), to = v.NextLevel;
        float xp = to <= from ? 1 : Math.Clamp((float)(v.Experience - from) / (to - from), 0, 1);
        if (v.MaxResolve > 0)
        {
            var will = new Rectangle(x, y + 32, width, 4);
            Raylib.DrawRectangleRec(will, Theme.Basalt);
            Raylib.DrawRectangleRec(will with { Width = width * Math.Clamp((float)v.Resolve / v.MaxResolve, 0, 1) }, Theme.Of(Tone.Verdigris));
        }
        var xpBar = new Rectangle(x, y + 38, width, 5);
        Raylib.DrawRectangleRec(xpBar, Theme.Basalt);
        Raylib.DrawRectangleRec(xpBar with { Width = width * xp }, Theme.Of(Tone.Gilt));
        var label = v.MaxResolve > 0 ? $"volontà {v.Resolve}/{v.MaxResolve} · esperienza {v.Experience}/{to}" : $"esperienza {v.Experience}/{to}";
        DrawText(label, new(x + width - Measure(label, 15), y + 44), 15, Theme.Of(Tone.Muted));
    }

    /// <summary>
    /// La barra dei prodigi conosciuti: numero (il tasto), nome e costo in Volontà; spenti quelli che non ci si può permettere.
    /// <paramref name="selected"/> è quello del pulsante PRODIGIO sugli schermi touch.
    /// </summary>
    public void DrawSpellBar(IReadOnlyList<LexArcana.Combat.Spell> spells, int resolve, int selected)
    {
        if (spells.Count == 0) return;
        int w = Raylib.GetScreenWidth(), h = Raylib.GetScreenHeight();
        if (Compact)
        {
            var s = spells[Math.Clamp(selected, 0, spells.Count - 1)];
            var text = $"{s.Name} · {s.Cost}";
            float tw = Measure(text, 17);
            DrawText(text, new(w - Margin - tw, h * 0.47f), 17, Theme.Of(resolve >= s.Cost ? Tone.Verdigris : Tone.Muted));
            return;
        }
        // una colonna a destra, sopra il pannello della salute: una riga per prodigio
        float width = 330, rowHeight = 27, x = w - Margin - width;
        float y = h - Margin - 92 - 14 - spells.Count * (rowHeight + 3);
        for (int i = 0; i < spells.Count; i++)
        {
            var s = spells[i];
            bool ok = resolve >= s.Cost;
            var tone = s.Tradition switch { LexArcana.Combat.Tradition.Miracoli => Tone.Gilt, LexArcana.Combat.Tradition.Invocazioni => Tone.Verdigris, _ => Tone.Bronze };
            var rect = new Rectangle(x - 12, y + i * (rowHeight + 3), width + 24, rowHeight);
            Raylib.DrawRectangleRec(rect, Theme.WithAlpha(Theme.Panel, 170));
            Raylib.DrawRectangleRec(rect with { Width = 3 }, Theme.Of(ok ? tone : Tone.Muted, 220));
            DrawCaps($"{i + 1}", new(rect.X + 12, rect.Y + 6), 14, Theme.Of(ok ? tone : Tone.Muted));
            DrawText(s.Name, new(rect.X + 34, rect.Y + 3), 19, Theme.Of(ok ? Tone.Ink : Tone.Muted, (byte)(ok ? 255 : 160)));
            var cost = $"{s.Cost} volontà";
            DrawText(cost, new(rect.X + rect.Width - 12 - Measure(cost, 15), rect.Y + 6), 15, Theme.Of(ok ? Tone.Verdigris : Tone.Muted));
        }
    }

    /// <summary>Un bordo rosso che sfuma: sei stato colpito.</summary>
    public static void DrawHurt(float strength)
    {
        int w = Raylib.GetScreenWidth(), h = Raylib.GetScreenHeight(), band = 120;
        var red = Theme.WithAlpha(Theme.Fresco, (byte)(180 * strength));
        var none = Theme.WithAlpha(Theme.Fresco, 0);
        Raylib.DrawRectangleGradientV(0, 0, w, band, red, none);
        Raylib.DrawRectangleGradientV(0, h - band, w, band, none, red);
        Raylib.DrawRectangleGradientH(0, 0, band, h, red, none);
        Raylib.DrawRectangleGradientH(w - band, 0, band, h, none, red);
    }

    public void DrawDown(double secondsLeft)
    {
        int w = Raylib.GetScreenWidth(), h = Raylib.GetScreenHeight();
        Raylib.DrawRectangle(0, 0, w, h, Theme.WithAlpha(Theme.Night, 150));
        const string title = "SEI A TERRA";
        DrawCaps(title, new(w / 2f - Raylib.MeasureTextEx(_caps, title, 40, 1).X / 2, h / 2f - 50), 40, Theme.Of(Tone.Pompeian));
        var hint = secondsLeft > 0 ? $"I compagni ti coprono: ti rialzi tra {Math.Ceiling(secondsLeft)} secondi." : "Ti rialzi…";
        DrawText(hint, new(w / 2f - Measure(hint, 22) / 2, h / 2f + 8), 22, Theme.Of(Tone.Ink));
    }

    private static readonly (string Keys, string What)[] Controls =
    [
        ("W A S D", "muoviti"), ("Clic sinistro", "colpisci in mischia"), ("Clic destro", "tira a distanza"), ("Maiusc", "corri"), ("Mouse", "guarda intorno"), ("Invio · T", "parla alla compagnia"),
        ("/", "scrivi un comando, es. /tira d20"), ("1 … 9", "lancia un prodigio"), ("Tab", "chi è presente"), ("C", "la scheda del personaggio"), ("Esc", "libera o riprendi il mouse"), ("F1", "chiudi questo aiuto"),
    ];

    private void DrawHelp(int w, int h)
    {
        float panelWidth = 560, panelHeight = 120 + Controls.Length * 30 + 150;
        float x = w / 2f - panelWidth / 2, y = h / 2f - panelHeight / 2;
        Raylib.DrawRectangleRec(new(x, y, panelWidth, panelHeight), Theme.WithAlpha(Theme.Panel, 235));
        Raylib.DrawRectangleLinesEx(new(x, y, panelWidth, panelHeight), 1, Theme.PanelEdge);
        DrawCaps("COMANDI", new(x + 28, y + 24), 24, Theme.Of(Tone.Gilt));
        float row = y + 74;
        foreach (var (keys, what) in Controls)
        {
            DrawCaps(keys, new(x + 28, row + 3), 16, Theme.Of(Tone.Bronze));
            DrawText(what, new(x + 200, row), TextSize, Theme.Of(Tone.Ink));
            row += 30;
        }
        row += 12;
        foreach (var line in "Nella chat valgono gli stessi comandi del terminale:\n/tira, /segreto, /sussurra, /scheda, /imposta, /chi, /esci.\nIl Demiurgo vede tutti i tiri e i sussurri.".Split('\n'))
        {
            DrawText(line, new(x + 28, row), 19, Theme.Of(Tone.Muted));
            row += 25;
        }
    }

    private void DrawOverlay(string message, int w, int h)
    {
        Raylib.DrawRectangle(0, 0, w, h, Theme.WithAlpha(Theme.Night, 170));
        var lines = Wrap(new StyledText().Add(message, Tone.Ink), MathF.Min(700, w - 60), 26);
        float y = h / 2f - lines.Count * 34 / 2f;
        foreach (var line in lines)
        {
            float lw = line.Sum(p => Measure(p.Text, 26));
            DrawLine(line, new(w / 2f - lw / 2, y), 26, 255);
            y += 34;
        }
        const string hint = "Premi Esc per uscire";
        DrawText(hint, new(w / 2f - Measure(hint, 20) / 2, y + 18), 20, Theme.Of(Tone.Muted));
    }

    // ---- Testo

    [GeneratedRegex(@"\S+|\s+")]
    private static partial Regex Words { get; }

    /// <summary>Divide un testo a stili in righe larghe al massimo <paramref name="maxWidth"/> pixel, andando a capo tra le parole.</summary>
    private List<List<Span>> Wrap(StyledText text, float maxWidth, float size)
    {
        List<List<Span>> result = [];
        foreach (var source in text.Lines)
        {
            List<Span> line = [];
            float x = 0;
            foreach (var span in source)
                foreach (Match word in Words.Matches(span.Text))
                {
                    var piece = word.Value;
                    bool space = string.IsNullOrWhiteSpace(piece);
                    float pw = Measure(piece, size);
                    if (!space && x > 0 && x + pw > maxWidth)
                    {
                        result.Add(line);
                        line = [];
                        x = 0;
                    }
                    if (space && x == 0 && result.Count > 0 && line.Count == 0) continue;
                    if (line.Count > 0 && line[^1] is var last && last.Tone == span.Tone && last.Bold == span.Bold && last.Italic == span.Italic)
                        line[^1] = last with { Text = last.Text + piece };
                    else
                        line.Add(span with { Text = piece });
                    x += pw;
                }
            result.Add(line);
        }
        return result;
    }

    private void DrawLine(List<Span> line, Vector2 at, float size, byte alpha)
    {
        foreach (var span in line)
        {
            var color = Theme.Of(span.Tone, alpha);
            var font = _text;
            // Nessuna variante corsiva o grassetta caricata: il grassetto si simula con un secondo passaggio spostato di un pixel.
            Raylib.DrawTextEx(font, span.Text, at + new Vector2(1, 1), size, 0, Theme.WithAlpha(Theme.Night, (byte)(alpha * 0.7)));
            Raylib.DrawTextEx(font, span.Text, at, size, 0, color);
            if (span.Bold) Raylib.DrawTextEx(font, span.Text, at + new Vector2(0.7f, 0), size, 0, color);
            at.X += Measure(span.Text, size);
        }
    }

    private void DrawText(string text, Vector2 at, float size, Color color) => Raylib.DrawTextEx(_text, text, at, size, 0, color);
    private void DrawCaps(string text, Vector2 at, float size, Color color) => Raylib.DrawTextEx(_caps, text, at, size, 1, color);
    private float Measure(string text, float size) => Raylib.MeasureTextEx(_text, text, size, 0).X;

    public void Dispose()
    {
        Raylib.UnloadFont(_text);
        Raylib.UnloadFont(_caps);
    }
}
