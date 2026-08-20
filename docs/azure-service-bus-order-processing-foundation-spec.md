---
id: ABS-001
title: Build a reliable local Azure Service Bus order-processing foundation
status: open
labels:
  - ready-for-agent
---

## Problem Statement

Developers need a production-quality .NET foundation for accepting work over HTTP, processing it asynchronously, and exposing durable processing status. The system must behave like an Azure Service Bus application while remaining fully runnable on a local development machine without an Azure subscription.

The foundation must address the failure modes hidden by a basic queue demo: duplicate HTTP submissions, process crashes between database and broker operations, duplicate broker delivery, malformed or forged messages, transient dependency outages, poison messages, schema drift, safe horizontal scaling, observability, and local credential handling. It must also avoid a queue-per-endpoint topology that becomes operationally unmanageable as the API grows.

## Solution

Provide a .NET 10 solution containing an Order API, an Order Worker, versioned message Contracts, and shared Persistence mappings. The API accepts an Order, atomically stores the Order and an Outbox Message in SQL Server, returns `202 Accepted`, and publishes `OrderCreatedV1` asynchronously to one `orders` queue. The Worker validates each message and its persisted provenance, processes it at least once, records an Inbox Message for deduplication, and updates durable Order Status. Clients poll the Order API until the Order reaches `Completed` or `Failed`.

Provide Docker Compose infrastructure using SQL Server 2022 and Microsoft Azure Service Bus Emulator 2.0.1. Use the same local SQL Server instance for emulator internals and a separate `OrdersDb` application database. Keep local credential bootstrap separate from production schema migrations. Support host-run applications for debugging and optional non-root application containers.

## User Stories

