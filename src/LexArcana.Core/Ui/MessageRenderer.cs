using System.Globalization;
using LexArcana.Game;
using LexArcana.Protocol;

namespace LexArcana.Ui;

/// <summary>
/// Trasforma un messaggio del server in testo da mostrare, dal punto di vista di <c>viewer</c>.
/// Restituisce null per i messaggi che non si leggono, come gli spostamenti.
/// </summary>
public sealed class MessageRenderer(string viewer)
{
    public StyledText? Render(ServerMessage message)
    {
        var t = new StyledText();
        switch (message)
        {
            case Welcome(var name, var players, var map, _, _):
                t.Add("LEX ARCANA", Tone.Gilt, bold: true).Add($" · {map.Name.Sanitized} · Benvenuto, {name.Sanitized}. ", Tone.Muted);
                Present(t, players).NewLine().Add("Scrivi /aiuto per i comandi.", Tone.Muted);
                break;
            case Joined(var name):
                t.Add($"» {name.Sanitized} entra nella sessione.", Tone.Verdigris);
                break;
            case Left(var name):
                t.Add($"« {name.Sanitized} lascia la sessione.", Tone.Muted);
                break;
            case Said(var from, var text):
                Speaker(t, from).Add(" " + text.Sanitized);
                break;
            case Narrated(var text):
                t.Add("❦ ", Tone.Gilt).Add(text.Sanitized, italic: true);
                break;
            case Rolled roll:
                Roll(t, roll);
                break;
            case Whispered(var from, var to, var text):
                t.Add(WhisperLead(from, to), Tone.Muted, italic: true).Add(text.Sanitized);
                break;
            case SheetShown sheet:
                Sheet(t, sheet);
                break;
            case PlayerList(var names):
                Present(t, names);
                break;
            case Refused(var reason):
                t.Add("! " + reason.Sanitized, Tone.Pompeian);
                break;
            case Expelled(var reason):
                t.Add(reason.Sanitized is { Length: > 0 } r ? r : "Il Demiurgo ti ha allontanato dalla sessione.", Tone.Pompeian);
                break;
            default:
                return null; // Moved e simili: si vedono nella scena 3D, non nel testo
        }
        return t;
    }

    private bool IsViewer(string name) => name.IsSameName(viewer);

    private static StyledText Speaker(StyledText t, string from) =>
        Names.IsDemiurge(from) ? t.Add(from + ":", Tone.Gilt, bold: true) : t.Add(from.Sanitized + ":", Tone.Bronze, bold: true);

    private string WhisperLead(string from, string to) =>
        IsViewer(from) ? $"Sussurri a {to.Sanitized}: " :
        IsViewer(to) ? $"{from.Sanitized} ti sussurra: " :
        $"{from.Sanitized} sussurra a {to.Sanitized}: ";

    private void Roll(StyledText t, Rolled r)
    {
        t.Add("◆ ", Tone.Bronze);
        if (IsViewer(r.From)) t.Add("Tiri", bold: true);
        else t.Add(r.From.Sanitized, Names.IsDemiurge(r.From) ? Tone.Gilt : Tone.Bronze, bold: true).Add(" tira", Tone.Muted);
        t.Add(" " + r.Expression.Sanitized, Tone.Muted);
        if (r.Secret) t.Add(" (segreto)", Tone.Muted, italic: true);
        var dice = string.Join(" ", r.Dice.Select(d => d < 0 ? $"−{-d}" : d.ToString(CultureInfo.InvariantCulture)));
        var modifier = r.Modifier switch { > 0 => $" +{r.Modifier}", < 0 => $" −{-r.Modifier}", _ => "" };
        t.Add($"  [{dice}]{modifier} = ", Tone.Muted).Add(r.Total.ToString(CultureInfo.InvariantCulture), Tone.Pompeian, bold: true);
    }

    private void Sheet(StyledText t, SheetShown s)
    {
        t.Add($"Scheda di {s.Owner.Sanitized}", Tone.Gilt, bold: true);
        if (s.UpdatedBy is { } by && !by.IsSameName(s.Owner) && !IsViewer(by))
            t.Add($" · aggiornata da {by.Sanitized}", Tone.Muted);
        if (s.Fields.Count == 0)
        {
            t.NewLine().Add("  ancora vuota: usa /imposta <campo> <valore>", Tone.Muted, italic: true);
            return;
        }
        int width = s.Fields.Keys.Max(k => k.Sanitized.Length);
        foreach (var (key, value) in s.Fields)
            t.NewLine().Add("  " + key.Sanitized.PadRight(width) + "  ", Tone.Muted).Add(value.Sanitized);
    }

    private static StyledText Present(StyledText t, string[] names)
    {
        t.Add("Presenti: ", Tone.Muted);
        for (int i = 0; i < names.Length; i++)
        {
            if (i > 0) t.Add(", ", Tone.Muted);
            t.Add(names[i].Sanitized, Names.IsDemiurge(names[i]) ? Tone.Gilt : Tone.Bronze);
        }
        return t;
    }
}
