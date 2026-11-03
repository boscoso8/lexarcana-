namespace LexArcana.Game;

/// <summary>
/// La scheda di un personaggio: campi liberi nome → valore (Virtù, Ferite, Equipaggiamento…).
/// I campi restano nell'ordine in cui sono stati scritti; i nomi non distinguono maiuscole e minuscole.
/// </summary>
public sealed class CharacterSheet(string owner)
{
    public const int MaxFields = 40;
    public const int MaxKeyLength = 32;
    public const int MaxValueLength = 200;

    private readonly OrderedDictionary<string, string> _fields = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public string Owner { get; } = owner;

    /// <summary>Imposta un campo; un valore vuoto lo rimuove.</summary>
    public bool TrySet(string? key, string? value, out string error)
    {
        error = "";
        key = key.Sanitized.Trim();
        value = value.Sanitized.Trim();
        if (key.Length is 0 or > MaxKeyLength)
        {
            error = $"Il nome del campo deve avere da 1 a {MaxKeyLength} caratteri.";
            return false;
        }
        if (value.Length > MaxValueLength)
        {
            error = $"Il valore può avere al massimo {MaxValueLength} caratteri.";
            return false;
        }

        lock (_lock)
        {
            if (value.Length == 0)
            {
                _fields.Remove(key);
                return true;
            }
            if (_fields.TryGetValue(key, out _, out int index))
            {
                _fields.SetAt(index, value);
                return true;
            }
            if (_fields.Count >= MaxFields)
            {
                error = $"Una scheda ha al massimo {MaxFields} campi.";
                return false;
            }
            _fields.Add(key, value);
            return true;
        }
    }

    public Dictionary<string, string> Snapshot()
    {
        lock (_lock) return new(_fields);
    }
}
