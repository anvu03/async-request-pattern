# HTTP API contract

This document describes the HTTP behavior implemented by the current source.
It is the client-facing contract, not a target-state design. For deployment,
ports, startup, health probe usage, or troubleshooting, use the
[infrastructure guide](infrastructure.md).

## Base URL and versioning

The API origin depends on the deployment. All Order operations use the
URL-versioned base path:

```text
<origin>/api/v1
```

Only HTTP API version `v1` is implemented. There is no header, query-string,
or media-type version negotiation. The Service Bus message schema version is a
separate contract and does not change the HTTP route version.

Examples in this document use `http://localhost:5126`, the host-run Development
origin documented in the [README](../README.md). Treat plain HTTP and that
origin as local examples only.

## Endpoint summary

| Method | Path | Authentication | Success | Purpose |
|---|---|---|---|---|
| `POST` | `/api/v1/orders` | Bearer token when authentication is enabled | `202 Accepted` | Durably accept one Order for asynchronous processing. |
| `GET` | `/api/v1/orders/{id}` | Bearer token when authentication is enabled | `200 OK` or `304 Not Modified` | Read the current durable status of one owned Order. |
| `GET` | `/alive` | None | `200 OK` | Process liveness. Returns `{"status":"alive"}`. |
| `GET` | `/health` | None | `200 OK` when ready | SQL-backed API readiness. |
| `GET` | `/openapi/v1.json` | None | `200 OK` | Generated OpenAPI document; Development environment only. |

`/alive` and `/health` are operational endpoints, are outside API versioning,
and are exempt from rate limiting. They are listed here only to make the HTTP
surface complete; this document does not define a probe runbook.

## JSON conventions

- Requests and responses use JSON. Send `Content-Type: application/json` on
  `POST`.
- Documented JSON property names are camel case. Response property casing is
  exactly as shown in the examples.
- Request property matching uses ASP.NET Core web defaults and is
  case-insensitive. Unknown request properties are ignored. Clients should
  still send the documented camel-case schema and no undeclared properties.
- UUID responses use the canonical hyphenated form. Accepted UUID request
  strings are parsed with .NET `Guid.TryParse`; equivalent accepted UUID text
  formats normalize to the same value.
- Timestamps are JSON strings in the ISO 8601 representation emitted for a UTC
  `DateTimeOffset`, for example `2026-09-16T14:25:31.4821930+00:00`.
- Money values are JSON numbers backed by .NET `decimal`, not binary floating
  point values or strings.

## Create an Order

```http
POST /api/v1/orders HTTP/1.1
Content-Type: application/json
Idempotency-Key: checkout-8f39c11b
Authorization: Bearer <token>
```

The `Authorization` header is required only when authentication is enabled.

### Headers

| Header | Required | Implemented behavior |
|---|---|---|
| `Content-Type` | Yes | The request body is JSON. Unsupported media types are rejected. |
| `Idempotency-Key` | Yes | Exactly one header value is required. It is trimmed, must remain non-empty, and must be at most 128 .NET UTF-16 code units. |
| `Authorization` | Configuration-dependent | `Bearer <token>` when JWT authentication is enabled. Do not send credentials in the idempotency key or body. |

### Request schema

```json
{
  "customerId": "10000000-0000-0000-0000-000000000001",
  "currency": "USD",
  "items": [
    {
      "productId": "20000000-0000-0000-0000-000000000001",
      "quantity": 2,
      "unitPrice": 12.50
    }
  ]
}
```

| Field | JSON type | Required | Constraints and normalization |
|---|---|---|---|
| `customerId` | string | Yes | Must parse as a non-empty UUID. Accepted UUID text is normalized to its UUID value. |
| `currency` | string | Yes | Exactly three ASCII uppercase letters `A` through `Z`. It is not trimmed or converted to uppercase. The code is shape-checked; the API does not verify an ISO currency registry. |
| `items` | array | Yes | Between 1 and 100 elements, inclusive. Request order is accepted, but does not affect idempotency equivalence. |
| `items[].productId` | string | Yes | Must parse as a non-empty UUID. Product UUIDs must be unique within the Order after UUID parsing. |
| `items[].quantity` | integer | Yes | From 1 through 1000, inclusive. The JSON value must fit a .NET 32-bit signed integer before business validation runs. |
| `items[].unitPrice` | number | Yes | Greater than zero, no more than `999999999999999.9999`, and equal to its value rounded to four decimal places. Non-zero precision beyond the fourth decimal place is rejected; redundant trailing zeros can normalize to the same decimal value. Must fit SQL `decimal(19,4)`. |

