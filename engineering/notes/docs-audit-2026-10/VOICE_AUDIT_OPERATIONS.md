# Public docs voice audit (B): administration, connectors, api, powershell, developer

Issue #1454. Reader: an identity or IT administrator, or an implementer, at a customer organisation. Audited against `docs/CLAUDE.md` (Diátaxis; Writing for the Reader) and the root `CLAUDE.md` Code Style rules. Read-only: no repository file was edited.

Scope: 49 customer-facing pages, read in full (about 135,000 words): `docs/administration/` (16 pages, SSO sub-pages included), `docs/connectors/` (7), `docs/api/` (3 markdown pages), and `docs/powershell/` (23). `docs/developer/` (18 pages) got a lighter audience-classification pass.

Line numbers refer to the files as they stood on 2026-10-11. Fit is scored 1 to 5: 5 means the page is clearly written for a customer implementer, 1 means it reads like internal engineering notes.

---

## 1. Headline findings

- **Mechanical rules are mostly met.** There are no em dashes, no "Sync Rule", and only one real American spelling (`administration/sso-setup.md` L213 "organization-level"). The mechanical problems that remain:
  - A literal `--` stands in for a dash 54 times across 14 pages. The site has no smartypants extension, so readers see two hyphens.
  - Domain nouns are lower-cased at scale (see pattern 4).
  - The Keycloak page over-corrects a third-party UI label to "Authorisation" (`sso-setup/keycloak.md` L165-166, L233-234). The on-screen label reads "Authorization".
- **The problem is substance, not mechanics.** On about two thirds of the pages, at least one passage explains how JIM is built rather than what the behaviour means to the reader. Release history, issue numbers and internal test or devcontainer infrastructure appear on about half the pages.
- **The best pages already show the voice.** `backup-recovery.md`, `jim-sql-connector.md` and its two Delta Import guides, `deploying-with-ansible.md`, `powershell/password-synchronisation.md` and `powershell/connection.md` meet nearly every rule in `docs/CLAUDE.md`. The house rules work when they are followed. The gap is coverage, not definition.
- **Hand-maintained examples have drifted on at least 15 pages.** These are factual defects, not voice issues, but they damage trust in the same way. Some examples cannot work as written: `Get-JIMSyncRule -All`, `Get-JIMConnectedSystem -All`, `Start-JIMRunProfile -RunProfileId 42` with no Connected System, and a connector called "CSV File". Some pages contradict each other: Kubernetes probes against "JIM does not support Kubernetes", and `C:\Data\import.csv` and `/data/imports` against `/connector-files`.
- **Troubleshooting has no synchronisation section.** `administration/troubleshooting.md` opens with "Documentation In Progress ... Content will be added in a future update." Its index card promises synchronisation troubleshooting, and none exists. Failed runs, export errors and Pending Export failures are JIM's core failure modes, and the page does not cover them.

---

## 2. Per-page summary table

Mode codes: H = how-to, R = reference, E = explanation, T = troubleshooting (a how-to sub-genre). The intended mode for every folder audited here is H, per `docs/CLAUDE.md`. That file concedes that PowerShell cmdlet pages are de facto reference.

| # | Page | Words | Actual mode | Mixed? | Fit | One-line verdict |
|---|------|------:|-------------|:------:|:---:|------------------|
| 1 | administration/index.md | 352 | Navigation | | 5 | Clear cards; "Working with Lists" is oddly placed first |
| 2 | administration/preview-features.md | 466 | E + H | | 4 | Good; mentions the issue tracker and the internal test harness |
| 3 | administration/security-headers.md | 458 | R + E | ✅ | 3 | Middleware pipeline, "stage one/two" roadmap, an internal "security assessment" |
| 4 | administration/security-audit-events.md | 794 | R + E + H | ✅ | 3 | "rows", "database writes", "write shape", design defence |
| 5 | administration/portal-lists.md | 1466 | E + R (UI) | ✅ | 4 | Release-note narration ("now", "any more", "moved rather than converted") |
| 6 | administration/backup-recovery.md | 1717 | H | | 5 | **Exemplar**: says what survives, verifies, gives a remedy |
| 7 | administration/configuration.md | 3280 | R + H | ✅ | 3 | Internal info box, devcontainer example, design-exploration theme names, duplicate settings table |
| 8 | administration/deployment.md | 6660 | H + R + E | ✅ | 4 | Strong but 6.6k words; worker internals in sizing; duplicates the PowerShell install |
| 9 | administration/podman.md | 2652 | H + E | | 5 | Sysadmin register, appropriate; one tangled sentence |
| 10 | administration/deploying-with-ansible.md | 950 | H | | 5 | **Exemplar** how-to shape |
| 11 | administration/upgrading.md | 3493 | H + E | | 4 | Good survival statements; version-specific notes pile up in the body |
| 12 | administration/troubleshooting.md | 3328 | T | | 3 | Good pattern; stale placeholder; no synchronisation section; history asides |
| 13 | administration/sso-setup.md | 2035 | E + H + T + guidance | ✅ | 3 | JWT middleware, devcontainer realm, "we recommend" |
| 14 | administration/sso-setup/microsoft-entra-id.md | 1113 | H | | 4 | Dev-only step; Docker-only `.env`; no restart; secret expiry ignored |
| 15 | administration/sso-setup/ad-fs.md | 1024 | H | | 4 | Troubleshooting bullets name a cause but no symptom or how-to |
| 16 | administration/sso-setup/keycloak.md | 838 | H | | 4 | Unexplained "service account client" step; over-corrected UI label |
| 17 | connectors/index.md | 605 | E + R | ✅ | 4 | Abstraction rationale written for developers; CSO/MVO in prose |
| 18 | connectors/jim-file-connector.md | 2831 | H + R + T | ✅ | 4 | "the customer's HR system"; RPEI jargon; wrong remedy for encoding |
| 19 | connectors/jim-ldap-connector.md | 14758 | R + H + E + T | ✅ | 3 | Exhaustive and expert but dense; design defence; lab narration; a 700-word bullet |
| 20 | connectors/jim-scim-connector.md | 3798 | R + E + H + T | ✅ | 4 | Explains "why" well; one failure with no remedy; contradicts itself on trust |
| 21 | connectors/jim-sql-connector.md | 6057 | H + R + E | | 5 | **Exemplar**: "What it means for you" column, Before you begin, steps, worked examples |
| 22 | connectors/jim-sql-connector-delta-import-change-log.md | 2156 | H + E | | 5 | **Exemplar** |
| 23 | connectors/jim-sql-connector-delta-import-watermark.md | 1396 | H + E | | 5 | **Exemplar** |
| 24 | api/index.md | 1181 | E + R (changelog) | ✅ | 3 | No workflows; a breaking-changes changelog; "the database cost tracks" |
| 25 | api/authentication.md | 423 | H | | 4 | Thin: no token acquisition, no 401/403 remedy, "review logs" with no pointer |
| 26 | api/rate-limiting.md | 780 | R + E | ✅ | 4 | Cache TTL internals; "integration tests"; duplicated settings table |
| 27 | powershell/index.md | 1033 | Orientation + H | | 4 | Incomplete air-gapped install; misplaced paragraph |
| 28 | powershell/system.md | 2197 | R | | 3 | Kubernetes probes vs "not supported"; Reset-JIMSystem says "singleton record", `BuiltIn = false` |
| 29 | powershell/connection.md | 1502 | R + task notes | | 5 | **Exemplar** among the cmdlet pages |
| 30 | powershell/connected-systems.md | 13483 | R | | 3 | Wrong noun ×10; structural accretion; examples that fail |
| 31 | powershell/run-profiles.md | 3116 | R | | 4 | "honest attribute diff ... content hash"; Windows paths; no survival statement on Remove |
| 32 | powershell/synchronisation-rules.md | 12240 | R | | 3 | "#242" ×5; "CRUD"; DTO name; DynamicExpresso; inverted headings |
| 33 | powershell/metaverse.md | 9732 | R | | 3 | "asserted null ... no row"; "cascade-removed"; "Identity" as a noun |
| 34 | powershell/password-synchronisation.md | 1760 | R + H | | 5 | **Exemplar**: opens with purpose, states what survives |
| 35 | powershell/predefined-searches.md | 1596 | R | | 4 | "essential for future expansion"; "client-side"; "slug" |
| 36 | powershell/schedules.md | 4352 | R + E | | 4 | "CRUD"; different table schema; missing `-ChangeReason`; no survival on Remove/Stop |
| 37 | powershell/activities.md | 2756 | R | | 3 | "now its own" in the opening; "response envelope ... unwraps it"; examples that fail |
| 38 | powershell/api-keys.md | 1218 | R | | 4 | Clean; no output tables; no rotation recipe |
| 39 | powershell/certificates.md | 1440 | R | | 4 | "may cause authentication failures" with no way to check; DER bytes saved as `.pem` |
| 40 | powershell/service-settings.md | 778 | R | | 4 | A "disable change tracking" example that never says what is lost |
| 41 | powershell/feature-flags.md | 739 | R | | 2 | Built around the integration harness; GitHub issue output field |
| 42 | powershell/security.md | 964 | R | | 4 | Titled "Security" but covers Roles; "Prior to this release" |
| 43 | powershell/history.md | 2197 | R | | 3 | A 330-word paragraph; a stale hand-kept list of cmdlets |
| 44 | powershell/previews.md | 4161 | R + E | ✅ | 3 | "entity ... surface"; output called "rows" ×10; internal fields exposed |
| 45 | powershell/example-data.md | 3653 | R + H | | 4 | Two names for one thing; **no warning against running it in production** |
| 46 | powershell/expressions.md | 355 | R | | 3 | "DynamicExpresso syntax" ×3 |
| 47 | powershell/worker-tasks.md | 535 | R | | 4 | Cancel-all example with no consequence stated |
| 48 | powershell/file-system.md | 273 | R | | 3 | "JIM Container"; example paths that contradict the File Connector page |
| 49 | powershell/logs.md | 644 | R | | 4 | Clean |

