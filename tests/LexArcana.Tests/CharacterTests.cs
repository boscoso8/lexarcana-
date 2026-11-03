using LexArcana.Characters;

namespace LexArcana.Tests;

public class CharacterTests
{
    private static CharacterBuilder Augur()
    {
        var b = new CharacterBuilder("Aurelia");
        b.Choose(Rules.FindVocation("Augure")!);
        b.Choose(Rules.FindOrigin("Britannia")!);
        b.SetRomanName("Aurelia Severa");
        Assert.True(b.TrySetAttribute(Trait.Robur, 3, out _));
        Assert.True(b.TrySetAttribute(Trait.Celeritas, 2, out _));
        Assert.True(b.TrySetAttribute(Trait.Ingenium, 4, out _));
        Assert.True(b.TrySetAttribute(Trait.Facundia, 2, out _));
        Assert.True(b.TrySetAttribute(Trait.Pietas, 4, out _));
        return b;
    }

    [Fact]
    public void A_complete_build_applies_vocation_and_origin_bonuses_and_skill_ranks()
    {
        var b = Augur();
        Assert.Equal(0, b.PointsLeft);
        Assert.True(b.TryChooseSkill("Tiro", out _));
        Assert.True(b.TryChooseSkill("atletica", out _));
        Assert.True(b.TryBuild(out var c, out var errors), string.Join(" ", errors));

        Assert.Equal(6, c[Trait.Pietas]);               // 4 + augure + Britannia
        Assert.Equal(3, c[Trait.Robur]);
        Assert.Equal(2, c.SkillRank("Presagi"));        // dalla vocazione
        Assert.Equal(1, c.SkillRank("Tiro"));           // scelta
        Assert.Equal(0, c.SkillRank("Medicina"));
        Assert.Equal(6 + 2, c.CheckBonus("Presagi"));
        Assert.Equal(8 + 2 * 3, c.Health);
        Assert.Equal(4 + 6 + 4, c.Resolve);
        Assert.Equal(10 + 2, c.Defense);
        Assert.Contains(("Salute", "14/14"), c.ToSheet());
    }

    [Fact]
    public void The_builder_refuses_what_breaks_the_rules()
    {
        var b = Augur();
        Assert.False(b.TrySetAttribute(Trait.Robur, 4, out var noPoints));   // finiti i punti
        Assert.Contains("punti", noPoints, StringComparison.Ordinal);
        Assert.False(b.TrySetAttribute(Trait.Robur, 6, out _));              // oltre il massimo
        Assert.False(b.TryChooseSkill("Presagi", out var fromVocation));     // già della vocazione
        Assert.Contains("vocazione", fromVocation, StringComparison.Ordinal);
        Assert.False(b.TryChooseSkill("Volare", out _));
        Assert.True(b.TryChooseSkill("Tiro", out _));
        Assert.False(b.TryChooseSkill("Tiro", out _));                       // ripetuta
        Assert.False(b.TryBuild(out _, out var errors));                     // manca la seconda abilità
        Assert.Contains(errors, e => e.Contains("abilità", StringComparison.Ordinal));
    }

    [Fact]
    public void Random_characters_are_always_valid_and_reproducible()
    {
        for (int seed = 0; seed < 300; seed++)
        {
            var c = Character.Random("Prova", seed);
            Assert.Empty(c.Validate());
            Assert.All(c.ToSheet(), f => Assert.True(f.Value.Length <= LexArcana.Game.CharacterSheet.MaxValueLength, f.Key));
        }
        Assert.Equal(Character.Random("Prova", 7).ToJson(), Character.Random("Prova", 7).ToJson());
    }

    [Fact]
    public void Characters_survive_a_round_trip_through_json_and_files_stay_readable()
    {
        var c = Character.Random("Livia", 11);
        var json = c.ToJson();
        Assert.DoesNotContain("health", json, StringComparison.Ordinal);   // i valori calcolati non si salvano
        Assert.DoesNotContain("\\u00", json, StringComparison.Ordinal);     // le lettere accentate restano leggibili
        var back = Character.FromJson(json);
        Assert.Equal(c.ToSheet(), back.ToSheet());
        Assert.Throws<FormatException>(() => Character.FromJson("{\"player\":1}"));
    }

    [Fact]
    public void Validation_catches_tampered_files()
    {
        var c = Character.Random("Livia", 11);
        var cheat = c with { BaseAttributes = Rules.Traits.ToDictionary(t => t, _ => 5) };
        Assert.Contains(cheat.Validate(), e => e.Contains("punti", StringComparison.Ordinal));
        Assert.NotEmpty((c with { Vocation = "Imperatore" }).Validate());
        Assert.NotEmpty((c with { Player = "Demiurgo" }).Validate());
    }

    [Theory]
    [InlineData("Niger", "Nigra")]
    [InlineData("Iulius", "Iulia")]
    public void Feminine_names_follow_latin_endings(string male, string female)
    {
        // RomanNames è privato nei dettagli: lo verifichiamo generando finché esce il nome cercato
        var random = new Random(0);
        for (int i = 0; i < 5000; i++)
        {
            var name = RomanNames.Generate(random, female: true);
            Assert.DoesNotContain(male, name, StringComparison.Ordinal);
            if (name.Contains(female, StringComparison.Ordinal)) return;
        }
        Assert.Fail($"{female} non compare mai.");
    }
}