The request has no `total` field. Extra fields, including a client-supplied
`total`, are ignored by current JSON deserialization and do not override the
server calculation.

### Decimal and total rules

The service computes the total with checked decimal arithmetic:

```text
total = sum(items[].quantity * items[].unitPrice)
```

The service does not round the result. Each unit price has no non-zero precision
beyond four decimal places, so multiplication by an integer and addition retain
at most four meaningful fractional digits. The final total must be no more than
`999999999999999.9999` and must fit SQL `decimal(19,4)`. Decimal arithmetic
overflow or a total outside that range produces a `400` validation error on
`total`.

Clients should use decimal arithmetic when displaying or independently checking
the amount. The server-computed value is authoritative.

### Validation behavior

Business validation can report several field errors in one `400` response.
Field keys use paths such as `items[1].unitPrice`. Idempotency header validation
runs first; if that header is invalid, only its error is returned for that
request. JSON syntax, JSON type conversion, and media-type failures happen
before business validation.

Exact business validation messages are:

| Error key | Condition | Message |
|---|---|---|
| `$` | Bound body is `null` | `A request body is required.` |
| `customerId` | Missing, malformed, or empty UUID | `A non-empty UUID is required.` |
| `currency` | Not exactly three ASCII uppercase letters | `Currency must be a 3-letter uppercase ISO code.` |
| `items` | Missing, `null`, empty, or more than 100 items | `Items must contain between 1 and 100 products.` |
| `items[n].productId` | Missing, malformed, or empty UUID | `A non-empty UUID is required.` |
| `items[n].productId` | Duplicate parsed product UUID | `Product IDs must be unique.` |
| `items[n].quantity` | Outside 1 through 1000 | `Quantity must be between 1 and 1000.` |
| `items[n].unitPrice` | Non-positive, over the decimal limit, or not equal to its value rounded to four decimal places | `Unit price must be positive and fit decimal(19,4).` |
| `total` | Checked arithmetic overflow or computed total over the decimal limit | `Order total must fit decimal(19,4).` |
| `Idempotency-Key` | Missing or resolves to more than one header value | `Exactly one Idempotency-Key header is required.` |
| `Idempotency-Key` | Empty after trimming | `A non-empty Idempotency-Key header is required.` |
| `Idempotency-Key` | More than 128 characters after trimming | `Idempotency-Key must not exceed 128 characters.` |

### Successful response

The API commits the Order, its items, and one Outbox Message in one SQL
transaction before returning success. It does not wait for Service Bus delivery
or Worker processing.

```http
HTTP/1.1 202 Accepted
Content-Type: application/json; charset=utf-8
Location: /api/v1/orders/01993d34-91c2-7a08-a3d5-b168a4d32f1c
Retry-After: 1

{
  "orderId": "01993d34-91c2-7a08-a3d5-b168a4d32f1c",
  "status": "Pending",
  "statusUrl": "/api/v1/orders/01993d34-91c2-7a08-a3d5-b168a4d32f1c",
  "createdUtc": "2026-09-16T14:25:31.4821930+00:00",
  "retryAfterSeconds": 1
}
```

| Response field | Type | Meaning |
|---|---|---|
| `orderId` | UUID string | Server-generated Order identifier. |
| `status` | string | Always `Pending` in the `202` representation, including an equivalent idempotent replay. Read the status resource for current state. |
| `statusUrl` | string | Relative URL of the status resource. It matches `Location`. |
| `createdUtc` | timestamp string | Creation time. An idempotent replay returns the original value. |
| `retryAfterSeconds` | integer | Polling hint, currently `1`. It duplicates the `Retry-After` response header for this response. |

### Idempotency semantics

The idempotency identity is the pair:

```text
(derived owner, normalized Idempotency-Key)
```

Implemented behavior:

1. The API requires the header to resolve to exactly one value.
2. Leading and trailing .NET whitespace is removed. Internal characters and
   casing are not changed. The trimmed value is stored, not hashed.
3. The first request stores a SHA-256 fingerprint of canonical request data and
   creates exactly one Order and one Outbox Message in the same transaction.
