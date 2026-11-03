using System.Globalization;
using System.Text.RegularExpressions;

namespace LexArcana.Game;

/// <summary>Un gruppo di dadi uguali: Count dadi da Sides facce, sommati (Sign = 1) o sottratti (Sign = -1).</summary>
public readonly record struct DiceGroup(int Count, int Sides, int Sign);

/// <summary>Un'espressione già controllata, pronta da tirare.</summary>
public sealed record DiceExpression(string Text, DiceGroup[] Groups, int Modifier);

/// <summary>Il risultato di un tiro.</summary>
public sealed record DiceResult(DiceExpression Expression, int[] Dice)
{
    public int Total => Dice.Sum() + Expression.Modifier;
}

/// <summary>
/// Tira i dadi in notazione standard: termini <c>NdM</c> o numeri separati da + o -,
/// per esempio <c>d20</c>, <c>3d6+2</c>, <c>1d8+1d4-1</c>.
/// </summary>
public sealed partial class Dice(Random? random = null)
{
    public const int MaxDice = 100;
    public const int MaxSides = 1000;
    public const int MaxTerms = 10;
    public const int MaxModifier = 10_000;
    public const string Hint = "Scrivi un tiro come d20, 3d6+2 o 1d8+1d4.";

    private readonly Random _random = random ?? Random.Shared;
    private readonly Lock _lock = new();

    [GeneratedRegex(@"^(?<sign>[+-]?)(?:(?<count>\d{0,4})d(?<sides>\d{1,5})|(?<flat>\d{1,6}))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Term { get; }

    [GeneratedRegex("(?=[+-])")]
    private static partial Regex BeforeSign { get; }

    public static bool TryParse(string? input, out DiceExpression expression, out string error)
    {
        expression = null!;
        error = Hint;
        var text = (input ?? "").Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
        if (text.Length is 0 or > 64) return false;

        var terms = BeforeSign.Split(text).Where(t => t.Length > 0).ToArray();
        if (terms.Length > MaxTerms)
        {
            error = $"Al massimo {MaxTerms} termini per tiro.";
            return false;
        }

        List<DiceGroup> groups = [];
        int modifier = 0;
        foreach (var term in terms)
        {
            var m = Term.Match(term);
            if (!m.Success)
            {
                error = $"«{term}» non è un dado valido. {Hint}";
                return false;
            }

            int sign = m.Groups["sign"].Value == "-" ? -1 : 1;
            if (m.Groups["flat"].Success)
            {
                modifier += sign * Number(m.Groups["flat"].Value);
                continue;
            }

            var count = m.Groups["count"].Value is { Length: > 0 } c ? Number(c) : 1;
            var sides = Number(m.Groups["sides"].Value);
            if (count < 1 || groups.Sum(g => g.Count) + count > MaxDice)
            {
                error = $"Da 1 a {MaxDice} dadi per tiro.";
                return false;
            }
            if (sides is < 2 or > MaxSides)
            {
                error = $"Un dado ha da 2 a {MaxSides} facce.";
                return false;
            }
            groups.Add(new(count, sides, sign));
        }

        if (groups.Count == 0)
        {
            error = $"Il tiro deve contenere almeno un dado. {Hint}";
            return false;
        }
        if (Math.Abs(modifier) > MaxModifier)
        {
            error = "Modificatore troppo grande.";
            return false;
        }

        expression = new(text, [.. groups], modifier);
        return true;
    }

    public DiceResult Roll(DiceExpression expression)
    {
        List<int> dice = [];
        lock (_lock)
        {
            foreach (var (count, sides, sign) in expression.Groups)
                for (int i = 0; i < count; i++)
                    dice.Add(sign * _random.Next(1, sides + 1));
        }
        return new(expression, [.. dice]);
    }

    private static int Number(string digits) => int.Parse(digits, CultureInfo.InvariantCulture);
}
