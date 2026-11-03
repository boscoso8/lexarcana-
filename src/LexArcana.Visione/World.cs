using System.Numerics;
using Raylib_cs;
using LexArcana.Game;

namespace LexArcana.Visione;

/// <summary>Disegna il luogo (pavimento, muri, colonne, altare) e i personaggi.</summary>
internal sealed unsafe class World : IDisposable
{
    private readonly GameMap _map;
    private readonly Lighting _lighting;
    private readonly Model _cube, _cylinder, _sphere, _cone;
    private readonly List<(int X, int Z)> _walls = [], _columns = [], _altars = [];

    public World(GameMap map, Lighting lighting)
    {
        _map = map;
        _lighting = lighting;
        _cube = Load(Raylib.GenMeshCube(1, 1, 1));
        _cylinder = Load(Raylib.GenMeshCylinder(1, 1, 28));
        _sphere = Load(Raylib.GenMeshSphere(1, 18, 18));
        _cone = Load(Raylib.GenMeshCone(1, 1, 20));

        for (int x = 0; x < map.Width; x++)
            for (int z = 0; z < map.Depth; z++)
                switch (map[x, z])
                {
                    // Un muro circondato da altri muri non si vede mai: non lo disegniamo.
                    case Tile.Wall when TouchesOpenSpace(x, z): _walls.Add((x, z)); break;
                    case Tile.Column: _columns.Add((x, z)); break;
                    case Tile.Altar: _altars.Add((x, z)); break;
                }
    }

    /// <summary>Le luci fisse del luogo: un fuoco su ogni altare, che tremola.</summary>
    public IEnumerable<(Vector3 Position, Vector3 Color, float Range)> Fires(double time)
    {
        foreach (var (x, z) in _altars)
        {
            var (cx, cz) = GameMap.CellCenter(x, z);
            float flicker = 0.85f + 0.1f * MathF.Sin((float)time * 9.1f + x) + 0.05f * MathF.Sin((float)time * 23.7f + z);
            yield return (new Vector3(cx, 1.8f, cz), new Vector3(1.15f, 0.55f, 0.28f) * flicker, 16f);
        }
    }

    public void Draw(Vector3 eye, double time)
    {
        float s = GameMap.CellSize, h = GameMap.WallHeight;

        // Pavimento: una sola lastra, le fughe le disegna lo shader.
        _lighting.Pattern(true);
        Box(new(_map.Width * s / 2, -0.1f, _map.Depth * s / 2), new(_map.Width * s, 0.2f, _map.Depth * s), Theme.Floor);
        _lighting.Pattern(false);

        foreach (var (x, z) in _walls)
        {
            var (cx, cz) = GameMap.CellCenter(x, z);
            if (!Near(eye, cx, cz)) continue;
            Box(new(cx, 0.15f, cz), new(s, 0.3f, s), Theme.Basalt);                    // zoccolo
            Box(new(cx, 0.3f + (h - 0.8f) / 2, cz), new(s, h - 0.8f, s), Theme.Stone);  // parete
            Box(new(cx, h - 0.375f, cz), new(s + 0.02f, 0.25f, s + 0.02f), Theme.Fresco); // fregio
            Box(new(cx, h - 0.125f, cz), new(s + 0.1f, 0.25f, s + 0.1f), Theme.Stone);  // cornice
        }

        foreach (var (x, z) in _columns)
        {
            var (cx, cz) = GameMap.CellCenter(x, z);
            if (!Near(eye, cx, cz)) continue;
            Box(new(cx, 0.1f, cz), new(1.0f, 0.2f, 1.0f), Theme.Marble);
            Box(new(cx, 0.26f, cz), new(0.82f, 0.12f, 0.82f), Theme.Marble);
            Cylinder(new(cx, 0.32f, cz), 0.32f, h - 0.82f, Theme.Marble);
            Box(new(cx, h - 0.4f, cz), new(0.78f, 0.16f, 0.78f), Theme.Gilt);          // capitello
            Box(new(cx, h - 0.24f, cz), new(1.0f, 0.16f, 1.0f), Theme.Marble);         // abaco
        }

        foreach (var (x, z) in _altars)
        {
            var (cx, cz) = GameMap.CellCenter(x, z);
            if (!Near(eye, cx, cz)) continue;
            Box(new(cx, 0.1f, cz), new(1.9f, 0.2f, 1.9f), Theme.Marble);
            Box(new(cx, 0.6f, cz), new(1.4f, 0.8f, 1.4f), Theme.Basalt);
            Box(new(cx, 1.05f, cz), new(1.65f, 0.1f, 1.65f), Theme.Gilt);
            float t = (float)time;
            float lick = 1f + 0.12f * MathF.Sin(t * 11f) + 0.06f * MathF.Sin(t * 27f);
            _lighting.Emissive(true);
            for (int i = 0; i < 5; i++)
            {
                // cinque lingue di fuoco che tremolano ognuna per conto suo
                float a = i * MathF.Tau / 5 + t * 0.7f;
                var offset = new Vector3(MathF.Cos(a), 0, MathF.Sin(a)) * 0.16f;
                float tongue = 0.35f + 0.18f * MathF.Sin(t * (7f + i * 1.7f) + i * 2.1f);
                Cone(new Vector3(cx, 1.1f, cz) + offset, 0.11f, tongue * lick, t * 30f + i * 40f, Theme.Flame);
            }
            Cone(new(cx, 1.1f, cz), 0.17f, 0.5f * (2f - lick), -t * 70f, Theme.Gilt);
            Sphere(new(cx, 1.14f, cz), 0.16f, Theme.Flame);
            _lighting.Emissive(false);
        }
    }

