using System.Numerics;
using LexArcana.Game;

namespace LexArcana.Visione;

/// <summary>Un altro personaggio nella scena. Scivola verso l'ultima posizione ricevuta, così il movimento resta fluido.</summary>
internal sealed class Avatar(string name, Pose pose)
{
    public string Name { get; } = name;
    public Pose Target { get; set; } = pose;
    public Vector2 Position { get; private set; } = new(pose.X, pose.Z);
    public float Yaw { get; private set; } = pose.Yaw;

    /// <summary>Quanto sta camminando (0–1), per l'oscillazione del passo.</summary>
    public float Stride { get; private set; }
    public float Phase { get; private set; }

    public void Update(float dt)
    {
        var target = new Vector2(Target.X, Target.Z);
        var k = 1f - MathF.Exp(-12f * dt);
        var before = Position;
        Position = Vector2.Distance(Position, target) > 6f ? target : Vector2.Lerp(Position, target, k);
        float delta = MathF.IEEERemainder(Target.Yaw - Yaw, MathF.Tau);
        Yaw += delta * k;

        float speed = Vector2.Distance(before, Position) / MathF.Max(dt, 1e-4f);
        Stride += (Math.Clamp(speed / 3f, 0f, 1f) - Stride) * k;
        Phase += speed * dt * 3.2f;
    }
}
