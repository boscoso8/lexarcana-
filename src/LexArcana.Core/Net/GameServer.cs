using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using LexArcana.Campaigns;
using LexArcana.Combat;
using LexArcana.Game;
using LexArcana.Protocol;

namespace LexArcana.Net;

/// <summary>
/// Il server della sessione, condotto dal Demiurgo. Accetta i giocatori via TCP, tira tutti i dadi
/// (così nessun client può barare), conserva le schede e inoltra i messaggi.
/// Il Demiurgo non è un client: vede ogni messaggio tramite <c>demiurgeView</c>, compresi
/// sussurri e tiri segreti, e agisce con <see cref="ExecuteAsync"/>.
/// </summary>
public sealed class GameServer(IPAddress address, int port, Action<ServerMessage> demiurgeView, string? password = null, Dice? dice = null, GameMap? map = null, Campaign? campaign = null, bool peaceful = false, string? demiurgePassword = null)
    : IAsyncDisposable
{
    public const int DefaultPort = 7777;
    public const int MaxPlayers = 16;
    /// <summary>Lo spostamento massimo accettato tra due messaggi Move, in metri (copre qualche pacchetto in ritardo).</summary>
    public const float MaxStep = 3f;
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(15);

    private readonly TcpListener _listener = new(address, port);
    private readonly Dice _dice = dice ?? new Dice();
    private readonly byte[]? _password = string.IsNullOrEmpty(password) ? null : Encoding.UTF8.GetBytes(password);
    private readonly byte[]? _demiurgePassword = string.IsNullOrEmpty(demiurgePassword) ? null : Encoding.UTF8.GetBytes(demiurgePassword);

    /// <summary>
    /// Chi conduce da remoto (es. un tavolo online, dove nessuno siede davanti alla console del server):
    /// riceve tutto ciò che vede la console e può dare i comandi del Demiurgo.
    /// </summary>
    private readonly ConcurrentDictionary<IConnection, byte> _remoteDemiurges = new();

    /// <summary>Ciò che vede il Demiurgo: la console del server e chi conduce da remoto.</summary>
    private void View(ServerMessage message)
    {
        demiurgeView(message);
        if (_remoteDemiurges.IsEmpty) return;
        var line = Wire.Encode(message);
        foreach (var remote in _remoteDemiurges.Keys) remote.Post(line);
    }
    private readonly ConcurrentDictionary<string, Player> _players = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CharacterSheet> _sheets = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Pose> _poses = new(StringComparer.OrdinalIgnoreCase);
    private int _nextSpawn;

    // Combattimento: il motore non è sicuro tra thread, quindi lo si tocca solo sotto questo lock.
    private readonly Lock _combatLock = new();
    private CombatEngine? _combatEngine;
    private CombatEngine Combat => _combatEngine ??= new CombatEngine(_dice, Map);
    /// <summary>I giocatori che si muovono nella scena 3D: solo loro possono essere visti e attaccati dai nemici.</summary>
    private readonly ConcurrentDictionary<string, byte> _inWorld = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Random _random = new();
    private Task? _ticker;
    public const int TicksPerSecond = 10;
    private double Now => _clock.Elapsed.TotalSeconds;
    private readonly ConcurrentDictionary<Task, byte> _sessions = new();
    private readonly CancellationTokenSource _stopping = new();
    private Task? _acceptLoop;

    private sealed record Player(string Name, IConnection Connection)
    {
        /// <summary>
        /// Diventa true appena il benvenuto è in coda: da lì in poi riceve anche i messaggi diffusi a tutti.
        /// Prima, un messaggio diffuso potrebbe arrivargli prima del benvenuto e il client lo rifiuterebbe.
        /// </summary>
        public volatile bool Ready;

        public bool Send(ServerMessage message) => Connection.Post(Wire.Encode(message));
    }

    /// <summary>La porta effettiva (utile se si è chiesta la porta 0).</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string[] PlayerNames => [.. _players.Keys.Order(StringComparer.OrdinalIgnoreCase)];

    private volatile GameMap _map = map ?? GameMap.Default;

    /// <summary>Il luogo in cui si trova il gruppo; il Demiurgo lo cambia con /avventura.</summary>
    public GameMap Map => _map;

    /// <summary>La campagna caricata, se c'è, e l'avventura in corso (da 1).</summary>
    public Campaign? Campaign { get; } = campaign;
    public int AdventureNumber { get; private set; }

    /// <summary>
    /// Porta tutti in un altro luogo: i giocatori ripartono dai punti di partenza della nuova mappa
    /// e i client 3D ricostruiscono la scena.
    /// </summary>
    public void ChangeMap(GameMap newMap, string? title = null, PlaceKind? kind = null, int adventure = 1, bool final = false, string? boss = null)
    {
        _map = newMap;
        int i = 0;
        foreach (var name in PlayerNames) _poses[name] = newMap.Spawns[i++ % newMap.Spawns.Length];
        EnemyList enemies;
        lock (_combatLock)
        {
            Combat.Populate(newMap, kind, adventure, final, boss, _random);
            if (peaceful) Combat.Clear();
            enemies = new EnemyList([.. Combat.Enemies.Select(Info)]);
        }
        Broadcast(new MapChanged(new MapData(newMap.Name, newMap.Rows), Positions(), title));
        Broadcast(enemies);
    }

    /// <summary>Comincia un'avventura della campagna: cambia luogo e narra il gancio a tutti.</summary>
    public ServerMessage StartAdventure(int number)
    {
        if (Campaign is null) return new Refused("Nessuna campagna caricata: avvia il server con --campagna <file>.");
        if (number < 1 || number > Campaign.Adventures.Length) return new Refused($"La campagna ha le avventure da 1 a {Campaign.Adventures.Length}.");
        var adventure = Campaign.Adventures[number - 1];
        if (!GameMap.TryParse([$"nome: {adventure.Place}", .. adventure.Map], out var adventureMap, out var error))
            return new Refused($"La mappa dell'avventura {number} non è valida: {error}");
        AdventureNumber = number;
        var title = $"{number}. {adventure.Title}";
        bool final = number == Campaign.Adventures.Length;
        ChangeMap(adventureMap, title, adventure.Kind, number, final, final ? Campaign.Villain.Split(',')[0] : null);
        Broadcast(new Narrated($"{title}. {adventure.Hook}"));
        return AdventureBrief(adventure);
    }

    /// <summary>Il promemoria privato del Demiurgo per un'avventura: obiettivo, PNG, complicazione, climax.</summary>
    private static Said AdventureBrief(Adventure a)
    {
        var npcs = string.Join(" · ", a.Npcs.Select(n => $"{n.Name}, {n.Role} ({n.Trait}; segreto: {n.Secret})"));
        return new Said(Names.Demiurge,
            $"[solo per te] Obiettivo: {a.Objective} PNG: {npcs}. Complicazione: {a.Complication}. Climax: {a.Climax} Ricompensa: {a.Reward}.");
    }

    public Pose? PoseOf(string name) => _poses.TryGetValue(name, out var pose) ? pose : null;

    private void Track(Task session)
    {
        _sessions.TryAdd(session, 0);
        _ = session.ContinueWith(t => _sessions.TryRemove(t, out _), TaskScheduler.Default);
    }

    private WebGateway? _web;
    private Task? _webLoop;

    /// <summary>
    /// Apre anche la porta web: WebSocket su /ws per i giocatori nel browser (iPhone compreso) e,
    /// se c'è <paramref name="webRoot"/>, i file del client web. Va chiamato dopo <see cref="Start"/>.
    /// </summary>
    public int StartWeb(IPAddress webAddress, int webPort, string? webRoot)
    {
        _web = new WebGateway(webAddress, webPort, (connection, ct) =>
        {
            var session = RunSessionAsync(connection, ct);
            Track(session);
            return session;
        }, webRoot);
        _webLoop = _web.StartAsync(_stopping.Token);
        return _web.Port;
    }

    public int? WebPort => _web?.Port;

    public void Start()
    {
        _listener.Start();
        if (AdventureNumber == 0 && !peaceful) lock (_combatLock) Combat.Populate(Map, null, 1, false, null, _random);
        _acceptLoop = AcceptLoopAsync(_stopping.Token);
        _ticker = TickLoopAsync(_stopping.Token);
    }

    /// <summary>Quanti nemici ci sono ora (per i test e per /nemici).</summary>
    public IReadOnlyList<EnemyInfo> EnemySnapshot()
    {
        lock (_combatLock) return [.. Combat.Enemies.Select(Info)];
    }

    public Vitals? VitalsOf(string name)
    {
        lock (_combatLock) return Combat.FighterOf(name) is { } f ? ToVitals(f) : null;
    }

    private async Task TickLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / TicksPerSecond));
        double last = Now;
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                double now = Now;
                var players = _inWorld.Keys.Where(_poses.ContainsKey).ToDictionary(n => n, n => _poses[n], StringComparer.OrdinalIgnoreCase);
                List<(ServerMessage, string?)> messages;
                lock (_combatLock) messages = Translate(Combat.Tick(now, (float)Math.Min(now - last, 0.25), players));
                last = now;
                Publish(messages);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Traduce gli eventi del motore in messaggi: ogni coppia è (messaggio, destinatario); destinatario null = tutti.
    /// Va chiamato sotto <see cref="_combatLock"/>, perché legge lo stato di nemici e combattenti.
    /// </summary>
    private List<(ServerMessage Message, string? To)> Translate(IEnumerable<CombatEvent> events)
    {
        List<(ServerMessage, string?)> messages = [];
        foreach (var e in events)
            switch (e)
            {
                case EnemiesChanged:
                    messages.Add((new EnemyList([.. Combat.Enemies.Select(Info)]), null));
                    break;
                case EnemiesMoved(var enemies):
                    messages.Add((new EnemyMoves([.. enemies.Select(x => new EnemyPose(x.Id, x.Pose.X, x.Pose.Z, x.Pose.Yaw))]), null));
                    break;
                case Blow b:
                    messages.Add((new Struck(b.Attacker, b.Target, b.Roll, b.Total, b.Against, b.Hit, b.Critical, b.Damage,
                        b.Health, b.MaxHealth, b.Defeated, b.Ranged, b.AttackerEnemyId, b.TargetEnemyId), null));
                    break;
                case VitalsChanged(var f):
                    messages.Add((ToVitals(f), f.Name));
                    break;
                case LevelUp(var f):
                    messages.Add((new Narrated($"{f.Name} sale al livello {f.Level}: salute piena, colpi più sicuri."), null));
                    break;
                case Miracle m:
                    messages.Add((new SpellCast(m.Caster, m.Spell.Id, m.Spell.Name, m.Spell.Tradition.ToString(), m.Roll, m.Total, m.Against,
                        m.Success, m.Target, m.Amount, m.Duration, m.X, m.Z), null));
                    break;
                case Respawned(var f, var pose):
                    _poses[f.Name] = pose;
                    messages.Add((new Moved(f.Name, pose.X, pose.Z, pose.Yaw), null));
                    break;
            }
        return messages;
    }

    private void Publish(List<(ServerMessage Message, string? To)> messages)
    {
        foreach (var (message, to) in messages)
        {
            if (to is null) Broadcast(message);
            else if (_players.TryGetValue(to, out var player) && player.Ready) player.Send(message);
        }
    }

    private static EnemyInfo Info(Enemy e) =>
        new(e.Id, e.Kind.Id, e.Name, e.Level, e.Health, e.MaxHealth, e.Pose.X, e.Pose.Z, e.Pose.Yaw, e.Rank.ToString());

    private static Vitals ToVitals(Fighter f) =>
        new(f.Name, f.Health, f.MaxHealth, f.Level, f.Experience, f.NextLevelAt, f.IsDown, f.Resolve, f.MaxResolve, [.. f.Spells.Select(s => s.Id)]);

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await _listener.AcceptTcpClientAsync(ct); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            Track(RunSessionAsync(new LineConnection(tcp), ct));
        }
    }

    private async Task RunSessionAsync(IConnection connection, CancellationToken ct)
    {
        Player? player = null;
        try
        {
            player = await HandshakeAsync(connection, ct);
            if (player is null) return;

            while (await connection.ReadLineAsync(ct) is { } line)
            {
                var message = Wire.DecodeClient(line);
                if (message is Leave) break;
                if (Handle(player, message) is { } reply) player.Send(reply);
            }
        }
        catch (ProtocolException ex)
        {
            connection.Post(Wire.Encode(new Refused(ex.Message)));
            await connection.FlushAsync(TimeSpan.FromSeconds(1));
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
        finally
        {
            if (player is not null && _players.TryRemove(new(player.Name, player)))
            {
                _poses.TryRemove(player.Name, out _);
                _inWorld.TryRemove(player.Name, out _);
                lock (_combatLock) Combat.Leave(player.Name);
                Broadcast(new Left(player.Name));
            }
            await connection.DisposeAsync();
        }
    }

    private async Task<Player?> HandshakeAsync(IConnection connection, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HelloTimeout);
        if (await connection.ReadLineAsync(timeout.Token) is not { } line) return null;
        var first = Wire.DecodeClient(line);

        if (first is Hello { Version: Wire.ProtocolVersion } dh && Names.IsDemiurge(dh.Name) && _demiurgePassword is not null &&
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(dh.Password ?? ""), _demiurgePassword))
        {
            await RunRemoteDemiurgeAsync(connection, ct);
            return null;
        }

        var player = first switch
        {
            not Hello => Refuse("Il primo messaggio deve essere hello."),
            Hello { Version: not Wire.ProtocolVersion } => Refuse($"Versione del protocollo non supportata: il server usa la {Wire.ProtocolVersion}. Aggiorna il client."),
            Hello h when !PasswordMatches(h.Password) => Refuse("Parola d'ordine errata."),
            Hello h when !Names.IsValid(h.Name) => Refuse($"Scegli un nome di 1–{Names.MaxLength} lettere o cifre, senza spazi."),
            Hello h when Names.IsDemiurge(h.Name) => Refuse(_demiurgePassword is null
                ? $"Il nome «{Names.Demiurge}» è riservato a chi conduce la partita."
                : "Parola d'ordine del Demiurgo errata."),
            Hello when _players.Count >= MaxPlayers => Refuse("La sessione è al completo."),
            Hello h when !_players.TryAdd(h.Name, new(h.Name, connection)) => Refuse($"Il nome «{h.Name}» è già in uso."),
            Hello h => _players[h.Name],
        };
        if (player is null)
        {
            await connection.FlushAsync(TimeSpan.FromSeconds(1));
            return null;
        }

        SheetOf(player.Name);
        Vitals vitals;
        EnemyList enemyList;
        lock (_combatLock)
        {
            vitals = ToVitals(Combat.Join(player.Name));
            enemyList = new EnemyList([.. Combat.Enemies.Select(Info)]);
        }
        var spawn = Map.Spawns[(int)((uint)Interlocked.Increment(ref _nextSpawn) % Map.Spawns.Length)];
        _poses[player.Name] = spawn;
        player.Send(new Welcome(player.Name, Present(), new MapData(Map.Name, Map.Rows), Positions()));
        player.Ready = true;
        Broadcast(new Joined(player.Name), except: player.Name);
        Broadcast(new Moved(player.Name, spawn.X, spawn.Z, spawn.Yaw), except: player.Name);
        player.Send(enemyList);
        player.Send(vitals);
        return player;

        Player? Refuse(string reason)
        {
            connection.Post(Wire.Encode(new Refused(reason)));
            return null;
        }
    }

    /// <summary>La sessione di chi conduce da remoto: ogni riga che manda è un comando del Demiurgo (o testo da dire a tutti).</summary>
    private async Task RunRemoteDemiurgeAsync(IConnection connection, CancellationToken ct)
    {
        connection.Post(Wire.Encode(new Welcome(Names.Demiurge, Present(), new MapData(Map.Name, Map.Rows), Positions())));
        lock (_combatLock) connection.Post(Wire.Encode(new EnemyList([.. Combat.Enemies.Select(Info)])));
        _remoteDemiurges.TryAdd(connection, 0);
        demiurgeView(new Narrated("Il Demiurgo conduce da remoto."));
        try
        {
            while (await connection.ReadLineAsync(ct) is { } line)
            {
                var message = Wire.DecodeClient(line);
                if (message is Leave) break;
                var text = message switch { Command(var l) => l, Say(var t) => t, _ => null };
                ServerMessage? reply = text is null ? new Refused("Da remoto il Demiurgo manda solo comandi e testo.")
                    : text.Trim() is "/fine" ? new Refused("La sessione si chiude solo dalla console del server.")
                    : await ExecuteAsync(text);
                if (reply is not null) connection.Post(Wire.Encode(reply));
            }
        }
        finally
        {
            _remoteDemiurges.TryRemove(connection, out _);
        }
    }

    private bool PasswordMatches(string? given) =>
        _password is null || CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given ?? ""), _password);

    /// <summary>Un messaggio da un giocatore; restituisce la risposta solo per lui, se c'è.</summary>
    private ServerMessage? Handle(Player player, ClientMessage message) => message switch
    {
        Say(var text) => Chat(player.Name, text),
        RollDice(var expression, var secret) => RollFor(player.Name, expression, secret),
        Whisper(var to, var text) => WhisperFrom(player.Name, to, text),
        SetField(var key, var value) => SetSheetField(player.Name, player.Name, key, value),
        SetSheet(var fields) => SetSheetFields(player.Name, fields),
        Embody(var character) => EmbodyAs(player, character),
        Attack(var enemyId, var ranged) => AttackWith(player, enemyId, ranged),
        Cast(var spell, var enemyId, var ally) => CastBy(player, spell, enemyId, ally),
        ShowSheet(var owner) => Sheet(owner ?? player.Name),
        ListPlayers => new PlayerList(Present()),
        Move(var x, var z, var yaw) => MoveTo(player, new Pose(x, z, yaw)),
        Hello => new Refused("Sei già nella sessione."),
        _ => new Refused("Messaggio non previsto."),
    };

    // ---- Azioni comuni a giocatori e Demiurgo

    private Refused? Chat(string from, string? text)
    {
        if (text.AsMessage is not { } clean) return new("Il messaggio è vuoto o troppo lungo.");
        Broadcast(new Said(from, clean));
        return null;
    }

    private ServerMessage? RollFor(string from, string? expression, bool secret)
    {
        if (!Dice.TryParse(expression, out var parsed, out var error)) return new Refused(error);
        var result = _dice.Roll(parsed);
        var rolled = new Rolled(from, parsed.Text, result.Dice, parsed.Modifier, result.Total, secret);
        if (!secret)
        {
            Broadcast(rolled);
            return null;
        }
        View(rolled); // un tiro segreto lo vedono solo chi tira e il Demiurgo
        return Names.IsDemiurge(from) ? null : rolled;
    }

    private Refused? WhisperFrom(string from, string? to, string? text)
    {
        if (text.AsMessage is not { } clean) return new("Il messaggio è vuoto o troppo lungo.");
        if (string.IsNullOrWhiteSpace(to)) return new("A chi vuoi sussurrare?");
        if (from.IsSameName(to)) return new("Non puoi sussurrare a te stesso.");

        Player? target = null;
        if (!Names.IsDemiurge(to) && !_players.TryGetValue(to, out target)) return new($"«{to}» non è nella sessione.");

        var whisper = new Whispered(from, target?.Name ?? Names.Demiurge, clean);
        View(whisper); // il Demiurgo sente ogni sussurro
        target?.Send(whisper);
        if (!Names.IsDemiurge(from) && _players.TryGetValue(from, out var sender)) sender.Send(whisper);
        return null;
    }

    private ServerMessage SetSheetField(string actor, string owner, string? key, string? value)
    {
        var sheet = SheetOf(owner);
        if (!sheet.TrySet(key, value, out var error)) return new Refused(error);
        var updated = new SheetShown(sheet.Owner, sheet.Snapshot(), actor);
        if (!Names.IsDemiurge(actor)) View(updated);
        return updated;
    }

    private ServerMessage SetSheetFields(string owner, Dictionary<string, string> fields)
    {
        var sheet = SheetOf(owner);
        if (fields.Count > CharacterSheet.MaxFields) return new Refused($"Una scheda ha al massimo {CharacterSheet.MaxFields} campi.");
        foreach (var (key, value) in fields)
            if (!sheet.TrySet(key, value, out var error)) return new Refused($"{key.Sanitized}: {error}");
        var updated = new SheetShown(sheet.Owner, sheet.Snapshot(), owner);
        View(updated);
        return updated;
    }

    /// <summary>Entra in gioco con un personaggio: lo controlla, compila la scheda e prepara il combattimento.</summary>
    private ServerMessage EmbodyAs(Player player, Characters.Character character)
    {
        var problems = character.Validate();
        if (problems.Count > 0) return new Refused("Il personaggio non rispetta le regole: " + string.Join(" ", problems));
        var sheetReply = SetSheetFields(player.Name, character.ToSheet().ToDictionary(f => f.Key, f => f.Value));
        Vitals vitals;
        lock (_combatLock) vitals = ToVitals(Combat.Join(player.Name, character));
        player.Send(vitals);
        return sheetReply;
    }

    private Refused? CastBy(Player player, string spell, int? enemyId, string? ally)
    {
        var pose = _poses.GetValueOrDefault(player.Name, Map.Spawns[0]);
        var players = _poses.Where(p => _inWorld.ContainsKey(p.Key) || p.Key.IsSameName(player.Name)).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        List<(ServerMessage, string?)> messages;
        string? refusal;
        lock (_combatLock) messages = Translate(Combat.Cast(player.Name, pose, spell, enemyId, ally, Now, players, out refusal));
        // il bersaglio della Mano di Asclepio riceve la sua salute nuova, gli altri vedono il prodigio
        Publish(messages);
        return refusal is null ? null : new Refused(refusal);
    }

    private Refused? AttackWith(Player player, int enemyId, bool ranged)
    {
        if (!_poses.TryGetValue(player.Name, out var pose)) return new Refused("Non sei nella scena.");
        List<(ServerMessage, string?)> messages;
        string? refusal;
        lock (_combatLock) messages = Translate(Combat.Attack(player.Name, pose, enemyId, ranged, Now, out refusal));
        Publish(messages);
        if (messages.Any(m => m.Item1 is Struck { Defeated: true, TargetEnemyId: not null }))
            lock (_combatLock) if (Combat.Enemies.Count == 0) Broadcast(new Narrated("Il luogo è libero: non resta nessun nemico."));
        return refusal is null ? null : new Refused(refusal);
    }

    private ServerMessage Sheet(string owner) =>
        _sheets.TryGetValue(owner, out var sheet)
            ? new SheetShown(sheet.Owner, sheet.Snapshot())
            : new Refused($"Non esiste una scheda di «{owner}».");

    /// <summary>
    /// Accetta una mossa se è un numero valido, non attraversa muri e non è un salto troppo lungo;
    /// altrimenti rimanda al giocatore la sua ultima posizione valida.
    /// </summary>
    private Moved? MoveTo(Player player, Pose wanted)
    {
        var current = _poses.GetValueOrDefault(player.Name, Map.Spawns[0]);
        bool down;
        lock (_combatLock) down = Combat.FighterOf(player.Name)?.IsDown == true;
        if (down) return new Moved(player.Name, current.X, current.Z, current.Yaw); // a terra non ci si muove
        if (!wanted.IsFinite || wanted.DistanceTo(current) > MaxStep || !Map.IsWalkable(wanted.X, wanted.Z))
            return new Moved(player.Name, current.X, current.Z, current.Yaw);

        var pose = wanted with { Yaw = NormalizeAngle(wanted.Yaw) };
        _poses[player.Name] = pose;
        _inWorld.TryAdd(player.Name, 0);
        var moved = Wire.Encode(new Moved(player.Name, pose.X, pose.Z, pose.Yaw));
        foreach (var other in _players.Values)
            if (other != player && other.Ready) other.Connection.Post(moved);
        return null;
    }

    private static float NormalizeAngle(float radians)
    {
        var a = radians % MathF.Tau;
        return a < 0 ? a + MathF.Tau : a;
    }

    private Position[] Positions() =>
        [.. _players.Values.Select(p => (p.Name, Pose: _poses.GetValueOrDefault(p.Name)))
            .Select(e => new Position(e.Name, e.Pose.X, e.Pose.Z, e.Pose.Yaw))];

    private CharacterSheet SheetOf(string name) => _sheets.GetOrAdd(name, static n => new CharacterSheet(n));

    // ---- Comandi del Demiurgo

    /// <summary>
    /// Esegue una riga digitata dal Demiurgo e restituisce cosa mostrargli in risposta, se c'è.
    /// Testo libero = parlare a tutti; i comandi iniziano con '/'.
    /// </summary>
    public async Task<ServerMessage?> ExecuteAsync(string line)
    {
        line = line.Trim();
        if (line.Length == 0) return null;
        if (line[0] != '/') return Chat(Names.Demiurge, line);

        var (command, rest) = CommandLine.Split(line);
        switch (command)
        {
            case "narra":
                if (rest.AsMessage is not { } narration) return new Refused("Uso: /narra <testo>");
                Broadcast(new Narrated(narration));
                return null;
            case "tira":
                return RollFor(Names.Demiurge, rest, secret: false);
            case "segreto":
                return RollFor(Names.Demiurge, rest, secret: true);
            case "sussurra":
                var (to, text) = CommandLine.FirstWord(rest);
                return WhisperFrom(Names.Demiurge, to, text);
            case "scheda":
                return rest.Length == 0 ? new Refused("Uso: /scheda <giocatore>") : Sheet(rest);
            case "imposta":
                var (owner, field) = CommandLine.FirstWord(rest);
                var (key, value) = CommandLine.FirstWord(field);
                if (key.Length == 0) return new Refused("Uso: /imposta <giocatore> <campo> [valore]");
                if (!_sheets.ContainsKey(owner)) return new Refused($"Non esiste una scheda di «{owner}».");
                var updated = SetSheetField(Names.Demiurge, owner, key, value);
                if (updated is SheetShown && _players.TryGetValue(owner, out var player)) player.Send(updated);
                return updated;
            case "chi":
                return new PlayerList(Present());
            case "aiuto":
                return new Said(Names.Demiurge, "[solo per te] Comandi: <testo> parla a tutti · /narra · /tira · /segreto · /sussurra <nome> · /scheda <nome> · " +
                    "/imposta <nome> <campo> [valore] · /chi · /espelli <nome> · /avventura [n] · /mappa <file> · /nemici · /evoca <tipo> [livello] [capo] · /pace · /cura [nome]");
            case "avventura":
                if (rest.Length == 0) return AdventureList();
                return int.TryParse(rest, out var number) ? StartAdventure(number) : new Refused("Uso: /avventura <numero>");
            case "nemici":
            {
                var list = EnemySnapshot();
                return new Said(Names.Demiurge, list.Count == 0
                    ? "[solo per te] Nessun nemico nel luogo. /evoca <tipo> [livello] per crearne; tipi: " + string.Join(", ", Bestiary.All.Select(k => k.Id)) + "."
                    : "[solo per te] " + string.Join(" · ", list.Select(e => $"#{e.Id} {e.Name} liv. {e.Level} ({e.Health}/{e.MaxHealth})")));
            }
            case "evoca":
            {
                var (kindName, levelText) = CommandLine.FirstWord(rest);
                if (Bestiary.Find(kindName) is not { } kind)
                    return new Refused("Uso: /evoca <tipo> [livello] [capo]. Tipi: " + string.Join(", ", Bestiary.All.Select(k => k.Id)) + ".");
                var (lvText, rankText) = CommandLine.FirstWord(levelText);
                EnemyList list;
                lock (_combatLock)
                {
                    int level = int.TryParse(lvText, out var l) ? l : Progression.EncounterLevel(Math.Max(1, AdventureNumber), Combat.PartyLevel);
                    var rank = rankText.Equals("capo", StringComparison.OrdinalIgnoreCase) ? EnemyRank.Elite : EnemyRank.Normal;
                    Combat.Summon(kind, level, rank, SummoningSpot());
                    list = new EnemyList([.. Combat.Enemies.Select(Info)]);
                }
                Broadcast(list);
                return null;
            }
            case "pace":
                lock (_combatLock) Combat.Clear();
                Broadcast(new EnemyList([]));
                Broadcast(new Narrated("Cala il silenzio: i nemici svaniscono."));
                return null;
            case "cura":
            {
                List<(ServerMessage, string?)> messages = [];
                lock (_combatLock)
                    foreach (var f in Combat.Fighters.Where(f => rest.Length == 0 || f.Name.IsSameName(rest)))
                    {
                        f.Health = f.MaxHealth;
                        messages.Add((ToVitals(f), f.Name));
                    }
                if (messages.Count == 0) return new Refused($"«{rest}» non è nella sessione.");
                Publish(messages);
                Broadcast(new Narrated(rest.Length == 0 ? "Una luce calda guarisce le ferite del gruppo." : $"Una luce calda guarisce le ferite di {rest}."));
                return null;
            }
            case "mappa":
                if (rest.Length == 0) return new Refused("Uso: /mappa <file>");
                string[] lines;
                try { lines = await File.ReadAllLinesAsync(rest); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new Refused($"Impossibile leggere {rest}: {ex.Message}"); }
                if (!GameMap.TryParse(lines, out var loaded, out var mapError)) return new Refused($"Mappa non valida: {mapError}");
                ChangeMap(loaded, loaded.Name);
                return null;
            case "espelli":
                var (name, reason) = CommandLine.FirstWord(rest);
                if (!_players.TryGetValue(name, out var expelled)) return new Refused($"«{name}» non è nella sessione.");
                expelled.Send(new Expelled(reason.AsMessage));
                await expelled.Connection.FlushAsync(TimeSpan.FromSeconds(1));
                expelled.Connection.Close();
                return null;
            default:
                return new Refused($"Comando sconosciuto: /{command}. Scrivi /aiuto.");
        }
    }

    /// <summary>
    /// Dove far comparire un nemico evocato: una casella libera a 6–14 m da un giocatore e in vista,
    /// così il gruppo lo vede arrivare. Senza giocatori, o se non c'è un punto così, un pavimento qualsiasi.
    /// </summary>
    private Pose SummoningSpot()
    {
        var map = Map;
        var players = _poses.Values.ToList();
        Pose? fallback = null;
        for (int tries = 0; tries < 400; tries++)
        {
            int x = _random.Next(map.Width), z = _random.Next(map.Depth);
            if (map[x, z] != Tile.Floor) continue;
            var (cx, cz) = GameMap.CellCenter(x, z);
            fallback ??= new Pose(cx, cz, 0);
            foreach (var p in players)
            {
                float d = MathF.Sqrt((p.X - cx) * (p.X - cx) + (p.Z - cz) * (p.Z - cz));
                if (d is >= 6 and <= 14 && map.HasLineOfSight(p.X, p.Z, cx, cz))
                    return new Pose(cx, cz, MathF.Atan2(p.X - cx, p.Z - cz));
            }
        }
        return fallback ?? map.Spawns[0];
    }

    private ServerMessage AdventureList()
    {
        if (Campaign is null) return new Refused("Nessuna campagna caricata: avvia il server con --campagna <file>.");
        var list = string.Join(" · ", Campaign.Adventures.Select(a =>
            $"{(a.Number == AdventureNumber ? "▶ " : "")}{a.Number}. {a.Title} ({a.Place})"));
        return new Said(Names.Demiurge, $"[solo per te] {Campaign.Title}: {list}. Scrivi /avventura <numero> per portarci il gruppo.");
    }

    // ---- Utilità

    private string[] Present() => [Names.Demiurge, .. PlayerNames.Select(n => _players.TryGetValue(n, out var p) ? p.Name : n)];

    private void Broadcast(ServerMessage message, string? except = null)
    {
        View(message);
        var line = Wire.Encode(message);
        foreach (var player in _players.Values)
            if (player.Ready && !player.Name.IsSameName(except))
                player.Connection.Post(line);
    }

    public async ValueTask DisposeAsync()
    {
        var closing = Wire.Encode(new Expelled("Il Demiurgo ha chiuso la sessione."));
        foreach (var player in _players.Values) player.Connection.Post(closing);
        await Task.WhenAll(_players.Values.Select(p => p.Connection.FlushAsync(TimeSpan.FromSeconds(1))));
        await _stopping.CancelAsync();
        _listener.Stop();
        _web?.Stop();
        if (_webLoop is not null) await _webLoop;
        _web?.Dispose();
        if (_ticker is not null) await _ticker;
        foreach (var player in _players.Values) player.Connection.Close();
        if (_acceptLoop is not null) await _acceptLoop;
        await Task.WhenAll(_sessions.Keys);
        _stopping.Dispose();
    }
}
