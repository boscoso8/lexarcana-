namespace LexArcana.Game;

/// <summary>Che cosa c'è in una casella della mappa.</summary>
public enum Tile { Void, Floor, Wall, Column, Altar }

/// <summary>Una posizione nel mondo, in metri; Yaw è la direzione dello sguardo in radianti (0 = verso +Z).</summary>
public readonly record struct Pose(float X, float Z, float Yaw)
{
    public bool IsFinite => float.IsFinite(X) && float.IsFinite(Z) && float.IsFinite(Yaw);
    public float DistanceTo(Pose other) => MathF.Sqrt((X - other.X) * (X - other.X) + (Z - other.Z) * (Z - other.Z));
}

/// <summary>
/// Una mappa a caselle, scritta come testo. Ogni carattere è una casella di <see cref="CellSize"/> metri:
/// <c>#</c> muro, <c>.</c> pavimento, <c>C</c> colonna, <c>A</c> altare, <c>S</c> punto di partenza
/// (pavimento), spazio = vuoto. Una prima riga <c>nome: …</c> dà il nome al luogo.
/// </summary>
public sealed class GameMap
{
    public const float CellSize = 2f;
    public const float WallHeight = 3.2f;
    public const float PlayerRadius = 0.35f;
    public const int MaxSize = 96;

    private readonly Tile[,] _tiles;

    private GameMap(string name, string[] rows, Tile[,] tiles, Pose[] spawns)
    {
        Name = name;
        Rows = rows;
        _tiles = tiles;
        Spawns = spawns;
    }

    public string Name { get; }
    /// <summary>Il testo della mappa, una stringa per riga: è ciò che viaggia in rete.</summary>
    public string[] Rows { get; }
    public int Width => _tiles.GetLength(0);
    public int Depth => _tiles.GetLength(1);
    public Pose[] Spawns { get; }

    public Tile this[int x, int z] => x >= 0 && z >= 0 && x < Width && z < Depth ? _tiles[x, z] : Tile.Void;

    public static bool IsSolid(Tile tile) => tile is Tile.Wall or Tile.Column or Tile.Altar or Tile.Void;

    /// <summary>Il centro della casella (x, z) in metri.</summary>
    public static (float X, float Z) CellCenter(int x, int z) => ((x + 0.5f) * CellSize, (z + 0.5f) * CellSize);

    /// <summary>True se un personaggio può stare in (x, z) senza toccare caselle solide.</summary>
    public bool IsWalkable(float x, float z, float radius = PlayerRadius)
    {
        if (!float.IsFinite(x) || !float.IsFinite(z)) return false;
        int minX = (int)MathF.Floor((x - radius) / CellSize), maxX = (int)MathF.Floor((x + radius) / CellSize);
        int minZ = (int)MathF.Floor((z - radius) / CellSize), maxZ = (int)MathF.Floor((z + radius) / CellSize);
        for (int cx = minX; cx <= maxX; cx++)
            for (int cz = minZ; cz <= maxZ; cz++)
                if (IsSolid(this[cx, cz]) && CircleTouchesCell(x, z, radius, cx, cz)) return false;
        return true;
    }

    /// <summary>
    /// Muove da <paramref name="from"/> di (dx, dz) scivolando lungo i muri: prima prova l'asse X, poi Z.
    /// </summary>
    public Pose Slide(Pose from, float dx, float dz, float yaw)
    {
        float x = from.X, z = from.Z;
        if (IsWalkable(x + dx, z)) x += dx;
        if (IsWalkable(x, z + dz)) z += dz;
        return new(x, z, yaw);
    }

    /// <summary>True se da (x0, z0) si vede (x1, z1) ad altezza d'occhi: nessun muro o colonna in mezzo. L'altare è basso e non copre.</summary>
    public bool HasLineOfSight(float x0, float z0, float x1, float z1)
    {
        float dx = x1 - x0, dz = z1 - z0;
        float length = MathF.Sqrt(dx * dx + dz * dz);
        int steps = (int)MathF.Ceiling(length / (CellSize * 0.25f));
        for (int i = 1; i < steps; i++)
        {
            float t = (float)i / steps;
            var tile = this[(int)MathF.Floor((x0 + dx * t) / CellSize), (int)MathF.Floor((z0 + dz * t) / CellSize)];
            if (tile is Tile.Wall or Tile.Column or Tile.Void) return false;
        }
        return true;
    }

