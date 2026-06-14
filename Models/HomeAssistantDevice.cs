using System.Text.Json.Serialization;

namespace HomeGlass.Models;

public sealed record HomeAssistantDevice(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("area_id")] string? AreaId,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("name_by_user")] string? NameByUser,
    [property: JsonPropertyName("manufacturer")] string? Manufacturer,
    [property: JsonPropertyName("model")] string? Model);
