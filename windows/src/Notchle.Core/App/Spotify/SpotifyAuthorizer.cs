using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Notchle.Core.App.Spotify;

/// "Connect Spotify…": Authorization Code + PKCE with a loopback redirect. Opens the browser on
/// Spotify's consent page, catches the redirect on http://127.0.0.1:&lt;ephemeral port&gt;/callback,
/// exchanges the code and stores the tokens.
public sealed class SpotifyAuthorizer(HttpClient http, ISpotifyTokenStore store, Func<Uri, Task> openBrowser, Func<DateTimeOffset>? now = null)
{
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);

    public async Task ConnectAsync(string clientId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new PlayerException(PlayerErrorKind.Unavailable, "Spotify Connect needs your Spotify client id (see the README)");
        using var listener = LoopbackRedirectListener.Start();
        var redirect = SpotifyAccounts.RedirectUri(listener.Port);
        var verifier = Pkce.CreateVerifier();
        var state = Pkce.CreateState();
        await openBrowser(SpotifyAccounts.AuthorizeUrl(clientId, redirect, Pkce.Challenge(verifier), state)).ConfigureAwait(false);

        var target = await listener.WaitForCallbackAsync(cancellationToken).ConfigureAwait(false);
        var code = SpotifyAccounts.ParseCallback(target, state);

        using var request = SpotifyAccounts.TokenRequest(clientId, code, redirect, verifier);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw SpotifyAccounts.TokenError((int)response.StatusCode, body);
        store.Save(SpotifyAccounts.ParseTokenResponse(body, _now()));
    }

    public void Disconnect() => store.Save(null);
}

/// Minimal one-shot HTTP listener on 127.0.0.1 (a TcpListener, so it needs no URL ACL on
/// Windows and runs in the macOS tests). Answers everything but /callback with 404.
public sealed class LoopbackRedirectListener : IDisposable
{
    private readonly TcpListener _listener;

    private LoopbackRedirectListener(TcpListener listener) => _listener = listener;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static LoopbackRedirectListener Start(int port = 0)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        return new LoopbackRedirectListener(listener);
    }

    /// Returns the request target of the first GET /callback (e.g. "/callback?code=…&amp;state=…").
    public async Task<string> WaitForCallbackAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            await using var stream = client.GetStream();
            var target = await ReadRequestTargetAsync(stream, cancellationToken).ConfigureAwait(false);
            var isCallback = target is not null
                && (target == "/callback" || target.StartsWith("/callback?", StringComparison.Ordinal));
            await RespondAsync(stream, isCallback, cancellationToken).ConfigureAwait(false);
            if (isCallback) return target!;
        }
    }

    public void Dispose() => _listener.Stop();

    /// Reads the request head (up to 16 KiB) and returns the target of a GET request line.
    private static async Task<string?> ReadRequestTargetAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false);
            if (read == 0) break;
            length += read;
            if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
        }
        var head = Encoding.ASCII.GetString(buffer, 0, length);
        var firstLine = head.Split("\r\n", 2)[0].Split(' ');
        return firstLine.Length >= 2 && firstLine[0] == "GET" ? firstLine[1] : null;
    }

    private static async Task RespondAsync(NetworkStream stream, bool ok, CancellationToken ct)
    {
        var html = ok
            ? "<!doctype html><meta charset=utf-8><title>Notchle</title><body style=\"font-family:sans-serif\"><p>Notchle is connected to Spotify. You can close this tab.</p>"
            : "<!doctype html><title>Not found</title>";
        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {(ok ? "200 OK" : "404 Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }
}
