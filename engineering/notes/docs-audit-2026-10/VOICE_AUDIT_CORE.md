# Docs voice audit A: home, Getting Started, Concepts, Configuration, Glossary

Scope: 31 pages, ~87,100 words (`docs/index.md`, `docs/getting-started/*`, `docs/concepts/*`, `docs/configuration/*`, `docs/reference/glossary.md`). Every page read in full. Line numbers are from the files as they stand on this branch. Context: issue #1454 (docs read as if written for JIM's developers rather than for the administrators implementing it).

Reader assumed throughout: an identity or IT administrator at a customer organisation, implementing JIM, who has never seen JIM's source or schema.

House rules applied: `docs/CLAUDE.md` > "Documentation Ethos: Diátaxis" and "Writing for the Reader"; root `CLAUDE.md` > Code Style.

Mechanical sweep results up front, because they are mostly clean:

- Em dashes (U+2014): **0** in scope.
- "Sync Rule": **0**.
- American spellings: **0** in prose (only `organizationalPerson`, an LDAP class name, and a URL).
- Double-hyphen ` -- ` used as a dash substitute: **21 of 31 pages**, mostly in "See also" lists (which also breach the house `<br />` term/description rule), but in running prose in `architecture.md` (9), `expressions.md` (11), `concepts/index.md`, `index.md`.
- Lower-cased domain nouns: "metaverse" lower-case in **17 pages** (the glossary and most of Configuration capitalise it); "connector space" lower-case in **7 pages** (34 occurrences) against "Connector Space" in 5; "deletion rule(s)", "scoping criteria", "enforce state" lower-case in **9 pages**.
- Passive refusals hiding the actor ("is rejected", "is refused"): **9 pages**, 25 occurrences.
- Addressing the reader: `architecture.md` uses "you"/"your" 0 times in 1,187 words; `synchronisation-pipeline.md` 0.7 per 1,000 words; `operations.md` 0.6; `jml-lifecycle.md` 1.2; compare `getting-started/index.md` at 48.

The real problems are not mechanical. They are below.

---

## 1. Per-page summary table

Fit = audience fit, 5 = clearly for a customer implementer, 1 = internal engineering notes (or empty).

| Page | Words | Intended mode | Actual mode(s) | Fit | Single biggest problem |
|---|---:|---|---|:-:|---|
| `index.md` | 1,026 | Landing | Landing + marketing | 4 | Feature cards carry internal jargon ("Null is a value assertion, next-contributor hand-over"); no Configuration quick link |
| `getting-started/index.md` | 310 | Tutorial (hub) | Hub | 3 | Promises a "Your First Sync" walk-through that is a stub; mixes in developer audience |
| `getting-started/prerequisites.md` | 462 | Tutorial (prep) | Checklist + developer setup | 3 | Developer devcontainer content (`admin`/`admin`) in a customer prerequisites page; no network/DNS/service-account prerequisites |
| `getting-started/quickstart.md` | 700 | Tutorial | How-to (admin) + developer guide | 3 | Half the page is contributor tooling; admin path dead-ends at "Example Data" with no next step |
| `getting-started/first-synchronisation.md` | 63 | Tutorial | Stub | 1 | The only tutorial does not exist |
| `concepts/index.md` | 510 | Explanation (hub) | Explanation | 4 | Minor: `--` dashes, mixed "metaverse"/"Metaverse" |
| `concepts/architecture.md` | 1,187 | Explanation | Explanation + developer architecture | 2 | Second half is source-tree architecture (DTOs, EF Core, `NOTIFY`/`LISTEN`, SignalR) |
| `concepts/attribute-priority.md` | 3,118 | Explanation | Explanation + how-to + reference | 3 | Dense deletion/recall mechanics; history narration; API field semantics |
| `concepts/case-sensitivity.md` | 1,313 | Explanation | Explanation + reference tables | 5 | Exemplar; only weak spot is the unexplained "database collation" escape hatch |
| `concepts/expressions.md` | 4,817 | Explanation | Tutorial + reference + how-to cookbook + troubleshooting | 4 | Customer-aimed, but a function reference and cookbook living in Concepts; C# API names leak in |
| `concepts/jml-lifecycle.md` | 1,555 | Explanation | Explanation + reference tables | 2 | CSO/MVO 29 times, a class name (`MetaverseObject`), and an end-to-end example that describes a Disable action the same page says does not exist |
| `concepts/object-naming.md` | 551 | Explanation | Explanation + reference | 4 | A "before this ordering existed" changelog paragraph |
| `concepts/passwords.md` | 7,362 | Explanation | Explanation + how-to + REST reference + UI tour + service internals | 3 | Careful, reader-aware writing buried in 7,400 words of mixed modes and delivery-service internals |
| `concepts/synchronisation-pipeline.md` | 2,740 | Explanation | Explanation + how-to (Initialising JIM) + performance internals | 3 | Content-hash and write-back optimisation internals; the one first-run procedure lives here |
| `configuration/index.md` | 495 | Unmapped (self-described hybrid) | Hub | 4 | `configuration/` has no Diátaxis mode in `docs/CLAUDE.md`; 15 `--` separators |
| `configuration/activities.md` | 7,672 | Unmapped | Reference + UI tour + explanation + LDAP troubleshooting + release notes | 2 | Reads as accumulated release notes ("now", "before this release", "Applies to new runs") with a factual error at L49 |
| `configuration/api-keys.md` | 716 | Unmapped | Reference (property-by-property) + how-to | 3 | Structured as the data model ("Prefix", "Enabled flag"); unexplained "infrastructure flag" |
| `configuration/certificates.md` | 600 | Unmapped | Reference + how-to | 3 | DTO flags (`Is Expired`, `Days Until Expiry`), "connector-files volume mount", no symptom that sends you here |
| `configuration/configuration-changes.md` | 1,954 | Unmapped | Explanation + reference | 4 | Strong opening; links "change tracking" to a page that never mentions it |
| `configuration/connected-systems.md` | 13,460 | Unmapped | Everything | 2 | 708 lines; the setup procedure is at L645; "transaction", "arms the stranded-value sweep", "fenced", property names |
| `configuration/data-flow.md` | 980 | Unmapped | Reference (UI) + how-to | 4 | Exemplar opening; minor "not modelled", Import/Inbound naming split |
| `configuration/metaverse.md` | 5,033 | Unmapped | Reference + explanation + UI tour + API | 3 | Deletion internals ("stranded-value sweep", "from state rather than from an event"), "MudBlazor icon name", duplicated deletion rules |
| `configuration/operations.md` | 1,566 | Unmapped | UI reference + monitoring how-to | 3 | Opens Service Health with "Until now ... Each service now writes a heartbeat to the database" |
| `configuration/predefined-searches.md` | 1,338 | Unmapped | Reference (API-shaped) | 2 | Written for API consumers ("URI slug", "integer ID is the canonical key", workflows as "set the enabled flag to false") |
| `configuration/roles.md` | 474 | Unmapped | Reference + how-to | 2 | "authenticated principals", "forward-compatible"; contradicts itself on whether a Roles UI exists |
| `configuration/run-profiles.md` | 1,870 | Unmapped | Reference + how-to | 3 | Good Safeguards guidance next to "flush", "stranded-value sweep stays shut", API counters, "capture the returned activity ID" |
| `configuration/schedules.md` | 2,997 | Unmapped | Reference + UI tour + how-to | 3 | "`stepIndex`", "hot paths", "application layer is the authoritative backstop", seven `LastExecution*` fields inline |
| `configuration/service-settings.md` | 800 | Unmapped | Reference (API design notes) | 2 | Explains API design ("an update with a null body"), feature-flag storage "under the hood"; lists no actual settings |
| `configuration/sync-preview.md` | 1,703 | Unmapped | Explanation + UI reference | 4 | Good conditional framing; "persisted", "object graph"; contradicts the pipeline page on what Full Sync processes |
| `configuration/synchronisation-rules.md` | 17,791 | Unmapped | Everything | 2 | 868 lines; engine internals, a "Changes saved before this release" workaround paragraph, REST route table, three duplicated topics |
| `reference/glossary.md` | 1,942 | Reference | Reference | 3 | Three factual errors (Partition = OU, Delta Sync change tracking, Initial Export Only for passwords) and many missing terms |

---

## 2. Per-page detail

### `docs/index.md` (1,026 words)

- **Mode:** landing page (outside Diátaxis by nature). Fine as a landing page.
- **Fit:** 4.
- **Developer-speak:**
  - L70 Attribute Priority card: "with \"Null is a value\" assertion, next-contributor hand-over, and per-value provenance." Three internal terms in a feature card a first-time evaluator reads.
  - L88 "pushed from the database rather than polled" (implementation detail as a selling point); "whether the Worker and Scheduler are alive" (service names not yet introduced).
  - L173 "bounded-memory pipelines validated at 500,000-user scale ... comprehensive integration test coverage" (engineering credentials; acceptable for an evaluator but sits oddly next to "working towards v1.0").
