namespace HomeGlass.Models;

public sealed record HomeAssistantConnection(
    Uri BaseUri,
    string ClientId,
    DateTimeOffset ConnectedAt);
