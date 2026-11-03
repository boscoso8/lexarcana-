using LexArcana.Game;

namespace LexArcana.Campaigns;

/// <summary>Il tipo di luogo di un'avventura: decide la forma della mappa.</summary>
public enum PlaceKind { Tempio, Foro, Villa, Catacombe, Necropoli, Miniera, Accampamento }

/// <summary>
/// Genera la mappa di un luogo nel formato di <see cref="GameMap"/> (# muro, . pavimento, C colonna, A altare, S partenza).
/// Ogni mappa ha tre punti di partenza e un altare: il cuore del luogo, dove di solito si chiude l'avventura.
/// Ogni casella di pavimento è raggiungibile dalle partenze.
/// </summary>
public static class MapGenerator
{
    public static string[] Generate(PlaceKind kind, Random random) => kind switch
    {
        PlaceKind.Tempio => Hall(random, forum: false),
        PlaceKind.Foro => Hall(random, forum: true),
        PlaceKind.Villa => Peristyle(random),
        PlaceKind.Accampamento => Camp(random),
        _ => Rooms(random, kind),
    };

    // ---- Sala con colonnato (tempio, foro)

    private static string[] Hall(Random random, bool forum)
    {
        int w = Odd(random, forum ? 21 : 15, forum ? 27 : 21), d = random.Next(forum ? 13 : 10, forum ? 17 : 14);
        var g = new Grid(w, d + 5);
        g.Fill(1, 1, w - 2, d - 2, '.');
        int mid = w / 2;
        for (int z = 2; z <= d - 3; z += 2)
        {
            g[2, z] = 'C';
            g[w - 3, z] = 'C';
            if (forum && z > 2 && z < d - 3) { g[6, z] = 'C'; g[w - 7, z] = 'C'; }
        }
        if (forum)
        {
            // un recinto sacro al centro, con l'ingresso verso sud
            int top = 3, bottom = Math.Min(d - 4, 8);
            g.Rect(mid - 3, top, mid + 3, bottom, '#');
            g.Fill(mid - 2, top + 1, mid + 2, bottom - 1, '.');
            g[mid, bottom] = '.';
            g[mid, top + 2] = 'A';
        }
        else
        {
            g[mid, 3] = 'A';
            for (int z = 5; z < d - 2; z += 3) { g[mid - 2, z] = 'C'; g[mid + 2, z] = 'C'; }
        }
        // corridoio d'ingresso a sud con le partenze
        g.Fill(mid - 1, d - 1, mid + 1, d + 3, '.');
        g[mid, d + 2] = 'S';
        g[mid - 1, d + 3] = 'S';
        g[mid + 1, d + 3] = 'S';
        return g.Finish();
    }

    // ---- Villa con peristilio

    private static string[] Peristyle(Random random)
    {
        int w = Odd(random, 23, 29), d = Odd(random, 19, 23);
        var g = new Grid(w, d + 3);
        g.Fill(1, 1, w - 2, d - 2, '.');

        // il cortile, circondato da un muro con quattro aperture e da un anello di colonne
        int inset = 5;
        int x0 = inset, z0 = inset, x1 = w - 1 - inset, z1 = d - 1 - inset;
        g.Rect(x0 - 1, z0 - 1, x1 + 1, z1 + 1, '#');
        g[(x0 + x1) / 2, z0 - 1] = '.';
        g[(x0 + x1) / 2, z1 + 1] = '.';
        g[x0 - 1, (z0 + z1) / 2] = '.';
        g[x1 + 1, (z0 + z1) / 2] = '.';
        for (int x = x0 + 1; x <= x1 - 1; x += 2) { g[x, z0 + 1] = 'C'; g[x, z1 - 1] = 'C'; }
        for (int z = z0 + 3; z <= z1 - 3; z += 2) { g[x0 + 1, z] = 'C'; g[x1 - 1, z] = 'C'; }
        g[(x0 + x1) / 2, (z0 + z1) / 2] = 'A';  // il larario al centro del cortile

        // stanze lungo i lati nord e sud, separate da muri con una porta
        foreach (var (top, bottom) in new[] { (1, z0 - 2), (z1 + 2, d - 2) })
            for (int x = 1 + random.Next(4, 7); x < w - 4; x += random.Next(4, 7))
            {
                for (int z = top; z <= bottom; z++) g[x, z] = '#';
                g[x, top == 1 ? bottom : top] = '.';
            }

        // l'ingresso al centro del lato sud
        int mid = w / 2;
        for (int z = z1 + 2; z <= d - 2; z++) g[mid, z] = g[mid, z] == '#' ? '.' : g[mid, z];
        g[mid, d - 1] = '.';
        g.Fill(mid - 1, d, mid + 1, d + 2, '.');
        g[mid, d + 1] = 'S';
        g[mid - 1, d + 2] = 'S';
        g[mid + 1, d + 2] = 'S';
        return g.Finish();
    }

