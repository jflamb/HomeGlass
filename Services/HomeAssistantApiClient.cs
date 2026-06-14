using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HomeGlass.Models;

namespace HomeGlass.Services;

public sealed class HomeAssistantApiClient
{
    private readonly HttpClient _httpClient;
    private readonly HomeAssistantAuthService _authService;
    private readonly HomeAssistantCredentialStore _credentialStore;

    public HomeAssistantApiClient(
        HttpClient httpClient,
        HomeAssistantAuthService authService,
        HomeAssistantCredentialStore credentialStore)
    {
        _httpClient = httpClient;
        _authService = authService;
        _credentialStore = credentialStore;
    }

    public async Task<JsonDocument> GetApiStatusAsync(CancellationToken cancellationToken = default)
    {
        return await SendAsync<JsonDocument>(HttpMethod.Get, "api/", null, cancellationToken);
    }

    public async Task<IReadOnlyList<HomeAssistantEntityState>> GetStatesAsync(CancellationToken cancellationToken = default)
    {
        return await SendAsync<List<HomeAssistantEntityState>>(HttpMethod.Get, "api/states", null, cancellationToken);
    }

    public async Task<JsonDocument> CallServiceAsync(
        string domain,
        string service,
        object? payload = null,
        CancellationToken cancellationToken = default)
    {
        return await SendAsync<JsonDocument>(HttpMethod.Post, $"api/services/{domain}/{service}", payload, cancellationToken);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string relativePath, object? payload, CancellationToken cancellationToken)
    {
        var connection = _credentialStore.GetConnection()
            ?? throw new InvalidOperationException("HomeGlass is not connected to Home Assistant.");
        var accessToken = await _authService.GetAccessTokenAsync(cancellationToken);

        using var request = new HttpRequestMessage(method, new Uri(connection.BaseUri, relativePath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Home Assistant API request failed with {(int)response.StatusCode}: {error}");
        }

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw new InvalidOperationException("Home Assistant returned an empty API response.");
    }
}