Mean fit is 3.8. Fifteen pages are mixed-mode.

---

## 3. Per-page detail

Each entry lists developer-speak (with line numbers), missing reader needs, voice breaches, mechanical breaches, and the worst sentence with a customer-facing rewrite.

### administration/

**index.md** (352w; navigation; fit 5)
- Developer-speak: none.
- Missing: nothing material. Card order puts "Working with Lists" (portal UI behaviour) before Deployment.
- Worst sentence: none material.

**preview-features.md** (466w; E+H; fit 4)
- Developer-speak:
  - L30 "Some feature flags exist purely for JIM's own development and its integration test harness"
  - L30 "JIM's release notes or issue tracker"
- Mechanical: L34-35 use `--` in See also instead of the house `<br />` form.
- Worst sentence (L30): "If you see a reference to an "in development" flag in JIM's release notes or issue tracker, it is not something you can or need to turn on yourself; it becomes a Preview feature, or ships outright, when it is ready."
  - Rewrite: "Release notes sometimes mention "in development" flags. You cannot turn these on, and you do not need to: each one becomes a Preview feature, or a standard part of JIM, when it is ready."

**security-headers.md** (458w; R+E; fit 3)
- Developer-speak:
  - L7 "Every response JIM.Web sends" (a project name)
  - L22 "If something earlier in the pipeline (a controller action, for example) has already set one of these headers"
  - L27-28 "currently in stage one ... compatible with Blazor Server and MudBlazor as they stand today"
  - L48 "a future stage-two release will move to a nonce-based policy" (roadmap)
  - L51 "plain HTTP (local development)"
  - L64 "the other REST API hardening control from the same security assessment" (an internal project the reader knows nothing about)
- Missing reader needs:
  - No opener saying who needs this page (a network security reviewer, a reverse-proxy owner).
  - No verification step, for example `curl -I`.
- Worst sentence (L22): "If something earlier in the pipeline (a controller action, for example) has already set one of these headers, JIM does not overwrite it; the more specific value wins."
  - Rewrite: delete it. The reader cannot act on JIM's internal pipeline. If the point is about proxies: "Your reverse proxy can add or replace these headers if your own policy requires different values."

**security-audit-events.md** (794w; R+E+H; fit 3)
- Developer-speak:
  - L7, L64 "from the same assessment"
  - L11 a column headed "Write shape"
  - L13 "One Activity per session establishment"
  - L22 "a naive one-row-per-failure design would let a key-spraying or credential-stuffing client force database writes at wire speed"
  - L24 "a handful of rows ... not 500 rows"
  - L34 "the pre-existing API key usage tracking"
  - L48 "Repeat-key the `targetType` query parameter", "a row you have already seen may reappear"
- Missing reader needs:
  - What to do when you see a spray of failures.
  - L30 cites "the (much longer) configuration change retention period" without giving the figure.
- Worst sentence (L22): "a naive one-row-per-failure design would let a key-spraying or credential-stuffing client force database writes at wire speed."
  - Rewrite: "JIM counts failed attempts rather than recording each one: one Activity per API key prefix, client address and reason in each 15-minute window, with a running total. A flood of attempts produces a handful of Activities, and you still see exactly how many attempts were made."

**portal-lists.md** (1466w; E/R of the UI, filed as a how-to; fit 4)
- Developer-speak:
  - L25 "rows currently rendered"
  - L68 "Every row of a scrolling table is one line tall, which is what lets the table place a row without having drawn the rows above it."
- History narration:
  - L51 "Nothing in the portal pages any more"
  - L64 "Two things moved rather than converted ... so they now open in a dialog"
  - L74 "now follow"
  - L93 "are paged, and are unchanged"
- In places the page reads like release notes for the move to virtual scrolling.
- Worst sentence (L64): "Two things moved rather than converted, because a scrolling table could not hold them. A Metaverse Attribute's contributors reorder by dragging, which needs a row that grows, so they now open in a dialog from the contributor count."
  - Rewrite: "To reorder a Metaverse Attribute's contributors, click its contributor count and drag them into order in the dialog that opens. A Schedule's run history opens from its **History** action and shows every run."

**backup-recovery.md** (1717w; H; fit 5; EXEMPLAR)
- What it does right:
  - L10 says what survives: "The rest of the data restores fine".
  - L159 is a verify step that names the failure and the remedy.
  - It ends with a checklist.
- Minor issues:
  - L23 names ASP.NET Core Data Protection (acceptable for a security reviewer).
  - L26 "This is the correct design" is defensive.
- Worst sentence (L140): "`--clean` removes only what the dump holds, so whatever was added since the backup stays: rolling back after an upgrade, what the newer release added stops parts of the backup restoring, and leaves a mixture of the two releases that the older JIM starts on without complaint."
  - Rewrite: "Do not use `pg_restore --clean`. It removes only what the dump contains, so anything a newer release added stays behind, and after a rollback the older JIM starts, without warning, on a database that is half one release and half the other."

**configuration.md** (3280w; R in a how-to folder, plus H; fit 3)
- Developer-speak:
  - L43 "the PostgreSQL image does not ship regional locales"
  - L68 "`docker-compose.production.yml` sets `ASPNETCORE_URLS` ... `ASPNETCORE_Kestrel__Certificates__Default__Path`"
  - L137 "e.g. a dev devcontainer where the JIM container reaches Keycloak via Docker DNS", with an example value of `http://localhost:8181/realms/jim`, plus "Backend token validation and JWKS lookups"
  - L169 "Service settings are stored in the database"
  - L185 "The most records of any one kind a single retention pass removes"
  - L223 exposes design-exploration theme names (`blended-nav`, `future-minimal`, `navy-o5`, `navy-o6`)
  - L258 "used by the parallel batch writer"
  - L261 an info box about the worker's command timeout that asks nothing of the reader
- Lower-case nouns: L157 "metaverse attribute ... User object type"; L171 "Service settings are the day-to-day operational knobs".
- Missing reader needs:
  - Nothing says which settings are required and which are optional.
  - The SSO table has no Default column.
  - The Service Settings table (L175-189) duplicates `configuration/service-settings.md` and `api/rate-limiting.md`, which breaches one-home-per-fact.
- Mechanical: `--` at L136, L212, L231, L261.
- Worst sentence (L261): "The worker uses a 300-second command timeout for bulk database operations (PostgreSQL default is 30 seconds). This accommodates large imports where individual batch writes may take longer than the default timeout. No configuration is required -- it is set automatically."
  - Rewrite: delete it, or reduce it to "Large imports can take up to five minutes for a single save. You do not need to configure this."

**deployment.md** (6660w; H+R+E; fit 4)
- Developer-speak:
  - L26 and L39 "the Worker holds every imported object ... until it has compared them all (to find duplicates, detect deleted objects, and link objects that refer to each other), and only then saves them"
  - L72 "the Scheduler queues tasks and the Worker polls them"
  - L667 "from their database heartbeats"
  - L671 "Each service writes a heartbeat file periodically"
