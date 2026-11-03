using LexArcana.Campaigns;
using LexArcana.Game;

namespace LexArcana.Combat;

/// <summary>Ciò che è successo in un combattimento; il server lo traduce in messaggi.</summary>
public abstract record CombatEvent;
/// <summary>I nemici sono cambiati del tutto (nuovo luogo, evocazione, pace): va mandato l'elenco intero.</summary>
public sealed record EnemiesChanged : CombatEvent;
public sealed record EnemiesMoved(IReadOnlyList<Enemy> Enemies) : CombatEvent;
/// <summary>Un colpo, andato a segno o no. Attaccante e bersaglio sono un giocatore (per nome) o un nemico (per id).</summary>
public sealed record Blow(
    string Attacker, int? AttackerEnemyId, string Target, int? TargetEnemyId,
    int Roll, int Total, int Against, bool Hit, bool Critical, int Damage,
    int Health, int MaxHealth, bool Defeated, bool Ranged) : CombatEvent;
public sealed record VitalsChanged(Fighter Fighter) : CombatEvent;
public sealed record LevelUp(Fighter Fighter) : CombatEvent;
public sealed record Respawned(Fighter Fighter, Pose Pose) : CombatEvent;

/// <summary>
/// Il combattimento, tutto lato server: dove sono i nemici, chi inseguono, chi colpisce chi.
/// Non sa nulla di rete; non è sicuro tra thread, quindi il chiamante lo usa sotto un lock.
/// Prova: 1d20 + bonus contro Difesa; 20 naturale è critico (danni doppi), 1 naturale manca sempre.
/// </summary>
public sealed class CombatEngine(Dice dice, GameMap map)
{
    public const double RespawnDelay = 6;
    public const float SafeRadius = 10f;
    private const float EnemyRadius = 0.35f, Separation = 0.9f;

    private readonly Dictionary<int, Enemy> _enemies = [];
    private readonly Dictionary<string, Fighter> _fighters = new(StringComparer.OrdinalIgnoreCase);
    private int _nextId;

    public GameMap Map { get; private set; } = map;
    public IReadOnlyCollection<Enemy> Enemies => _enemies.Values;
    public IReadOnlyCollection<Fighter> Fighters => _fighters.Values;
    public Fighter? FighterOf(string name) => _fighters.GetValueOrDefault(name);
    public Enemy? EnemyOf(int id) => _enemies.GetValueOrDefault(id);

    public Fighter Join(string name, Characters.Character? character = null)
    {
        var fighter = new Fighter(name, character);
        _fighters[name] = fighter;
        return fighter;
    }

    public void Leave(string name)
    {
        _fighters.Remove(name);
        foreach (var e in _enemies.Values) if (name.IsSameName(e.Target)) e.Target = null;
    }

    public double PartyLevel => _fighters.Count == 0 ? 1 : _fighters.Values.Average(f => f.Level);

    public void Clear() => _enemies.Clear();

    /// <summary>
    /// Popola un luogo: nemici sparsi lontano dalle partenze, un guardiano più forte accanto all'altare
    /// e, nell'avventura finale, il boss. Il livello segue <see cref="Progression.EncounterLevel"/>.
    /// </summary>
    public IReadOnlyList<Enemy> Populate(GameMap newMap, PlaceKind? kind, int adventure, bool finalAdventure, string? bossName, Random random)
    {
        Map = newMap;
        _enemies.Clear();
        int level = Progression.EncounterLevel(adventure, PartyLevel);
        var table = Bestiary.ForPlace(kind);

        var floors = new List<Pose>();
        (float X, float Z)? altar = null;
        for (int x = 0; x < newMap.Width; x++)
            for (int z = 0; z < newMap.Depth; z++)
            {
                var (cx, cz) = GameMap.CellCenter(x, z);
                if (newMap[x, z] == Tile.Altar) altar = (cx, cz);
                if (newMap[x, z] == Tile.Floor && newMap.Spawns.All(s => Distance(s.X, s.Z, cx, cz) > SafeRadius))
                    floors.Add(new Pose(cx, cz, (float)(random.NextDouble() * MathF.Tau)));
            }

        if (altar is var (ax, az))
        {
            var near = floors.OrderBy(f => Distance(f.X, f.Z, ax, az)).ToList();
            if (near.Count > 0) Summon(table[0], level + 1, EnemyRank.Elite, near[0]);
            if (finalAdventure && near.Count > 1) Summon(table[0], level + 2, EnemyRank.Boss, near[1], bossName);
        }

        int wanted = Math.Clamp(floors.Count / 18, 3, 9);
        foreach (var spot in floors.OrderBy(_ => random.Next()))
        {
            if (_enemies.Count >= wanted + (altar is null ? 0 : finalAdventure ? 2 : 1)) break;
            if (_enemies.Values.Any(e => Distance(e.Pose.X, e.Pose.Z, spot.X, spot.Z) < 3f)) continue;
            Summon(table[random.Next(table.Count)], Math.Max(1, level - random.Next(2)), EnemyRank.Normal, spot);
        }
        return [.. _enemies.Values];
    }