1. As an API client, I want to submit an Order over HTTP, so that it can be processed asynchronously.
2. As an API client, I want a successful submission to return `202 Accepted`, so that I know processing continues outside the request.
3. As an API client, I want the accepted response to contain the Order ID, initial Order Status, and status URL, so that I can track the Order.
4. As an API client, I want a `Location` header, so that standard HTTP tooling can discover the Order resource.
5. As an API client, I want a `Retry-After` hint, so that I can poll without excessive traffic.
6. As an API client, I want invalid Order fields reported using Problem Details and field-level validation errors, so that I can correct the request.
7. As an API client, I want Customer IDs and Product IDs validated as non-empty UUIDs, so that malformed identifiers never enter processing.
8. As an API client, I want an Order to contain between one and one hundred unique products, so that requests remain bounded and unambiguous.
9. As an API client, I want quantities and Unit Prices constrained to supported SQL ranges, so that accepted values remain persistable.
10. As an API client, I want Currency represented by an uppercase three-letter code, so that money has an explicit denomination.
11. As an API client, I want the Order Total calculated by the service, so that clients cannot submit inconsistent totals.
12. As an API client, I want an `Idempotency-Key` to identify retries of the same submission, so that network retries do not create duplicate Orders.
13. As an API client, I want a repeated key with the same normalized request to return the original Order, so that retries are safe.
14. As an API client, I want a repeated key with a different request to return `409 Conflict`, so that accidental key reuse is visible.
15. As an API client, I want Order Status to persist independently of Service Bus availability, so that temporary broker outages do not lose accepted work.
16. As an API client, I want to retrieve Order Status by Order ID, so that I know whether processing is pending, queued, active, complete, or failed.
17. As an API client, I want ETags and `304 Not Modified` responses while polling, so that unchanged status responses are efficient.
18. As an API client, I want only sanitized failure codes and messages, so that failures are actionable without exposing internals.
19. As an authenticated API client, I want Orders isolated by exact token issuer and subject identity, so that another identity cannot read or collide with my Orders.
20. As a local developer, I want an explicitly enabled anonymous mode, so that local emulator development does not require an identity provider.
21. As a security engineer, I want anonymous mode limited to Development with emulator transport, so that production cannot accidentally run unauthenticated.
22. As a security engineer, I want optional JWT validation with authority and audience checks, so that production can add defense in depth behind an ingress or API gateway.
23. As a platform engineer, I want Azure mode to use `DefaultAzureCredential`, so that future deployments can use Workload Identity instead of Service Bus secrets.
24. As an API operator, I want token-bucket rate limiting by authenticated identity or source address, so that one caller cannot consume all API capacity.
25. As an API operator, I want request bodies capped at one MiB, so that oversized HTTP payloads cannot exhaust process memory.
26. As an API operator, I want lightweight liveness and SQL-backed readiness endpoints, so that orchestration can distinguish process failure from dependency failure.
27. As an API operator, I want the API to remain ready during a Service Bus outage, so that the Outbox can continue accepting durable work.
28. As a publisher, I want the Order and Outbox Message committed in one SQL transaction, so that accepted Orders cannot disappear before publication.
29. As a publisher, I want multiple API replicas to lease Outbox Messages safely, so that horizontal scale does not cause uncontrolled duplicate publishing.
30. As a publisher, I want a stable message ID across retries, so that broker duplicate detection and consumer deduplication remain effective.
31. As a publisher, I want transient failures retried with capped exponential backoff and jitter, so that broker outages do not cause a retry storm.
32. As a publisher, I want permanently invalid Outbox Messages quarantined, so that poison data does not retry forever.
33. As an operator, I want published Outbox Messages retained for seven days and then cleaned in bounded batches, so that diagnostics remain available without unbounded growth.
34. As a message consumer, I want all Order work delivered through one workflow-owned `orders` queue, so that queue count does not grow with HTTP endpoints.
35. As a message consumer, I want message Contracts versioned independently of API routes, so that producer and consumer evolution is explicit.
36. As a message consumer, I want envelope metadata and body identities to agree, so that inconsistent messages are rejected.
37. As a message consumer, I want message size and business fields bounded before processing, so that malformed or hostile messages cannot consume excessive resources.
38. As a message consumer, I want every message matched to its persisted Outbox provenance, so that forged broker messages cannot mutate Orders.
39. As a message consumer, I want invalid messages dead-lettered without trusting their Order metadata, so that malformed input cannot force arbitrary Order failures.
40. As a message consumer, I want manual completion only after the SQL transaction commits, so that crashes cannot acknowledge uncommitted work.
41. As a message consumer, I want Inbox Message deduplication by message ID, so that redelivery does not repeat business effects.
42. As a message consumer, I want duplicate messages completed as no-ops, so that normal at-least-once delivery remains efficient.
43. As a message consumer, I want transient processing errors abandoned for retry, so that short dependency failures recover automatically.
44. As a message consumer, I want a message dead-lettered after five failed deliveries, so that poison messages stop blocking normal work.
45. As an Order client, I want a valid Order marked `Failed` when its processing exhausts retries, so that status does not remain active forever.
46. As an operator, I want dead-letter reasons and diagnostics sanitized, so that broker diagnostics are useful without leaking payloads or secrets.
47. As an operator, I want bounded Worker concurrency and prefetch, so that throughput is configurable without unbounded memory or SQL pressure.
48. As an operator, I want Worker lock renewal bounded to five minutes, so that long processing can finish without creating permanently held locks.
49. As an operator, I want Worker readiness to verify SQL and non-destructive Service Bus receive connectivity, so that unhealthy consumers stop receiving traffic.
50. As an operator, I want graceful shutdown to stop new work and settle in-flight messages safely within thirty seconds, so that deployments minimize duplicate work.
51. As an operator, I want structured privacy-safe logs, so that I can correlate behavior without logging request bodies, credentials, or idempotency keys.
52. As an operator, I want OpenTelemetry traces and metrics with optional OTLP export, so that the same binaries integrate with different observability backends.
53. As an operator, I want W3C trace context propagated through Service Bus properties, so that an Order can be traced across API, Outbox, queue, and Worker.
54. As an operator, I want metrics for API requests, Outbox backlog and failures, Worker duration, duplicates, abandonment, and dead-lettering, so that reliability problems are measurable.
55. As a database operator, I want schema defined by forward-only SQL scripts, so that database changes are explicit and reviewable.
56. As a database operator, I want migrations serialized with a SQL application lock and recorded in Schema Versions, so that concurrent migration attempts are safe.
57. As a database operator, I want applications to reject missing or stale schema versions, so that incompatible binaries do not run silently.
58. As a production operator, I want production database credentials provisioned outside schema migrations, so that repository-known local passwords are never deployed.
59. As a local developer, I want one script to start SQL Server and the Service Bus emulator and wait for readiness, so that startup is deterministic.
60. As a local developer, I want one script to create `OrdersDb`, apply schema migrations, and provision the local application login, so that setup is repeatable.
61. As a local developer, I want SQL application data persisted in a named volume, so that normal container restarts do not erase Orders.
62. As a local developer, I want a guarded destructive reset command, so that I can return to a clean environment intentionally.
63. As a local developer, I want infrastructure-only Compose by default, so that I can debug API and Worker with `dotnet run`.
64. As a local developer, I want an optional application-container profile, so that I can run the complete stack in containers.
65. As a local developer, I want all published local ports bound to loopback, so that repository-known local credentials are not exposed to the network.
66. As a container operator, I want API and Worker images to run non-root with read-only filesystems and dropped capabilities, so that container compromise has less impact.
67. As an Apple Silicon developer, I want the SQL Server x86 emulation limitation documented, so that slower startup and unsupported architecture behavior are understood.
68. As a tester, I want fast validation, fingerprint, state-transition, envelope, retry, and provenance tests without Docker, so that most feedback arrives quickly.
69. As a tester, I want one real process-level end-to-end scenario through HTTP, SQL Server, Service Bus emulator, Worker, and status polling, so that component integration is proven at the highest useful seam.
70. As a tester, I want Docker-backed tests opt-in during the default test command and enabled by the full-test script, so that fast and comprehensive verification are both available.
71. As a maintainer, I want package versions managed centrally and locked, so that restores and container builds are reproducible.
72. As a maintainer, I want nullable analysis, analyzers, warnings as errors, and formatting checks, so that defects are caught before runtime.
73. As a maintainer, I want complete local and production-configuration documentation, so that future contributors can operate and evolve the system safely.

