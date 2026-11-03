using System.Net.Sockets;
using LexArcana.Game;
using LexArcana.Net;
using LexArcana.Visione;

// Tutto avviene sul thread principale: la finestra (GLFW) lo richiede, soprattutto su macOS.
if (!VisioneOptions.TryParse(args, out var options, out var error))
{
    if (error.Length > 0) Console.Error.WriteLine(error + "\n");
    Console.Error.WriteLine(VisioneOptions.Usage);
    return error.Length > 0 ? 2 : 0;
}

GameClient client;
try
{
    client = GameClient.ConnectAsync(options.Host, options.Port, options.Name, options.Password).GetAwaiter().GetResult();
}
catch (ConnectionRefusedException ex)
{
    Console.Error.WriteLine("Il server ha rifiutato l'accesso: " + ex.Message);
    return 1;
}
catch (SocketException ex)
{
    Console.Error.WriteLine($"Impossibile collegarsi a {options.Host}:{options.Port}: {ex.Message}");
    return 1;
}

try
{
    if (!GameMap.TryParse(client.Welcome.Map.Rows, out var map, out var mapError))
    {
        Console.Error.WriteLine("Il server ha mandato una mappa non valida: " + mapError);
        return 1;
    }
    return new VisioneApp(client, map, options).Run();
}
finally
{
    client.DisposeAsync().AsTask().GetAwaiter().GetResult();
}
