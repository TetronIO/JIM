# Public Documentation Standard: Implementation Plan

- **Status:** Doing (Phases 0 and 1 complete)
- **Issue:** [#1454](https://github.com/TetronIO/JIM/issues/1454)
- **PRD:** [`PRD_PUBLIC_DOCUMENTATION_STANDARD.md`](../../prd/doing/PRD_PUBLIC_DOCUMENTATION_STANDARD.md)
- **Evidence:** [`engineering/notes/docs-audit-2026-10/`](../../notes/docs-audit-2026-10/README.md)

## Overview

The PRD defines what the public documentation must become and why: one specification with rule IDs, a declared type and audience for every page, mechanical checks that fail a pull request on the defects a machine can see, source-generated coverage inventories, an "integrate, don't append" rule for changes, a tutorial the release suite executes, and about 15 priority pages held to a stricter bar. This plan is how it gets there, in six phases, each delivered as one or more ordinary pull requests on top of `main`.

The ordering principle: **stop new defects before fixing old ones, and fix the pages that decide adoption before the rest.** Phase 0 corrects what is factually wrong and makes links checkable at pull request time; Phases 1 and 2 build the standard and the checks; Phase 3 delivers the priority pages and the tested tutorial; Phase 4 migrates the remaining folders; Phase 5 moves contributor content out and closes the issue.

## Business Value

Administrators in high-trust environments decide whether to trust JIM with production partly by reading its documentation. Today that documentation is accurate in its best pages and wrong, contradictory or developer-facing in many others (see the PRD's Problem Statement). Every correction lands on the live site the moment it merges, so value arrives per phase, not at the end.

## Technical Architecture

### Current state (before Phase 0)

- **Checks:** `changelog-lint` runs five checks, of which two touch docs: docs coupling (a ✨/🔄 entry must change `docs/`) and the parameter-alias lint (`Lint-DocExamples.ps1`). `mkdocs build --strict` runs only after merge (`docs.yml`), with anchors at INFO, so a broken anchor publishes.
- **Rules:** the house voice lives in `docs/CLAUDE.md` ("Writing for the Reader"), read only by Claude sessions working under `docs/`.
- **Structure:** no page declares a type or audience; `configuration/` has no Diátaxis mode.

### Target state

```
                 pull request
                      |
              changelog-lint job (required; no new required check)
                      |
   +---------------+--+-------------+------------------+------------------+
   |               |                |                  |                  |
Lint-Changelog  Lint-DocsCoupling  Lint-DocExamples   Test-DocsConformance  Test-DocsSite
                                   (aliases, unknown  (this standard's    (strict build +
                                    cmdlets and        mechanical rules)   lychee)
                                    parameters)              |
                                              +--------------+-------------+
                                              |              |             |
                                          structure      language      coverage
                                          (front matter, (vocabulary,  (inventories
                                           page shape,    issue refs,   from source,
                                           size, a11y)    time words)   both ways)
                                              |
                       shrink-only baseline  +  in-page exception markers  +  conformance config
                       (rule, page, count)      (reason required)             (tiers, priority and
                                                                               conformant pages)
```

Advisory, non-blocking, outside the required job: `docs-review.yml` (rubric review; review threads on priority pages), and a weekly docs health workflow (external links, staleness, coverage drift). Release time: the tutorial scenario in the `-PreRelease` suite, and `/release` listing priority pages due for validation.

All checks read Markdown sources and built HTML, so they survive a change of generator ([#2058](https://github.com/TetronIO/JIM/issues/2058)).

## Implementation Phases

### Phase 0: Correct the facts, and check links at pull request time ✅

Delivered on `claude/bold-gauss-yt9ekg`.

- **Factual corrections** across 27 pages, each verified against source: the non-existent `-All` parameter (docs and five of the module's own `Get-Help` examples), three non-existent cmdlet names, the wrong REST route in every authentication example, the broken Scalar link, two malformed front matter blocks, Kubernetes presented as a platform JIM supports, the Disable Deprovisioning Action described as shipping, Partition defined as an organisational unit, Full Synchronisation described as processing every object, a Roles portal area that does not exist, "the only password JIM stores", Initial Export Only offered for passwords, the withheld-deletions counter described backwards, File Connector paths outside `/connector-files`, Culture offered as the fix for an encoding problem, two contradictory SCIM trust remedies, cmdlet counts, the `-All` page cap, camelCase output tables the module never returns, `-ChangeReason` missing from the Schedule cmdlets, and the remaining contradictions the audit listed.
- **`Lint-DocExamples.ps1` extended (rule ACC-2):** a JIM cmdlet named anywhere in a page must exist, and every parameter an example passes must bind (declared, common, or `-WhatIf`/`-Confirm` under ShouldProcess; abbreviations and common-parameter aliases are reported). Covers docs and the module's own `.EXAMPLE` help; command aliases are read from `JIM.psm1`. Ten new Pester tests, written red first.
- **`Test-DocsSite.ps1` (rules LNK-1, LNK-2)** in `changelog-lint`, path-filtered to docs sources: `mkdocs build --strict` with `anchors`, `absolute_links` and `omitted_files` raised to warnings, then lychee 0.24.2 (official image, digest-pinned) offline over the built site. 20,802 links checked in under a second.
- `overrides/` excluded from the published files; stale "no link checker in CI" statements corrected in `docs/CLAUDE.md`, the root `CLAUDE.md` and `docs.yml`; lychee recorded as row 10 of `engineering/DEPENDENCY_PINNING.md`.

### Phase 1: The standard and the conformance framework ✅

Delivered on `claude/bold-gauss-yt9ekg` (second pull request).

- **`engineering/DOCUMENTATION_STANDARD.md`:** audiences; page types and their required shapes, including `object`; front matter and structure (STR); voice (VOI, SAFE, HOME: the "Writing for the Reader" rules, moved unchanged in substance); language (LANG); maintenance (MNT, including integrate-don't-append); accuracy and links (ACC, LNK); accessibility (A11Y); style (STY); priority pages and tutorials (PRI, TUT); coverage (COV, TERM); and a rule index giving every rule its tier and checker. Rules not yet checked say so (Review, or Planned with their phase).
- **`docs/CLAUDE.md`** reduced to site mechanics plus a "Before you change a page" pointer; the root `CLAUDE.md`, `engineering/CLAUDE.md` and `src/JIM.PowerShell/CLAUDE.md` point at the standard; the PR template's Documentation section asks for MNT-1 and MNT-2; `engineering/prompts/api-docs-section.md` retired.
- **`scripts/Test-DocsConformance.ps1`** with 42 Pester tests written red first: STR-1 to STR-8, MNT-3, A11Y-1, A11Y-2, TYP-1 and EXC-1; tiers from `docs/.conformance/config.psd1`; the shrink-only baseline (`docs/.conformance/baseline.tsv`, `-UpdateBaseline`, `-SeedBaseline`, `merge=union` with the lower count winning); in-page exception markers; conformant and priority pages; annotations and a job-summary table. About three seconds over the whole site, so it runs on every pull request.
- **Seeded** with 81 STR-2 entries (258 missing front matter keys) and the two stub pages (STR-6). Everything else except MNT-3 blocks from day one, after fixing what it found: ten pages given one name (title, H1 and nav label), seven code blocks given a language, and one heading skip excused in place until the PowerShell pages migrate.

Differences from the plan as first written:

- **No single orchestrator.** `Lint-DocExamples.ps1` and `Test-DocsSite.ps1` stay separate `changelog-lint` steps rather than running under `Test-DocsConformance.ps1`: the site check needs MkDocs and Docker, and each step's annotations stay attributable.
- **The conformance data lives in `docs/.conformance/`**, which MkDocs never publishes because the folder starts with a dot.
- **STR-3 accepts a nav label that drops leading words of the page's name** ("Authentication" under API for "API Authentication"), after the first run showed the stricter rule flagging legitimate section-qualified names.
- **A11Y-1 blocks rather than ratchets:** every one of the site's 53 Mermaid diagrams is on a contributor page leaving in Phase 5, so customer pages already conform.
- **MNT-3 warns until Phase 5 rather than blocking now.** Eleven pages are already over budget, among them the Synchronisation Rules, Connected Systems and PowerShell pages most features document into. Replayed against `main`, the check failed the first Generated Value Forms pull request ([#2062](https://github.com/TetronIO/JIM/pull/2062)) on both Synchronisation Rules pages, so blocking would have failed most feature pull requests, or pushed authors to cut useful text to make room, until those pages are split.
- **No exception marker can excuse MNT-3.** It compares with the base branch, so a marker excusing growth suppresses nothing once that growth merges, and EXC-1 would then fail every later pull request.

### Phase 2: Coverage and language

Six pull requests, in this order, each to the full standard (tests first, then the debt it finds fixed or baselined):

- **2a, language and vocabulary.** `docs/.conformance/vocabulary.psd1`: per glossary entry, its home page, rejected variants and whether Title Case is enforced. The glossary stays the single source of each term's canonical form and anchor, and the vocabulary is checked against it both ways (TERM-1), so the portal's `TermDefinitions` are reconciled transitively through `GlossaryTermConsistencyTests`. Checks for LANG-1 to LANG-8 in `Test-DocsConformance.ps1`. LANG-9 stays with the reviewer: a check would have to list competitors' product names, which the same rule keeps out of code. (The plan first named `docs/vocabulary.yml`; a PowerShell data file needs no YAML parser, and the dot folder keeps it off the site.) **Delivered:** the checks, with 26 new tests; the blocking debt fixed (142 dashes standing in for a dash or a range, one American spelling, ten time-relative phrases rewritten or anchored to a release, ten promises removed, four issue numbers); and 421 findings baselined (254 lower-case product nouns, 129 prose abbreviations, 38 minimisers).
- **2b, coverage checked by script:** cmdlets (COV-1), environment variables (COV-2), product deep links into the docs (LNK-3) and expression functions (COV-6), with one reasoned allow-list, and the gaps they find documented. **Delivered:** `scripts/Test-DocsCoverage.ps1` with 16 tests, run by `changelog-lint` on every pull request (about seven seconds), configured and allow-listed in `docs/.conformance/coverage.psd1`. It found 26 gaps: a reference page for `Get-JIMDataFlow`, the installer's 13 unattended-install variables (the Configuration Reference had pointed readers at the script's header), `JIM_CACHE_WARM_PARALLELISM` and `ToDecimal` were documented, and ten build, benchmark and test-harness variables allow-listed with their reasons. All 22 links from the product into the docs resolve. Environment variables are read from the product's own reads (`Constants.Config` and literal `GetEnvironmentVariable` calls) rather than every `JIM_` string, which also names SQL aliases.
- **2c, the API:** REST routes in examples (ACC-3) and resource groups (COV-4). **Delivered:** both rules in `Test-DocsCoverage.ps1`, with 11 more tests, run by `build-and-test` against the OpenAPI document it generates from its own build (about four seconds), since `changelog-lint` has no build to generate it from. A route with a method must be a whole route that takes that method; a path without one names a resource, so it may stop short of a whole route. An abbreviated route (`/connected-systems/{id}/clear`) fails, naming the full route to write: 22 of them were rewritten in full, and every other route the docs give was right. `docs/api/index.md` gained a Resource groups table naming all 23 groups and the page that explains each.
- **2d, coverage checked by NUnit:** Service Settings (COV-3, with every key seeded, which [#2060](https://github.com/TetronIO/JIM/issues/2060) fixes or allow-lists) and connector settings (COV-6), beside `GlossaryTermConsistencyTests`. **Delivered:** in `test/JIM.Worker.Tests` rather than beside `GlossaryTermConsistencyTests`, because the inventories are internal there: `SeedingServer.BuiltInConnectors()` (so a new Connector is checked with no list to update) and the seeding harness. Nine tests: COV-3 and the Connector settings of COV-6 checked both ways, with stale allow-list entries failing, and every key in `Constants.SettingKeys` created by the built-in configuration pipeline. They read the same allow-list as `Test-DocsCoverage.ps1`. The Configuration Reference's Service Settings table went from 13 of 34 keys to every setting that changes how JIM behaves (29); the LDAP page named one setting differently from its form, and the SCIM page folded six settings into three rows. Five keys are allow-listed against the issues that fix them: `Worker.StaleTaskTimeout` is never seeded ([#2060](https://github.com/TetronIO/JIM/issues/2060)), and four seeded settings do nothing or show the wrong value ([#2101](https://github.com/TetronIO/JIM/issues/2101)).
- **2e, portal docs links:** a docs-page parameter on `PageInfo` for every portal page (COV-5), a portal-only display affordance with no REST or PowerShell surface; a mock is agreed before building. **Delivered:** `PageInfo` takes `DocsPage`, the Markdown path of the page that covers it, and its popover links there (**Documentation**) before any Glossary link; the portal turns the path into the site's address (`DocsSite.PageUrl`). Naming the Markdown file rather than a URL is what lets COV-5 check it from source, in `Test-DocsCoverage.ps1` with seven more tests: every page with an `@page` directive names one or is allow-listed, and every page and heading named exists, including each literal an expression can choose (the Connector page links each built-in Connector to its own page). Of the 40 pages, 28 now link their docs, twelve of them detail pages that gained an info button; twelve are allow-listed: the two landing pages, three developer specimen pages, and seven pages for features no docs page covers yet ([#2114](https://github.com/TetronIO/JIM/issues/2114)).
- **2f, tooling:** `tooling-pin-check` extended to Docker Hub images, so the lychee pin is watched like the npm and NuGet pins; and `docs-review.yml`, the advisory rubric review on the already-pinned Claude Code action, with read-only tools, one summary comment citing rule IDs, and never a required check.

Decided by the product owner on 2026-10-11, from a census of the current pages and inventories:

1. **Small language debt is fixed, then blocks:** spaced ` -- ` dashes (134), American spellings (1), promises about the future (13), issue numbers (4) and unambiguous time-relative phrases (10). The larger debt ratchets: lower-case product nouns (about 160), CSO, MVO and RPEI in prose (138), and "simply".
2. **Patterns that are mostly false positives are left to the advisory review rather than warned on:** "now", "currently", "no longer", "just", "easy", and the single-word product nouns that are also ordinary English (Activity, Schedule, Export). At 85 to 90% false positives, a warning teaches authors to ignore warnings.
3. **API resource groups (COV-4) are named in one table on `docs/api/index.md`,** linking into the generated reference, rather than one overview page per OpenAPI tag: the 23 tags are controller names, and one of them holds 112 of the 282 operations.
4. **REST routes (ACC-3) are checked against the real OpenAPI document,** generated in `build-and-test`, not re-derived from controller attributes; and examples give routes in full (`/api/v1/...`), so the 22 abbreviated ones are rewritten.
5. **Portal docs links (COV-5) are portal-only,** being a display affordance.
6. **The unwatched Trivy pin and the drifted PostgreSQL pins are not docs work;** filed as [#2075](https://github.com/TetronIO/JIM/issues/2075).

### Phase 3: The priority wave

- **The tested tutorial:** `getting-started/first-synchronisation.md` rewritten as an HR file in, through the Metaverse, out to a second file, File Connector only; each step in portal and PowerShell tabs; machine-readable checkpoint markers; the sample file published under `docs/assets/`; the next scenario number, directory-agnostic, reading its steps from the page and asserting its checkpoints, registered in `-PreRelease`; `scenario:` front matter checked.
- **A synchronisation section for `administration/troubleshooting.md`**, in the troubleshooting shape, and the stale "in progress" placeholder removed.
- **The remaining priority pages migrated**, including splits of `configuration/synchronisation-rules.md`, `configuration/connected-systems.md` and `connectors/jim-ldap-connector.md`; each carries `validated:`; stricter budget and blocking warnings switched on as each lands.
- `/release` lists priority pages due for validation; `engineering/RELEASE_PROCESS.md` gains the step.

### Phase 4: Folder migrations

One pull request per folder, each ending with every page declaring its type, an empty baseline and no page over its size budget, then marked conformant: `getting-started/`, `administration/`, `configuration/`, `concepts/`, `connectors/`, `api/`, then `powershell/` once [#1083](https://github.com/TetronIO/JIM/issues/1083) has decided generation (native blocked-by link recorded on #1454).

### Phase 5: Contributor content, promotion, close

- Customer-relevant behaviour in `docs/developer/diagrams/` (Pending Export states, deletion grace periods, Schedule step failure) rewritten into `concepts/`; the 16 contributor pages moved to `engineering/`; a small "Extending JIM" section kept for connector authors.
- Warnings whose baseline has reached zero and that ran a release without a false positive promoted to blocking, and MNT-3 promoted to Block now that no page is over budget.
- PRD and this plan moved to `done/` in the pull request that closes #1454.

## Success Criteria

The PRD's acceptance criteria, unchanged. In short: the standard exists with rule IDs; every page declares type and audience and matches its shape; the blocking tier fails pull requests; the baseline only shrinks; coverage is generated and checked both ways; the audit's factual defects are corrected; the tutorial is executed every release; the priority pages are migrated and validated each release; every customer folder is conformant.

## Benefits

- **Trust:** wrong examples, contradictions and dead links stop reaching the live site, because they fail the pull request that introduces them.
- **Adoption:** a new administrator can reach a working synchronisation from the docs alone, on the release they installed.
- **Authoring cost:** authors and agents learn the standard from rule IDs at pull request time, not from review comments.

## Dependencies

- [#1083](https://github.com/TetronIO/JIM/issues/1083) before the `powershell/` migration (native blocked-by link on #1454).
- [#2058](https://github.com/TetronIO/JIM/issues/2058) (generator end of support): not blocking, by design.
- [#2060](https://github.com/TetronIO/JIM/issues/2060) (an unseeded Service Setting): the Phase 2 Service Settings coverage check either finds it fixed or allow-lists it with that reference.
- Approved new dependency: lychee (Decision 2). No other.

## Risks & Mitigations

| Risk | Mitigation |
|------|------------|
| Rules that fire on legitimate text train people to ignore them | Only proven defect classes block; anything with an unfixable false positive is demoted; exceptions are in-page markers with a reason |
| Parallel sessions edit `docs/` constantly, so a baseline file conflicts or blocks unrelated work | One sorted line per rule and file; counts, not line numbers; ratchets block only new debt; migrations go a folder at a time |
| The migration stalls part-way, as "existing pages will migrate over time" did | Each phase lands value on its own; folder flags and the baseline make progress visible and irreversible |
| Splitting large pages breaks inbound links | The parent keeps its URL; every product deep link into the docs is checked |
| `CHANGELOG.md` is in the docs path filter, so the site check runs on most pull requests | Expected to add about a minute (pip install, build, link check); measure on the first pull requests and narrow the filter if it costs more |
| The tutorial scenario adds to the pre-release suite | Directory-agnostic, so it runs once per pass; file-to-file needs no directory start-up |
| Material for MkDocs leaves maintenance | Every check is generator-agnostic; #2058 decides the generator |
