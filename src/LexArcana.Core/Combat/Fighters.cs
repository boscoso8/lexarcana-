using LexArcana.Characters;
using LexArcana.Game;

namespace LexArcana.Combat;

public enum EnemyRank { Normal, Elite, Boss }

/// <summary>Un nemico nel mondo. Lo crea e lo muove <see cref="CombatEngine"/>.</summary>
public sealed class Enemy
{
    internal Enemy(int id, EnemyKind kind, int level, EnemyRank rank, Pose pose, string? name)
    {
        Id = id;
        Kind = kind;
        Level = level;
        Rank = rank;
        Pose = pose;
        Home = pose;
        float multiplier = rank switch { EnemyRank.Elite => 1.6f, EnemyRank.Boss => 3f, _ => 1f };
        MaxHealth = (int)MathF.Round((kind.BaseHealth + kind.HealthPerLevel * (level - 1)) * multiplier);
        Health = MaxHealth;
        Attack = kind.BaseAttack + level / 2 + (int)rank;
        Defense = kind.BaseDefense + level / 2 + (int)rank;
        DamageBonus = level / 2 + (int)rank;
        Name = name ?? (rank == EnemyRank.Normal ? kind.Name : kind.EliteName);
    }

    public int Id { get; }
    public EnemyKind Kind { get; }
    public string Name { get; }
    public int Level { get; }
    public EnemyRank Rank { get; }
    public int MaxHealth { get; }
    public int Health { get; internal set; }
    public int Attack { get; }
    public int Defense { get; }
    public int DamageBonus { get; }
    public Pose Pose { get; internal set; }
    public Pose Home { get; }
    public bool IsDead => Health <= 0;
    public int Experience => Kind.Experience * Level * Rank switch { EnemyRank.Elite => 2, EnemyRank.Boss => 5, _ => 1 };
    public float Reach => Kind.Reach * (Rank == EnemyRank.Boss ? 1.25f : 1f);

    internal string? Target { get; set; }
    /// <summary>Fermato dalle Catene di Saturno fino a questo istante.</summary>
    internal double StunnedUntil { get; set; }
    /// <summary>In fuga per il Terrore di Pan fino a questo istante.</summary>
    internal double FleeingUntil { get; set; }
    internal double NextAttackAt { get; set; }
    internal Pose LastSent { get; set; }

    public string Label => $"{Name} (liv. {Level})";
}

/// <summary>Un giocatore in combattimento: statistiche ricavate dal personaggio (o di base) e dal livello.</summary>
public sealed class Fighter
{
    public const int DefaultMelee = 3, DefaultRanged = 2, DefaultHealth = 14, DefaultDefense = 11;
    public const float MeleeReach = 2.4f, RangedReach = 16f;
    public const double AttackCooldown = 0.9;

    private readonly Character? _character;

    public Fighter(string name, Character? character)
    {
        Name = name;
        _character = character;
        Experience = character?.Experience ?? 0;
        Level = Progression.LevelFor(Experience);
        Health = MaxHealth;
        Resolve = MaxResolve;
    }

    public string Name { get; }
    public int Level { get; private set; }
    public int Experience { get; private set; }
    public int Health { get; internal set; }
    public bool IsDown => Health <= 0;
    internal double RespawnAt { get; set; }
    internal double NextAttackAt { get; set; }
    internal double NextCastAt { get; set; }
    internal double NextResolveAt { get; set; }
    internal double AegisUntil { get; set; }

    /// <summary>La Volontà: la riserva da cui si pagano i prodigi.</summary>
    public int Resolve { get; internal set; }
    public int MaxResolve => (_character?.Resolve ?? 6) + (Level - 1);
    public IReadOnlyList<Spell> Spells => Grimoire.Known(_character, Level);
    public bool Knows(Spell spell) => Spells.Contains(spell);

    /// <summary>Il bonus della prova di un prodigio: attributo + abilità della tradizione + bonus di livello.</summary>
    public int CastBonus(Tradition t) => _character is { } c ? c[Grimoire.Trait(t)] + c.SkillRank(Grimoire.Skill(t)) + LevelBonus : 0;
    public int TraitValue(Trait t) => _character?[t] ?? 1;

    private int LevelBonus => (Level - 1) / 2;

    public int MaxHealth => (_character?.Health ?? DefaultHealth) + 3 * (Level - 1);
    public int Defense => (_character?.Defense ?? DefaultDefense) + LevelBonus + AegisBonus;
    internal int AegisBonus { get; set; }
    public int MeleeBonus => (_character?.CheckBonus("Armi") ?? DefaultMelee) + LevelBonus;
    public int RangedBonus => (_character?.CheckBonus("Tiro") ?? DefaultRanged) + LevelBonus;
    public int MeleeDamageBonus => (_character is { } c ? c[Trait.Robur] / 2 : 1) + LevelBonus;
    public int RangedDamageBonus => (_character is { } c ? c[Trait.Celeritas] / 3 : 0) + LevelBonus;
    public const int MeleeDie = 8, RangedDie = 6;

    public int NextLevelAt => Level >= Progression.MaxLevel ? Progression.ExperienceFor(Progression.MaxLevel) : Progression.ExperienceFor(Level + 1);

    /// <summary>Aggiunge esperienza; restituisce true se il personaggio è salito di livello (e lo cura del tutto).</summary>
    internal bool Gain(int experience)
    {
        Experience += experience;
        int level = Progression.LevelFor(Experience);
        if (level == Level) return false;
        Level = level;
        Health = MaxHealth;
        Resolve = MaxResolve;
        return true;
    }
}