- Product smell: L180 and L274 "(the template's `localhost` is for development)". The shipped template defaults to development values.
- Missing and duplicated:
  - L714-784 duplicates the PowerShell install from `powershell/index.md`, and the two versions differ.
  - The page is 6.6k words; TLS and reverse proxy (L386-656) is a page of its own.
- Strengths: L321 says what survives an interrupted install; L339 points the reader to the worker log.
- Mechanical: `--` ×12, for example L33-36 "10,000 -- 50,000"; L819 "File connector" is lower-cased.
- Worst sentence (L275): "For the bundled PostgreSQL on Docker's classic image store (`docker info` shows `Storage Driver: overlay2` rather than `overlayfs` with the containerd snapshotter), Docker drops the registry digest the compose file pins PostgreSQL by when it loads the image, so run the loaded image by its ID instead, after checking that ID is the one the bundle records:"
  - Rewrite: "If `docker info` shows `Storage Driver: overlay2`, Docker cannot find the bundled PostgreSQL image by the name the compose file uses. Run the commands below to point JIM at the image you just loaded; they first check that it is the image the bundle shipped."

**podman.md** (2652w; H+E; fit 5)
- The opener (L7-9) says what, why and who.
- Strengths: L60 says what not to do and why; L62 and L225 state the limits honestly.
- Developer-speak (justified by the symptoms): L102 "it opens `/dev/stdout` by name"; L131 "The .NET runtime signals its own threads".
- Worst sentence (L119): "NetworkManager sometimes takes the network interface (veth) Podman has just plugged one of a rootful JIM's pods in with".
  - Rewrite: "On RHEL, NetworkManager can take over the virtual network interface Podman creates for one of JIM's pods and start DHCP on it, which disconnects that pod from JIM's network."

**deploying-with-ansible.md** (950w; H; fit 5; EXEMPLAR)
- Shape: purpose opener, Before You Start, the playbook, After the Play.
- Worst sentence (L177): "The `restarts` keys need a recent version of the role."
  - Rewrite: name the minimum role version. "With an earlier version, restart JIM yourself after each change."

**upgrading.md** (3493w; H+E; fit 4)
- Strengths: L7, L29, L45-47 and L78 say what survives.
- History accumulating in a how-to:
  - L23 "Upgrading from v0.15 or earlier on Docker"
  - L138 and L199-205 "from v0.16.0 or earlier" (four paragraphs)
  - L73 "an installation made before it kept that record"
  - The examples use v0.13 and v0.14 while the notes discuss v0.16.
- Developer-speak:
  - L223 "on a converged instance"
  - L224 "`JIM.Application is not ready yet. Sleeping...`"
  - L227 "rewrites a large table"
  - L330 "against a newer schema"
- Voice: L41 "you will switch those back on"; L333 "rolls the connector space back ... the next run will re-evaluate".
- Missing: the verify step (L237) is Docker-only.
- Mechanical: `--` ×5.
- Worst sentence (L230): "Do not restore a backup unless the upgrade has left the schema in a state you cannot move forwards from." The reader cannot judge the state of the schema.
  - Rewrite: "Fix the cause the log names and restart the services to complete the upgrade. Roll back (below) only if JIM still cannot start on the new release after that, because a rollback discards everything since your backup."

**troubleshooting.md** (3328w; T; fit 3)
- Strengths: the "What it means / How to fix" pattern is good, and the page says what survives (L24, L57, L79, L85, L91, L137, L145, L182).
- Problems:
  - L7-10 is a stale placeholder, "Documentation In Progress ... Content will be added in a future update.", with an HTML TODO on a 3,300-word page.
  - There is no synchronisation section, although `administration/index.md` promises one.
- History narration:
  - L43 "Releases up to v0.16.0 do not read those settings"
  - L57 "which is all that earlier versions of Before You Install gave JIM's user"
  - L79 "JIM v0.16.0 gave `systemctl --user -M jim@` ... in this documentation"
  - L115 "installer of v0.16.0 or earlier"
  - L174 "(Earlier versions of JIM sent the browser back...)"; the heading "Sign-in loops..." still names the old behaviour
- Developer-speak: L225 "(Npgsql) probes for the GSS/Kerberos authentication library"; L229 "Why we do not install it ... we keep images minimal" (a "we" voice defending design).
- Density: L182 is a 230-word paragraph on correlation cookies.
- Worst sentence (L7-8): "This page is under active development. Content will be added in a future update."
  - Rewrite: delete it, and add a **Synchronisation** section (failed runs, export errors, objects that do not join). The L174 aside should also go, with the heading renamed "Sign-in stops on **Sign-in could not complete**".

**sso-setup.md** (2035w; E+H+T+guidance; fit 3)
- Developer-speak:
  - L56 "The JIM web application (Blazor UI) ... sent directly from the backend"
  - L57 "(PowerShell module, future CLI tools)"
  - L77 "the JWT bearer middleware that protects `/api/**` endpoints"
  - L176 "so the middleware can include it on sign-out"
  - L219 "The bundled devcontainer Keycloak realm ships with brute-force detection enabled"
- Voice: L219 "we recommend"; L104-119 hedges every test step with "should".
- Mechanical:
  - L213 "organization-level" (American spelling) in the same bullet as "organisation-wide".
  - L125 and L182 "service setting" lower-cased.
  - `--` at L110-112 and L146.
- Structure: Sign-Out Troubleshooting competes with `troubleshooting.md`.
- Worst sentence (L77): "Backend token validation (the JWT bearer middleware that protects `/api/**` endpoints) always uses `JIM_SSO_AUTHORITY` for issuer and JWKS, and `JIM_SSO_API_SCOPE` for the audience, regardless of which public client issued the token."
  - Rewrite: "Whichever client a token comes from, JIM accepts it only if your identity provider at `JIM_SSO_AUTHORITY` issued it for the scope in `JIM_SSO_API_SCOPE`. Keep both clients in the same tenant or realm, and grant both that scope."

**sso-setup/microsoft-entra-id.md** (1113w; H; fit 4)
- L71-84: a "(Development only, Optional)" Scalar step sits in the customer guide.
- L115 "Add these to your `.env` file" covers Docker only; Podman readers use `jim-config.yaml` and `jim-secrets`.
- Step 7 never says to restart JIM after updating `JIM_SSO_INITIAL_ADMIN`.
- L40 "Select an expiry period" never says that sign-in stops when the secret expires, or how to rotate it.
- Voice: L104 and L108 use the future tense.
- Worst sentence (L108): "If port 8400 is busy, the module will try ports 8401--8409. You may need to add additional redirect URIs if you encounter port conflicts."
  - Rewrite: "If port 8400 is in use, the module tries 8401 to 8409 in turn. Register all ten redirect URIs (`http://localhost:8400/callback/` to `http://localhost:8409/callback/`) so sign-in works whichever port is free."

**sso-setup/ad-fs.md** (1024w; H; fit 4)
- L111 maps UPN to `sub` without warning that renaming a UPN breaks the administrator's mapping.
- L129-131: the troubleshooting bullets name a cause with no symptom and no how-to.
- There is no find-your-admin-`sub` step and no restart; configuration is `.env` only.
- Mechanical: `--` ×5.
- Worst sentence (L131): "**CORS**: If JIM and AD FS are on different domains, configure CORS in AD FS"
  - Rewrite: name the symptom first ("Sign-in fails and the browser console reports a cross-origin error"), then give the exact AD FS setting or command, and how to confirm the fix.

**sso-setup/keycloak.md** (838w; H; fit 4)
- L180-192: Step 4 "Create a Service Account Client (Optional)" is never connected to any JIM setting, so the reader cannot tell what it is for.
- L165, L166, L233 and L234 British-ise the Keycloak UI label "Authorization".
- There is no admin-`sub` step and no restart.
- Worst sentence (L182): "If you need separate API clients for service-to-service communication:"
  - Rewrite: say which JIM feature uses this client and which setting points at it, or delete the step.

**SSO provider pages: structural divergence**

| Page | Shape |
|------|-------|
| Entra ID | Steps 1-5, 5a, 5b, 6, 7 (find admin `sub`), Next steps; no troubleshooting section |
| AD FS | Steps 1-8, AD FS Claim Rules, AD FS Troubleshooting, Next steps |
| Keycloak | Steps 1-6, 6a, 7, Keycloak Troubleshooting, Next steps |

