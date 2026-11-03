namespace LexArcana.Combat;

/// <summary>Livelli ed esperienza dei personaggi.</summary>
public static class Progression
{
    public const int MaxLevel = 10;

    /// <summary>L'esperienza totale che serve per arrivare a un livello: 0, 100, 300, 600, 1000…</summary>
    public static int ExperienceFor(int level) => 50 * level * (level - 1);

    public static int LevelFor(int experience)
    {
        int level = 1;
        while (level < MaxLevel && experience >= ExperienceFor(level + 1)) level++;
        return level;
    }

    /// <summary>
    /// Il livello dei nemici di un luogo: cresce con l'avventura e con il livello medio del gruppo,
    /// così i combattimenti restano una sfida senza diventare impossibili.
    /// </summary>
    public static int EncounterLevel(int adventure, double partyLevel) =>
        Math.Clamp((int)Math.Round(Math.Max(1, adventure) + Math.Max(1, partyLevel) - 1), 1, MaxLevel);
}
