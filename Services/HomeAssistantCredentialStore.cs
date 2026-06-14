using HomeGlass.Models;
using Windows.Security.Credentials;
using Windows.Storage;

namespace HomeGlass.Services;

public sealed class HomeAssistantCredentialStore
{
    private const string ResourceName = "HomeGlass.HomeAssistant";
    private const string RefreshTokenUserName = "refresh_token";
    private const string BaseUriKey = "HomeAssistant.BaseUri";
    private const string ClientIdKey = "HomeAssistant.ClientId";
    private const string ConnectedAtKey = "HomeAssistant.ConnectedAt";

    private readonly PasswordVault _passwordVault = new();
    private readonly ApplicationDataContainer _settings = ApplicationData.Current.LocalSettings;

    public HomeAssistantConnection? GetConnection()
    {
        var baseUri = _settings.Values[BaseUriKey] as string;
        var clientId = _settings.Values[ClientIdKey] as string;
        var connectedAt = _settings.Values[ConnectedAtKey] as string;

        if (string.IsNullOrWhiteSpace(baseUri) ||
            string.IsNullOrWhiteSpace(clientId) ||
            !Uri.TryCreate(baseUri, UriKind.Absolute, out var parsedBaseUri))
        {
            return null;
        }

        return new HomeAssistantConnection(
            parsedBaseUri,
            clientId,
            DateTimeOffset.TryParse(connectedAt, out var parsedConnectedAt) ? parsedConnectedAt : DateTimeOffset.MinValue);
    }

    public string? GetRefreshToken()
    {
        try
        {
            return _passwordVault.Retrieve(ResourceName, RefreshTokenUserName).Password;
        }
        catch
        {
            return null;
        }
    }

    public void Save(HomeAssistantConnection connection, string refreshToken)
    {
        Clear();

        _settings.Values[BaseUriKey] = connection.BaseUri.ToString();
        _settings.Values[ClientIdKey] = connection.ClientId;
        _settings.Values[ConnectedAtKey] = connection.ConnectedAt.ToString("O");
        _passwordVault.Add(new PasswordCredential(ResourceName, RefreshTokenUserName, refreshToken));
    }

    public void Clear()
    {
        _settings.Values.Remove(BaseUriKey);
        _settings.Values.Remove(ClientIdKey);
        _settings.Values.Remove(ConnectedAtKey);

        try
        {
            _passwordVault.Remove(_passwordVault.Retrieve(ResourceName, RefreshTokenUserName));
        }
        catch
        {
        }
    }
}
