namespace HomeGlass.Services;

public static class AppServices
{
    public static HttpClient HttpClient { get; } = new();

    public static HomeAssistantCredentialStore CredentialStore { get; } = new();

    public static HomeAssistantAuthService HomeAssistantAuth { get; } = new(HttpClient, CredentialStore);

    public static HomeAssistantApiClient HomeAssistantApi { get; } = new(HttpClient, HomeAssistantAuth, CredentialStore);
}