    public Enemy Summon(EnemyKind kind, int level, EnemyRank rank, Pose pose, string? name = null)
    {
        var enemy = new Enemy(++_nextId, kind, Math.Clamp(level, 1, Progression.MaxLevel), rank, pose, name);
        enemy.LastSent = pose;
        _enemies[enemy.Id] = enemy;
        return enemy;
    }

    /// <summary>Un giocatore colpisce un nemico. Se il colpo non si può tentare, <paramref name="refusal"/> dice perché.</summary>
    public List<CombatEvent> Attack(string player, Pose from, int enemyId, bool ranged, double now, out string? refusal)
    {
        List<CombatEvent> events = [];
        refusal = null;
        if (FighterOf(player) is not { } fighter) { refusal = "Non sei in combattimento."; return events; }
        if (fighter.IsDown) { refusal = "Sei a terra: aspetta di rialzarti."; return events; }
        if (now < fighter.NextAttackAt) return events; // troppo presto: il colpo si perde senza messaggi
        if (EnemyOf(enemyId) is not { IsDead: false } enemy) { refusal = "Quel nemico non c'è più."; return events; }

        float distance = Distance(from.X, from.Z, enemy.Pose.X, enemy.Pose.Z);
        float reach = ranged ? Fighter.RangedReach : Fighter.MeleeReach + EnemyRadius;
        if (distance > reach) { refusal = ranged ? "Troppo lontano anche per un tiro." : "Troppo lontano: avvicinati o tira (clic destro)."; return events; }
        if (!Map.HasLineOfSight(from.X, from.Z, enemy.Pose.X, enemy.Pose.Z)) { refusal = "Non hai un tiro libero."; return events; }

        fighter.NextAttackAt = now + Fighter.AttackCooldown;
        int bonus = ranged ? fighter.RangedBonus : fighter.MeleeBonus;
        var (roll, total, hit, critical) = Resolve(bonus, enemy.Defense);
        int damage = 0;
        if (hit)
        {
            int die = ranged ? Fighter.RangedDie : Fighter.MeleeDie;
            damage = Math.Max(1, dice.D(die) + (critical ? dice.D(die) : 0) + (ranged ? fighter.RangedDamageBonus : fighter.MeleeDamageBonus));
            enemy.Health = Math.Max(0, enemy.Health - damage);
        }
        enemy.Target ??= fighter.Name; // chi viene attaccato si gira verso chi lo attacca
        events.Add(new Blow(fighter.Name, null, enemy.Label, enemy.Id, roll, total, enemy.Defense, hit, critical, damage,
            enemy.Health, enemy.MaxHealth, enemy.IsDead, ranged));

        if (enemy.IsDead)
        {
            _enemies.Remove(enemy.Id);
            // l'esperienza va a tutto il gruppo: si combatte insieme
            foreach (var f in _fighters.Values)
            {
                bool levelled = f.Gain(enemy.Experience);
                events.Add(new VitalsChanged(f));
                if (levelled) events.Add(new LevelUp(f));
            }
        }
        return events;
    }

