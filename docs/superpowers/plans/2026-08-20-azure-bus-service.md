# Azure Bus Service Implementation Plan

> **For agentic workers:** Implement tasks in dependency order and verify each deliverable before continuing.

**Goal:** Build a production-quality .NET 10 order API and queue worker with durable status, SQL outbox/inbox reliability, and a local Azure Service Bus emulator environment.

**Architecture:** The API writes orders and outbox messages atomically to SQL Server, then a hosted publisher sends versioned contracts to one Service Bus queue. The worker consumes at least once and commits inbox deduplication with status transitions in one SQL transaction. Docker Compose supplies one SQL Server instance and Microsoft's Service Bus emulator.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, EF Core SQL Server, Azure.Messaging.ServiceBus, Azure.Identity, OpenTelemetry, xUnit v3, Docker Compose.

## Global Constraints

- Keep queue boundaries aligned with consumer workflows, never HTTP endpoints.
- Define schema through forward-only SQL scripts; use EF Core only at runtime.
- Use explicit emulator and Azure transport modes without fallback.
- Keep Kubernetes, cloud IaC, CI, UI, dashboards, and automated dead-letter replay out of scope.
- Initialize Git on `main`; create no commit.

---

### Task 1: Solution Foundations

**Files:**
- Create: `AzureBusService.sln`
- Create: `Directory.Build.props`
- Create: `Directory.Packages.props`
- Create: `global.json`
- Create: `.editorconfig`
- Create: `.gitignore`
- Create: `src/AzureBusService.Contracts/AzureBusService.Contracts.csproj`
- Create: `src/AzureBusService.Persistence/AzureBusService.Persistence.csproj`
- Create: `src/AzureBusService.Api/AzureBusService.Api.csproj`
- Create: `src/AzureBusService.Worker/AzureBusService.Worker.csproj`

**Interfaces:**
- Produces: buildable .NET 10 solution with nullable references, warnings as errors, central package versions, and locked restore.

- [x] Scaffold solution and projects.
- [x] Add references from API and Worker to Contracts and Persistence.
- [x] Run `dotnet restore --use-lock-file` and expect successful restore.
- [x] Run `dotnet build --no-restore` and expect zero warnings and errors.

### Task 2: Contracts and Persistence

**Files:**
- Create: `src/AzureBusService.Contracts/OrderCreatedV1.cs`
- Create: `src/AzureBusService.Persistence/OrderStatus.cs`
- Create: `src/AzureBusService.Persistence/Entities.cs`
- Create: `src/AzureBusService.Persistence/OrdersDbContext.cs`
- Create: `database/migrations/001_initial_schema.sql`
- Create: `database/local/app_login.sql`

**Interfaces:**
- Produces: `OrderCreatedV1`, `OrdersDbContext`, order/outbox/inbox records, guarded status model, and SQL schema matching EF mappings.

- [x] Define immutable camel-case JSON message contract with UUID v7 metadata.
- [x] Define SQL-backed order, item, outbox, and inbox entities with `rowversion` concurrency.
- [x] Map entities and constraints explicitly in `OrdersDbContext`.
- [x] Add transactional, forward-only SQL schema scripts and separate local credential bootstrap.
- [x] Build solution and verify EF mappings compile.

### Task 3: Order API and Outbox

**Files:**
- Create: `src/AzureBusService.Api/Program.cs`
- Create: `src/AzureBusService.Api/Orders/OrderModels.cs`
- Create: `src/AzureBusService.Api/Orders/OrderEndpoints.cs`
- Create: `src/AzureBusService.Api/Orders/OrderService.cs`
- Create: `src/AzureBusService.Api/Messaging/ServiceBusOptions.cs`
- Create: `src/AzureBusService.Api/Messaging/OutboxPublisher.cs`
- Create: `src/AzureBusService.Api/Messaging/OutboxCleanupService.cs`
- Create: `src/AzureBusService.Api/appsettings.json`
- Create: `src/AzureBusService.Api/appsettings.Development.json`

**Interfaces:**
- Consumes: `OrdersDbContext`, `OrderCreatedV1`.
- Produces: `POST /api/v1/orders`, `GET /api/v1/orders/{id}`, `/health`, `/alive`, OpenAPI JSON, transactional outbox publication, idempotent creation, ETags, and optional JWT authentication.

- [x] Add failing API service tests for validation, idempotent replay, payload mismatch, and status lookup.
- [x] Implement transactional order/outbox creation and RFC Problem Details responses.
- [x] Implement leased outbox claims, stable message IDs, retries, and cleanup.
- [x] Add rate limits, body limits, structured logs, telemetry, and validated options.
- [x] Run API tests and expect all to pass.

### Task 4: Queue Worker

**Files:**
- Create: `src/AzureBusService.Worker/Program.cs`
- Create: `src/AzureBusService.Worker/OrderMessageProcessor.cs`
- Create: `src/AzureBusService.Worker/WorkerHealthState.cs`
- Create: `src/AzureBusService.Worker/appsettings.json`
- Create: `src/AzureBusService.Worker/appsettings.Development.json`

**Interfaces:**
- Consumes: `OrderCreatedV1`, `OrdersDbContext`, one `orders` queue.
- Produces: bounded concurrent processing, inbox idempotency, transactional state changes, explicit dead-letter behavior, readiness, liveness, and graceful shutdown.

- [x] Add failing processor tests for completion, duplicate delivery, missing order, and invalid contract.
- [x] Implement manual settlement and five-delivery terminal failure behavior.
- [x] Commit inbox receipt and order status in one SQL transaction before completing messages.
- [x] Add health listener and OpenTelemetry instrumentation.
- [x] Run worker tests and expect all to pass.

### Task 5: Local Infrastructure and Containers

**Files:**
- Create: `compose.yaml`
- Create: `infra/servicebus/Config.json`
- Create: `.env.example`
- Create: `src/AzureBusService.Api/Dockerfile`
- Create: `src/AzureBusService.Worker/Dockerfile`
- Create: `scripts/start-infra.sh`
- Create: `scripts/migrate.sh`
- Create: `scripts/reset.sh`
- Create: `scripts/test-all.sh`

**Interfaces:**
- Produces: SQL Server 2022, Service Bus emulator 2.0.1, durable application database volume, one-shot health gate, explicit migrations, optional application containers, and non-root .NET 10 images.

- [x] Configure emulator queue limits and local connection endpoints.
- [x] Configure one shared SQL instance with isolated application database.
- [x] Add startup, migration, reset, and full-test scripts.
- [x] Add multi-stage non-root chiseled container builds.
- [x] Run `docker compose config` and expect valid resolved configuration.

### Task 6: Verification and Documentation

**Files:**
- Create: `tests/AzureBusService.Api.Tests/AzureBusService.Api.Tests.csproj`
- Create: `tests/AzureBusService.Worker.Tests/AzureBusService.Worker.Tests.csproj`
- Create: `tests/AzureBusService.EndToEndTests/AzureBusService.EndToEndTests.csproj`
- Create: `README.md`

**Interfaces:**
- Produces: fast default tests, sequential Docker-backed end-to-end coverage, and complete operating instructions.

- [x] Test status transitions, request fingerprints, duplicate processing, API contracts, and end-to-end queue flow.
- [x] Document architecture, ports, setup, migrations, requests, status flow, tests, reset, emulator limitations, and Azure production configuration.
- [x] Run `dotnet format --verify-no-changes` and expect success.
- [x] Run `dotnet build --no-restore` and `dotnet test --no-build` and expect success.
- [x] Review full diff for security, correctness, and accidental scope expansion.
