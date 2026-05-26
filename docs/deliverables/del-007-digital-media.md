# DEL 007 — Digital media delivery (index)

> Anchored to TOR ID(s): R2606 (TOR §7.1). Index doc; defines the
> physical-medium delivery for the full source + docs + binaries +
> database dump. Iteration 103.

## 1. Purpose

Single navigation surface for the digital-media deliverable required
by TOR §7.1 DEL 007. Defines what is written to the encrypted DVD or
USB medium, how it is encrypted, retention, and chain of custody.

## 2. Audience

CNAS contracting authority, CNAS DevOps, CNAS security officer,
supplier release manager, supplier security officer.

## 3. Medium contents

| Section | Contents | Source |
|---|---|---|
| `/source/` | Full source tree at the delivered git tag | Repo `src/`, `tests/`, `perf/`, build configs |
| `/docs/` | Full `docs/` directory at the delivered tag | Repo `docs/` |
| `/binaries/` | Published self-contained artefacts | `dotnet publish` outputs per target framework |
| `/db/` | Sanitized database schema dump + reference data | EF Core migrations + reference seed |
| `/secrets-template/` | Empty templates (no real secrets) | Configuration schema, never values |
| `/manifest.json` | SHA-256 checksums per file, git tag, build date, builder identity | Generated at packaging |
| `/README.md` | Mount instructions, decryption flow, contact roster | Authored at packaging |

## 4. Encryption and integrity

- Medium encrypted at rest with AES-256 (LUKS for USB, password-
  protected ISO for DVD).
- Decryption key handed over via an out-of-band channel (sealed
  envelope to CNAS contracting authority).
- Per-file SHA-256 checksums in `manifest.json`; manifest itself
  signed with the supplier release key.
- No real production secrets, certificates, or live database dumps —
  only sanitized schemas + reference data.

## 5. Chain of custody and retention

- Two identical media produced; one held by CNAS contracting
  authority, one by CNAS security officer.
- Retention: ≥ 1 year from contract end (per R2507 / PIR 041-043).
- Destruction at end of retention: certificate of destruction filed
  in audit archive.
- Verification: CNAS DevOps reproduces a clean build from `/source/`
  and confirms the resulting binaries match `/binaries/` checksums.

## 6. Acceptance criteria

- Two encrypted media produced and labelled with git tag + date.
- Decryption succeeds; checksums verify; sample build green.
- `dotnet build Cnas.Ps.slnx -p:TreatWarningsAsErrors=true` succeeds
  from the medium's `/source/` tree.
- Sign-off entered in the Acceptance Protocol row "DEL 007 / R2606".

## 7. Status / open gaps

- Packaging script and manifest generator: not yet committed
  (operational tooling produced at release-time).
- Out-of-band key delivery channel: pending CNAS security officer
  procedure.
- Medium type (DVD vs USB): bilateral preference, not yet locked.

## 8. References

- TOR §7.1 DEL 007
- TODO.md R2606 (this row), R2445, R2507
- [`../handover/source-code-handover.md`](../handover/source-code-handover.md)
- [`../handover/contract-end-procedures.md`](../handover/contract-end-procedures.md)
