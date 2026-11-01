#nullable enable
using System.Collections;
using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Calendoro;

/// <summary>Client settings. Set one of ApiKey, OAuthToken or SessionToken.</summary>
public sealed class ClientOptions
{
    /// <summary>An API key, cdo_...</summary>
    public string? ApiKey { get; set; }
    /// <summary>An OAuth access token, cdoa_...</summary>
    public string? OAuthToken { get; set; }
    /// <summary>A user session token, cds_..., which session-only operations need.</summary>
    public string? SessionToken { get; set; }
    public string BaseUrl { get; set; } = "https://api.calendoro.app";
    /// <summary>Timeout of each attempt.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Retries after the first attempt.</summary>
    public int MaxRetries { get; set; } = 2;
    public HttpClient? HttpClient { get; set; }
}

/// <summary>Overrides for one call.</summary>
public sealed class RequestOptions
{
    /// <summary>Idempotency-Key of an idempotent operation. One is generated if omitted.</summary>
    public string? IdempotencyKey { get; set; }
    public TimeSpan? Timeout { get; set; }
}

public class CalendoroException : Exception
{
    public CalendoroException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>No response arrived.</summary>
public class CalendoroConnectionException : CalendoroException
{
    public CalendoroConnectionException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class CalendoroTimeoutException : CalendoroConnectionException
{
    public CalendoroTimeoutException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>The API answered with an error. Quote RequestId to support.</summary>
public class ApiException : CalendoroException
{
    public int Status { get; }
    public string Code { get; }
    public JsonElement? Details { get; }
    public string? RequestId { get; }
    public TimeSpan? RetryAfter { get; }

    public ApiException(int status, string code, string message, JsonElement? details, string? requestId, TimeSpan? retryAfter)
        : base($"{status} {code}: {message}" + (requestId is null ? "" : $" (request {requestId})"))
    {
        Status = status;
        Code = code;
        Details = details;
        RequestId = requestId;
        RetryAfter = retryAfter;
    }

    internal static ApiException For(int s, string c, string m, JsonElement? d, string? r, TimeSpan? a) => s switch
    {
        400 => new ValidationException(s, c, m, d, r, a),
        401 => new AuthenticationException(s, c, m, d, r, a),
        403 => new PermissionDeniedException(s, c, m, d, r, a),
        404 => new NotFoundException(s, c, m, d, r, a),
        409 => new ConflictException(s, c, m, d, r, a),
        429 => new RateLimitException(s, c, m, d, r, a),
        >= 500 => new ServerException(s, c, m, d, r, a),
        _ => new ApiException(s, c, m, d, r, a),
    };
}

public sealed class ValidationException(int s, string c, string m, JsonElement? d, string? r, TimeSpan? a) : ApiException(s, c, m, d, r, a);
public sealed class AuthenticationException(int s, string c, string m, JsonElement? d, string? r, TimeSpan? a) : ApiException(s, c, m, d, r, a);
public sealed class PermissionDeniedException(int s, string c, string m, JsonElement? d, string? r, TimeSpan? a) : ApiException(s, c, m, d, r, a);
public sealed class NotFoundException(int s, string c, string m, JsonElement? d, string? r, TimeSpan? a) : ApiException(s, c, m, d, r, a);
public sealed class ConflictException(int s, string c, string m, JsonElement? d, string? r, TimeSpan? a) : ApiException(s, c, m, d, r, a);
public sealed class RateLimitException(int s, string c, string m, JsonElement? d, string? r, TimeSpan? a) : ApiException(s, c, m, d, r, a);
public sealed class ServerException(int s, string c, string m, JsonElement? d, string? r, TimeSpan? a) : ApiException(s, c, m, d, r, a);

/// <summary>HTTP runtime: auth, retries, timeouts, idempotency keys, paging and errors.</summary>
public abstract class BaseClient
{
    public const string Version = "0.1.0";
    private static readonly HashSet<int> Retryable = [408, 429, 500, 502, 503, 504];
    internal static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly ClientOptions _options;
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string? _token;

    protected BaseClient(ClientOptions options)
    {
        _options = options;
        // Each attempt has its own timeout below, so the HttpClient's own must not cut in first.
        _http = options.HttpClient ?? new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        _baseUrl = options.BaseUrl.TrimEnd('/');
        _token = options.ApiKey ?? options.OAuthToken ?? options.SessionToken;
    }

    private sealed class Page<T>
    {
        [JsonPropertyName("data")] public List<T>? Data { get; set; }
        [JsonPropertyName("nextCursor")] public string? NextCursor { get; set; }
    }

    private static string Text(object value) => value switch
    {
        bool b => b ? "true" : "false",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    private static string QueryString(IReadOnlyDictionary<string, object?>? query)
    {
        var pairs = new List<string>();
        foreach (var (k, v) in query ?? new Dictionary<string, object?>())
        {
            if (v is null) continue;
            if (v is IEnumerable many && v is not string)
            {
                foreach (var x in many)
                    if (x is not null) pairs.Add($"{Uri.EscapeDataString(k)}={Uri.EscapeDataString(Text(x))}");
            }
            else
            {
                pairs.Add($"{Uri.EscapeDataString(k)}={Uri.EscapeDataString(Text(v))}");
            }
        }
        return pairs.Count == 0 ? "" : "?" + string.Join("&", pairs);
    }

    /// <summary>Runs a request and decodes the response. The typed methods go through here.</summary>
    public async Task<T> RequestAsync<T>(string method, string path, IReadOnlyDictionary<string, string>? pathParams, IReadOnlyDictionary<string, object?>? query, object? body, bool idempotent, RequestOptions? options, CancellationToken ct = default)
    {
        foreach (var (k, v) in pathParams ?? new Dictionary<string, string>()) path = path.Replace("{" + k + "}", Uri.EscapeDataString(v));
        var url = _baseUrl + path + QueryString(query);
        var payload = body is null ? null : JsonSerializer.Serialize(body, body.GetType(), Json);
        var key = idempotent ? options?.IdempotencyKey ?? Guid.NewGuid().ToString() : null; // one key for every retry of this call
        // A POST or PATCH without a key may already have taken effect, so only a 429, which the server refused up front, is safe to repeat.
        var safe = idempotent || method is "GET" or "PUT" or "DELETE";
        for (var attempt = 0; ; attempt++)
        {
            TimeSpan wait;
            try
            {
                var raw = await OnceAsync(method, url, payload, key, options?.Timeout ?? _options.Timeout, ct).ConfigureAwait(false);
                return raw.Length == 0 ? default! : JsonSerializer.Deserialize<T>(raw, Json)!;
            }
            catch (ApiException e) when (attempt < _options.MaxRetries && Retryable.Contains(e.Status) && (safe || e.Status == 429))
            {
                wait = e.RetryAfter is { } after ? TimeSpan.FromSeconds(Math.Min(after.TotalSeconds, 60)) : Backoff(attempt);
            }
            catch (CalendoroConnectionException) when (attempt < _options.MaxRetries && safe)
            {
                wait = Backoff(attempt);
            }
            await Task.Delay(wait, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Exponential backoff with full jitter.</summary>
    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Random.Shared.NextDouble() * Math.Min(8, 0.5 * Math.Pow(2, attempt)));

    /// <summary>Yields every item of a list, following nextCursor.</summary>
    public async IAsyncEnumerable<T> PaginateAsync<T>(string path, IReadOnlyDictionary<string, string>? pathParams, IReadOnlyDictionary<string, object?>? query, RequestOptions? options, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var q = query is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(query);
        while (true)
        {
            var page = await RequestAsync<Page<T>>("GET", path, pathParams, q, null, false, options, ct).ConfigureAwait(false);
            foreach (var item in page.Data ?? []) yield return item;
            if (string.IsNullOrEmpty(page.NextCursor)) yield break;
            q["cursor"] = page.NextCursor;
        }
    }

    private async Task<string> OnceAsync(string method, string url, string? payload, string? key, TimeSpan timeout, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(new HttpMethod(method), url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.TryAddWithoutValidation("User-Agent", $"calendoro-dotnet/{Version}");
        if (_token is not null) req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_token}");
        if (key is not null) req.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        if (payload is not null) req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(timeout);
        HttpResponseMessage res;
        string raw;
        try
        {
            res = await _http.SendAsync(req, attempt.Token).ConfigureAwait(false);
            raw = await res.Content.ReadAsStringAsync(attempt.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new CalendoroTimeoutException($"timed out after {timeout}", e);
        }
        catch (HttpRequestException e)
        {
            throw new CalendoroConnectionException(e.Message, e);
        }
        using (res)
        {
            if ((int)res.StatusCode < 400) return raw;
            throw Error(res, raw);
        }
    }

    private static ApiException Error(HttpResponseMessage res, string raw)
    {
        string code = "http_error", message = $"HTTP {(int)res.StatusCode}";
        JsonElement? details = null;
        string? bodyRequestId = null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            {
                if (err.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String) code = c.GetString()!;
                if (err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) message = m.GetString()!;
                if (err.TryGetProperty("details", out var d)) details = d.Clone();
                if (err.TryGetProperty("requestId", out var r) && r.ValueKind == JsonValueKind.String) bodyRequestId = r.GetString();
            }
        }
        catch (JsonException)
        {
            // A proxy's HTML error page still becomes an ApiException below.
        }
        var requestId = res.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() : bodyRequestId;
        return ApiException.For((int)res.StatusCode, code, message, details, requestId, res.Headers.RetryAfter?.Delta);
    }
}
