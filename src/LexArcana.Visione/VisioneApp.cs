using System.Collections.Concurrent;
using System.Numerics;
using Raylib_cs;
using LexArcana.Game;
using LexArcana.Net;
using LexArcana.Protocol;
using LexArcana.Ui;

namespace LexArcana.Visione;

/// <summary>
/// La finestra 3D di un giocatore: il mondo visto con gli occhi del personaggio.
/// La rete gira su un altro thread e consegna i messaggi in una coda; tutto il resto
/// (input, movimento, disegno) avviene sul thread della finestra.
/// </summary>
internal sealed class VisioneApp(GameClient client, GameMap initialMap, VisioneOptions options) : IDisposable
{
    private const float EyeHeight = 1.6f, WalkSpeed = 3.2f, RunSpeed = 5.6f, MouseSensitivity = 0.0028f;
    private static readonly TimeSpan MoveInterval = TimeSpan.FromMilliseconds(100);

    private readonly ConcurrentQueue<ServerMessage> _inbox = new();
    private readonly Dictionary<string, Avatar> _avatars = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _present = [];
    private readonly MessageRenderer _renderer = new(client.Name);
    private GameMap _map = initialMap;
    private readonly Dictionary<int, EnemyView> _enemies = [];
    private readonly List<FloatingText> _floating = [];
    private readonly List<Shot> _shots = [];
    private Vitals? _vitals;
    private double _downSince, _hurtAt = -10, _swingAt = -10, _nextAttackAt;
    private int? _aimed;
    private const double LocalCooldown = 0.9, SwingTime = 0.32;
    private World? _world;
    private SheetShown? _mySheet;
    private bool _showSheet = options.ShowSheet;
    private Pose _pose;
    private Pose _lastSent;
    private float _pitch;
    private double _lastSentAt;
    // L'aiuto compare all'inizio e si chiude da solo al primo passo; aperto con F1 resta finché non lo si richiude.
    private bool _showHelp = true, _helpPinned, _mouseCaptured = true, _quit;
    private string? _overlay;

