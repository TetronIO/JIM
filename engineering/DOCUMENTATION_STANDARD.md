# JIM Documentation Standard

> The rules for JIM's public documentation (`docs/`, published at docs.junctional.io): who each page is for, what shape it takes, how it reads, and how it stays true. Every rule has an ID; a failing check names the ID, and this document defines it.
>
> Issue [#1454](https://github.com/TetronIO/JIM/issues/1454). Design and evidence: [`PRD_PUBLIC_DOCUMENTATION_STANDARD.md`](prd/done/PRD_PUBLIC_DOCUMENTATION_STANDARD.md), [`notes/docs-audit-2026-10/`](notes/docs-audit-2026-10/README.md). Site mechanics (diagrams, the lightbox, MkDocs dependencies) stay in `docs/CLAUDE.md`.

The standard applies to every page under `docs/` except the released-version entries `docs/reference/changelog.md` pulls in from `CHANGELOG.md` (through `docs/hooks/changelog.py`; the page's own front matter and introduction follow the standard). Contributor material is not public documentation: it lives in `engineering/`, the contributor guides and process diagrams in `engineering/developer/` (PRD Decision 3).

## Contents

- [Using this standard](#using-this-standard)
- [How the standard is enforced](#how-the-standard-is-enforced)
- [Audiences](#audiences)
- [Page types](#page-types)
- [Front matter and page structure](#front-matter-and-page-structure)
- [Voice](#voice)
- [Language](#language)
- [Maintaining pages](#maintaining-pages)
- [Accuracy and links](#accuracy-and-links)
- [Accessibility](#accessibility)
- [Visuals](#visuals)
- [Style conventions](#style-conventions)
- [Priority pages and tutorials](#priority-pages-and-tutorials)
- [Coverage](#coverage)
- [Rule index](#rule-index)

## Using this standard

**Before writing a page,** decide who it is for ([Audiences](#audiences)) and what kind of page it is ([Page types](#page-types)). The type decides the shape; most pages that read badly mix several types.

**While writing,** follow [Voice](#voice) and [Language](#language). They are short, and each exists because the opposite shipped at least once.

**When you change behaviour,** read [Maintaining pages](#maintaining-pages) first. The commonest failure is not writing something false; it is leaving something true-when-written, or appending a paragraph of release history to a page instead of rewriting it.

**When a check fails,** its message names the rule. Find the ID in the [Rule index](#rule-index).

`changelog-lint` runs every check on every pull request. Locally they are optional for a docs-only change (the root `CLAUDE.md` does not ask for local validation of `.md` changes), but the conformance check takes about three seconds and is the only way to update its baseline:

```bash
pwsh ./scripts/Test-DocsConformance.ps1            # this standard's mechanical rules
pwsh ./scripts/Lint-DocExamples.ps1                # cmdlet names and parameters in examples
pwsh ./scripts/Test-DocsSite.ps1                   # strict MkDocs build and every link (needs mkdocs and Docker)
```

## How the standard is enforced

Every rule has one of four tiers. The [Rule index](#rule-index) gives each rule's tier; for the rules `Test-DocsConformance.ps1` checks, `docs/.conformance/config.psd1` is authoritative.

| Tier | Meaning |
|------|---------|
| **Block** | Every finding fails the pull request. Used only where the defect class is proven and false positives are near zero. |
| **Ratchet** | Findings are compared with the baseline, per rule and page. Exceeding the baseline fails. A new page has no allowance, so it is conformant from its first commit. |
| **Warn** | Reported as an annotation on the pull request diff (for the pages it changes), never fails. |
| **Review** | Judgement a machine cannot make reliably. Checked by the reviewer, and by the rubric review (`.github/workflows/docs-review.yml`) on a pull request that changes customer pages, when it opens or is marked ready and again whenever the `docs-review` label is added. Each finding is a review thread citing its rule ID, and the merge waits until every thread is resolved: fix the text, or reply with why it stays, then resolve. That is as far as it holds a merge: answering clears it, and it is never a required check, so a review that fails to run holds nothing. |

### The baseline only shrinks

`docs/.conformance/baseline.tsv` records the existing debt each Ratchet rule allows on each page, one `rule <TAB> page <TAB> count` line per entry.

- **More findings than the baseline allows fails.** Touching a large page never obliges you to fix its existing debt, only not to add to it.
- **Fewer findings than the baseline also fails,** until you lock the improvement in with `pwsh ./scripts/Test-DocsConformance.ps1 -UpdateBaseline`. Otherwise the baseline would silently re-admit the debt you just removed.
- `-UpdateBaseline` lowers and removes entries; it never adds or raises one. `-SeedBaseline -Rule <ID>` adds entries only for a rule that has none, which is how a new Ratchet rule is introduced over existing pages. Reviewers read any seeding diff.
- The file carries a `merge=union` driver (`.gitattributes`), so parallel pull requests that touch neighbouring lines do not conflict. Where a union merge leaves two lines for one rule and page, the lower count wins; where it brings back a line another pull request removed, the check fails until `-UpdateBaseline` removes it again.

### Exceptions live in the page

A rule that is wrong for one place is excused in that place, with a reason the next author can read:

```html
<!-- conformance: allow STR-5 "terminal output, not code" -->
<!-- conformance: allow-page STR-2 "typed in the folder migration" -->
```

`allow` covers the marker's own line and the next non-blank line; `allow-page` covers the whole page. A marker with no reason, naming an unknown rule, or suppressing nothing fails (EXC-1), so exceptions cannot outlive their cause. Neither EXC-1 nor MNT-3 can be suppressed: MNT-3 compares with the base branch, so a marker excusing growth would suppress nothing once the growth merged, and fail every later pull request.

### Conformant pages, priority pages, promotion

- A page listed under `ConformantPages`, or inside a folder listed under `ConformantFolders`, has been migrated to this standard. It carries no baseline: its Ratchet findings fail outright. A page or folder is added once every page declares its type and has no baseline entries, and is never taken off again. The one exception is a Ratchet rule introduced after its pages were conformant, listed under `BaselineOnConformant` (VIS-1): it keeps its baseline on conformant pages too, so the pages that predate it are held at their debt while a new page must meet it from its first commit.
- A **priority page** ([Priority pages and tutorials](#priority-pages-and-tutorials)) that is conformant is held higher still: its warnings fail too, and its size budget is 3,000 words rather than 5,000.
- A Warn rule is promoted (to Ratchet or Block) by editing `config.psd1`, once it has run for a release without a false positive. A rule that produces a false positive it cannot be configured out of is demoted, not tolerated.

## Audiences

Every page declares one primary audience in its front matter (`audience:`; STR-2). The reader decides the depth: the same feature is explained differently to someone deciding whether to adopt JIM and to someone scripting it.

| `audience` | Who | What they need from a page |
|------------|-----|----------------------------|
| `evaluator` | Someone deciding whether JIM fits their organisation | What it is, why it matters, what it cannot do; no internals |
| `implementer` | Someone installing JIM and setting it up for the first time | A working path, prerequisites stated up front, how to tell it worked |
| `administrator` | Someone configuring and changing JIM in production | Tasks, settings with their defaults, what survives anything destructive |
| `operator` | Someone running JIM day to day and recovering from failures | Symptoms, causes, remedies, and what to collect before asking for help |
| `automation-author` | Someone scripting JIM through the REST API or PowerShell | Examples that run as written, exact names, links to the generated reference |
| `security-assessor` | Someone approving JIM for deployment in a high-trust environment | Security behaviour stated plainly, once, with what it protects against |

**AUD-1 (Review).** The page is written for its declared audience. JIM's own contributors are not an audience of the public site; contributor material belongs in `engineering/`.

## Page types

Every page declares its type in front matter (`type:`; STR-2). The type decides the page's shape, and `Test-DocsConformance.ps1` checks the parts of that shape a machine can see (TYP-1, for every page that declares a type). The headings it checks are exact: use the names below.

| `type` | Title | Required shape | Forbidden |
|--------|-------|----------------|-----------|
| `tutorial` | An outcome ("Your First Synchronisation") | Time and outcome up front; a **Before you begin** section; numbered steps with checkpoints; a **What you achieved** section; a `scenario:` naming the integration scenario that runs it ([Priority pages and tutorials](#priority-pages-and-tutorials)) | Branches and alternatives |
| `how-to` | An imperative ("Configure SSO with Microsoft Entra ID") | When to do this and the outcome; **Before you begin** when there are prerequisites; numbered steps; a warning before any destructive step stating what survives (SAFE-1); a **Check it worked** section; optionally **Next steps** | Teaching digressions (link to the concept) and exhaustive option lists (link to the reference) |
| `concept` | The name of the thing, never "Overview", "Introduction" or "How it works" | What it is and why it matters, in the first paragraph; a diagram where the shape is the point; links on to tasks and reference | Numbered procedures |
| `reference` | A noun phrase | A one-line scope; consistent tables (setting, default, allowed values, effect); generated from source where possible | Narrative and instructions |
| `troubleshooting` | "Troubleshooting <area>" | Entries titled by the symptom or message verbatim, each with **Cause**, **Fix**, **Check** and what is not lost; data-loss entries first; a closing **Still stuck?** section saying what to collect | Reassurance with no fix |
| `landing` | The section's name | One purpose paragraph, then cards or links | Other content |
| `object` | The domain noun ("Connected Systems") | One page per domain object, in a fixed order: what it is and why it matters; then tasks, each a verb heading ("Create a Connected System"); then **Settings** (the reference table), **Troubleshooting** and **Related**, last and in that order where present | Anything out of that order |

`object` is how `configuration/` and `connectors/` pages are shaped (PRD Decision 4): strict Diátaxis would scatter one object across four folders, and the fixed order keeps each mode findable on the page administrators actually look for. The size budget (STR-8) forces a split before one page grows unusable.

## Front matter and page structure

Every page opens with front matter:

```yaml
---
title: "SSO Setup: Keycloak"
description: "Register JIM with Keycloak so administrators sign in with their existing accounts."
type: how-to
audience: implementer
---
```

| ID | Tier | Rule |
|----|------|------|
| **STR-1** | Block | The front matter is valid. Quote any value containing `: ` or ` #`, or starting with a YAML indicator character: MkDocs drops an invalid block without a warning, and the page loses its description. Only the keys `title`, `description`, `type`, `audience`, `template`, `hide`, `scenario` and `validated` are allowed; `type` and `audience` take the values above; the `description` is one sentence of 50 to 160 characters saying what the page is for. |
| **STR-2** | Ratchet | The front matter declares `title`, `description`, `type` and `audience`. |
| **STR-3** | Block | One name for the page: the `title` equals the H1, and the nav label equals that name or drops its leading words (the nav section already says them: "Keycloak" under SSO Setup for "SSO Setup: Keycloak"). |
| **STR-4** | Block | Exactly one H1 (a page with a `template` has none), and no heading skips a level. |
| **STR-5** | Block | Every code block names its language; use `text` for output and messages. |
| **STR-6** | Ratchet | No stub or placeholder pages. A page that does not exist yet stays out of the nav. |
| **STR-7** | Warn | The page opens with a paragraph, directly under the H1, saying what this is and why it matters. Not a heading, admonition, table, list or included file. |
| **STR-8** | Warn | The page is within its size budget: 5,000 words of prose (code excluded), or 3,000 for a conformant priority page. Over it, split one sub-feature per page; the parent keeps its URL and links on. |

## Voice

These rules decide how the sentences read. They are the house voice that `docs/CLAUDE.md` carried as "Writing for the Reader", moved here unchanged in substance. All are Review tier: a mechanical ban would fire on every legitimate use, and a check that encodes taste rather than a real defect is friction (if one regresses more than once, that is when to reach for a check; the escalation pattern is in `test/CLAUDE.md` > Blazor component tests).

**VOI-1. Name the domain concept, never the implementation.** A customer-facing sentence must not contain a word whose meaning comes from JIM's source: `row`, `record`, `entity`, `table`, `column`, `foreign key`, `index`, `cascade`, `null`, `DTO`, `enum`, a class or method name, or "the database". Say what the thing *is* to the reader: a change, an account, an identity, a Connected System, a Metaverse Object. "The row stays, marked Cancelled" tells an administrator nothing about whether their data is safe, because they have never seen the schema and should never need to; "the change stays, marked Cancelled" says it exactly. The word is only allowed where it is genuinely the reader's own: a row of a table they are looking at on screen, or a row in a SQL system JIM is connected to.

**VOI-2. Write in the present tense, in the active voice, to "you".** JIM is the subject of what JIM does: "JIM refuses to send", not "the request will be refused". The reader is the subject of what they do: "turn this on once you know the connection is encrypted". Passive constructions hide who acts, which in identity management is exactly the fact the reader needs.

**VOI-3. Hedge only where JIM is genuinely uncertain.** "May", "should" and "typically" are load-bearing when a target system's behaviour is out of JIM's control, and are noise everywhere else. If JIM guarantees something, say it flatly; a page that hedges uniformly gives the reader no way to tell a guarantee from a likelihood.

**VOI-4. Leave JIM's own workings out of a public page.** No GitHub issue or pull request numbers, no JIM development or test infrastructure (the devcontainer, the integration lab), no design defence ("this is deliberate", "we chose"), and no class, method, property or table name unless the reader types it. These are for engineers, and they belong in `engineering/`.

**VOI-5. Prefer prose and diagrams to screenshots.** The site has none by design, and adding one costs more than it looks: it is invalidated by any change to the control it shows, it cannot be searched, it is invisible to a screen reader unless the alt text repeats it, and it is the thing nobody remembers to update. Name the control and say what it does instead. A diagram is different: it is the right answer where the shape of something is the point, and every page has one ([Visuals](#visuals)). Make its `<desc>` carry the same information as the picture rather than describing the picture.

**SAFE-1. Answer "is anything lost?" before the reader has to ask.** Any operation that sounds destructive, and every refusal, states what survives it in the same paragraph. "Nothing is discarded when JIM refuses: queued password changes wait, and accounts stay owed their first password" is the shape. An administrator reading about a feature that stops, refuses, expires, cancels or deletes is deciding whether to trust it with production; a page that leaves that unanswered gets read defensively, and the feature gets left switched off.

**SAFE-2. Name a failure and its remedy in the same breath.** A page that says something can fail, park, expire or be refused says what the reader does about it, or links to where that is. A symptom with no remedy is a support ticket that has not been raised yet.

**HOME-1. One home per fact.** Where two pages would both explain something, one explains it and the other links. Duplicated prose does not stay duplicated: it drifts, and then the reader gets two answers with no way to tell which is current. The audit found twelve pages whose copies already contradicted each other. This is also why the docs never restate the generated REST API reference.

## Language

The cross-cutting rules in the root `CLAUDE.md` apply to every page exactly as they apply to code. `Test-DocsConformance.ps1` checks each page's running text, leaving out code, link targets, URLs and HTML, and matches only wording that is wrong wherever it appears. Where the same words are usually right ("now", "currently", "just", a lower-case "schedule"), the reviewer judges instead, because a check that is mostly false alarms teaches authors to ignore it.

| ID | Tier | Rule | What the check matches |
|----|------|------|------------------------|
| **LANG-1** | Block | Never an em dash. Use a semicolon, comma, colon or parentheses. Never a literal `--` as a dash either: the site has no smart-punctuation extension, so readers see two hyphens. | An em dash, and `--` with a space either side or between two words or numbers (write a range with "to": 1 to 2 GB). |
| **LANG-2** | Block | British English throughout ("synchronisation", "authorisation", "behaviour"). A third-party product's on-screen label keeps its own spelling ("Authorization" in Keycloak). | Words ending *-ize*, *-ized*, *-izing*, *-ization* and so on, less the English words that only look like them (*size*, *seize*); and a list of others (*behavior*, *color*, *center*, *canceled*) in `docs/.conformance/config.psd1`, which also lists the third-party and protocol terms that keep their spelling ("authorization code", "Global Catalog"). |
| **LANG-3** | Ratchet | JIM's domain nouns are proper nouns in Title Case, even mid-sentence: Connected System, Metaverse, Metaverse Object, Synchronisation Rule, Run Profile, Connector Space, Pending Export. Always "Synchronisation Rule" in full, never "Sync Rule". | Each glossary term the vocabulary holds to Title Case, written in any other case; and the vocabulary's rejected variants ("Sync Rule", "Connector Space Object"). |
| **LANG-4** | Ratchet | Abbreviations such as CSO, MVO and RPEI only in tight table headers, with the full term nearby; never in running prose. | The vocabulary's abbreviations anywhere but a table's header row, unless the full term introduces them ("Metaverse Object (MVO)"). |
| **LANG-5** | Block | No time-relative words ("now", "currently", "until now", "previously", "no longer", "this release"). The site is unversioned while customers run several releases, so "now" is wrong for some reader whatever it says. Behaviour that differs by release carries a marker: `Available from v0.17.0`, or names the releases ("v0.16.0 or earlier"). Upgrade-specific notes live in `administration/upgrading.md` and the changelog only. | "Until now", "this release", "in this version", "(previously", "previously called". |
| **LANG-6** | Block | No promises about the future ("planned", "will be added", "coming soon"). Say what JIM does today; the roadmap is `reference/roadmap.md`. | "Is planned", "planned for", "future release", "coming soon", "will be added" and similar. |
| **LANG-7** | Ratchet | No minimisers ("simply", "just", "easy"). What is simple to the writer is the reader's problem. | "Simply", "straightforward", "obviously", "of course". |
| **LANG-8** | Block | No GitHub issue or pull request numbers on a public page (VOI-4). | `#` followed by digits in running text, and links to GitHub issues, pull requests and discussions. |
| **LANG-9** | Review | No names of competing identity products; use generic terms ("traditional ILM solutions"). Industry standards (SCIM, LDAP, OIDC) are fine. | Not checked: a check would have to list the very names this rule keeps out of the repository. |

**The product vocabulary.** The glossary (`reference/glossary.md`) is the one place each product noun is named and defined: its entry gives the canonical form and the anchor to link to. `docs/.conformance/vocabulary.psd1` adds only what the glossary cannot hold: each term's home page (HOME-1), whether it is held to Title Case (every multi-word name, and "Metaverse"; not the single words that are also ordinary English, such as "Activity", "Export" and "Schedule"), and the variants a page must not use. TERM-1 checks the two against each other both ways. The portal's term hints are checked against the glossary too (`GlossaryTermConsistencyTests`), so the portal, the glossary and the pages use one set of names. A new product noun needs a glossary entry and a vocabulary entry in the same pull request.

## Maintaining pages

`changelog-lint` makes every user-facing pull request touch `docs/`, which is right, but it proves only that *a* page changed. These rules are about changing the *right* page in the right way.

**MNT-1 (Review). Integrate, don't append.** A change to documented behaviour rewrites the page so it reads as though the new behaviour had always been true. Superseded sentences are replaced or deleted, never qualified ("previously ... now ..."). New material goes where the page type puts it: a new task under the tasks, a new setting in the settings table, a new failure in troubleshooting. Release history goes in the changelog and `administration/upgrading.md`, and nowhere else. The page this rule exists for, `configuration/synchronisation-rules.md`, was changed by 77 commits in six months and reached 17,800 words one appended paragraph at a time.

**MNT-2 (Review). Find what is now wrong before writing what is new.** The likelier failure is not writing something false, it is *leaving* something true-when-written. The LDAP Connector reference told readers JIM "will not stop you" sending a password over an unencrypted connection for two releases after the setting that does stop you shipped, because the change added a new page and nobody searched for the old claim. When you change behaviour, search `docs/` for the sentence that is now wrong before you search for where to describe the new thing.

**MNT-3 (Block). A page over its size budget cannot grow.** A pull request may not increase the word count of a page that was over its size budget (STR-8) on the base branch, unless it also splits the page or removes as much as it adds. The comparison is against the base branch, so trimming a large page never needs a baseline update. A page that only now crosses the budget gets a warning (STR-8), not a failure. The rule warned until the eleven pages already over budget were split, since blocking sooner would have failed most feature pull requests; with none over budget, it blocks.

## Accuracy and links

| ID | Tier | Rule | Checked by |
|----|------|------|------------|
| **ACC-1** | Block | A PowerShell example invokes a cmdlet with each parameter's real name, never a declared alias. An alias reads like a different parameter: `Get-JIMRunProfile -Id 42` means "every Run Profile on Connected System 42". | `Lint-DocExamples.ps1` |
| **ACC-2** | Block | Every JIM cmdlet a page names exists, and every parameter an example passes binds: declared, a common parameter, or `-WhatIf`/`-Confirm` where the cmdlet supports them; never abbreviated. | `Lint-DocExamples.ps1` |
| **ACC-3** | Block | Every REST route a page gives exists in the OpenAPI document, and takes the method given with it. Write routes in full, as the API reference names them: `/connected-systems/{id}/clear` fails, naming `/api/v1/synchronisation/connected-systems/{connectedSystemId}/clear`. A path given without a method names a resource rather than a call, so it may stop short of a whole route (`/api/v1/metaverse`). | `Test-DocsCoverage.ps1`, in `build-and-test`, which builds the OpenAPI document it reads |
| **ACC-4** | Review | Every factual claim is a claim about source, so check it there. Parameter names, property names, setting keys, defaults, file paths, endpoint routes, counts and limits are all verifiable, and none is verified by the build. State a default alongside the setting key that carries it, so a reader who finds the default has drifted can find what to change. | |
| **ACC-5** | Block | A cmdlet's Syntax, Parameters and Output on a `docs/powershell/` page are generated from the module, between `<!-- BEGIN GENERATED: <cmdlet> -->` and `<!-- END GENERATED: <cmdlet> -->`, and match it. Change the cmdlet (its parameters, its `.PARAMETER` and `.OUTPUTS` help, its `[OutputType('JIM.<Schema>', ParameterSetName = '<Set>')]`), then regenerate; never edit inside the markers. A cmdlet with a block describes every parameter and declares what it returns. Every cmdlet the module exports has a block; one without fails. | `Update-PowerShellReference.ps1 -Check`, in `build-and-test`, which builds the OpenAPI document it reads |
| **ACC-6** | Block | A property a PowerShell example reads from a JIM cmdlet's output is one the cmdlet returns for the parameter set the example binds: read through `Select-Object`, `Sort-Object`, `Where-Object`, `ForEach-Object` and the like, as `$_.Name` in a script block given to them, from a variable the output was assigned to, or from a parenthesised call. A property that does not exist prints nothing and raises no error, so a reader who copies the example gets blank output. A property the module adds to the API's object counts once its `.OUTPUTS` help lists it (`Name (type): description`). Cmdlets whose output nothing describes are not checked. | `Lint-DocExamples.ps1 -OpenApiPath`, in `build-and-test`, which builds the OpenAPI document it reads |
| **LNK-1** | Block | Every link and anchor in the Markdown resolves. Grep the target file for the heading rather than inferring the slug: `#passwords` and `#password-synchronisation` look equally plausible. | `Test-DocsSite.ps1` (strict MkDocs build) |
| **LNK-2** | Block | Every link in the built site resolves, including links into the generated API reference and links written in raw HTML. | `Test-DocsSite.ps1` (lychee) |
| **LNK-3** | Block | Every link from the product into the docs (the portal's glossary links, and `docs.junctional.io` URLs in source, the installer and the deployment files) resolves to a page and anchor. | `Test-DocsCoverage.ps1`; the glossary links, built from a template, by `GlossaryTermConsistencyTests` |

**The generated references are not restated.** REST endpoint specifications live in the Scalar reference, rendered from the OpenAPI document at build time; `docs/api/` holds only overview pages that link into it. PowerShell cmdlet pages carry their reference inline, because each sits beside the examples and notes that explain it: each cmdlet's Syntax, Parameters and Output are generated into the page from the module (ACC-5), and the rest of the section is written by hand. Every property an example reads from a cmdlet's output is checked against what it returns (ACC-6).

## Accessibility

| ID | Tier | Rule |
|----|------|------|
| **A11Y-1** | Block | Every Mermaid diagram carries `accTitle` and `accDescr`, so a screen reader gets what the picture shows. |
| **A11Y-2** | Block | Every image has alt text, and every included concept diagram has a `<title>` and a `<desc>`. |
| **A11Y-3** | Review | Link text says where it goes ("see [Run Profiles](...)"), never "click here" or "this page". |

## Visuals

A diagram gives the reader the shape of a subject (what flows where, which state follows which, what holds what) before the detail, and the shape is what they remember. Every page has at least one, specific to it. Most are a short `jim-diagram` spec written in the page, where the diagram appears: the build draws it in the house style, in both themes, with a phone layout and a text alternative, so a diagram is a few lines of text rather than a drawing. How to write one is in `docs/CLAUDE.md` > Generated diagrams; the visual language is in `engineering/DESIGN.md` > Diagrams. Screenshots are not diagrams, and the site has none (VOI-5).

| ID | Tier | Rule |
|----|------|------|
| **VIS-1** | Ratchet | The page has at least one diagram: a `jim-diagram` spec, a concept diagram included from `assets/diagrams/` (`--8<-- "assets/diagrams/<name>.svg"`), an inline `<svg class="jim-diagram">`, or a Mermaid diagram. One shown inside another code block, as an example of how to write one, does not count. The Changelog page is excused in place, since its entries are generated from `CHANGELOG.md`. A new page has its diagram from its first commit. |
| **VIS-2** | Review | A section whose text describes a flow of three or more steps or components, a lifecycle of states, relationships among three or more things, a decision that branches, or a precedence or ordering, has a diagram. A list of settings, a single step, or anything a table already says better, does not need one. |
| **VIS-3** | Review | A diagram carries information specific to its page: the page's own steps, states, objects and outcomes, named as the page names them. No decorative or generic diagrams; one that would sit as well on any other page of its section is generic. |
| **VIS-4** | Review | What a diagram shows matches the page's text and the product: the same steps in the same order, the same names, the same outcomes, and nothing the product does not do. This is ACC-4 for pictures: check a diagram against the source as you would a sentence, and change the diagram and the text in the same edit. |

## Style conventions

STY-1 blocks: `Test-DocsConformance.ps1` checks each page's Markdown, and `Test-DocsSite.ps1` checks the built site, which also covers text a build hook or an inlined diagram puts on a page. STY-3 blocks on where heading icons go (`Test-DocsConformance.ps1`) and is Review tier for the rest. STY-2 is Review tier.

**STY-1. No emoji on a page; icons come from Material Design Icons, which the theme ships.** Emoji bring their own palettes and a casual register into every heading and table, and an emoji set cannot match the site's colours. A yes, no or caution in a table or list is a status mark, always with its spoken label, because a screen reader reads nothing for an unlabelled icon:

| Meaning | Write |
|---------|-------|
| Yes, supported, allowed | `:material-check-circle-outline:{ .jim-yes role="img" aria-label="Yes" }` |
| No, unsupported, not allowed | `:material-close-circle-outline:{ .jim-no role="img" aria-label="No" }` |
| Caution, see below | `:material-alert-outline:{ .jim-warn role="img" aria-label="Caution" }` |

The classes colour the mark green, red or amber in both themes (`custom.css`). Emoji inside code and code blocks are exempt, since they show what a reader types or sees.

**STY-2. Put a term-and-description bullet's description on a new line, with `<br />`.** The first word of the description is capitalised, because it starts a new line. This applies to feature lists, capability lists and similar label-and-explanation structures, not to short flat lists of single phrases.

```markdown
- **Term**<br /> Descriptive text explaining the term.
```

Not `- **Term** -- descriptive text` (which also breaks LANG-1).

**STY-3. Every H2 carries an icon; nothing below it does.** On every page, every H2 opens with one Material Design icon (`## :material-layers-outline: Architecture`), and every grid card opens with one (`:material-sync:{ .lg .middle }`); `custom.css` draws both as a tile in the brand colour, which marks each section break on a long page. H3 and lower stay plain, so the tile always means a section. The check fails an H2 without a Material Design icon and an icon on any other heading; the Changelog page's release headings get theirs from the build hook. Pick for the section's subject, and reuse one icon per recurring concept and heading across the site: Connected System `server-network-outline`, Metaverse `hub-outline`, Synchronisation Rule `link-variant`, Run Profile `play-circle-outline`, Schedule `calendar-clock-outline`, settings `tune-variant`, troubleshooting `wrench-outline`, "See also" and "Related" `book-open-variant-outline`, "Check it worked" `check-decagram-outline`, "Before you begin" `clipboard-list-outline`; never the status-mark icons (STY-1) on a heading. Use the `-outline` variant wherever Material Design Icons has one, so the set reads as line icons rather than a mix of line and solid shapes; a brand's own logo (`:material-microsoft:`) is the exception. Never an icon mid-sentence, and never one as a checklist (use `- [ ]` / `- [x]`, which `pymdownx.tasklist` renders as styled checkboxes). `CHANGELOG.md` keeps its leading emoji per entry (its own convention, `engineering/CLAUDE.md`), and the build hook that publishes it as the Changelog page strips them (`docs/hooks/changelog.py`).

## Priority pages and tutorials

**Priority pages** are the 15 or so pages that decide whether JIM is adopted, installed and trusted (PRD requirement 36; Decision 7), listed in `docs/.conformance/config.psd1`. They migrate first, ahead of any folder. Once conformant, every warning fails on them and their size budget is 3,000 words. The list stays short on purpose: every page on it costs a re-validation each release, so a page belongs on it only if an administrator who cannot use it would stall or lose trust in JIM.

| ID | Tier | Rule |
|----|------|------|
| **PRI-1** | Block | Each conformant priority page records the release it was last checked against (`validated: vX.Y.Z`, no later than `VERSION`), and no other page records one. `/release` lists every priority page due for re-validation (`scripts/Get-DocsDueForValidation.ps1`); the release proceeds once each is re-validated or explicitly deferred, with a reason, in the release pull request. Validating means reading the page against the product as the release ships it (ACC-4), not re-running the scripts, which run on every pull request anyway. |
| **PRI-2** | Retired | Folded into the Review tier, which now posts every page's findings as review threads, each answered by a fix or a reply. It began as a priority-page rule. |
| **TUT-1** | Block | A tutorial is tested. Its front matter names the integration scenario that runs it (`scenario:`; TYP-1 checks the script exists), and the scenario executes the tutorial's PowerShell steps from the page itself and asserts each checkpoint it states, in the pre-release suite, so a release cannot ship while the tutorial is broken. Tutorials are few, and tested; never scarce for fear of breaking. |
| **TUT-2** | Block | Every label a tutorial's Portal tab names in bold is one the portal shows: the check finds it in the portal's source (`PortalLabelSources` in `docs/.conformance/config.psd1`), ignoring case. A label the portal builds from data ("Project Users to the Metaverse?") is excused in place with a marker saying so. This proves the labels exist, not that the clicks work. |

**Writing a tutorial.** Give each step's portal and PowerShell forms as content tabs (`=== "Portal"`, `=== "PowerShell"`), the portal form as numbered steps naming each control in bold. The scenario runs every fenced `powershell` block inside a PowerShell tab, in page order and in one session, so a block can use the variables an earlier one set; code anywhere else is not run, so the reader's own setup (`Connect-JIM` in **Before you begin**) and shell commands (copying a file onto the server) sit outside the PowerShell tabs, and the scenario does the same work itself. Mark each thing the page tells the reader to expect, immediately above the sentence that says it:

- `<!-- checkpoint: <PowerShell that returns $true> -->`, run after the steps above it, such as `(Get-JIMPendingExport -ConnectedSystemId $directory.Id -Count) -eq 25`;
- `<!-- checkpoint-file: /connector-files/<path> rows=<n> -->`, for a file in the connector files volume.

Scope each checkpoint to the tutorial's own objects, since the pre-release suite runs other scenarios in the same environment. The tutorial's sample data is published with the page under `docs/assets/`, and the scenario reads that same file. `test/integration/utils/Tutorial-Helpers.Tests.ps1` reads every tutorial page on each pull request, so a marker that cannot be read, or a block that does not parse, fails there; the scenario runs at release time.

## Coverage

"Complete coverage" is defined against inventories generated from source, never hand-maintained lists, and each is checked both ways: something that exists but is undocumented fails, and so does a name in the docs that no longer exists. COV-1, COV-2, COV-4, COV-5 and the expression functions of COV-6 are checked by `Test-DocsCoverage.ps1`, and TERM-1 by `Test-DocsConformance.ps1`. COV-3 and the Connector settings of COV-6 take their inventories from the compiled product, so NUnit tests in `test/JIM.Worker.Tests/Docs/` check them (`ServiceSettingDocsCoverageTests`, `ConnectorSettingDocsCoverageTests`), and `ServiceSettingSeedingCoverageTests` holds that every Service Setting key is seeded. All of these block.

| ID | Inventory | Documented means |
|----|-----------|------------------|
| **COV-1** | Exported cmdlets (module manifest) | A heading, or a row in the first column of a table, on a `docs/powershell/` page |
| **COV-2** | Environment variables the product and installer read: `Constants.Config` and `GetEnvironmentVariable` reads, the PowerShell module's `$env:` reads, the compose files, `.env.example`, and the variables the installer's header documents | A row in a settings table |
| **COV-3** | Service Settings (`Constants.SettingKeys`, each one seeded) | A row in the Service Settings table of the Configuration Reference (`docs/administration/configuration.md`) |
| **COV-4** | REST resource groups (OpenAPI tags) | A row in the Resource groups table on the API overview page (`docs/api/index.md`) |
| **COV-5** | Portal pages (Razor components with an `@page` directive) | The page's `PageInfo` names the docs page that covers it (`DocsPage="configuration/connected-systems.md#the-connector-space"`), and that page and heading exist; where the value is an expression, every page it can choose is a literal and exists |
| **COV-6** | Connector settings (each built-in Connector's `GetSettings()`) and expression functions | A row in a Setting table on the Connector's page (the page `docs/connectors/index.md` links under its name), naming the setting exactly as the settings form shows it; a row in the function reference's table |
| **TERM-1** | Product nouns: the glossary's entries, and `docs/.conformance/vocabulary.psd1` (see "The product vocabulary" under Language) | A glossary entry with an anchor, a vocabulary entry, and a home page that exists (HOME-1) |

"Documented" means a heading, the first column of a reference table, or a glossary entry with an anchor; a mention in running text does not count. Deliberate exclusions live in one allow-list (`docs/.conformance/coverage.psd1`) with a reason per entry, and an entry for something that no longer exists, or is now documented, fails.

## Rule index

| ID | Rule | Tier | Checked by |
|----|------|------|------------|
| AUD-1 | Written for its declared audience | Review | |
| STR-1 | Front matter is valid | Block | `Test-DocsConformance.ps1` |
| STR-2 | Front matter declares title, description, type and audience | Ratchet | `Test-DocsConformance.ps1` |
| STR-3 | One name for the page | Block | `Test-DocsConformance.ps1` |
| STR-4 | One H1, no skipped heading levels | Block | `Test-DocsConformance.ps1` |
| STR-5 | Code blocks name their language | Block | `Test-DocsConformance.ps1` |
| STR-6 | No stub pages | Ratchet | `Test-DocsConformance.ps1` |
| STR-7 | Opens with a paragraph | Warn | `Test-DocsConformance.ps1` |
| STR-8 | Within its size budget | Warn | `Test-DocsConformance.ps1` |
| TYP-1 | Has the shape its type requires | Block | `Test-DocsConformance.ps1` |
| VOI-1 | Domain concept, never implementation | Review | |
| VOI-2 | Present tense, active voice, "you" | Review | |
| VOI-3 | Hedge only where JIM is uncertain | Review | |
| VOI-4 | JIM's own workings stay out | Review | |
| VOI-5 | Prose and diagrams, not screenshots | Review | |
| SAFE-1 | Says what survives | Review | |
| SAFE-2 | Names the remedy with the failure | Review | |
| HOME-1 | One home per fact | Review | |
| LANG-1 | No em dash, no `--` as a dash | Block | `Test-DocsConformance.ps1` |
| LANG-2 | British English | Block | `Test-DocsConformance.ps1` |
| LANG-3 | Product nouns in Title Case, no rejected variants | Ratchet | `Test-DocsConformance.ps1` |
| LANG-4 | Abbreviations only in table headers | Ratchet | `Test-DocsConformance.ps1` |
| LANG-5 | No time-relative wording | Block (the unambiguous phrases); Review (the rest) | `Test-DocsConformance.ps1` |
| LANG-6 | No promises about the future | Block | `Test-DocsConformance.ps1` |
| LANG-7 | No minimisers | Ratchet ("simply" and its kind); Review ("just", "easy") | `Test-DocsConformance.ps1` |
| LANG-8 | No issue or pull request numbers | Block | `Test-DocsConformance.ps1` |
| LANG-9 | No competing products named | Review | |
| MNT-1 | Integrate, don't append | Review | |
| MNT-2 | Find what is now wrong first | Review | |
| MNT-3 | An over-budget page cannot grow | Block | `Test-DocsConformance.ps1` |
| ACC-1 | Real parameter names, never aliases | Block | `Lint-DocExamples.ps1` |
| ACC-2 | Cmdlets and parameters exist | Block | `Lint-DocExamples.ps1` |
| ACC-3 | REST routes exist | Block | `Test-DocsCoverage.ps1` |
| ACC-4 | Claims checked against source | Review | |
| ACC-5 | PowerShell reference generated from the module | Block | `Update-PowerShellReference.ps1` |
| ACC-6 | Example properties exist | Block | `Lint-DocExamples.ps1` |
| LNK-1 | Markdown links and anchors resolve | Block | `Test-DocsSite.ps1` |
| LNK-2 | Built-site links resolve | Block | `Test-DocsSite.ps1` |
| LNK-3 | Product deep links resolve | Block | `Test-DocsCoverage.ps1` |
| A11Y-1 | Mermaid diagrams carry accTitle and accDescr | Block | `Test-DocsConformance.ps1` |
| A11Y-2 | Images and diagrams carry text alternatives | Block | `Test-DocsConformance.ps1` |
| A11Y-3 | Meaningful link text | Review | |
| VIS-1 | At least one diagram on every page | Ratchet | `Test-DocsConformance.ps1` |
| VIS-2 | A diagram where a section describes a shape | Review | |
| VIS-3 | Diagrams specific to their page, never decorative | Review | |
| VIS-4 | Diagrams match the text and the product | Review | |
| STY-1 | No emoji; status marks and icons from Material Design Icons | Block | `Test-DocsConformance.ps1`, `Test-DocsSite.ps1` |
| STY-2 | Term-and-description bullets | Review | |
| STY-3 | An icon on every H2 and grid card, and on no other heading | Block (heading placement); Review (the rest) | `Test-DocsConformance.ps1` |
| PRI-1 | Priority pages record their last validation | Block | `Test-DocsConformance.ps1`; listed for each release by `Get-DocsDueForValidation.ps1` |
| PRI-2 | Review findings arrive as review threads (every page's, under the Review tier) | Retired | |
| TUT-1 | Tutorials are tested | Block | `Test-DocsConformance.ps1` (TYP-1, the scenario named); the tutorial's scenario in the pre-release suite (the steps executed) |
| TUT-2 | A tutorial names only labels the portal shows | Block | `Test-DocsConformance.ps1` |
| COV-1, COV-2, COV-4 | Cmdlets, environment variables and REST resource groups are documented | Block | `Test-DocsCoverage.ps1` |
| COV-3 | Service Settings are documented, and seeded | Block | `ServiceSettingDocsCoverageTests`, `ServiceSettingSeedingCoverageTests` |
| COV-6 | Connector settings and expression functions are documented | Block | `ConnectorSettingDocsCoverageTests`; `Test-DocsCoverage.ps1` |
| COV-5 | Portal pages link the docs page that covers them | Block | `Test-DocsCoverage.ps1` |
| TERM-1 | The vocabulary and the glossary agree; every product noun has a home page | Block | `Test-DocsConformance.ps1` |
| EXC-1 | Exception markers are well formed and in use | Block | `Test-DocsConformance.ps1` |