4. A concurrent request is serialized by the SQL unique index on owner and key.
5. Reusing the pair with an equivalent fingerprint returns `202` with the
   original `orderId` and `createdUtc`; it does not create or republish work.
6. Reusing the pair with a different fingerprint returns `409 Conflict`.
7. The key has no expiration. It remains reserved as long as the Order row
   exists; the current API has no Order deletion operation.

The 128-character check is the .NET string length after `string.Trim()`, so it
counts UTF-16 code units rather than Unicode grapheme clusters.

Fingerprint equivalence normalizes UUID text, decimal text/scale, and item
order. These requests are therefore equivalent if all other values match:

- `{...}` versus canonical `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx` UUID text.
- `12.5`, `12.50`, and `12.5000` as the same decimal value.
- The same unique items in a different array order.

Currency and quantity remain exact values. Changing any customer UUID,
currency, product UUID, quantity, or unit price changes the fingerprint.

The `IdempotencyKey` database column has no explicit binary collation, so key
equality follows the configured `OrdersDb` default collation. The supplied local
database inherits the SQL Server default, which is normally case-insensitive.
Clients must not use case-only key differences as distinct keys. This differs
from owner derivation, whose stored value uses an explicit binary collation.

An equivalent replay still returns a `Pending` representation even if the
original Order has since advanced. Follow `statusUrl` to obtain current status.

## Get Order status

```http
GET /api/v1/orders/01993d34-91c2-7a08-a3d5-b168a4d32f1c HTTP/1.1
Authorization: Bearer <token>
```

`id` must match the route's UUID constraint. A valid UUID is looked up together
with the derived owner. There is no unscoped lookup.

### `200 OK` response

```http
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8
ETag: "AAAAAAAAB9E="
Retry-After: 1

{
  "orderId": "01993d34-91c2-7a08-a3d5-b168a4d32f1c",
  "customerId": "10000000-0000-0000-0000-000000000001",
  "currency": "USD",
  "total": 25.00,
  "status": "Queued",
  "createdUtc": "2026-09-16T14:25:31.4821930+00:00",
  "updatedUtc": "2026-09-16T14:25:31.7314420+00:00",
  "failure": null
}
```

| Response field | Type | Meaning |
|---|---|---|
| `orderId` | UUID string | Order identifier. |
| `customerId` | UUID string | Customer UUID from the accepted request. |
| `currency` | string | Accepted three-letter currency code. |
| `total` | number | Server-computed total. |
| `status` | string | One of `Pending`, `Queued`, `Processing`, `Completed`, or `Failed`. |
| `createdUtc` | timestamp string | Durable creation time. |
| `updatedUtc` | timestamp string | Time of the latest persisted Order state update. |
| `failure` | object or `null` | Sanitized terminal failure for `Failed`; otherwise always `null`. |

Items, the idempotency key, owner key, and message metadata are not returned.

### Failed response representation

HTTP status remains `200 OK` when the Order's business status is `Failed`.
Failure is a resource state, not an HTTP request failure.

```json
{
  "orderId": "01993d34-91c2-7a08-a3d5-b168a4d32f1c",
  "customerId": "10000000-0000-0000-0000-000000000001",
  "currency": "USD",
  "total": 25.00,
  "status": "Failed",
  "createdUtc": "2026-09-16T14:25:31.4821930+00:00",
  "updatedUtc": "2026-09-16T14:27:02.1130040+00:00",
  "failure": {
    "code": "MaxDeliveryCountExceeded",
    "message": "Message processing failed after five deliveries."
  }
}
```

Current stored failure projections are:

| Cause | `failure.code` | `failure.message` |
|---|---|---|
| Permanent Outbox publication failure | `publish_failed` | `Order publication failed.` |
| Processing exception on the fifth delivery | `MaxDeliveryCountExceeded` | `Message processing failed after five deliveries.` |
| Missing or blank stored failure data | `order_failed` | `Order processing failed.` |

Treat codes as machine-readable and messages as display-safe summaries. Do not
parse messages or expect dependency exception text.

### Owner isolation and `404`

The GET query requires both Order ID and owner to match. A valid ID that does
not exist and an ID owned by another caller both return the same `404` Problem
Details response titled `Order not found`. This prevents the endpoint from
confirming another owner's Order. A path segment that is not a UUID does not
match the route and receives the framework's generic `404` response.

Anonymous local mode has one shared owner named `anonymous`; it does not isolate
different local callers from one another.

