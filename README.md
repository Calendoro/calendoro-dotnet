# Calendoro for .NET

**Status: not yet published to NuGet.** The install command below works after the first release. Until then, use this folder directly.

Every method is generated from the Calendoro OpenAPI document. .NET 8+, no dependencies outside the framework.

```
dotnet add package Calendoro
```

```csharp
using Calendoro;
using Calendoro.Models;

var client = new CalendoroClient(new ClientOptions { ApiKey = "cdo_..." }); // or OAuthToken = "cdoa_..."

var me = await client.Account.GetApiIdentityAsync();

await foreach (var hook in client.Webhooks.ListWebhooksIterAsync()) // follows nextCursor
    Console.WriteLine(hook.Url);

var booking = await client.Bookings.CreateBookingAsync(
    new CreateBookingRequest { EventTypeId = "...", StartAt = "2026-11-02T15:00:00Z" },
    new RequestOptions { IdempotencyKey = "order-1234" }); // generated for you if omitted
```

Models are classes in `Calendoro.Models`. Every property is nullable, because the wire may omit it; null properties are not sent. Enums are strings, so a new server value cannot break old clients.

**Retries**: `MaxRetries` (default 2), exponential backoff with full jitter, honoring `Retry-After`. GET, PUT, DELETE and calls with an idempotency key retry on network errors, 408, 429 and 5xx. Other writes retry only on 429, since a failed one may have taken effect.

**Timeouts**: `Timeout` per attempt (default 30s), `RequestOptions.Timeout` per call, plus your `CancellationToken`.

**Errors**: `ApiException` has `Status`, `Code`, `Message`, `Details`, `RequestId`. Subclasses: `ValidationException`, `AuthenticationException`, `PermissionDeniedException`, `NotFoundException`, `ConflictException`, `RateLimitException`, `ServerException`. Network failures throw `CalendoroConnectionException` or `CalendoroTimeoutException`.

**Webhooks**: pass the raw request body.

```csharp
using var evt = Webhooks.ConstructEvent(rawBody, Request.Headers["Calendoro-Signature"], "whsec_...");
```

Methods documented as needing a user session refuse API keys and OAuth tokens; use `SessionToken`.
