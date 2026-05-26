# Cnas.Ps.E2E.Tests

End-to-end tests for the CNAS PS API and (later) Blazor host. Boots the API in-process on
a real Kestrel port and drives it with [Microsoft.Playwright](https://playwright.dev/dotnet/).

## Running

The first run downloads ~150 MB of Chromium binaries. Subsequent runs reuse the cache.

```bash
dotnet build Cnas.Ps.slnx
dotnet test tests/Cnas.Ps.E2E.Tests/Cnas.Ps.E2E.Tests.csproj
```

If the browser binaries are already provisioned (CI image, dev box that ran Playwright once
already), skip the install step:

```bash
PLAYWRIGHT_SKIP_INSTALL=1 dotnet test tests/Cnas.Ps.E2E.Tests/Cnas.Ps.E2E.Tests.csproj
```

To install the browser manually (equivalent to what `PlaywrightFixture` does on first run):

```bash
pwsh tests/Cnas.Ps.E2E.Tests/bin/Debug/net10.0/playwright.ps1 install --with-deps chromium
```

## Layout

| File | Role |
| --- | --- |
| `Cnas.Ps.E2E.Tests.csproj` | Project file — references Microsoft.Playwright, xUnit, FluentAssertions, EF Core InMemory, Cnas.Ps.Api. |
| `PlaywrightFixture.cs` | xUnit collection fixture: installs Chromium, launches a shared headless browser. |
| `ApiHostFixture.cs` | xUnit collection fixture: boots Cnas.Ps.Api on `http://127.0.0.1:0` with an InMemory DB. |
| `CollectionDefinitions.cs` | xUnit `[CollectionDefinition]` wiring the two fixtures together. |
| `Journeys/HealthCheckJourneyTests.cs` | `/health/live` and `/health/ready` smoke. |
| `Journeys/OpenApiDocumentJourneyTests.cs` | `/openapi/v1.json` schema document smoke. |
| `Journeys/StaffLoginPageJourneyTests.cs` | Skipped placeholder for the future MPass UI journey. |
