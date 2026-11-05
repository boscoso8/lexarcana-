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
            case MapChanged(var map, _, var title):
                t.Add("» Il gruppo raggiunge un nuovo luogo: ", Tone.Verdigris).Add(map.Name.Sanitized, Tone.Gilt, bold: true);
                if (title is { Length: > 0 } && !title.Sanitized.EndsWith(map.Name.Sanitized, StringComparison.Ordinal))
                    t.Add($" · {title.Sanitized}", Tone.Muted);
                break;
            case Struck s:
                Blow(t, s);
                break;
            case SpellCast c:
                Spell(t, c);
                break;
            case EnemyList(var enemies) when enemies.Length > 0:
                t.Add($"† {enemies.Length} nemici nel luogo", Tone.Pompeian).Add(
                    $" · livello {enemies.Min(e => e.Level)}–{enemies.Max(e => e.Level)}" +
                    (enemies.FirstOrDefault(e => e.Rank == "Boss") is { } boss ? $" · li guida {boss.Name}" : ""), Tone.Muted);
                break;
            default:
                return null; // Moved, EnemyMoves, Vitals: si vedono nella scena 3D, non nel testo: si vedono nella scena 3D, non nel testo
        }
        return t;
    }

    private bool IsViewer(string name) => name.IsSameName(viewer);

    private void Spell(StyledText t, SpellCast c)
    {
        string who = IsViewer(c.Caster) ? "Tu" : c.Caster.Sanitized;
        var tone = c.Tradition switch { "Miracoli" => Tone.Gilt, "Invocazioni" => Tone.Verdigris, _ => Tone.Bronze };
        t.Add("❦ ", tone).Add(who, Tone.Bronze, bold: true)
         .Add(IsViewer(c.Caster) ? " invochi " : " invoca ", Tone.Muted).Add(c.Name.Sanitized, tone, bold: true);
        if (c.Target is { Length: > 0 } target && c.Spell is "mano" or "catene" or "egida")
            t.Add($" su {(IsViewer(target) ? "di te" : target.Sanitized)}", Tone.Muted);
        t.Add($"  [{c.Roll}] {c.Total} contro {c.Against}", Tone.Muted);
        if (!c.Success) { t.Add(": il prodigio non risponde.", Tone.Muted, italic: true); return; }
        string effect = c.Spell switch
        {
            "mano" => $": +{c.Amount} salute",
            "catene" => $": fermo per {c.Duration:0} secondi",
            "egida" => $": +3 alla Difesa per {c.Duration:0} secondi",
            "occhio" => $": vedi {c.Amount} nemici per {c.Duration:0} secondi",
            "pan" => c.Amount == 0 ? ": nessun nemico abbastanza vicino" : $": {c.Amount} nemici fuggono",
            _ => "",
        };
        if (effect.Length > 0) t.Add(effect, tone);
    }

    private void Blow(StyledText t, Struck s)
    {
        string who = IsViewer(s.Attacker) ? "Tu" : s.Attacker.Sanitized;
        string whom = IsViewer(s.Target) ? "te" : s.Target.Sanitized;
        var attackerTone = s.AttackerEnemyId is null ? Tone.Bronze : Tone.Pompeian;
        t.Add("† ", Tone.Pompeian).Add(who, attackerTone, bold: true);
        if (!s.Hit)
        {
            t.Add($" {(IsViewer(s.Attacker) ? "manchi" : "manca")} {whom}", Tone.Muted).Add($"  [{s.Roll}] {s.Total} contro {s.Against}", Tone.Muted);
            return;
        }
        string verb = s.Ranged ? (IsViewer(s.Attacker) ? "trafiggi" : "trafigge") : (IsViewer(s.Attacker) ? "colpisci" : "colpisce");
        t.Add($" {verb} {whom}", Tone.Ink);
        if (s.Critical) t.Add(" con un colpo critico", Tone.Gilt, bold: true);
        t.Add($"  [{s.Roll}] {s.Total} contro {s.Against}: ", Tone.Muted).Add($"{s.Damage} danni", Tone.Pompeian, bold: true)
         .Add($" ({s.Health}/{s.MaxHealth})", Tone.Muted);
        if (s.Defeated)
            t.Add(s.TargetEnemyId is null ? (IsViewer(s.Target) ? " · sei a terra!" : $" · {whom} è a terra!") : $" · {whom} è sconfitto.", Tone.Gilt, bold: true);
    }

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
