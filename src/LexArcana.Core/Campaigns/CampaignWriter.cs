using System.Text;

namespace LexArcana.Campaigns;

/// <summary>Il manuale del Demiurgo: la campagna in Markdown, da leggere o stampare prima della sessione.</summary>
public static class CampaignWriter
{
    public static string ToMarkdown(Campaign c)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {c.Title}").AppendLine();
        sb.AppendLine($"*Campagna generata con il seme {c.Seed} · {c.Adventures.Length} avventure · {c.City}, {c.Province}*").AppendLine();
        sb.AppendLine("## Premessa").AppendLine().AppendLine(c.Premise).AppendLine();
        sb.AppendLine($"- **Minaccia:** {c.Threat}");
        sb.AppendLine($"- **Antagonista:** {c.Villain}");
        sb.AppendLine($"- **Committente:** {c.Patron}").AppendLine();

        foreach (var a in c.Adventures)
        {
            sb.AppendLine($"## {a.Number}. {a.Title}").AppendLine();
            sb.AppendLine($"**Luogo:** {a.Place} ({a.Kind.ToString().ToLowerInvariant()}) · mappa: `avventura-{a.Number}.txt`").AppendLine();
            sb.AppendLine($"**Gancio.** {a.Hook}").AppendLine();
            sb.AppendLine($"**Obiettivo.** {a.Objective}").AppendLine();
            sb.AppendLine("**Scene possibili**").AppendLine();
            foreach (var scene in a.Scenes) sb.AppendLine($"- {Capitalize(scene)}.");
            sb.AppendLine().AppendLine("**Personaggi non giocanti**").AppendLine();
            sb.AppendLine("| Nome | Ruolo | Come si riconosce | Segreto |").AppendLine("| --- | --- | --- | --- |");
            foreach (var n in a.Npcs) sb.AppendLine($"| {n.Name} | {n.Role} | {n.Trait} | {n.Secret} |");
            sb.AppendLine().AppendLine($"**Complicazione.** {Capitalize(a.Complication)}.").AppendLine();
            sb.AppendLine($"**Climax.** {a.Climax}").AppendLine();
            sb.AppendLine($"**Ricompensa.** {Capitalize(a.Reward)}.").AppendLine();
            sb.AppendLine("```text");
            foreach (var row in a.Map) sb.AppendLine(row);
            sb.AppendLine("```").AppendLine();
        }

        sb.AppendLine("---").AppendLine();
        sb.AppendLine("Legenda delle mappe: `#` muro, `.` pavimento, `C` colonna, `A` altare (il cuore del luogo), `S` partenza del gruppo. Ogni casella misura 2 metri.");
        return sb.ToString();
    }

    /// <summary>Il file di mappa di un'avventura, nel formato che il server carica con --mappa.</summary>
    public static string ToMapFile(Adventure a) => $"nome: {a.Place}\n" + string.Join('\n', a.Map) + "\n";

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
