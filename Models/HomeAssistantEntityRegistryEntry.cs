using System.Text.Json.Serialization;

namespace HomeGlass.Models;

public sealed record HomeAssistantEntityRegistryEntry(
    [property: JsonPropertyName("entity_id")] string EntityId,
    [property: JsonPropertyName("area_id")] string? AreaId,
    [property: JsonPropertyName("device_id")] string? DeviceId,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("original_name")] string? OriginalName,
    [property: JsonPropertyName("platform")] string? Platform,
    [property: JsonPropertyName("disabled_by")] string? DisabledBy,
    [property: JsonPropertyName("hidden_by")] string? HiddenBy,
    [property: JsonPropertyName("entity_category")] string? EntityCategory);