## ETags, conditional GET, and polling

Every `200` status response includes a strong `ETag` generated by base64
encoding the SQL `rowversion` and surrounding it with quotes:

```http
ETag: "AAAAAAAAB9E="
```

Send the value exactly, including quotes:

```http
GET /api/v1/orders/01993d34-91c2-7a08-a3d5-b168a4d32f1c HTTP/1.1
If-None-Match: "AAAAAAAAB9E="
```

The implementation recognizes:

- The exact current strong tag.
- The exact current tag prefixed with `W/`.
- `*` for any existing owned Order.
- A comma-separated list containing any matching value.

Tag comparison is ordinal and case-sensitive after surrounding whitespace is
trimmed. Conditional evaluation happens only after the owned Order is found.

On a match, the API returns:

```http
HTTP/1.1 304 Not Modified
ETag: "AAAAAAAAB9E="
```

The `304` has no body. It also has no `Retry-After` header because conditional
matching occurs before the active-status polling hint is added. Continue using
the polling interval already held by the client.

On a non-match, the API returns `200`, a body, and the current `ETag`. Save that
new tag for the next poll. `If-Match` and conditional writes are not supported.

### `Retry-After`

| Response | Header behavior |
|---|---|
| Successful `POST` (`202`) | `Retry-After: 1` |
| `GET` `200` with `Pending`, `Queued`, or `Processing` | `Retry-After: 1` |
| `GET` `200` with `Completed` or `Failed` | No `Retry-After`; stop polling. |
| Conditional `GET` `304` | No `Retry-After`; retain the prior interval. |
| Rate-limited `429` | No `Retry-After` is currently emitted, despite the Problem Details guidance to try later. Apply client backoff. |

The value is a delay in seconds. It is a minimum polling hint, not a completion
deadline or service-level guarantee.

### Order status meanings

| Status | Terminal | Client interpretation |
|---|---|---|
| `Pending` | No | Order, items, and Outbox Message are committed in SQL. Publication has not yet been recorded. |
| `Queued` | No | The publisher sent the message and recorded publication. |
| `Processing` | No | Worker intermediate state. It is written in the same SQL transaction as `Completed` and is normally not observable. |
| `Completed` | Yes | Worker committed successful simulated processing and its Inbox receipt. |
| `Failed` | Yes | Publication or processing reached a terminal failure. Inspect `failure`. |

Polling can skip `Queued` and `Processing`. It can move directly from `Pending`
to `Completed` because broker delivery can race publication recording and the
Worker commits `Processing` and `Completed` together. Status is a current
snapshot; no status history exists.

Recommended polling loop:

1. Use `Location` or `statusUrl` from `202` rather than constructing another
   path.
2. Wait at least the supplied delay.
3. GET the status and save its `ETag`.
4. On later polls, send `If-None-Match`.
5. On `304`, keep the prior representation and wait again.
6. On `200`, replace the representation and tag.
7. Stop on `Completed` or `Failed`.
8. Apply exponential backoff with jitter to transport failures, `429`, and
   retryable `5xx` responses. A long-running active state is not itself proof
   that the original Order was lost.

## Authentication and owner derivation

The API has two mutually exclusive startup modes.

| Mode | Request behavior | Owner derivation |
|---|---|---|
| JWT bearer enabled | Both Order endpoints require an authenticated token accepted for the configured authority and audience. | SHA-256 of exact UTF-8 `iss`, one U+001F separator, and exact UTF-8 `sub`; stored internally as uppercase hexadecimal. |
| Local anonymous | No bearer token is required. | Literal owner `anonymous`. |

JWT inbound claim mapping is disabled. The policy reads claims named exactly
`iss` and `sub`. Both must be non-blank. `iss` is limited to 2048 characters and
`sub` to 200 characters. Their values are case-sensitive and are not trimmed;
the same `sub` from different issuers, or a case variant, derives a different
owner. Other token claims do not participate in ownership.

JWT mode validates identity but defines no application role or scope policy.
Deployment ingress authorization remains a separate concern.

### Local anonymous guard

The process refuses to start with authentication disabled unless all three
conditions hold:

1. `Authentication:AllowAnonymousLocal` is explicitly `true`.
2. The ASP.NET Core environment is `Development`.
3. Service Bus mode is `Emulator`.

This is a local convenience, not a production security boundary. All anonymous
callers share Orders and one idempotency-key namespace.

