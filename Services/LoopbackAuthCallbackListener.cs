using System.Net;
using System.Net.Sockets;
using System.Text;

namespace HomeGlass.Services;

public sealed class LoopbackAuthCallbackListener : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

    public int Port { get; private set; }

    public Uri ClientId => new($"http://127.0.0.1:{Port}/");

    public Uri RedirectUri => new(ClientId, "auth/callback");

    public void Start()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public async Task<string> WaitForCodeAsync(string expectedState, CancellationToken cancellationToken)
    {
        using var client = await _listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);

        var requestLine = await reader.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(requestLine))
        {
            await WriteResponseAsync(stream, HttpStatusCode.BadRequest, "HomeGlass did not receive an authorization response.", cancellationToken);
            throw new InvalidOperationException("The authorization response was empty.");
        }

        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken)))
        {
        }

        var parts = requestLine.Split(' ');
        if (parts.Length < 2 || !parts[0].Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            await WriteResponseAsync(stream, HttpStatusCode.MethodNotAllowed, "HomeGlass expected a browser redirect.", cancellationToken);
            throw new InvalidOperationException("The authorization response was not a GET request.");
        }

        var callbackUri = new Uri(ClientId, parts[1].TrimStart('/'));
        var query = ParseQuery(callbackUri.Query);

        if (!query.TryGetValue("state", out var actualState) || actualState != expectedState)
        {
            await WriteResponseAsync(stream, HttpStatusCode.BadRequest, "The authorization response did not match this HomeGlass sign-in attempt.", cancellationToken);
            throw new InvalidOperationException("The authorization response state did not match.");
        }

        if (query.TryGetValue("error", out var error))
        {
            await WriteResponseAsync(stream, HttpStatusCode.BadRequest, "Home Assistant did not authorize HomeGlass.", cancellationToken);
            throw new InvalidOperationException($"Home Assistant returned an authorization error: {error}");
        }

        if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
        {
            await WriteResponseAsync(stream, HttpStatusCode.BadRequest, "HomeGlass did not receive an authorization code.", cancellationToken);
            throw new InvalidOperationException("The authorization response did not include a code.");
        }

        await WriteResponseAsync(stream, HttpStatusCode.OK, "HomeGlass is connected. You can close this browser tab.", cancellationToken);
        return code;
    }

    public ValueTask DisposeAsync()
    {
        _listener.Stop();
        return ValueTask.CompletedTask;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        return query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(pair => pair.Length == 2)
            .ToDictionary(
                pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')),
                pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')),
                StringComparer.Ordinal);
    }

    private static async Task WriteResponseAsync(Stream stream, HttpStatusCode statusCode, string message, CancellationToken cancellationToken)
    {
        var body = $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <title>HomeGlass</title>
              <style>
                body { font-family: "Segoe UI", sans-serif; margin: 48px; color: #1f1f1f; }
                main { max-width: 560px; }
              </style>
            </head>
            <body>
              <main>
                <h1>HomeGlass</h1>
                <p>{{WebUtility.HtmlEncode(message)}}</p>
              </main>
            </body>
            </html>
            """;

        var response = Encoding.UTF8.GetBytes(
            $"HTTP/1.1 {(int)statusCode} {statusCode}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");

        await stream.WriteAsync(response, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
