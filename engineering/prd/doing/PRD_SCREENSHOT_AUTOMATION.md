# Screenshot Automation

- **Status:** Doing (all phases built: the release's pre-release run captures and checks the library and opens pull requests on junctional.io and tetron.io, which the release merges when it is published, and the README reads junctional.io's fixed address; junctional.io's first publish was TetronIO/junctional.io#17, merged by hand, and the first unattended publish to both sites is at the next release)
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
3. The manifest is validated before the browser starts: unknown kinds, profiles or themes, duplicate names, and steps of an unknown shape are rejected with the entry named. So is sign-in: when nothing answers at the address the portal sends browsers to (on the development stack, Keycloak at `localhost:8181`, which the devcontainer forwards and the integration runner bridges only where `socat` is installed), the run stops and names it, rather than failing every image with the same refused connection. `-PrepareData` publishes that address itself, on the loopback interface only, where nothing else does.
4. After capture, every image's pixel dimensions are checked against its profile; any mismatch fails the run and names the image.
5. Each capture waits until the page is ready: network idle, web fonts loaded, no progress indicator or Blazor reconnect overlay showing, plus any element the entry names. Animations and the text caret are disabled and the pointer is parked where it hovers nothing.
6. Theme and navigation state are set the way a user would leave them, through the portal's own preferences (`jim_darkMode`, `jim_navGroup_*` in browser storage), before the first page loads.
7. Data preparation (`-PrepareData`) runs integration Scenario 8 through `Run-IntegrationTests.ps1` against OpenLDAP at a chosen template (default `Scale100k50Groups`, matching the published images), adds the Scenario 8 Schedules, then rebuilds the JIM services at the release version (without the runner's development suffix, which the footer and `Connect-JIM` would show) and recreates them in Production mode on the same database.
8. Terminal screenshots run real PowerShell commands against the live stack inside a 120×30 pseudo-terminal, so the output, its colours and its Format-List styling are exactly what a terminal shows. The typed command is highlighted from PowerShell's own tokeniser with PSReadLine's default colours. The session is drawn in a recreated macOS-style terminal window (IBM Plex Mono, which JIM already self-hosts; the palette, window box and shadow are measured from the published images) and captured with a transparent background. Box-drawing and block characters are drawn as vectors, one per cell, so the banner tiles whatever the font covers. A command may be marked as set-up (for example signing in), which runs but does not appear. Like a real terminal, a session longer than 30 rows shows its last 30. The session signs in with an API key read from a file (by default the one the integration runner leaves), never from a command line, so the image shows only `Connect-JIM`'s masked form of it; dates and numbers are written as en-GB, whatever the recording host's locale.
9. The run ends with a summary: images written, by kind and theme, and elapsed time.
10. Publishing (`scripts/screenshots/Publish-TourScreenshots.ps1`) updates a junctional.io checkout and opens a pull request with it. Only the images the tour's steps name are published, read from the site's own dictionaries, which must name identical images; the homepage images are also written to `public/assets/screenshots/`, whose addresses never change; folders no step uses are removed. Everything is checked before anything changes: a missing image or disagreeing dictionaries stop the run with the site untouched. The commit is made through GitHub's API with the `jim-automation` app's token, so GitHub signs it. One branch and one pull request per version: publishing again moves the branch and updates the open pull request.
11. Publishing needs no one. Before anything is published the capture refuses an image that is blank (its luminance barely varies) or a dark and light pair in the same theme (the light image not clearly brighter), and junctional.io is built with the new images. The pull requests merge when JIM's release is published, never before, once GitHub reports their checks passed: `release.yml` merges them after creating the release, and the screenshots job merges its own if the release was published while it was still capturing. A merge that fails turns the release run's screenshots job red, naming why; the release is already published, so it holds nothing back. Closing a pull request keeps its images off the site. Alt text is not generated; the `/release` checklist reminds the releaser to check it where an image changed.

### Non-Functional Requirements

- **No new dependencies.** Browser automation reuses the Playwright the dev container already installs, through `scripts/JimPlaywright.ps1`, as the docs checks do. The release job installs the same pinned `@playwright/mcp` version (`.devcontainer/setup.sh`, watched by `tooling-pin-check`), and every action it uses is already pinned elsewhere in the repository.
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
| junctional.io | A PR adding `src/assets/tour/<version>/` with the images the tour shows (22 of the 78; the whole library stays in the run's artefact, because every image committed to the site stays in its history for good) and setting `tour.imageVersion` in both `src/i18n/en-GB.ts` and `src/i18n/en-US.ts`. The site's Astro pipeline makes the AVIF/WebP variants, so only the PNG sources are needed. Opened by the release's pre-release run after the site builds with it, merged automatically when the release is published. | 5, 7 ✅ |
| JIM README | Point the hero image at `https://junctional.io/assets/screenshots/homepage-dark.png`, which every publish writes, instead of the content-hashed `/_astro/` path, which stops resolving at the first publish. Light and dark, as the README's diagrams are. | 5 ✅ |
| tetron.io | A PR replacing `assets/images/homepage-dark.png`, the homepage hero (the same capture as junctional.io's homepage image; Hugo's `resized-figure` shortcode makes the WebP). The publisher first checks a page still names the image, so a redesign stops the publish rather than having each release commit a file nothing shows. Opened by the same job with its own token and steps, merged automatically when the release is published, once the PR's preview deployment has built. | 5, 7 ✅ |
| docs.junctional.io | Not in scope (see Non-Goals). | n/a |

## Implementation Phases

1. **Capture engine and data-independent entries** ✅. Manifest, schema validation, the PowerShell entry point, the Playwright driver, geometry profiles, sign-in, theme and navigation state, readiness waits and size checks. Entries that need no particular data: Home, the main lists, the API reference.
2. **Scenario 8 data and Production mode** ✅. `-PrepareData`, then the remaining tour entries.
3. **Terminal screenshots** ✅. The HTML terminal window, built after the mock-ups were approved: `powershell-1` (signing in) to `powershell-3` (a session longer than the terminal).
4. **The rest of the library** ✅. The 54 images in junctional.io's asset folder that are not on a tour page. Reaching a particular object (a user, a group, the first Full Sync) uses a `fill` step to type into a page's search box, or a list's own URL state (`?q=`, `desc=0`), never an id that changes from run to run.
5. **Distribution** ✅. The junctional.io pull request; the README reading the fixed address (the address added by TetronIO/junctional.io#16); the tetron.io pull request (`Publish-TetronScreenshots.ps1`).
6. **Release workflow** ✅. The `jim-screenshots` job in `pre-release.yml`, after a passing suite, on the same runner and commit. A job there rather than a workflow of its own, because the `jim-prerelease` runner group admits that one file; it cannot hold up a release, since the gate reads the status the suite has already posted.
7. **Unattended publishing** ✅. The blank and same-theme checks (`ScreenshotChecks.ps1`), junctional.io's build before committing, and the merge when the release is published: `release.yml`'s `publish-screenshots` job (`Merge-ReleaseScreenshots.ps1`), or the screenshots job when the release came first (`-MergeWhenReleased`).

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
| `engineering/RELEASE_PROCESS.md` | Product Tour Screenshots: when the job runs, what it refuses, where the images go, when the sites change, the runner's one-time setup (Phases 6 and 7) ✅ |
| `.claude/skills/release/SKILL.md` | Post-release: confirm the sites show the new screenshots, and check alt text where an image changed; the site prompt leaves the tour's images to the job (Phases 6 and 7) ✅ |

## Acceptance Criteria

- [x] `Capture-Screenshots.ps1` captures every tour entry in both themes at exactly the published geometry, and fails on any mismatch.
- [x] The portal is captured in Production mode, with every service running and navigation groups expanded.
- [x] `-PrepareData` reproduces the Scenario 8 dataset unattended.
- [x] The three terminal images are generated from a live session.
- [x] One end-to-end flow is proven: manifest entry, capture, published to junctional.io (TetronIO/junctional.io#17, from the v0.16.0 release's run).
- [ ] A release refreshes every published screenshot with no one taking a step: captured, checked and merged on junctional.io and tetron.io when it is published.
