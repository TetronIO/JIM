# Runtime Verification in Cloud Sandboxes

> How agents (and humans) run JIM inside a Claude Code on the web sandbox to visually verify web changes and execute PowerShell module changes, instead of relying on unit tests alone.

- **Status:** Done
- **Applies to:** Claude Code on the web (remote/cloud sandbox) sessions. Local devcontainers already have the full `jim-*` alias tooling.

## What the sandbox provides

The SessionStart hook (`.claude/hooks/session-start.sh`, registered in `.claude/settings.json`) prepares every remote session automatically:

| Provision | Detail |
|---|---|
| .NET 10 SDK | `~/.dotnet` (exported on `PATH` via the session env file) |
| PowerShell | `pwsh` from the Microsoft apt repository |
| Docker daemon | Started per session; image layers cache in the container state |
| `.env` | Created from `.env.example` with dev credentials |
| Warm caches | Database/Keycloak images pre-pulled, NuGet packages restored |

Sandbox capacity (typical): 4 vCPUs, 15 GiB RAM, ~30 GiB free disk. This comfortably runs the light stack below. It is **not** sized for the parallel all-scenarios integration suite; single integration scenarios are borderline and should be attempted only when the task demands it.

## Starting the stack

```powershell
pwsh ./scripts/Start-SandboxStack.ps1            # start
pwsh ./scripts/Start-SandboxStack.ps1 -Down      # stop
```

This is the sandbox equivalent of `jim-build-light`: database and Keycloak as containers, JIM.Web and JIM.Worker natively (the Worker applies migrations to a fresh database), and a `localhost:8181` bridge to Keycloak so the bundled realm's browser-facing URLs resolve. First start takes a few minutes (migration + seeding); the script waits until `/api/v1/health/ready` returns 200.

- Web UI: `http://localhost:5200` - dev realm users `admin`/`admin` and `user`/`user`
- Keycloak admin console: `http://localhost:8181` - `admin`/`admin`

**Do not use `jim-build` (Docker image builds) in sandboxes.** The egress proxy intercepts TLS, so `dotnet restore` inside a Docker build stage fails certificate verification. Native builds restore through the proxy without issue, which is why the light topology is canonical here.

## Visually verifying web changes

Chromium is pre-installed at `/opt/pw-browsers/chromium` with Playwright available globally (`NODE_PATH=/opt/node22/lib/node_modules`). The Playwright MCP browser may be configured for a Chrome channel that is absent in the sandbox; scripting Playwright directly is the reliable pattern:

```javascript
// verify.js - run with: NODE_PATH=/opt/node22/lib/node_modules node verify.js
const { chromium } = require('playwright');
(async () => {
  const browser = await chromium.launch({ executablePath: '/opt/pw-browsers/chromium', headless: true });
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  await page.goto('http://localhost:5200', { waitUntil: 'networkidle' });
  if (page.url().includes('8181')) {                       // Keycloak sign-in
    await page.fill('#username', 'admin');
    await page.fill('#password', 'admin');
    await Promise.all([
      page.waitForURL('**localhost:5200**', { timeout: 30000 }),
      page.click('#kc-login'),
    ]);
    await page.waitForLoadState('networkidle');
  }
  await page.goto('http://localhost:5200/<page-under-test>', { waitUntil: 'networkidle' });
  await page.screenshot({ path: 'verify.png', fullPage: true });
  await browser.close();
})();
```

Verification protocol for UI changes:

1. Drive the actual flow the change affects (not just the page load): click, fill, submit.
2. Screenshot the before/after states and inspect them yourself - render bugs, clipping, empty states.
3. Check the browser console and `web.log` for errors introduced by the change.
4. Attach the screenshots to your report (SendUserFile) so the reviewer sees what you saw.

## Verifying PowerShell module changes

`Connect-JIM` supports non-interactive authentication with an API key. The dev realm's OIDC clients are interactive-only (no direct access grants), so mint the first API key through the web UI with Playwright: sign in as `admin`, go to the API Keys admin page, create a key with the scopes the cmdlet under test needs, and capture the generated value from the page.

```powershell
Import-Module ./src/JIM.PowerShell/JIM.psd1 -Force
Connect-JIM -Url 'http://localhost:5200' -ApiKey $env:JIM_API_KEY
Get-JIMMetaverseObjectType          # or whichever cmdlets the change touches
```

Exercise the changed cmdlets against the live instance, not just their Pester mocks: real serialisation, pagination, and error shapes only show up here. Pester remains the regression net (`pwsh -Command "Invoke-Pester src/JIM.PowerShell/Tests"`); this step is the end-to-end confirmation.

## Cleanup and troubleshooting

- `pwsh ./scripts/Start-SandboxStack.ps1 -Down` stops everything; sandboxes are ephemeral so leftover state costs nothing after the session ends.
- Service logs land in the temp directory printed by the start script (`web.log`, `worker.log`, `bridge.log`).
- Docker daemon issues: see `/tmp/dockerd.log`; the SessionStart hook starts it, and it must be re-started in each new session.
- Web stuck at "Database has pending migrations": the Worker is not running or failed; check `worker.log`.
