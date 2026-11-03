namespace LexArcana.Net;

/// <summary>Scompone le righe digitate nel terminale.</summary>
public static class CommandLine
{
    /// <summary>"/Comando resto del testo" → ("comando", "resto del testo").</summary>
    public static (string Command, string Remainder) Split(string line)
    {
        var (command, remainder) = FirstWord(line.TrimStart('/'));
        return (command.ToLowerInvariant(), remainder);
    }

    /// <summary>"parola resto del testo" → ("parola", "resto del testo"), mantenendo maiuscole e minuscole.</summary>
    public static (string First, string Remainder) FirstWord(string text)
    {
        text = text.Trim();
        int space = text.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? (text, "") : (text[..space], text[(space + 1)..].Trim());
    }
}
