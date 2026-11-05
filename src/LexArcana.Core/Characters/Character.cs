using System.Text.Json;
using System.Text.Json.Serialization;
using LexArcana.Game;

namespace LexArcana.Characters;

/// <summary>
/// Un personaggio giocante. Si crea con <see cref="CharacterBuilder"/> (passo per passo) o con
/// <see cref="Character.Random"/>, si salva come JSON e si porta in ogni sessione.
/// </summary>
public sealed record Character
{
    /// <summary>Il nome con cui il giocatore entra in sessione (lettere e cifre, senza spazi).</summary>
    public required string Player { get; init; }
    /// <summary>Il nome romano del personaggio, es. "Aurelia Severa".</summary>
    public required string RomanName { get; init; }
    public required string Vocation { get; init; }
    public required string Origin { get; init; }
    /// <summary>I punti spesi su ogni attributo, prima dei bonus di vocazione e origine.</summary>
    public required Dictionary<Trait, int> BaseAttributes { get; init; }
    /// <summary>Le due abilità scelte oltre a quelle della vocazione.</summary>
    public required string[] ChosenSkills { get; init; }
    public string Concept { get; init; } = "";
    /// <summary>L'esperienza accumulata giocando; il livello si ricava da qui (vedi <see cref="Combat.Progression"/>).</summary>
    public int Experience { get; init; }

    [JsonIgnore]
    public int Level => Combat.Progression.LevelFor(Experience);

    [JsonIgnore]
    public Vocation VocationInfo => Rules.FindVocation(Vocation) ?? throw new InvalidOperationException($"Vocazione sconosciuta: {Vocation}");
    [JsonIgnore]
    public Origin OriginInfo => Rules.FindOrigin(Origin) ?? throw new InvalidOperationException($"Origine sconosciuta: {Origin}");

    /// <summary>Il valore finale di un attributo: punti + bonus della vocazione + bonus dell'origine.</summary>
    public int this[Trait attribute] =>
        BaseAttributes.GetValueOrDefault(attribute, Rules.AttributeMinimum)
        + (VocationInfo.Bonus == attribute ? 1 : 0)
        + (OriginInfo.Bonus == attribute ? 1 : 0);

    /// <summary>Il grado di un'abilità: 2 se viene dalla vocazione, 1 se scelta, 0 altrimenti (si sommano se coincidono).</summary>
    public int SkillRank(string skill) =>
        (VocationInfo.Skills.Contains(skill, StringComparer.OrdinalIgnoreCase) ? Rules.VocationSkillRank : 0)
        + (ChosenSkills.Contains(skill, StringComparer.OrdinalIgnoreCase) ? Rules.ChosenSkillRank : 0);

    /// <summary>Il bonus totale per una prova: attributo dell'abilità + grado.</summary>
    public int CheckBonus(string skill) =>
        Rules.FindSkill(skill) is { } s ? this[s.Trait] + SkillRank(s.Name) : throw new ArgumentException($"Abilità sconosciuta: {skill}", nameof(skill));

    [JsonIgnore]
    public int Health => 8 + 2 * this[Trait.Robur];
    [JsonIgnore]
    public int Resolve => 4 + this[Trait.Pietas] + this[Trait.Ingenium];
    [JsonIgnore]
    public int Defense => 10 + this[Trait.Celeritas];

    /// <summary>Controlla che il personaggio rispetti le regole; restituisce gli errori, vuota se è valido.</summary>
    public IReadOnlyList<string> Validate()
    {
        List<string> errors = [];
        if (!Names.IsValid(Player)) errors.Add($"Il nome del giocatore deve avere 1–{Names.MaxLength} lettere o cifre, senza spazi.");
        if (Names.IsDemiurge(Player)) errors.Add($"«{Names.Demiurge}» è riservato a chi conduce la partita.");
        if (RomanName.Sanitized.Trim().Length is 0 or > 60) errors.Add("Il nome romano deve avere da 1 a 60 caratteri.");
        if (Rules.FindVocation(Vocation) is null) errors.Add($"Vocazione sconosciuta: {Vocation}.");
        if (Rules.FindOrigin(Origin) is null) errors.Add($"Origine sconosciuta: {Origin}.");
        if (Concept.Length > 160) errors.Add("Il concetto può avere al massimo 160 caratteri.");
        if (Experience is < 0 or > 1_000_000) errors.Add("L'esperienza deve stare tra 0 e 1.000.000.");

        foreach (var a in Rules.Traits)
        {
            int value = BaseAttributes.GetValueOrDefault(a, 0);
            if (value is < Rules.AttributeMinimum or > Rules.AttributeMaximumBeforeBonuses)
                errors.Add($"{a} deve stare tra {Rules.AttributeMinimum} e {Rules.AttributeMaximumBeforeBonuses} prima dei bonus.");
        }
        int spent = Rules.Traits.Sum(a => BaseAttributes.GetValueOrDefault(a, 0) - Rules.AttributeMinimum);
        if (spent != Rules.AttributePoints) errors.Add($"Vanno spesi esattamente {Rules.AttributePoints} punti negli attributi (spesi: {spent}).");

        if (ChosenSkills.Length != Rules.ChosenSkills) errors.Add($"Scegli {Rules.ChosenSkills} abilità oltre a quelle della vocazione.");
        if (ChosenSkills.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ChosenSkills.Length) errors.Add("Le abilità scelte devono essere diverse.");
        foreach (var skill in ChosenSkills.Where(s => Rules.FindSkill(s) is null)) errors.Add($"Abilità sconosciuta: {skill}.");
        return errors;
    }

