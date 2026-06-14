using System.Text.Json.Serialization;

namespace HomeGlass.Models;

public sealed record HomeAssistantTokenSet(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("token_type")] string TokenType)
{
    public DateTimeOffset ExpiresAt { get; init; } = DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, ExpiresIn - 60));
}
