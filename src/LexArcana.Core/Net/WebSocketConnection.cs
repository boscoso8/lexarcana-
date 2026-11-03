using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using LexArcana.Protocol;

namespace LexArcana.Net;

/// <summary>
/// Una connessione WebSocket dal browser (per esempio Safari su iPhone): ogni messaggio di testo è un messaggio JSON.
/// Stessi limiti della connessione TCP: messaggi al massimo di <see cref="LineConnection.MaxLineBytes"/>
/// e una coda d'uscita che, se si riempie, chiude la connessione invece di rallentare gli altri.
/// </summary>
public sealed class WebSocketConnection : IConnection
{
    private readonly WebSocket _socket;
    private readonly Channel<string> _outbox = Channel.CreateBounded<string>(
        new BoundedChannelOptions(LineConnection.MaxQueuedLines) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _closed = new();
    private readonly Task _writer;
    private readonly byte[] _buffer = new byte[4096];

    public WebSocketConnection(WebSocket socket, string remoteEndPoint)
    {
        _socket = socket;
        RemoteEndPoint = remoteEndPoint;
        _writer = WriteLoopAsync();
    }

    public string RemoteEndPoint { get; }
    public CancellationToken Closed => _closed.Token;

    public bool Post(string line)
    {
        if (_outbox.Writer.TryWrite(line)) return true;
        Close();
        return false;
    }

    public async Task FlushAsync(TimeSpan timeout)
    {
        _outbox.Writer.TryComplete();
        try { await _writer.WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }

    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);
        using var message = new MemoryStream();
        while (true)
        {
            var result = await _socket.ReceiveAsync(_buffer, linked.Token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                // RFC 6455: a una richiesta di chiusura si risponde con la propria, poi si chiude.
                _outbox.Writer.TryComplete();
                await _writer.ConfigureAwait(false);
                return null;
            }
            if (result.MessageType != WebSocketMessageType.Text) throw new ProtocolException("Sono ammessi solo messaggi di testo.");
            message.Write(_buffer, 0, result.Count);
            if (message.Length > LineConnection.MaxLineBytes) throw new ProtocolException("Messaggio troppo lungo.");
            if (!result.EndOfMessage) continue;
            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length).Trim();
            if (text.Length > 0) return text;
            message.SetLength(0);
        }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var line in _outbox.Reader.ReadAllAsync(_closed.Token).ConfigureAwait(false))
                await _socket.SendAsync(Encoding.UTF8.GetBytes(line), WebSocketMessageType.Text, true, _closed.Token).ConfigureAwait(false);
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException)
        {
        }
        finally
        {
            Close();
        }
    }

    public void Close()
    {
        _outbox.Writer.TryComplete();
        try { _closed.Cancel(); } catch (ObjectDisposedException) { }
        _socket.Abort();
    }

    public async ValueTask DisposeAsync()
    {
        Close();
        await _writer.ConfigureAwait(false);
        _socket.Dispose();
        _closed.Dispose();
    }
}