    /// <summary>
    /// Un passo della simulazione. <paramref name="players"/> sono i giocatori presenti nella scena 3D
    /// (chi gioca solo da terminale non si muove e i nemici non lo vedono).
    /// </summary>
    public List<CombatEvent> Tick(double now, float dt, IReadOnlyDictionary<string, Pose> players)
    {
        List<CombatEvent> events = [];

        foreach (var f in _fighters.Values.Where(f => f.IsDown && f.RespawnAt > 0 && now >= f.RespawnAt))
        {
            f.Health = f.MaxHealth;
            f.RespawnAt = 0;
            events.Add(new Respawned(f, Map.Spawns[Math.Abs(f.Name.GetHashCode(StringComparison.OrdinalIgnoreCase)) % Map.Spawns.Length]));
            events.Add(new VitalsChanged(f));
        }

        List<Enemy> moved = [];
        foreach (var enemy in _enemies.Values)
        {
            var target = ChooseTarget(enemy, players);
            enemy.Target = target?.Name;
            var pose = enemy.Pose;

            if (target is not null)
            {
                var tp = players[target.Name];
                float dx = tp.X - pose.X, dz = tp.Z - pose.Z, distance = MathF.Sqrt(dx * dx + dz * dz);
                float yaw = MathF.Atan2(dx, dz);
                if (distance > enemy.Reach * 0.8f)
                {
                    var (sx, sz) = Separate(enemy);
                    float step = enemy.Kind.Speed * dt / MathF.Max(distance, 0.001f);
                    pose = Map.Slide(pose, dx * step + sx * dt, dz * step + sz * dt, yaw);
                }
                else pose = pose with { Yaw = yaw };

                if (distance <= enemy.Reach && now >= enemy.NextAttackAt)
                {
                    enemy.NextAttackAt = now + (enemy.Rank == EnemyRank.Boss ? 1.3 : 1.6);
                    events.AddRange(Strike(enemy, target, now));
                }
            }
            else if (Distance(pose.X, pose.Z, enemy.Home.X, enemy.Home.Z) > 1f)
            {
                float dx = enemy.Home.X - pose.X, dz = enemy.Home.Z - pose.Z, distance = MathF.Sqrt(dx * dx + dz * dz);
                float step = enemy.Kind.Speed * 0.5f * dt / distance;
                pose = Map.Slide(pose, dx * step, dz * step, MathF.Atan2(dx, dz));
            }

            enemy.Pose = pose;
            if (Distance(pose.X, pose.Z, enemy.LastSent.X, enemy.LastSent.Z) > 0.03f || MathF.Abs(MathF.IEEERemainder(pose.Yaw - enemy.LastSent.Yaw, MathF.Tau)) > 0.05f)
            {
                enemy.LastSent = pose;
                moved.Add(enemy);
            }
        }
        if (moved.Count > 0) events.Insert(0, new EnemiesMoved(moved));
        return events;
    }

    private Fighter? ChooseTarget(Enemy enemy, IReadOnlyDictionary<string, Pose> players)
    {
        Fighter? best = null;
        float bestDistance = float.MaxValue;
        foreach (var (name, pose) in players)
        {
            if (FighterOf(name) is not { IsDown: false } fighter) continue;
            float d = Distance(enemy.Pose.X, enemy.Pose.Z, pose.X, pose.Z);
            bool current = name.IsSameName(enemy.Target);
            // un bersaglio nuovo va visto ed è entro la portata dello sguardo; quello già inseguito si lascia solo se scappa lontano
            if (d > enemy.Kind.Aggro * (current ? 1.6f : 1f)) continue;
            if (!current && !Map.HasLineOfSight(enemy.Pose.X, enemy.Pose.Z, pose.X, pose.Z)) continue;
            if (current) d -= 2f; // preferisce chi sta già inseguendo
            if (d < bestDistance) { best = fighter; bestDistance = d; }
        }
        return best;
    }

    private IEnumerable<CombatEvent> Strike(Enemy enemy, Fighter fighter, double now)
    {
        var (roll, total, hit, critical) = Resolve(enemy.Attack, fighter.Defense);
        int damage = 0;
        if (hit)
        {
            damage = Math.Max(1, dice.D(enemy.Kind.DamageDie) + (critical ? dice.D(enemy.Kind.DamageDie) : 0) + enemy.DamageBonus);
            fighter.Health = Math.Max(0, fighter.Health - damage);
        }
        if (fighter.IsDown)
        {
            fighter.RespawnAt = now + RespawnDelay;
            foreach (var e in _enemies.Values) if (fighter.Name.IsSameName(e.Target)) e.Target = null;
        }
        yield return new Blow(enemy.Label, enemy.Id, fighter.Name, null, roll, total, fighter.Defense, hit, critical, damage,
            fighter.Health, fighter.MaxHealth, fighter.IsDown, false);
        yield return new VitalsChanged(fighter);
    }

    private (int Roll, int Total, bool Hit, bool Critical) Resolve(int bonus, int defense)
    {
        int roll = dice.D(20);
        int total = roll + bonus;
        bool critical = roll == 20;
        return (roll, total, critical || (roll != 1 && total >= defense), critical);
    }

    /// <summary>Una piccola spinta per non ammucchiare i nemici uno sull'altro.</summary>
    private (float X, float Z) Separate(Enemy enemy)
    {
        float x = 0, z = 0;
        foreach (var other in _enemies.Values)
        {
            if (other == enemy) continue;
            float dx = enemy.Pose.X - other.Pose.X, dz = enemy.Pose.Z - other.Pose.Z, d = MathF.Sqrt(dx * dx + dz * dz);
            if (d is > 0.001f and < Separation) { x += dx / d * (Separation - d) * 3; z += dz / d * (Separation - d) * 3; }
        }
        return (x, z);
    }

    private static float Distance(float x0, float z0, float x1, float z1) => MathF.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));
}
