# Screenshot Automation

- **Status:** Doing (Phases 1 to 4 complete: all 78 images in junctional.io's asset folder are in the manifest and capture at their published size, with `-PrepareData` proven unattended; distribution outstanding)
- **Created:** 2026-10-11
- **Author:** JayVDZ
- **Issue:** [#582](https://github.com/TetronIO/JIM/issues/582)

## Problem Statement

Every JIM release makes the published screenshots stale. The junctional.io product tour shows 22 of them, from a library of 78 kept per release under `junctional.io/src/assets/tour/<version>/`, and this repository's README shows one more. All of them are captured by hand, which takes hours per release and shows:

- **Sizes drift between releases.** v0.10.0 was captured at 3588×2242, the v0.10.2 homepage at 3840×2400, v0.10.3 at 3348×2092, and the v0.10.3 API reference shot is still 3588×2242. Each is whatever browser window happened to be open.
- **Zoom drifts.** v0.10.3 was taken at 90% browser zoom because the portal felt too large at the time; the portal has since been resized, so 90% now understates it.
- **The portal is not shown as a customer sees it.** Captures come from a development stack (`ASPNETCORE_ENVIRONMENT=Development`, which changes the Settings page, the Operations queue, the user menu and an error page), and anything not running shows its warning banner.
- **Terminal shots are edited by hand.** The PowerShell images are macOS window captures with the username blurred in an image editor.
- **The README's hero image is a content-hashed URL** (`https://junctional.io/_astro/homepage-dark.C__Eo5nO.png`), which stops resolving the first time the site is rebuilt with a new image.

Because a refresh is this expensive, screenshots fall behind the product, which undermines the material most prospective customers see first.

## Goals

- One command produces the full screenshot library for a release, named and sized as drop-in replacements for `junctional.io/src/assets/tour/<version>/`.
- Every image matches the published geometry exactly: portal 3348×2092 PNG (1674×1046 CSS pixels at 2× density, 100% zoom), API reference 3588×2242 (1794×1121 at 2×), terminal 1944×1222 PNG with transparency. The run fails if any image differs.
- The portal is captured as a customer sees it: Production mode, every service running, navigation groups expanded, in both themes where the image has light and dark variants.
- The same data every run, so a refresh changes only what the release changed.
- Refreshing all screenshots for a release is one command plus a review of the output.

## Non-Goals

- **Annotations and callouts** (arrows, highlights, numbered markers). Revisit once the library is automated.
- **Change detection** that suppresses images which only moved by a pixel. Revisit after the first automated release shows how noisy a refresh is.
- **Screenshots in the docs site.** `docs/CLAUDE.md` prefers prose and diagrams to screenshots by design; this PRD does not change that.
- **A purpose-built demo dataset.** Integration Scenario 8 is the data source (see Data). The capture engine does not depend on it, so a dedicated dataset can replace it later.

## User Stories

1. As the person releasing JIM, I want to refresh every published screenshot with one command, so that the website never shows a previous release.
2. As the person releasing JIM, I want each refreshed image to be a drop-in replacement at the same size and sharpness, so that the site's image pipeline and layout need no changes.
3. As a prospective customer reading the website, I want the screenshots to show the product as it ships, so that what I evaluate is what I would deploy.
4. As a developer changing the portal, I want to capture the affected screenshots locally, so that I can see how a change reads in marketing material before it ships.

## Requirements

### Functional Requirements

1. A declarative manifest (`scripts/screenshots/manifest.json`) lists every screenshot: its name, kind (`portal`, `api-reference`, `terminal`), the steps that reach it, which themes it is captured in, its geometry profile, and where it is published.
2. `scripts/Capture-Screenshots.ps1` signs in through the bundled Keycloak realm, captures every manifest entry (or those named by `-Name`), and writes `<OutputPath>/<Version>/<name>-<theme>.png` (`<name>.png` for single-theme images). The default output path is `artifacts/screenshots`, which is gitignored.
3. The manifest is validated before the browser starts: unknown kinds, profiles or themes, duplicate names, and steps of an unknown shape are rejected with the entry named. So is sign-in: when nothing answers at the address the portal sends browsers to (on the development stack, the Keycloak bridge on port 8181, which the integration runner starts only where `socat` is installed), the run stops and names it, rather than failing every image with the same refused connection.
4. After capture, every image's pixel dimensions are checked against its profile; any mismatch fails the run and names the image.
5. Each capture waits until the page is ready: network idle, web fonts loaded, no progress indicator or Blazor reconnect overlay showing, plus any element the entry names. Animations and the text caret are disabled and the pointer is parked where it hovers nothing.
6. Theme and navigation state are set the way a user would leave them, through the portal's own preferences (`jim_darkMode`, `jim_navGroup_*` in browser storage), before the first page loads.
7. Data preparation (`-PrepareData`) runs integration Scenario 8 through `Run-IntegrationTests.ps1` against OpenLDAP at a chosen template (default `Scale100k50Groups`, matching the published images), adds the Scenario 8 Schedules, then rebuilds the JIM services at the release version (without the runner's development suffix, which the footer and `Connect-JIM` would show) and recreates them in Production mode on the same database.
8. Terminal screenshots run real PowerShell commands against the live stack inside a 120×30 pseudo-terminal, so the output, its colours and its Format-List styling are exactly what a terminal shows. The typed command is highlighted from PowerShell's own tokeniser with PSReadLine's default colours. The session is drawn in a recreated macOS-style terminal window (IBM Plex Mono, which JIM already self-hosts; the palette, window box and shadow are measured from the published images) and captured with a transparent background. Box-drawing and block characters are drawn as vectors, one per cell, so the banner tiles whatever the font covers. A command may be marked as set-up (for example signing in), which runs but does not appear. Like a real terminal, a session longer than 30 rows shows its last 30. The session signs in with an API key read from a file (by default the one the integration runner leaves), never from a command line, so the image shows only `Connect-JIM`'s masked form of it; dates and numbers are written as en-GB, whatever the recording host's locale.
9. The run ends with a summary: images written, by kind and theme, and elapsed time.

### Non-Functional Requirements

- **No new dependencies.** Browser automation reuses the Playwright the dev container already installs, through `scripts/JimPlaywright.ps1`, as the docs checks do.
- **Air-gap compatible.** Nothing is fetched at capture time beyond the JIM stack itself.
- **No secrets in the repository.** The default sign-in is the bundled development realm's documented account; any other is passed as a `PSCredential`.
- **Scale:** the `Scale100k50Groups` template needs a host with 20+ GB of RAM, so release captures run in the dev container or on a self-hosted runner, not in a 15 GB cloud sandbox (which can use a smaller template for development).

## Examples and Scenarios

### Scenario 1: Refreshing the library for a release

**Given**: a release candidate built in the dev container
**When**: `./scripts/Capture-Screenshots.ps1 -Version v0.17.0 -PrepareData`
**Then**: Scenario 8 runs at `Scale100k50Groups`, the JIM services restart in Production mode, every manifest entry is captured in each of its themes, every image is 3348×2092 (or its profile's size), and `artifacts/screenshots/v0.17.0/` holds the library under the filenames junctional.io expects.

### Scenario 2: Iterating on one page

**Given**: a running stack that already holds the Scenario 8 data
**When**: `./scripts/Capture-Screenshots.ps1 -Version dev -Name connected-systems-list`
**Then**: only `connected-systems-list-dark.png` and `connected-systems-list-light.png` are written, in seconds.

### Scenario 3: A capture comes out the wrong size

**Given**: a manifest entry whose profile is `portal`
**When**: the page renders at a different size (for example a full-page capture of a tall page)
**Then**: the run fails, naming the image, its actual size and the expected 3348×2092; nothing is reported as complete.

### Manifest entry

```json
{
  "name": "connected-systems-detail-schema",
  "kind": "portal",
  "profile": "portal",
  "themes": ["dark", "light"],
  "usedBy": ["junctional.io tour: Schema discovery"],
  "steps": [
    { "goto": "/admin/connected-systems" },
    { "click": { "text": "Yellowstone APAC" } },
    { "click": { "role": "tab", "name": "Schema" } },
    { "waitFor": { "text": "inetOrgPerson" } }
  ]
}
```

## Data

The published images were captured after integration Scenario 8 (Cross-Domain Entitlement Sync) at `Scale100k50Groups` on an `inetOrgPerson` directory: 100,001 users, 50 groups, Connected Systems "Yellowstone APAC" and "Glitterband EMEA" with 100,050 objects each, five Synchronisation Rules, and the Scenario 8 Schedules. Replaying it reproduces that content with tooling the project already maintains and tests. The cost is that the test-style names stay (for example "Alice Smith (S8-0)"), and the screenshots follow Scenario 8 as it changes for testing reasons; that coupling is accepted for now and is the reason the engine keeps data preparation separate from capture.

The integration runner resets the environment at the start of a run and leaves it running at the end, so the sequence is: run the scenario, add the Schedules, then recreate `jim.web`, `jim.worker` and `jim.scheduler` with `ASPNETCORE_ENVIRONMENT=Production`. The database volume survives the recreate. Production mode signs in over `http://localhost` in Chromium, which treats localhost as a secure context.

## Distribution

| Destination | Mechanism | Phase |
|---|---|---|
| junctional.io | A PR adding `src/assets/tour/<version>/` and setting `tour.imageVersion` in both `src/i18n/en-GB.ts` and `src/i18n/en-US.ts`. The site's Astro pipeline makes the AVIF/WebP variants, so only the PNG sources are needed. | 5 |
| JIM README | Point the hero image at a stable URL instead of the content-hashed `/_astro/` path. | 5 |
| tetron.io | To be scoped: find where its screenshots live and how it deploys. | 5 |
| docs.junctional.io | Not in scope (see Non-Goals). | n/a |

## Implementation Phases

1. **Capture engine and data-independent entries** ✅. Manifest, schema validation, the PowerShell entry point, the Playwright driver, geometry profiles, sign-in, theme and navigation state, readiness waits and size checks. Entries that need no particular data: Home, the main lists, the API reference.
2. **Scenario 8 data and Production mode** ✅. `-PrepareData`, then the remaining tour entries.
3. **Terminal screenshots** ✅. The HTML terminal window, built after the mock-ups were approved: `powershell-1` (signing in) to `powershell-3` (a session longer than the terminal).
4. **The rest of the library** ✅. The 54 images in junctional.io's asset folder that are not on a tour page. Reaching a particular object (a user, a group, the first Full Sync) uses a `fill` step to type into a page's search box, or a list's own URL state (`?q=`, `desc=0`), never an id that changes from run to run.
5. **Distribution.** junctional.io PR, README stable URL, tetron.io.
6. **Release workflow (later).** A workflow on a self-hosted runner with enough memory, run on demand or on release tag.

## Affected Areas

| Area | Impact |
|------|--------|
| `scripts/` | New `Capture-Screenshots.ps1`, `scripts/screenshots/` (manifest, Playwright driver, terminal template), Pester tests |
| Integration harness | Reused unchanged by `-PrepareData` |
| Product code | None. Bugs the captures expose are fixed in their own PRs (the first: future "Next Run" times read "just now") |

## Documentation Impact

| Doc | Change |
|------|--------|
| `engineering/SANDBOX_RUNTIME_VERIFICATION.md` | Capture at 2× density and 1674×1046 instead of 1440×900 at 1×, so verification screenshots are legible |
| `engineering/RELEASE_PROCESS.md` | Add the screenshot refresh to the release steps (Phase 5) |
| `.claude/skills/release/SKILL.md` | Replace "screenshots are out of scope; request them from the user" with the capture command (Phase 5) |

## Acceptance Criteria

- [x] `Capture-Screenshots.ps1` captures every tour entry in both themes at exactly the published geometry, and fails on any mismatch.
- [x] The portal is captured in Production mode, with every service running and navigation groups expanded.
- [x] `-PrepareData` reproduces the Scenario 8 dataset unattended.
- [x] The three terminal images are generated from a live session.
- [ ] One end-to-end flow is proven: manifest entry, capture, published to junctional.io.
- [ ] Refreshing every screenshot for a release is one command plus a review.
