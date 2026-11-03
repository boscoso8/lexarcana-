namespace LexArcana.Campaigns;

public static class CampaignFiles
{
    /// <summary>Scrive la campagna in una cartella: campagna.json, campagna.md e avventura-N.txt. Restituisce i file scritti.</summary>
    public static IReadOnlyList<string> Write(Campaign campaign, string folder)
    {
        Directory.CreateDirectory(folder);
        List<string> written = [];
        void Save(string name, string content)
        {
            var path = Path.Combine(folder, name);
            File.WriteAllText(path, content);
            written.Add(path);
        }
        Save("campagna.json", campaign.ToJson());
        Save("campagna.md", CampaignWriter.ToMarkdown(campaign));
        foreach (var adventure in campaign.Adventures) Save($"avventura-{adventure.Number}.txt", CampaignWriter.ToMapFile(adventure));
        return written;
    }

    public static bool TryLoad(string path, out Campaign campaign, out string error)
    {
        campaign = null!;
        error = "";
        try
        {
            campaign = Campaign.FromJson(File.ReadAllText(path));
            if (campaign.Adventures.Length == 0) error = "La campagna non ha avventure.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            error = $"Impossibile leggere la campagna {path}: {ex.Message}";
        }
        return error.Length == 0;
    }
}
