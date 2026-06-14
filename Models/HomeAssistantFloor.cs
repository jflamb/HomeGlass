using System.Text.Json.Serialization;

namespace HomeGlass.Models;

public sealed record HomeAssistantFloor(
    [property: JsonPropertyName("floor_id")] string FloorId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("icon")] string? Icon,
    [property: JsonPropertyName("aliases")] IReadOnlyList<string>? Aliases);
