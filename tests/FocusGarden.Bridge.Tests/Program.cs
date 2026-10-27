using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FocusGarden.App;

// Real loopback HTTP integration tests. No WPF reference, UI, production data,
// or access to the application's normal port is involved.
var testRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "FocusGarden.Bridge.Tests"));
var testDirectory = Path.Combine(testRoot, Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testDirectory);
int passed = 0;
int failed = 0;
LocalBridge? bridge = null;

try
{
    int port = AvailablePort();
    bridge = new LocalBridge(testDirectory, port);
    if (bridge.Error is not null) throw new InvalidOperationException("Cannot start test listener: " + bridge.Error);
    using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(5) };
    string token = bridge.Token;

    await Check("missing token returns 401", async () =>
    {
        using var response = await client.GetAsync("state");
        Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Equal(default(DateTimeOffset), bridge.LastContact);
    });

    await Check("incorrect token returns 401", async () =>
    {
        using var request = Request(HttpMethod.Get, "state", token[0] == 'A' ? "B" + token[1..] : "A" + token[1..]);
        using var response = await client.SendAsync(request);
        Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Equal(default(DateTimeOffset), bridge.LastContact);
    });

    await Check("authenticated idle state is inactive", async () =>
    {
        var state = await ReadState(client, token);
        Inactive(state);
        True(bridge.LastContact > DateTimeOffset.UtcNow.AddSeconds(-5), "Authenticated contact was not recorded.");
    });

    await Check("published active state has expected JSON fields", async () =>
    {
        var before = DateTimeOffset.UtcNow;
        var domains = new[] { "example.com", "www.example.org" };
        bridge.Publish(true, domains, 120);
        domains[0] = "mutated.example";
        var state = await ReadState(client, token);
        Equal(4, state.EnumerateObject().Count());
        Equal(true, state.GetProperty("active").GetBoolean());
        Equal(2, state.GetProperty("domains").GetArrayLength());
        Equal("example.com", state.GetProperty("domains")[0].GetString());
        Equal("www.example.org", state.GetProperty("domains")[1].GetString());
        var lease = state.GetProperty("leaseExpiresAt").GetDateTimeOffset();
        var sessionEnd = state.GetProperty("sessionEndsAt").GetDateTimeOffset();
        True(lease >= before.AddSeconds(15) && lease <= DateTimeOffset.UtcNow.AddSeconds(15), "Lease is not 15 seconds.");
        True(sessionEnd >= before.AddSeconds(120) && sessionEnd <= DateTimeOffset.UtcNow.AddSeconds(120), "Session end is incorrect.");
        True(!state.TryGetProperty("token", out _), "Pairing secret must not appear in response JSON.");
    });

    await Check("only GET /state is supported and rejected writes do not change state", async () =>
    {
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Patch })
        {
            using var request = Request(method, "state", token);
            using var response = await client.SendAsync(request);
            Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        using (var request = Request(HttpMethod.Get, "stop", token))
        using (var response = await client.SendAsync(request)) Equal(HttpStatusCode.NotFound, response.StatusCode);
        Equal(true, (await ReadState(client, token)).GetProperty("active").GetBoolean());
    });

    await Check("publishing idle immediately releases active restrictions", async () =>
    {
        bridge.Publish(false, new[] { "ignored.example" }, 120);
        Inactive(await ReadState(client, token));
    });

    await Check("expired 15-second lease fails open without new publication", async () =>
    {
        bridge.Publish(true, new[] { "example.com" }, 300);
        Equal(true, (await ReadState(client, token)).GetProperty("active").GetBoolean());
        await Task.Delay(TimeSpan.FromSeconds(16));
        Inactive(await ReadState(client, token));
    });

    await Check("pairing token persists across listener restart", async () =>
    {
        bridge.Dispose();
        bridge = null;
        int restartPort = AvailablePort();
        using var restarted = new LocalBridge(testDirectory, restartPort);
        if (restarted.Error is not null) throw new InvalidOperationException("Restart failed: " + restarted.Error);
        Equal(token, restarted.Token);
        Equal(token, File.ReadAllText(Path.Combine(testDirectory, "browser-pairing.key")).Trim());
        using var restartedClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{restartPort}/"), Timeout = TimeSpan.FromSeconds(5) };
        Inactive(await ReadState(restartedClient, token));
    });

    await Check("invalid optional ports are rejected", () =>
    {
        Throws<ArgumentOutOfRangeException>(() => new LocalBridge(testDirectory, 0));
        Throws<ArgumentOutOfRangeException>(() => new LocalBridge(testDirectory, 65536));
        return Task.CompletedTask;
    });
}
catch (Exception exception)
{
    failed++;
    Console.Error.WriteLine("FAIL test setup: " + exception.Message);
}
finally
{
    bridge?.Dispose();
    // Verify the exact absolute destination before recursively deleting only
    // this run's generated scratch directory.
    string resolved = Path.GetFullPath(testDirectory);
    string expectedParent = testRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!resolved.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase) || resolved == testRoot)
        throw new InvalidOperationException("Refusing to remove a directory outside test scratch storage.");
    if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    Console.WriteLine("Test data cleaned up.");
}

Console.WriteLine($"Bridge tests: {passed}/{passed + failed} passed.");
return failed == 0 ? 0 : 1;

async Task Check(string name, Func<Task> test)
{
    try { await test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception exception) { failed++; Console.Error.WriteLine($"FAIL {name}: {exception.Message}"); }
}

static int AvailablePort()
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    if (port == 17839) throw new InvalidOperationException("Refusing to use the application's normal port.");
    return port;
}

static HttpRequestMessage Request(HttpMethod method, string path, string token)
{
    var request = new HttpRequestMessage(method, path);
    request.Headers.Add("X-Focus-Token", token);
    return request;
}

static async Task<JsonElement> ReadState(HttpClient client, string token)
{
    using var request = Request(HttpMethod.Get, "state", token);
    using var response = await client.SendAsync(request);
    Equal(HttpStatusCode.OK, response.StatusCode);
    Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    True(response.Headers.CacheControl?.NoStore == true, "State response must not be cached.");
    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    return document.RootElement.Clone();
}

static void Inactive(JsonElement state)
{
    Equal(false, state.GetProperty("active").GetBoolean());
    Equal(0, state.GetProperty("domains").GetArrayLength());
    Equal(JsonValueKind.Null, state.GetProperty("sessionEndsAt").ValueKind);
    True(state.GetProperty("leaseExpiresAt").GetDateTimeOffset() <= DateTimeOffset.UtcNow, "Inactive state must not have a future lease.");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}; got {actual}.");
}

static void True(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Throws<TException>(Action action) where TException : Exception
{
    try { action(); }
    catch (TException) { return; }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}
