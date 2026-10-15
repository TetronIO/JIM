# Public Documentation Standard

- **Status:** Done
- **Note:** STR-7 and STR-8 stay Warn until a release has run with them clean; tracked under [#2247](https://github.com/TetronIO/JIM/issues/2247).
- **Created:** 2026-10-11
- **Author:** Drafted by Claude Code for product owner review
- **Issue:** [#1454](https://github.com/TetronIO/JIM/issues/1454)
- **Plan:** [`PUBLIC_DOCUMENTATION_STANDARD.md`](../../plans/done/PUBLIC_DOCUMENTATION_STANDARD.md)
- **Evidence:** [`engineering/notes/docs-audit-2026-10/`](../../notes/docs-audit-2026-10/README.md) (every page read in full; product surfaces inventoried from source)

## Problem Statement

JIM's public documentation is written for the people who build JIM more often than for the people who implement it. An administrator who opens a page usually needs three answers: what this is, what to do, and what it means for their data. Too many pages answer a fourth question instead: how JIM does it inside.

The October 2026 audit read all 99 authored pages (about 222,000 words on the 80 customer-facing ones) and measured where the gap actually is. It is not where the issue's wording suggests.

**The mechanical style rules are already met.** No em dashes, no "Sync Rule", about seven American spellings across the whole site. A prose linter enforcing more rules of that kind would make the docs no more useful to an administrator.

**The defects that hurt are substance, structure, accuracy and coverage:**

| Defect | Scale | Example |
|--------|-------|---------|
| Pages explain JIM's internals instead of the meaning to the reader | 15 of 31 core pages; 31 of 49 operations pages | "inside the same transaction ... arms the stranded-value sweep" (`configuration/connected-systems.md`) |
| Changelog voice: history, release and roadmap narration in reference and how-to pages | 16 of 31; 24 of 49 | "Until now ... Each service now writes a heartbeat" (`configuration/operations.md`); GitHub issue numbers on 4 customer pages |
| Sibling pages share no structure | 33 of 49 operations pages | The PowerShell pages use three heading schemes and four ways of presenting `-WhatIf` and `-Confirm`; the three SSO guides share no template |
| Pages too long to use, mixing every Diátaxis mode | 9 core pages; the largest five hold 51,000 of 87,000 words | `configuration/synchronisation-rules.md` is 17,800 words; the setup steps in `configuration/connected-systems.md` start at line 645 of 708 |
| The same fact told twice, and the copies now disagree | 12 core pages | The glossary says a Partition is an organisational unit; the Connected Systems page says it is not. The JML page says a Disable action is planned, then its worked example disables accounts |
| Examples and names that do not work | 15 operations pages | `Get-JIMConnectedSystem -All` and `Get-JIMSyncRule -All` (no such parameter); `Invoke-JIMRunProfile`, `Invoke-JIMSchedule` and `New-JIMCertificate` (no such cmdlets); every REST example in `api/authentication.md` uses a route that is not the controller's |
| Missing journeys | 2 pages | `getting-started/first-synchronisation.md` is a 63-word stub, so no page takes a new administrator from install to a working synchronisation; `administration/troubleshooting.md` has no synchronisation section at all |
| Missing terms and settings | See Coverage below | About 25 product nouns used in the docs have no glossary entry; 13 of 34 Service Settings are named nowhere |

**Coverage is better than feared on the mechanical inventories and weak on the conceptual ones.** Connector settings 65 of 65, environment variables 23 of 24, cmdlets 172 of 182 with a section, every REST endpoint in the generated reference. But the glossary lacks Object Type, Metaverse Attribute, Container, Deletion Rule, Generated Value, Run Profile Execution Item and about twenty more; 10 of 43 customer-facing portal pages are documented nowhere; Service Settings are 13 of 34 in the reference table.

**Nothing checks any of it before merge.** `mkdocs build --strict` runs only after merge to `main`, anchors are not validated, `unrecognized_links: ignore` hides one broken link, and two pages have front matter so malformed that MkDocs silently drops it.

### Why the existing guidance did not prevent this

`docs/CLAUDE.md` already contains a good voice specification ("Writing for the Reader"), and the best pages (`concepts/case-sensitivity.md`, `connectors/jim-sql-connector.md`, `administration/backup-recovery.md`) follow it closely. So "no defined voice" is only half true. What is actually missing:

1. **A home a human can find.** The rules live in an agent-instruction file excluded from the site. Only Claude sessions working under `docs/` ever read them.
2. **An audience and a page shape.** There is no definition of who each page is for, and no required structure per page type. `configuration/`, the largest customer section, has no Diátaxis mode at all, so every one of its 16 pages decides its own shape, and nearly all mix four modes.
3. **A check.** Everything is enforced by memory, and the rules that matter most are the ones a reviewer is least likely to catch.
4. **A migration driver.** "Existing pages will migrate over time" has been the policy since the Diátaxis section was written; without a mechanism, they did not.
5. **A rule for changing a page, not just writing one.** `changelog-lint` makes every user-facing pull request touch `docs/`, which is right, but nothing says *how*. The path of least resistance is to append a paragraph describing the new behaviour, in the voice of a release note. `configuration/synchronisation-rules.md` was changed by 77 commits in the six months to October 2026 and reached 17,800 words that way, with paragraphs such as "Changes saved before this release". The page-length and changelog-voice defects above are mostly this one mechanism, and no sentence-level rule touches it.
6. **Tested tutorials.** `docs/CLAUDE.md` makes tutorials "scarce by design" because they break between releases. The cost is real, but avoiding tutorials is the wrong answer to it: the first-run path is the single page that most decides whether an evaluator adopts a product, and JIM's is a 63-word stub. The answer to breakage is a test, and JIM already runs a pre-release integration suite that gates every release.
7. **Priorities.** Every page is held to the same bar, so the 15 or so pages that decide whether JIM is adopted, installed and trusted got no more care than a cmdlet reference.

## Goals

- One specification for public documentation, in one place, where every rule has a stable ID that the lint message, the review rubric and the specification all share.
- Every published page declares its type and its audience, and the build checks it has the shape its type requires.
- Defects that a machine can see (broken links and anchors, malformed front matter, cmdlets or parameters that do not exist, REST routes that do not exist, undocumented settings, missing glossary entries, issue numbers, time-relative narration) fail the pull request that introduces them.
- Existing debt can only fall: a shrink-only baseline lets every existing page keep working while forbidding new debt, and every new page is conformant from its first commit.
- "Complete coverage" is defined against inventories generated from source, and each inventory has a check that fails when something exists but is undocumented, or is documented but no longer exists.
- Every customer-facing folder is migrated to the specification, each finishing with an empty baseline.
- A change to documented behaviour leaves the page reading as though the new behaviour had always been true: superseded text is rewritten, not qualified, and a page over its size budget cannot grow.
- A new administrator can go from installation to a first working synchronisation using only the docs, and the release cannot ship if that tutorial no longer works.
- A named set of about 15 priority pages, the ones that decide whether JIM is adopted, installed and trusted, is migrated first and held to a stricter bar than the rest of the site.

## Non-Goals

- **Migrating off MkDocs or Material for MkDocs.** Material entered maintenance mode in November 2025 with fixes promised only until about 5 November 2026, and MkDocs 2.0 drops the plugin system. That is a real risk, but a separate decision; this work keeps every check generator-agnostic (Markdown sources and built HTML) so it survives a move. See Dependencies.
- **A versioned docs site, or shipping docs inside the air-gapped release bundle.** The specification adds version markers to mitigate an unversioned site; versioning the site is a product decision.
- **Generating the PowerShell cmdlet reference.** That is [#1083](https://github.com/TetronIO/JIM/issues/1083). This work depends on it for the `powershell/` migration (see Dependencies) rather than doing it.
- **Rewriting the generated REST API reference (Scalar).** Only the `docs/api/` overview pages are in scope.
- **Blocking a merge on a language model's judgement.** The rubric review is advisory by construction. On priority pages its findings arrive as review threads, which the author clears by fixing or by replying with a reason (requirement 38); no finding is ever a gate the author cannot clear.
- **Driving the tutorial's portal steps with a browser.** The tutorial's PowerShell path is executed every release and its portal labels are checked against the portal source (requirements 31 and 34); a browser-driven run of the portal path is the stronger and costlier option, deferred until the cheaper checks prove insufficient.
- **Screenshots.** The existing rule against them stands.
- **Translation or localisation.**

## User Stories

1. As an **evaluator**, I want each concept page to open with what the thing is and why it matters to me, so that I can judge whether JIM fits without reading about its internals.
2. As an **implementer**, I want a tutorial that takes me from installation to a first working synchronisation, and that works on the release I installed, so that I learn JIM by succeeding with it rather than by assembling twenty pages.
3. As an **administrator**, I want every task page to say what I need first, what to do, how to check it worked and what survives anything destructive, so that I can act on production without reading defensively.
4. As an **operator**, I want troubleshooting entries titled by the exact symptom I am seeing, with cause, fix and check, so that I can recover a failed run without raising a ticket.
5. As an **automation author**, I want every PowerShell and REST example to run as written, so that I can trust the docs as much as the product.
6. As a **security assessor**, I want security behaviour stated plainly in one place per fact, so that I can sign off a deployment without reconciling contradictory pages.
7. As a **JIM author (human or Claude)**, I want the shape of the page I am writing, and to be told at pull request time which rule I broke and where it is defined, so that I do not have to learn the standard by review comments.
8. As the **product owner**, I want to see each folder's conformance at a glance, so that I know how far the migration has come and where to spend effort.
9. As the **product owner**, I want the pages that decide whether JIM is adopted to be the best on the site, and to know each was checked against the release I am about to cut, so that the first impression never lags the product.
10. As a **JIM author**, when my change alters documented behaviour, I want to be told to rewrite what is now wrong rather than add to it, so that the page stays one coherent description instead of a history.

## Requirements

### Functional Requirements

#### The specification

1. The specification lives in one document (`engineering/DOCUMENTATION_STANDARD.md`; [Decision 1](#decisions)). `docs/CLAUDE.md` keeps only site mechanics (diagrams, lightbox, dependencies) and points to it; it no longer restates any rule. The "Writing for the Reader" rules move into the specification unchanged in substance.
2. Every rule has a stable ID (for example `LANG-5`). The ID appears in the lint message, the rubric and the specification, and the lint message links to the rule.
3. **Audiences.** The specification names the readers and what each needs: evaluator, implementer, administrator, operator, automation author, security assessor. Every page declares one primary audience in front matter (`audience:`). JIM contributors are not an audience of the public site ([Decision 3](#decisions)).
4. **Page types and required shapes.** Every page declares `type:` in front matter, and each type has a required shape:

    | Type | Title | Required | Forbidden |
    |------|-------|----------|-----------|
    | `tutorial` | Outcome ("Run your first synchronisation") | Time and outcome up front; prerequisites; checkpoints; "What you achieved"; an integration scenario that executes it (requirements 31 to 35) | Branches and alternatives |
    | `how-to` | Imperative ("Configure SSO with Microsoft Entra ID") | When to do this and the outcome; "Before you begin" when there are prerequisites; numbered steps; a warning before any destructive step stating what survives; "Check it worked"; next steps | Teaching digressions (link to the concept) and exhaustive option lists (link to the reference) |
    | `concept` | Noun phrase, never "Overview" | What it is and why it matters, in the first paragraph; a diagram where the shape is the point; links on to tasks and reference | Numbered procedures |
    | `reference` | Noun phrase | One-line scope; consistent tables (setting, default, allowed values, effect, available from); generated where possible | Narrative and instructions |
    | `troubleshooting` | "Troubleshooting <area>" | Entries titled by the symptom or message verbatim, each with cause, fix, check and what is not lost; data-loss entries first; "Still stuck?" with what to collect | Reassurance with no fix |
    | `landing` | Section name | One purpose paragraph, then cards or links | Other content |
    | `object` ([Decision 4](#decisions)) | The domain noun ("Connected Systems") | A fixed order: what it is and why it matters, then tasks (each a verb heading), then settings reference, then troubleshooting; split when over the size budget | Anything out of that order |

5. **Voice.** Present tense, active voice, addressed to "you", JIM as the subject of what JIM does. Name the domain concept, never the implementation. Hedge only where JIM is genuinely uncertain. A failure is named with its remedy; anything destructive or refused says what survives. (These are the existing "Writing for the Reader" rules, kept.)
6. **What does not belong on a public page:** GitHub issue or pull request numbers; release, upgrade or roadmap narration ("now", "until now", "previously", "this release", "planned"); JIM's own development or test infrastructure (devcontainer, integration lab); design defence ("this is deliberate", "we chose"); class, method, property or table names unless the reader types them.
7. **Version language.** The site is unversioned while customers run several releases, so time-relative words are banned. Behaviour that differs by release carries an `Available from vX.Y.Z` marker, validated against released versions; a `vNEXT` placeholder is resolved by `/release`. Upgrade-specific notes live in `administration/upgrading.md` and the changelog only.
8. **Terminology.** A machine-readable controlled vocabulary file (proposed `docs/vocabulary.yml`, excluded from the build) is the single source for product nouns: each entry has its canonical form, rejected variants, glossary anchor and home page. It drives the glossary check, the Title Case check and the rejected-synonym check, and it is reconciled with the existing `TermDefinitions` that the portal's `TermHint` shows.
9. **One home per fact.** Each concept has exactly one home page (recorded in the vocabulary file); other pages link to it rather than restating it.
10. **Size budget.** Pages over 5,000 words warn, and cannot grow (requirement 29); priority pages have a budget of 3,000 (requirement 40). The specification says how to split (one sub-feature per page, the parent keeping its URL and linking on).
11. **Accessibility.** Meaningful link text; one H1 and no skipped heading levels; a text alternative for every non-text element, including an `accTitle`/`accDescr` for every Mermaid diagram (none of the 54 has one today).

#### Enforcement

12. One PowerShell orchestrator (proposed `scripts/Test-DocsConformance.ps1`) runs every check, locally and in CI, inside the existing required `changelog-lint` job so that no new required status check is needed. Each rule has Pester tests with passing and failing fixtures.
13. Rules are tiered:
    - **Blocking:** internal links and anchors; built-site links (including the Scalar reference); front matter parses and has the required keys; one H1 and no skipped levels; fenced code declares a language; every page is in the nav; no stub pages in the nav; no em dashes or American spellings; cmdlet parameter aliases (exists today) and unknown cmdlets or parameters in examples; REST routes in examples exist in the OpenAPI document; coverage checks (below); issue numbers on customer pages; every docs URL in product source resolves to a page and anchor; an over-budget page does not grow (requirement 29, from Phase 5); a tutorial names an existing scenario and its portal labels exist (requirements 34 and 35).
    - **Ratcheted** (blocking for new occurrences, baselined for existing ones): Title Case domain nouns, required front matter keys, title = H1 = nav label, page-type shape, Mermaid text alternatives, glossary coverage.
    - **Warnings** (annotations, never fail): abbreviations in prose, time-relative words, future promises, minimisers ("simply", "just"), Latin abbreviations, size budget, opening paragraph. A warning is promoted to blocking once its baseline is zero and it has run a release without a false positive. On a priority page every warning is blocking (requirement 39).
    - **Rubric only** (advisory review plus human): audience fit, voice, implementation nouns, "what survives", failure plus remedy, Diátaxis purity, one home per fact, claims about source.
    - **Scheduled** (weekly, non-blocking, one tracking issue): external links, staleness, coverage drift summary.
14. **Shrink-only baseline.** One sorted line per entry (`rule`, `path`, `count`), so parallel branches touch different lines and merge cleanly. A file absent from the baseline has an allowance of zero. Exceeding a count fails and annotates only that rule's hits in that file; a count that has fallen also fails until the improvement is locked in with `-UpdateBaseline`, so the baseline never silently re-admits debt.
15. **Permanent exceptions** are marker comments in the page with a reason (for example `<!-- conformance: allow STR-7 "landing page opens with cards by design" -->`), never baseline entries. A marker without a reason fails.
16. Findings appear as annotations on the pull request diff and as a per-rule table in the job summary.
17. Checks are generator-agnostic: they read Markdown sources and built HTML, and treat MkDocs' own `validation:` settings as a bonus rather than a foundation.
18. An advisory review (separate, non-required workflow on the already-pinned Claude Code action, read-only tools) comments on documentation pull requests against the rubric rules, citing rule IDs.

#### Coverage

19. Inventories are generated from source, never from hand-maintained lists:
    - cmdlets and their parameters (module manifest plus the PowerShell AST);
    - environment variables the product reads, and installer variables;
    - Service Settings (`Constants.SettingKeys`, plus an assertion that every key is actually seeded);
    - portal pages (each routable page declares its docs page through the existing `PageInfo` component);
    - REST resource groups (OpenAPI tags) mapped to `docs/api/` overview pages;
    - connector settings (each connector's settings, read from a test rather than by parsing);
    - expression functions;
    - the controlled vocabulary.
20. "Documented" means named in a heading, in the first column of a reference table, or as a glossary entry with an anchor; a mention in running text does not count.
21. Every coverage check also runs in reverse: a name in the docs that no longer exists in source fails.
22. Deliberate exclusions live in one allow-list with a reason per entry; an entry for something that no longer exists, or is now documented, fails.

#### Migration

23. **Phase 0 corrects the factual defects first**, before any rewriting: the non-existent cmdlets and parameters, the wrong REST route, the broken link, the malformed front matter, the contradictions listed in the audit, and the glossary's three factual errors. The stub tutorial is not a correction: it is replaced by the tested tutorial (requirements 31 to 35) in the priority wave.
24. The priority pages (requirement 36) migrate first, as one wave ahead of any folder. The remaining pages then migrate a folder at a time, in order of reader impact: `getting-started/`, `administration/`, `configuration/`, `concepts/`, `connectors/`, `api/`, then `powershell/` once [#1083](https://github.com/TetronIO/JIM/issues/1083) has decided generation. A folder is marked conformant in the conformance configuration when every page declares its type and its baseline is empty; the mark never reverts.
25. Splitting a page keeps the original URL as the parent and moves sub-features to child pages. Every deep link from the product into the docs (the portal's glossary links and `docs.junctional.io` links in source) keeps resolving, checked by requirement 13.
26. Customer-relevant behaviour currently explained only in `docs/developer/diagrams/` (Pending Export states, deletion grace periods, Schedule step failure) is rewritten into `concepts/` before any developer content moves.

#### Keeping pages integrated: change a page, do not append to it

27. **Integrate, don't append.** A change to documented behaviour rewrites the page so it reads as though the new behaviour had always been true. Superseded sentences are replaced or deleted, never qualified ("previously ... now ..."). New material goes where the page type puts it: a new task under the tasks, a new setting in the settings table, a new failure in troubleshooting. Release history goes in the changelog and `administration/upgrading.md`, and nowhere else.
28. **Find what is now wrong before writing what is new.** Before adding anything, the author searches `docs/` for the statements the change makes false and rewrites them (the existing "Document shipped behaviour" rule, given an ID). The coupling check proves *a* page changed; this rule is about the *right* page.
29. **An over-budget page cannot grow.** A pull request may not increase the word count of a page that was over its size budget on the base branch, unless it also splits the page or removes as much as it adds. The comparison is against the base branch rather than a baseline file, so trimming a large page never needs a baseline update. No exception marker can excuse it: a marker would excuse nothing once the growth merged, and then fail every later pull request. The check warns until the pages already over budget are split, and blocks from Phase 5 (decision 9).
30. **Prompted where the change is made.** The pull request template's Documentation section asks which existing statements the change made wrong and where new material went. The advisory review reads the docs diff for appended release-note voice and for superseded text left standing beside its replacement.

#### The tested tutorial

31. **Executed every release.** `getting-started/first-synchronisation.md` is backed by an integration scenario (the next scenario number, registered as directory-agnostic so it runs once per pass) in the `-PreRelease` suite. A release cannot be cut while the tutorial is broken, because the suite already gates every release through the `jim-pre-release` status.
32. **One source.** Each step shows its portal and PowerShell forms side by side (tabbed). The scenario reads the PowerShell steps from the page itself and executes them in order; it keeps no copy of its own, so the page and the test cannot drift apart. Every checkpoint the page states (an object count, a state, a value the reader should see) carries a machine-readable marker that the scenario asserts.
33. **No external system.** The sample data is a file published with the docs and is also the scenario's input. The tutorial needs nothing but JIM, so it works on an air-gapped evaluation install (an HR file in, through the Metaverse, out to a second file; [Decision 8](#decisions)).
34. **Portal labels checked.** Every portal control the tutorial names in bold must exist in the portal source; the conformance check fails otherwise. This proves the labels exist, not that the clicks work (see Non-Goals).
35. **Tutorials declare their test.** A page with `type: tutorial` must name its scenario in front matter (`scenario:`), and the named script must exist. The tutorial's PowerShell steps are checked statically at pull request time like every other example; they are executed at release time. This replaces "tutorials are scarce by design" with "tutorials are few, and tested".

#### Priority pages

36. **The list.** About 15 pages, recorded in the conformance configuration so the stricter rules apply mechanically. By the reader's journey ([Decision 7](#decisions)):

    | Journey | Pages |
    |---------|-------|
    | Evaluate | `index.md`, `concepts/index.md`, `concepts/synchronisation-pipeline.md` |
    | Install | `getting-started/prerequisites.md`, `administration/deployment.md` |
    | Learn | `getting-started/first-synchronisation.md` |
    | Configure | `configuration/connected-systems.md`, `configuration/synchronisation-rules.md`, `connectors/jim-ldap-connector.md`, `concepts/jml-lifecycle.md` |
    | Operate | `configuration/run-profiles.md`, `configuration/activities.md` |
    | Recover | `administration/troubleshooting.md`, `administration/upgrading.md`, `administration/backup-recovery.md` |

    Three of these are the site's largest pages; when one is split, the list names whichever resulting pages carry its core tasks.
37. **First.** Priority pages migrate ahead of every folder (requirement 24).
38. **Every finding answered.** On a priority page the advisory review posts each finding as a review thread rather than a summary comment. Unresolved threads already block a merge, so each finding is either fixed or answered with a reason; the review still cannot block a merge the author has answered.
39. **Zero tolerance.** Every mechanical rule, warnings included, is blocking on a priority page, and priority pages have no baseline allowance once migrated.
40. **Tighter budget.** 3,000 words, and the opening-paragraph rule is blocking.
41. **Validated each release.** Each priority page records the release it was last checked against (`validated: vX.Y.Z` in front matter). `/release` lists every priority page last validated before the previous minor release, and the release proceeds only once each is re-validated or explicitly deferred, with a reason, in the release pull request.

### Non-Functional Requirements

- The checks add no more than about 60 seconds to `changelog-lint`; the MkDocs build and link check short-circuit unless the pull request touches the docs, the module's public cmdlets, `CHANGELOG.md` or the conformance configuration.
- Every new CI tool is pinned by version and SHA-256 per `engineering/DEPENDENCY_PINNING.md`, covered by `tooling-pin-check`, and approved per the dependency governance rule before it is added.
- Nothing is added to the product's runtime; the product stays air-gap deployable.
- A rule that produces a false positive it cannot be configured out of is demoted, not tolerated.

## Examples and Scenarios

### Scenario 1: A new page without a type

**Given**: an author adds `docs/configuration/generated-values.md` with no `type:` in its front matter
**When**: the pull request runs `changelog-lint`
**Then**: it fails with `STR-2: front matter has no 'type' (docs/configuration/generated-values.md:1)` and a link to the rule; new files have no baseline allowance

### Scenario 2: Editing a large legacy page without adding debt

**Given**: `configuration/synchronisation-rules.md` is baselined at 31 lower-cased domain nouns
**When**: an author corrects a paragraph and the count stays at 31 or below
**Then**: the check passes, and if the count fell, it asks the author to run `-UpdateBaseline` to lock the improvement in

### Scenario 3: Adding a cmdlet without documenting it

**Given**: a feature adds `Get-JIMGeneratedValueForm` to the module manifest
**When**: no `docs/powershell/` page has a heading for it
**Then**: `COV-1` fails, naming the cmdlet and the page its noun maps to

### Scenario 4: An example that cannot run

**Given**: a page shows `Get-JIMConnectedSystem -All`
**When**: the AST check resolves the cmdlet's real parameter sets
**Then**: `ACC-2` fails: "`-All` is not a parameter of Get-JIMConnectedSystem"

### Scenario 5: Changelog voice in a how-to

**Given**: a pull request adds "Until now, JIM retried indefinitely; it now stops after three attempts" to a how-to page
**When**: the checks run
**Then**: `LANG-5` annotates the line as a warning, and the advisory review suggests "JIM stops after three attempts", with an `Available from` marker if the release matters to the reader

### Scenario 6: Appending to an over-budget page

**Given**: `configuration/synchronisation-rules.md` is 17,800 words on the base branch, over its 5,000-word budget
**When**: a feature pull request adds a 400-word section to it and removes nothing
**Then**: the check fails, naming the page's size on the base branch and on the head; the author splits the new sub-feature onto its own page and links to it, or rewrites the superseded section the feature replaces so the page does not grow

### Scenario 7: A product change breaks the tutorial

**Given**: a pull request changes how a new Connected System's Object Matching Rule is proposed, so the tutorial's HR records no longer join; every cmdlet and parameter on the page still exists, so the static checks pass
**When**: the release pull request merges and the pre-release suite runs
**Then**: the tutorial scenario fails at the checkpoint where the page tells the reader the Metaverse now holds 25 people, `jim-pre-release` goes red on that commit, and the release is not cut until the page (and with it the scenario, which reads the page) or the product is corrected

### Scenario 8: Release-note voice on a priority page

**Given**: a pull request adds "JIM now retries three times" to `administration/troubleshooting.md`, a priority page
**When**: the checks run
**Then**: `LANG-5` fails rather than warns, and the advisory review's finding arrives as a review thread that must be resolved before the pull request can merge

## Constraints

- All automation in PowerShell; no new bash scripts.
- No new third-party dependency without the user's approval; lychee is approved, Vale and markdownlint-cli2 are not adopted ([Decision 2](#decisions)).
- British English, no em dashes, Title Case domain nouns, no competing product names (existing rules).
- The required status check list stays unchanged: the checks run inside `changelog-lint` ([Decision 5](#decisions)).
- Parallel sessions edit `docs/` constantly; the baseline format and folder-by-folder rollout must not create merge conflicts or block unrelated work.

## Affected Areas

| Area | Impact |
|------|--------|
| `engineering/` | New `DOCUMENTATION_STANDARD.md` |
| `docs/` | Front matter on every page; folder-by-folder rewrites; page splits; new tutorial and troubleshooting content; new `vocabulary.yml` (excluded from the build) |
| `docs/CLAUDE.md` | Reduced to site mechanics plus a pointer to the standard |
| `scripts/` | `Test-DocsConformance.ps1` and its rule modules, Pester tests, baseline and configuration files; `Lint-DocExamples.ps1` extended with unknown-parameter detection |
| `.github/workflows/` | `changelog-lint.yml` gains the conformance step; new advisory `docs-review.yml`; new weekly docs health workflow |
| `mkdocs.yml` | Stricter `validation:` settings; `overrides/` excluded from the published files |
| UI | `PageInfo` gains a docs page parameter so every routable page declares where it is documented |
| Tests | Coverage assertions that need reflection (connector settings, seeded Service Settings) as NUnit tests beside `GlossaryTermConsistencyTests` |
| `test/integration/` | A new directory-agnostic scenario that executes the tutorial's PowerShell steps from the page and asserts its checkpoints; registered in the `-PreRelease` suite |
| `docs/assets/` | The tutorial's sample data file, shared with the scenario |
| `.github/pull_request_template.md` | The Documentation section asks what the change made wrong and where new material went |
| `.claude/skills/release/` | Lists priority pages due for validation; the release pull request records each re-validation or deferral |

## Documentation Impact

| Doc | Change |
|-----|--------|
| `engineering/DOCUMENTATION_STANDARD.md` | New: the specification |
| `docs/CLAUDE.md` | Rules move out; pointer and site mechanics remain; "Tutorials are scarce by design" is replaced by requirement 35 |
| `test/CLAUDE.md` and `engineering/INTEGRATION_TESTING.md` | The tutorial scenario, and that it reads its steps from the page |
| `engineering/RELEASE_PROCESS.md` | Priority page validation as a release step |
| `engineering/CLAUDE.md` | "Keeping Documentation Current" links the standard |
| `engineering/prompts/api-docs-section.md` | Retire or rewrite: it tells authors to create per-endpoint pages that `docs/CLAUDE.md` forbids |
| Every page under `docs/` | Migrated per requirement 24 |

## Dependencies

- **[#1083](https://github.com/TetronIO/JIM/issues/1083), generating the PowerShell cmdlet reference,** should be decided before the `powershell/` migration. The 23 PowerShell pages are the largest drift surface; linting them is a stopgap, generating them removes the defect class. Recorded as a native blocked-by link on #1454.
- **The docs generator's end of support** is tracked in [#2058](https://github.com/TetronIO/JIM/issues/2058). Material for MkDocs committed to critical and security fixes for at least 12 months from 9.7.0 (11 November 2025), and MkDocs 2.0 is incompatible with it. Not blocking: every check here is generator-agnostic (requirement 17).

## Decisions

Decided by the product owner on 2026-10-11: the first eight as recommended in the draft, the ninth after the conformance check first ran against real pull requests.

1. **The specification lives in `engineering/DOCUMENTATION_STANDARD.md`.** Its readers are JIM's authors, human and Claude, not customers. A public page under `docs/` would only help outside contributors, and would itself have to conform.
2. **Tooling: lychee is approved; Vale and markdownlint-cli2 are not adopted.** lychee (Apache-2.0 or MIT; a single static binary, pinned by version and SHA-256) checks links in the built site, including the one broken link MkDocs cannot see. Vale was rejected because the mechanical style rules it would enforce are already 99% met, its Markdown parser skips indented content after a blank line (every admonition body, about 109 lines in 20 files, exactly where warnings live), and the stock style packages raised 38,998 alerts that mostly contradict JIM's own conventions; PowerShell rules driven by the vocabulary file cover the same ground without the blind spot. markdownlint-cli2 was rejected for its 81 npm packages and high false-positive rate on MkDocs syntax.
3. **`docs/developer/` leaves the public site, except connector authoring.** 16 of its 18 pages are for JIM's own contributors (the ten diagram pages alone carry more than 200 issue references); they move to `engineering/` once the customer-relevant behaviour they hold is rewritten into `concepts/` (requirement 26). Connector authoring stays as a small "Extending JIM" section once its placeholder content is finished. The Quick Start and Prerequisites pages stop sending new customers into contributor material.
4. **`configuration/` and `connectors/` pages use the `object` type.** One page per domain object, in a fixed concept, tasks, reference, troubleshooting order, with the size budget forcing splits. Strict Diátaxis would scatter "Connected Systems" across four folders, which is purer but makes an administrator assemble the answer; the fixed order makes each mode findable on the page administrators actually look for.
5. **The checks run inside `changelog-lint`.** No ruleset change is needed. The job then runs eight unrelated checks under a misleading name; renaming it (for example to `pr-lint`) remains a one-time ruleset edit for whenever it is convenient.
6. **The generator's end of support is its own issue,** [#2058](https://github.com/TetronIO/JIM/issues/2058), filed 2026-10-11.
7. **The priority pages are the 15 in requirement 36,** chosen along the reader's journey from evaluating to recovering. The list stays short on purpose: every page on it costs a re-validation each release, so a page belongs on it only if an administrator who cannot use it would stall or lose trust in JIM.
8. **The tutorial synchronises an HR file in, through the Metaverse, out to a second file,** using the File Connector only. It needs no directory, works air-gapped and on any evaluation install, and is the cheapest to keep passing. A directory target (Active Directory or LDAP) is what most customers actually want, but it cannot be assumed on an evaluation install; it is better as a second, how-to-style guide once the first tutorial exists.
9. **An over-budget page growing warns until the over-budget pages are split, then blocks.** Eleven pages were over budget when the check first ran, including the Synchronisation Rules, Connected Systems and PowerShell pages that most features document into; replayed against `main`, a blocking check failed the first Generated Value Forms pull request on both Synchronisation Rules pages. Blocking before those pages are split would fail most feature pull requests, or push authors to cut useful text to make room. The pages are split as the priority wave and folder migrations reach them, and requirement 29 blocks from Phase 5.

## Acceptance Criteria

- [ ] `engineering/DOCUMENTATION_STANDARD.md` exists, every rule has an ID, and `docs/CLAUDE.md` restates none of them
- [ ] Every page under `docs/` declares `type:` and `audience:`, and its shape matches its type
- [ ] `Test-DocsConformance.ps1` runs in `changelog-lint` on every pull request, with Pester fixtures for every rule
- [ ] Introducing any blocking-tier defect on any page fails the pull request with the rule ID and location
- [ ] The baseline is shrink-only, and new files have no allowance
- [ ] Every coverage inventory is generated from source and checked in both directions
- [ ] Every factual defect listed in the audit is corrected
- [ ] `getting-started/first-synchronisation.md` takes a new administrator from installation to a working synchronisation, and `administration/troubleshooting.md` has a synchronisation section
- [ ] Every customer-facing folder is marked conformant with an empty baseline
- [ ] The advisory review comments on documentation pull requests citing rule IDs
- [ ] Every product deep link into the docs resolves, and stays checked
- [ ] A pull request that grows an over-budget page fails, and the pull request template asks what the change made wrong
- [ ] The tutorial scenario reads its steps from the page, asserts its checkpoints, and runs in the `-PreRelease` suite
- [ ] A `type: tutorial` page without an existing `scenario:` fails the conformance check
- [ ] The priority page list is in the conformance configuration, those pages are migrated first, every warning is blocking on them, and each carries `validated:`
- [ ] `/release` lists priority pages due for validation, and the release pull request records each re-validation or deferral

## Additional Context

- **Audit evidence:** [`engineering/notes/docs-audit-2026-10/`](../../notes/docs-audit-2026-10/README.md): per-page findings with quotes and suggested rewrites, the coverage inventories with every gap, and the tooling evaluation with measured baselines.
- **Exemplar pages to model the specification on:** `concepts/case-sensitivity.md`, `configuration/data-flow.md`, `configuration/sync-preview.md`, `connectors/jim-sql-connector.md` and its two Delta Import guides, `administration/backup-recovery.md`, `administration/deploying-with-ansible.md`, `powershell/connection.md`, `powershell/password-synchronisation.md`.
- **Sources:** [Diátaxis](https://diataxis.fr/); [Google developer documentation style guide](https://developers.google.com/style); [Microsoft Writing Style Guide](https://learn.microsoft.com/style-guide/welcome/); [GitLab documentation topic types](https://docs.gitlab.com/development/documentation/topic_types/) and its Vale-based enforcement; Mark Baker, *Every Page is Page One*; [ESLint bulk suppressions](https://eslint.org/blog/2025/04/introducing-bulk-suppressions/) (the model for the shrink-only baseline). Full list with notes in the research file.
- **Effort.** The issue is sized Medium. Phases 0 to 2 (corrections, specification, enforcement framework) are about that; the migration of about 222,000 words across 80 pages, including splitting the five largest pages, is substantially larger and is best delivered as one pull request per folder. The priority wave is where most of the value lands and should be planned as its own pull requests: the tutorial with its scenario, the troubleshooting synchronisation section, and the splits of the three largest priority pages. Re-validating the priority pages adds a recurring cost to each release, which is why the list is kept short.
