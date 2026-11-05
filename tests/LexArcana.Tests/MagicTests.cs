using LexArcana.Characters;
using LexArcana.Combat;
using LexArcana.Game;
using LexArcana.Net;
using LexArcana.Protocol;

namespace LexArcana.Tests;

public class MagicTests
{
    private static GameMap Hall()
    {
        string wall = new('#', 22), floor = "#S" + new string('.', 19) + "#";
        Assert.True(GameMap.TryParse([wall, floor, "#" + new string('.', 20) + "#", floor.Replace('S', '.'), wall], out var map, out _));
        return map;
    }

    private static readonly Pose Here = new(3, 3, MathF.PI / 2);
    private static Character Augur(int experience = 0) => Character.Random("Aurelia", 3, "Augure") with { Experience = experience };
    private static Dictionary<string, Pose> Alone => new() { ["Aurelia"] = Here };

    /// <summary>Lancia finché riesce (le prove possono fallire), restituendo gli eventi del lancio riuscito.</summary>
    private static List<CombatEvent> CastUntilSuccess(CombatEngine engine, Fighter f, string spell, int? enemy, string? ally, Dictionary<string, Pose> players, ref double now)
    {
        for (int i = 0; i < 50; i++)
        {
            f.Resolve = f.MaxResolve;
            var events = engine.Cast(f.Name, Here, spell, enemy, ally, now, players, out var refusal);
            Assert.Null(refusal);
            now += CombatEngine.CastCooldown;
            if (events.OfType<Miracle>().Single().Success) return events;
        }
        throw new InvalidOperationException("Il prodigio non riesce mai.");
    }

    [Fact]
    public void Spells_unlock_with_the_tradition_skill_and_the_level()
    {
        var augur = Augur();
        Assert.Equal(["dardo", "mano", "occhio"], Grimoire.Known(augur, 1).Select(s => s.Id));          // Erudizione, Riti, Presagi
        Assert.Equal(6, Grimoire.Known(augur, 3).Count);
        Assert.Equal(9, Grimoire.Known(augur, 5).Count);
        var legionary = Character.Random("Marco", 5, "Legionario") with { ChosenSkills = ["Tiro", "Cavalcare"] };
        Assert.Empty(Grimoire.Known(legionary, 10));
        Assert.Contains(augur.ToSheet(), f => f.Key == "Magia" && f.Value == "Dardo di luce, Mano di Asclepio, Occhio dell'augure · altri 6 salendo di livello");
        Assert.All(Grimoire.All, s => Assert.InRange(s.Cost, 1, 5));
    }

    [Fact]
    public void Casting_spends_volonta_refuses_what_you_cannot_afford_and_regenerates()
    {
        var engine = new CombatEngine(new Dice(new Random(1)), Hall());
        var aurelia = engine.Join("Aurelia", Augur());
        int full = aurelia.Resolve;
        Assert.Equal(aurelia.MaxResolve, full);

        engine.Cast("Aurelia", Here, "occhio", null, null, 0, Alone, out var r1);
        Assert.Null(r1);
        Assert.Equal(full - 1, aurelia.Resolve);

        aurelia.Resolve = 1;
        Assert.Empty(engine.Cast("Aurelia", Here, "mano", null, null, 10, Alone, out var poor));
        Assert.Contains("Volontà", poor, StringComparison.Ordinal);
        Assert.Empty(engine.Cast("Aurelia", Here, "folgore", null, null, 10, Alone, out var unknown));
        Assert.Contains("livello 5", unknown, StringComparison.Ordinal);
        Assert.Empty(engine.Cast("Aurelia", Here, "volo", null, null, 10, Alone, out var none));
        Assert.NotNull(none);

        for (double t = 10; t < 10 + CombatEngine.ResolveRegenEvery * 2 + 0.5; t += 0.5) engine.Tick(t, 0.5f, Alone);
        Assert.Equal(3, aurelia.Resolve);   // due punti recuperati in due intervalli
    }

    [Fact]
    public void Healing_never_goes_past_full_and_reaches_a_nearby_ally()
    {
        var engine = new CombatEngine(new Dice(new Random(2)), Hall());
        var aurelia = engine.Join("Aurelia", Augur());
        var marco = engine.Join("Marco");
        marco.Health = 3;
        var players = new Dictionary<string, Pose> { ["Aurelia"] = Here, ["Marco"] = new(5, 3, 0) };
        double now = 0;
        var events = CastUntilSuccess(engine, aurelia, "mano", null, "Marco", players, ref now);
        var miracle = events.OfType<Miracle>().Single();
        Assert.Equal(miracle.Amount, marco.Health - 3);
        Assert.Contains(events, e => e is VitalsChanged v && v.Fighter == marco);

        marco.Health = marco.MaxHealth - 1;
        CastUntilSuccess(engine, aurelia, "mano", null, "Marco", players, ref now);
        Assert.Equal(marco.MaxHealth, marco.Health);

        players["Marco"] = new(40, 3, 0);
        aurelia.Resolve = aurelia.MaxResolve;
        Assert.Empty(engine.Cast("Aurelia", Here, "mano", null, "Marco", now + 5, players, out var far));
        Assert.Contains("lontano", far, StringComparison.Ordinal);
    }

