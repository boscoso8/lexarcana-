using LexArcana.Campaigns;

namespace LexArcana.Combat;

/// <summary>
/// Un tipo di nemico al livello 1. Le statistiche crescono con il livello (vedi <see cref="Enemy"/>).
/// Speed in m/s; Reach, Aggro in metri.
/// </summary>
public sealed record EnemyKind(
    string Id, string Name, string EliteName,
    int BaseHealth, int HealthPerLevel, int BaseAttack, int BaseDefense, int DamageDie,
    float Speed, float Reach, float Aggro, int Experience, string Description);

/// <summary>I nemici, presi dal folclore romano. Tutti originali nelle statistiche.</summary>
public static class Bestiary
{
    public static EnemyKind Cultist { get; } = new("cultista", "Cultista", "Gran sacerdote del culto", 8, 4, 2, 11, 6, 2.6f, 1.7f, 11f, 15, "Fanatici incappucciati, armati di falcetti rituali.");
    public static EnemyKind GraveRobber { get; } = new("ladro", "Ladro di tombe", "Capobanda", 7, 3, 3, 12, 6, 3.2f, 1.6f, 10f, 15, "Svelti e senza scrupoli, colpiscono e scappano.");
    public static EnemyKind BlackWolf { get; } = new("lupo", "Lupo nero", "Lupo capobranco", 9, 4, 3, 12, 6, 4.2f, 1.5f, 13f, 18, "Più grandi dei lupi veri, con occhi che riflettono il fuoco.");
    public static EnemyKind Lemur { get; } = new("lemure", "Lemure", "Lemure antico", 10, 4, 3, 13, 8, 2.2f, 1.8f, 12f, 20, "Uno spirito inquieto dei morti senza sepoltura.");
    public static EnemyKind Skeleton { get; } = new("scheletro", "Legionario scheletrico", "Centurione scheletrico", 12, 5, 3, 13, 8, 2.4f, 1.9f, 10f, 22, "Soldati caduti che marciano ancora, con lo scudo in mano.");
    public static EnemyKind Strix { get; } = new("strige", "Strige", "Strige regina", 6, 3, 4, 14, 4, 4.5f, 1.6f, 14f, 18, "Uccello notturno con artigli di bronzo e voce di donna.");
    public static EnemyKind Guardian { get; } = new("custode", "Custode di bronzo", "Colosso di bronzo", 20, 8, 4, 15, 10, 1.8f, 2.2f, 9f, 40, "Un automa costruito per proteggere il cuore del luogo.");

    public static IReadOnlyList<EnemyKind> All { get; } = [Cultist, GraveRobber, BlackWolf, Lemur, Skeleton, Strix, Guardian];

    public static EnemyKind? Find(string idOrName) =>
        All.FirstOrDefault(k => k.Id.Equals(idOrName, StringComparison.OrdinalIgnoreCase) || k.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Chi si incontra in un luogo. Il primo della lista fa da guardiano all'altare.</summary>
    public static IReadOnlyList<EnemyKind> ForPlace(PlaceKind? kind) => kind switch
    {
        PlaceKind.Tempio => [Cultist, Lemur, Cultist],
        PlaceKind.Foro => [Cultist, GraveRobber, GraveRobber],
        PlaceKind.Villa => [Lemur, Cultist, Strix],
        PlaceKind.Catacombe => [Skeleton, Lemur, GraveRobber],
        PlaceKind.Necropoli => [Skeleton, Lemur, Strix],
        PlaceKind.Miniera => [Guardian, BlackWolf, Skeleton],
        PlaceKind.Accampamento => [Skeleton, BlackWolf, Skeleton],
        _ => [Cultist, BlackWolf, Lemur],
    };
}
