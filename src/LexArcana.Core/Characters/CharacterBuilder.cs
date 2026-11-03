namespace LexArcana.Characters;

/// <summary>
/// Crea un personaggio un passo alla volta, controllando le regole a ogni scelta.
/// È la logica del creatore guidato; l'interfaccia (terminale o altro) chiede soltanto.
/// </summary>
public sealed class CharacterBuilder(string player)
{
    private readonly Dictionary<Trait, int> _attributes = Rules.Traits.ToDictionary(a => a, _ => Rules.AttributeMinimum);
    private readonly List<string> _skills = [];

    public string Player { get; } = player;
    public string? RomanName { get; private set; }
    public Vocation? Vocation { get; private set; }
    public Origin? Origin { get; private set; }
    public string Concept { get; set; } = "";

    public int PointsLeft => Rules.AttributePoints - _attributes.Values.Sum(v => v - Rules.AttributeMinimum);
    public int Base(Trait a) => _attributes[a];
    public IReadOnlyList<string> ChosenSkills => _skills;

    public void SetRomanName(string name) => RomanName = name.Trim();
    public void Choose(Vocation vocation) => Vocation = vocation;
    public void Choose(Origin origin) => Origin = origin;

    /// <summary>Imposta un attributo (punti prima dei bonus). Fallisce se esce dai limiti o non bastano i punti.</summary>
    public bool TrySetAttribute(Trait attribute, int value, out string error)
    {
        error = "";
        if (value is < Rules.AttributeMinimum or > Rules.AttributeMaximumBeforeBonuses)
        {
            error = $"Un attributo va da {Rules.AttributeMinimum} a {Rules.AttributeMaximumBeforeBonuses} prima dei bonus.";
            return false;
        }
        int cost = value - _attributes[attribute];
        if (cost > PointsLeft)
        {
            error = $"Restano solo {PointsLeft} punti.";
            return false;
        }
        _attributes[attribute] = value;
        return true;
    }

    /// <summary>Aggiunge un'abilità a scelta. Non può essere già della vocazione, né ripetuta.</summary>
    public bool TryChooseSkill(string name, out string error)
    {
        error = "";
        if (Rules.FindSkill(name) is not { } skill) error = $"Abilità sconosciuta: {name}.";
        else if (_skills.Count >= Rules.ChosenSkills) error = $"Hai già scelto {Rules.ChosenSkills} abilità.";
        else if (_skills.Contains(skill.Name)) error = $"Hai già scelto {skill.Name}.";
        else if (Vocation?.Skills.Contains(skill.Name) == true) error = $"{skill.Name} viene già dalla tua vocazione: scegline un'altra.";
        else _skills.Add(skill.Name);
        return error.Length == 0;
    }

    public void ClearSkills() => _skills.Clear();

    /// <summary>Il personaggio finito, se tutte le scelte sono state fatte e rispettano le regole.</summary>
    public bool TryBuild(out Character character, out IReadOnlyList<string> errors)
    {
        character = null!;
        List<string> missing = [];
        if (Vocation is null) missing.Add("Scegli una vocazione.");
        if (Origin is null) missing.Add("Scegli un'origine.");
        if (string.IsNullOrWhiteSpace(RomanName)) missing.Add("Dai un nome romano al personaggio.");
        if (missing.Count > 0)
        {
            errors = missing;
            return false;
        }

        character = new Character
        {
            Player = Player,
            RomanName = RomanName!,
            Vocation = Vocation!.Name,
            Origin = Origin!.Name,
            BaseAttributes = new(_attributes),
            ChosenSkills = [.. _skills],
            Concept = Concept.Trim(),
        };
        errors = character.Validate();
        return errors.Count == 0;
    }
}
