using System.Numerics;
using LexArcana.Protocol;

namespace LexArcana.Visione;

/// <summary>Un nemico come lo vede il client: scivola verso la posizione ricevuta, lampeggia se colpito, svanisce se sconfitto.</summary>
internal sealed class EnemyView(EnemyInfo info)
{
    public int Id { get; } = info.Id;
    public string Kind { get; } = info.Kind;
    public string Name { get; } = info.Name;
    public int Level { get; } = info.Level;
    public string Rank { get; } = info.Rank;
    public int Health { get; set; } = info.Health;
    public int MaxHealth { get; } = info.MaxHealth;
    public Vector2 Target { get; set; } = new(info.X, info.Z);
    public float TargetYaw { get; set; } = info.Yaw;
    public Vector2 Position { get; private set; } = new(info.X, info.Z);
    public float Yaw { get; private set; } = info.Yaw;
    public double FlashUntil { get; set; }
    public double AttackAt { get; set; } = -10;
    public double? DiedAt { get; set; }
    public float Phase { get; private set; } = info.Id * 1.7f;
    public float Stride { get; private set; }

    public float Scale => Rank switch { "Boss" => 1.35f, "Elite" => 1.15f, _ => 1f };
    public string Label => $"{Name} · liv. {Level}";
    public float Height => (Kind switch { "lupo" => 1.0f, "strige" => 2.4f, "custode" => 2.5f, _ => 1.9f }) * Scale;

    public void Update(float dt)
    {
        float k = 1f - MathF.Exp(-12f * dt);
        var before = Position;
        Position = Vector2.Distance(Position, Target) > 6f ? Target : Vector2.Lerp(Position, Target, k);
        Yaw += MathF.IEEERemainder(TargetYaw - Yaw, MathF.Tau) * k;
        float speed = Vector2.Distance(before, Position) / MathF.Max(dt, 1e-4f);
        Stride += (Math.Clamp(speed / 3f, 0f, 1f) - Stride) * k;
        Phase += dt * (2f + speed * 3f);
    }
}

/// <summary>Un numero che sale e svanisce sopra chi è stato colpito.</summary>
internal readonly record struct FloatingText(Vector3 Position, string Text, LexArcana.Ui.Tone Tone, double Time);

/// <summary>L'effetto visibile di un prodigio, che dura qualche istante.</summary>
internal readonly record struct SpellEffect(string Spell, Vector3 From, Vector3 To, double Time)
{
    public const double Life = 0.9;
}

/// <summary>Un dardo in volo, dal giocatore al bersaglio.</summary>
internal readonly record struct Shot(Vector3 From, Vector3 To, double Time);
