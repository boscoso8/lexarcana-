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

    /// <summary>Un nemico, disegnato secondo il suo tipo. Lampeggia di bianco se è appena stato colpito e affonda quando muore.</summary>
    public void DrawEnemy(EnemyView e, double time)
    {
        float t = (float)time;
        float dying = e.DiedAt is { } died ? (float)Math.Clamp((time - died) / 1.2, 0, 1) : 0;
        float scale = e.Scale * (1f - dying * 0.4f);
        var p = new Vector3(e.Position.X, -dying * 1.2f, e.Position.Y);
        bool flash = time < e.FlashUntil;
        Color C(Color c) => flash ? Theme.Marble : c;
        float lunge = time - e.AttackAt < 0.35 ? MathF.Sin((float)(time - e.AttackAt) / 0.35f * MathF.PI) * 0.35f : 0f;
        float bob = MathF.Abs(MathF.Sin(e.Phase)) * 0.06f * e.Stride;
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitY, e.Yaw);
        Vector3 L(float x, float y, float z) => p + Vector3.Transform(new Vector3(x, y, z + lunge) * scale, q);
        void Part(Vector3 center, Vector3 size, Color color, Quaternion? extra = null) =>
            Oriented(_cube, center, extra is { } r ? Quaternion.Concatenate(r, q) : q, size * scale, C(color));

        switch (e.Kind)
        {
            case "lupo":
            {
                float gait = MathF.Sin(e.Phase * 2.2f) * 0.25f * e.Stride;
                Part(L(0, 0.55f + bob, 0), new(0.38f, 0.42f, 0.95f), Theme.Basalt);
                Part(L(0, 0.72f + bob, 0.6f), new(0.28f, 0.3f, 0.38f), Theme.Basalt);
                Part(L(0, 0.66f + bob, 0.85f), new(0.16f, 0.14f, 0.22f), Theme.Basalt);          // muso
                Part(L(0, 0.6f, -0.55f), new(0.08f, 0.08f, 0.4f), Theme.Basalt, Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.6f)); // coda
                foreach (var (lx, lz, phase) in new[] { (0.14f, 0.32f, 1f), (-0.14f, 0.32f, -1f), (0.14f, -0.32f, -1f), (-0.14f, -0.32f, 1f) })
                    Part(L(lx, 0.22f, lz + gait * phase), new(0.09f, 0.44f, 0.09f), Theme.Basalt);
                _lighting.Emissive(true);
                Sphere(L(0.08f, 0.8f + bob, 0.8f), 0.035f * scale, Theme.Flame);
                Sphere(L(-0.08f, 0.8f + bob, 0.8f), 0.035f * scale, Theme.Flame);
                _lighting.Emissive(false);
                break;
            }
            case "strige":
            {
                float hover = 1.7f + MathF.Sin(t * 2.1f + e.Id) * 0.15f;
                float flap = MathF.Sin(t * 14f + e.Id) * 0.7f;
                Sphere(L(0, hover, 0), 0.22f * scale, C(Theme.Basalt));
                Sphere(L(0, hover + 0.25f, 0.12f), 0.14f * scale, C(Theme.Marble));                // volto pallido
                Part(L(0.38f, hover + 0.05f, 0), new(0.6f, 0.03f, 0.3f), Theme.Fresco, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, flap));
                Part(L(-0.38f, hover + 0.05f, 0), new(0.6f, 0.03f, 0.3f), Theme.Fresco, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -flap));
                Part(L(0.06f, hover - 0.28f, 0.05f), new(0.04f, 0.18f, 0.04f), Theme.Gilt);       // artigli di bronzo
                Part(L(-0.06f, hover - 0.28f, 0.05f), new(0.04f, 0.18f, 0.04f), Theme.Gilt);
                break;
            }
            case "scheletro":
            {
                float gait = MathF.Sin(e.Phase * 2f) * 0.2f * e.Stride;
                Part(L(0.12f, 0.45f, gait), new(0.07f, 0.9f, 0.07f), Theme.Marble);
                Part(L(-0.12f, 0.45f, -gait), new(0.07f, 0.9f, 0.07f), Theme.Marble);
                Part(L(0, 1.25f + bob, 0), new(0.4f, 0.55f, 0.18f), Theme.Marble);              // costole
                Part(L(0, 0.98f + bob, 0), new(0.36f, 0.08f, 0.2f), Theme.Fresco);              // cintura rossa
                Sphere(L(0, 1.72f + bob, 0), 0.15f * scale, C(Theme.Marble));
                Part(L(0, 1.85f + bob, 0), new(0.34f, 0.08f, 0.34f), Theme.Gilt);               // elmo
                Part(L(0, 1.98f + bob, 0), new(0.04f, 0.16f, 0.3f), Theme.Fresco);              // cresta
                Part(L(-0.3f, 1.2f + bob, 0.18f), new(0.08f, 0.75f, 0.5f), Theme.Fresco);       // scudo
                Part(L(0.28f, 1.2f + bob, 0.3f), new(0.05f, 0.05f, 0.7f), Theme.Marble);        // gladio
                break;
            }
            case "lemure":
            {
                float hover = 0.25f + MathF.Sin(t * 1.6f + e.Id) * 0.1f;
                _lighting.Emissive(true);
                Cone(L(0, hover, 0), 0.4f * scale, 1.7f * scale, e.Yaw * 57.3f, C(Theme.WithAlpha(Theme.Marble, 200)));
                Sphere(L(0, hover + 1.45f, 0), 0.19f * scale, C(Theme.WithAlpha(Theme.Marble, 220)));
                _lighting.Emissive(false);
                Part(L(0.07f, hover + 1.48f, 0.16f), new(0.05f, 0.07f, 0.03f), Theme.Basalt);
                Part(L(-0.07f, hover + 1.48f, 0.16f), new(0.05f, 0.07f, 0.03f), Theme.Basalt);
                Part(L(0, hover + 1.36f, 0.17f), new(0.09f, 0.03f, 0.02f), Theme.Basalt);
                break;
            }
            case "custode":
            {
                var bronze = new Color((byte)0x9a, (byte)0x6a, (byte)0x2a, (byte)255);
                float gait = MathF.Sin(e.Phase * 1.6f) * 0.15f * e.Stride;
                Part(L(0.22f, 0.5f, gait), new(0.26f, 1.0f, 0.3f), bronze);
                Part(L(-0.22f, 0.5f, -gait), new(0.26f, 1.0f, 0.3f), bronze);
                Part(L(0, 1.45f + bob, 0), new(0.95f, 0.95f, 0.55f), bronze);
                Part(L(0.62f, 1.35f + bob, lunge * 2), new(0.25f, 0.9f, 0.28f), bronze);
                Part(L(-0.62f, 1.35f + bob, 0), new(0.25f, 0.9f, 0.28f), bronze);
                Part(L(0, 2.15f + bob, 0), new(0.45f, 0.42f, 0.42f), bronze);
                Part(L(0, 1.45f + bob, 0.29f), new(0.5f, 0.12f, 0.02f), Theme.Gilt);
                _lighting.Emissive(true);
                Part(L(0, 2.18f + bob, 0.22f), new(0.3f, 0.06f, 0.02f), Theme.Flame);           // occhi di brace
                _lighting.Emissive(false);
                break;
            }
            default: // cultista, ladro di tombe: figure incappucciate
            {
                bool thief = e.Kind == "ladro";
                var robe = thief ? new Color((byte)0x5c, (byte)0x4d, (byte)0x40, (byte)255) : Theme.Basalt;
                float h = thief ? 1.3f : 1.5f;
                Cone(L(0, 0.05f + bob, 0), 0.36f * scale, h * scale, e.Yaw * 57.3f, C(robe));
                Part(L(0, h - 0.25f + bob, 0), new(0.5f, 0.14f, 0.26f), robe);
                Sphere(L(0, h + 0.02f + bob, 0), 0.17f * scale, C(thief ? robe : Theme.Fresco)); // cappuccio
                Part(L(0, h + bob, 0.13f), new(0.18f, 0.12f, 0.03f), Theme.Night);              // il volto nell'ombra
                Part(L(0.3f, h - 0.4f + bob, 0.25f), new(0.04f, 0.04f, 0.42f), Theme.Gilt,
                    Quaternion.CreateFromAxisAngle(Vector3.UnitX, -1.2f * lunge * 3));           // falcetto o pugnale
                break;
            }
        }
    }

    /// <summary>
    /// La spada in prima persona, davanti alla telecamera. <paramref name="swing"/> va da 0 a 1 durante un colpo.
    /// </summary>
    public void DrawWeapon(Vector3 eye, float yaw, float pitch, float swing, float sway)
    {
        var camera = Quaternion.Concatenate(Quaternion.CreateFromAxisAngle(Vector3.UnitX, -pitch), Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw));
        // a riposo la lama è inclinata in avanti e verso sinistra; il colpo la porta da destra a sinistra
        float sweep = swing > 0 ? MathF.Sin(swing * MathF.PI) : 0;
        var local = Quaternion.Concatenate(
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.55f + sweep * 0.9f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.35f - (swing > 0 ? (swing * 1.9f - 0.5f) : 0)));
        var q = Quaternion.Concatenate(local, camera);
        Vector3 C(float x, float y, float z) => eye + Vector3.Transform(new Vector3(x, y, z), camera);
        var hilt = C(-0.3f + sweep * 0.22f, -0.34f + MathF.Sin(sway) * 0.012f, 0.55f);
        var along = Vector3.Transform(Vector3.UnitZ, q);
        var steel = new Color((byte)0xd8, (byte)0xd2, (byte)0xc4, (byte)255);
        Oriented(_cube, hilt + along * 0.3f, q, new(0.04f, 0.012f, 0.52f), steel);
        Oriented(_cube, hilt + along * 0.02f, q, new(0.12f, 0.025f, 0.03f), Theme.Gilt);
        Oriented(_cube, hilt - along * 0.07f, q, new(0.025f, 0.025f, 0.12f), Theme.Basalt);
        Sphere(hilt - along * 0.14f, 0.022f, Theme.Gilt);
    }

    /// <summary>Gli effetti dei prodigi: raggi, fulmini, fuoco, cure, egide, catene, onde di terrore.</summary>
    public void DrawEffect(SpellEffect fx, double time)
    {
        float k = (float)Math.Clamp((time - fx.Time) / SpellEffect.Life, 0, 1);
        byte fade = (byte)(230 * (1 - k));
        var verdigris = new Color((byte)0x72, (byte)0xbb, (byte)0xa8, fade);
        _lighting.Emissive(true);
        switch (fx.Spell)
        {
            case "dardo" or "esorcismo":
            {
                var dir = fx.To - fx.From;
                float len = dir.Length();
                if (len < 0.01f) break;
                var head = fx.From + dir * MathF.Min(1, k * 2.5f);
                var q = Look(dir);
                Oriented(_cube, Vector3.Lerp(fx.From, head, 0.5f), q, new(0.06f, 0.06f, Vector3.Distance(fx.From, head)),
                    fx.Spell == "dardo" ? Theme.WithAlpha(Theme.Marble, fade) : verdigris);
                Sphere(head, 0.16f, fx.Spell == "dardo" ? Theme.WithAlpha(Theme.Gilt, fade) : verdigris);
                break;
            }
            case "folgore":
            {
                // il fulmine scende dal cielo a zig-zag sul bersaglio
                var top = fx.To + new Vector3(0, 14, 0);
                var prev = top;
                for (int i = 1; i <= 6; i++)
                {
                    float t = i / 6f;
                    var p = Vector3.Lerp(top, fx.To, t) + (i < 6 ? new Vector3(MathF.Sin(i * 7.3f) * 0.6f, 0, MathF.Cos(i * 5.1f) * 0.6f) : Vector3.Zero);
                    Oriented(_cube, Vector3.Lerp(prev, p, 0.5f), Look(p - prev), new(0.12f, 0.12f, Vector3.Distance(prev, p)), Theme.WithAlpha(Theme.Marble, fade));
                    prev = p;
                }
                Sphere(fx.To + new Vector3(0, 0.3f, 0), 0.6f * (1 - k), Theme.WithAlpha(Theme.Gilt, fade));
                break;
            }
            case "fiamma":
                Sphere(fx.To + new Vector3(0, 0.8f, 0), 0.4f + 2.6f * k, Theme.WithAlpha(Theme.Flame, (byte)(fade * 0.7f)));
                Sphere(fx.To + new Vector3(0, 0.8f, 0), 0.2f + 1.4f * k, Theme.WithAlpha(Theme.Gilt, fade));
                break;
            case "mano":
                for (int i = 0; i < 10; i++)
                {
                    float a = i * MathF.Tau / 10 + k * 3;
                    var p = fx.To + new Vector3(MathF.Cos(a) * 0.45f, 0.2f + k * 1.8f + (i % 3) * 0.2f, MathF.Sin(a) * 0.45f);
                    Sphere(p, 0.05f, verdigris);
                }
                break;
            case "egida":
                Cylinder(fx.To + new Vector3(0, 0.05f, 0), 0.6f + 7.4f * k, 0.06f, Theme.WithAlpha(Theme.Gilt, (byte)(fade * 0.6f)));
                Cylinder(fx.From + new Vector3(0, 0.05f, 0), 0.9f, 2.0f * (1 - k), Theme.WithAlpha(Theme.Gilt, (byte)(fade * 0.25f)));
                break;
            case "catene":
                for (int i = 0; i < 3; i++)
                    Cylinder(fx.To + new Vector3(0, 0.4f + i * 0.5f, 0), 0.55f, 0.08f, Theme.WithAlpha(Theme.Basalt, (byte)Math.Max(fade, (byte)120)));
                break;
            case "pan":
                Cylinder(fx.From + new Vector3(0, 0.05f, 0), 0.5f + 8.5f * k, 0.1f, verdigris);
                break;
            case "occhio":
                Sphere(fx.From + new Vector3(0, 1.9f, 0), 0.25f + 0.4f * k, Theme.WithAlpha(Theme.Gilt, fade));
                break;
        }
        _lighting.Emissive(false);
    }

    private static Quaternion Look(Vector3 dir)
    {
        dir = Vector3.Normalize(dir);
        float yaw = MathF.Atan2(dir.X, dir.Z), pitch = MathF.Asin(Math.Clamp(dir.Y, -1, 1));
        return Quaternion.Concatenate(Quaternion.CreateFromAxisAngle(Vector3.UnitX, -pitch), Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw));
    }

    public void DrawShot(Shot shot, double time)
    {
        float k = (float)Math.Clamp((time - shot.Time) / 0.18, 0, 1);
        var dir = Vector3.Normalize(shot.To - shot.From);
        var at = Vector3.Lerp(shot.From, shot.To, k);
        float yaw = MathF.Atan2(dir.X, dir.Z), pitch = MathF.Asin(Math.Clamp(dir.Y, -1, 1));
        var q = Quaternion.Concatenate(Quaternion.CreateFromAxisAngle(Vector3.UnitX, -pitch), Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw));
        Oriented(_cube, at, q, new(0.025f, 0.025f, 0.5f), Theme.Gilt);
    }

    /// <summary>Disegna un modello ruotato da un quaternione (raylib vuole asse e angolo).</summary>
    private static void Oriented(Model model, Vector3 center, Quaternion q, Vector3 size, Color color)
    {
        q = Quaternion.Normalize(q);
        if (q.W < 0) q = -q;
        float angle = 2f * MathF.Acos(Math.Clamp(q.W, -1f, 1f));
        float s = MathF.Sqrt(MathF.Max(0f, 1f - q.W * q.W));
        var axis = s < 1e-4f ? Vector3.UnitY : new Vector3(q.X, q.Y, q.Z) / s;
        Raylib.DrawModelEx(model, center, axis, angle * 180f / MathF.PI, size, color);
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
