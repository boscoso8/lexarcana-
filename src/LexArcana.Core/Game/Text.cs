namespace LexArcana.Game;

/// <summary>Regole sul testo condivise da server e client (membri di estensione di C# 14).</summary>
public static class Text
{
    public const int MaxMessageLength = 1000;

    extension(string? text)
    {
        /// <summary>Il testo senza caratteri di controllo: nessuno può inviare sequenze ANSI ai terminali degli altri.</summary>
        public string Sanitized => text is null ? "" : string.Concat(text.Where(c => !char.IsControl(c)));

        /// <summary>Il testo ripulito e accorciato, o null se è vuoto o supera <see cref="MaxMessageLength"/>.</summary>
        public string? AsMessage => text.Sanitized.Trim() is { Length: > 0 and <= MaxMessageLength } clean ? clean : null;

        public bool IsSameName(string? other) => string.Equals(text, other, StringComparison.OrdinalIgnoreCase);
    }
}

public static class Names
{
    /// <summary>Il nome riservato a chi conduce la partita dal server.</summary>
    public const string Demiurge = "Demiurgo";
    public const int MaxLength = 24;

    /// <summary>Un nome valido ha 1–24 lettere, cifre, '_' o '-'.</summary>
    public static bool IsValid(string? name) =>
        name is { Length: > 0 and <= MaxLength } && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-');

    public static bool IsDemiurge(string? name) => name.IsSameName(Demiurge);
}
