# Role documentation index

> One file per work role defined in the TOR. Each doc describes what
> the role does in the system, which use cases they touch, which
> permissions / RBAC policies they map to, day-to-day tasks, and the
> features (in [`../features/`](../features/)) they own.

## TOR role → RBAC policy map

The TOR defines **8 human roles** plus the **SYS** non-human actor.
SI PS implements four broad RBAC policies (`CnasUser`,
`CnasDecider`, `CnasAdmin`, `CnasTechAdmin`); each TOR role maps to
one of them, often with an additional ABAC overlay (geography,
subdivision, document category) and per-step workflow ACL.

| TOR role | Code | RBAC policy | Doc |
|---|---|---|---|
| Utilizator Internet | UI | none (anonymous) | [`utilizator-internet.md`](utilizator-internet.md) |
| Utilizator autorizat | UA | `CnasUser` (citizen variant) | [`utilizator-autorizat.md`](utilizator-autorizat.md) |
| Solicitant | SOL | `CnasUser` (applicant variant) | [`solicitant.md`](solicitant.md) |
| Utilizator CNAS | UCNAS | `CnasUser` (staff variant) | [`utilizator-cnas.md`](utilizator-cnas.md) |
| Șeful direcției | SD | `CnasDecider` | [`seful-directiei.md`](seful-directiei.md) |
| Șeful CNAS | SC | `CnasDecider` (executive variant) | [`seful-cnas.md`](seful-cnas.md) |
| Administrator de sistem | AS | `CnasAdmin` | [`administrator-sistem.md`](administrator-sistem.md) |
| Administrator tehnic STISC | AT | `CnasTechAdmin` | [`administrator-tehnic.md`](administrator-tehnic.md) |
| Sistem (automated) | SYS | n/a (background-job identity) | not documented as a role — see [`../features/background-jobs.md`](../features/background-jobs.md) |

## TOR use-case ownership (Figura 2.4)

```
UI    → UC01, UC02
UA    → UC01, UC02, UC09, UC11
SOL   → UC01, UC02, UC06, UC11, UC13
UCNAS → UC03, UC04, UC05, UC06, UC07, UC08, UC09, UC11, UC12, UC13
SD    → UC10, UC08, UC09
SC    → UC10, UC09
AS    → UC15, UC16, UC17, UC18, UC20
AT    → UC20, UC23
SYS   → UC14, UC19, UC20, UC21, UC22, UC23
```

## How to read a role doc

1. **Who they are** — one paragraph.
2. **TOR identifier + RBAC policy + ABAC overlay** if any.
3. **Use cases owned**.
4. **Day-to-day tasks** — concrete things they do.
5. **Features they touch** — links into [`../features/`](../features/).
6. **What they cannot do** — explicit boundary.
7. **Onboarding & offboarding** — admin steps.
