using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using LexArcana.Game;
using LexArcana.Protocol;

namespace LexArcana.Net;

/// <summary>
/// Il server della sessione, condotto dal Demiurgo. Accetta i giocatori via TCP, tira tutti i dadi
/// (così nessun client può barare), conserva le schede e inoltra i messaggi.
/// Il Demiurgo non è un client: vede ogni messaggio tramite <c>demiurgeView</c>, compresi
/// sussurri e tiri segreti, e agisce con <see cref="ExecuteAsync"/>.
/// </summary>
public sealed class GameServer(IPAddress address, int port, Action<ServerMessage> demiurgeView, string? password = null, Dice? dice = null)
    : IAsyncDisposable
{
    public const int DefaultPort = 7777;
    public const int MaxPlayers = 16;
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(15);

    private readonly TcpListener _listener = new(address, port);
    private readonly Dice _dice = dice ?? new Dice();
    private readonly byte[]? _password = string.IsNullOrEmpty(password) ? null : Encoding.UTF8.GetBytes(password);
    private readonly ConcurrentDictionary<string, Player> _players = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CharacterSheet> _sheets = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Task, byte> _sessions = new();
    private readonly CancellationTokenSource _stopping = new();
    private Task? _acceptLoop;

    private sealed record Player(string Name, LineConnection Connection)
    {
        public bool Send(ServerMessage message) => Connection.Post(Wire.Encode(message));
    }

    /// <summary>La porta effettiva (utile se si è chiesta la porta 0).</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string[] PlayerNames => [.. _players.Keys.Order(StringComparer.OrdinalIgnoreCase)];

    public void Start()
    {
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_stopping.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await _listener.AcceptTcpClientAsync(ct); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            var session = RunSessionAsync(new LineConnection(tcp), ct);
            _sessions.TryAdd(session, 0);
            _ = session.ContinueWith(t => _sessions.TryRemove(t, out _), TaskScheduler.Default);
        }
    }

    private async Task RunSessionAsync(LineConnection connection, CancellationToken ct)
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
                Broadcast(new Left(player.Name));
            await connection.DisposeAsync();
        }
    }

    private async Task<Player?> HandshakeAsync(LineConnection connection, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HelloTimeout);
        if (await connection.ReadLineAsync(timeout.Token) is not { } line) return null;

        var player = Wire.DecodeClient(line) switch
        {
            not Hello => Refuse("Il primo messaggio deve essere hello."),
            Hello { Version: not Wire.ProtocolVersion } => Refuse($"Versione del protocollo non supportata: il server usa la {Wire.ProtocolVersion}. Aggiorna il client."),
            Hello h when !PasswordMatches(h.Password) => Refuse("Parola d'ordine errata."),
            Hello h when !Names.IsValid(h.Name) => Refuse($"Scegli un nome di 1–{Names.MaxLength} lettere o cifre, senza spazi."),
            Hello h when Names.IsDemiurge(h.Name) => Refuse($"Il nome «{Names.Demiurge}» è riservato a chi conduce la partita."),
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
        player.Send(new Welcome(player.Name, Present()));
        Broadcast(new Joined(player.Name), except: player.Name);
        return player;

        Player? Refuse(string reason)
        {
            connection.Post(Wire.Encode(new Refused(reason)));
            return null;
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
        ShowSheet(var owner) => Sheet(owner ?? player.Name),
        ListPlayers => new PlayerList(Present()),
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
        demiurgeView(rolled); // un tiro segreto lo vedono solo chi tira e il Demiurgo
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
        demiurgeView(whisper); // il Demiurgo sente ogni sussurro
        target?.Send(whisper);
        if (!Names.IsDemiurge(from) && _players.TryGetValue(from, out var sender)) sender.Send(whisper);
        return null;
    }

    private ServerMessage SetSheetField(string actor, string owner, string? key, string? value)
    {
        var sheet = SheetOf(owner);
        if (!sheet.TrySet(key, value, out var error)) return new Refused(error);
        var updated = new SheetShown(sheet.Owner, sheet.Snapshot(), actor);
        if (!Names.IsDemiurge(actor)) demiurgeView(updated);
        return updated;
    }

    private ServerMessage Sheet(string owner) =>
        _sheets.TryGetValue(owner, out var sheet)
            ? new SheetShown(sheet.Owner, sheet.Snapshot())
            : new Refused($"Non esiste una scheda di «{owner}».");

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

    // ---- Utilità

    private string[] Present() => [Names.Demiurge, .. PlayerNames.Select(n => _players.TryGetValue(n, out var p) ? p.Name : n)];

    private void Broadcast(ServerMessage message, string? except = null)
    {
        demiurgeView(message);
        var line = Wire.Encode(message);
        foreach (var player in _players.Values)
            if (!player.Name.IsSameName(except))
                player.Connection.Post(line);
    }

    public async ValueTask DisposeAsync()
    {
        var closing = Wire.Encode(new Expelled("Il Demiurgo ha chiuso la sessione."));
        foreach (var player in _players.Values) player.Connection.Post(closing);
        await Task.WhenAll(_players.Values.Select(p => p.Connection.FlushAsync(TimeSpan.FromSeconds(1))));
        await _stopping.CancelAsync();
        _listener.Stop();
        foreach (var player in _players.Values) player.Connection.Close();
        if (_acceptLoop is not null) await _acceptLoop;
        await Task.WhenAll(_sessions.Keys);
        _stopping.Dispose();
    }
}