    // ---- Accampamento: baracche in griglia dentro la cinta, il sacello al centro

    private static string[] Camp(Random random)
    {
        int blocksX = random.Next(3, 5), blocksZ = random.Next(3, 4);
        int bw = 4, bd = 3, road = 2;
        int w = 2 + blocksX * bw + (blocksX + 1) * road, d = 2 + blocksZ * bd + (blocksZ + 1) * road;
        if (blocksX % 2 == 0) w++; // una via centrale da nord a sud
        var g = new Grid(w, d + 3);
        g.Fill(1, 1, w - 2, d - 2, '.');
        int mid = w / 2;
        for (int bx = 0; bx < blocksX; bx++)
            for (int bz = 0; bz < blocksZ; bz++)
            {
                int x = 1 + road + bx * (bw + road) + (blocksX % 2 == 0 && bx >= blocksX / 2 ? 1 : 0);
                int z = 1 + road + bz * (bd + road);
                if (x <= mid && x + bw - 1 >= mid) continue; // la via principale resta libera
                if (random.Next(5) > 0) g.Fill(x, z, x + bw - 1, z + bd - 1, '#');
            }
        g[mid, d / 2] = 'A';
        g[mid - 1, d / 2 - 1] = 'C';
        g[mid + 1, d / 2 - 1] = 'C';
        // la porta sud
        g.Fill(mid - 1, d - 1, mid + 1, d + 1, '.');
        g[mid, d] = 'S';
        g[mid - 1, d + 1] = 'S';
        g[mid + 1, d + 1] = 'S';
        return g.Finish();
    }

    // ---- Stanze e cunicoli (catacombe, necropoli, miniera)

    private readonly record struct Room(int X, int Z, int W, int D)
    {
        public int CenterX => X + W / 2;
        public int CenterZ => Z + D / 2;
        public bool Overlaps(Room o, int margin) =>
            X - margin < o.X + o.W && o.X - margin < X + W && Z - margin < o.Z + o.D && o.Z - margin < Z + D;
    }

    private static string[] Rooms(Random random, PlaceKind kind)
    {
        int w = random.Next(34, 44), d = random.Next(26, 34);
        bool mine = kind == PlaceKind.Miniera;
        var g = new Grid(w, d);
        List<Room> rooms = [];
        int wanted = random.Next(6, 10);
        for (int attempt = 0; attempt < 400 && rooms.Count < wanted; attempt++)
        {
            int rw = random.Next(mine ? 3 : 4, mine ? 7 : 9), rd = random.Next(mine ? 3 : 4, mine ? 6 : 8);
            var room = new Room(random.Next(2, w - rw - 2), random.Next(2, d - rd - 2), rw, rd);
            if (rooms.All(r => !r.Overlaps(room, 2))) rooms.Add(room);
        }

        // si collegano in ordine da ovest a est, più qualche passaggio in più per non fare un corridoio unico
        rooms.Sort((a, b) => a.CenterX.CompareTo(b.CenterX));
        foreach (var r in rooms) g.Fill(r.X, r.Z, r.X + r.W - 1, r.Z + r.D - 1, '.');
        for (int i = 1; i < rooms.Count; i++) Corridor(g, random, rooms[i - 1], rooms[i]);
        for (int extra = random.Next(1, 3); extra > 0 && rooms.Count > 3; extra--)
            Corridor(g, random, rooms[random.Next(rooms.Count)], rooms[random.Next(rooms.Count)]);

        // pilastri o tombe nelle stanze grandi, mai dove passa un corridoio
        foreach (var r in rooms.Where(r => r.W >= 6 && r.D >= 6))
        {
            if (kind == PlaceKind.Necropoli)
                for (int x = r.X + 1; x < r.X + r.W - 1; x += 2) { Pillar(g, x, r.Z + 1); Pillar(g, x, r.Z + r.D - 2); }
            else
            {
                Pillar(g, r.X + 1, r.Z + 1); Pillar(g, r.X + r.W - 2, r.Z + 1);
                Pillar(g, r.X + 1, r.Z + r.D - 2); Pillar(g, r.X + r.W - 2, r.Z + r.D - 2);
            }
        }

        var start = rooms[0];
        g[start.CenterX, start.CenterZ] = 'S';
        g[start.CenterX - 1, start.CenterZ] = 'S';
        g[start.CenterX + 1, start.CenterZ] = 'S';
        var heart = rooms.MaxBy(r => Math.Abs(r.CenterX - start.CenterX) + Math.Abs(r.CenterZ - start.CenterZ));
        g[heart.CenterX, heart.CenterZ] = 'A';
        return g.Finish();
    }

