using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using HomeGlass.Models;

namespace HomeGlass.Services;

public sealed class HomeAssistantWebSocketClient
{
    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly HomeAssistantAuthService _authService;
    private readonly HomeAssistantCredentialStore _credentialStore;

    public HomeAssistantWebSocketClient(
        HomeAssistantAuthService authService,
        HomeAssistantCredentialStore credentialStore)
    {
        _authService = authService;
        _credentialStore = credentialStore;
    }

    public async Task<IReadOnlyList<HomeAssistantArea>> GetAreasAsync(CancellationToken cancellationToken = default)
    {
        return await SendCommandAsync<IReadOnlyList<HomeAssistantArea>>("config/area_registry/list", "rooms", cancellationToken);
    }

    public async Task<IReadOnlyList<HomeAssistantFloor>> GetFloorsAsync(CancellationToken cancellationToken = default)
    {
        return await SendCommandAsync<IReadOnlyList<HomeAssistantFloor>>("config/floor_registry/list", "floors", cancellationToken);
    }

    public async Task<IReadOnlyList<HomeAssistantDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        return await SendCommandAsync<IReadOnlyList<HomeAssistantDevice>>("config/device_registry/list", "devices", cancellationToken);
    }

    public async Task<IReadOnlyList<HomeAssistantEntityRegistryEntry>> GetEntityRegistryAsync(CancellationToken cancellationToken = default)
    {
        return await SendCommandAsync<IReadOnlyList<HomeAssistantEntityRegistryEntry>>("config/entity_registry/list", "entities", cancellationToken);
    }

    private async Task<T> SendCommandAsync<T>(string commandType, string description, CancellationToken cancellationToken)
    {
        var connection = _credentialStore.GetConnection()
            ?? throw new InvalidOperationException("HomeGlass is not connected to Home Assistant.");
        var accessToken = await _authService.GetAccessTokenAsync(cancellationToken);

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(GetWebSocketUri(connection.BaseUri), cancellationToken);

        using var authRequired = await ReceiveJsonAsync(socket, cancellationToken);
        RequireMessageType(authRequired.RootElement, "auth_required");

        await SendJsonAsync(socket, new
        {
            type = "auth",
            access_token = accessToken
        }, cancellationToken);

        using var authResponse = await ReceiveJsonAsync(socket, cancellationToken);
        var authResponseType = GetMessageType(authResponse.RootElement);
        if (authResponseType == "auth_invalid")
        {
            throw new InvalidOperationException("Home Assistant rejected the stored credentials. Sign out and connect again.");
        }

        if (authResponseType != "auth_ok")
        {
            throw new InvalidOperationException($"Home Assistant returned an unexpected WebSocket auth response: {authResponseType}.");
        }

        const int requestId = 1;
        await SendJsonAsync(socket, new
        {
            id = requestId,
            type = commandType
        }, cancellationToken);

        while (true)
        {
            using var response = await ReceiveJsonAsync(socket, cancellationToken);
            var root = response.RootElement;

            if (!root.TryGetProperty("id", out var idElement) || idElement.GetInt32() != requestId)
            {
                continue;
            }

            if (root.TryGetProperty("success", out var successElement) && !successElement.GetBoolean())
            {
                var error = root.TryGetProperty("error", out var errorElement)
                    ? errorElement.ToString()
                    : "unknown error";
                throw new InvalidOperationException($"Home Assistant could not load {description}: {error}");
            }

            if (!root.TryGetProperty("result", out var resultElement))
            {
                throw new InvalidOperationException($"Home Assistant returned a {description} response without a result.");
            }

            return resultElement.Deserialize<T>(JsonSerializerOptions)
                ?? throw new InvalidOperationException($"Home Assistant returned an empty {description} response.");
        }
    }

    private static Uri GetWebSocketUri(Uri baseUri)
    {
        var builder = new UriBuilder(baseUri)
        {
            Scheme = baseUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Path = "api/websocket",
            Query = string.Empty
        };

        return builder.Uri;
    }

    private static async Task SendJsonAsync(ClientWebSocket socket, object message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message, JsonSerializerOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<JsonDocument> ReceiveJsonAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();

        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("Home Assistant closed the WebSocket connection.");
            }

            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        stream.Position = 0;
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static void RequireMessageType(JsonElement message, string expectedType)
    {
        var actualType = GetMessageType(message);
        if (actualType != expectedType)
        {
            throw new InvalidOperationException($"Home Assistant returned an unexpected WebSocket message: {actualType}.");
        }
    }

    private static string GetMessageType(JsonElement message)
    {
        return message.TryGetProperty("type", out var typeElement)
            ? typeElement.GetString() ?? string.Empty
            : string.Empty;
    }
}
