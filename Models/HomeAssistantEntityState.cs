using System.Text.Json;
using System.Text.Json.Serialization;

namespace HomeGlass.Models;

public sealed record HomeAssistantEntityState(
    [property: JsonPropertyName("entity_id")] string EntityId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("attributes")] JsonElement Attributes,
    [property: JsonPropertyName("last_changed")] DateTimeOffset LastChanged,
    [property: JsonPropertyName("last_updated")] DateTimeOffset LastUpdated);
