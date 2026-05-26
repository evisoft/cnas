# Feature — Notifications

## What it is

In-app notifications (toast + dashboard tile + persistent inbox) plus
outbound delivery through MNotify (email / SMS / push). Per-step
notification strategies bound to workflow definitions. Template-driven
content, multi-locale.

## TOR / UC mapping

- **UC22** — Notific utilizatori.
- TOR clauses: CF 22.*, MR.

## Surface

| Endpoint | Auth | Purpose |
|---|---|---|
| `GET /api/notifications/mine` | `CnasUser` | Inbox for current user |
| `POST /api/notifications/{sqid}/read` | `CnasUser` | Mark read |
| `POST /api/m-notify-templates-admin` | `CnasAdmin` | Edit MNotify template |
| `POST /api/workflow-notification-strategies` | `CnasAdmin` | Per-workflow-step strategy |

## Code map

- Controllers
  - [`NotificationsController.cs`](../../src/Cnas.Ps.Api/Controllers/NotificationsController.cs)
  - [`MNotifyTemplatesAdminController.cs`](../../src/Cnas.Ps.Api/Controllers/MNotifyTemplatesAdminController.cs)
  - [`WorkflowNotificationStrategiesController.cs`](../../src/Cnas.Ps.Api/Controllers/WorkflowNotificationStrategiesController.cs)
- Application services
  - `INotificationService`, `IMNotifyTemplateService`,
    `IMNotifyBounceHandler`.
- Blazor front-end
  - `ToastNotificationHost.razor` (wired into `MainLayout.razor`)
  - `ClientToastQueue` (scoped per circuit)
  - `ClientNotificationPoller` (pull on Inbox.razor first render)

## Data model

| Entity | Purpose |
|---|---|
| `Notification` | One row per delivered in-app notification. Carries `ReadAtUtc`, channel, deep-link, sensitivity. |
| `MNotifyTemplate` | Localised email / SMS / push template content. |
| `NotificationDeliveryStatus` enum | Drives the Annex 6g delivery report. |

## Workflows

```
NotificationService.EnqueueAsync
  ├─ Write Notification row (in-app)
  ├─ If recipient has email + national id → MNotifyClient.SendAsync (Polly-wrapped)
  ├─ Honour per-channel opt-out (UserProfile)
  ├─ Persist NotificationDeliveryStatus
  └─ On MNotify failure → FailedJob (replayable)
```

Blazor front-end:

```
ClientNotificationPoller (Inbox.razor first render)
  ↓ pulls GET /api/notifications/mine?unreadOnly=true
  ↓ dedups by Sqid id
  ↓ pushes new arrivals into IClientToastQueue
ToastNotificationHost (MainLayout) renders <article class="toast"> per row
```

## Business rules

- Toast deduplication keyed on Sqid id — repeat polls never re-toast.
- Per-channel opt-out (email / SMS / push) honoured before MNotify
  call.
- Templates use the same multi-locale fallback chain as the rest of
  the system (per-culture → RO → EN → base → empty).
- Delivery status drives Annex 6g — see [`reporting.md`](reporting.md).

## Tests

- `tests/Cnas.Ps.Web.Tests/Components/ToastNotificationHostTests.cs`
- `tests/Cnas.Ps.Web.Tests/Services/ClientNotificationPollerTests.cs`
- `tests/Cnas.Ps.Infrastructure.Tests/Dashboard/UnreadNotificationsTileProducerTests.cs`

## What's NOT here

- SignalR push channel for sub-30 s latency — deferred; today the
  poller fires on Inbox.razor first render and on programmatic
  enqueue. Wiring a periodic timer would need a Blazor Server vs.
  WASM-specific harness.
