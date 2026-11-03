using LexArcana.Campaigns;
using LexArcana.Ui;

namespace LexArcana.App;

/// <summary>Genera una campagna e la scrive su disco.</summary>
internal static class CampaignConsole
{
    public static int Run(Options options, Palette p)
    {
        int seed = options.Seed ?? Random.Shared.Next(1, 1_000_000);
        var campaign = CampaignGenerator.Generate(seed, options.Adventures);
        var folder = options.Folder ?? Path.Combine("campagne", Slug(campaign.Title));
        IReadOnlyList<string> files;
        try
        {
            files = CampaignFiles.Write(campaign, folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(p.Pompeian($"Impossibile scrivere in {folder}: {ex.Message}"));
            return 1;
        }

        Terminal.WriteLine(p.Gilt(p.Bold(campaign.Title)) + p.Muted($" · seme {seed}"));
        Terminal.WriteLine(p.Ink(campaign.Premise));
        foreach (var a in campaign.Adventures)
            Terminal.WriteLine($"  {p.Bronze($"{a.Number}.")} {p.Ink(a.Title)} {p.Muted($"· {a.Place}")}");
        Terminal.WriteLine(p.Muted($"\nScritti {files.Count} file in {folder}. Il manuale del Demiurgo è campagna.md."));
        Terminal.WriteLine(p.Muted("Per giocare: ") + p.Ink($"lexarcana server --campagna {Path.Combine(folder, "campagna.json")}"));
        return 0;
    }

    private static string Slug(string title)
    {
        var chars = title.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .Select(c => char.IsLetterOrDigit(c) ? c : '-');
        return string.Join('-', new string([.. chars]).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }
}
