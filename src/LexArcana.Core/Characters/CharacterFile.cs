namespace LexArcana.Characters;

public static class CharacterFile
{
    /// <summary>Legge e controlla un personaggio salvato; in caso di problemi spiega quale.</summary>
    public static bool TryLoad(string path, out Character character, out string error)
    {
        character = null!;
        try
        {
            character = Character.FromJson(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            error = $"Impossibile leggere il personaggio {path}: {ex.Message}";
            return false;
        }
        var problems = character.Validate();
        error = problems.Count == 0 ? "" : $"Il personaggio in {path} non rispetta le regole: {string.Join(" ", problems)}";
        return problems.Count == 0;
    }

    /// <summary>Il nome di file proposto per un personaggio: il nome del giocatore in minuscolo.</summary>
    public static string DefaultPath(Character character) => character.Player.ToLowerInvariant() + ".json";
}