The optional-step labels also differ: "(Optional but recommended)" against "(Recommended)". Pick one template: Prerequisites, numbered steps, Configure JIM (Docker and Podman), Find the first administrator, Restart and test, Troubleshooting.

### connectors/

**Structural consistency.** No two sibling pages share a template:

| Page | Sections |
|------|----------|
| index | What are Connectors? / How Connectors Fit / Available / Upcoming / Custom |
| File | Overview, Supported Features, Settings, File access, Schema Discovery, Export Cell Formats, Export Only mode, Troubleshooting (`###` per symptom) |
| LDAP | Overview, Supported Directories, Features (6 subsections), Auxiliary Object Classes, Connection Settings (17 subsections, including Container Scope and Exclusion), Security Considerations (6, including the permission recipes), Troubleshooting (`###` per symptom) |
| SCIM | Overview, Supported Service Providers, Features, Connection Settings, Security Considerations, Troubleshooting (bold paragraphs), Related |
| SQL | Overview, Supported databases, What the connector can do, **Before you begin**, Setting up a Connected System (Steps 1-4), How the connector behaves, Worked examples, Security, Licensing notice, Wider database support, Troubleshooting (bold paragraphs), Related |
| SQL Delta Import guides | How it works, What it cannot see, numbered steps, Troubleshooting, Related |

The differences in detail:
- Capabilities appear as a `**Capabilities:**` line (File, LDAP) or a table (SCIM, SQL).
- Only SQL has a Before-you-begin section and a numbered setup.
- Settings sit at top level (File), under "Connection Settings" (LDAP, SCIM), or inside Step 1 (SQL).
- The security section is called "Security Considerations" (LDAP, SCIM) or "Security" (SQL); File has none.
- Troubleshooting uses `###` headings (File, LDAP) or bold paragraphs (SCIM, SQL).
- Only SCIM and SQL have "Related".
- No connector page has front-matter `title:`.
- Heading case is mixed ("Supported databases" against "Supported Directories").
- **Recommendation:** adopt the SQL page's outline as the connector template.

**index.md** (605w; E+R; fit 4)
- L7: an abstraction rationale written for developers.
- L11, L13 and L23 lower-case "metaverse" and "connector space"; L13 introduces "(CSOs)" and "(MVOs)" in prose; L22 "object types"; L24 "(full import, delta import, export)".
- L38 "through fully managed ADO.NET drivers".
- Generic "connector" is lower-cased throughout, while other pages capitalise "Connector".
- Mechanical: `--` ×3.
- Worst sentence (L7): "Connectors abstract away the complexity of each external system, presenting a consistent interface to JIM's synchronisation engine. This means the core synchronisation logic does not need to know the specifics of how to talk to any particular system"
  - Rewrite: "Every Connector offers JIM the same operations, so you configure Synchronisation Rules, Run Profiles and Schedules the same way whatever system sits behind them."

**jim-file-connector.md** (2831w; H+R+T; fit 4)
- Voice:
  - L79 "the customer's HR system", a vendor voice rather than "your".
  - L39 "**You will configure all File Connector File Path settings**".
  - L53 "whenever schema discovery is triggered" (passive and lower-cased).
- History: L203 "or upgrade from a JIM release that did not record exported values".
- Missing: the troubleshooting commands (L217-218, L234) are Docker-only, though the page covers Podman.
- **Wrong remedy:** L256 fixes garbled characters by setting "**Culture**", but Culture governs number and date parsing (L15, L31), not character encoding. L258 names a cause with no remedy.
- Worst sentence (L103): "Permission errors during sync show up as RPEIs on the failing import or export Activity, with a clear "Access to the path … is denied" message."
  - Rewrite: "If JIM cannot read or write the file, the import or export Activity lists the affected objects with the error "Access to the path … is denied". Fix the folder's ownership as above, then run the Run Profile again."

**jim-ldap-connector.md** (14758w; R+H+E+T; fit 3)
- Density:
  - L30 is a single bullet of about 700 words.
  - L17 is a 100-word table cell.
  - L484 (about 250 words) and L485 (about 350 words) are single bullets.
- Developer-speak and design defence:
  - L51 "each value escaped as an LDAP filter requires"
  - L123 "Only what you enable is persisted"
  - L145 "These two scopes are the honest options"
  - L147 "queued as a worker task"
  - L156 "so convergence follows the flows"
  - L236 "opens a throwaway connection ... re-affirms itself for free on every run"
  - L252 "The platform LDAP client will follow a referral ..."
  - L298-299 "Why not just search around the excluded branch? JIM could replace one subtree search ... and it deliberately does not."
  - L353 "The judgement is made from the class's OID"
  - L355 "the Connector already asks the directory to exclude its own hidden and defunct classes"
  - L488 "Discovery costs at most three searches"
  - L672 "The LDAP client library reports a rejected certificate the same way"
- Internal test and lab narration:
  - L17 "verified ... by JIM's integration lab"
  - L457 "JIM's development and integration test stacks do not set it ... so LDAPS validation is exercised for real"
  - L520 "JIM's integration lab runs against exactly these, which is what keeps them honest"
  - L559, L581, L583 and L650 "the integration lab"
  - L611 gives the repository path `test/integration/docker/dirsrv/aci/`
- History and roadmap:
  - L167 "as they always have"
  - L244 "manages one domain today"
  - L256 "today ... is planned"
  - L267 "is how Container selection has always behaved, so existing Connected Systems are unaffected"
  - L698 "JIM no longer completes"
  - L713 "before JIM began recording the invocationId"
- Other:
  - L28 "not been tested against ... Recycle Bin" offers no advice.
  - L200 "IP address is fastest" contradicts the LDAPS name-matching advice (L409, L443).
  - L232 and L711 "persisted watermark".
  - L395 "we recommend".
  - L463 "will not stop you".
  - L703 "Run a full import to re-baseline".
  - L711 "not a bug".
  - L730 "Check the activity log".
  - L657 "may require" (Docker only, no remedy).
- Structure: Container Scope and Exclusion are filed under "Connection Settings".
- Strengths: L274 and L292 say exactly what is deprovisioned; L465 and L707 say "nothing is lost"; L688 quotes real error text.
- Worst sentence (L252): "This is deliberate. The platform LDAP client will follow a referral, but it does so on a new connection that carries none of the credentials JIM bound with, making the follow-up read anonymous."
  - Rewrite: "JIM does not follow referrals, because the directory would answer the follow-up search without JIM's credentials and Active Directory would refuse it. If objects you need live on another server, add a Connected System for that server."

**jim-scim-connector.md** (3798w; R+E+H+T; fit 4)
- Strengths: L70, L73, L75 and L97 explain why in reader terms.
- L10 "The reverse arrangement ... is not covered here" gives no link, so the reader cannot tell whether it exists. Source has a "JIM SCIM 2.0 Service Provider Connector" name constant.
- Developer-speak:
  - L17 "protocol floors"
  - L101-106 bulk protocol internals ("does not set `failOnErrors`", "halves the batch")
  - L105 "is survived, not failed"
  - L126 "quotes and backslashes escaped"
- History: L176 "now the shorter route".
- Voice: L223 and L229 use the future tense and the passive.
- Self-contradiction: L232 says an untrusted issuer "is fixed by adding the certificate under **Admin > Certificates**", but L165 recommends the on-card **Choose what to trust** action.
- Worst sentence (L226): "They should not: deletion detection runs only on a Full Import. If objects are being marked obsolete, the run in question was a Full Import that could not see them."
  - Rewrite: "A Delta Import never removes objects; only a Full Import does. If a Full Import marked objects obsolete that still exist in the provider, the credential could not see them. Check its read permission on that resource type, then run the Full Import again."

**jim-sql-connector.md** (6057w; H+R+E; fit 5; EXEMPLAR)
- "row", "table", "column" and "NULL" are used legitimately here, because they name the reader's own database.
- What makes it the model:
  - The "What it means for you" column.
  - Before you begin, then Steps 1-4.
  - Worked examples.
  - Troubleshooting that quotes the error, then gives the cause and the fix.
  - Transactional guarantees and "One object's failure never stops the rest" (L213, L223).
- Minor issues: L267 "as PostgreSQL and Java read it" (drop it); L40 and L147 use the future tense.

