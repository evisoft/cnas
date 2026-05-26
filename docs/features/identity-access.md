# Feature — Identity & access (UC18)

## What it is

User accounts, role policies, ABAC (attribute-based) overlay, user
groups, granular permissions, sessions, delegations (MPower), pending
admin actions (4-eyes maker-checker), and the account state machine.
The security backbone of the system.

## TOR / UC mapping

- **UC18** — Utilizatori și acces.
- TOR clauses: CF 18.*, SEC 014, SEC 021–026, SEC 054, R0051–R0059.

## Surface

| Endpoint | Auth | Purpose |
|---|---|---|
| `GET /api/users` | `CnasAdmin` | List users |
| `POST /api/users` | `CnasAdmin` | Create user (4-eyes) |
| `PATCH /api/users/{sqid}` | `CnasAdmin` | Edit user |
| `POST /api/user-groups` | `CnasAdmin` | Group management |
| `POST /api/abac-admin` | `CnasAdmin` | ABAC rule sets |
| `POST /api/access-scope` | `CnasAdmin` | Access-scope rules |
| `POST /api/admin-permissions` | `CnasAdmin` | Granular admin permissions |
| `GET /api/sessions` | `CnasUser` | List own sessions |
| `POST /api/sessions/{sqid}/revoke` | `CnasUser` | Revoke a session |
| `POST /api/delegations` | `CnasDecider` | Grant delegation |
| `POST /api/profile` | `CnasUser` | Edit own profile |
| `POST /api/auth/login` | none | Local login (deferred) |
| `POST /api/auth/refresh` | bearer | Refresh JWT |
| `POST /api/auth/logout` | bearer | Revoke session |
| `POST /api/pending-admin-actions/{sqid}/approve` | `CnasAdmin` | 4-eyes approval |
| `POST /api/sensitive-admin-actions` | `CnasAdmin` | Sensitive action submission |
| `POST /api/user-absences` | `CnasUser` | Self-declared absence |

## Code map

- Controllers
  - [`UsersController.cs`](../../src/Cnas.Ps.Api/Controllers/UsersController.cs)
  - [`UserGroupsController.cs`](../../src/Cnas.Ps.Api/Controllers/UserGroupsController.cs)
  - [`AbacAdminController.cs`](../../src/Cnas.Ps.Api/Controllers/AbacAdminController.cs)
  - [`AccessScopeController.cs`](../../src/Cnas.Ps.Api/Controllers/AccessScopeController.cs)
  - [`AccessScopeBackfillController.cs`](../../src/Cnas.Ps.Api/Controllers/AccessScopeBackfillController.cs)
  - [`AdminPermissionsController.cs`](../../src/Cnas.Ps.Api/Controllers/AdminPermissionsController.cs)
  - [`SessionsController.cs`](../../src/Cnas.Ps.Api/Controllers/SessionsController.cs)
  - [`DelegationsController.cs`](../../src/Cnas.Ps.Api/Controllers/DelegationsController.cs)
  - [`ProfileController.cs`](../../src/Cnas.Ps.Api/Controllers/ProfileController.cs)
  - [`AuthController.cs`](../../src/Cnas.Ps.Api/Controllers/AuthController.cs)
  - [`MPassSamlController.cs`](../../src/Cnas.Ps.Api/Controllers/MPassSamlController.cs)
  - [`PendingAdminActionsController.cs`](../../src/Cnas.Ps.Api/Controllers/PendingAdminActionsController.cs)
  - [`SensitiveAdminActionsController.cs`](../../src/Cnas.Ps.Api/Controllers/SensitiveAdminActionsController.cs)
  - [`UserAbsencesController.cs`](../../src/Cnas.Ps.Api/Controllers/UserAbsencesController.cs)
- Application services
  - `IUserAdministrationService`, `IUserDirectoryService`,
    `IUserAccountStateService`, `ISessionLimitEnforcer`,
    `ISessionLockService`, `IDelegationLifecycleService`,
    `IPendingAdminActionService`, `IPendingAdminActionExecutor`,
    `IProfileService`.
- Infrastructure
  - `Argon2idPasswordHasher` (OWASP 2024 parameters, PHC format).
  - `JwtTokenIssuer` + `RefreshTokenService` (rotation + family reuse
    detection).
  - `MPassSamlAssertionParser` (claim mapping including MPower).

## Authorization model

Tiered RBAC, defined in `AuthorizationComposition.cs`:

```
CnasUser  ⊂  CnasDecider  ⊂  CnasAdmin
CnasTechAdmin   (standalone — infrastructure / system jobs)
```

Higher policies satisfy lower ones — a `CnasAdmin` user passes
`CnasDecider` and `CnasUser` checks transparently. Controllers
reference policy names through `Policies.*` constants, never bare
role strings.

ABAC overlay (R0056 — partial today) — `AbacRule` + `AbacRuleSet` plus
`UserGroup` allow geography / subdivision / document-category
expression rules on top of the role check. The expression engine is
the deferred piece.

## Data model

| Entity | Purpose |
|---|---|
| `UserAccount` / `UserProfile` | Account + profile. `NationalId`, `PhoneE164` encrypted. |
| `UserGroup` | Group membership. |
| `RefreshToken` | Opaque refresh token with family-reuse detection. |
| `DelegationGrant` | MPower-equivalent local delegation. |
| `PendingAdminAction` | Maker side of the 4-eyes queue. |
| `AbacRule` + `AbacRuleSet` | ABAC overlay rules. |
| `GranularPermissionAssignment` | Per-user per-resource grants. |
| `UserAccountState` enum | Active / Suspended / Disabled / Locked. |

## Business rules

- Account state transitions audit-logged per transition.
- Sessions enforce idle timeout (15 min cookie / JWT short-lived).
  Redis-backed session store and concurrent-session cap (R0054) are
  pending.
- Refresh-token rotation — every refresh issues a new token and
  invalidates the prior. A token reuse triggers family revocation.
- 4-eyes maker-checker — every sensitive admin action lands in
  `PendingAdminAction`; the maker and checker cannot be the same user.
  `MakerCheckerExpirySweeper` flips Pending → Expired.
- MPass SAML assertion parser maps `mpower:principal_idnp` and
  `mpower:delegation_id` claims into `ICallerContext` — MPower is
  **not** a separate HTTP service.

## Tests

- `tests/Cnas.Ps.Infrastructure.Tests/Security/`
- `tests/Cnas.Ps.Application.Tests/Identity/`

## What's NOT here

- Local username/password endpoint wiring (R0051) — hasher exists,
  endpoint returns 501 today.
- Redis session store + concurrent-session cap (R0054).
- ABAC expression engine (R0056) — entities + admin endpoints exist;
  the evaluator is deferred.
- Delegation lifecycle UI (R0057) — the claim path works; the admin UI
  to grant / revoke is deferred.