## Problem Details and HTTP failures

Application-generated errors use RFC Problem Details with content type
`application/problem+json`. Current Problem Details responses are extended with:

| Property | Meaning |
|---|---|
| `code` | Stable machine-readable HTTP error code. Current handlers use the default `request_failed` for all HTTP failures, including validation, conflict, not found, rate limiting, and unhandled errors. It is not the same namespace as Order `failure.code`. |
| `traceId` | Current activity ID, or the ASP.NET Core request trace identifier when no activity exists. Treat it as opaque and include it in support or log-correlation reports. It changes per request. |
| `errors` | Present on validation responses only. Maps field paths to arrays of messages. |

Other standard members can include `type`, `title`, `status`, and `detail`.
Framework-generated `type` links and generic titles can vary with the ASP.NET
Core patch version; clients should branch on HTTP status and `code`, not those
strings.

### Validation example

Request with a valid idempotency header but invalid body:

```json
{
  "customerId": "not-a-uuid",
  "currency": "usd",
  "items": []
}
```

Response shape:

```http
HTTP/1.1 400 Bad Request
Content-Type: application/problem+json

{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "customerId": [
      "A non-empty UUID is required."
    ],
    "currency": [
      "Currency must be a 3-letter uppercase ISO code."
    ],
    "items": [
      "Items must contain between 1 and 100 products."
    ]
  },
  "traceId": "00-4d6f3d0a8fb427d4db4ad6a15b8e4ed2-23335fc851f91b7a-01",
  "code": "request_failed"
}
```

The `type` URL and `traceId` above are realistic examples, not fixed literal
values.

### Outcome table

| Status | When returned | Implemented response notes |
|---:|---|---|
| `400 Bad Request` | Header/business validation, malformed JSON, empty or unconvertible fields, or another bad request. | Business and idempotency validation include `errors`; parser-level failures may be generic Problem Details. |
| `401 Unauthorized` | JWT mode has no acceptable bearer token. | Generic Problem Details and the authentication challenge behavior. |
| `403 Forbidden` | Token is authenticated but the Order policy fails, including invalid or missing raw `iss`/`sub`. | Generic Problem Details. |
| `404 Not Found` | Owned Order is absent, another owner owns that UUID, route UUID is invalid, or another route is absent. | Valid Order-route misses use title `Order not found`; other misses use a generic title. |
| `409 Conflict` | Same owner and normalized idempotency key already exist with a different request fingerprint. | Title `Idempotency key conflict`; detail `The idempotency key was already used with a different request payload.` |
| `413 Content Too Large` | Request body exceeds 1 MiB when the application server enforces the configured limit. | Generic Problem Details when rejection reaches application error handling. An upstream server may supply its own response instead. |
| `415 Unsupported Media Type` | POST body media type is not supported for JSON binding. | Generic Problem Details. |
| `429 Too Many Requests` | The in-process token bucket has no token available. | Title `Rate limit exceeded`; detail `Try the request again later.` No body is queued and no `Retry-After` header is set. |
| `500 Internal Server Error` | An unhandled application or dependency error occurs. | Generic Problem Details. Exception details are not intentionally added to the response; use `traceId` for correlation. |

Status-code pages also produce Problem Details for otherwise empty `4xx` and
`5xx` application responses. A reverse proxy or web server can reject a request
before application middleware and may then return a different shape.

## Request size and rate limits

| Limit | Current value | Scope |
|---|---:|---|
| Request body | 1 MiB (`1,048,576` bytes) | Kestrel-wide limit plus explicit POST endpoint metadata. |
| Token bucket capacity | 200 requests | Per partition, per API process. |
| Token replenishment | 100 requests each second | Per partition, per API process. |
| Rate-limit queue | 0 | Requests without a token are rejected immediately with `429`. |

Authenticated rate-limit partitions use only the raw JWT `sub` value, not the
issuer-derived owner key. Consequently, equal subjects from different issuers
have separate Order ownership but share a rate-limit partition within one API
process. Anonymous partitions use the observed remote IP address. Forwarded
client addresses are honored only for explicitly configured trusted proxies.

Rate limiting is memory-local. Multiple API replicas do not share counters, and
restarting a replica resets its buckets. `/alive` and `/health` are exempt;
Order endpoints and the Development OpenAPI endpoint use the global limiter.

## OpenAPI

When the environment is `Development`, generated OpenAPI JSON is available at:

