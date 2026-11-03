using System.Net;
using LexArcana.Game;
using LexArcana.Net;

namespace LexArcana.App;

internal enum Mode { Help, Server, Client }

/// <summary>Le opzioni della riga di comando.</summary>
internal sealed record Options(Mode Mode, string? Host, int Port, string? Name, string? Password, bool Color)
{
    public const string Usage = """
        lexarcana server [--porta 7777] [--indirizzo 0.0.0.0] [--parola-d-ordine <testo>] [--senza-colori]
            Apre una sessione: tu sei il Demiurgo e conduci la partita da questo terminale.

        lexarcana client --nome <nome> [--host 127.0.0.1] [--porta 7777] [--parola-d-ordine <testo>] [--senza-colori]
            Ti collega alla sessione di un Demiurgo come giocatore.
        """;

    public static bool TryParse(string[] args, out Options options, out string error)
    {
        options = new(Mode.Help, null, GameServer.DefaultPort, null, null, true);
        error = "";
        if (args is [] or ["-h" or "--help" or "aiuto", ..]) return true;

        Mode mode;
        switch (args[0].ToLowerInvariant())
        {
            case "server": mode = Mode.Server; break;
            case "client": mode = Mode.Client; break;
            default:
                error = $"Modalità sconosciuta: {args[0]}. Usa «server» o «client».";
                return false;
        }

        string? host = null, name = null, password = null;
        int port = GameServer.DefaultPort;
        bool color = true;
        for (int i = 1; i < args.Length; i++)
        {
            var flag = args[i];
            string? Value() => i + 1 < args.Length ? args[++i] : null;
            switch (flag)
            {
                case "--porta" or "-p":
                    if (!int.TryParse(Value(), out port) || port is < 0 or > 65535)
                        return Fail("--porta vuole un numero tra 0 e 65535.", out error);
                    break;
                case "--host" or "--indirizzo":
                    host = Value();
                    if (string.IsNullOrWhiteSpace(host)) return Fail($"{flag} vuole un indirizzo.", out error);
                    if (mode == Mode.Server && !IPAddress.TryParse(host, out _))
                        return Fail("--indirizzo vuole un indirizzo IP, es. 0.0.0.0 o 127.0.0.1.", out error);
                    break;
                case "--nome" or "-n":
                    name = Value();
                    break;
                case "--parola-d-ordine":
                    password = Value();
                    if (string.IsNullOrEmpty(password)) return Fail("--parola-d-ordine vuole un testo.", out error);
                    break;
                case "--senza-colori":
                    color = false;
                    break;
                default:
                    return Fail($"Opzione sconosciuta: {flag}", out error);
            }
        }

        if (mode == Mode.Client && !Names.IsValid(name))
            return Fail($"Il client vuole --nome: da 1 a {Names.MaxLength} lettere o cifre, senza spazi.", out error);

        options = new(mode, host, port, name, password, color);
        return true;

        static bool Fail(string message, out string error)
        {
            error = message;
            return false;
        }
    }
}
