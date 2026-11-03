using LexArcana.Campaigns;
using LexArcana.Characters;
using LexArcana.Combat;
using LexArcana.Game;

namespace LexArcana.Tests;

public class CombatTests
{
    /// <summary>Una stanza lunga e vuota: partenze a ovest, spazio a est.</summary>
    private static GameMap Hall()
    {
        string wall = new('#', 22), floor = "#S" + new string('.', 19) + "#";
        Assert.True(GameMap.TryParse([wall, floor, "#" + new string('.', 20) + "#", floor.Replace('S', '.'), wall], out var map, out var error), error);
        return map;
    }

    private static readonly Pose West = new(3, 3, MathF.PI / 2);

    [Fact]
    public void Levels_follow_the_experience_table_and_encounters_scale_with_the_party()
    {
        Assert.Equal([0, 100, 300, 600, 1000], Enumerable.Range(1, 5).Select(Progression.ExperienceFor));
        Assert.Equal(1, Progression.LevelFor(99));
        Assert.Equal(2, Progression.LevelFor(100));
        Assert.Equal(Progression.MaxLevel, Progression.LevelFor(int.MaxValue / 2));
        Assert.Equal(1, Progression.EncounterLevel(1, 1));
        Assert.Equal(4, Progression.EncounterLevel(3, 2));
        Assert.Equal(Progression.MaxLevel, Progression.EncounterLevel(7, 9));
    }

    [Fact]
    public void Enemies_get_stronger_with_level_and_rank()
    {
        var engine = new CombatEngine(new Dice(new Random(1)), Hall());
        var weak = engine.Summon(Bestiary.Lemur, 1, EnemyRank.Normal, West);
        var strong = engine.Summon(Bestiary.Lemur, 5, EnemyRank.Normal, West);
        var boss = engine.Summon(Bestiary.Lemur, 5, EnemyRank.Boss, West, "Flavia Severa");
        Assert.True(strong.MaxHealth > weak.MaxHealth && strong.Attack > weak.Attack && strong.Defense > weak.Defense);
        Assert.True(boss.MaxHealth > strong.MaxHealth * 2);
        Assert.Equal("Flavia Severa (liv. 5)", boss.Label);
        Assert.Equal(strong.Experience * 5, boss.Experience);
    }

    [Fact]
    public void Populate_keeps_the_start_safe_guards_the_altar_and_brings_the_boss_in_the_finale()
    {
        var campaign = CampaignGenerator.Generate(42, 4);
        var last = campaign.Adventures[^1];
        Assert.True(GameMap.TryParse([$"nome: x", .. last.Map], out var map, out _));
        var engine = new CombatEngine(new Dice(new Random(2)), map);
        var enemies = engine.Populate(map, last.Kind, last.Number, finalAdventure: true, "Flavia Severa", new Random(3));

        Assert.InRange(enemies.Count, 4, 11);
        Assert.All(enemies, e => Assert.True(map.IsWalkable(e.Pose.X, e.Pose.Z)));
        Assert.All(enemies, e => Assert.All(map.Spawns, s => Assert.True(new Pose(e.Pose.X, e.Pose.Z, 0).DistanceTo(s) > CombatEngine.SafeRadius)));
        Assert.Single(enemies, e => e.Rank == EnemyRank.Elite);
        var boss = Assert.Single(enemies, e => e.Rank == EnemyRank.Boss);
        Assert.Equal("Flavia Severa", boss.Name);
        Assert.Equal(Progression.EncounterLevel(4, 1) + 2, boss.Level);
        Assert.All(enemies, e => Assert.Contains(e.Kind, Bestiary.ForPlace(last.Kind)));
    }

    [Fact]
    public void A_character_attacks_with_its_skills_and_shares_the_experience()
    {
        var engine = new CombatEngine(new Dice(new Random(5)), Hall());
        var legionary = Character.Random("Marco", 0) with { Vocation = "Legionario" };
        var marco = engine.Join("Marco", legionary);
        var livia = engine.Join("Livia");
        Assert.Equal(legionary.CheckBonus("Armi"), marco.MeleeBonus);
        Assert.Equal(legionary.Health, marco.MaxHealth);

        var rat = engine.Summon(Bestiary.GraveRobber, 1, EnemyRank.Normal, new Pose(4.5f, 3, 0));
        double now = 0;
        List<CombatEvent> all = [];
        while (engine.EnemyOf(rat.Id) is not null && now < 60)
        {
            all.AddRange(engine.Attack("Marco", West, rat.Id, ranged: false, now, out var refusal));
            Assert.Null(refusal);
            now += Fighter.AttackCooldown;
        }
        Assert.Null(engine.EnemyOf(rat.Id));
        var final = all.OfType<Blow>().Last();
        Assert.True(final.Hit && final.Defeated && final.Health == 0);
        Assert.Equal(rat.Experience, marco.Experience);
        Assert.Equal(rat.Experience, livia.Experience); // tutto il gruppo impara
    }