```text
/openapi/v1.json
```

It is not mapped in other environments. No Swagger UI is included. The
generated document is useful for discovery, but runtime constraints and
idempotency semantics in this document remain important because not all of them
are expressible by the generated schema.

## Compatibility rules

- Existing `v1` request and response fields retain their meanings.
- Compatible additive fields or endpoints may be added within `v1`. Clients
  must ignore unknown response properties.
- A change that removes or renames fields, changes accepted meanings, or
  otherwise breaks existing clients requires a new major URL such as `/api/v2`.
- Clients should send canonical camel-case JSON, JSON numbers for decimals, and
  canonical UUID strings even where the current parser accepts alternatives.
- Order status strings and machine-readable failure codes are case-sensitive.
- Message contract versions are internal to asynchronous transport and evolve
  independently of the HTTP major version.

Only `v1` exists today; these rules do not imply a planned `v2`.

## Security and privacy constraints

- Use TLS outside isolated local development. This repository does not perform
  TLS termination for a production deployment.
- Never place access tokens, credentials, secrets, or sensitive customer data
  in `Idempotency-Key`. The normalized key is stored in SQL in plaintext.
- Accepted customer IDs, product IDs, prices, totals, and serialized Order
  message data are persisted in SQL. Apply database access, retention, backup,
  and privacy controls appropriate to that data.
- The API returns only owner-scoped Orders. Do not weaken the issuer-plus-subject
  derivation to subject-only ownership.
- Anonymous mode provides no caller isolation and must remain local and
  emulator-only.
- Failure responses and stored Order failures expose sanitized summaries, not
  raw dependency exceptions. Server logs do not intentionally log request
  bodies, credentials, or raw idempotency keys.
- Trust forwarded headers only from configured proxy IPs; otherwise rate-limit
  identity can be spoofed through an incorrectly trusted proxy path.
- JWT mode has no scope or role authorization beyond authenticated ownership.
  Apply any required coarse-grained access policy at a trusted ingress or add an
  explicit application policy before exposing the API.

## Client guidance

- Generate one high-entropy idempotency key per logical Order and retain it
  until the `POST` outcome is known.
- Retry an ambiguous POST timeout with the same key and equivalent payload.
  Never generate a new key merely because the response was lost.
- Do not rely on key casing to distinguish operations.
- Treat `202` as durable acceptance, not completion. Poll the supplied URL.
- Preserve ETag quotes and use `If-None-Match` to reduce unchanged response
  bodies.
- Honor the one-second hint and back off with jitter on `429`, network errors,
  and retryable `5xx` responses.
- Stop polling only at `Completed` or `Failed`; intermediate statuses can be
  skipped.
- Treat Problem Details `traceId` as opaque. Log it with the HTTP status and
  `code`, but do not expose bearer tokens or request bodies in client logs.
- Ignore unknown response properties so additive `v1` evolution remains safe.

## Known limitations

- Only create and single-Order status retrieval exist. There is no list,
  search, status history, update, cancellation, deletion, webhook, or push API.
- An idempotent replay always renders `status: "Pending"`; it does not project
  the Order's current state into the `202` body.
- Idempotency keys have no expiry, lookup endpoint, or explicit binary
  collation. Database collation can affect key comparison.
- Order item details are not returned by the status resource.
- ETags apply only to status GETs. No cache lifetime or `Cache-Control` policy is
  emitted, and `304` does not repeat the polling delay.
- `429` does not include `Retry-After`.
- Rate limits are per process, not cluster-wide, and authenticated partitions
  use `sub` without `iss`.
- The generated OpenAPI document is Development-only and there is no bundled
  interactive API UI.
- Browser CORS policy is not configured by the application.
- Processing is simulated; `Completed` does not represent payment, inventory,
  shipping, or another external fulfillment side effect.
- Delivery is at least once, not globally exactly once. Status can remain active
  if infrastructure remains unavailable or a broker message expires without
  Worker handling. Operational recovery is outside this HTTP contract.
- There is no client-visible deadline or maximum processing time.
- A `null` element inside `items` is not explicitly handled by business
  validation and can produce `500` instead of a field-level `400`. Clients must
  send an object for every array element.

## Related documentation

- [System architecture](architecture.md)
- [Infrastructure guide](infrastructure.md)
- [Foundation specification](azure-service-bus-order-processing-foundation-spec.md)
- [Repository README](../README.md)