**jim-sql-connector-delta-import-change-log.md** (2156w; H+E; fit 5; EXEMPLAR)
- L3 says what this is, that it is recommended, and the outcome. L18 names the failure and the backstop. L200 says what an early purge loses. L213 explains a refusal.
- The `--` hits on this page are SQL comments, not prose.

**jim-sql-connector-delta-import-watermark.md** (1396w; H+E; fit 5; EXEMPLAR)
- "What it cannot see" comes before the steps.
- Minor: L135 offers only "Keep write transactions short"; add "or run a Full Import".

### api/

**index.md** (1181w; E + R changelog; fit 3)
- Missing the end-to-end workflows that `docs/CLAUDE.md` asks API index pages to provide.
- L27-44 embeds a breaking-changes changelog ("now", "previously", "This is a pre-v1.0 breaking change." ×3).
- Developer-speak:
  - L39 "This closes a gap where an out-of-range number ... bound to an undefined enum value"
  - L44 "enum member ... serialised"
  - L71 "heartbeats they write to the database"
- Mechanical and voice: L7 "the metaverse" lower-cased; L23 future tense.
- Worst sentence (L57): "The ceiling is an offset rather than a page number because that is what the database cost tracks, so the deepest page you may request depends on your page size"
  - Rewrite: "You can page through the first 1,000,000 results (page 10,001 at `pageSize=100`). To reach anything beyond that, narrow the request with a filter or search."

**authentication.md** (423w; H; fit 4)
- L98 "Tokens are validated against the configured OIDC provider" (passive).
- L108 "navigate to the API Keys management page" gives no menu path.
- L122-136 repeats the Usage blocks verbatim.
- Missing reader needs: how to get a token without a browser; what 401 and 403 mean.
- Worst sentence (L158): "**Monitor API key usage.** Review API access logs and revoke unused keys."
  - Rewrite: "Check each key's last-used time (`Get-JIMApiKey`, or the API Keys page) and failed key attempts under [Security Audit Events](../administration/security-audit-events.md). Disable any key nobody has used recently."

**rate-limiting.md** (780w; R+E; fit 4)
- Developer-speak: L174 "The Blazor web UI, the SignalR/Blazor Server connection"; L187 "trusted backend automation (CI/CD pipelines, integration tests ...) ... this trusted principal".
- The settings table duplicates `administration/configuration.md` L187-189.
- Strength: the L225 tip is good.
- Worst sentence (L202): "The rate limiter reads these settings from a short-lived cache (TTL: 30 seconds) rather than the database on every request, so it does not add a database round trip to every API call."
  - Rewrite: "A change to these settings takes effect within 30 seconds. You do not need to restart JIM."

### powershell/

**Structural consistency.** The sibling pages do not share one structure:

- **Heading hierarchy** comes in three schemes:
  - Cmdlet as `##` with `###` sections (system, connection, run-profiles, password-synchronisation, activities, api-keys, certificates, service-settings, feature-flags, security, history, previews, example-data, worker-tasks, file-system, logs, most of connected-systems).
  - Group as `##`, cmdlet as `###`, sections as `####` (metaverse, schedules).
  - Inverted or hybrid (synchronisation-rules): an `###` group heading ("### Synchronisation Rule CRUD") sits above `##` cmdlets, and later sections switch to `##` groups with `###` cmdlets.
  - metaverse breaks its own scheme with `## Set-JIMMetaverseObjectPassword`, and connected-systems nests three cmdlets as `###` under Get-JIMConnectedSystem with no Syntax or Parameters headings.
- **Parameter tables**:
  - Most use "Name | Type | Required | Default | Description".
  - schedules uses "Parameter | Type | Required | Pipeline | Description" (or "Parameter Set") with .NET-cased types (`Guid`, `String`, `Int32`, `Switch`).
  - history adds a "Parameter Set" column.
  - expressions drops the code formatting.
  - security uses "Yes*" footnotes.
  - Type casing (`guid` against `Guid`) differs even within api-keys and activities.
- **ShouldProcess** is shown four ways:
  - a Notes bullet (connected-systems, certificates);
  - a bold line (synchronisation-rules);
  - an `!!! info` admonition (metaverse, worker-tasks);
  - an inline sentence (run-profiles, schedules, api-keys, service-settings, feature-flags, predefined-searches).
- **Output** is shown four ways: a property table (system, connection, password-synchronisation, schedules, metaverse in part); a bare "returns PSCustomObject instances" (api-keys, run-profiles Get, security, file-system, logs, Get-JIMSyncRule); a run-on paragraph of property names (connected-systems L445 and L1549, previews L271); or camelCase names that contradict `index.md` L136-146 ("output is PascalCase"). The module converts output to PascalCase (`src/JIM.PowerShell/Tests/OutputNormalisation.Tests.ps1`).
- **Missing sections**:
  - Set-JIMScopingCriterion has no Parameters or Output.
  - Set-JIMSyncRuleInitialPassword has no Syntax or Parameters at all.
  - Add-JIMRoleMember and Remove-JIMRoleMember have no Output.
- **Openers**: only password-synchronisation says why an administrator would script this rather than click it. Most start "Cmdlets for ..." or "The X cmdlets manage ...".
- **Recommendation:** one template, ideally generated (`docs/CLAUDE.md` already names platyPS as the fix): `## Cmdlet`, then a one-line purpose, Syntax, Parameters (fixed columns), Output (always a table), Examples, Notes (ShouldProcess on one fixed line). Group headings stay only in the left-hand table of contents.

**index.md** (1033w; fit 4)
- L23-31: the air-gapped install comment says "Copy the module" but shows no command. `deployment.md` L736-764 has a fuller, different version.
- L146-152: a paragraph sits between the sentence that introduces a code block and the block.
- Worst sentence (L150, misplaced): "The `Attributes` map is only present on the list form ..." Move it below the code block.

**system.md** (2197w; R; fit 3)
- L52 and L67-68 describe Kubernetes readiness and liveness probes, but `administration/podman.md` L27 says "JIM does not support Kubernetes or OpenShift".
- L75 "heartbeat every service writes to the database every 5 seconds".
- L157 "`Cache-Control: no-store`".
- Reset-JIMSystem:
  - L299 "preserving the schema, seeded built-ins".
  - L299 says "take a database backup first"; it should say database and keys.
  - L317 "counts of removed entities".
  - L339 "`BuiltIn = false`".
  - L341 "factory-state provenance re-recorded".
- Strength: L339-345 (Removed, Preserved, Restored) fully answers "is anything lost?"
- Worst sentence (L340): "**Preserved:** the database schema and its upgrade history, ... the singleton Service Settings record, infrastructure API keys (`IsInfrastructureKey = true`)"
  - Rewrite: "**Kept:** JIM's own structure and upgrade state, every built-in Metaverse Attribute, Object Type, Role, Connector, Example Data Set and Predefined Search, all your Service Settings values, and the infrastructure API key."

**connection.md** (1502w; fit 5; EXEMPLAR)
- L9-16 and L88 quote the failure, say "nothing is changed", and give the remedy.
- L55 and `system.md` L261 define `Authorised` differently.
- Worst sentence (L143): "Removal is **auth-agnostic**"
  - Rewrite: "Disconnect-JIM removes the stored sign-in for the instance whichever way you connected."

**connected-systems.md** (13483w; R; fit 3)
- **Wrong domain noun ×10:** "Connected System Object" is used for the Connected System itself, at L248, L252, L296, L305, L309, L437, L441, L511, L512 and L1463 (and similarly at L1645).
- "connector space object(s)" appears ×10 alongside "Connected System Object"; "object type(s)" is lower-cased ×20.
- Structural accretion (L89-225): three cmdlets nested at `###`; the Stranded-value sweep, the IsDeterminable warning and a DeletionPreview example are stranded after an unrelated cmdlet.
- **Examples that fail:**
  - L153 `Get-JIMConnectedSystem -All` (no such parameter; source parameters are Id, Name, ObjectTypes, DeletionPreview).
  - L686 `-Name "CSV File"`: an exact match is required and the connector is "JIM File Connector" (`src/JIM.Connectors/Constants.cs` L12).
  - L1214 and L1243 "~100,000 items at the default page size" with a default PageSize of 50, while L1287 and L1338 say ~50,000.