- **Missing reader needs:** Quick Links (L136-162) has Getting Started, Concepts, Administration, Connectors, but **no Configuration card**, although Configuration is the largest section and where an implementer spends most time. "Administration" card says "Configure, monitor, and manage" (L154), which points readers at the wrong section.
- **Voice:** fine.
- **Mechanical:** L58 `--` as a dash; L22 "metaverse", "object types" lower-case.
- **Worst sentence + rewrite:**
  - L70: "Deterministic precedence when multiple Connected Systems contribute the same Metaverse attribute, with \"Null is a value\" assertion, next-contributor hand-over, and per-value provenance."
  - Rewrite: "When several Connected Systems supply the same attribute, you decide which one wins, and you can see where every value came from."

### `docs/getting-started/index.md` (310 words)

- **Mode:** tutorial hub; actual hub. Fine.
- **Fit:** 3.
- **Developer-speak:** L10 "Whether you are deploying JIM for production use or setting up a development environment" (two audiences in the opening of the customer path).
- **Missing reader needs:** L32-38 card "Your First Sync: Set up a Connected System, configure a Synchronisation Rule, and run your first import, sync, and export cycle" links to a 63-word stub. No time estimate, no "what you will have at the end".
- **Voice:** L8 "This section will guide you" (future tense).
- **Mechanical:** card title "Your First Sync" vs nav "Your First Synchronisation" (one name per thing).
- **Worst + rewrite:** L10 as above. Rewrite: "These pages take you from an empty server to JIM synchronising its first Connected System. If you are contributing to JIM itself, start at the Developer Guide instead."

### `docs/getting-started/prerequisites.md` (462 words)

- **Mode:** tutorial preparation; actual checklist + developer setup (mixed audience).
- **Fit:** 3.
- **Developer-speak:** L30-31 tip "If you are using the devcontainer for development ... sign in with `admin` / `admin`"; L50-59 "For Developers" section (Codespaces, .NET 10.0, VS Code extensions) in the customer prerequisites.
- **Missing reader needs:** no network prerequisites (ports, firewall, which hosts JIM must reach, e.g. LDAPS 636 to domain controllers); no DNS name/host name decision; no service accounts on the systems to be connected (the single most common blocker, documented only deep in connector pages); no supported browsers; no "who you need to involve" (IdP admin, directory admin, DBA). Hardware is "modest" with one data point (L41). No "next: Quick Start" pointer at the end.
- **Voice:** L12, L21 "You will need" (future).
- **Mechanical:** none.
- **Worst + rewrite:** L41 "As a general guideline, JIM's resource requirements are modest." Rewrite: "For a pilot with a few Connected Systems and up to tens of thousands of objects, one Linux host with 2 CPU cores and 4 GB of RAM is enough. Size a production host with the Deployment Guide."

### `docs/getting-started/quickstart.md` (700 words)

- **Mode:** tutorial; actual admin how-to (L30-72) + developer guide (L76-107) + PowerShell install.
- **Fit:** 3.
- **Developer-speak:** L86 "runs JIM.Web natively on the host. This is the fastest iteration loop; code changes rebuild and restart in seconds"; L94 "JIM.Web, JIM.Worker, and JIM.Scheduler ... exercise the worker or scheduler"; L67 "watch generation and persistence progress".
- **Missing reader needs:** no "how to verify the install worked" beyond signing in; L67 tells the reader to use "Example Data" without saying where it is or what to do next; no link to a first synchronisation (because it does not exist). No time estimate.
- **Voice:** L102 "The devcontainer will set up" (future); "log in" (L67) vs "sign in" elsewhere.
- **Mechanical:** none.
- **Worst + rewrite:** L67 "When you execute a template, a live progress bar appears on the template page so you can watch generation and persistence progress without leaving it". Rewrite: "Choose a template and run it; a progress bar shows JIM creating the sample users and groups. When it finishes, open Metaverse to see them, then continue to Your First Synchronisation."
- **Recommendation:** move L76-107 to `developer/dev-environment.md`.

### `docs/getting-started/first-synchronisation.md` (63 words)

- **Mode:** tutorial; actual stub ("This page is under active development. Content will be added in a future update.", L7-8).
- **Fit:** 1.
- **Missing reader needs:** everything. The closest substitute is "Initialising JIM" inside `concepts/synchronisation-pipeline.md` L147-162 (run order only), and "Setting up a new Connected System" at L645 of `connected-systems.md`. An implementer has no guided path from "JIM is running" to "JIM synchronised HR to AD".
- **Worst + rewrite:** L8 "Content will be added in a future update." There is no rewrite; the page needs writing (see synthesis).

### `docs/concepts/index.md` (510 words)

- **Mode:** explanation hub; matches.
- **Fit:** 4.
- **Developer-speak:** L29 "a **password channel** that runs parallel to attribute flow and never through it: nothing is held in the Metaverse, staged as a Pending Export, or read back" (accurate but engineering-flavoured; fine for Concepts).
- **Missing reader needs:** no "read these in this order" guidance for a newcomer.
- **Mechanical:** L13, L33 `--` dashes; L3 "metaverse hub" vs L25 "Metaverse attribute"; L29 "attribute flow" lower-case.
- **Worst + rewrite:** L13 "No data moves directly between Connected Systems -- every change passes through the metaverse". Rewrite: "Data never moves directly between Connected Systems: every change passes through the Metaverse, so you govern it in one place."

### `docs/concepts/architecture.md` (1,187 words)

- **Mode:** explanation; actual explanation (L1-66) + operator-relevant containers (L75-105) + **developer architecture** (L107-146).
- **Fit:** 2. The first third is good; the rest is the source tree.
- **Developer-speak:**
  - L85 "built with Blazor Server and MudBlazor".
  - L93 caption "the host dispatches import, synchronise and export processors, and the Sync Engine makes the synchronisation decisions. The host polls the task queue in PostgreSQL".
  - L109 "triggers on the Worker Task and Activity tables publish PostgreSQL `NOTIFY` events on commit, which each service receives over a dedicated `LISTEN` connection."
  - L116 "A SignalR hub at `/hubs/notifications` broadcasts the same events for non-Blazor consumers."
  - L132 "core business logic (\"Servers\") that orchestrate operations ... The UI and API never access the database directly".
  - L138 "defines the domain entities (MetaverseObject, ConnectedSystemObject, SyncRule, etc.), DTOs, and enumerations."
  - L142 "implements the repository interfaces using Entity Framework Core ... migrations, and query optimisation."
  - L65 "This is why the product names the two sides **Metaverse Object** and **Connected System Object** throughout" (product-naming meta-commentary).
- **Missing reader needs:** what the containers mean operationally (which must be reachable from where, which hold state, what to back up) is the reader's actual need and is absent; the page explains code layering instead.
- **Voice:** zero second-person in 1,187 words.
- **Mechanical:** 9 `--` dashes (L44, L89, L120, L128, ...); "metaverse" lower-case.
- **Worst + rewrite:** L138 "**JIM.Models** -- defines the domain entities (MetaverseObject, ConnectedSystemObject, SyncRule, etc.), DTOs, and enumerations." Rewrite: delete from this page (belongs in `developer/architecture.md`). If a customer-facing replacement is wanted: "Everything JIM knows, configuration and identity data alike, lives in its PostgreSQL database, so backing up that database backs up JIM."
- **Recommendation:** move "Real-Time Coordination" internals, the worker-components diagram and the whole "Layered Architecture" section to `developer/architecture.md`.

### `docs/concepts/attribute-priority.md` (3,118 words)

- **Mode:** explanation; actual explanation + how-to (L96-142 "Configuring priority", portal navigation) + reference (outcome list L84-90).
- **Fit:** 3.
- **Developer-speak:**
  - L76 "with one mechanical difference: the deletion itself would sever the provenance the recall depends on, so the recall cannot wait for a synchronisation."
  - L92 "An asserted null appears as a value row flagged `nullValue` with provenance but no value ... consumers should treat such a row as \"no value present\"" (API field semantics in a concept page).
  - L138 "which write it in the same transaction as the ordering".
