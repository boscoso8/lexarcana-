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
            client.Send(new SetSheet(character.ToSheet().ToDictionary(f => f.Key, f => f.Value)));

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
                if (_showHelp && (now > 12 || _pose.DistanceTo(_lastSent) > 0.05f || hud.Typing || options.CapturePath is not null)) _showHelp = _helpPinned;
                SendPose(now);

                var forward = new Vector3(MathF.Sin(_pose.Yaw) * MathF.Cos(_pitch), MathF.Sin(_pitch), MathF.Cos(_pose.Yaw) * MathF.Cos(_pitch));
                var eye = new Vector3(_pose.X, EyeHeight, _pose.Z);
                var camera = new Camera3D(eye, eye + forward, Vector3.UnitY, 70f, CameraProjection.Perspective);

                int count = 0;
                lights[count++] = (eye + new Vector3(0, -0.2f, 0) + forward * 0.3f, new Vector3(0.62f, 0.5f, 0.36f), 9f); // lanterna
                foreach (var fire in world.Fires(now).Take(Lighting.MaxLights - 1)) lights[count++] = fire;
                lighting.Frame(eye, lights.AsSpan(0, count));

                Raylib.BeginDrawing();
                Raylib.ClearBackground(Theme.Night);
                Raylib.BeginMode3D(camera);
                world.Draw(eye, now);
                foreach (var avatar in _avatars.Values) world.DrawAvatar(avatar);
                Raylib.EndMode3D();

                DrawNameTags(hud, camera, eye, forward);
                hud.DrawPlaceName(_map.Name);
                if (_showSheet || Raylib.IsKeyDown(KeyboardKey.C) && !hud.Typing) hud.DrawSheet(_mySheet, options.Character);
                if (_overlay is null && !hud.Typing && !_showHelp) Hud.DrawCrosshair();
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
            _mouseCaptured = true;
            Raylib.DisableCursor();
        }
        if (hud.Typing) return;

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
