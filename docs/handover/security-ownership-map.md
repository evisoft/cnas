# Security ownership map

Date: 2026-05-25

## Verification result

The `security-ownership-map` workflow could not compute git-history ownership in
this workspace because the checked-out tree does not include a `.git` directory.
The analyzer requires local git history to calculate bus factor, hidden owners,
or stale sensitive ownership.

Command attempted:

```powershell
python C:\Users\evisoft\.codex\skills\security-ownership-map\scripts\run_ownership_map.py `
  --repo . `
  --out C:\tmp\cnas-ownership-map `
  --since "12 months ago" `
  --emit-commits
```

Observed failure:

```text
fatal: not a git repository (or any of the parent directories): .git
```

## Repository fix applied

The repository now includes `.github/CODEOWNERS` with explicit two-owner coverage
for authentication, authorization, callback, SAML, audit, crypto/security,
dependency, CI, and deployment paths.

`SecurityOwnershipTests` in the architecture test suite enforces that sensitive
paths remain covered by at least two owner tokens.

## Rerun requirement after handover

After the full-history remote is available, rerun the ownership map against the
real clone and compare the output against `.github/CODEOWNERS`:

```powershell
python C:\Users\evisoft\.codex\skills\security-ownership-map\scripts\run_ownership_map.py `
  --repo . `
  --out ownership-map-out `
  --since "12 months ago" `
  --emit-commits
```

Review these sections first:

```powershell
python C:\Users\evisoft\.codex\skills\security-ownership-map\scripts\query_ownership.py `
  --data-dir ownership-map-out `
  summary `
  --section orphaned_sensitive_code

python C:\Users\evisoft\.codex\skills\security-ownership-map\scripts\query_ownership.py `
  --data-dir ownership-map-out `
  summary `
  --section bus_factor_hotspots

python C:\Users\evisoft\.codex\skills\security-ownership-map\scripts\query_ownership.py `
  --data-dir ownership-map-out `
  summary `
  --section hidden_owners
```