- Developer-speak:
  - L247 "("commit message")"
  - L299 setting IDs 1, 2 and 3 with no way shown to find them
  - L367 "fenced ... obsoletion"
  - L369 "from its checkpoint"
  - L438 "persisting"
  - L440 "background worker task ... standard pipeline"
  - L587 "nothing in the database ... never persisted"
  - L1054 "rejected with a 400"
  - L1196 and L1504 "arms the stranded-value sweep"
  - L1244 "rows"
  - L1500 "deferred references"
- History: L807, L842, L1052.
- Destructive operation with no statement of what survives: L532 "This operation is **destructive**: it replaces the existing partition and container configuration." It never says whether selection, scope or exclusions survive.
- Three ways to preview a deletion (L28, L1580, `previews.md`) with no guidance on which to use.
- Strengths: L190, L361, L485, L1444, L1501 and L1704.
- Worst sentence (L1402): "A non-zero count indicates data integrity issues in the connector space. This commonly occurs after a partial import. Running a full import typically resolves outstanding references."
  - Rewrite: "A non-zero count means some reference values (a group's members, a user's manager) point at objects JIM has not imported. Usually the referenced objects sit in a Container outside this Connected System's scope: bring that Container into scope, or, if the last import was interrupted, run a Full Import."

**run-profiles.md** (3116w; R; fit 4)
- L118-119 and L201 use the Windows path `C:\Data\import.csv`; the File Connector reads `/connector-files/...` inside a Linux container.
- L220 "leaving the other two unchanged" is stale; there are five limits (L171).
- L236: Remove has no statement of what survives.
- L297 and L392 "JIM worker service"; L325 and L393 describe endpoint polling internals; L395 "will be rejected".
- Worst sentence (L82): "Runs the honest attribute diff on every object instead of skipping unchanged objects by content hash."
  - Rewrite: "Compares every attribute of every object, instead of skipping objects JIM believes are unchanged. Turn it on for one run when you suspect a change was missed, then off again: it makes the import slower."

**synchronisation-rules.md** (12240w; R; fit 3)
- Inverted and inconsistent headings (L11, L380, L1024, L1369, L1423, L1753, L822); L11 "### Synchronisation Rule CRUD".
- **Issue references:** L493, L504, L555, L726 and L771 "(Unique Value Generation, #242)".
- Developer-speak:
  - L238 "`SyncRuleHeader`" (a DTO name)
  - L525 and L553 "DynamicExpresso" (a third-party parser presented as the language)
  - L144 and L238 the enum `WhenAuthoritativeSourceDisconnected` in prose
  - L775 "no-op"
  - L853 "rows"
  - L1469 and L1658 "the synchronisation engine would never consult it"
  - L1396 and L1400 "Returns the updated Connected System Object" (wrong noun)
- Abbreviations in prose: L138 and L229-230 "MVO ... CSO"; the example titles at L560, L1495 and L1662 use "CS"/"MV".
- Lower-case and drifting nouns: "the metaverse" ×4; "metaverse attribute" ×10; L1026 "Scoping criteria"; L1371 "Matching rules"; L382 "direct attribute-to-Attribute Flows" (a bulk-replace artefact); L409 "Attribute Flow Rules".
- History: L554.
- **Examples that fail:** L1800 and L1809 `Get-JIMSyncRule -All`, which has no `-All` (`src/JIM.PowerShell/Public/SyncRules/Get-JIMSyncRule.ps1`).
- Missing: Set-JIMSyncRuleInitialPassword (L1817) has no parameter table.
- Strengths: L303, L356, L777, L1409 and L1789.
- Worst sentence (L525): "A DynamicExpresso expression."
  - Rewrite: "An expression in JIM's expression language; see [Expressions](../concepts/expressions.md)."

**metaverse.md** (9732w; R; fit 3)
- Developer-speak:
  - L99 and L179 "The MudBlazor icon name" (and no list of valid names)
  - L161 "`BuiltIn = false` so it can be removed via `Reset-JIMSystem`"
  - L242 and L577 "type-the-name safeguard"
  - L248, L450 (in a heading), L509 and L577 "cascade-removed"
  - L542 "no-op"
  - L830 "rows"
- Wrong nouns:
  - L115 and L118 use "connectors" for joined Connected System Objects, which clashes with the product noun Connector.
  - L860, L865 and L1092 use "Identity" as a product noun.
- History: L828 and L1327 "Prior to this release ... / previously flat"; L1140 "reserved for a later release"; L1056 "#242".
- Structure: Set-JIMMetaverseObjectPassword breaks the page's scheme.
- Missing: Get-JIMPendingDeletion never says how to stop a pending deletion.
- Strengths: L223, L248, L1190-1202 and L1287-1290.
- Worst sentence (L834): "A value row with `NullValue` set to `true` is an asserted null: a deliberate, authoritative "no value" assertion carrying provenance only; treat it as no value present, distinct from the attribute having no row at all."
  - Rewrite: "A value with `NullValue` set to `$true` means a Connected System has stated, with authority, that this attribute is empty. Treat it as empty. An attribute with no value listed at all simply has no value yet."

**password-synchronisation.md** (1760w; fit 5; EXEMPLAR)
- L7-9 open with purpose ("a recovery is not a job for a browser").
- L76, L122 and L182 state what survives and why. L212 warns before the destructive example.
- Minor: L12 "encrypted in the database"; L70 "what a link to it is built from".

**predefined-searches.md** (1596w; fit 4)
- Developer-speak: L245 "a dedicated server endpoint ... the full search graph"; L265 "filtered client-side"; L275 "slug"; L288 "null"; L289 "principals".
- L377-398 documents the criteria cmdlets as compact tables, unlike every other page.
- Worst sentence (L365): "This is essential for future expansion as new toggle fields are added."
  - Rewrite: delete it. Keep: "Omit `-IsEnabled` to leave the search as it is; pass `$false` to disable it."

**schedules.md** (4352w; R+E; fit 4)
- L9 and L13 "CRUD".
- Divergent table schema (see above). "schedule" is lower-case 79 times against 69 capitalised.
- **Factual gaps:**
  - Enable-JIMSchedule and Disable-JIMSchedule (L327, L361) omit `-ChangeReason`, which both accept (`Disable-JIMSchedule.ps1` L54) and which `upgrading.md` L37 uses.
  - L676 `-Status` omits `Paused`, which L681 lists as an output state.
  - L468 says "Currently only `RunProfile` is supported", but Backup mentions "Schedule SQL-step connection strings".
- L691 "one entry per Schedule Step *row*".
- No statement of what survives on L281 (Remove-JIMSchedule) or L740 (Stop-JIMScheduleExecution "sends a cancellation request to the server").
- Worst sentence (L281): "Deletes a schedule permanently. This action cannot be undone."
  - Rewrite: "Deletes the Schedule. Activities it produced keep its name (see Activities), and the Run Profiles it ran are not affected. This cannot be undone."

**activities.md** (2756w; fit 3)
- L7 puts history in the opener: "Example data generation is now its own distinct **Data Generation** activity type".
- "activity" and "activities" are lower-cased throughout.
- L232 and L264 offer the stack trace as the customer's remedy.
- L244 and L308 "rows".
- **Examples that fail:** L157, L171, L212 and L217 run `Start-JIMRunProfile -RunProfileId 42` with no Connected System. That cannot resolve a parameter set (`run-profiles.md` L299; `Start-JIMRunProfile.ps1` L93-108).
- Worst sentence (L289): "The underlying API returns a paginated response envelope, but this cmdlet unwraps it internally and still emits one object per child activity to the pipeline, so existing scripts that pipe its output are unaffected by the paginated response shape."
  - Rewrite: "Returns one object per child Activity. Use `-All` to fetch every page."

**api-keys.md** (1218w; fit 4)
- No output tables. L72 gives no pointer to where role IDs come from. L165 uses the future tense. No rotation recipe.
- Worst sentence: none material.

**certificates.md** (1440w; fit 4)
- L224 "used for secure authentication" (they are trust anchors).
- L545 writes DER bytes to a file named `root-ca.pem`.
- No link to Approve-JIMConnectedSystemServerCertificate.
- Worst sentence (L443): "Removing a certificate that is in use by a Connected System may cause authentication failures. Verify usage before deleting."
  - Rewrite: "Before you remove a certificate, check which Connected Systems connect to servers it vouches for. Their next connection fails with an untrusted-certificate error until you trust it again."