- **History narration:** L3 "The result no longer depends on the order your synchronisations happen to run in"; L74 "The default, **recall**, is what happened before the choice existed".
- **Missing reader needs:** good on what survives (L48, L66, L80). Weakness is density: L74-80 are four 100-150-word paragraphs on deletion/recall a reader must parse to answer "if I delete this flow, do values vanish?".
- **Voice:** L52 "this is prevented when you configure the order" (passive); L34 "That is the intended behaviour, and it is quiet".
- **Mechanical:** L98 "Attribute priority" lower-case; "Metaverse attribute" vs "Metaverse Attribute" (L127) inconsistent.
- **Worst + rewrite:** L76 (above). Rewrite: "Deleting a whole Synchronisation Rule offers the same choice. If you choose recall, JIM disables the rule straight away, recalls its values as an Activity you can follow on the Operations page, and deletes the rule when that finishes."

### `docs/concepts/case-sensitivity.md` (1,313 words)

- **Mode:** explanation; actual explanation with reference tables. A legitimate mix (tables serve the explanation).
- **Fit:** 5. **Exemplar.**
- **Why it works:** states the principle in one bold sentence (L3); tells the reader why it matters to them (L5: "predict when a change will flow, when two objects will join"); tables carry a "Configurable?" column; L71 names a failure, says what is *not* lost ("Nothing is lost on the target"), and L73-78 give the remedy.
- **Developer-speak:** L95 "this can be arranged at the database level through PostgreSQL collation configuration" (passive, no link, no procedure).
- **Missing reader needs:** L95 offers an option with no way to act on it; L12 "and logs" (which logs?).
- **Mechanical:** L28 "Scoping criteria" lower-case vs "Scoping Criteria" elsewhere; L11, L63 "object type".
- **Worst + rewrite:** L95 "this can be arranged at the database level through PostgreSQL collation configuration." Rewrite: "JIM can be made case-insensitive throughout by changing its database's collation. This affects every comparison and is hard to reverse, so contact support before you do it; the per-rule switches above cover almost every case."

### `docs/concepts/expressions.md` (4,817 words)

