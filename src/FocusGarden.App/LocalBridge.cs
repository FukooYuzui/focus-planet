using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace FocusGarden.App;

public sealed record BridgeState(bool active, string[] domains, DateTimeOffset leaseExpiresAt, DateTimeOffset? sessionEndsAt);
public sealed class LocalBridge : IDisposable
{
    private readonly HttpListener listener = new();
    private volatile BridgeState state = new(false, [], DateTimeOffset.UtcNow, null);
    public string Token { get; }
    public DateTimeOffset LastContact { get; private set; }
    public string? Error { get; private set; }
    public LocalBridge(string dataDirectory, int port = 17839)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var tokenPath = Path.Combine(dataDirectory, "browser-pairing.key");
        Token = File.Exists(tokenPath) ? File.ReadAllText(tokenPath).Trim() : Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        if (Token.Length != 64) Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        File.WriteAllText(tokenPath, Token);
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        try { listener.Start(); _ = Listen(); }
        catch (Exception ex) { Error = ex.Message; }
    }
    public void Publish(bool active, string[] domains, int remainingSeconds)
        => state = new(active, active ? domains.ToArray() : [], DateTimeOffset.UtcNow.AddSeconds(active ? 15 : 0), active ? DateTimeOffset.UtcNow.AddSeconds(remainingSeconds) : null);
    private async Task Listen()
    {
        while (listener.IsListening)
        {
            try
            {
                var c = await listener.GetContextAsync();
                _ = Respond(c);
            }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
        }
    }
    private async Task Respond(HttpListenerContext c)
    {
        try
        {
            c.Response.Headers["Cache-Control"] = "no-store";
            if (c.Request.HttpMethod != "GET" || c.Request.Url?.AbsolutePath != "/state") { c.Response.StatusCode = 404; return; }
            var supplied = c.Request.Headers["X-Focus-Token"] ?? "";
            if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(Token), System.Text.Encoding.UTF8.GetBytes(supplied))) { c.Response.StatusCode = 401; return; }
            LastContact = DateTimeOffset.UtcNow;
            var snapshot = state;
            if (snapshot.leaseExpiresAt <= DateTimeOffset.UtcNow) snapshot = new(false, [], DateTimeOffset.UtcNow, null);
            c.Response.ContentType = "application/json; charset=utf-8";
            await JsonSerializer.SerializeAsync(c.Response.OutputStream, snapshot);
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException) { }
        finally { c.Response.Close(); }
    }
    public void Dispose() { Publish(false, [], 0); listener.Close(); }
}
