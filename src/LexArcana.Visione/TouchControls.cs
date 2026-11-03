using System.Numerics;
using Raylib_cs;
using LexArcana.Ui;

namespace LexArcana.Visione;

/// <summary>
/// I comandi per schermi touch (iPhone, iPad, telefoni): un joystick a sinistra per camminare,
/// trascinare a destra per guardare, pulsanti per colpire, tirare, parlare e aprire la scheda.
/// Funziona con le dita e, per provarlo da computer, anche con il mouse.
/// </summary>
internal sealed class TouchControls(Func<double[]>? source = null)
{
    public enum Button { Melee, Ranged, Talk, Sheet }

    private readonly Dictionary<int, (Vector2 Start, Vector2 Last, string Role)> _touches = [];
    private Vector2 _joyOrigin, _joyKnob;
    private bool _joyActive;

    public Vector2 Move { get; private set; }          // x: di lato (+ destra), y: avanti
    public Vector2 Look { get; private set; }          // pixel trascinati in questo fotogramma
    public HashSet<Button> Pressed { get; } = [];

    private static float Unit => MathF.Max(48f, MathF.Min(Raylib.GetScreenWidth(), Raylib.GetScreenHeight()) * 0.11f);

    public static IEnumerable<(Button Button, Rectangle Rect, string Label)> Buttons()
    {
        int w = Raylib.GetScreenWidth(), h = Raylib.GetScreenHeight();
        float u = Unit, gap = u * 0.25f;
        yield return (Button.Melee, new Rectangle(w - u * 1.6f - gap, h - u * 1.6f - gap, u * 1.6f, u * 1.6f), "COLPISCI");
        yield return (Button.Ranged, new Rectangle(w - u * 3.0f - gap * 2, h - u * 1.15f - gap, u * 1.3f, u * 1.15f), "TIRA");
        yield return (Button.Talk, new Rectangle(w - u * 1.6f - gap, h - u * 2.6f - gap * 2, u * 1.6f, u * 0.8f), "PARLA");
        yield return (Button.Sheet, new Rectangle(w - u * 3.0f - gap * 2, h - u * 2.6f - gap * 2, u * 1.3f, u * 0.8f), "SCHEDA");
    }

    public void Update()
    {
        Pressed.Clear();
        Look = Vector2.Zero;
        int w = Raylib.GetScreenWidth();

        // Le dita presenti in questo fotogramma (con il mouse come dito numero -1, per le prove da computer).
        // Nel browser le dita le legge la pagina (source: id, x, y, id, x, y…): è più affidabile della gestione
        // del touch di raylib per il web, che a volte non registra il sollevamento di un dito.
        var now = new Dictionary<int, Vector2>();
        int count;
        if (source is not null)
        {
            var flat = source();
            for (int i = 0; i + 2 < flat.Length; i += 3) now[(int)flat[i]] = new Vector2((float)flat[i + 1], (float)flat[i + 2]);
            count = now.Count;
        }
        else
        {
            count = Raylib.GetTouchPointCount();
            for (int i = 0; i < count; i++) now[Raylib.GetTouchPointId(i)] = Raylib.GetTouchPosition(i);
        }
        if (count == 0 && Raylib.IsMouseButtonDown(MouseButton.Left)) now[-1] = Raylib.GetMousePosition();

        foreach (var (id, pos) in now)
        {
            if (!_touches.TryGetValue(id, out var t))
            {
                // un dito nuovo: un pulsante, il joystick (metà sinistra) o lo sguardo (il resto)
                string role = "look";
                foreach (var (button, rect, _) in Buttons())
                    if (Raylib.CheckCollisionPointRec(pos, rect)) { role = "button"; Pressed.Add(button); break; }
                if (role == "look" && pos.X < w * 0.4f && !_joyActive)
                {
                    role = "joy";
                    _joyActive = true;
                    _joyOrigin = pos;
                }
                _touches[id] = (pos, pos, role);
                continue;
            }
            if (t.Role == "look") Look += pos - t.Last;
            if (t.Role == "joy") _joyKnob = pos;
            _touches[id] = t with { Last = pos };
        }
        foreach (var gone in _touches.Keys.Where(id => !now.ContainsKey(id)).ToList())
        {
            if (_touches[gone].Role == "joy") _joyActive = false;
            _touches.Remove(gone);
        }

        if (_joyActive)
        {
            var delta = (_joyKnob == Vector2.Zero ? _joyOrigin : _joyKnob) - _joyOrigin;
            float radius = Unit * 1.1f;
            if (delta.Length() > radius) delta = Vector2.Normalize(delta) * radius;
            _joyKnob = _joyOrigin + delta;
            var v = delta / radius;
            Move = new Vector2(v.X, -v.Y);
        }
        else
        {
            Move = Vector2.Zero;
            _joyKnob = Vector2.Zero;
        }
    }

    public void Draw()
    {
        float u = Unit;
        if (_joyActive)
        {
            Raylib.DrawCircleV(_joyOrigin, u * 1.1f, Theme.WithAlpha(Theme.Night, 110));
            Raylib.DrawCircleLinesV(_joyOrigin, u * 1.1f, Theme.Of(Tone.Gilt, 160));
            Raylib.DrawCircleV(_joyKnob, u * 0.45f, Theme.Of(Tone.Gilt, 170));
        }
        else
        {
            int h = Raylib.GetScreenHeight();
            var hint = new Vector2(u * 1.7f, h - u * 1.7f);
            Raylib.DrawCircleLinesV(hint, u * 1.1f, Theme.Of(Tone.Muted, 90));
            Raylib.DrawCircleV(hint, u * 0.45f, Theme.Of(Tone.Muted, 60));
        }
    }

    /// <summary>I pulsanti, disegnati dall'HUD con i suoi font.</summary>
    public static void DrawButtons(Hud hud)
    {
        foreach (var (button, rect, label) in Buttons())
        {
            bool primary = button == Button.Melee;
            Raylib.DrawRectangleRounded(rect, 0.3f, 8, primary ? Theme.WithAlpha(Theme.Fresco, 200) : Theme.WithAlpha(Theme.Night, 170));
            Raylib.DrawRectangleRoundedLinesEx(rect, 0.3f, 8, 1.5f, Theme.Of(Tone.Gilt, 200));
            hud.DrawCentered(label, rect, primary ? Tone.Ink : Tone.Gilt);
        }
    }
}
