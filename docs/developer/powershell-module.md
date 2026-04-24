---
title: PowerShell Module
---

# PowerShell Module

Developer guidance for loading, iterating on, and testing the JIM PowerShell module from source in the devcontainer.

!!! note "Looking for end-user guidance?"
    For installation, authentication, and cmdlet reference material aimed at administrators using the module against a deployed JIM instance, see [PowerShell Module](../powershell/index.md) under the PowerShell Module section.

The module lives at `src/JIM.PowerShell/` and is a plain script module: no compilation, no packaging step. During development you import it directly from the source tree and iterate in place.

## Loading the module from source

From the repository root:

```powershell
pwsh
Import-Module ./src/JIM.PowerShell/JIM.psd1
```

After editing a cmdlet, re-import with `-Force` to pick up the change:

```powershell
Import-Module ./src/JIM.PowerShell/JIM.psd1 -Force
```

If `-Force` is not enough (for example, after renaming a function or changing the manifest), remove and re-import:

```powershell
Remove-Module JIM
Import-Module ./src/JIM.PowerShell/JIM.psd1
```

## Connecting to local JIM

Start the Docker stack first:

```bash
jim-stack
```

Then from a PowerShell session, connect with an API key. The devcontainer ships with an infrastructure API key generated in `.env` under `JIM_INFRASTRUCTURE_API_KEY`; the simplest approach is to read it directly from `.env`:

```powershell
$apiKey = (Get-Content .env | Where-Object { $_ -match '^JIM_INFRASTRUCTURE_API_KEY=' }) -replace '^JIM_INFRASTRUCTURE_API_KEY=', ''
Connect-JIM -Url "http://localhost:5200" -ApiKey $apiKey
```

You can also paste the value directly:

```powershell
Connect-JIM -Url "http://localhost:5200" -ApiKey "jim_ak_xxxxxxxxxxxx"
```

!!! note "The infrastructure key is the right one for dev"
    JIM provisions the infrastructure API key automatically when `JIM_INFRASTRUCTURE_API_KEY` is set in `.env`; it shows as **Infrastructure** in the JIM web UI under **Admin** > **API Keys**. It is intended exactly for this kind of local automation and scripting. You can create additional personal keys via the UI or `New-JIMApiKey` once you are connected, but for day-to-day cmdlet development the infrastructure key is enough.

    `JIM_INFRASTRUCTURE_API_KEY` is consumed by docker-compose at stack startup, not exported to your shell; that is why `Connect-JIM -ApiKey $env:JIM_INFRASTRUCTURE_API_KEY` does not work out of the box.

Verify with:

```powershell
Test-JIMConnection
```

!!! warning "Use API keys from inside the devcontainer"
    Interactive browser-based SSO (`Connect-JIM -Url "http://localhost:5200"` with no `-ApiKey`) **does not work** from a PowerShell session running inside the devcontainer.

    The browser-based flow needs the PowerShell session to (a) resolve `localhost:8181` to the bundled Keycloak container and (b) open a browser on the user's machine to complete sign-in. The devcontainer is a separate container with no access to the host's `localhost:8181` port binding and no display, so both prerequisites fail.

    The browser-based flow does work from a PowerShell session **on the host machine** (outside any container), because that session can reach the port-forwarded `http://localhost:8181` Keycloak endpoint and has a browser available. The devcontainer's bundled Keycloak realm includes a `jim-powershell` public client with loopback redirect URIs and `docker-compose.override.yml` sets `JIM_SSO_PUBLIC_AUTHORITY` and `JIM_SSO_PUBLIC_CLIENT_ID` so `/api/v1/auth/config` advertises values the host can use.

    For inside-the-devcontainer development, always use an API key.

## Module layout

| Path | Purpose |
|------|---------|
| `src/JIM.PowerShell/JIM.psd1` | Module manifest. Declares `FunctionsToExport`, `ModuleVersion`, minimum PowerShell version, and metadata |
| `src/JIM.PowerShell/JIM.psm1` | Module loader. Auto-discovers every `.ps1` under `Public/` and `Private/` and dot-sources them |
| `src/JIM.PowerShell/Public/` | Exported cmdlets, grouped into subfolders by area (`Connection/`, `Security/`, `Metaverse/`, etc.). Every `.ps1` here must have a matching entry in `FunctionsToExport` |
| `src/JIM.PowerShell/Private/` | Internal helpers. Auto-loaded but not exported |
| `src/JIM.PowerShell/Tests/` | Pester test suites. One `.Tests.ps1` file per cmdlet area |

Auto-discovery in `JIM.psm1` means you do not need to edit the `.psm1` when adding a cmdlet. Only the manifest (`JIM.psd1`) needs updating to export the new function.

## Adding a new cmdlet

1. Create the file under the appropriate area folder, using verb-noun naming:

    ```
    src/JIM.PowerShell/Public/Security/Add-JIMRoleMember.ps1
    ```

2. Include the copyright header, a complete comment-based help block (synopsis, description, parameters, examples, notes), and the function body. See an existing cmdlet such as [`Connect-JIM.ps1`](https://github.com/TetronIO/JIM/blob/main/src/JIM.PowerShell/Public/Connection/Connect-JIM.ps1) for the expected shape.
3. Add the function name to `FunctionsToExport` in `JIM.psd1`, under the correct grouping comment.
4. Create the matching Pester test in `src/JIM.PowerShell/Tests/<Area>.Tests.ps1`.
5. Reload the module (`Import-Module ./src/JIM.PowerShell/JIM.psd1 -Force`) and invoke the new cmdlet interactively to sanity-check.

!!! note "Test first"
    JIM follows TDD. Write the Pester test before the cmdlet implementation; it must fail before the function exists, then pass once you have written the minimum code to satisfy it. See [Testing](testing.md) for the full workflow.

## Running Pester tests

From the repository root:

```bash
jim-test-ps
```

This runs every `.Tests.ps1` file in `src/JIM.PowerShell/Tests/` through Pester with detailed output.

`jim-test-all` runs the .NET test suite and the Pester suite in sequence and produces a combined summary. Use it for the final pre-PR check when your change touches both sides.

To target a single test file, invoke Pester directly:

```bash
pwsh -NoProfile -Command "Import-Module Pester; Invoke-Pester -Path ./src/JIM.PowerShell/Tests/Security.Tests.ps1 -Output Detailed"
```

## Further Reading

- [PowerShell Module (end-user reference)](../powershell/index.md): installation, full cmdlet reference, scripting examples
- [Testing](testing.md): TDD workflow and .NET testing conventions
- [Development Environment](dev-environment.md): devcontainer setup, shell aliases, and development workflows
