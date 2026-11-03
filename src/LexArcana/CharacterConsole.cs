using System.Globalization;
using LexArcana.Characters;
using LexArcana.Ui;

namespace LexArcana.App;

/// <summary>Il creatore di personaggi da terminale: guidato passo per passo, oppure casuale.</summary>
internal static class CharacterConsole
{
    public static async Task<int> RunAsync(Options options, Palette p, CancellationToken stop)
    {
        Character character;
        if (options.RandomCharacter)
        {
            character = Character.Random(options.Name!, options.Seed ?? Random.Shared.Next());
        }
        else
        {
            var built = await GuideAsync(options.Name!, p, stop);
            if (built is null) return 1;
            character = built;
        }

        Show(character, p);
        var path = options.CharacterFile ?? CharacterFile.DefaultPath(character);
        try
        {
            await File.WriteAllTextAsync(path, character.ToJson(), stop);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Console.Error.WriteLineAsync(p.Pompeian($"Impossibile salvare {path}: {ex.Message}"));
            return 1;
        }
        Terminal.WriteLine(p.Muted("Salvato in ") + p.Ink(path) + p.Muted(". Portalo in sessione con: ") +
                           p.Ink($"lexarcana-visione --personaggio {path} --host <indirizzo>"));
        return 0;
    }

    private static async Task<Character?> GuideAsync(string player, Palette p, CancellationToken stop)
    {
        var lines = Terminal.ReadLinesAsync(stop).GetAsyncEnumerator(stop);
        async Task<string?> Ask(string question)
        {
            Terminal.WriteLine(p.Bronze("› ") + p.Ink(question));
            return await lines.MoveNextAsync() ? lines.Current.Trim() : null;
        }
        async Task<T?> Choose<T>(string title, IReadOnlyList<T> items, Func<T, string> name, Func<T, string> describe) where T : class
        {
            Terminal.WriteLine("\n" + p.Gilt(p.Bold(title)));
            for (int i = 0; i < items.Count; i++) Terminal.WriteLine($"  {p.Bronze((i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(2))}  {describe(items[i])}");
            while (true)
            {
                var answer = await Ask($"Scegli un numero da 1 a {items.Count} (o scrivi il nome):");
                if (answer is null) return null;
                if (int.TryParse(answer, out int n) && n >= 1 && n <= items.Count) return items[n - 1];
                if (items.FirstOrDefault(i => name(i).Equals(answer, StringComparison.OrdinalIgnoreCase)) is { } named) return named;
                Terminal.WriteLine(p.Pompeian("! Scrivi il numero o il nome di una delle voci."));
            }
        }

        var builder = new CharacterBuilder(player);
        Terminal.WriteLine(p.Gilt(p.Bold("CREAZIONE DEL PERSONAGGIO")) + p.Muted($" · giocatore {player}"));
        Terminal.WriteLine(p.Muted("Le prove si fanno con 1d20 + attributo + abilità contro 10 (facile), 15 (normale) o 20 (difficile)."));

        var vocation = await Choose("Vocazione", Rules.Vocations, v => v.Name,
            v => $"{p.Ink(v.Name.PadRight(12))}{p.Muted($"+1 {v.Bonus} · {string.Join(", ", v.Skills)} · {v.Description}")}");
        if (vocation is null) return null;
        builder.Choose(vocation);

        var origin = await Choose("Origine", Rules.Origins, o => o.Name,
            o => $"{p.Ink(o.Name.PadRight(12))}{p.Muted($"+1 {o.Bonus} · parla {o.Language} · {o.Description}")}");
        if (origin is null) return null;
        builder.Choose(origin);

        Terminal.WriteLine("\n" + p.Gilt(p.Bold("Attributi")) + p.Muted($" · ognuno parte da 1; hai {Rules.AttributePoints} punti, al massimo {Rules.AttributeMaximumBeforeBonuses} prima dei bonus. L'ultimo prende i punti rimasti."));
        while (true)
        {
            foreach (var t in Rules.Traits) builder.TrySetAttribute(t, Rules.AttributeMinimum, out _);
            bool ok = true;
            for (int i = 0; i < Rules.Traits.Count && ok; i++)
            {
                var trait = Rules.Traits[i];
                if (i == Rules.Traits.Count - 1)
                {
                    int value = Rules.AttributeMinimum + builder.PointsLeft;
                    ok = builder.TrySetAttribute(trait, value, out _);
                    Terminal.WriteLine(ok
                        ? p.Muted($"  {trait} ({Rules.Describe(trait)}) prende i punti rimasti: ") + p.Ink(value.ToString(CultureInfo.InvariantCulture))
                        : p.Pompeian($"! Per {trait} resterebbero {value} punti, oltre il massimo di {Rules.AttributeMaximumBeforeBonuses}. Ricominciamo."));
                    break;
                }
                while (true)
                {
                    var answer = await Ask($"{trait} ({Rules.Describe(trait)}): da 1 a {Rules.AttributeMaximumBeforeBonuses}, punti rimasti {builder.PointsLeft}");
                    if (answer is null) return null;
                    if (!int.TryParse(answer, out int value)) { Terminal.WriteLine(p.Pompeian("! Scrivi un numero.")); continue; }
                    if (builder.TrySetAttribute(trait, value, out var error)) break;
                    Terminal.WriteLine(p.Pompeian("! " + error));
                }
            }
            if (ok) break;
        }
        return await FinishAsync();

        async Task<Character?> FinishAsync()
        {
            var free = Rules.Skills.Where(s => !vocation.Skills.Contains(s.Name)).ToList();
            Terminal.WriteLine("\n" + p.Gilt(p.Bold("Abilità")) + p.Muted($" · la vocazione ti dà +{Rules.VocationSkillRank} in {string.Join(", ", vocation.Skills)}; scegline altre {Rules.ChosenSkills} a +{Rules.ChosenSkillRank}."));
            while (builder.ChosenSkills.Count < Rules.ChosenSkills)
            {
                var skill = await Choose($"Abilità {builder.ChosenSkills.Count + 1} di {Rules.ChosenSkills}", free.Where(f => !builder.ChosenSkills.Contains(f.Name)).ToList(), s => s.Name,
                    s => $"{p.Ink(s.Name.PadRight(14))}{p.Muted($"{s.Trait} · {s.Description}")}");
                if (skill is null) return null;
                if (!builder.TryChooseSkill(skill.Name, out var error)) Terminal.WriteLine(p.Pompeian("! " + error));
            }

            var suggestion = RomanNames.Generate(Random.Shared, Random.Shared.Next(2) == 0);
            var name = await Ask($"Nome romano del personaggio (Invio per «{suggestion}»):");
            if (name is null) return null;
            builder.SetRomanName(name.Length == 0 ? suggestion : name);
            builder.Concept = await Ask("Una frase sul personaggio (facoltativa):") ?? "";

            if (builder.TryBuild(out var character, out var errors)) return character;
            foreach (var e in errors) Terminal.WriteLine(p.Pompeian("! " + e));
            return null;
        }
    }

    private static void Show(Character c, Palette p)
    {
        Terminal.WriteLine("\n" + p.Gilt(p.Bold(c.RomanName)) + p.Muted($" · {c.Vocation} da {c.Origin} · giocatore {c.Player}"));
        foreach (var (key, value) in c.ToSheet().Skip(1))
            Terminal.WriteLine("  " + p.Muted(key.PadRight(16)) + p.Ink(value));
    }
}
