using System.Text.Json;
using System.Text.Json.Serialization;

namespace HomeGlass.Models;

public sealed record HomeAssistantArea(
    [property: JsonPropertyName("area_id")] string AreaId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("icon")] string? Icon,
    [property: JsonPropertyName("floor_id")] string? FloorId,
    [property: JsonPropertyName("aliases")] IReadOnlyList<string>? Aliases)
{
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; init; }
}
