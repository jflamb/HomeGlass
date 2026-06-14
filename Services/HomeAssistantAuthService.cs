using System.Net.Http.Json;
using System.Security.Cryptography;
using HomeGlass.Models;
using Windows.System;

namespace HomeGlass.Services;

public sealed class HomeAssistantAuthService
{
    private readonly HttpClient _httpClient;
    private readonly HomeAssistantCredentialStore _credentialStore;
    private HomeAssistantTokenSet? _currentTokenSet;

    public HomeAssistantAuthService(HttpClient httpClient, HomeAssistantCredentialStore credentialStore)
    {
        _httpClient = httpClient;
        _credentialStore = credentialStore;
    }

    public HomeAssistantConnection? CurrentConnection => _credentialStore.GetConnection();

    public async Task<HomeAssistantConnection> ConnectAsync(string homeAssistantUrl, CancellationToken cancellationToken = default)
    {
        var baseUri = NormalizeHomeAssistantUrl(homeAssistantUrl);
        await using var callbackListener = new LoopbackAuthCallbackListener();
        callbackListener.Start();

        var state = CreateState();
        var authorizeUri = BuildAuthorizeUri(baseUri, callbackListener.ClientId, callbackListener.RedirectUri, state);
        var callbackTask = callbackListener.WaitForCodeAsync(state, cancellationToken);

        var launched = await Launcher.LaunchUriAsync(authorizeUri);
        if (!launched)
        {
            throw new InvalidOperationException("Windows could not open the Home Assistant sign-in page.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var code = await callbackTask.WaitAsync(timeout.Token);

        var tokenSet = await ExchangeAuthorizationCodeAsync(baseUri, code, callbackListener.ClientId.ToString(), cancellationToken);
        if (string.IsNullOrWhiteSpace(tokenSet.RefreshToken))
        {
            throw new InvalidOperationException("Home Assistant did not return a refresh token.");
        }

        var connection = new HomeAssistantConnection(baseUri, callbackListener.ClientId.ToString(), DateTimeOffset.UtcNow);
        _credentialStore.Save(connection, tokenSet.RefreshToken);
        _currentTokenSet = tokenSet;

        return connection;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_currentTokenSet is not null && _currentTokenSet.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return _currentTokenSet.AccessToken;
        }

        var connection = _credentialStore.GetConnection()
            ?? throw new InvalidOperationException("HomeGlass is not connected to Home Assistant.");
        var refreshToken = _credentialStore.GetRefreshToken();
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException("HomeGlass does not have a Home Assistant refresh token.");
        }

        _currentTokenSet = await RefreshAccessTokenAsync(connection.BaseUri, connection.ClientId, refreshToken, cancellationToken);
        return _currentTokenSet.AccessToken;
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        var connection = _credentialStore.GetConnection();
        var refreshToken = _credentialStore.GetRefreshToken();

        if (connection is not null && !string.IsNullOrWhiteSpace(refreshToken))
        {
            try
            {
                using var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["token"] = refreshToken,
                    ["action"] = "revoke"
                });

                await _httpClient.PostAsync(new Uri(connection.BaseUri, "auth/token"), content, cancellationToken);
            }
            catch
            {
            }
        }

        _currentTokenSet = null;
        _credentialStore.Clear();
    }

    private async Task<HomeAssistantTokenSet> ExchangeAuthorizationCodeAsync(Uri baseUri, string code, string clientId, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = clientId
        });

        return await PostTokenRequestAsync(baseUri, content, cancellationToken);
    }

    private async Task<HomeAssistantTokenSet> RefreshAccessTokenAsync(Uri baseUri, string clientId, string refreshToken, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId
        });

        return await PostTokenRequestAsync(baseUri, content, cancellationToken);
    }

    private async Task<HomeAssistantTokenSet> PostTokenRequestAsync(Uri baseUri, HttpContent content, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsync(new Uri(baseUri, "auth/token"), content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Home Assistant token request failed with {(int)response.StatusCode}: {error}");
        }

        return await response.Content.ReadFromJsonAsync<HomeAssistantTokenSet>(cancellationToken)
            ?? throw new InvalidOperationException("Home Assistant returned an empty token response.");
    }

    private static Uri NormalizeHomeAssistantUrl(string input)
    {
        var candidate = input.Trim();
        if (!candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            candidate = $"http://{candidate}";
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Enter a valid Home Assistant URL.");
        }

        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
    }

    private static Uri BuildAuthorizeUri(Uri baseUri, Uri clientId, Uri redirectUri, string state)
    {
        var query = string.Join("&",
            $"response_type=code",
            $"client_id={Uri.EscapeDataString(clientId.ToString())}",
            $"redirect_uri={Uri.EscapeDataString(redirectUri.ToString())}",
            $"state={Uri.EscapeDataString(state)}");

        return new Uri(baseUri, $"auth/authorize?{query}");
    }

    private static string CreateState()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
