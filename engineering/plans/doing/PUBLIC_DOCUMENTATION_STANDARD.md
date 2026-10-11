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
- **`scripts/Test-DocsConformance.ps1`** with 41 Pester tests written red first: STR-1 to STR-8, MNT-3, A11Y-1, A11Y-2, TYP-1 and EXC-1; tiers from `docs/.conformance/config.psd1`; the shrink-only baseline (`docs/.conformance/baseline.tsv`, `-UpdateBaseline`, `-SeedBaseline`, `merge=union` with the lower count winning); in-page exception markers; conformant and priority pages; annotations and a job-summary table. About three seconds over the whole site, so it runs on every pull request.
- **Seeded** with 81 STR-2 entries (258 missing front matter keys) and the two stub pages (STR-6). Everything else blocks from day one, after fixing what it found: ten pages given one name (title, H1 and nav label), seven code blocks given a language, and one heading skip excused in place until the PowerShell pages migrate.

Differences from the plan as first written:

- **No single orchestrator.** `Lint-DocExamples.ps1` and `Test-DocsSite.ps1` stay separate `changelog-lint` steps rather than running under `Test-DocsConformance.ps1`: the site check needs MkDocs and Docker, and each step's annotations stay attributable.
- **The conformance data lives in `docs/.conformance/`**, which MkDocs never publishes because the folder starts with a dot.
- **STR-3 accepts a nav label that drops leading words of the page's name** ("Authentication" under API for "API Authentication"), after the first run showed the stricter rule flagging legitimate section-qualified names.
- **A11Y-1 blocks rather than ratchets:** every one of the site's 53 Mermaid diagrams is on a contributor page leaving in Phase 5, so customer pages already conform.

### Phase 2: Coverage and language

- `docs/vocabulary.yml` (excluded from the build): canonical form, rejected variants, glossary anchor and home page per product noun; reconciled with `TermDefinitions`; drives glossary coverage, Title Case and rejected-synonym rules.
- Coverage inventories from source, checked both ways, with one reasoned allow-list: cmdlets (headings on `docs/powershell/`), environment variables, Service Settings (with an NUnit assertion that every key is seeded, which [#2060](https://github.com/TetronIO/JIM/issues/2060) fixes or allow-lists), REST routes in examples against the OpenAPI document, OpenAPI tags to `docs/api/` pages, connector settings and expression functions (NUnit, beside `GlossaryTermConsistencyTests`), every `docs.junctional.io` link in source, and portal pages through a docs-page parameter on `PageInfo` (a portal-only display affordance, so no REST or PowerShell surface).
- Language rules: issue numbers, time-relative words, future promises, minimisers, prose abbreviations, em dashes, American spellings.
- `tooling-pin-check` extended to Docker Hub images, so the lychee pin is watched like the npm and NuGet pins.
- `docs-review.yml`: the advisory rubric review on the already-pinned Claude Code action, read-only tools, citing rule IDs.

### Phase 3: The priority wave

- **The tested tutorial:** `getting-started/first-synchronisation.md` rewritten as an HR file in, through the Metaverse, out to a second file, File Connector only; each step in portal and PowerShell tabs; machine-readable checkpoint markers; the sample file published under `docs/assets/`; the next scenario number, directory-agnostic, reading its steps from the page and asserting its checkpoints, registered in `-PreRelease`; `scenario:` front matter checked.
- **A synchronisation section for `administration/troubleshooting.md`**, in the troubleshooting shape, and the stale "in progress" placeholder removed.
- **The remaining priority pages migrated**, including splits of `configuration/synchronisation-rules.md`, `configuration/connected-systems.md` and `connectors/jim-ldap-connector.md`; each carries `validated:`; stricter budget and blocking warnings switched on as each lands.
- `/release` lists priority pages due for validation; `engineering/RELEASE_PROCESS.md` gains the step.

### Phase 4: Folder migrations

One pull request per folder, each ending with every page declaring its type and an empty baseline, then marked conformant: `getting-started/`, `administration/`, `configuration/`, `concepts/`, `connectors/`, `api/`, then `powershell/` once [#1083](https://github.com/TetronIO/JIM/issues/1083) has decided generation (native blocked-by link recorded on #1454).

### Phase 5: Contributor content, promotion, close

- Customer-relevant behaviour in `docs/developer/diagrams/` (Pending Export states, deletion grace periods, Schedule step failure) rewritten into `concepts/`; the 16 contributor pages moved to `engineering/`; a small "Extending JIM" section kept for connector authors.
- Warnings whose baseline has reached zero and that ran a release without a false positive promoted to blocking.
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