    private static bool CircleTouchesCell(float x, float z, float r, int cx, int cz)
    {
        float nearestX = Math.Clamp(x, cx * CellSize, (cx + 1) * CellSize);
        float nearestZ = Math.Clamp(z, cz * CellSize, (cz + 1) * CellSize);
        float dx = x - nearestX, dz = z - nearestZ;
        return dx * dx + dz * dz < r * r;
    }

    public static bool TryParse(IEnumerable<string> lines, out GameMap map, out string error)
    {
        map = null!;
        error = "";
        var name = "Luogo senza nome";
        List<string> rows = [];
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (rows.Count == 0 && line.StartsWith("nome:", StringComparison.OrdinalIgnoreCase))
            {
                name = line[5..].Sanitized.Trim() is { Length: > 0 } n ? n : name;
                continue;
            }
            if (rows.Count == 0 && line.Trim().Length == 0) continue;
            rows.Add(line);
        }
        while (rows.Count > 0 && rows[^1].Trim().Length == 0) rows.RemoveAt(rows.Count - 1);

        if (rows.Count == 0) return Fail("La mappa è vuota.", out error);
        int width = rows.Max(r => r.Length);
        if (width > MaxSize || rows.Count > MaxSize) return Fail($"La mappa può essere al massimo {MaxSize}×{MaxSize} caselle.", out error);

        var tiles = new Tile[width, rows.Count];
        List<Pose> spawns = [];
        List<Pose> floors = [];
        for (int z = 0; z < rows.Count; z++)
            for (int x = 0; x < width; x++)
            {
                char c = x < rows[z].Length ? rows[z][x] : ' ';
                Tile? tile = c switch
                {
                    '#' => Tile.Wall,
                    '.' or 'S' or 's' => Tile.Floor,
                    'C' or 'c' => Tile.Column,
                    'A' or 'a' => Tile.Altar,
                    ' ' => Tile.Void,
                    _ => null,
                };
                if (tile is not { } known)
                    return Fail($"Carattere «{c}» non valido alla riga {z + 1}, colonna {x + 1}. Usa # . C A S o spazio.", out error);
                tiles[x, z] = known;
                var (cxm, czm) = CellCenter(x, z);
                if (c is 'S' or 's') spawns.Add(new(cxm, czm, 0));
                if (tiles[x, z] == Tile.Floor) floors.Add(new(cxm, czm, 0));
            }

        if (floors.Count == 0) return Fail("La mappa non ha pavimento su cui stare.", out error);

        // Chi entra guarda verso il centro del pavimento.
        float centerX = floors.Average(f => f.X), centerZ = floors.Average(f => f.Z);
        Pose[] starts = [.. (spawns.Count > 0 ? spawns : [floors[0]])
            .Select(p => p with { Yaw = MathF.Atan2(centerX - p.X, centerZ - p.Z) })];
        map = new GameMap(name, [.. rows], tiles, starts);
        return true;

        static bool Fail(string message, out string error)
        {
            error = message;
            return false;
        }
    }

    /// <summary>La mappa usata quando il Demiurgo non ne sceglie una.</summary>
    public static GameMap Default { get; } = Parse(DefaultText);

    private static GameMap Parse(string text) =>
        TryParse(text.Split('\n'), out var map, out var error) ? map : throw new InvalidOperationException(error);

    public const string DefaultText = """
        nome: Tempio di Giano
        ###############
        #.............#
        #.C...C.C...C.#
        #.............#
        #......A......#
        #.............#
        #.C...C.C...C.#
        #.............#
        ######...######
             #...#
             #.S.#
             #S.S#
             #####
        """;
}
