# Training plan, schedule and materials

> Anchored to TOR ID(s): R2440 (Milestone M5), UTD 002, UTD 011-012.
> Companion to R2441 (admins ≥64h), R2442 (trainers ≥56h), R2443
> (≤100 users ≥40h). Iteration 100.

## 1. Purpose / scope

Defines audiences, syllabi, schedule, materials, languages, and
acceptance criteria for the M5 training programme. Covers preparation,
delivery, examination, and post-training certification for SI
„Protecția Socială".

## 2. Audience tiers

| Tier | Headcount | Min hours | Languages | TOR row |
|---|---|---|---|---|
| End users (CNAS, CTAS, MMPS counter staff) | ≤100 | 40 | RO + RU | UTD 009 / R2443 |
| Trainers (train-the-trainer) | ≥4 | 56 | RO + RU | UTD 008 / R2442 |
| System administrators | ≥2 | 64 | RO + EN | UTD 007 / R2441 |

## 3. Content / procedure

### 3.1 End-user track (RO + RU)

1. Portal navigation, MPass sign-in, language switcher.
2. Submitting `Cerere` (application) — every benefit type.
3. Status follow-up, MSign sign-off, MNotify channels.
4. Document inbox, attachments, audit timeline.
5. Practical scenarios (per benefit) and final assessment.

### 3.2 Trainer track (RO + RU)

1. Everything in 3.1, plus pedagogy and material customisation.
2. Train-the-trainer methodology, learner support, RO/RU bilingual
   delivery checklists.
3. Refresher cadence: maximum 12 months between trainer recerts.

### 3.3 Admin track (RO + EN)

1. ABAC scopes, role management, audit explorer, integrity dashboards.
2. Workflow definitions, change-request workflow (`IChangeRequestService`).
3. Backup & restore drills (`docs/recovery-procedures.md`).
4. Monitoring, performance dashboards (`docs/performance-ops.md`).
5. Integrations operating procedures (`docs/integration/technical-integration-specs.md`).
6. Incident management (PIR 020-023 SLA classes).

## 4. Materials

- Slide decks (RO, RU, EN as per audience) — version-tagged per release.
- Recorded video walkthroughs per role (UTD 011-012).
- Lab environment with seeded data (separate from production).
- Workbook with hands-on exercises and answer key.
- Final-assessment quizzes.
- Bilingual quick-reference cards (UTD 002).

## 5. Schedule

| Week | Track | Mode |
|---|---|---|
| W1 | Admin (RO + EN), trainers (RO) | Classroom + lab |
| W2 | Trainers (RU), end-user cohort 1 (RO) | Classroom + lab |
| W3 | End-user cohorts 2-3 (RO) | Classroom + remote |
| W4 | End-user cohorts 4-5 (RU), make-up sessions | Classroom + remote |

## 6. Acceptance criteria / sign-off

- Headcounts met for each tier (R2441/R2442/R2443).
- Each learner reaches the documented minimum hours.
- Each learner passes the final assessment (≥75%).
- Materials archived in both languages per tier.
- Training attendance log and assessment results signed by CNAS HR
  and the supplier training lead.

## 7. Implementation map

Training is documentation-and-process; the platform supports it via:

| Need | System surface |
|---|---|
| Sandbox tenants | Docker compose / Helm `staging` overlay (R2415) |
| Synthetic data | Seed scripts and fixtures referenced from SRS / SDD |
| Role assignment for learners | ABAC roles (`AbacAdminController`) |
| Localisation | `PagesResource.resx` (RO/RU/EN) — tri-lingual smoke gate R2711 |

## 8. Status / open gaps

- Slide decks and workbooks — pending (R2440).
- Final assessment item bank — pending.
- Schedule above is indicative; CNAS HR sign-off pending.
- Trainer recert cadence not yet automated.

## 9. References

- TOR §UTD 002, 007-009, 011-012
- TODO.md rows R2440-R2443
- [`../operations/operational-guides-index.md`](../operations/operational-guides-index.md)
