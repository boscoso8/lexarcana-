namespace LexArcana.Net;

/// <summary>
/// Un canale di messaggi testuali verso un client: una riga JSON per messaggio.
/// Lo implementano la connessione TCP (<see cref="LineConnection"/>) e quella WebSocket dei browser (<see cref="WebSocketConnection"/>).
/// </summary>
public interface IConnection : IAsyncDisposable
{
    string RemoteEndPoint { get; }
    /// <summary>Si attiva quando la connessione si chiude, da qualunque lato.</summary>
    CancellationToken Closed { get; }
    /// <summary>Mette in coda un messaggio; false se la connessione è chiusa o l'altro capo non legge più.</summary>
    bool Post(string line);
    /// <summary>Attende che i messaggi in coda siano partiti (o che la connessione si chiuda).</summary>
    Task FlushAsync(TimeSpan timeout);
    /// <summary>Il prossimo messaggio, o null quando l'altro capo chiude.</summary>
    Task<string?> ReadLineAsync(CancellationToken ct);
    void Close();
}