- **Mode:** explanation; actual tutorial intro (L3-35, spreadsheet analogy) + full reference (operators, function tables L61-221) + explanation (nulls, L223-331) + how-to cookbook (L333-479) + troubleshooting (L481-532). Heavily mixed, but written for the customer.
- **Fit:** 4.
- **Developer-speak:**
  - L122 "This is a technical limitation of how attribute values are stored internally."
  - L186 "`DateTime.Now`, `DateTime.UtcNow`, `DateTime.Today` and `Guid.NewGuid()`" (C# API names that appear in no function table; the reader cannot tell if they are supported functions).
  - L242, L258 "null-safe", "a null operand is treated as an **empty string**" (programmer vocabulary; arguably the reader's own here, since they are writing expressions, but never defined).
  - L518 "**Check the worker logs** ... the worker service logs the error details" (no how or where; errors appear on Activities).
- **History narration:** L307 "The default is **Evaluate anyway** on every existing and newly created mapping, so nothing changes until you choose otherwise" (upgrade note).
- **Missing reader needs:** L174 `ToInt` "returns 0 if not a valid number" is a silent-data hazard not flagged anywhere near the null section; L518 troubleshooting step with no remedy path.
- **Voice:** L501 "you will see" (future).
- **Mechanical:** 11 `--` dashes in prose and code comments (L112, L125, L128, L247, ...); L13-14 "metaverse attributes" vs "Metaverse attributes".
- **Worst + rewrite:** L122. Rewrite: "Comparing text with `==` can report two identical values as different, so always use `Eq()` to compare text."
- **Recommendation:** split. Function and operator tables to a Reference page; Common Scenarios to a how-to; keep "Where expressions fit" and the null semantics here.

### `docs/concepts/jml-lifecycle.md` (1,555 words)

- **Mode:** explanation; actual explanation + reference tables (Deprovisioning Action, Deletion Rules).
- **Fit:** 2.
- **Developer-speak:**
  - CSO/MVO used 29 times as prose abbreviations, never expanded on this page (L21 "creating a new CSO in the connector space").
  - L25 "JIM **projects** a new MetaverseObject. This is the birth of the identity in JIM's metaverse." (class name).
  - L23 "the Synchronisation Rule's join rules" (the product noun is Object Matching Rules); L53 "inbound Attribute Flow Rules" (not a product noun); L53 "Only changed attributes are processed".
- **Factual contradiction:** L106 "A **Disable** action ... is planned for a future release", but the End-to-End example says L168 "Outbound sync disables Alex's AD account and email" and L169 "Export sends the disable operations". The glossary L39 also says deprovisioning means "removing or disabling accounts". A reader designing their leaver process would build on a capability that does not exist.
- **Accuracy concern:** L88 and L166 imply a status change ("Terminated") disconnects the HR object by itself; it does so only if a scoping criterion makes it leave scope, which the page never says at that point.
- **History/roadmap:** L106 "planned for a future release"; L137 "an unrelated system reconnecting no longer rescues the object".
- **One-home breach:** Deletion Rules and grace-period cancellation (L110-137) restate `configuration/metaverse.md` L19-52 nearly verbatim (compare L137 with metaverse.md L52).
- **Voice:** low "you" (1.2 per 1,000 words); L129 "JIM supports grace periods".
- **Mechanical:** L75 `--`; "connector space", "deletion rules", "metaverse" lower-case; L13 "synchronisation pipeline" lower-case link text.
- **Worst + rewrite:** L25. Rewrite: "If JIM finds no existing Metaverse Object to match, it creates one. This is called projection, and it is how a new person first appears in JIM."

### `docs/concepts/object-naming.md` (551 words)

- **Mode:** explanation; actual explanation + reference tables. Fine.
- **Fit:** 4.
- **History narration:** L132-138 "Before this ordering existed, such groups fell straight through to their External ID and appeared as raw identifiers ... They now appear as, for example, `Project-GlobalGateway`." (changelog paragraph).
- **Missing reader needs:** good remedy section (L156-161).
- **Mechanical:** L66 "object type"; L124 "The object's ID".
- **Worst + rewrite:** L132. Rewrite: "Without rule 2, a group with only a `cn` would display as its External ID, a long string such as `1f16ccb0-...`. With it, the group reads as `Project-GlobalGateway`."

### `docs/concepts/passwords.md` (7,362 words)

- **Mode:** explanation; actual explanation + how-to (setting a password, L115-137, L200-229) + REST reference (L227-229) + UI tour (L284-288) + service internals (L247-257) + operations reference (L259-303).
- **Fit:** 3. The sentences are often model reader-first writing; the page as a whole is not.
- **Developer-speak:**
  - L24 "the moment the export gives the new Connected System Object its external id".
  - L229 "It answers `200` when everything settled and `202` when something was still on its way, with the same body either way: `origin` (`Explicit` or `Propagated`), `settled`, and one entry per Connected System carrying its `state` ... `attemptCount`, and `nextAttemptAt`".
  - L249 "The Worker runs a separate Password Delivery Service alongside its synchronisation loop, and the two share nothing but the process".
  - L251-255 "A sweep every 30 seconds sits under all of that as a floor ... up to four systems in progress at once ... the claim lapses after a minute".
  - L265 "the queued value is encrypted in the database".
  - L286 "The entry's dot takes the colour of its worst outcome." L288 "It reads from the Activities rather than from the queue, deliberately."
- **History/roadmap:** L8 "Today the LDAP Connector is the Connector that supports it"; L72 "it is not built yet"; L212 "a future capture agent ... would use"; L326 "When one exists, it will use"; L179 "If an earlier version of your deployment had selected one".
- **Vocabulary over-correction:** "Connected System Object" used where the reader means "account", making sentences stilted: L3 "A newly provisioned Connected System Object is not much use until somebody gives it a password." L117 "the Connected System Object whose provisioning password was refused".
- **Internal contradiction:** L62 "it is the only password JIM stores anywhere" (also `synchronisation-rules.md` L347 "This is the only password JIM stores") vs L141 and L314: queued, parked and cancelled changes hold the encrypted password, potentially for the one-year retention period.
- **One-home breach:** initial passwords (L12-72) repeat `synchronisation-rules.md` L319-391; policy discovery and "could not tell" (L95-108) repeat `connected-systems.md` L426-442; on-demand set password (L115-137) repeats `connected-systems.md` L464-506.
- **Exemplary parts:** L30-36 states table with a "What you do" column; L318-323 encrypted-connection warning ("Nothing is discarded when JIM refuses"), which `docs/CLAUDE.md` quotes as the model.
- **Mechanical:** fine.
- **Worst + rewrite:** L249. Rewrite: "Password delivery never waits for a synchronisation run: JIM attempts a queued change within about a second, even while a Full Import is running."
- **Recommendation:** cut to a ~2,000-word concept page (why passwords are different, what JIM keeps and for how long, the five states). Move the Operations > Passwords tab to `configuration/operations.md`, REST/PowerShell detail to the API/PowerShell pages, delivery-service internals to developer docs, and keep one home for initial password configuration.

### `docs/concepts/synchronisation-pipeline.md` (2,740 words)

- **Mode:** explanation; actual explanation + how-to ("Initialising JIM", L147-162) + performance internals.
- **Fit:** 3.
- **Developer-speak:**
  - L47-49 "JIM speeds this up with a stored **content hash** ... JIM computes the same hash for each incoming object *before* loading its existing attribute values ... JIM skips loading and comparing that object's attributes entirely".
  - L128 "this avoids re-processing millions of attribute values that JIM itself just wrote".
  - L110 "configurable export concurrency for asynchronous LDAP operation pipelining".
  - L23 "the post-clear reconciliation gate".
  - L55 "MetaverseObjects (MVOs)" (class name); CSO/MVO 18 times.
  - L41 "An obsoleted CSO stays that way".
- **Naming inconsistency:** L63 "join rules" (the page itself later says Object Matching Rules, L149); L67 "Attribute Flow Rules"; L1 "Sync Pipeline" vs nav "Synchronisation Pipeline"; L91-92 "Full Sync"/"Delta Sync" vs the Run Profile names Full Synchronisation/Delta Synchronisation used everywhere else.
- **Contradiction:** L91 "**Full Sync** re-evaluates every CSO against the Synchronisation Rules" vs `sync-preview.md` L37 and `connected-systems.md` L607 ("a routine run skips objects unchanged since the one before").
- **Missing reader needs:** L105 "Failed exports are logged with error details for troubleshooting" (where? what do I do?).
- **Voice:** "you" 0.7 per 1,000 words; passive steps (L33-39).
- **Mechanical:** "full import" (L15) vs "Full Import"; "connector space" lower-case 12 times; "activity log" (L39, L106); L120, L166 `--`.
- **Worst + rewrite:** L49. Rewrite (replacing L45-51): "JIM skips objects that have not changed since the last Full Import, so a Full Import of a large, mostly unchanged system finishes quickly. If you suspect a change was missed, turn on Verification Mode on the Run Profile for one run: JIM then compares every object in full and reports any change it would have skipped."
- **Recommendation:** move "Initialising JIM" into the first-synchronisation tutorial (or its own how-to) and link to it from here.

### `docs/configuration/index.md` (495 words)

- **Mode:** `configuration/` is **not in the Diátaxis folder mapping** in `docs/CLAUDE.md` (L10-15 maps `administration/`, `connectors/`, `api/`, `powershell/` to how-to). The page self-describes a hybrid: "What it is ... Common workflows at a concept level (the steps that matter, not click-by-click instructions) ... Where to do the work" (L53-58).
- **Fit:** 4.
- **Developer-speak:** L7 "a small set of first-class objects".
- **Missing reader needs:** no suggested order for a new implementer (the nav lists Configuration Changes and Sync Preview before Connected Systems).
- **Mechanical:** 15 ` -- ` separators (L15-49), breaching the `<br />` term/description rule; "metaverse schema, schedules" lower-case (L7).
- **Worst + rewrite:** L7 "JIM configuration is centred on a small set of first-class objects". Rewrite: "You configure JIM through a handful of things: the Connected Systems it talks to, the Synchronisation Rules that say what flows, the Run Profiles and Schedules that make it happen, and the Metaverse that holds the result."

### `docs/configuration/activities.md` (7,672 words)

- **Mode:** unmapped; actual reference (statuses, counters) + UI tour (Lineage, pills, rings) + explanation (causality) + LDAP troubleshooting (L57-71) + configuration-history reference + release notes.
- **Fit:** 2.
- **Developer-speak:**
  - L22 "Most monitoring code only cares about whether the activity has reached a terminal status".
  - L32-35 counter names as fields (`Total Added` ...), L41 `exportCreatesWithheld` ..., L49 "It is `null` on every other activity".
  - L59, L69 `uSNChanged`, `cn=Retro Changelog Plugin,cn=plugins,cn=config`, `nsslapd-log-deleted` (connector-specific detail in the Activities page; L69 is a single ~250-word bullet).
  - L79 "every entity mentioned"; L83 "the object graph itself, as columns" (one ~600-word bullet, L83).
  - L101 "A queued deprovision is distinguished from an ordinary **Export queued** because the two look alike in the data but mean opposite things ... without the distinction that payload was reported as an attribute change".
  - L134 "the walk stopped at its depth bound".
  - L146 "JIM pairs the confirmation to its export by the queued change they both name, rather than by the Connected System Object, because a Connected System Object cycles through export and import repeatedly".
  - L154 "a background housekeeping process on the worker".
  - L183 fifteen query-string parameter names; L191 "rendered as a structured diff"; L193 "change payload; no separate call is needed"; L210 "much cheaper to serve than the full Activity detail endpoint"; L219 "resolves change history references ... resolves cross-page references"; L224 "It is green because the run is past it; it is a ring rather than a filled tick because it did no work".
- **History narration (the heaviest in scope):** L7 "Example data generation is now its own distinct **Data Generation** activity type"; L66 "the record predates this version of JIM"; L83 "a run from before that was captured shows none"; L95 "Records written before this capture existed fall back"; L105 "Review items recorded before this release show no operation"; L111-112 admonition "Applies to new runs ... Runs from this version onwards use the new outcome"; L114 "Activities recorded before this outcome existed show these objects as **Attributes flowed**"; L150 "changes queued **before** you upgraded to a version carrying this show a shorter chain"; L197 "Configuration change history now covers **every**"; L249 "as it always has".
- **Factual error:** L49 "otherwise it is how many Connected System Objects deletion detection found missing but marked as deleted." Should read "but did **not** mark as deleted" (the counter is of withheld deletions).
- **Missing reader needs:** no opening that says when to come here (a run failed; an object did something unexpected). Statuses (L15-20) carry no "what to do next". The one task a reader most needs ("a run says Complete with error: find which objects failed and why") is at L253-259, the end of the page.
- **Mechanical:** "activity"/"Activity" mixed (L7 "An **activity**" vs L164 "An Activity"); L39 vs L47 headings fine.
- **Worst + rewrite:** L101. Rewrite: "A **Deprovision queued** outcome means JIM has queued the account for deletion in the target system. Expand it to see how JIM will find the account (for a directory, its distinguished name) under **Target identified by**; these values are not being written to the account."
- **Recommendation:** split into (1) Activities reference (statuses with next steps, counters, filters, retention); (2) "Find out why something happened" how-to (Lineage/Timeline/Table, causes); (3) Configuration Change History as its own page (every configuration page and the home page link to `activities.md#configuration-change-history`); (4) move Delta Import change-source checks to the LDAP Connector page; (5) delete the release-notes paragraphs (the changelog already holds them).

### `docs/configuration/api-keys.md` (716 words)

- **Mode:** unmapped; actual reference walked property by property ("Prefix", "Name and description", "Expiry", "Enabled flag", "Roles", "Last-used tracking") + how-to workflows.
- **Fit:** 3.
- **Developer-speak:** L11 "JIM stores only a SHA-256 hash of the key; the plaintext is never persisted" (security reviewers want this; "persisted" is still the wrong word); L29 heading "Enabled flag"; L43 "infrastructure flag" (never explained), "change history payload", "operational state, not configuration"; L52 "Capture the full key from the create response".
- **Roadmap:** L35 "future role granularity will let you mint narrower keys".
- **Missing reader needs:** what an automation sees when its key expires or is disabled (refusal and its error) and how to see expiry coming; what "infrastructure" keys are.
- **Voice:** L25 "Expired keys are rejected automatically" (passive).
- **Mechanical:** L75-76 `--`.
- **Worst + rewrite:** L43 "...enabled state, infrastructure flag, and Role assignments) alongside who made the change, when, and an optional reason." Rewrite: either define it ("Infrastructure keys, created by JIM's installer for its own services, ...") or drop it from customer docs.

### `docs/configuration/certificates.md` (600 words)

- **Mode:** unmapped; reference + how-to.
- **Fit:** 3.
- **Developer-speak:** L83 "used by connectors for LDAP and HTTPS authentication" (it is server certificate trust, not authentication); L91 "supplied as Base64 and stored directly in the JIM database"; L92 "a path inside the connector-files volume mount"; L96 heading "Enabled flag"; L102 "surfaces convenience flags: `Is Expired`, `Is Expiring Soon`, and `Days Until Expiry`"; L106 "It does not modify state"; L127 "connectors successfully negotiate".
- **Missing reader needs:** no symptom that brings a reader here (e.g. an LDAPS connection failing with an untrusted-certificate error), no example of that error; L102 tells you expired certificates stay trusted but offers no way to be alerted.
- **Good:** L85 "adding one can only ever allow more connections to succeed" answers "is anything at risk?".
- **Mechanical:** "connectors" lower-case.
- **Worst + rewrite:** L102. Rewrite: "Each certificate shows when it becomes valid and when it expires, and the list marks those expiring soon. JIM keeps trusting an expired certificate until you disable or delete it, so check the list as part of your certificate renewal routine, or script it with `Get-JIMCertificate`."

### `docs/configuration/configuration-changes.md` (1,954 words)

- **Mode:** unmapped; explanation + reference.
- **Fit:** 4.
- **Exemplary:** L3 "Some configuration edits are harmless. Others decide whether accounts are deleted." L21 "Cancelling the confirmation abandons the save. Nothing is written." L68-72 "Three things are worth knowing before you act on a preview".
- **Developer-speak:** L7 "Every configuration property carries one of three classes"; L35 "Previews are available where a surface has an evaluator for it"; L53 "counting the objects a change would affect is a statement about a change that will happen"; L71 "detail rows"; L76 "pass the preview's Activity id back on the change itself".
- **Broken promise:** L90 links "[configuration change tracking](service-settings.md) must be switched on" and L105 "Service Settings -- switching configuration change tracking on or off", but `service-settings.md` never mentions change tracking. The setting (`ChangeTracking.ConfigurationChanges.Enabled`) is only named in `activities.md` L197.
- **Mechanical:** L13 "deletion rules"; L96-105 ten `--` separators; title "Configuration changes" in sentence case.
- **Worst + rewrite:** L35. Rewrite: "You can preview these changes before you save them:".

### `docs/configuration/connected-systems.md` (13,460 words, 708 lines)

- **Mode:** unmapped; actual explanation (connector space), UI reference, how-to (setup at L645), API reference, troubleshooting, upgrade notes, and a third copy of password content.
- **Fit:** 2.
- **Developer-speak (selection):**
  - L34 "**Rollback potential**: The metaverse is only updated in the sync phase" (implies a rollback feature that does not exist).
  - L72 "Clearing records, inside the same transaction, which Metaverse Objects were joined at that moment, and arms the **stranded-value sweep** ... once the gate is open"; L74 "the arming stays in place".
  - L215 "Every unresolved reference is logged (at Warning level in Warn mode, Debug level in Ignore mode)".
  - L300 `dependentDerivedFlows`; L305 `HasRemovalsOrDefinitionChanges`, `Dependents`, `RemovalImpact`; L586-588 `ConfigurationDrift`, `IsDeterminable`, `HasPendingChanges`; L533 `ConnectorSupportsPasswordSet`.
  - L376 "the bulk attribute endpoint refuses a request carrying a data type rather than ignoring it".
  - L457 "a search returning no rows, both with a success code".
  - L543 "Run an export Run Profile to flush them."
  - L660 "While it runs, the system is fenced"; L661 "Small systems are removed synchronously".
  - L299 "with the honest warning".
- **History narration:** L78 "historical strays from a clear that predates this feature"; L111 "the hierarchy has not been retrieved since this feature shipped"; L193 "Containers selected before this behaviour shipped record their identifier at their next hierarchy refresh"; L343 "the File Connector has always asked you"; L382 note "Existing Connected Systems"; L400 "Upgrades" bullet.
- **Missing reader needs:** the setup procedure an implementer needs first ("Setting up a new Connected System", L645-654) is at the bottom of a 708-line page and is eight one-line steps with no links to the parts above it. No opening that says "start here to connect a system".
- **One-home breach:** L414-506 duplicates `concepts/passwords.md` (policy discovery table L433-440 vs passwords.md L101-106; "could not tell" L428 vs passwords.md L98; one password across systems L489-504 vs passwords.md L126-137).
- **Exemplary parts:** L124-125 explain *why* exports outside scope are refused in reader terms; L299 states that cancelling "does not preserve the status quo"; L523 "Switching a system off does not discard anything."
- **Mechanical:** "connector space" lower-case 17 times vs "Connector Space" 7 times on the same page; "metaverse" lower-case 15 times; L549-637 hard-wrapped source lines (inconsistent with the rest).
- **Worst + rewrite:** L72. Rewrite: "When you clear a Connector Space, JIM notes which Metaverse Objects it was joined to. After your next successful Full Import and Full Synchronisation of that system, JIM checks which of them did not come back and tidies them up: it withdraws the values they contributed and applies their Deletion Rule. Until then it changes nothing, so a half-finished re-import is never mistaken for people leaving."
- **Recommendation:** split (see synthesis).

### `docs/configuration/data-flow.md` (980 words)

- **Mode:** unmapped; UI reference + how-to. Coherent.
- **Fit:** 4. **Exemplar for openings.**
- **Why it works:** L83 opens with the reader's problem in their words ("where does this person's job title actually come from?"), L85 says it is read-only and where changes are made, L87 says where to find it.
- **Developer-speak:** L137 "An expression's attribute references live in its text and are not modelled".
- **Naming:** L148 `-Direction Import` vs the UI's "Inbound" (L93, L128).
- **Mechanical:** L83 "Attribute flows" lower-case; L105 "Connected System object type".
- **Worst + rewrite:** L137. Rewrite: "The Metaverse Attribute filter cannot see inside expressions, so it misses a flow whose source is an expression. Use Search instead: it looks inside expression text."

### `docs/configuration/metaverse.md` (5,033 words)

- **Mode:** unmapped; reference + explanation + UI tour (Inspect, Connections) + API.
- **Fit:** 3.
- **Developer-speak:**
  - L27 "JIM finds these on every stranded-value sweep, and applies the rule from state rather than from an event ... **Specific source(s) disconnect** is event-only and is not reached this way, because state alone cannot tell a listed source's departure from one that never joined."
  - L46 "The REST object type responses list these systems as `deletionSourceWarnings`, and the Synchronisation Rule create and update responses carry `deletionSourceWarning`."
  - L68 "They are read at the moment a Connected System Object disconnects, not by the housekeeping pass that acts on objects already marked".
  - L78 "An optional MudBlazor icon name (for example `Devices`)" (the UI component library's name).
  - L91, L131 "**cascade-removed** ... in dependency order so nothing is left dangling".
  - L182 "the explanations are evaluated in memory against values already stored".
  - L206 "a fast named-search API ... call by URI".
- **History/roadmap:** L39 "Configurations created before trigger modes existed keep the **Specific source(s) disconnect** behaviour"; L114 "Over time the mappings will also power suggested default flows"; L142 "values set before JIM recorded their source"; L163 "For objects joined before JIM recorded it".
- **Inconsistency:** L9, L17, L135 "`person` and `group`" vs L74 "JIM's built-in `User` and `Group` types"; L95 example attributes `displayName`, `mail`, `employeeId` vs the built-ins named at L104 (`First Name`, `Job Title`, `Email`).
- **One-home breach:** Deletion Rules and grace periods (L19-55) duplicate `jml-lifecycle.md` L110-137; the "provisioned targets count as connectors" warning (L29-30) is repeated in `sync-preview.md` L57-59.
- **Good:** L29 "Nothing is lost, but nothing is deprovisioned either."; L50 "The grace period is the right default for production".
- **Mechanical:** "Object types" heading (L15), "metaverse" lower-case 13 times, "Pending deletions" (L196), "connector space link" (L198).
- **Worst + rewrite:** L27. Rewrite: "A Metaverse Object can also lose its last connection without any single disconnection causing it, most often after you clear a Connector Space and some objects do not come back. JIM still applies When Last Connector Disconnected, and When Authoritative Source Disconnected in All sources mode, to these objects. Specific source(s) mode only reacts to a named source disconnecting, so it does not apply. Pending Deletions shows these objects as 'No connector remains'."

### `docs/configuration/operations.md` (1,566 words)

- **Mode:** unmapped; UI reference + monitoring how-to.
- **Fit:** 3.
- **History narration:** L18 "Until now the only sign either was alive was the container health check ... Each service now writes a heartbeat to the database every 5 seconds".
- **Developer-speak:** L20 "whether the portal is receiving real-time change notifications from the database"; L28 "(green, amber or red; the pill is the only coloured thing on the card) ... each value is in a fixed-width face"; L64 twelve JSON field names and `Cache-Control: no-store`.
- **Voice:** L56 "These are fixed for now; if a deployment needs them tuned, say so." (informal, no channel given, "for now"); 0.6 "you" per 1,000 words.
- **Exemplary:** L34-40 status table with Meaning and a remedy in each row; L52 "restart it"; L75 tells you which health endpoint to use for what.
- **Worst + rewrite:** L18. Rewrite: "JIM does its work in two background services, the Worker and the Scheduler. Each reports in every 5 seconds, and the Service Health strip shows whether each is running and what it is doing, so you see a stopped service before a Schedule fails to run."

### `docs/configuration/predefined-searches.md` (1,338 words)

- **Mode:** unmapped; reference written for API consumers.
- **Fit:** 2.
- **Developer-speak:** L7 "back the fast named-search endpoint that end-user search consumers call"; L9 "end users ... consume the ones that already exist"; L18 "**A stable URI slug** ... that consuming code uses to invoke the search"; L22 "even though the underlying integer ID is the canonical key for write operations"; L26 "first-class"; L32 heading "Enabled flag"; L45 "The running surface is part of the metaverse endpoints"; L51-52 workflow "capture its ID ... Update the search with the enabled flag set to false"; L102 `IsActive = true` pseudo-code.
- **History:** L57 "a previously hidden search" (fine), none serious.
- **Missing reader needs:** no use case (why would an administrator create one: a helpdesk list of leavers this week, a sidebar entry for contractors); who "end users" are in JIM is never said.
- **Mechanical:** L7 "predefined search", "metaverse objects" lower-case.
- **Worst + rewrite:** L22. Rewrite: "Each search has a short web address (for example `people`) that the portal's links, scripts and integrations use to open it. Choose it with care, because anything that links to the search uses that address."

### `docs/configuration/roles.md` (474 words)

- **Mode:** unmapped; reference + how-to.
- **Fit:** 2.
- **Developer-speak:** L67 "what authenticated principals (users and API keys) can do ... the underlying model is forward-compatible"; L75 "(typically `person` objects)"; L77 "metaverse-object membership"; L90 "recorded as System-attributed version 1 from first startup".
- **Contradiction:** L92 "There is no Role management UI yet (Role definition editing arrives with a future release)" vs L109 "**JIM portal**: Roles area of the admin UI".
- **Missing reader needs:** how a person signing in through SSO becomes a Metaverse Object that can hold a role (L98 "created via synchronisation from a source system, or manually" is all), how the first administrator is established, what a signed-in user without the role sees, what each role permits.
- **Roadmap:** L67, L92 future releases.
- **Mechanical:** L115-116 `--`.
- **Worst + rewrite:** L67. Rewrite: "A role decides what a person or an API key can do in JIM. Today JIM has one role that matters, Administrator, and almost every administrative task needs it."

### `docs/configuration/run-profiles.md` (1,870 words)

- **Mode:** unmapped; reference + how-to.
- **Fit:** 3.
- **Developer-speak:** L19 "Flush Pending Exports out"; L23 "increase the blast radius if a batch fails. Sensible defaults differ per connector; tune as needed" (no default stated, no setting key); L33 `targetsDeselectedPartition`; L57 `exportCreatesWithheld` counters; L77 `detectedDeletionsWithheld`; L79 "the Connected System's last successful Full Import time is not stamped, so the stranded-value sweep stays shut"; L83-85 "Asynchronous execution: Triggering a Run Profile returns an activity ID. The actual work runs on the worker process ... polling the activity"; L98 "capture the returned activity ID".
- **Exemplary:** L57 "leaves every one of them exactly where they were: still Pending, untouched"; L65 recommended values; L69-71 "Letting a legitimate mass change through" is model guidance (states the trade-off and recommends the safer option).
- **Voice:** L49, L55, L75 "is rejected" (passive).
- **Mechanical:** "connector space object" (L17), "schedules" (L9), "activities" (L85) lower-case.
- **Worst + rewrite:** L79. Rewrite: "A Full Import that withheld its deletions does not count as a successful Full Import after a Connector Space clear, so JIM postpones tidying up after the clear until a Full Import completes within its limits."

### `docs/configuration/schedules.md` (2,997 words)

- **Mode:** unmapped; reference + UI tour + how-to.
- **Fit:** 3.
- **Developer-speak:** L37 "A `stepIndex` orders steps"; L92 `LastExecutionId`, `LastExecutionStatus`, `LastExecutionCurrentStepIndex`, `LastExecutionTotalSteps`, `LastExecutionFailedStepIndices`, `LastExecutionCompletedAt`, `LastExecutionErrorMessage`; L116 `CancellationReason`; L122 "A task is removed from the queue the moment it finishes"; L124 "The wedges are ordered by outcome rather than by task"; L136 "Both the import and export hot paths skip an object that hasn't changed"; L138 "rather than in one long transaction"; L149 "the underlying application layer is the authoritative backstop".
- **History:** L72 "Upgrading JIM changes nothing about how your existing Schedules behave ... steps that had **Continue on failure** switched on are set to ...".
- **Missing reader needs:** L42-44 PowerShell, Executable and SQL script step types get one line each: nowhere says which container runs them, which account they run as, where scripts live, or what "a configured database connection" is (L44). These are the security-relevant questions an implementer in a regulated organisation will ask first. "Auditing" (L151-153) and "Change history" (L155-161) overlap.
- **Exemplary:** L52-59 "When a step fails" (clear options with when to use each); L76 "Nothing is lost when a schedule stops or cannot start".
- **Mechanical:** "schedule" vs "Schedule" mixed within the page (L7 vs L48).
- **Worst + rewrite:** L136. Rewrite: "**Temporal Scope Reconciliation.** A Synchronisation Rule can scope on dates, such as leavers whose end date has passed. Synchronisation only looks at objects whose data has changed, so without this schedule a leaver whose record has not changed would not be deprovisioned when the end date arrives. It runs hourly by default."

### `docs/configuration/service-settings.md` (800 words)

- **Mode:** unmapped; reference written as API design notes.
- **Fit:** 2.
- **Developer-speak:** L7 "Each setting has a key (in dot notation), a typed value, and a default"; L11-14 "Two flags help you reason about state ... **Effective value** ... **Is overridden**"; L18 "These are returned by the admin surfaces for visibility but cannot be changed at runtime; updating them is rejected"; L20 "By design, JIM keeps the set of environment-variable-driven settings small"; L34 "it does not change semantics"; L36 "JIM's feature flags are stored as Service Settings under the hood ... `api/v1/service-settings` and `Get-JIMServiceSetting`/... all exclude and refuse Preview Features rows"; L40 "resetting the value back to null ... It conveys intent (return to default) more clearly than an update with a null body would"; L54 "Settings are typed: string, boolean, integer, or timespan. The type is enforced on update."
- **Missing reader needs:** the page names no actual setting or what any one does (deferred to `administration/configuration.md`); `configuration-changes.md` sends readers here for change tracking, which is absent.
- **One-home breach:** Service Settings has two homes (`configuration/service-settings.md` and `administration/configuration.md#service-settings`).
- **Worst + rewrite:** L40. Rewrite: "**Revert** puts a setting back on its built-in default, so you do not need to remember what the default was. The setting's history records the revert."

### `docs/configuration/sync-preview.md` (1,703 words)

- **Mode:** unmapped; explanation + UI reference. Coherent.
- **Fit:** 4.
- **Exemplary:** L7 frames the question in the reader's words; L21 "Every statement in the preview is conditional ... Nothing has happened"; L67-69 "What the preview cannot know".
- **Developer-speak:** L9 "Nothing is staged, persisted, or exported"; L23 "a preview has no recorded object graph to draw"; heading L39 "The destructive cascade".
- **Contradiction:** L37 "A Full Synchronisation skips an object that has not changed since the system's last synchronisation, unless a Synchronisation Rule has changed" vs `synchronisation-pipeline.md` L91 and `glossary.md` L54 (Full Synchronisation processes every object).
- **One-home breach:** L57-59 repeats `metaverse.md` L29-30.
- **Mechanical:** "metaverse" lower-case (L69); L84-88 `--`.
- **Worst + rewrite:** L23 "(the Lineage view is not offered, since a preview has no recorded object graph to draw)". Rewrite: "(there is no Lineage view, because nothing has happened yet to trace)".

### `docs/configuration/synchronisation-rules.md` (17,791 words, 868 lines)

- **Mode:** unmapped; actual explanation + reference + how-to + REST reference + upgrade notes, plus full copies of Attribute Priority and Initial Password content.
- **Fit:** 2.
- **Developer-speak (selection):**
  - L170 "which rules the synchronisation engine consults ... A rule of the other scope would be silently inert".
  - L254 "when the export Run Profile flushes Pending Exports".
  - L380 "inflate an attempt count that is supposed to mean \"distinct configurations tried\"".
  - L397 "The refusal exists because the synchronisation engine evaluates one mapping per target attribute, so a same-rule duplicate would be accepted in configuration but silently never contribute."
  - L440 "notes how many it skipped in the service log".
  - L620-627 a REST route table with HTTP `200`/`409` semantics; L850 "answers `200 OK` ... `202 Accepted`".
  - L657, L671 long REST paths inline; L578, L580, L629 `generation` object, `participants`, `reportsCollisions`, `exclusions`.
  - L726 "An Expression that throws"; L728 "The evaluation is the synchronisation engine's own, run twice per object"; L848 "the ordinary recall machinery covers it".
  - CSO/MVO 21 times (L37-51, L135, L152-154, L250, L779-803).
- **History narration:** L294 entire paragraph "**Changes saved before this release.** Only changes saved from this release onwards are reviewed ... switch **Provision to Connected System** off and save, then switch it on and save again"; L308 "before this preview existed the first sign of what one meant was the synchronisation run"; L491 "so a stray space no longer masquerades as a real value"; L563 admonition "A synchronisation used to work from JIM's database alone. Now a synchronisation that generates a value opens a connection"; L673 "cannot yet be changed by hand"; L696 "in this release, a Reference attribute"; L714 "(not supported yet)".
- **Contradictions / likely inaccuracies:**
  - L502 Initial Export Only examples: "an initial password or API token". The page's own credential guidance (and `connected-systems.md` L385-412, `passwords.md` L179) says passwords must never flow as attributes; the glossary L66 repeats "Intended for initial passwords".
  - L399 "the equivalent null-coalescing operator `cs[\"jimBadgeColour\"] ?? cs[\"roomNumber\"]`": `??` appears nowhere in the Expressions operator reference.
  - L64 scoping example `cs["objectClass"] = "user" AND cs["employeeType"] = "FTE"` vs L801 `Eq(cs["employeeType"], "FTE")`; L77 says criteria "use the JIM expression language", but scoping criteria are built from attribute/operator/value groups in the editor.
  - L347 "This is the only password JIM stores" (see passwords.md).
  - L852 "No Contributor outcomes" vs `attribute-priority.md` L87 "Value cleared".
  - L189 "The five behaviour toggles" never lists all five.
- **One-home breaches:** Attribute Priority (L740-773) restates `concepts/attribute-priority.md`; Initial password (L319-391) restates `concepts/passwords.md` L12-72; Missing Input Behaviour (L421-432) restates `expressions.md` L290-317.
- **Missing reader needs:** the "Common workflows" (L775-790) and the worked example (L792-810) sit after 770 lines of detail; the Generated Values topic alone is ~5,000 words with no "if you only need unique account names, do this" summary up front.
- **Exemplary parts:** L221-223 "Matching mistakes do not fail ... both are found later by a person"; L544-550 rename warning with two concrete protections; L650 "**Is anything lost?** No."; L841-846 what deletion keeps and removes.
- **Mechanical:** "enforce state" (L53, L505), "scoping criteria" (L14, L57), "metaverse" lower-case 17 times, "connector space" lower-case 6 times; L163-185, L189-244 hard-wrapped source.
- **Worst + rewrite:** L397. Rewrite: "A Synchronisation Rule can write each attribute only once, so JIM refuses a second Attribute Flow to the same target. To fall back from one source attribute to another, use a single expression; to choose between two systems, use Attribute Priority."
- **Second worst:** L294. Rewrite: move to the release's upgrade notes in `administration/upgrading.md`; it describes a one-off migration, not how JIM behaves.
- **Recommendation:** split (see synthesis).

### `docs/reference/glossary.md` (1,942 words)

- **Mode:** reference; matches. It also feeds the in-product info icons (`synchronisation-rules.md` L25), which raises the cost of errors.
- **Fit:** 3.
- **Developer-speak:** L15 "who changed a configuration entity ... every administrator-mutable configuration type"; L24 "an HR row"; L120 "Nothing is staged, persisted or exported".
- **Factual errors / contradictions:**
  - L87 Partition: "such as organisational units in an LDAP directory". `connected-systems.md` L93-94: "Partitions and organisational units (OUs) are distinct ... an OU ... is modelled in JIM as a container."
  - L36 Delta Synchronisation: "at the cost of relying on the Connected System's own change tracking". That describes Delta Import; Delta Synchronisation works from changes already in the Connector Space.
  - L39 Deprovisioning: "removing or disabling accounts" (no Disable action exists, per `jml-lifecycle.md` L106).
  - L66 Initial Export Only: "Intended for initial passwords, one-time tokens" (conflicts with the password channel guidance).
  - L102 Run Profile: "page size" vs `run-profiles.md` L21 "Batch size".
  - L54 Full Synchronisation "processes every Connected System Object in scope" vs `sync-preview.md` L37.
- **History:** none serious ("previously present" L84 is fine).
- **Missing terms** (used across these pages without a glossary entry): Container, Deletion Rule, Delta Import / Full Import (only under Import), Enforce State / Drift Detection, Generated Value, Retired value, Lineage, Missing Input Behaviour, Object Type, Out-of-Scope Action, Parked, Password Delivery Service, Pending Deletion, Predefined Search, Run Profile Execution Item, Scheduler, Worker, Service Setting, Role, API Key, Data Flow.
- **Mechanical:** L123 "metaverse", L126 "scoping criteria" lower-case.
- **Worst + rewrite:** L87. Rewrite: "**Partition**: a top-level division a directory defines, such as an Active Directory domain or an OpenLDAP naming context. You choose which partitions JIM manages on the Connected System's Scope tab; organisational units within a partition are Containers."

---

## 3. Synthesis

### 3.1 Top 8 systemic patterns (ranked by frequency x reader harm)

**1. Explaining the mechanism instead of the consequence (15 pages; high harm).**
Pages describe how JIM's code, engine, worker or database achieves something, where the reader needs what it means for their data and what to do. This is the core of issue #1454.
Affected: architecture, synchronisation-pipeline, passwords, attribute-priority, expressions, activities, connected-systems, synchronisation-rules, metaverse, schedules, service-settings, operations, run-profiles, predefined-searches, certificates.
Examples: `connected-systems.md` L72 ("Clearing records, inside the same transaction ... arms the stranded-value sweep"); `synchronisation-rules.md` L397 ("the synchronisation engine evaluates one mapping per target attribute"). Also `schedules.md` L136 "hot paths", `architecture.md` L109 `NOTIFY`/`LISTEN`.

**2. Changelog voice: history, upgrade and roadmap narration (16 pages; high harm).**
"Now", "no longer", "until now", "before this release", "records written before this existed", "planned for a future release", "not yet". The reader cannot tell current behaviour from history, and every such sentence goes stale. `CHANGELOG.md` and `administration/upgrading.md` are the homes for this.
Affected: activities (10+ instances), synchronisation-rules (7), connected-systems (6), metaverse (4), passwords (5), operations, object-naming, attribute-priority, jml-lifecycle, roles, schedules, expressions, api-keys, glossary, index, first-synchronisation.
Examples: `operations.md` L18 ("Until now the only sign either was alive ... Each service now writes a heartbeat"); `synchronisation-rules.md` L294 ("Changes saved before this release ... switch Provision to Connected System off and save, then switch it on and save again").

**3. Contradictions and duplicated facts across pages (12 pages; high harm, because it destroys trust).**
The "one home per fact" rule is breached repeatedly, and the copies have drifted.
Duplications: deletion rules and grace periods (jml-lifecycle + metaverse); Attribute Priority (concept + synchronisation-rules); initial passwords (passwords + synchronisation-rules); policy discovery and Set Password (passwords + connected-systems); "provisioned targets are connectors" (metaverse + sync-preview); Service Settings (configuration + administration).
Resulting contradictions: Disable deprovisioning exists (`jml-lifecycle.md` L168, glossary L39) vs is planned (`jml-lifecycle.md` L106); Partition = OU (glossary L87) vs not (`connected-systems.md` L93); Full Synchronisation processes everything (pipeline L91, glossary L54) vs skips unchanged objects (`sync-preview.md` L37); Roles UI absent (`roles.md` L92) vs present (L109); "the only password JIM stores" (`passwords.md` L62, `synchronisation-rules.md` L347) vs queued passwords held up to a year (`passwords.md` L314); Initial Export Only for passwords (glossary L66, `synchronisation-rules.md` L502) vs the credential block; `activities.md` L49 "found missing but marked as deleted" (should be "not marked"); batch size vs page size; `person`/`group` vs `User`/`Group`.
Examples: `jml-lifecycle.md` L106 vs L168; `reference/glossary.md` L87.

**4. Inline API and PowerShell field reference in concept and configuration prose (13 pages; medium-high harm).**
REST routes, JSON property names, HTTP status codes and PowerShell object properties written into sentences. It bloats the pages, breaches "Do not duplicate reference content" (`docs/CLAUDE.md` L19), and is exactly the drift-prone material `docs/CLAUDE.md` L20 warns about. A "Do it from automation" box per page, linking to the cmdlet and the Scalar reference, would serve the reader better.
Affected: passwords, activities, connected-systems, synchronisation-rules, schedules, metaverse, operations, run-profiles, sync-preview, data-flow, attribute-priority, expressions, configuration-changes.
Examples: `passwords.md` L229 (`200`/`202`, `origin`, `settled`, `attemptCount`, `nextAttemptAt`); `schedules.md` L92 (seven `LastExecution*` properties).

**5. Pages shaped like the data model or the API, not the reader's task (12 pages; medium-high harm).**
Headings walk the object's properties ("Prefix", "Enabled flag", "Value types", "Default vs override", "Stable URI slug"); workflows are written as API calls ("capture the returned activity ID", "update the search with the enabled flag set to false"); pages open with a definition instead of "you need this when ..."; the task the reader came for is at the bottom.
Affected: api-keys, certificates, predefined-searches, service-settings, roles, run-profiles, schedules, activities, connected-systems, synchronisation-rules, metaverse, configuration/index.
Examples: `predefined-searches.md` L51-52; `connected-systems.md` L645 (setup steps at line 645 of 708).

**6. Unexplained jargon, abbreviations and implementation vocabulary (15 pages; medium harm).**
CSO/MVO as prose abbreviations (jml-lifecycle 29, synchronisation-rules 21, synchronisation-pipeline 18, connected-systems 9) despite the vocabulary rule limiting them to tight headers with tooltips; class names (`MetaverseObject`, jml L25, pipeline L55); internal coinages presented as if the reader knows them ("stranded-value sweep", "post-clear reconciliation gate", "armed", "fenced", "flush", "hot paths", "object graph", "evaluator", "principals", "first-class", "persisted", "throws", "MudBlazor"); non-product names for product things ("join rules", "Attribute Flow Rules").
Affected: jml-lifecycle, synchronisation-pipeline, synchronisation-rules, connected-systems, run-profiles, metaverse, activities, schedules, sync-preview, roles, predefined-searches, configuration/index, api-keys, quickstart, glossary.
Examples: `jml-lifecycle.md` L25; `run-profiles.md` L79.

**7. Mode-mixing mega-pages (9 pages; high harm per page).**
Five pages hold about 51,000 of the 87,000 words. Each mixes explanation, UI tour, how-to, reference, API reference and troubleshooting, so the reader cannot tell what kind of page they are on or where the task is. Pages: synchronisation-rules (17,791), connected-systems (13,460), activities (7,672), passwords (7,362), metaverse (5,033), expressions (4,817), attribute-priority (3,118), schedules (2,997), synchronisation-pipeline (2,740, with the only first-run procedure inside a concept page).
Examples: `activities.md` L83 (one 600-word bullet describing UI rings and markers); `concepts/passwords.md` (explanation, REST reference and Operations tab in one page).

**8. Broken first-run journey (7 pages; very high harm for new implementers).**
The tutorial does not exist (`first-synchronisation.md`, 63 words); Getting Started promises it; Quick Start sends the administrator to "Example Data" and stops; prerequisites and quick start spend about half their words on contributor tooling; the only run-order guidance is inside a concept page (`synchronisation-pipeline.md` L147-162); the Configuration nav starts with Configuration Changes and Sync Preview, before Connected Systems; the home page has no Configuration card.
Examples: `getting-started/first-synchronisation.md` L7-8; `getting-started/quickstart.md` L67.

**Mechanical breaches** (below the top 8 in harm but the widest spread): ` -- ` dash substitutes in 21 pages; "metaverse" lower-case in 17 pages; "connector space" lower-case in 7; other domain nouns lower-case in 9; passive refusals in 9. No em dashes, no "Sync Rule", no American spellings anywhere in scope.

### 3.2 Exemplar pages (and why)

- **`concepts/case-sensitivity.md`** (the best page in scope). One-sentence principle first; tells the reader why it matters to them; tables answer "can I change it?"; names a failure (export confirmation on a case-folding target), says what is not lost, and gives the remedy in the next paragraph.
- **`configuration/data-flow.md`**. Opens with the reader's question in their own words ("where does this person's job title actually come from?"), says what the view will not do (read-only) and where to go instead, explains filters by the purpose each serves.
- **`configuration/configuration-changes.md`** (opening and "Three things are worth knowing", L68-72). Leads with stakes ("Others decide whether accounts are deleted"), states what survives a cancel, and is honest about the limits of a preview.
- **`configuration/sync-preview.md`**. Conditional framing throughout and an explicit "What the preview cannot know" section.
- **Exemplary sections inside weaker pages**, worth using as models when splitting: `run-profiles.md` L51-81 (safeguards: what survives, recommended values, and the "second unlimited Run Profile" trade-off); `operations.md` L34-40 (status table with meaning and remedy); `schedules.md` L46-76 ("When a step fails", ending "Nothing is lost"); `concepts/passwords.md` L30-36 (states table with "What you do") and L318-323 (the encrypted-connection warning `docs/CLAUDE.md` itself cites); `synchronisation-rules.md` L221-223 (matching mistakes "do not fail") and L544-550 (rename warning with two concrete protections).

### 3.3 Split, merge and move recommendations

**Split**

- `configuration/synchronisation-rules.md` into: (a) **Synchronisation Rules** (direction, scope, matching, projection and provisioning, deprovisioning, deleting a rule); (b) **Attribute Flow** (mappings, Missing Input Behaviour, value processing, Initial Export Only, derived attributes); (c) **Generated Values** (generation, availability checks, collision remediation, retired values: ~5,000 words, a page of its own). Delete the Attribute Priority section (link the concept page) and the Initial Password section (one home with passwords). Move L294 to upgrade notes.
- `configuration/connected-systems.md` into: (a) **Connected Systems** overview with the setup procedure first; (b) **Scope** (partitions, containers, Advanced Mode, scope previews); (c) **Schema** (selection, refresh, writability, data types, credential attributes); (d) **Removing a Connected System** (delete options and deletion preview). Move the Passwords tab content to one passwords how-to.
- `configuration/activities.md` into: (a) **Activities** reference (statuses with next steps, counters, filters, retention); (b) **Investigating what happened** how-to (Lineage, Timeline, Table, causes); (c) **Configuration Change History** as its own page (it is linked from every configuration page and the home page). Move "Delta Import change source checks" (L57-71) to the LDAP Connector page. Delete the release-notes paragraphs.
- `concepts/passwords.md`: keep a ~2,000-word concept page; move "Watching the queue" to `configuration/operations.md`, set-password procedures to a passwords how-to, REST and PowerShell detail to the API and PowerShell pages, Password Delivery Service internals to developer docs.
- `concepts/expressions.md`: function and operator tables to a Reference page (`reference/expression-functions.md`); "Common Scenarios" to a how-to; keep "Where expressions fit" and the null semantics in Concepts.
- `getting-started/quickstart.md` and `prerequisites.md`: move the "For Developers" sections to `developer/dev-environment.md`.

**Move**

- `concepts/architecture.md` "Layered Architecture", "Real-Time Coordination" internals and the worker-components diagram to `developer/architecture.md`. Replace with what operators need: which containers hold state, what must reach what, what to back up.
- `concepts/synchronisation-pipeline.md` "Initialising JIM" into `getting-started/first-synchronisation.md` (or its own how-to), linked from the concept page. Move the content-hash and "Applying Exported Changes Straight Away" sections to developer docs, leaving a one-paragraph consequence and the Verification Mode pointer.

**Merge / pick one home**

- Deletion Rules and grace periods: one home in `configuration/metaverse.md`; `jml-lifecycle.md` links to it.
- `configuration/service-settings.md` and `administration/configuration.md#service-settings`: merge, and document configuration change tracking there (the link from `configuration-changes.md` L90 currently goes nowhere).
- Previews: the "Previewing a ..." sections scattered across connected-systems, synchronisation-rules and metaverse share one mechanism already described in `configuration-changes.md`; keep each page's "what it reports" table and link the shared behaviour (stale previews, Activity linkage, automation) to one place.

**Write**

- `getting-started/first-synchronisation.md`: the missing tutorial (HR file to directory, or Example Data to a File Connector target), ending with how to verify the result. This is the single highest-value fix in scope.

**Structure**

- `configuration/` has no Diátaxis mode in `docs/CLAUDE.md`. Decide what it is (the self-description in `configuration/index.md` L51-58 is a hybrid) and record it, or the mixing will continue.
- Reorder the Configuration nav to the order an implementer works in: Connected Systems, Synchronisation Rules, Run Profiles, Schedules, Metaverse, then previews and the supporting objects.
- Add a Configuration card to the home page Quick Links.
- Fix the glossary errors (Partition, Delta Synchronisation, Deprovisioning, Initial Export Only, Run Profile) and add the missing terms, since the in-product info icons link to it.
