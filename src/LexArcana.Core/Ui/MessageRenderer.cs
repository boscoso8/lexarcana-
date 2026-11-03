using System.Globalization;
using System.Text;
using LexArcana.Game;
using LexArcana.Protocol;

namespace LexArcana.Ui;

/// <summary>Trasforma un messaggio del server in testo per il terminale, dal punto di vista di <c>viewer</c>.</summary>
public sealed class MessageRenderer(Palette p, string viewer)
{
    public string Render(ServerMessage message) => message switch
    {
        Welcome(var name, var players, _) =>
            $"{p.Gilt(p.Bold("LEX ARCANA"))}{p.Muted($" · Benvenuto, {name.Sanitized}. ")}{Present(players)}\n{p.Muted("Scrivi /aiuto per i comandi.")}",
        Joined(var name) => p.Verdigris($"» {name.Sanitized} entra nella sessione."),
        Left(var name) => p.Muted($"« {name.Sanitized} lascia la sessione."),
        Said(var from, var text) => $"{Speaker(from)} {p.Ink(text.Sanitized)}",
        Narrated(var text) => $"\n{p.Gilt("❦ ")}{p.Italic(p.Ink(text.Sanitized))}\n",
        Rolled roll => Roll(roll),
        Whispered(var from, var to, var text) => p.Muted(p.Italic(WhisperLead(from, to))) + p.Ink(text.Sanitized),
        SheetShown sheet => Sheet(sheet),
        PlayerList(var names) => Present(names),
        Refused(var reason) => p.Pompeian("! " + reason.Sanitized),
        Expelled(var reason) => p.Pompeian(reason.Sanitized is { Length: > 0 } r ? r : "Il Demiurgo ti ha allontanato dalla sessione."),
        _ => p.Muted($"[{message.GetType().Name}]"),
    };

    private string Speaker(string from) =>
        Names.IsDemiurge(from) ? p.Gilt(p.Bold(from + ":")) : p.Bronze(p.Bold(from.Sanitized + ":"));

    private bool IsViewer(string name) => name.IsSameName(viewer);

    private string WhisperLead(string from, string to) =>
        IsViewer(from) ? $"Sussurri a {to.Sanitized}: " :
        IsViewer(to) ? $"{from.Sanitized} ti sussurra: " :
        $"{from.Sanitized} sussurra a {to.Sanitized}: ";

    private string Roll(Rolled r)
    {
        var dice = string.Join(" ", r.Dice.Select(d => d < 0 ? $"−{-d}" : d.ToString(CultureInfo.InvariantCulture)));
        var modifier = r.Modifier switch { > 0 => $" +{r.Modifier}", < 0 => $" −{-r.Modifier}", _ => "" };
        var secret = r.Secret ? p.Muted(p.Italic(" (segreto)")) : "";
        var who = IsViewer(r.From) ? p.Bold(p.Ink("Tiri")) : Speaker(r.From).Replace(":", "", StringComparison.Ordinal) + p.Muted(" tira");
        return $"{p.Bronze("⚄ ")}{who}{p.Muted($" {r.Expression.Sanitized}")}{secret}{p.Muted($"  [{dice}]{modifier} = ")}{p.Pompeian(p.Bold(r.Total.ToString(CultureInfo.InvariantCulture)))}";
    }

    private string Sheet(SheetShown s)
    {
        var sb = new StringBuilder(p.Gilt(p.Bold($"Scheda di {s.Owner.Sanitized}")));
        if (s.UpdatedBy is { } by && !by.IsSameName(s.Owner) && !by.IsSameName(viewer))
            sb.Append(p.Muted($" · aggiornata da {by.Sanitized}"));
        if (s.Fields.Count == 0)
            return sb.Append('\n').Append(p.Muted(p.Italic("  ancora vuota: usa /imposta <campo> <valore>"))).ToString();

        int width = s.Fields.Keys.Max(k => k.Sanitized.Length);
        foreach (var (key, value) in s.Fields)
            sb.Append("\n  ").Append(p.Muted(key.Sanitized.PadRight(width))).Append("  ").Append(p.Ink(value.Sanitized));
        return sb.ToString();
    }

    private string Present(string[] names) =>
        p.Muted("Presenti: ") + string.Join(p.Muted(", "), names.Select(n => Names.IsDemiurge(n) ? p.Gilt(n) : p.Bronze(n.Sanitized)));
}