    /// <summary>I campi della scheda condivisa in sessione, in ordine di lettura (ognuno sta nei limiti di <see cref="CharacterSheet"/>).</summary>
    public IEnumerable<(string Key, string Value)> ToSheet()
    {
        yield return ("Nome", RomanName);
        yield return ("Vocazione", Vocation);
        yield return ("Livello", Level.ToString(System.Globalization.CultureInfo.InvariantCulture));
        yield return ("Origine", $"{Origin} ({OriginInfo.Language})");
        foreach (var a in Rules.Traits) yield return (a.ToString(), this[a].ToString(System.Globalization.CultureInfo.InvariantCulture));
        yield return ("Salute", $"{Health}/{Health}");
        yield return ("Volontà", $"{Resolve}/{Resolve}");
        yield return ("Difesa", Defense.ToString(System.Globalization.CultureInfo.InvariantCulture));
        yield return ("Abilità", string.Join(", ", Rules.Skills.Where(s => SkillRank(s.Name) > 0).Select(s => $"{s.Name} +{CheckBonus(s.Name)}")));
        yield return ("Equipaggiamento", string.Join(", ", VocationInfo.Equipment));
        var spells = Combat.Grimoire.All.Where(s => SkillRank(Combat.Grimoire.Skill(s.Tradition)) > 0).ToList();
        if (spells.Count > 0)
        {
            var known = spells.Where(s => Level >= Combat.Grimoire.LevelFor(s.Tier)).Select(s => s.Name).ToList();
            int later = spells.Count - known.Count;
            yield return ("Magia", string.Join(", ", known) + (later > 0 ? $" · altri {later} salendo di livello" : ""));
        }
        if (Concept.Length > 0) yield return ("Concetto", Concept);
    }

    /// <summary>Un personaggio casuale ma valido. Con lo stesso seme esce sempre lo stesso.</summary>
    public static Character Random(string player, int seed) => Random(player, seed, null);

    /// <summary>Come <see cref="Random(string, int)"/>, ma con la vocazione scelta (se esiste).</summary>
    public static Character Random(string player, int seed, string? vocationName)
    {
        var random = new Random(seed);
        var vocation = Rules.Vocations[random.Next(Rules.Vocations.Count)];
        if (vocationName is not null && Rules.FindVocation(vocationName) is { } chosen) vocation = chosen;
        var origin = Rules.Origins[random.Next(Rules.Origins.Count)];

        // Più punti dove la vocazione ne ha bisogno, il resto a caso.
        var attributes = Rules.Traits.ToDictionary(a => a, _ => Rules.AttributeMinimum);
        var weights = Rules.Traits.ToDictionary(a => a, a =>
            1 + (a == vocation.Bonus ? 3 : 0) + vocation.Skills.Count(s => Rules.FindSkill(s)!.Trait == a));
        for (int spent = 0; spent < Rules.AttributePoints;)
        {
            int roll = random.Next(weights.Values.Sum());
            var pick = weights.First(w => (roll -= w.Value) < 0).Key;
            if (attributes[pick] >= Rules.AttributeMaximumBeforeBonuses) continue;
            attributes[pick]++;
            spent++;
        }

        var others = Rules.Skills.Where(s => !vocation.Skills.Contains(s.Name)).OrderBy(_ => random.Next()).Take(Rules.ChosenSkills);
        bool female = random.Next(2) == 0;
        return new Character
        {
            Player = player,
            RomanName = RomanNames.Generate(random, female),
            Vocation = vocation.Name,
            Origin = origin.Name,
            BaseAttributes = attributes,
            ChosenSkills = [.. others.Select(s => s.Name)],
            Concept = vocation.Description,
        };
    }

    // ---- File

    public string ToJson() => JsonSerializer.Serialize(this, CharacterJson.Readable.Character);

    public static Character FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, CharacterJson.Default.Character) ?? throw new FormatException("File vuoto.");
        }
        catch (JsonException ex)
        {
            throw new FormatException("Il file del personaggio non è valido: " + ex.Message, ex);
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UseStringEnumConverter = true, RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(Character))]
internal sealed partial class CharacterJson : JsonSerializerContext
{
    /// <summary>Come Default, ma scrive le lettere accentate così come sono: i file restano leggibili e modificabili a mano.</summary>
    public static CharacterJson Readable => field ??= new(new JsonSerializerOptions(Default.Options) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