## Implementation Decisions

- Target .NET 10 LTS for every application and test module.
- Organize the solution into Order API, Order Worker, Contracts, and Persistence modules, with test modules for API behavior, Worker behavior, and end-to-end flow.
- Use ASP.NET Core Minimal APIs for a small HTTP surface: create Order, get Order Status, liveness, readiness, and development-only OpenAPI JSON.
- Version the HTTP API by URL major version. Additive compatible changes remain within v1.
- Return RFC Problem Details with stable error codes and trace identifiers for errors.
- Use UUID v7 for server-generated Order, message, correlation, and persistence identifiers.
- Require and normalize one `Idempotency-Key` header, scoped to the caller owner key. Store a SHA-256 fingerprint of canonical request data.
- Derive authenticated ownership from SHA-256 of exact UTF-8 issuer, separator, and subject values. Store it under a binary/case-sensitive SQL collation.
- Permit anonymous Order access only when explicitly enabled in the Development environment and Service Bus emulator mode.
- Use JWT bearer validation when authentication is enabled. Authority and audience are mandatory in that mode.
- Model Order Status as `Pending -> Queued -> Processing -> Completed`, with `Failed` as a terminal transition from active states.
- Return durable Order Status and sanitized failure data. Use SQL `rowversion` values as HTTP ETags.
- Store Orders, Order Items, Outbox Messages, Inbox Messages, and Schema Versions in SQL Server.
- Use EF Core for runtime querying and transactions, not for schema generation.
- Define schema with numbered, forward-only SQL migrations. Serialize migration execution with a SQL application lock.
- Keep fixed local login creation outside numbered production migrations. Production identities and credentials are provisioned separately.
- Require the application database schema version to match the binary's expected version at startup.
- Use one local SQL Server container for emulator internals and a separate `OrdersDb` database. Do not reuse emulator databases for application state.
- Persist Order and Outbox Message atomically before returning `202 Accepted`.
- Run the Outbox Publisher as an API hosted service. Claim up to one hundred due records with SQL locking and one-minute leases.
- Send stable Service Bus message IDs. Use JSON `OrderCreatedV1` with contract name, schema version, Order ID, correlation ID, timestamp, Currency, Total, and Order Items.
- Put contract identity, schema version, Order ID, correlation ID, and W3C trace context in Service Bus application properties. Keep business data authoritative in the body.
- Use one `orders` queue for the Order-processing consumer workflow. Never derive queues from HTTP endpoint count.
- Configure local queue lock duration to one minute, maximum delivery count to five, dead-letter-on-expiration, one-hour local TTL, five-minute duplicate detection, and no sessions.
- Document a seven-day production TTL and ten-minute production duplicate-detection window as deployment configuration, not local emulator settings.
- Configure Worker manual settlement, concurrency sixteen, prefetch thirty-two, and lock renewal up to five minutes. Cap configured concurrency at 256.
- Validate message size, contract metadata, identity agreement, Currency, timestamps, item count, item uniqueness, quantity, Unit Price, and Total before processing.
- Require an unquarantined persisted Outbox Message whose ID, Order ID, contract metadata, and exact payload match the received message before mutating an Order.
- Dead-letter invalid or forged messages without mutating Order Status from untrusted metadata.
- Process valid messages in a serializable SQL transaction that checks Inbox deduplication, transitions the Order, and inserts the Inbox Message before broker completion.
- Use the same message ID for broker duplicate detection and the durable Inbox primary key. Broker duplicate detection supplements but never replaces Inbox idempotency.
- Retry transient SQL operations through EF Core's SQL execution strategy and transient Service Bus operations through SDK retries plus Outbox retry scheduling.
- Quarantine permanent Outbox failures and mark their active Orders failed. Retain failed records for operator review.
- Clean successful Outbox Messages after seven days and Inbox Messages after a configurable thirty days, using bounded hourly batches.
- Expose API readiness based on SQL only. Expose Worker readiness based on SQL and non-destructive Service Bus receiver connectivity.
- Emit JSON console logs. Export logs, traces, and metrics through OTLP only when an endpoint is configured.
- Apply one MiB HTTP request limits and token-bucket rate limiting at one hundred requests per second with burst capacity two hundred, partitioned by caller.
- Honor forwarded headers only when explicit trusted proxy IPs are configured.
- Use Docker Compose for local orchestration. Start infrastructure by default and place API and Worker behind an optional `apps` profile.
- Bind every local host port to loopback.
- Build API and Worker with multi-stage .NET images and run on chiseled-extra .NET 10 runtime images as the non-root application user.
- Make application containers read-only, provide temporary storage only at `/tmp`, drop Linux capabilities, and set no-new-privileges.
- Pin emulator and .NET runtime versions. Keep SQL Server on the serviced `2022-latest` tag as an explicit security-update tradeoff.
- Initialize Git on `main` without creating an initial commit.