    [Fact]
    public void Chains_stop_an_enemy_terror_makes_it_flee_and_aegis_raises_defense()
    {
        var engine = new CombatEngine(new Dice(new Random(3)), Hall());
        var aurelia = engine.Join("Aurelia", Augur(1000));   // livello 5: tutti i prodigi
        var wolf = engine.Summon(Bestiary.BlackWolf, 1, EnemyRank.Normal, new Pose(10, 3, 0));
        double now = 0;

        CastUntilSuccess(engine, aurelia, "catene", wolf.Id, null, Alone, ref now);
        var held = wolf.Pose;
        engine.Tick(now, 0.5f, Alone);
        Assert.Equal(held, wolf.Pose);                       // fermo nelle catene
        now += 5;

        CastUntilSuccess(engine, aurelia, "pan", null, null, Alone, ref now);
        wolf.Pose = new Pose(7, 3, 0);
        var before = wolf.Pose;
        engine.Tick(now, 0.5f, Alone);
        Assert.True(wolf.Pose.X > before.X);                 // scappa, allontanandosi da Aurelia

        int defense = aurelia.Defense;
        var aegis = CastUntilSuccess(engine, aurelia, "egida", null, null, Alone, ref now);
        Assert.Equal(defense + 3, aurelia.Defense);
        engine.Tick(now + 25, 0.1f, Alone);
        Assert.Equal(defense, aurelia.Defense);               // finita dopo 20 secondi
        Assert.Equal(20, aegis.OfType<Miracle>().Single().Duration);
    }

    [Fact]
    public void Damage_spells_hurt_the_exorcism_prefers_the_undead_and_fire_hits_an_area()
    {
        var engine = new CombatEngine(new Dice(new Random(4)), Hall());
        var aurelia = engine.Join("Aurelia", Augur(1000));
        double now = 0;

        var skeleton = engine.Summon(Bestiary.Skeleton, 10, EnemyRank.Boss, new Pose(10, 3, 0));
        var wolf = engine.Summon(Bestiary.BlackWolf, 10, EnemyRank.Boss, new Pose(10, 5, 0));
        int undead = CastUntilSuccess(engine, aurelia, "esorcismo", skeleton.Id, null, Alone, ref now).OfType<Miracle>().Single().Amount;
        int living = CastUntilSuccess(engine, aurelia, "esorcismo", wolf.Id, null, Alone, ref now).OfType<Miracle>().Single().Amount;
        Assert.InRange(living, 1, 12);                       // 1d6, raddoppiato se critico
        Assert.True(undead >= 3 + aurelia.TraitValue(Trait.Pietas));

        int skeletonBefore = skeleton.Health, wolfBefore = wolf.Health;
        var fire = CastUntilSuccess(engine, aurelia, "fiamma", skeleton.Id, null, Alone, ref now);
        Assert.Equal(2, fire.OfType<Blow>().Count());       // il lupo è a 2 metri: preso anche lui
        Assert.True(skeleton.Health < skeletonBefore && wolf.Health < wolfBefore);

        var weak = engine.Summon(Bestiary.GraveRobber, 1, EnemyRank.Normal, new Pose(12, 3, 0));
        for (int i = 0; i < 20 && engine.EnemyOf(weak.Id) is not null; i++) CastUntilSuccess(engine, aurelia, "dardo", weak.Id, null, Alone, ref now);
        Assert.Null(engine.EnemyOf(weak.Id));
        Assert.True(aurelia.Experience > 1000);              // anche la magia dà esperienza
    }

    [Fact]
    public void Spell_messages_round_trip()
    {
        Assert.Equal(new Cast("mano", null, "Marco"), Wire.DecodeClient(Wire.Encode(new Cast("mano", null, "Marco"))));
        Assert.Equal(new Cast("dardo", 3), Wire.DecodeClient(Wire.Encode(new Cast("dardo", 3))));
        var cast = new SpellCast("Aurelia", "egida", "Egida di Minerva", "Miracoli", 14, 22, 12, true, "2 compagni", 2, 20, 3, 4);
        Assert.Equal(Wire.Encode(cast), Wire.Encode(Wire.DecodeServer(Wire.Encode(cast))));
        var vitals = new Vitals("Aurelia", 10, 14, 2, 120, 300, false, 5, 12, ["dardo", "mano"]);
        Assert.Equal(["dardo", "mano"], Assert.IsType<Vitals>(Wire.DecodeServer(Wire.Encode(vitals))).Spells!);
        Assert.Equal(new PlayerInput.Send(new Cast("mano", null, "Livia")), PlayerCommands.Parse("/lancia mano Livia"));
    }
}
