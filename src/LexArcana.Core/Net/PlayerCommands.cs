using LexArcana.Protocol;

namespace LexArcana.Net;

/// <summary>Che cosa fare con una riga digitata da un giocatore.</summary>
public abstract record PlayerInput
{
    public sealed record Send(ClientMessage Message) : PlayerInput;
    public sealed record ShowHelp : PlayerInput;
    public sealed record Quit : PlayerInput;
    public sealed record Invalid(string Reason) : PlayerInput;
    public sealed record Empty : PlayerInput;
}

public static class PlayerCommands
{
    public const string Help = """
        Comandi del giocatore
          <testo>                      parla a tutta la compagnia
          /tira <dadi>                 tira i dadi davanti a tutti, es. /tira 3d6+2
          /segreto <dadi>              tira di nascosto: lo vedete solo tu e il Demiurgo
          /sussurra <nome> <testo>     parla in privato (il Demiurgo sente tutto)
          /scheda [nome]               mostra la tua scheda o quella di un compagno
          /imposta <campo> [valore]    scrivi un campo della tua scheda; senza valore lo cancelli
          /lancia <prodigio> [nome]    un prodigio su di te o su un compagno, es. /lancia mano Livia
                                       (quelli sui nemici si lanciano nella scena 3D, con 1 2 3)
          /chi                         chi è presente
          /aiuto                       questo elenco
          /esci                        lascia la sessione
        """;

    public static PlayerInput Parse(string? line)
    {
        line = line?.Trim() ?? "";
        if (line.Length == 0) return new PlayerInput.Empty();
        if (line[0] != '/') return new PlayerInput.Send(new Say(line));

        var (command, rest) = CommandLine.Split(line);
        return command switch
        {
            "tira" or "segreto" when rest.Length == 0 => new PlayerInput.Invalid($"Uso: /{command} <dadi>, es. /{command} d20+3"),
            "tira" => new PlayerInput.Send(new RollDice(rest)),
            "segreto" => new PlayerInput.Send(new RollDice(rest, Secret: true)),
            "sussurra" => CommandLine.FirstWord(rest) is ({ Length: > 0 } to, { Length: > 0 } text)
                ? new PlayerInput.Send(new Whisper(to, text))
                : new PlayerInput.Invalid("Uso: /sussurra <nome> <testo>"),
            "scheda" => new PlayerInput.Send(new ShowSheet(rest.Length == 0 ? null : rest)),
            "imposta" => CommandLine.FirstWord(rest) is ({ Length: > 0 } key, var value)
                ? new PlayerInput.Send(new SetField(key, value))
                : new PlayerInput.Invalid("Uso: /imposta <campo> [valore], es. /imposta Ferite 2"),
            "lancia" => CommandLine.FirstWord(rest) is ({ Length: > 0 } spell, var ally)
                ? new PlayerInput.Send(new Cast(spell, null, ally.Length == 0 ? null : ally))
                : new PlayerInput.Invalid("Uso: /lancia <prodigio> [nome], es. /lancia mano Livia"),
            "chi" => new PlayerInput.Send(new ListPlayers()),
            "aiuto" or "?" => new PlayerInput.ShowHelp(),
            "esci" => new PlayerInput.Quit(),
            _ => new PlayerInput.Invalid($"Comando sconosciuto: /{command}. Scrivi /aiuto."),
        };
    }
}
