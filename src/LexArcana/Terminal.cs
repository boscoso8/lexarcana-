namespace LexArcana.App;

/// <summary>Scrive sul terminale da più thread senza mescolare le righe.</summary>
internal static class Terminal
{
    private static readonly Lock Gate = new();

    public static void WriteLine(string text)
    {
        lock (Gate) Console.WriteLine(text);
    }

    /// <summary>Scrive un messaggio già impaginato; non fa nulla se è null (es. uno spostamento).</summary>
    public static void WriteLine(Ui.Palette palette, Ui.StyledText? text)
    {
        if (text is not null) WriteLine(palette.Format(text));
    }

    /// <summary>Le righe digitate, finché l'ingresso resta aperto o finché <paramref name="ct"/> non si attiva.</summary>
    public static async IAsyncEnumerable<string> ReadLinesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var input = new StreamReader(Console.OpenStandardInput(), System.Text.Encoding.UTF8);
        while (!ct.IsCancellationRequested)
        {
            string? line;
            try { line = await input.ReadLineAsync(ct); }
            catch (OperationCanceledException) { yield break; }
            if (line is null) yield break;
            yield return line;
        }
    }
}