    [Fact]
    public void Attacks_are_refused_out_of_reach_behind_walls_and_while_down()
    {
        var engine = new CombatEngine(new Dice(new Random(5)), Hall());
        engine.Join("Marco");
        var far = engine.Summon(Bestiary.Lemur, 1, EnemyRank.Normal, new Pose(14, 3, 0));  // 11 m: oltre la mischia, entro il tiro
        Assert.Empty(engine.Attack("Marco", West, far.Id, ranged: false, 0, out var tooFar));
        Assert.Contains("lontano", tooFar, StringComparison.Ordinal);
        Assert.NotEmpty(engine.Attack("Marco", West, far.Id, ranged: true, 0, out var shot));   // a distanza sì
        Assert.Null(shot);
        Assert.Empty(engine.Attack("Marco", West, far.Id, ranged: true, 0.1, out var early)); // ricarica: il colpo si perde
        Assert.Null(early);
        Assert.Empty(engine.Attack("Marco", West, 999, ranged: true, 5, out var gone));
        Assert.NotNull(gone);
        Assert.Empty(engine.Attack("Nessuno", West, far.Id, ranged: true, 5, out _));
    }

    [Fact]
    public void Enemies_hunt_players_they_can_see_hit_them_and_the_fallen_rise_again()
    {
        var engine = new CombatEngine(new Dice(new Random(9)), Hall());
        var marco = engine.Join("Marco");
        var wolf = engine.Summon(Bestiary.BlackWolf, 6, EnemyRank.Boss, new Pose(14, 3, 0));
        var players = new Dictionary<string, Pose> { ["Marco"] = West };

        // il lupo corre verso Marco
        var start = wolf.Pose;
        engine.Tick(0.1, 0.1f, players);
        Assert.True(wolf.Pose.X < start.X);
        Assert.Contains(engine.Tick(0.2, 0.1f, players), e => e is EnemiesMoved);

        // e alla fine lo mette a terra
        double now = 0.2;
        while (!marco.IsDown && now < 120) { now += 0.1; engine.Tick(now, 0.1f, players); }
        Assert.True(marco.IsDown);
        Assert.Equal(0, marco.Health);

        // a terra non lo attaccano più; dopo qualche secondo si rialza a una partenza con la salute piena
        var events = new List<CombatEvent>();
        for (double t = now; t < now + CombatEngine.RespawnDelay + 0.5; t += 0.1) events.AddRange(engine.Tick(t, 0.1f, players));
        Assert.DoesNotContain(events, e => e is Blow b && b.Target == "Marco" && b.Roll > 0 && events.IndexOf(e) < events.FindIndex(x => x is Respawned));
        var risen = Assert.Single(events.OfType<Respawned>());
        Assert.Equal(marco.MaxHealth, marco.Health);
        Assert.Contains(risen.Pose, engine.Map.Spawns);
    }

    [Fact]
    public void Enemies_ignore_players_who_are_not_in_the_scene_or_out_of_sight()
    {
        Assert.True(GameMap.TryParse(["#########", "#S..#...#", "#...#...#", "#########"], out var map, out _));
        var engine = new CombatEngine(new Dice(new Random(1)), map);
        engine.Join("Marco");
        var lemur = engine.Summon(Bestiary.Lemur, 1, EnemyRank.Normal, new Pose(13, 3, 0));
        var home = lemur.Pose;
        engine.Tick(0.1, 0.1f, new Dictionary<string, Pose>());                              // Marco gioca da terminale
        engine.Tick(0.2, 0.1f, new Dictionary<string, Pose> { ["Marco"] = new(3, 3, 0) });    // c'è un muro in mezzo
        Assert.Equal(home, lemur.Pose);
    }

    [Fact]
    public void Levelling_up_heals_and_improves_the_fighter()
    {
        var engine = new CombatEngine(new Dice(new Random(1)), Hall());
        var marco = engine.Join("Marco");
        int health = marco.MaxHealth, melee = marco.MeleeBonus;
        var boss = engine.Summon(Bestiary.Guardian, 10, EnemyRank.Boss, new Pose(4.5f, 3, 0));
        List<CombatEvent> events = [];
        for (double now = 0; engine.EnemyOf(boss.Id) is not null && now < 2000; now += 1) events.AddRange(engine.Attack("Marco", West, boss.Id, false, now, out _));
        Assert.Contains(events, e => e is LevelUp);
        Assert.True(marco.Level > 1);
        Assert.True(marco.MaxHealth > health && marco.MeleeBonus >= melee);
        Assert.Equal(marco.MaxHealth, marco.Health);
    }
}
