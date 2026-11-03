using System.Net.Sockets;
using LexArcana.Protocol;

namespace LexArcana.Net;

/// <summary>
/// Il client di un giocatore: si collega al server del Demiurgo, si presenta con un nome e
/// riceve i messaggi della sessione con <see cref="ReadAllAsync"/>.
/// </summary>
public sealed class GameClient : IAsyncDisposable
{
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(15);
    private readonly IConnection _connection;

    private GameClient(IConnection connection, Welcome welcome)
    {
        _connection = connection;
        Welcome = welcome;
    }

    public Welcome Welcome { get; }
    public string Name => Welcome.Name;

    /// <summary>Si collega via TCP e si presenta. Se il server rifiuta, lancia <see cref="ConnectionRefusedException"/> con il motivo.</summary>
    public static async Task<GameClient> ConnectAsync(string host, int port, string name, string? password = null, CancellationToken ct = default)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, ct);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
        return await HelloAsync(new LineConnection(tcp), name, password, ct);
    }

    /// <summary>
    /// Si collega via WebSocket (es. <c>ws://host:8080/ws</c>) e si presenta: è la strada dei browser, iPhone compreso.
    /// </summary>
    public static async Task<GameClient> ConnectWebSocketAsync(Uri uri, string name, string? password = null, CancellationToken ct = default)
    {
        var socket = new System.Net.WebSockets.ClientWebSocket();
        try
        {
            await socket.ConnectAsync(uri, ct);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        return await HelloAsync(new WebSocketConnection(socket, uri.ToString()), name, password, ct);
    }

    private static async Task<GameClient> HelloAsync(IConnection connection, string name, string? password, CancellationToken ct)
    {
        try
        {
            connection.Post(Wire.Encode(new Hello(name, string.IsNullOrEmpty(password) ? null : password)));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(HelloTimeout);
            var reply = await connection.ReadLineAsync(timeout.Token) is { } line
                ? Wire.DecodeServer(line)
                : throw new ConnectionRefusedException("Il server ha chiuso la connessione.");
            return reply switch
            {
                Welcome welcome => new GameClient(connection, welcome),
                Refused(var reason) => throw new ConnectionRefusedException(reason),
                _ => throw new ConnectionRefusedException("Risposta inattesa dal server."),
            };
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Mette in coda un messaggio per il server; false se la connessione è chiusa.</summary>
    public bool Send(ClientMessage message) => _connection.Post(Wire.Encode(message));

    /// <summary>
    /// I messaggi del server, finché la connessione resta aperta. Termina normalmente quando
    /// il server chiude; lancia <see cref="ProtocolException"/> se riceve qualcosa di illeggibile.
    /// </summary>
    public async IAsyncEnumerable<ServerMessage> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        while (true)
        {
            string? line;
            try { line = await _connection.ReadLineAsync(ct); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException or System.Net.WebSockets.WebSocketException) { yield break; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { yield break; } // chiusa da noi
            if (line is null) yield break;
            yield return Wire.DecodeServer(line);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Send(new Leave());
        await _connection.FlushAsync(TimeSpan.FromSeconds(1));
        await _connection.DisposeAsync();
    }
}

public sealed class ConnectionRefusedException(string message) : Exception(message);
