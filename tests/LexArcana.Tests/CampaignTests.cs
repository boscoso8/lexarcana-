using LexArcana.Campaigns;
using LexArcana.Game;

namespace LexArcana.Tests;

public class CampaignTests
{
    public static TheoryData<PlaceKind> Kinds => [.. Enum.GetValues<PlaceKind>()];

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Generated_maps_are_valid_with_three_starts_one_altar_and_no_unreachable_floor(PlaceKind kind)
    {
        for (int seed = 0; seed < 60; seed++)
        {
            var rows = MapGenerator.Generate(kind, new Random(seed));
            Assert.True(GameMap.TryParse(rows, out var map, out var error), $"{kind} seme {seed}: {error}");
            Assert.Equal(3, map.Spawns.Length);
            Assert.Equal(1, rows.Sum(r => r.Count(c => c == 'A')));
            Assert.All(map.Spawns, s => Assert.True(map.IsWalkable(s.X, s.Z), $"{kind} seme {seed}: partenza dentro un muro"));

            var unreachable = UnreachableFloor(map);
            Assert.True(unreachable.Count == 0, $"{kind} seme {seed}: {unreachable.Count} caselle irraggiungibili\n{string.Join('\n', rows)}");
            Assert.True(AltarReachable(map), $"{kind} seme {seed}: l'altare non si raggiunge\n{string.Join('\n', rows)}");
        }
    }

    /// <summary>Le caselle di pavimento che non si raggiungono camminando dalla prima partenza.</summary>
    private static List<(int, int)> UnreachableFloor(GameMap map)
    {
        var seen = Flood(map);
        List<(int, int)> missing = [];
        for (int x = 0; x < map.Width; x++)
            for (int z = 0; z < map.Depth; z++)
                if (map[x, z] == Tile.Floor && !seen.Contains((x, z))) missing.Add((x, z));
        return missing;
    }

    private static bool AltarReachable(GameMap map)
    {
        var seen = Flood(map);
        for (int x = 0; x < map.Width; x++)
            for (int z = 0; z < map.Depth; z++)
                if (map[x, z] == Tile.Altar)
                    return new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }.Any(d => seen.Contains((x + d.Item1, z + d.Item2)));
        return false;
    }

    private static HashSet<(int, int)> Flood(GameMap map)
    {
        var start = ((int)(map.Spawns[0].X / GameMap.CellSize), (int)(map.Spawns[0].Z / GameMap.CellSize));
        var seen = new HashSet<(int, int)> { start };
        var queue = new Queue<(int X, int Z)>([start]);
        while (queue.TryDequeue(out var c))
            foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                var n = (c.X + dx, c.Z + dz);
                if (map[n.Item1, n.Item2] == Tile.Floor && seen.Add(n)) queue.Enqueue(n);
            }
        return seen;
    }

    [Fact]
    public void Campaigns_are_reproducible_and_end_in_the_villain_s_lair()
    {
        var a = CampaignGenerator.Generate(42, 5);
        var b = CampaignGenerator.Generate(42, 5);
        Assert.Equal(a.ToJson(), b.ToJson());
        Assert.NotEqual(a.ToJson(), CampaignGenerator.Generate(43, 5).ToJson());

        Assert.Equal(5, a.Adventures.Length);
        Assert.Equal([1, 2, 3, 4, 5], a.Adventures.Select(x => x.Number));
        Assert.Equal(a.Adventures.Length, a.Adventures.Select(x => x.Kind).Distinct().Count());   // luoghi tutti diversi
        Assert.Equal(a.Adventures.Length, a.Adventures.Select(x => x.Title).Distinct().Count());  // titoli tutti diversi
        Assert.Contains(a.Villain.Split(',')[0], a.Adventures[^1].Objective, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_generated_campaign_has_sound_text_and_valid_maps()
    {
        for (int seed = 0; seed < 80; seed++)
        {
            var c = CampaignGenerator.Generate(seed, 3 + seed % 5);
            Assert.InRange(c.Adventures.Length, CampaignGenerator.MinAdventures, CampaignGenerator.MaxAdventures);
            foreach (var adv in c.Adventures)
            {
                Assert.True(GameMap.TryParse([$"nome: {adv.Place}", .. adv.Map], out var map, out var error), error);
                Assert.Equal(adv.Place, map.Name);
                Assert.InRange(adv.Npcs.Length, 2, 3);
                Assert.Equal(adv.Npcs.Length, adv.Npcs.Select(n => n.Role).Distinct().Count());
                // niente preposizioni non articolate davanti a un articolo: "a la", "di il", "de il"…
                foreach (var text in new[] { adv.Hook, adv.Objective, adv.Climax, adv.Title, c.Premise })
                    foreach (var bad in new[] { " a il ", " a la ", " a le ", " a i ", " di il ", " di la ", " di le ", " de ", " in il ", " in la ", " prima di la ", " a l'" })
                        Assert.DoesNotContain(bad, " " + text + " ", StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Campaigns_round_trip_and_are_written_as_handbook_json_and_maps()
    {
        var c = CampaignGenerator.Generate(7, 3);
        Assert.Equal(c.ToJson(), Campaign.FromJson(c.ToJson()).ToJson());

        var dir = Path.Combine(Path.GetTempPath(), "lexarcana-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var files = CampaignFiles.Write(c, dir);
            Assert.Equal(2 + c.Adventures.Length, files.Count);
            var md = File.ReadAllText(Path.Combine(dir, "campagna.md"));
            Assert.StartsWith($"# {c.Title}", md, StringComparison.Ordinal);
            Assert.True(GameMap.TryParse(File.ReadAllLines(Path.Combine(dir, "avventura-2.txt")), out var map, out _));
            Assert.Equal(c.Adventures[1].Place, map.Name);
            Assert.True(CampaignFiles.TryLoad(Path.Combine(dir, "campagna.json"), out var loaded, out _));
            Assert.Equal(c.Title, loaded.Title);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