    /// <summary>Un personaggio: una statua di marmo con la tunica del suo colore e una fascia scura sugli occhi che dice dove guarda.</summary>
    public void DrawAvatar(Avatar avatar)
    {
        var p = new Vector3(avatar.Position.X, 0, avatar.Position.Y);
        float angle = avatar.Yaw * 180f / MathF.PI;
        var forward = new Vector3(MathF.Sin(avatar.Yaw), 0, MathF.Cos(avatar.Yaw));
        float bob = MathF.Abs(MathF.Sin(avatar.Phase)) * 0.05f * avatar.Stride;
        var tunic = Theme.TunicFor(avatar.Name);

        Cylinder(p, 0.34f, 0.1f, Theme.Basalt);                                            // piedistallo
        Cone(p + new Vector3(0, 0.1f + bob, 0), 0.34f, 1.45f, angle, tunic);               // veste, stretta in alto: la punta resta dentro la testa
        Cylinder(p + new Vector3(0, 0.1f + bob, 0), 0.335f, 0.08f, Theme.Gilt);            // orlo
        Rotated(p + new Vector3(0, 1.2f + bob, 0), new(0.5f, 0.14f, 0.24f), angle, tunic); // spalle
        Rotated(p + new Vector3(0, 1.0f + bob, 0) + forward * 0.06f, new(0.08f, 0.5f, 0.2f), angle - 35f, Theme.Marble); // braccio e mantello
        Cylinder(p + new Vector3(0, 1.27f + bob, 0), 0.07f, 0.12f, Theme.Marble);          // collo
        Sphere(p + new Vector3(0, 1.5f + bob, 0), 0.15f, Theme.Marble);                    // testa
        Rotated(p + new Vector3(0, 1.53f + bob, 0) + forward * 0.12f, new(0.2f, 0.04f, 0.07f), angle, Theme.Basalt); // occhi
        Cylinder(p + new Vector3(0, 1.6f + bob, 0), 0.155f, 0.045f, Theme.Gilt);           // corona d'alloro
    }

    private bool TouchesOpenSpace(int x, int z)
    {
        for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
                if (_map[x + dx, z + dz] is Tile.Floor or Tile.Column or Tile.Altar) return true;
        return false;
    }

    private static bool Near(Vector3 eye, float x, float z) =>
        (eye.X - x) * (eye.X - x) + (eye.Z - z) * (eye.Z - z) < Lighting.VisibleDistance * Lighting.VisibleDistance;

    private void Box(Vector3 center, Vector3 size, Color color) =>
        Raylib.DrawModelEx(_cube, center, Vector3.UnitY, 0, size, color);

    private void Rotated(Vector3 center, Vector3 size, float degrees, Color color) =>
        Raylib.DrawModelEx(_cube, center, Vector3.UnitY, degrees, size, color);

    private void Cylinder(Vector3 bottom, float radius, float height, Color color) =>
        Raylib.DrawModelEx(_cylinder, bottom, Vector3.UnitY, 0, new(radius, height, radius), color);

    private void Cone(Vector3 bottom, float radius, float height, float degrees, Color color) =>
        Raylib.DrawModelEx(_cone, bottom, Vector3.UnitY, degrees, new(radius, height, radius), color);

    private void Sphere(Vector3 center, float radius, Color color) =>
        Raylib.DrawModelEx(_sphere, center, Vector3.UnitY, 0, new(radius), color);

    private Model Load(Mesh mesh)
    {
        var model = Raylib.LoadModelFromMesh(mesh);
        model.Materials[0].Shader = _lighting.Shader;
        return model;
    }

    public void Dispose()
    {
        foreach (var model in new[] { _cube, _cylinder, _sphere, _cone })
        {
            model.Materials[0].Shader = default; // lo shader lo libera Lighting
            Raylib.UnloadModel(model);
        }
    }
}
