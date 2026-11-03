using LexArcana.Game;

namespace LexArcana.Tests;

public class DiceTests
{
    [Theory]
    [InlineData("d20", 1, 0)]
    [InlineData("3d6+2", 3, 2)]
    [InlineData("1d8 + 1d4 - 1", 2, -1)]
    [InlineData("2D10", 2, 0)]
    public void Parses_valid_expressions(string input, int diceCount, int modifier)
    {
        Assert.True(Dice.TryParse(input, out var expression, out _));
        Assert.Equal(diceCount, expression.Groups.Sum(g => g.Count));
        Assert.Equal(modifier, expression.Modifier);
    }

    [Theory]
    [InlineData("")]
    [InlineData("5")]
    [InlineData("d1")]
    [InlineData("0d6")]
    [InlineData("101d6")]
    [InlineData("d1001")]
    [InlineData("2x6")]
    [InlineData("99999999999d6")]
    [InlineData("60d6+60d6")]
    [InlineData("d6+99999")]
    public void Rejects_invalid_expressions(string input)
    {
        Assert.False(Dice.TryParse(input, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Rolls_stay_within_the_faces_and_the_total_adds_up()
    {
        var dice = new Dice(new Random(42));
        Assert.True(Dice.TryParse("4d6+3", out var expression, out _));
        for (int i = 0; i < 200; i++)
        {
            var result = dice.Roll(expression);
            Assert.All(result.Dice, d => Assert.InRange(d, 1, 6));
            Assert.Equal(result.Dice.Sum() + 3, result.Total);
        }
    }

    [Fact]
    public void Subtracted_dice_count_negative()
    {
        Assert.True(Dice.TryParse("d4-d4", out var expression, out _));
        var result = new Dice(new Random(1)).Roll(expression);
        Assert.InRange(result.Dice[0], 1, 4);
        Assert.InRange(result.Dice[1], -4, -1);
    }
}