**service-settings.md** (778w; fit 4)
- L10 "transmitted as strings. The server interprets the type"; L41 "may be null"; "audit activity" is lower-cased ×4.
- The page never mentions that feature-flag keys are refused.
- Worst example (L93-95): "Disable change tracking" never says what stops being recorded. Retitle it, and state the consequence: what stops being recorded and what is kept.

**feature-flags.md** (739w; fit 2)
- Built around the internal test harness:
  - L214 "(integration harness setup)"
  - L256 "as the integration harness does in scenario setup"
  - L219 and L295 placeholder keys `Features.SomeInDevelopmentFeature`
  - L203 output `TrackingIssueNumber`, "The GitHub issue tracking the flag's own removal"
  - L169 "under the hood"
- Mechanical: `--` ×3.
- Worst (L256): "Enable an In Development flag, as the integration harness does in scenario setup". Delete it, and move InDevelopment handling to the contributor docs.

**security.md** (964w; fit 4)
- The page is titled "Security" but covers Roles.
- Worst sentence (L386): "(Prior to this release the object type was exposed as flat `TypeId`/`TypeName` properties; this is a breaking change to the output shape.)"
  - Rewrite: delete it, and keep the note in the changelog.

**history.md** (2197w; fit 3)
- L13 is a 330-word paragraph; make it a table.
- L41 "rows".
- L113 hand-lists the cmdlets that accept `-ChangeReason`, and the list is already incomplete.
- L162, L166 and L174 use "MVOs" and "CSOs" in prose.
- L291's example loop sums 3 of 8 counters, so it can stop early.
- Worst: L13 (see above).

**previews.md** (4161w; R+E; fit 3)
- Output is called "rows" ×10 (L95, L213-218, L325-340, L411).
- L271 exposes `DeltaPersistence`, `DispatchedToWorker` and `StalenessBaseline` without explanation.
- L330 "client-side page cap"; L386 "PowerShell surface over its two REST endpoints".
- Mechanical: `--` ×7.
- Worst sentence (L65): "Which identifier you pass selects the entity, and the parameters beside it select the surface."
  - Rewrite: "The identifier you pass decides what you are previewing: `-MetaverseObjectTypeId` previews a type's deletion settings; `-SyncRuleId` previews a Synchronisation Rule change; `-ConnectedSystemId` previews scope, Object Matching Rules, schema selection, deleting the system, or a Full Synchronisation, depending on the parameter you add."

**example-data.md** (3653w; R+H; fit 4)
- Two names for one thing: "Example Data Set" against "example data sets"; "Data Generation Template" against "Example Data Template" (here, and in `index.md`, `portal-lists.md` and `activities.md`).
- L72 ".NET culture"; L258 `MvaRef*` keys; L297 "Object Type graph".
- **Safety gap:** the page never says that generated objects are real Metaverse Objects, which enabled export Synchronisation Rules can provision into live systems.
- Missing sentence to add: "Generated objects are real Metaverse Objects. Any enabled export Synchronisation Rule in scope for their type provisions accounts for them, so generate example data only in a non-production instance."

**expressions.md** (355w; fit 3)
- Worst sentence (L7): "Expressions use DynamicExpresso syntax with ..."
  - Rewrite: "Expressions use JIM's expression language (see [Expressions](../concepts/expressions.md))".
- The parameter table also drops the code formatting.

**worker-tasks.md** (535w; fit 4)
- L84 "record is deleted ... durable audit record".
- L143-175: Stop-JIMWorkerTask never says what a cancelled run leaves behind, and the "cancel every task" example has no warning. `upgrading.md` L45-47 shows the shape to copy.

**file-system.md** (273w; fit 3)
- L189 "the JIM Container's file system": the capital C collides with the LDAP Container noun.
- "configured allowed mount points" never says where they are configured.
- L225 and L257 use `/data/imports`, which contradicts the File Connector page (`/connector-files`). `/data` is where the encryption keys live.

**logs.md** (644w; fit 4)
- Clean. Add a pointer to `JIM_LOG_LEVEL`.

---

## 4. Synthesis: the top 8 systemic patterns

Ranked by frequency × harm. Counts are approximate page counts out of the 49 audited, tallied from the per-page notes; a page counts once it has at least one clear instance.

**1. Implementation vocabulary and code walk-throughs in place of reader meaning (31 pages).**
- This covers rows, records, database, null, entity, cascade, persisted, DTO and type names, framework and library names (Blazor, MudBlazor, DynamicExpresso, Npgsql, ADO.NET, .NET culture), worker and polling internals, and "why we built it this way" passages.
- It is the core complaint in #1454: the reader learns how JIM works inside instead of what the behaviour means for their data.
- Examples:
  - `administration/security-audit-events.md` L22: "a naive one-row-per-failure design would let a key-spraying ... client force database writes at wire speed".
  - `powershell/metaverse.md` L834: "A value row with `NullValue` set to `true` is an asserted null ... distinct from the attribute having no row at all".

**2. Destructive, refusing or failing behaviour with no "what survives" or remedy (19 pages).**
- This is the most harmful pattern for production trust, and `docs/CLAUDE.md` names it explicitly.
- Examples:
  - `powershell/connected-systems.md` L532: "This operation is **destructive**: it replaces the existing partition and container configuration." Nothing says what is kept.
  - `connectors/jim-scim-connector.md` L226: "If objects are being marked obsolete, the run in question was a Full Import that could not see them." No remedy is given.
- Also affected: Remove-JIMSchedule, Stop-JIMScheduleExecution, Stop-JIMWorkerTask, Remove-JIMRunProfile, Remove-JIMCertificate, the change-tracking example, Example Data (no production warning), the File Connector encoding remedy, AD FS troubleshooting, Entra secret expiry, upgrading.md L230, and troubleshooting.md's missing synchronisation section.

**3. Release history, issue numbers, roadmap and internal-project narration (24 pages).**
- This covers "now", "previously", "no longer", "Prior to this release", "#242", "v0.16.0 or earlier", "integration lab" and "harness", the devcontainer, repository paths, "stage two", "today ... is planned", and "the same security assessment".
- It ages badly and leaves the reader asking "which release?" The docs should describe shipped behaviour; history belongs in the changelog.
- Examples:
  - `powershell/synchronisation-rules.md` L493: "# Import: generated (Unique Value Generation, #242)".
  - `connectors/jim-ldap-connector.md` L520: "JIM's integration lab runs against exactly these, which is what keeps them honest".

**4. Domain-noun drift: wrong noun, abbreviations, case, two names (26 pages).**
- "Connected System Object" is used to mean the Connected System (12×, which is wrong).
- "connector space object" alternates with "Connected System Object".
- CSO, MVO, CS and MV appear in prose and example titles.
- "Identity" is used as a product noun; "connectors" is used for joins; "JIM Container" appears.
- Lower-case metaverse, object type, metaverse attribute, Activity, Schedule and connector space are common.
- One feature has two names: Example Data Template against Data Generation Template.
- Examples:
  - `powershell/connected-systems.md` L248: "Returns the created Connected System Object".
  - `powershell/metaverse.md` L860: "See which systems an Identity is joined to".

**5. Hand-maintained claims and examples that are wrong or contradict a sibling page (15 pages).**
- Examples that fail: `-All` on Get-JIMSyncRule and Get-JIMConnectedSystem; Start-JIMRunProfile with no Connected System; the "CSV File" connector name.
- Contradictions: Kubernetes probes against "not supported"; `C:\Data` and `/data/imports` against `/connector-files`; ~100,000 against ~50,000 rows; Culture offered as the fix for encoding; SCIM's two trust remedies; `Authorised` meaning two things; camelCase against PascalCase output; `-ChangeReason` missing from the Schedule syntax.
- Examples:
  - `powershell/activities.md` L157: `Start-JIMRunProfile -RunProfileId 42 -Wait -PassThru`.
  - `powershell/system.md` L67: "Use `-Ready` as a Kubernetes readiness probe".

**6. Sibling pages that share no structure (33 pages: 23 PowerShell, 7 connector, 3 SSO).**
- PowerShell uses three heading schemes, four ShouldProcess conventions, four Output conventions and two parameter-table schemas. Connector pages share no outline, and SSO provider pages differ in steps and troubleshooting.
- Readers cannot predict where facts live, and gaps hide (Set-JIMSyncRuleInitialPassword has no parameter table).
- Examples:
  - `powershell/synchronisation-rules.md` L11 ("### Synchronisation Rule CRUD" above `##` cmdlets).
  - `connectors/jim-file-connector.md` against `jim-sql-connector.md` (no Before-you-begin or setup steps).