## Testing Decisions

- The primary test seam is one process-level Order flow: submit an Order over HTTP, persist it in real SQL Server, publish through the real Service Bus emulator, consume it in the real Worker, and poll the API until durable status is `Completed`. This is the highest seam that proves the system's core promise and is the only cross-component seam.
- Good tests assert externally observable behavior: HTTP status and payloads, durable Order Status, broker settlement outcomes, accepted/rejected message Contracts, and idempotent results. Tests should not assert private method call order or duplicate framework behavior.
- The end-to-end test launches actual API and Worker binaries against Compose infrastructure. It uses unique identifiers, waits on health endpoints, captures redacted process diagnostics, and always terminates child processes.
- Default tests keep the Docker end-to-end scenario discovered but skipped. The full-test script starts infrastructure, applies migrations and local bootstrap, enables the scenario, and runs the entire solution.
- API tests cover request validation boundaries, decimal overflow, canonical request fingerprints, Idempotency Key normalization, Order Status transitions, ETag matching, stored failure projection, exact issuer-subject ownership, Outbox retry delays, and Service Bus message construction.
- Worker tests cover contract and schema mismatch, malformed metadata, identity mismatch, missing correlation metadata, semantic Order validation, inconsistent Total, empty Items, state transitions, retry behavior, and persisted Outbox provenance matching.
- SQL locking, SQL constraints, Service Bus settlement, and EF Core transaction behavior are tested through the real end-to-end seam rather than mocked or approximated with EF Core InMemory.
- Emulator tests run sequentially because the emulator is development-only and has low connection and entity quotas.
- Build verification requires locked restore, zero warnings, formatting compliance, valid default and application-profile Compose configurations, successful non-root container image builds, and no known vulnerable NuGet packages.
- Prior art is the existing API validator and cache tests, Worker validator and state-transition tests, and the Order process end-to-end test already established in this codebase.

## Out of Scope

- Kubernetes manifests, Helm charts, Kustomize overlays, or cluster deployment automation.
- Azure cloud infrastructure provisioning, including Service Bus namespaces, SQL resources, identities, networking, and RBAC assignments.
- CI/CD workflows, image publishing, deployment promotion, and release automation.
- A browser UI, mobile client, administrative dashboard, or embedded observability dashboard.
- Webhooks, SignalR, push notifications, or other alternatives to Order Status polling.
- Automated dead-letter inspection, correction, or replay tooling.
- Additional Service Bus queues, topics, subscriptions, sessions, or queue-per-endpoint routing.
- Global message ordering or exactly-once processing guarantees.
- A production business fulfillment integration; Worker processing is deterministic simulated fulfillment.
- Order listing, cancellation, update, deletion, or status-history APIs.
- Application data retention and archival policies beyond current Outbox and Inbox cleanup.
- Production SQL identity provisioning or secret-store integration.
- Full production authorization scopes and roles; current JWT mode establishes identity and Order ownership while ingress remains the primary external authorization boundary.
- Azure Monitor, Prometheus, Grafana, Jaeger, or another bundled telemetry backend.
- Multi-region failover, geo-disaster recovery, Azure Service Bus Premium partitioning, and cloud autoscaling.

## Further Notes

- The Service Bus emulator is for local development and testing only. It has no SLA and does not reproduce Microsoft Entra authentication, Azure networking, RBAC, autoscaling, portal operations, metrics, geo-disaster recovery, or all production quotas.
- Emulator entities and messages are non-persistent across emulator restarts. Application Order data persists in the SQL named volume until an explicit reset.
- On Apple Silicon, the emulator has an ARM64 image but SQL Server 2022 runs as `linux/amd64` under container translation. Microsoft does not officially support SQL Server's ARM-host emulation path.
- SQL Server and emulator local credentials are intentionally documented development values. Local ports bind only to loopback, `.env` variants are ignored, and these values must never be reused outside isolated development.
- Azure mode deliberately uses identity-based Service Bus authentication. A future Kubernetes deployment should select Workload Identity explicitly and assign API Data Sender and Worker Data Receiver permissions separately.
- The current repository has no Git remote or configured external issue tracker. This specification is published to the local issue tracker fallback with the `ready-for-agent` label.