    public int Run()
    {
        var welcome = client.Welcome;
        _present.AddRange(welcome.Players);
        foreach (var p in welcome.Positions)
        {
            var pose = new Pose(p.X, p.Z, p.Yaw);
            if (p.Name.IsSameName(client.Name)) _pose = pose;
            else _avatars[p.Name] = new Avatar(p.Name, pose);
        }
        if (options.Frame is { } frame)
        {
            _pose = new Pose(frame.X, frame.Z, frame.Yaw);
            _pitch = frame.Pitch;
        }
        _lastSent = _pose;

        using var network = new CancellationTokenSource();
        var receiving = Task.Run(() => ReceiveAsync(network.Token));
        if (options.Character is { } character)
            client.Send(new Embody(character));

        Raylib.SetConfigFlags(ConfigFlags.Msaa4xHint | ConfigFlags.ResizableWindow | ConfigFlags.VSyncHint);
        Raylib.InitWindow(1280, 720, $"Lex Arcana · {client.Name}");
        Raylib.SetExitKey(KeyboardKey.Null);
        Raylib.SetTargetFPS(60);
        Raylib.DisableCursor();

        var assets = Path.Combine(AppContext.BaseDirectory, "Assets");
        using (var lighting = new Lighting())
        using (var hud = new Hud(assets))
        {
            _world = new World(_map, lighting);
            var start = Raylib.GetTime();
            hud.Add(_renderer.Render(welcome)!, 0);
            bool captured = false;
            var lights = new (Vector3, Vector3, float)[Lighting.MaxLights];

            while (!_quit && !Raylib.WindowShouldClose())
            {
                double now = Raylib.GetTime() - start;
                float dt = MathF.Min(Raylib.GetFrameTime(), 0.1f);

                Drain(hud, lighting, now);
                var world = _world!;
                HandleInput(hud, dt, now);
                foreach (var avatar in _avatars.Values) avatar.Update(dt);
                foreach (var enemy in _enemies.Values) enemy.Update(dt);
                foreach (var gone in _enemies.Values.Where(e => e.DiedAt is { } d && now - d > 1.3).Select(e => e.Id).ToList()) _enemies.Remove(gone);
                _floating.RemoveAll(f => now - f.Time > 1.4);
                _shots.RemoveAll(s => now - s.Time > 0.2);
                if (_showHelp && (now > 12 || _pose.DistanceTo(_lastSent) > 0.05f || hud.Typing || options.CapturePath is not null)) _showHelp = _helpPinned;
                SendPose(now);

                var forward = new Vector3(MathF.Sin(_pose.Yaw) * MathF.Cos(_pitch), MathF.Sin(_pitch), MathF.Cos(_pose.Yaw) * MathF.Cos(_pitch));
                var eye = new Vector3(_pose.X, EyeHeight, _pose.Z);
                var camera = new Camera3D(eye, eye + forward, Vector3.UnitY, 70f, CameraProjection.Perspective);
                _aimed = Aim(eye, forward);
                if (_overlay is null && !hud.Typing && _vitals?.Down != true) HandleAttack(eye, forward, now);

                int count = 0;
                lights[count++] = (eye + new Vector3(0, -0.2f, 0) + forward * 0.3f, new Vector3(0.62f, 0.5f, 0.36f), 9f); // lanterna
                foreach (var fire in world.Fires(now).Take(Lighting.MaxLights - 1)) lights[count++] = fire;
                lighting.Frame(eye, lights.AsSpan(0, count));

                Raylib.BeginDrawing();
                Raylib.ClearBackground(Theme.Night);
                Raylib.BeginMode3D(camera);
                world.Draw(eye, now);
                foreach (var avatar in _avatars.Values) world.DrawAvatar(avatar);
                foreach (var enemy in _enemies.Values) world.DrawEnemy(enemy, now);
                foreach (var shot in _shots) world.DrawShot(shot, now);
                if (_vitals?.Down != true)
                {
                    float swing = (float)Math.Clamp((now - _swingAt) / SwingTime, 0, 1);
                    world.DrawWeapon(eye, _pose.Yaw, _pitch, swing >= 1 ? 0 : swing, (float)now * 1.7f);
                }
                Raylib.EndMode3D();

                DrawEnemyTags(hud, camera, eye, forward);
                foreach (var f in _floating) hud.DrawFloating(f, Raylib.GetWorldToScreen(f.Position, camera), now);
                if (now - _hurtAt < 0.5) Hud.DrawHurt((float)(1 - (now - _hurtAt) / 0.5));
                if (_vitals is { } myVitals) hud.DrawVitals(myVitals);

                DrawNameTags(hud, camera, eye, forward);
                hud.DrawPlaceName(_map.Name);
                if (_showSheet || Raylib.IsKeyDown(KeyboardKey.C) && !hud.Typing) hud.DrawSheet(_mySheet, options.Character);
                if (_overlay is null && !hud.Typing && !_showHelp) Hud.DrawCrosshair();
                if (_vitals?.Down == true && _overlay is null) hud.DrawDown(CombatRespawnDelay - (now - _downSince));
                hud.Draw(now, _showHelp, _present, _overlay);
                Raylib.EndDrawing();

                if (options.CapturePath is { } path && !captured && now >= options.CaptureAfter)
                {
                    var image = Raylib.LoadImageFromScreen();
                    Raylib.ExportImage(image, path);
                    Raylib.UnloadImage(image);
                    captured = true;
                    _quit = true;
                }
            }
            Dispose(); // la scena va liberata finché la finestra e il contesto OpenGL esistono
        }

        Raylib.CloseWindow();
        SaveExperience();
        network.Cancel();
        try { receiving.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        return 0;
    }

    public void Dispose()
    {
        _world?.Dispose();
        _world = null;
    }

    private async Task ReceiveAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var message in client.ReadAllAsync(ct)) _inbox.Enqueue(message);
            _inbox.Enqueue(new Expelled("La connessione con il Demiurgo si è interrotta."));
        }
        catch (OperationCanceledException) { }
        catch (ProtocolException ex)
        {
            _inbox.Enqueue(new Expelled(ex.Message));
        }
    }

    private void Drain(Hud hud, Lighting lighting, double now)
    {
        while (_inbox.TryDequeue(out var message))
        {
            switch (message)
            {
                case MapChanged(var data, var positions, _):
                    if (GameMap.TryParse([$"nome: {data.Name}", .. data.Rows], out var newMap, out var mapError))
                    {
                        _world?.Dispose();
                        _map = newMap;
                        _world = new World(newMap, lighting);
                        _avatars.Clear();
                        foreach (var p in positions)
                        {
                            var pose = new Pose(p.X, p.Z, p.Yaw);
                            if (p.Name.IsSameName(client.Name)) { _pose = pose; _lastSent = pose; _pitch = 0; }
                            else _avatars[p.Name] = new Avatar(p.Name, pose);
                        }
                    }
                    else hud.Add(new StyledText().Add("! Il Demiurgo ha mandato una mappa non valida: " + mapError, Tone.Pompeian), now);
                    break;
                case EnemyList(var list):
                    _enemies.Clear();
                    foreach (var info in list) _enemies[info.Id] = new EnemyView(info);
                    break;
                case EnemyMoves(var poses):
                    foreach (var pose in poses)
                        if (_enemies.TryGetValue(pose.Id, out var view)) { view.Target = new(pose.X, pose.Z); view.TargetYaw = pose.Yaw; }
                    break;
                case Struck s:
                    OnStruck(s, now);
                    break;
                case Vitals v when v.Name.IsSameName(client.Name):
                    if (v.Down && _vitals?.Down != true) _downSince = now;
                    _vitals = v;
                    continue;
                case SheetShown sheet when sheet.Owner.IsSameName(client.Name):
                    _mySheet = sheet;
                    if (sheet.UpdatedBy?.IsSameName(client.Name) == true) continue; // la propria compilazione non riempie la chat
                    break;
                case Moved(var name, var x, var z, var yaw) when name.IsSameName(client.Name):
                    _pose = new Pose(x, z, _pose.Yaw); // il server ha corretto una mossa non valida
                    _lastSent = _pose;
                    break;
                case Moved(var name, var x, var z, var yaw):
                    if (_avatars.TryGetValue(name, out var avatar)) avatar.Target = new Pose(x, z, yaw);
                    else _avatars[name] = new Avatar(name, new Pose(x, z, yaw));
                    break;
                case Joined(var name):
                    if (!_present.Contains(name, StringComparer.OrdinalIgnoreCase)) _present.Add(name);
                    break;
                case Left(var name):
                    _avatars.Remove(name);
                    _present.RemoveAll(n => n.IsSameName(name));
                    break;
                case PlayerList(var names):
                    _present.Clear();
                    _present.AddRange(names);
                    break;
                case Narrated(var text):
                    hud.Banner(text.Sanitized, now);
                    break;
                case Expelled(var reason):
                    _overlay ??= reason.Sanitized is { Length: > 0 } r ? r : "Il Demiurgo ti ha allontanato dalla sessione.";
                    break;
            }
            if (_renderer.Render(message) is { } styled && message is not Expelled) hud.Add(styled, now);
        }
    }

    private void HandleInput(Hud hud, float dt, double now)
    {
        if (_vitals?.Down == true) return; // a terra non ci si muove
        if (_overlay is not null)
        {
            Raylib.EnableCursor();
            if (Raylib.IsKeyPressed(KeyboardKey.Escape) || Raylib.IsKeyPressed(KeyboardKey.Enter)) _quit = true;
            return;
        }

        if (hud.Typing)
        {
            if (hud.UpdateInput() is { } line) Submit(hud, line, now);
            return;
        }

        if (Raylib.IsKeyPressed(KeyboardKey.F1)) _showHelp = _helpPinned = !_showHelp;
        if (Raylib.IsKeyPressed(KeyboardKey.Tab) && Raylib.IsKeyDown(KeyboardKey.LeftShift)) _showSheet = !_showSheet;
        if (Raylib.IsKeyPressed(KeyboardKey.Enter) || Raylib.IsKeyPressed(KeyboardKey.T)) hud.OpenInput();
        else if (Raylib.IsKeyPressed(KeyboardKey.Slash)) hud.OpenInput("/");
        if (Raylib.IsKeyPressed(KeyboardKey.Escape))
        {
            _mouseCaptured = !_mouseCaptured;
            if (_mouseCaptured) Raylib.DisableCursor(); else Raylib.EnableCursor();
        }
        if (!_mouseCaptured && Raylib.IsMouseButtonPressed(MouseButton.Left))
        {
            _nextAttackAt = now + 0.2; // il clic che riprende il mouse non è un colpo
            _mouseCaptured = true;
            Raylib.DisableCursor();
        }
        if (hud.Typing) return;

        if (options.AutoFight)
        {
            AutoFight(dt);
            return;
        }

        float yaw = _pose.Yaw;
        if (_mouseCaptured)
        {
            var mouse = Raylib.GetMouseDelta();
            yaw -= mouse.X * MouseSensitivity;
            _pitch = Math.Clamp(_pitch - mouse.Y * MouseSensitivity, -1.35f, 1.35f);
        }
        if (Raylib.IsKeyDown(KeyboardKey.Left)) yaw += 2.2f * dt;
        if (Raylib.IsKeyDown(KeyboardKey.Right)) yaw -= 2.2f * dt;

        var forward = new Vector2(MathF.Sin(yaw), MathF.Cos(yaw));
        var right = new Vector2(-forward.Y, forward.X);
        var move = Vector2.Zero;
        if (Raylib.IsKeyDown(KeyboardKey.W) || Raylib.IsKeyDown(KeyboardKey.Up)) move += forward;
        if (Raylib.IsKeyDown(KeyboardKey.S) || Raylib.IsKeyDown(KeyboardKey.Down)) move -= forward;
        if (Raylib.IsKeyDown(KeyboardKey.D)) move += right;
        if (Raylib.IsKeyDown(KeyboardKey.A)) move -= right;
        if (move != Vector2.Zero)
        {
            float speed = Raylib.IsKeyDown(KeyboardKey.LeftShift) || Raylib.IsKeyDown(KeyboardKey.RightShift) ? RunSpeed : WalkSpeed;
            move = Vector2.Normalize(move) * speed * dt;
        }
        _pose = _map.Slide(_pose, move.X, move.Y, yaw);
    }

    private void Submit(Hud hud, string line, double now)
    {
        switch (PlayerCommands.Parse(line))
        {
            case PlayerInput.Send(var message):
                if (!client.Send(message)) _overlay = "La connessione con il Demiurgo si è interrotta.";
                break;
            case PlayerInput.ShowHelp:
                _showHelp = _helpPinned = true;
                break;
            case PlayerInput.Invalid(var reason):
                hud.Add(new StyledText().Add("! " + reason, Tone.Pompeian), now);
                break;
            case PlayerInput.Quit:
                _quit = true;
                break;
        }
    }

    private const double CombatRespawnDelay = LexArcana.Combat.CombatEngine.RespawnDelay;

    /// <summary>Il nemico più vicino sotto il mirino, se c'è e si vede.</summary>
    private int? Aim(Vector3 eye, Vector3 forward)
    {
        int? best = null;
        float bestDistance = float.MaxValue;
        foreach (var e in _enemies.Values)
        {
            if (e.DiedAt is not null) continue;
            var center = new Vector3(e.Position.X, e.Height * 0.55f, e.Position.Y);
            var to = center - eye;
            float distance = to.Length();
            if (distance > LexArcana.Combat.Fighter.RangedReach || distance < 0.01f) continue;
            float angle = MathF.Acos(Math.Clamp(Vector3.Dot(to / distance, forward), -1, 1));
            if (angle > MathF.Atan(0.75f * e.Scale / distance) + 0.05f) continue;
            if (!_map.HasLineOfSight(eye.X, eye.Z, e.Position.X, e.Position.Y)) continue;
            if (distance < bestDistance) { best = e.Id; bestDistance = distance; }
        }
        return best;
    }

    /// <summary>Solo per le prove automatiche: guarda il nemico più vicino che si vede e gli va incontro.</summary>
    private void AutoFight(float dt)
    {
        var me = new Vector2(_pose.X, _pose.Z);
        var target = _enemies.Values.Where(e => e.DiedAt is null && _map.HasLineOfSight(me.X, me.Y, e.Position.X, e.Position.Y))
            .OrderBy(e => Vector2.Distance(me, e.Position)).FirstOrDefault();
        if (target is null) return;
        var to = target.Position - me;
        float distance = to.Length();
        float yaw = MathF.Atan2(to.X, to.Y);
        _pitch = MathF.Atan2(target.Height * 0.55f - EyeHeight, distance) * 0.8f;
        var step = distance > 1.8f ? Vector2.Normalize(to) * WalkSpeed * dt : Vector2.Zero;
        _pose = _map.Slide(_pose, step.X, step.Y, yaw);
        _autoRanged = distance > LexArcana.Combat.Fighter.MeleeReach;
        _autoAttack = true;
    }

    private bool _autoAttack, _autoRanged;

    private void HandleAttack(Vector3 eye, Vector3 forward, double now)
    {
        if (!(_mouseCaptured || options.AutoFight) || now < _nextAttackAt) return;
        bool melee = Raylib.IsMouseButtonPressed(MouseButton.Left) || Raylib.IsKeyPressed(KeyboardKey.F) || (_autoAttack && !_autoRanged);
        bool ranged = Raylib.IsMouseButtonPressed(MouseButton.Right) || Raylib.IsKeyPressed(KeyboardKey.R) || (_autoAttack && _autoRanged);
        _autoAttack = false;
        if (!melee && !ranged) return;
        _nextAttackAt = now + LocalCooldown;
        if (melee) _swingAt = now;
        if (_aimed is { } id && _enemies.TryGetValue(id, out var target))
        {
            if (ranged) _shots.Add(new Shot(eye + forward * 0.5f - Vector3.UnitY * 0.15f, new Vector3(target.Position.X, target.Height * 0.55f, target.Position.Y), now));
            client.Send(new Attack(id, ranged));
        }
        else if (ranged) _shots.Add(new Shot(eye + forward * 0.5f, eye + forward * 14f, now));
    }

    private void OnStruck(Struck s, double now)
    {
        if (s.TargetEnemyId is { } enemyId && _enemies.TryGetValue(enemyId, out var enemy))
        {
            enemy.Health = s.Health;
            var head = new Vector3(enemy.Position.X, enemy.Height + 0.1f, enemy.Position.Y);
            if (s.Hit)
            {
                enemy.FlashUntil = now + 0.15;
                _floating.Add(new FloatingText(head, s.Critical ? $"{s.Damage}!" : s.Damage.ToString(System.Globalization.CultureInfo.InvariantCulture), s.Critical ? Tone.Gilt : Tone.Ink, now));
            }
            else _floating.Add(new FloatingText(head, "mancato", Tone.Muted, now));
            if (s.Defeated) enemy.DiedAt = now;
        }
        if (s.AttackerEnemyId is { } attackerId && _enemies.TryGetValue(attackerId, out var attacker)) attacker.AttackAt = now;
        if (s.TargetEnemyId is null && s.Target.IsSameName(client.Name) && s.Hit) _hurtAt = now;
        if (s.TargetEnemyId is null && !s.Target.IsSameName(client.Name) && _avatars.TryGetValue(s.Target, out var ally) && s.Hit)
            _floating.Add(new FloatingText(new Vector3(ally.Position.X, 2.0f, ally.Position.Y), s.Damage.ToString(System.Globalization.CultureInfo.InvariantCulture), Tone.Pompeian, now));
    }

    private void DrawEnemyTags(Hud hud, Camera3D camera, Vector3 eye, Vector3 forward)
    {
        foreach (var e in _enemies.Values.Where(e => e.DiedAt is null).OrderByDescending(e => Vector2.Distance(e.Position, new(eye.X, eye.Z))))
        {
            var head = new Vector3(e.Position.X, e.Height + 0.25f, e.Position.Y);
            var to = head - eye;
            float distance = to.Length();
            if (distance > 24 || Vector3.Dot(to, forward) <= 0.2f) continue;
            if (!_map.HasLineOfSight(eye.X, eye.Z, e.Position.X, e.Position.Y)) continue;
            hud.DrawEnemyTag(e, Raylib.GetWorldToScreen(head, camera), distance, e.Id == _aimed);
        }
    }

    /// <summary>All'uscita, scrive nel file del personaggio l'esperienza guadagnata.</summary>
    private void SaveExperience()
    {
        if (options.Character is not { } character || options.CharacterPath is not { } path || _vitals is not { } v) return;
        if (v.Experience <= character.Experience) return;
        try
        {
            File.WriteAllText(path, (character with { Experience = v.Experience }).ToJson());
            Console.WriteLine($"Esperienza salvata in {path}: {v.Experience} (livello {v.Level}).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Impossibile salvare l'esperienza in {path}: {ex.Message}");
        }
    }

    /// <summary>Manda la posizione al server al massimo 10 volte al secondo, e solo se è cambiata.</summary>
    private void SendPose(double now)
    {
        if (_overlay is not null || options.Frame is not null || now - _lastSentAt < MoveInterval.TotalSeconds) return;
        if (_pose.DistanceTo(_lastSent) < 0.01f && MathF.Abs(MathF.IEEERemainder(_pose.Yaw - _lastSent.Yaw, MathF.Tau)) < 0.01f) return;
        client.Send(new Move(_pose.X, _pose.Z, _pose.Yaw));
        _lastSent = _pose;
        _lastSentAt = now;
    }

    private void DrawNameTags(Hud hud, Camera3D camera, Vector3 eye, Vector3 forward)
    {
        foreach (var avatar in _avatars.Values.OrderByDescending(a => Vector2.Distance(a.Position, new(eye.X, eye.Z))))
        {
            var head = new Vector3(avatar.Position.X, 1.82f, avatar.Position.Y);
            var toHead = head - eye;
            float distance = toHead.Length();
            if (distance > 30 || Vector3.Dot(toHead, forward) <= 0.2f) continue;
            if (!_map.HasLineOfSight(eye.X, eye.Z, avatar.Position.X, avatar.Position.Y)) continue;
            hud.DrawNameTag(avatar.Name, Raylib.GetWorldToScreen(head, camera), distance);
        }
    }
}
