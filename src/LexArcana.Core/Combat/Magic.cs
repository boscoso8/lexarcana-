using LexArcana.Characters;

namespace LexArcana.Combat;

/// <summary>Le tre vie della magia: ognuna si appoggia a un'abilità e a un attributo.</summary>
public enum Tradition
{
    /// <summary>Incantesimi: il sapere proibito degli eruditi (Erudizione + Ingenium).</summary>
    Incantesimi,
    /// <summary>Miracoli divini: la grazia degli dei (Riti + Pietas).</summary>
    Miracoli,
    /// <summary>Invocazioni: gli spiriti, i Lari e i Mani (Presagi + Pietas).</summary>
    Invocazioni,
}

/// <summary>Su chi si lancia un prodigio.</summary>
public enum SpellTarget { Enemy, Ally, Party, None }

/// <summary>
/// Un prodigio. Tier 1 si conosce subito, 2 dal livello 3, 3 dal livello 5, purché si abbia almeno un grado
/// nell'abilità della sua tradizione. Cost in punti Volontà; Range in metri.
/// </summary>
public sealed record Spell(string Id, string Name, Tradition Tradition, int Tier, int Cost, SpellTarget Target, float Range, string Description)
{
    /// <summary>La difficoltà della prova per i prodigi che non colpiscono un nemico (per quelli, la sua Difesa).</summary>
    public int Difficulty => 8 + 2 * Tier;
}

public static class Grimoire
{
    public static IReadOnlyList<Spell> All { get; } =
    [
        new("dardo", "Dardo di luce", Tradition.Incantesimi, 1, 2, SpellTarget.Enemy, 18, "Una freccia di luce bianca: 2d6 + metà di Ingenium."),
        new("catene", "Catene di Saturno", Tradition.Incantesimi, 2, 3, SpellTarget.Enemy, 14, "Il tempo si ferma intorno al nemico: non si muove né colpisce per 4 secondi."),
        new("fiamma", "Fiamma di Vulcano", Tradition.Incantesimi, 3, 5, SpellTarget.Enemy, 16, "Un'esplosione di fuoco: 2d8 + metà di Ingenium a tutti i nemici entro 3 metri dal bersaglio."),
        new("mano", "Mano di Asclepio", Tradition.Miracoli, 1, 2, SpellTarget.Ally, 12, "Guarisce un compagno, o te stesso: 2d6 + Pietas."),
        new("egida", "Egida di Minerva", Tradition.Miracoli, 2, 3, SpellTarget.Party, 8, "+3 alla Difesa tua e dei compagni entro 8 metri, per 20 secondi."),
        new("folgore", "Folgore di Giove", Tradition.Miracoli, 3, 5, SpellTarget.Enemy, 20, "Un fulmine dal cielo: 3d8 + Pietas."),
        new("occhio", "Occhio dell'augure", Tradition.Invocazioni, 1, 1, SpellTarget.None, 0, "Per 15 secondi vedi tutti i nemici del luogo, anche attraverso i muri."),
        new("esorcismo", "Esorcismo dei Mani", Tradition.Invocazioni, 2, 3, SpellTarget.Enemy, 12, "Contro lemuri e scheletri 3d6 + Pietas; contro gli altri solo 1d6."),
        new("pan", "Terrore di Pan", Tradition.Invocazioni, 3, 4, SpellTarget.Party, 9, "I nemici entro 9 metri fuggono in preda al panico per 6 secondi."),
    ];

    public static Spell? Find(string idOrName) =>
        All.FirstOrDefault(s => s.Id.Equals(idOrName, StringComparison.OrdinalIgnoreCase) || s.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));

    public static string Skill(Tradition t) => t switch
    {
        Tradition.Incantesimi => "Erudizione",
        Tradition.Miracoli => "Riti",
        _ => "Presagi",
    };

    public static Trait Trait(Tradition t) => t == Tradition.Incantesimi ? Characters.Trait.Ingenium : Characters.Trait.Pietas;

    public static int LevelFor(int tier) => tier switch { 1 => 1, 2 => 3, _ => 5 };

    /// <summary>I prodigi che un personaggio conosce al suo livello.</summary>
    public static IReadOnlyList<Spell> Known(Character? character, int level) =>
        character is null ? [] : [.. All.Where(s => character.SkillRank(Skill(s.Tradition)) > 0 && level >= LevelFor(s.Tier))];

    /// <summary>Le creature su cui l'Esorcismo dei Mani ha pieno effetto.</summary>
    public static bool IsUndead(EnemyKind kind) => kind.Id is "lemure" or "scheletro";
}
