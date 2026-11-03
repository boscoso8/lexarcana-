using System.Text.Json;
using System.Text.Json.Serialization;

namespace LexArcana.Campaigns;

/// <summary>Un personaggio non giocante: chi è, come si riconosce e che cosa nasconde.</summary>
public sealed record Npc(string Name, string Role, string Trait, string Secret);

/// <summary>Un'avventura della campagna, con la mappa del suo luogo.</summary>
public sealed record Adventure(
    int Number,
    string Title,
    PlaceKind Kind,
    string Place,
    string Hook,
    string Objective,
    string[] Scenes,
    Npc[] Npcs,
    string Complication,
    string Climax,
    string Reward,
    string[] Map);

/// <summary>Una campagna: una minaccia che cresce di avventura in avventura fino allo scontro finale.</summary>
public sealed record Campaign(
    int Seed,
    string Title,
    string Province,
    string City,
    string Threat,
    string Villain,
    string Patron,
    string Premise,
    Adventure[] Adventures)
{
    public string ToJson() => JsonSerializer.Serialize(this, CampaignJson.Readable.Campaign);

    public static Campaign FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, CampaignJson.Default.Campaign) ?? throw new FormatException("File vuoto.");
        }
        catch (JsonException ex)
        {
            throw new FormatException("Il file della campagna non è valido: " + ex.Message, ex);
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UseStringEnumConverter = true, RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(Campaign))]
internal sealed partial class CampaignJson : JsonSerializerContext
{
    public static CampaignJson Readable => field ??= new(new JsonSerializerOptions(Default.Options) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
