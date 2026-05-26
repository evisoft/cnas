# Software Design Document (SDD) — Iterative

> Anchored to TOR ID(s): R2414 (Deliverable 2.1, Milestone M2). Version 0.1,
> iteration 99. Updated at each milestone gate; companion to the structural
> SRS (R2402).

## 1. Purpose

Describe *how* the system is built. Sufficient detail for a new developer to
locate any subsystem, understand the layering, and trace a request from edge
to database. Pointers to actual aggregates instead of duplicating code.

## 2. Scope

Whole programme. Not a tutorial — assumes familiarity with ASP.NET Core, EF
Core, and PostgreSQL.

## 3. Sections

### 3.1 Architecture layers (TOR §3 ARH)

Strict inward dependency direction. Layer boundaries enforced by architecture
tests.

| Layer | Project | Depends on |
|---|---|---|
| Core / Domain | `src/Cnas.Ps.Core/` | (nothing) |
| Application | `src/Cnas.Ps.Application/` | Core |
| Infrastructure | `src/Cnas.Ps.Infrastructure/` | Core, Application |
| API / Presentation | `src/Cnas.Ps.Api/` | All |
| Web (back-office UI + portal) | `src/Cnas.Ps.Web/` | All |
| Contracts (DTOs across boundary) | `src/Cnas.Ps.Contracts/` | (nothing) |

Composition root: `src/Cnas.Ps.Api/Program.cs` +
`src/Cnas.Ps.Api/Composition/`.

See also: [`docs/ARCHITECTURE.md`](../ARCHITECTURE.md).

### 3.2 Persistence

- ORM: EF Core against PostgreSQL 16. Configurations under
  `src/Cnas.Ps.Infrastructure/Persistence/Configurations/`.
- Write context: `CnasDbContext`.
- Read context: `IReadOnlyCnasDbContext`
  (`src/Cnas.Ps.Application/Abstractions/IReadOnlyCnasDbContext.cs`, iter 68/84)
  — routed to the streaming read-replica via the connection-string switch in
  `Cnas.Ps.Infrastructure.Persistence.ReadReplica*`. All reporting queries go
  through this context (PSR 006).
- Pool sizing: `PostgresPoolOptions.cs`. Sits in front of PgBouncer
  (transaction pooling).
- Migrations: code-first; applied at startup in non-production and through
  the migration job in production (R0XXX in `TODO.md`).

### 3.3 Authentication

- External users (citizens, payers): MPass (MGov SSO).
- Internal users: local accounts on `UserProfile`
  (`src/Cnas.Ps.Core/Domain/UserProfile.cs`).
- Refresh tokens: rotated, with reuse detection. Stored hashed.
- Account-enumeration prevention enforced at auth endpoints.

### 3.4 Authorisation

- RBAC: roles via `UserGroup` (`src/Cnas.Ps.Core/Domain/UserGroup.cs`) +
  `UserProfile`.
- ABAC: rule evaluation in `src/Cnas.Ps.Application/Abac/`
  (`AbacRule`, `AbacRuleSet`) — iter 88. Used for resource-level checks
  (own branch, own region, own portfolio).
- 4-eyes principle: bulk actions and high-risk decisions enforced through
  `src/Cnas.Ps.Application/BulkActions/` (iter 81).
- Deny by default at every endpoint; policy declared in
  `src/Cnas.Ps.Api/Authorization/`.

### 3.5 Observability

- Metrics: `CnasMeter` (see references in `docs/performance-ops.md`).
- Audit chain: append-only chained audit records on every domain mutation
  (R0194). Verification job under
  `src/Cnas.Ps.Infrastructure/Audit/`. Aggregates: `AuditLog`,
  `AuditFieldPolicy`, `AuditPolicy`, `AuditableEntity`.
- Tracing: W3C `traceparent` propagated from edge through services.
- Logging: structured key-value with correlation IDs.

### 3.6 Cross-cutting services

- Sqid encoding (CLAUDE.md cardinal rule 3): all external IDs encoded at the
  API boundary. Service lives in Application; controllers and Web hosts
  inject it.
- Time provider: `ITimeProvider` injected throughout — tests use a fixed
  clock; production uses UTC system clock.
- Application-level encryption: `EncryptedString` value converter wraps
  sensitive columns (gov IDs, bank accounts). Keys from MCloud secrets
  store.
- Background jobs: idempotent, retry-with-backoff, monitored. Job host in
  Infrastructure.

### 3.7 Interop edge

- Inbound: `IInteropApi` (`src/Cnas.Ps.Application/Interop/IInteropApi.cs`) +
  `InteropController` (iter 72).
- Offline batch: `OfflineBatchController` +
  `src/Cnas.Ps.Application/Interop/Batch/` (iter 79).
- Outbound: `src/Cnas.Ps.Application/External/` (typed MGov / agency
  facades). Status delta in
  [`docs/EGOV-INTEGRATION-GAP.md`](../EGOV-INTEGRATION-GAP.md).

## 4. Cadence / Lifecycle

- v0.1 issued at M1 close (this document, iteration 99).
- Minor version bump at end of each M2 iteration that introduces or moves a
  subsystem.
- v1.0 frozen for UAT at M6 start.
- Diff history available in git.

## 5. Implementation map

The pointers above link to actual aggregates / projects. This SDD MUST be
kept in sync with the codebase — any layering change requires updating this
file in the same commit.

## 6. Status

Skeleton complete with concrete pointers. Open gaps tracked by TODO R2414:
- Deployment topology diagram (pending Helm-chart freeze).
- Sequence diagrams for top 5 user journeys.
- Detailed integration sequence for each MGov facade.

## 7. References

- `docs/ARCHITECTURE.md`.
- `docs/pm/srs-structural.md` (R2402).
- `docs/pm/tech-infra-requirements.md` (R2403).
- `docs/performance-ops.md`, `docs/operations.md`,
  `docs/production-deployment.md`, `docs/bcp-drp-backup-plan.md`.
- `docs/EGOV-INTEGRATION-GAP.md`.
- `tor/TOR.md` §3, §4, §5.
