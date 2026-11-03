using LexArcana.Game;

namespace LexArcana.Tests;

public class MapTests
{
    private static GameMap Room()
    {
        Assert.True(GameMap.TryParse(["nome: Stanza", "#####", "#.C.#", "#S..#", "#####"], out var map, out var error), error);
        return map;
    }

    [Fact]
    public void Parses_name_tiles_and_spawns()
    {
        var map = Room();
        Assert.Equal("Stanza", map.Name);
        Assert.Equal((5, 4), (map.Width, map.Depth));
        Assert.Equal(Tile.Column, map[2, 1]);
        Assert.Equal(Tile.Void, map[-1, 0]);
        var spawn = Assert.Single(map.Spawns);
        Assert.Equal(GameMap.CellCenter(1, 2), (spawn.X, spawn.Z));
    }

    [Theory]
    [InlineData(new string[0], "vuota")]
    [InlineData(new[] { "###", "#x#", "###" }, "«x»")]
    [InlineData(new[] { "###", "###" }, "pavimento")]
    public void Rejects_bad_maps(string[] rows, string reason)
    {
        Assert.False(GameMap.TryParse(rows, out _, out var error));
        Assert.Contains(reason, error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_default_map_is_valid_and_faces_new_players_into_the_hall()
    {
        var map = GameMap.Default;
        Assert.Equal("Tempio di Giano", map.Name);
        Assert.Equal(3, map.Spawns.Length);
        Assert.All(map.Spawns, s => Assert.True(map.IsWalkable(s.X, s.Z)));
        // le partenze sono in fondo al corridoio (z grande): guardano verso nord, cioè con coseno negativo
        Assert.All(map.Spawns, s => Assert.True(MathF.Cos(s.Yaw) < -0.7f));
    }

    [Fact]
    public void The_example_maps_in_the_repository_are_valid()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "mappe");
        var files = Directory.GetFiles(dir, "*.txt");
        Assert.NotEmpty(files);
        foreach (var file in files)
            Assert.True(GameMap.TryParse(File.ReadAllLines(file), out _, out var error), $"{Path.GetFileName(file)}: {error}");
    }

    [Fact]
    public void Walls_and_columns_block_movement_and_sliding_follows_the_wall()
    {
        var map = Room();
        var (x, z) = GameMap.CellCenter(1, 2);
        Assert.True(map.IsWalkable(x, z));
        Assert.False(map.IsWalkable(x - 0.8f, z));                     // dentro il muro a ovest
        Assert.False(map.IsWalkable(GameMap.CellCenter(2, 1).X, GameMap.CellCenter(2, 1).Z)); // la colonna
        Assert.False(map.IsWalkable(float.NaN, z));

        // camminando in diagonale contro il muro ovest si scivola solo lungo Z
        var slid = map.Slide(new Pose(x, z, 0), -1.5f, -0.5f, 0);
        Assert.Equal(x, slid.X);
        Assert.Equal(z - 0.5f, slid.Z, 3);
    }

    [Fact]
    public void Line_of_sight_is_blocked_by_columns_but_not_by_open_floor()
    {
        var map = Room();
        var a = GameMap.CellCenter(1, 1);
        var b = GameMap.CellCenter(3, 1);
        var c = GameMap.CellCenter(3, 2);
        Assert.False(map.HasLineOfSight(a.X, a.Z, b.X, b.Z)); // la colonna in (2, 1) è in mezzo
        Assert.True(map.HasLineOfSight(GameMap.CellCenter(1, 2).X, GameMap.CellCenter(1, 2).Z, c.X, c.Z));
    }
}
