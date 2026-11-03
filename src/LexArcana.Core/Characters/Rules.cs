namespace LexArcana.Characters;

// Un sistema di regole originale e leggero.
// Prova: 1d20 + attributo + abilità contro una difficoltà (10 facile, 15 normale, 20 difficile).

/// <summary>I cinque attributi di un personaggio.</summary>
public enum Trait
{
    /// <summary>Forza, resistenza, combattimento corpo a corpo.</summary>
    Robur,
    /// <summary>Agilità, riflessi, mira.</summary>
    Celeritas,
    /// <summary>Intelligenza, memoria, ragionamento.</summary>
    Ingenium,
    /// <summary>Parola, presenza, capacità di convincere.</summary>
    Facundia,
    /// <summary>Devozione, forza d'animo, legame con il sacro.</summary>
    Pietas,
}

/// <summary>Un'abilità addestrabile, legata a un attributo.</summary>
public sealed record Skill(string Name, Trait Trait, string Description);

/// <summary>Una vocazione: il mestiere del personaggio prima che il mistero lo trovasse.</summary>
public sealed record Vocation(string Name, Trait Bonus, string[] Skills, string[] Equipment, string Description);

/// <summary>La provincia d'origine.</summary>
public sealed record Origin(string Name, Trait Bonus, string Language, string Description);

public static class Rules
{
    public const int AttributeMinimum = 1;
    public const int AttributeMaximumBeforeBonuses = 5;
    public const int AttributeMaximum = 7;
    /// <summary>Punti da distribuire oltre al minimo di 1 in ogni attributo.</summary>
    public const int AttributePoints = 10;
    public const int VocationSkillRank = 2;
    public const int ChosenSkillRank = 1;
    public const int ChosenSkills = 2;

    public static IReadOnlyList<Trait> Traits { get; } = Enum.GetValues<Trait>();

    public static string Describe(Trait a) => a switch
    {
        Trait.Robur => "forza e resistenza",
        Trait.Celeritas => "agilità e mira",
        Trait.Ingenium => "intelletto e memoria",
        Trait.Facundia => "parola e presenza",
        Trait.Pietas => "devozione e forza d'animo",
        _ => "",
    };

    public static IReadOnlyList<Skill> Skills { get; } =
    [
        new("Armi", Trait.Robur, "combattere con gladio, lancia, scudo"),
        new("Atletica", Trait.Robur, "correre, arrampicarsi, nuotare, sollevare"),
        new("Furtività", Trait.Celeritas, "muoversi senza farsi notare, scassinare"),
        new("Tiro", Trait.Celeritas, "arco, fionda, giavellotto"),
        new("Cavalcare", Trait.Celeritas, "cavalli, carri, fughe"),
        new("Medicina", Trait.Ingenium, "ferite, veleni, malattie"),
        new("Erudizione", Trait.Ingenium, "storia, leggi, lingue antiche"),
        new("Indagine", Trait.Ingenium, "indizi, documenti, menzogne"),
        new("Persuasione", Trait.Facundia, "convincere, sedurre, intimidire"),
        new("Comando", Trait.Facundia, "guidare uomini, imporsi"),
        new("Commercio", Trait.Facundia, "trattare, valutare, corrompere"),
        new("Riti", Trait.Pietas, "preghiere, sacrifici, protezioni"),
        new("Presagi", Trait.Pietas, "leggere segni, sogni, voli di uccelli"),
        new("Sopravvivenza", Trait.Pietas, "orientarsi, cacciare, resistere alla fame"),
    ];

    public static IReadOnlyList<Vocation> Vocations { get; } =
    [
        new("Legionario", Trait.Robur, ["Armi", "Atletica", "Comando"],
            ["gladio", "scudo", "lorica", "pilum", "razioni per tre giorni"],
            "Ha marciato ai confini dell'impero e ha visto cose che i rapporti non raccontano."),
        new("Augure", Trait.Pietas, ["Presagi", "Riti", "Erudizione"],
            ["lituo", "tavolette cerate", "incenso", "amuleto di bronzo"],
            "Legge la volontà degli dei nel volo degli uccelli, e a volte gli dei rispondono."),
        new("Medico", Trait.Ingenium, ["Medicina", "Erudizione", "Persuasione"],
            ["borsa di strumenti", "erbe e unguenti", "rotolo di Ippocrate"],
            "Ha curato senatori e schiavi, e sa che i corpi raccontano segreti."),
        new("Esploratore", Trait.Celeritas, ["Furtività", "Tiro", "Sopravvivenza"],
            ["arco e frecce", "mantello scuro", "corda", "acciarino"],
            "Va avanti alle colonne, dove le mappe finiscono."),
        new("Mercante", Trait.Facundia, ["Commercio", "Persuasione", "Indagine"],
            ["borsa di denari", "sigillo di famiglia", "bilancia", "lettere di credito"],
            "Conosce i porti, i prezzi e chi si può comprare."),
        new("Gladiatore", Trait.Robur, ["Armi", "Atletica", "Persuasione"],
            ["tridente o spada corta", "rete", "cicatrici"],
            "Ha conquistato la libertà nell'arena; il pubblico ancora ne grida il nome."),
        new("Erudito", Trait.Ingenium, ["Erudizione", "Indagine", "Riti"],
            ["codici e papiri", "stilo", "lanterna", "lettera di presentazione"],
            "Ha studiato ad Alessandria e ha letto ciò che altri hanno bruciato."),
    ];

    public static IReadOnlyList<Origin> Origins { get; } =
    [
        new("Italia", Trait.Facundia, "latino", "Cresciuto all'ombra del Foro, tra clientele e favori."),
        new("Gallia", Trait.Robur, "gallico", "Discendente di guerrieri, romano da due generazioni."),
        new("Hispania", Trait.Celeritas, "iberico", "Viene dalle miniere e dalle coste dell'ovest."),
        new("Aegyptus", Trait.Ingenium, "greco ed egizio", "Ha visto i templi del Nilo e le loro biblioteche."),
        new("Achaia", Trait.Facundia, "greco", "Educato alla retorica e alla filosofia."),
        new("Britannia", Trait.Pietas, "britannico", "Viene da un'isola di nebbie e boschi sacri."),
        new("Syria", Trait.Ingenium, "aramaico e greco", "Conosce le carovane e i culti d'oriente."),
        new("Germania", Trait.Robur, "germanico", "Nato oltre il limes, ha scelto Roma."),
    ];

    public static Skill? FindSkill(string name) => Skills.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    public static Vocation? FindVocation(string name) => Vocations.FirstOrDefault(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    public static Origin? FindOrigin(string name) => Origins.FirstOrDefault(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
