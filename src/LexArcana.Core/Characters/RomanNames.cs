namespace LexArcana.Characters;

/// <summary>Nomi romani plausibili: praenomen, nomen e cognomen per gli uomini, nomen e cognomen al femminile per le donne.</summary>
public static class RomanNames
{
    private static readonly string[] Praenomina = ["Gaius", "Lucius", "Marcus", "Publius", "Quintus", "Titus", "Aulus", "Gnaeus", "Sextus", "Decimus", "Servius", "Spurius"];
    private static readonly string[] Nomina = ["Iulius", "Cornelius", "Valerius", "Aemilius", "Claudius", "Fabius", "Licinius", "Sempronius", "Antonius", "Flavius", "Aurelius", "Caecilius", "Pompeius", "Sulpicius", "Vipsanius", "Domitius", "Terentius", "Marius"];
    private static readonly string[] Cognomina = ["Rufus", "Niger", "Longus", "Severus", "Felix", "Crispus", "Paullus", "Varro", "Celer", "Priscus", "Maximus", "Lentulus", "Corvus", "Catulus", "Naso", "Saturninus", "Verus", "Gallus"];

    public static string Generate(Random random, bool female)
    {
        var nomen = Pick(random, Nomina);
        var cognomen = Pick(random, Cognomina);
        return female ? $"{Feminine(nomen)} {Feminine(cognomen)}" : $"{Pick(random, Praenomina)} {nomen} {cognomen}";
    }

    private static string Feminine(string name) =>
        name.EndsWith("us", StringComparison.Ordinal) ? name[..^2] + "a" :
        name.EndsWith("er", StringComparison.Ordinal) ? name[..^2] + "ra" :
        name.EndsWith('o') ? name + "nia" :
        name.EndsWith('x') ? name[..^1] + "cia" :
        name + "a";

    private static string Pick(Random random, string[] items) => items[random.Next(items.Length)];
}
