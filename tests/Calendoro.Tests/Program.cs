using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Calendoro;
using CreateBookingRequest = Calendoro.Models.CreateBookingRequest;
using CreateEventTypeRequest = Calendoro.Models.CreateEventTypeRequest;

var probe = new TcpListener(IPAddress.Loopback, 0);
probe.Start();
var port = ((IPEndPoint)probe.LocalEndpoint).Port;
probe.Stop();
var url = $"http://127.0.0.1:{port}";
var seen = new ConcurrentQueue<(string Path, string? Auth, string? Key)>();
var failures = 0;

var listener = new HttpListener();
listener.Prefixes.Add(url + "/");
listener.Start();
_ = Task.Run(async () =>
{
    while (listener.IsListening)
    {
        var ctx = await listener.GetContextAsync();
        _ = Task.Run(async () =>
        {
            var path = ctx.Request.Url!.AbsolutePath;
            var raw = ctx.Request.RawUrl!;
            seen.Enqueue((raw.Split('?')[0], ctx.Request.Headers["Authorization"], ctx.Request.Headers["Idempotency-Key"]));
            var post = ctx.Request.HttpMethod == "POST";
            (int Status, string Body) r;
            if (Interlocked.Decrement(ref failures) >= 0)
            {
                ctx.Response.Headers["Retry-After"] = "0";
                r = (503, """{"error":{"code":"stub_failure","message":"try again"}}""");
            }
            else if (path == "/v1/webhooks") r = (200, raw.Contains("cursor=p2") ? """{"data":[{"id":"whk_2"}],"nextCursor":null}""" : """{"data":[{"id":"whk_1"}],"nextCursor":"p2"}""");
            else if (path == "/v1/me") r = (200, """{"organizationId":"org_1","futureField":1}""");
            else if (path == "/v1/bookings" && post) r = (201, """{"id":"bkg_1","status":"confirmed"}""");
            else if ((path == "/v1/event-types" || path == "/v1/auth/login") && post) r = (500, """{"error":{"code":"internal","message":"boom"}}""");
            else if (path == "/slow") { await Task.Delay(1000); r = (200, "{}"); }
            else r = (404, """{"error":{"code":"not_found","message":"nope","details":["x"]}}""");
            try
            {
                var bytes = Encoding.UTF8.GetBytes(r.Body);
                ctx.Response.StatusCode = r.Status;
                ctx.Response.Headers["X-Request-Id"] = "req_stub";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
            catch (Exception)
            {
                // The client gave up, which the timeout test does on purpose.
            }
        });
    }
});

var client = new CalendoroClient(new ClientOptions { ApiKey = "cdo_key", BaseUrl = url });
var failed = 0;
void Check(string name, bool ok)
{
    Console.WriteLine((ok ? "ok   " : "FAIL ") + name);
    if (!ok) failed++;
}
void Reset(int failNext = 0)
{
    seen.Clear();
    Volatile.Write(ref failures, failNext);
}
async Task<E?> Raises<E>(Func<Task> f) where E : Exception
{
    try
    {
        await f();
    }
    catch (E e)
    {
        return e;
    }
    return null;
}

Reset();
Check("api key is bearer", (await client.Account.GetApiIdentityAsync()).OrganizationId == "org_1" && seen.First().Auth == "Bearer cdo_key");
Reset();
await new CalendoroClient(new ClientOptions { OAuthToken = "cdoa_t", BaseUrl = url }).Account.GetApiIdentityAsync();
Check("oauth token is bearer", seen.First().Auth == "Bearer cdoa_t");

Reset(1);
await client.Account.GetApiIdentityAsync();
Check("retries a 503", seen.Count == 2);
Reset(3);
Check("gives up after max retries", await Raises<ServerException>(() => client.Account.GetApiIdentityAsync()) is not null && seen.Count == 3);

Reset(1);
var booking = await client.Bookings.CreateBookingAsync(new CreateBookingRequest());
var keys = seen.Select(s => s.Key).ToList();
Check("idempotency key stable across retries", booking.Id == "bkg_1" && keys.Count == 2 && keys[0] is not null && keys[0] == keys[1]);
Reset();
await client.Bookings.CreateBookingAsync(new CreateBookingRequest(), new RequestOptions { IdempotencyKey = "mine" });
Check("caller idempotency key", seen.First().Key == "mine");

Reset();
Check("write without key not retried on 500", await Raises<ServerException>(() => client.Authentication.LoginAsync(new Calendoro.Models.LoginRequest())) is not null && seen.Count == 1 && seen.First().Key is null);

Reset();
var ids = new List<string?>();
await foreach (var w in client.Webhooks.ListWebhooksIterAsync()) ids.Add(w.Id);
Check("iterator follows cursor", ids.SequenceEqual(new[] { "whk_1", "whk_2" }));

Reset();
var nf = await Raises<NotFoundException>(() => client.EventTypes.GetEventTypeAsync("a/b"));
Check("typed error carries request id", nf is { Status: 404, Code: "not_found", RequestId: "req_stub" } && nf.Details?.ValueKind == JsonValueKind.Array);
Check("path params are encoded", seen.First().Path == "/v1/event-types/a%2Fb");

var fast = new CalendoroClient(new ClientOptions { ApiKey = "k", BaseUrl = url, Timeout = TimeSpan.FromMilliseconds(100), MaxRetries = 0 });
Check("timeout", await Raises<CalendoroTimeoutException>(() => fast.RequestAsync<JsonElement>("GET", "/slow", null, null, null, false, null)) is not null);

using var vectors = JsonDocument.Parse(File.ReadAllText(Fixture()));
foreach (var v in vectors.RootElement.EnumerateArray())
{
    var tolerance = v.TryGetProperty("tolerance", out var t) ? t.GetInt64() : Webhooks.DefaultToleranceSeconds;
    Check("webhook: " + v.GetProperty("name").GetString(), Webhooks.VerifySignature(v.GetProperty("header").GetString(), v.GetProperty("secret").GetString()!, v.GetProperty("body").GetString()!, v.GetProperty("now").GetInt64(), tolerance) == v.GetProperty("valid").GetBoolean());
}
Check("construct event refuses", await Raises<WebhookSignatureException>(() => { Webhooks.ConstructEvent("{}", null, "s"); return Task.CompletedTask; }) is not null);

listener.Stop();
return failed > 0 ? 1 : 0;

static string Fixture([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "fixtures", "webhook-vectors.json");