**7. Mixed Diátaxis modes and misfiled pages, often with no purpose opener (16 pages).**
- Reference, explanation and troubleshooting are packed into how-to folders, and many pages never say who the page is for or why it matters.
- Examples:
  - `administration/sso-setup.md` combines concept, testing, troubleshooting and security guidance.
  - `api/index.md` has a changelog and no workflows.
- Also: configuration.md (reference), portal-lists.md (UI explanation), security-headers.md, the LDAP page (all four modes), deployment.md (6.6k words).

**8. Wall-of-text density (10 pages).**
- Single bullets or paragraphs of 250 to 700 words, and runs of property names written as prose.
- The worst cases are on the most-used pages.
- Examples:
  - `connectors/jim-ldap-connector.md` L30 (about 700 words in one bullet).
  - `powershell/connected-systems.md` L1549 (a 330-word bullet of property names).

**Below the top eight:**
- Hedging and the future tense ("will", "should", "may" where JIM's behaviour is certain) appear on about 14 pages, at low harm.
- A literal `--` appears on 14 pages (54 occurrences) and renders as two hyphens.

---

## 5. Exemplars (and why)

| Page | Why it works |
|------|--------------|
| `connectors/jim-sql-connector.md` | A "What it means for you" column; Before you begin; numbered setup; worked examples for both databases; quoted errors with the cause and fix; "row" and "column" used only where they are the reader's own database; guarantees in reader terms ("a row is never left half-written") |
| `connectors/jim-sql-connector-delta-import-change-log.md` and `-watermark.md` | Say what it is and when to choose it, put "What it cannot see" before the steps, and name each failure with its backstop |
| `administration/backup-recovery.md` | Opens with the stakes, answers "is anything lost?" at L10, verifies at L159, and ends with a checklist |
| `administration/deploying-with-ansible.md` | The canonical how-to shape: purpose, prerequisites, artefact, after the play, how to change things later |
| `powershell/password-synchronisation.md` | The only cmdlet page that opens with why you would script this; every destructive cmdlet says what is kept and how to undo it |
| `powershell/connection.md` | Tells you what happens if you get it wrong, quotes the error, says "nothing is changed" and gives the remedy |
| `administration/podman.md` | The right register for a Linux administrator; honest about limits ("best effort rather than supported") |

These pages are the de facto voice specification #1454 asks for. A voice guide could be distilled from them rather than written from scratch.

---

## 6. Pages to split, merge or move

**Split:**
- `connectors/jim-ldap-connector.md` (14.8k words) into:
  - an overview and settings page;
  - "Granting the service account access" (Active Directory, OpenLDAP, 389 Directory Server, one page each);
  - "Delta Import change sources";
  - password policy discovery as reference.
  - Move Container Scope and Exclusion explanation to `configuration/connected-systems.md`, which already holds the text mode, and move the "Why not ..." design notes to engineering.
- `administration/deployment.md` (6.7k words): make "TLS and reverse proxy" its own page; replace the PowerShell install section with a link to `powershell/index.md`.
- `powershell/connected-systems.md` (13.5k), `synchronisation-rules.md` (12.2k) and `metaverse.md` (9.7k): split by cmdlet group, or better, generate them.

**Move:**
- `administration/configuration.md`: move to `reference/` as the environment-variable reference, and drop its Service Settings table (link to `configuration/service-settings.md` instead).
- `administration/portal-lists.md`: move to `configuration/` (it orients the reader in the UI), and strip the release-note narration.
- `api/index.md`: move "Breaking changes" to the changelog or an API-changes reference, and add workflows instead.
- The version-specific "from v0.x" notes in `upgrading.md` and `troubleshooting.md`: move into per-release upgrade notes.
- The InDevelopment material in `powershell/feature-flags.md`: move to the contributor docs.

**Merge or consolidate:**
- Sign-Out Troubleshooting in `sso-setup.md` merges into `troubleshooting.md` (Authentication).
- Consider merging `security-headers.md`, `security-audit-events.md` and `api/rate-limiting.md` into one "Security controls" section aimed at security reviewers.
- Consolidate the three deletion-preview routes in PowerShell.
- Rename `powershell/security.md` to "Roles".

**Fix in place:**
- `troubleshooting.md`: remove the placeholder and add a Synchronisation section.
- `developer/connectors.md`: remove the placeholder and the HTML TODO, and finish the page (see below).

---

## 7. developer/: audience classification and recommendation

| Page | Words | Audience | Belongs on the customer site? |
|------|------:|----------|-------------------------------|
| developer/index.md | 266 | JIM core contributor ("everything you need to get started as a contributor") | ❌ |
| developer/dev-environment.md | 1351 | Core contributor (Codespaces, devcontainer, "sign in with `admin` / `admin`") | ❌ |
| developer/architecture.md | 1732 | Mostly core contributor (N-tier projects, DI, bulk write performance); its system-context and container diagrams already live in `concepts/architecture.md` | ❌ (keep the concepts version) |
| developer/building.md | 711 | Core contributor (build from source, migrations); its one production paragraph already lives in `administration/configuration.md` | ❌ |
| developer/testing.md | 953 | Core contributor (TDD, test projects) | ❌ |
| developer/powershell-module.md | 1229 | Core contributor (module development); it opens by redirecting end users elsewhere, which shows it is misplaced | ❌ |
| developer/contributing.md | 730 | Core contributor | ❌ (belongs in GitHub `CONTRIBUTING.md`) |
| developer/connectors.md | 3195 | Connector author or integrator | ✅ as a separate "Extending JIM" audience, but L7-10 carries "Documentation In Progress" and an HTML TODO; only the progress and error-reporting half exists |
| diagrams/ACTIVITY_AND_RPEI_FLOW.md | 2220 | Core contributor (`FlushRpeisAsync`, "Since v0.10.0") | ❌ |
| diagrams/CONNECTOR_LIFECYCLE.md | 1761 | Connector author and core contributor | Partly: the interface hierarchy suits "Extending JIM" once rewritten |
| diagrams/DELTA_SYNC_FLOW.md | 953 | Core contributor | ❌ (what it means for customers belongs in `concepts/synchronisation-pipeline.md`) |
| diagrams/EXPORT_EXECUTION_FLOW.md | 3149 | Core contributor (`SyncExportTaskProcessor`, `ISyncServer`) | ❌ |
| diagrams/FULL_IMPORT_FLOW.md | 3289 | Core contributor | ❌ |
| diagrams/FULL_SYNC_CSO_PROCESSING.md | 2863 | Core contributor (`ISyncEngine`) | ❌ |
| diagrams/MVO_DELETION_AND_GRACE_PERIOD.md | 3032 | Core contributor; the behaviour matters to customers | ❌ as written; extract the behaviour to concepts |
| diagrams/PENDING_EXPORT_LIFECYCLE.md | 3993 | Core contributor; the states matter to customers | ❌ as written; extract to concepts |
| diagrams/SCHEDULE_EXECUTION_LIFECYCLE.md | 3406 | Core contributor (`SeedingServer.BuiltInSchedules`) | ❌ |
| diagrams/WORKER_TASK_LIFECYCLE.md | 2322 | Core contributor | ❌ |

The 10 diagram pages carry 209 GitHub issue references and repeated "Since vX" or "Previously" narration.

**Recommendation: split.**
1. **Move the core-contributor material to `engineering/`** or a separate contributor site: `index`, `dev-environment`, `building`, `testing`, `contributing`, `powershell-module`, the internals of `architecture`, and all of `diagrams/`. `engineering/` already holds `DEVELOPER_GUIDE.md`, ADRs and testing strategy, so this is the material's natural home.
2. **Keep one clearly separate, customer-site section, "Extending JIM", for connector authors.** It contains `developer/connectors.md` (finished, with the placeholder removed) and a rewritten connector-lifecycle overview.
3. **Rewrite the customer-relevant behaviour** buried in the diagrams (Pending Export states, Metaverse Object deletion grace periods, Schedule step-failure behaviour) into `concepts/` in reader terms.
4. **Fix the doctrine that invited the bleed.** `docs/CLAUDE.md` lists `developer/` beside `concepts/` as "Explanation", and the nav makes "Developer Guide" a top-level tab level with the customer tabs. Separately, `getting-started/quickstart.md` L107 and `getting-started/prerequisites.md` L59 send new customers into the Developer Guide.
