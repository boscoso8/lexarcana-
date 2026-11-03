using System.Net.Sockets;
using LexArcana.Characters;
using LexArcana.Net;
using LexArcana.Protocol;
using LexArcana.Ui;

namespace LexArcana.App;

/// <summary>Il terminale di un giocatore collegato al server del Demiurgo.</summary>
internal static class PlayerConsole
{
    public static async Task<int> RunAsync(Options options, Palette p, CancellationToken stop)
    {
        var host = options.Host ?? "127.0.0.1";
        Character? character = null;
        if (options.CharacterFile is { } file)
        {
            if (!CharacterFile.TryLoad(file, out var loaded, out var characterError))
            {
                await Console.Error.WriteLineAsync(p.Pompeian(characterError));
                return 1;
            }
            character = loaded;
        }
        var name = options.Name ?? character!.Player;
        GameClient client;
        try
        {
            client = await GameClient.ConnectToAsync(host, options.Port, name, options.Password, stop);
        }
        catch (ConnectionRefusedException ex)
        {
            await Console.Error.WriteLineAsync(p.Pompeian("Il server ha rifiutato l'accesso: " + ex.Message));
            return 1;
        }
        catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or HttpRequestException)
        {
            await Console.Error.WriteLineAsync(p.Pompeian($"Impossibile collegarsi al tavolo {host}: {ex.Message}"));
            return 1;
        }
        catch (SocketException ex)
        {
            await Console.Error.WriteLineAsync(p.Pompeian($"Impossibile collegarsi a {host}:{options.Port}: {ex.Message}"));
            return 1;
        }

        await using (client)
        {
            var renderer = new MessageRenderer(client.Name);
            Terminal.WriteLine(p, renderer.Render(client.Welcome));
            if (character is not null)
                client.Send(new Embody(character));

            using var session = CancellationTokenSource.CreateLinkedTokenSource(stop);
            var receiving = ReceiveAsync(client, renderer, p, session);
            bool demiurge = LexArcana.Game.Names.IsDemiurge(client.Name);
            if (demiurge) Terminal.WriteLine(p.Gilt("Conduci da remoto: ") + p.Muted("ogni riga è un comando del Demiurgo, /aiuto per l'elenco, /esci per lasciare."));
            await foreach (var line in Terminal.ReadLinesAsync(session.Token))
            {
                if (demiurge)
                {
                    if (line.Trim() == "/esci") { await session.CancelAsync(); break; }
                    if (line.Trim().Length > 0 && !client.Send(new Command(line))) await session.CancelAsync();
                    continue;
                }
                switch (PlayerCommands.Parse(line))
                {
                    case PlayerInput.Send(var message):
                        if (!client.Send(message)) await session.CancelAsync();
                        break;
                    case PlayerInput.ShowHelp:
                        Terminal.WriteLine(p.Muted(PlayerCommands.Help));
                        break;
                    case PlayerInput.Invalid(var reason):
                        Terminal.WriteLine(p.Pompeian("! " + reason));
                        break;
                    case PlayerInput.Quit:
                        await session.CancelAsync();
                        break;
                }
            }
            await session.CancelAsync();
            return await receiving;
        }
    }

    /// <summary>Mostra i messaggi del server; restituisce 0 se si è usciti di propria volontà.</summary>
    private static async Task<int> ReceiveAsync(GameClient client, MessageRenderer renderer, Palette p, CancellationTokenSource session)
    {
        string? farewell = "Connessione al server persa.";
        try
        {
            await foreach (var message in client.ReadAllAsync(session.Token))
            {
                Terminal.WriteLine(p, renderer.Render(message));
                if (message is Expelled) farewell = null; // il motivo è già stato mostrato
            }
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (ProtocolException ex)
        {
            farewell = ex.Message;
        }

        if (farewell is not null) Terminal.WriteLine(p.Muted(farewell));
        await session.CancelAsync();
        return 1;
    }
}