    private static void Corridor(Grid g, Random random, Room a, Room b)
    {
        int x = a.CenterX, z = a.CenterZ;
        bool horizontalFirst = random.Next(2) == 0;
        void Step(int tx, int tz)
        {
            while (x != tx) { x += Math.Sign(tx - x); if (g[x, z] != 'C') g[x, z] = '.'; }
            while (z != tz) { z += Math.Sign(tz - z); if (g[x, z] != 'C') g[x, z] = '.'; }
        }
        if (horizontalFirst) { Step(b.CenterX, z); Step(x, b.CenterZ); }
        else { Step(x, b.CenterZ); Step(b.CenterX, z); }
    }

    /// <summary>Mette una colonna solo dove non chiude un passaggio: tutte le caselle intorno devono essere pavimento.</summary>
    private static void Pillar(Grid g, int x, int z)
    {
        for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
                if (g[x + dx, z + dz] != '.') return;
        g[x, z] = 'C';
    }

    private static int Odd(Random random, int min, int max) => random.Next(min, max + 1) | 1;

    /// <summary>Una griglia di caratteri; alla fine circonda il pavimento di muri e toglie i bordi vuoti.</summary>
    private sealed class Grid(int width, int depth)
    {
        private readonly char[,] _cells = Init(width, depth);

        private static char[,] Init(int w, int h)
        {
            var c = new char[w, h];
            for (int x = 0; x < w; x++) for (int z = 0; z < h; z++) c[x, z] = ' ';
            return c;
        }

        public char this[int x, int z]
        {
            get => x >= 0 && z >= 0 && x < width && z < depth ? _cells[x, z] : ' ';
            set { if (x >= 0 && z >= 0 && x < width && z < depth) _cells[x, z] = value; }
        }

        public void Fill(int x0, int z0, int x1, int z1, char c)
        {
            for (int x = x0; x <= x1; x++) for (int z = z0; z <= z1; z++) this[x, z] = c;
        }

        public void Rect(int x0, int z0, int x1, int z1, char c)
        {
            for (int x = x0; x <= x1; x++) { this[x, z0] = c; this[x, z1] = c; }
            for (int z = z0; z <= z1; z++) { this[x0, z] = c; this[x1, z] = c; }
        }

        public string[] Finish()
        {
            // ogni casella vuota che tocca uno spazio aperto diventa muro
            var walls = new List<(int, int)>();
            for (int x = -1; x <= width; x++)
                for (int z = -1; z <= depth; z++)
                    if (this[x, z] == ' ' && Neighbours(x, z).Any(n => n is '.' or 'S' or 'C' or 'A')) walls.Add((x, z));

            // si allarga di uno per far posto ai muri del bordo
            var rows = new List<string>();
            for (int z = -1; z <= depth; z++)
            {
                var row = new char[width + 2];
                for (int x = -1; x <= width; x++)
                    row[x + 1] = walls.Contains((x, z)) ? '#' : this[x, z];
                rows.Add(new string(row).TrimEnd());
            }
            while (rows.Count > 0 && rows[0].Length == 0) rows.RemoveAt(0);
            while (rows.Count > 0 && rows[^1].Length == 0) rows.RemoveAt(rows.Count - 1);
            int left = rows.Where(r => r.Length > 0).Min(r => r.Length - r.TrimStart().Length);
            return [.. rows.Select(r => r.Length >= left ? r[left..] : r)];
        }

        private IEnumerable<char> Neighbours(int x, int z)
        {
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                    if (dx != 0 || dz != 0) yield return this[x + dx, z + dz];
        }
    }
}
