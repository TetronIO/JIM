# Public documentation standards and enforcement for JIM (issue #1454)

Research date: 2026-10-11. Scope: what makes great public product documentation, what a JIM specification should contain, which enforcement tools fit JIM's constraints, and a tiered, ratcheted rollout against the existing site. The repository was not modified; all measurements were taken on a scratch copy of `docs/`.

---

## 0. Headline findings

1. **The docs ship with no PR-time validation at all.** `mkdocs build --strict` runs only on push to `main` (`.github/workflows/docs.yml`), and even then broken anchors are only `info` (MkDocs 1.6 default), so they never fail. Turning on `validation.links.anchors: warn`, `nav.omitted_files: warn` and `absolute_links: warn` produces **zero** warnings on today's docs (verified; I also verified it does catch injected broken page, cross-page anchor and same-page anchor links). That gate can go blocking at PR time immediately, at no migration cost.
2. **Real defects the current pipeline cannot see** (all found by cheap mechanical checks during this research):
   - `docs/powershell/system.md:351` links `../api/reference/`, which resolves to `/powershell/api/reference/` (404). MkDocs cannot see it because `mkdocs.yml` sets `unrecognized_links: ignore`; `lychee --offline` over the built site found it as the only broken link out of 20,820.
   - `docs/administration/portal-lists.md` and `docs/getting-started/prerequisites.md` have **invalid YAML front matter** (an unquoted `: ` inside `description`). MkDocs silently discards the whole block: the built pages carry no `<meta name="description">`, with no warning even under `--strict`.
   - `docs/powershell/connected-systems.md:153` runs `Get-JIMConnectedSystem -All`; that cmdlet has no `-All` parameter (list is the parameterless default), so the example fails when pasted. An AST check of every ```powershell fence against the module's real parameter sets found exactly this one.
   - `Get-JIMDataFlow` is exported but has no reference section under `docs/powershell/`; `JIM_CACHE_WARM_PARALLELISM` is read by `src/JIM.Worker/Worker.cs:94` and appears nowhere in `docs/`.
   - `RPEI`/`RPEIs` appears 155 times in `docs/` (150 in `developer/diagrams/`, 2 on customer pages) and has no glossary entry; "Object Type" (322 uses, including compounds such as "Metaverse Object Type"), "Full Import" (221), "Delta Import" (169), "Worker Task", "Predefined Search", "API Key" and "Service Settings" are also absent as glossary entries.
   - `getting-started/first-synchronisation.md` is a 63-word "Documentation In Progress" stub sitting in the Getting Started nav; Diátaxis explicitly warns against empty structures.
   - Eight pages have a front matter `title` that differs from their H1 (for example "Deployment" vs "Deployment Guide"), plus a third name in `nav`: a "one name per thing" violation.
   - `docs/overrides/home.html` (the Jinja theme override) is published as a static file at `/overrides/home.html`, because `custom_dir` lives inside `docs_dir`.
   - `engineering/prompts/api-docs-section.md` tells authors to create per-endpoint pages under `docs/api/<resource>/` modelled on `docs/api/connected-systems/*`, which do not exist and which `docs/CLAUDE.md` forbids. Stale guidance is part of the drift problem.
3. **Off-the-shelf prose style packages are unusable for JIM.** Vale with the Google, Microsoft, write-good and proselint packages produced **38,998 alerts (4,706 errors) across 99 pages**, many contradicting JIM's own conventions (semicolons are JIM's mandated em-dash replacement; Title Case domain nouns conflict with sentence-case heading rules; contractions). A small JIM-only Vale style (11 rules, about 120 lines of YAML) produced 1,242 alerts, of which the error-level ones are concentrated and mostly genuine (194 lowercase domain nouns, about 7 US spellings, 1 meaningless link text, 0 em dashes).
4. **The toolchain is ageing out underneath any MkDocs-specific enforcement.** MkDocs 1.6.1 (August 2024) is the last 1.x release; Material for MkDocs entered maintenance mode in November 2025 with fixes promised only until about 5 November 2026, and its own build banner warns that MkDocs 2.0 removes the plugin system with no migration path. The successor, Zensical, is at 0.0.69 (Alpha) but already has link and anchor validation. Enforcement should therefore lint **Markdown sources** and **built HTML**, both generator-agnostic, and treat MkDocs `validation:` as a cheap bonus, not a foundation.
5. **Recommended design:** a single PowerShell orchestrator (`scripts/Test-DocsConformance.ps1`) run inside the existing required `changelog-lint` job; blocking rules only where a defect class is proven; every other mechanical rule as annotations; a shrink-only per-file, per-rule baseline (ESLint "bulk suppressions" semantics) so the ~100 existing pages migrate folder by folder while every new page is born conformant; and an advisory LLM rubric review for the judgement-only rules. New dependencies needing approval: Vale and lychee (both single static binaries, MIT / Apache-2.0-or-MIT, SHA-256 published). markdownlint-cli2 is not recommended.

Note on page count: `docs/` holds 101 Markdown files; excluding `docs/CLAUDE.md` (not published) and the `reference/changelog.md` snippet wrapper, there are **99 authored pages** (126 files including assets), not ~125.

---

## Part 1: What makes great public documentation

### 1.1 Sources surveyed

| Source | What it contributes | URL |
|---|---|---|
| Diátaxis (Daniele Procida) | Four modes by user need; the compass; iterative migration | https://diataxis.fr/ , https://diataxis.fr/compass/ , https://diataxis.fr/how-to-guides/ , https://diataxis.fr/reference/ , https://diataxis.fr/how-to-use-diataxis/ |
| Google developer documentation style guide | Voice, procedures, timeless language, accessibility | https://developers.google.com/style/highlights , https://developers.google.com/style/procedures , https://developers.google.com/style/timeless-documentation , https://developers.google.com/style/accessibility |
| Google Technical Writing One | Audience definition, knowledge gap | https://developers.google.com/tech-writing/one/audience |
| Microsoft Writing Style Guide / Learn contributor guide | Voice, step rules, required metadata (`ms.date`, `ms.author`, `ms.topic`, `description`) | https://learn.microsoft.com/en-us/style-guide/top-10-tips-style-voice , https://learn.microsoft.com/en-us/style-guide/procedures-instructions/writing-step-by-step-instructions , https://learn.microsoft.com/en-us/contribute/content/metadata |
| GitLab documentation guide | Topic types with templates; Vale/markdownlint/lychee in CI; word list mapped to Vale rules; ownership metadata | https://docs.gitlab.com/development/documentation/topic_types/ , .../topic_types/task/ , .../topic_types/concept/ , .../topic_types/troubleshooting/ , https://docs.gitlab.com/development/documentation/testing/ , https://docs.gitlab.com/development/documentation/testing/vale/ , https://docs.gitlab.com/development/documentation/metadata/ , https://docs.gitlab.com/development/documentation/styleguide/ , https://docs.gitlab.com/development/documentation/styleguide/word_list/ |
| Write the Docs | Documentation principles; docs as code | https://www.writethedocs.org/guide/writing/docs-principles/ , https://www.writethedocs.org/guide/docs-as-code/ |
| Every Page is Page One (Mark Baker) | Seven characteristics of a standalone topic | https://everypageispageone.com/the-book/ |
| Software Engineering at Google, ch. 10 | Owners, freshness dates, three kinds of review, who/what/when/where/why | https://abseil.io/resources/swe-book/html/ch10.html |
| Kubernetes style guide | No statements about the future; no "currently"/"new"; version in front matter | https://kubernetes.io/docs/contribute/style/style-guide/ |
| Cloudflare style guide | 16 content types; strict how-to and troubleshooting templates; blocking internal links, non-blocking external and anchor audits; schema-validated front matter; AI usage | https://developers.cloudflare.com/style-guide/ , .../documentation-content-strategy/content-types/ , .../content-types/how-to/ , .../content-types/troubleshooting/ , .../how-we-docs/links/ , .../how-we-docs/metadata/ , .../how-we-docs/how-we-ai/ |
| HashiCorp web-unified-docs content guide | Diátaxis-based Usage/Concept/Overview/Reference templates (read via a third-party mirror, so treat as secondary) | https://mintlify.wiki/hashicorp/web-unified-docs/content-guides/content-types |
| Tailscale | "Last validated" date and plan-availability note at the top of every page; dedicated "Verify your connection" section (observed on a live page; Tailscale publishes no style guide I could find) | https://tailscale.com/kb/1019/subnets , https://tailscale.com/docs.md |
| Stripe | Docs as a career-ladder expectation; Markdoc gives build-time validation (secondary, vendor-authored sources only) | https://mintlify.com/blog/stripe-docs , https://writethedocs-www.readthedocs.io/videos/prague/2022/two-years-of-markdoc-what-we-ve-learned-about-balancing-developer-and-author-ex-ryan-paul/ |
| WCAG 2.2 | Success criteria relevant to written content | https://www.w3.org/WAI/WCAG22/quickref/ |

Caveat: several pages were read through a summarising fetch tool, so short quotations below are faithful to the summaries but may differ in small wording from the originals. Stripe and HashiCorp claims rest on secondary sources.

### 1.2 Principles, sources and checkability

Legend: **M** mechanically checkable (a deterministic tool can decide it); **P** partially checkable (a tool can detect presence/shape or likely violations, a human decides); **J** judgement-only.

#### Audience and personas

| Principle | Source | Check |
|---|---|---|
| Define the audience as a role plus proximity to the subject; good documentation = what the reader needs to do the task minus what they already know. Beware the curse of knowledge. | Google Tech Writing One | J |
| Write from the customer's perspective, not the contributor's. | GitLab style guide | J |
| Assume a qualified reader, but give an unqualified one enough orientation to recognise the gap and a link to close it. | EPPO (Baker) | J |
| State who the page is for in the opening (who/what/why). | SWE at Google ch. 10 | P (an `audience:` front matter key is M; the sentence is J) |

**For JIM**, issue #1454 states the problem directly: pages read as if written for JIM's developers rather than for the people implementing JIM. Proposed personas, each mapped to where it is primarily served:

| Persona | Needs | Primary folders |
|---|---|---|
| Identity administrator implementing JIM | What JIM can do, how to configure it, why it matters, what is safe | `concepts/`, `configuration/`, `connectors/` |
| Platform operator | Install, upgrade, TLS, SSO, backup, troubleshooting | `getting-started/`, `administration/` |
| Automation engineer | PowerShell and REST, exact names, idempotent scripts | `powershell/`, `api/` |
| Security and compliance assessor (healthcare, finance, government) | Guarantees, audit events, data handling, what survives failure | `administration/security-*`, `concepts/` |
| Contributor / connector author | Architecture, internals, build | `developer/` only |

The last persona legitimately uses implementation vocabulary (150 of the 155 uses of "RPEI" are in `developer/diagrams/`). Treating `developer/` as a separate rule profile is what makes the customer-voice rules enforceable elsewhere.

#### Voice and tone

| Principle | Source | Check |
|---|---|---|
| Second person ("you"); active voice so the actor is visible; present tense. | Google highlights; Kubernetes; `docs/CLAUDE.md` | P (passive detectors are noisy: write-good flagged 3,012 passive constructions in JIM docs) |
| Conversational but precise; no marketing language; avoid "simply", "easily", "just". | GitLab (Vale `Simplicity.yml`); Microsoft top 10 | M for the word list |
| Avoid "we"; the reader may not know who "we" is. | Kubernetes | M (word list; 7 hits in JIM) |
| No "please" in instructions. | GitLab word list; Google procedures | M (4 hits) |
| Get to the point fast; lead with what matters; front-load keywords. | Microsoft top 10 | J |
| Hedge only where genuinely uncertain; flat guarantees otherwise. | `docs/CLAUDE.md` (JIM's own) | J |
| British English; no em dash; Title Case domain nouns. | Root `CLAUDE.md` (JIM's own) | M |

Note on conflicts: Microsoft's "use contractions" and "sentence-case everything" and Google's "avoid semicolons" contradict JIM's established style. A JIM specification should adopt the principles, not a vendor style wholesale.

#### Page types and required sections

| Principle | Source | Check |
|---|---|---|
| Classify each piece of content by two questions: action or cognition; acquisition or application. Four modes: tutorial, how-to, reference, explanation. | Diátaxis compass | J (a declared `type:` is M) |
| Each topic conforms to a type defined by the information its purpose requires. | EPPO | P |
| Even a short page typically starts with a concept, then a task or reference topic; avoid one- or two-sentence topics and pages that are only links (except navigation pages). | GitLab topic types | P |
| Concept: noun title; "a paragraph or two that explains what this thing is and why you would use it"; no steps; no "Overview"/"Introduction"/"How it works" titles. | GitLab concept | P (no ordered-list steps; banned titles are M) |
| How-to: title is an active verb phrase; one task; steps; "If a page has no steps, question whether it is a how-to"; end with verification, then the irreversible closing step, then next steps; "Never end on the last numbered step". | GitLab task; Cloudflare how-to; Diátaxis | P |
| Reference: neutral, austere description only; consistent structure; mirror the product's structure; generate from source where possible. | Diátaxis reference | P |
| Troubleshooting: see 1.2 Troubleshooting below. | GitLab; Cloudflare | P |
| Tutorial: scarce, end-to-end, must not fail the reader. | Diátaxis; Cloudflare; `docs/CLAUDE.md` | J |
| Overview/landing: orient briefly, then route onward. | Cloudflare overview; HashiCorp overview | P |

#### Page openings

| Principle | Source | Check |
|---|---|---|
| The first paragraph says what the thing is and why you would use it; how-to opens with "when to do this" ("Create an issue when you want to..."). | GitLab concept and task templates | P (presence of a paragraph directly under H1 is M; quality J) |
| Address who, what, when, where, why in the first two paragraphs. | SWE at Google ch. 10 | J |
| Every page must establish its context because readers arrive from search, anywhere. | EPPO | J |
| Avoid self-referential openings ("This page explains..."). | GitLab (Vale `SelfReferential.yml`) | M |
| A short front matter `description` (Microsoft: 75 to 300 characters; Learn's template says 115 to 145) is used for search snippets. | Microsoft Learn metadata; Cloudflare metadata | M |

JIM today: 90 of 99 pages open with a paragraph directly under the H1; 9 open with a heading, admonition or no H1 (all five connector pages open with a heading). Only 5 pages have a `description`, two of which are silently dropped because of invalid YAML.

#### Progressive disclosure

| Principle | Source | Check |
|---|---|---|
| Structure content so readers can skip what they know. | Write the Docs "Skimmable" | P |
| Stay on one level; let the reader choose when to go deeper; link richly. | EPPO | J |
| How-to: do not list every option, point to reference; do not teach, link to explanation. | Diátaxis how-to | J |
| Variants (platform, CLI versus UI) go in tabs within one page; pick the recommended method and link the others. | Cloudflare how-to; Google procedures ("give only the best method") | P |
| "A page drowning in exception callouts has the wrong happy path." | Cloudflare how-to | P (admonition density is M) |

JIM signal: 11 pages exceed 5,000 words (`configuration/synchronisation-rules.md` 17,791; `connectors/jim-ldap-connector.md` 14,758; `powershell/connected-systems.md` 13,483; `configuration/connected-systems.md` 13,460). Page length is a cheap mechanical proxy for mixed modes; it should be a report, not a gate.

#### Task structure

| Principle | Source | Check |
|---|---|---|
| Prerequisites labelled consistently; roles first; avoid "Ensure that"; shared prerequisites get their own topic. | GitLab task | P |
| Introduce steps with a short action and a colon; numbered list; single-step procedures use a bullet. | GitLab; Google; Microsoft | M (structure) |
| One action per step; location before action; goal before action; result after action in the same paragraph; "Optional:" prefix. | Google procedures; Microsoft step-by-step | P |
| Complete sentences, imperative verbs, input-neutral verbs ("select", not "click"). | Microsoft; GitLab word list | M for "click" |
| Do not introduce code with "Run the following command"; say what it does. | Google procedures | M |
| Include the completing action (OK/Apply); keep the procedure short. | Microsoft | J |
| Explicit verification ("Verify your connection"). | Tailscale (observed); Cloudflare how-to | P (presence of a verification heading is M) |
| Next steps / related topics at the end. | GitLab; HashiCorp usage template | M (presence) |
| Destructive steps are preceded by a warning. | Cloudflare how-to; `src/JIM.PowerShell/CLAUDE.md` | P |

#### Terminology control

| Principle | Source | Check |
|---|---|---|
| One controlled word list, wired to lint rules (GitLab's word list entries cite the Vale rule that enforces them, for example `CurrentStatus.yml`, `Simplicity.yml`, `LatinTerms.yml`). | GitLab word list | M |
| Use consistent language and formatting. | Write the Docs "Consistent" | M (vocabulary) |
| One name per thing, one per place; abbreviations (CSO/MVO) only in tight headers with a tooltip; "identity" and "record" are not product nouns. | `engineering/DEVELOPER_GUIDE.md` > Vocabulary (JIM's own) | M (mostly) |
| Glossary pages/topics as a defined type. | GitLab glossary topic type | M (entries carry anchors) |

JIM today: the glossary (`docs/reference/glossary.md`) has 41 entries with anchors; the vocabulary rules exist only as prose. Lowercase domain nouns appear 194 times (121 "metaverse", 63 "connector space"); abbreviations CSO/MVO 367 times across 20 files.

#### Troubleshooting format

| Principle | Source | Check |
|---|---|---|
| Entry title is the symptom, ideally the verbatim message (trimmed to about 70 characters with an ellipsis); optional severity prefix. | GitLab; Cloudflare | P |
| Body: symptom (message in a code block), cause, remedy; "workaround" for temporary fixes, "resolution" for permanent ones. | GitLab troubleshooting template | P |
| Bold **Cause**, **Fix**, **Verify** labels in that order; fixes must be executable ("check your configuration" is not a fix); every workaround states its cost and its permanent alternative; data-loss failures first; closing "Still stuck?" with what to collect. | Cloudflare troubleshooting | P |
| Name a failure and its remedy in the same breath; state what survives. | `docs/CLAUDE.md` (JIM's own) | J |

JIM's `administration/troubleshooting.md` already titles entries by symptom and puts messages in code blocks (good), but uses three different body shapes ("**What it means.** / **How to fix.**" in one entry, inline prose in others) and still carries a "Documentation In Progress" banner above real content.

#### Versioning and what-changed language

| Principle | Source | Check |
|---|---|---|
| Avoid time-relative words: "currently", "now", "new", "soon", "latest", "as of this writing", "eventually", "does not yet", "existing"; docs are assumed current unless a version is stated; pair "new" with a version or date if you must. | Google timeless documentation; Kubernetes; GitLab (`CurrentStatus.yml`) | M |
| Do not promise future features; say an issue exists, or that removal is planned; deprecation notices are the exception. | GitLab; Kubernetes | M (phrase list) |
| Show availability per feature in a standard place under the title. | GitLab availability details; Tailscale plan note; Kubernetes `feature-state` shortcode and `min-kubernetes-server-version` front matter | M (format) |
| "Write 'In version 1.4, ...', not 'In the current version, ...'". | Kubernetes | M |

JIM-specific weight: the site is unversioned while customers run several on-premises releases. A version marker ("Available from v0.17.0"; "Releases up to v0.16.0 do not ...", a form JIM's troubleshooting page already uses) is therefore more important for JIM than for SaaS docs. 83 time-relative words today; 5 future promises (four of them in the two "in progress" stubs).

#### Accessibility

| Principle | Source | Check |
|---|---|---|
| Heading hierarchy without skipped levels; one H1 (WCAG 1.3.1, 2.4.6). | Google accessibility; WCAG 2.2 | M (1 skipped level in JIM: `powershell/synchronisation-rules.md:11`) |
| Link text meaningful out of context; no "click here"/"this page" (WCAG 2.4.4). | Google; GitLab | M (1 hit in JIM: `index.md:186`) |
| Text alternatives for non-text content (WCAG 1.1.1); diagram descriptions carry the information, not a description of the picture. | WCAG; `docs/CLAUDE.md` | M (presence) / J (quality). All 17 concept SVGs have `role="img"`, `<title>` and `<desc>`; **0 of 54 Mermaid diagrams** (all in `developer/diagrams/`) carry `accTitle`/`accDescr`. |
| No information by colour alone (WCAG 1.4.1). | WCAG; Google | P (JIM's ✅/❌ carry shape as well as colour) |
| No images of text (WCAG 1.4.5); no screenshots by design. | WCAG; `docs/CLAUDE.md` | M (JIM has zero Markdown images) |
| Avoid directional language ("above", "below"). | Google accessibility | M (213 hits; low value, suggestion only) |
| Moving content can be paused (WCAG 2.2.2). | WCAG | J (JIM's SMIL packet animations honour `prefers-reduced-motion` but have no in-page pause; worth an explicit review, not a lint) |
| Code blocks declare a language (screen readers, highlighting). | GitLab markdownlint use | M (7 missing in JIM) |
| Page language set (WCAG 3.1.1). | WCAG | M (built pages carry `lang="en"`) |

#### Keeping docs true over time

| Principle | Source | Check |
|---|---|---|
| Incorrect documentation is worse than missing documentation. | Write the Docs "Current" | J |
| Docs as code: version control, review, tests, CI; teams can block merging features without docs. | Write the Docs docs-as-code | M (coupling) |
| Store sources near the code they describe; eliminate overlap between sources (one home per fact). | Write the Docs "Nearby", "Unique" | P |
| Owners; freshness dates with reminders; a visible "last reviewed by"; remove or mark obsolete docs. | SWE at Google ch. 10 | M (date present and age) |
| Three reviews: technical (accuracy), audience (clarity), writing (consistency). | SWE at Google ch. 10 | J |
| Visible "Last validated" date per page. | Tailscale (observed) | M |
| `ms.date`: last substantially edited or "guaranteed fresh"; `ms.author` owner; `ms.topic` type; missing fields fail validation. | Microsoft Learn metadata | M |
| Front matter validated by schema (Zod) with IDE IntelliSense. | Cloudflare metadata | M |
| Generate reference from source where possible. | Diátaxis reference | M (generation, not linting) |
| CODEOWNERS derived from page ownership metadata. | GitLab metadata | M |

JIM-specific weight: `docs/CLAUDE.md` records the defining failure, "leaving something true-when-written": the LDAP Connector page contradicted shipped behaviour for two releases. Existing `Lint-DocsCoupling.ps1` proves *a* docs page changed, not that the stale one did. Mechanical mitigations are partial (a source-area-to-page map; a rename sentinel that greps docs for identifiers removed in the diff); the rest is review.

#### Coverage (issue #1454 asks for complete coverage of concepts, features and terms)

| Principle | Source | Check |
|---|---|---|
| All publications together answer the questions users will have. | Write the Docs "Comprehensive" | J |
| Cover a concept fully or state clearly that coverage is partial. | Write the Docs "Complete" | J |
| Every public interface element is documented (cmdlets, environment variables, settings, glossary terms). | Diátaxis reference ("precise and complete") | M (set comparison against source) |

### 1.3 What best-in-class enterprise and infrastructure docs actually do

- **GitLab**: typed topics with copy-paste templates; ownership front matter drives CODEOWNERS and writer assignment; Vale error-level rules fail CI, warnings show only in the MR diff, suggestions only in editors; `lint-doc.sh` fails on missing ownership front matter, executable files, filename rules and Mermaid that will not render; lychee for links; a word list that cites the Vale rule enforcing each entry.
- **Cloudflare**: 16 content types with scaffolds (`npx @cloudflare/nimbus-docs add content-troubleshooting`); `pcx_content_type` and `description` required and schema-validated; **broken internal links fail the build, external links and anchors are periodic non-blocking audits**; AI used to score strings and API docs and to draft from content-type prompts, always with human review.
- **Microsoft Learn**: required metadata (`title`, `description`, `author`, `ms.author`, `ms.date`, `ms.topic`) with build validation errors.
- **HashiCorp**: Diátaxis-based templates; usage pages require Requirements, numbered Steps linking to reference, Next steps; front matter `page_title` must match H1 and nav title (the same rule JIM breaks on 8 pages).
- **Tailscale**: "Last validated <date>" at the top of every page, plan availability as a note under the title, and a dedicated verification section in setup guides.
- **Stripe**: documentation is part of engineers' job ladders; Markdoc's declarative format lets invalid structure fail the build (secondary sources).
- **Kubernetes**: feature-state shortcode and minimum-version front matter instead of prose status words.

The common shape: **typed pages with templates, required and validated metadata, blocking internal integrity checks, non-blocking external checks, a word list wired to a prose linter, and humans (increasingly with AI assistance) for judgement**.

### 1.4 Proposed JIM specification outline (the "spec")

One spec, one home: a single document (candidate locations: `engineering/DOCUMENTATION_STANDARDS.md`, or a public `docs/developer/documentation-standards.md` for community contributors), with `docs/CLAUDE.md` linking to it rather than restating it. Every rule carries a stable ID that is also the lint rule name and the rubric item, so a CI message links straight to the rule text (Vale rules support a `link:` field).

| Area | Rules (IDs) |
|---|---|
| Audience | AUD-1 every page declares `audience:` (administrator, operator, automation, assessor, contributor); AUD-2 contributor-only vocabulary stays in `developer/` |
| Page types | TYP-1 every page declares `type:` (tutorial, how-to, concept, reference, troubleshooting, landing); TYP-2..7 per-type required shape (below) |
| Structure | STR-1 valid front matter with `title`, `description` (75 to 160 characters), `type`, `audience`; STR-2 title = H1 = nav label; STR-3 one H1, no skipped levels; STR-4 code fences declare a language; STR-5 every page in nav; STR-6 no stub or "in progress" pages in nav; STR-7 opening paragraph directly under the H1 |
| Links | LNK-1 internal links and anchors resolve; LNK-2 built-site links resolve (including the Scalar reference); LNK-3 external links alive (scheduled); LNK-4 meaningful link text |
| Language | LANG-1 no em dash; LANG-2 British spelling; LANG-3 Title Case domain nouns, never "Sync Rule"; LANG-4 CSO/MVO/RPEI only in table headers; LANG-5 no time-relative words; LANG-6 no future promises; LANG-7 no minimisers; LANG-8 Latin abbreviations; LANG-9 directional words; LANG-10 "please"; LANG-11 you/present/active, JIM as subject; LANG-12 hedge only where uncertain; LANG-13 domain concept, never implementation noun; LANG-14 no competitor product names |
| Terminology | TERM-1 controlled vocabulary file is the single source; TERM-2 every vocabulary term has a glossary entry with an anchor; TERM-3 rejected synonyms |
| Accuracy and coverage | ACC-1 no parameter aliases in examples (exists); ACC-2 example parameters exist; ACC-3 output property names exist on the DTO; ACC-4 claims verified against source; COV-1 every exported cmdlet has a reference section; COV-2 every environment variable the product reads is documented; COV-3 a user-facing change updates its mapped page |
| Safety | SAFE-1 destructive operations state what survives; SAFE-2 forced bulk deletes suggest a dry run; SAFE-3 failures name their remedy |
| Versioning | VER-1 availability marker format (`Available from vX.Y.Z`), validated against released versions, with a `vNEXT` placeholder that `/release` must resolve |
| Freshness | FRS-1 `validated: vX.Y.Z` front matter, reported when more than N releases behind; FRS-2 rename sentinel |
| Accessibility | A11Y-1 non-text alternatives; A11Y-2 Mermaid `accTitle`/`accDescr`; A11Y-3 tables have header rows; A11Y-4 motion review |

Per-type required shape (TYP-2..7), synthesised from GitLab, Cloudflare, HashiCorp, Google and Microsoft, adapted to JIM:

| Type | Title | Required | Forbidden |
|---|---|---|---|
| how-to | Imperative verb phrase ("Configure SSO with Microsoft Entra ID") | Opening: when to do this and the outcome; "Before you begin" when there are prerequisites (roles, versions, access); numbered steps (location, then action, then result); a warning before any destructive step stating what survives; "Check it worked"; "Next steps" or related links | Teaching digressions (link to concepts); exhaustive option lists (link to reference) |
| concept | Noun phrase, never "Overview"/"Introduction"/"How it works" | Opening: what it is and why it matters; diagram where shape is the point; links onward to tasks and reference (EPPO "link richly"; GitLab differs here, and the spec should choose linking) | Numbered steps |
| reference | Noun phrase | One-line scope statement; consistent tables (setting key, default, allowed values, effect, available from); generated where possible | Narrative, opinion, instructions |
| troubleshooting | "Troubleshooting <area>"; entries titled by verbatim symptom | Per entry: message in a code block, **Cause**, **Fix** (executable), **Check** and, where relevant, what is not lost; data-loss entries first; closing "Still stuck?" with what to collect | Reassurance without a fix |
| tutorial | Outcome phrase | Time and outcome up front; prerequisites; checkpoints; "What you achieved"; re-run each release | Branching, alternatives |
| landing | Section name | One purpose paragraph plus cards or links | Other content |

---

## Part 2: Enforcement layer

### 2.1 What JIM already has

| Asset | What it enforces | Notes |
|---|---|---|
| `.github/workflows/changelog-lint.yml` (required check `changelog-lint`, every PR, no path filter) | Bundles five checks to avoid registering new required checks: changelog quality, docs coupling, doc example aliases, CSS brace balance, diagram label cuts (the last short-circuits unless diagram files changed) | The natural host for blocking docs rules; already demonstrates the in-step path short-circuit pattern |
| `scripts/Lint-Changelog.ps1` | Canonical emoji (fail), duplicates and near-duplicates (fail), internal-looking or long entries (warn) | Shows the house pattern: hard fails only where there is no false positive in history; warnings otherwise |
| `scripts/Lint-DocsCoupling.ps1` | A newly added ✨/🔄 entry requires a `docs/` change, or a reasoned `Docs: n/a - <reason>` / `docs-not-needed` opt-out | Proves *a* page changed, not the right one |
| `scripts/Lint-DocExamples.ps1` | No parameter alias in ```powershell fences or `.EXAMPLE` help; AST-based, not regex | Easily extended to ACC-2 (unknown parameters); my probe reused its approach and found one real defect |
| `scripts/Tests/*.Tests.ps1` | Pester tests for every lint script, run in `build-and-test` | Every new lint script should follow suit |
| `.github/workflows/docs.yml` | `mkdocs build --strict` on push to `main` only, then deploy | The only docs gate, after merge |
| `mkdocs.yml` `validation:` | Only `links.unrecognized_links: ignore` | Anchors stay at the `info` default and never fail; the ignore is needed for the Scalar API reference (36 links) but also hides the one broken one |
| `docs/CLAUDE.md` | Diátaxis ethos and "Writing for the Reader" rules, explicitly prose-only for implementation nouns | Says "There is no link checker in CI" |
| `test/CLAUDE.md` | Sweeps only after a convention "has actually regressed more than once"; escape hatches as visible marker comments in the markup, not allow-lists inside the test | Directly governs how the ratchet's exceptions should look |
| `.github/workflows/claude.yml` | `anthropics/claude-code-action` pinned by SHA, gated on OWNER/MEMBER/COLLABORATOR, on GitHub-hosted runners | Reusable for the LLM rubric |
| `.github/workflows/tooling-pin-check.yml` | Weekly bump PRs for tooling pinned outside Dependabot manifests | Extendable to Vale and lychee pins |

Constraints carried into the design: all automation in PowerShell; new third-party dependencies need explicit approval; the product must be air-gap deployable (CI tooling may use the internet, nothing ships); required checks are a fixed ruleset list; every lint has Pester tests.

### 2.2 Measured baselines (a scratch copy of `docs/`, 99 pages)

| Check | Result | Run time |
|---|---|---|
| `mkdocs build --strict` with `anchors`, `omitted_files`, `not_found`, `absolute_links` at `warn` | **0 warnings** (injected broken links correctly reported) | about 7 s, plus about 20 s pip install |
| Same, with `unrecognized_links: warn` | 36 warnings, all links to the Scalar reference (35 correct, 1 broken) | |
| `lychee --offline --include-fragments --index-files index.html --fallback-extensions html` over the built site | 20,820 links (2,714 unique), **1 error**: the real broken API reference link | under 1 s |
| lychee online over the 28 unique external URLs | 22 OK; 6 "errors", all `403` from bot-blocking or sign-in walls (GitHub Discussions, project boards, Azure portal): **false positives** | 2 s |
| YAML parse of every front matter block | **2 invalid** (portal-lists, prerequisites) | |
| markdownlint-cli2 0.23.3, default rules | **10,455 findings across 100 files** (6,149 line length, 2,700 table style, 744 inline HTML, 629 duplicate headings) | 2.8 s |
| markdownlint-cli2, tuned for MkDocs (line length, table style, code-block style, link fragments off; `<br>` etc. allowed; siblings-only duplicate headings; front matter title) | 69 findings in 15 files, of which 51 are MkDocs-syntax false positives (MD030 on Material card grids, MD007 on Python-Markdown's 4-space nesting); about **18 genuine** (7 fences without language, 5 emphasis-as-heading, 2 spaces in code spans, 2 blanks around lists, 1 skipped heading level, 1 first-line heading) | |
| Vale 3.24.0 with Google, Microsoft, write-good, proselint packages | **38,998 alerts (4,706 errors)** in 99 files | 16.7 s |
| Vale 3.24.0 with a prototype 11-rule JIM style | 1,242 alerts; errors: ProductNouns 194, AmericanSpelling 28 (21 are "Global Catalog", an Active Directory proper noun needing an exception; about 7 genuine), LinkText 1, EmDash 0; warnings: Abbreviations 367, Latin 254, Hedges 86, Timeless 83 (some legitimate, such as "the installer downloads the latest release"), FuturePromises 5, Please 4; suggestions: Directional 213, WeVoice 7 | 1.0 s |
| AST probe: example parameters that do not exist on the cmdlet | 184 functions mapped; **1 defect** (`Get-JIMConnectedSystem -All`) | about 2 s |
| Exported cmdlets without a reference heading or table entry | 1 (`Get-JIMDataFlow`) | |
| `JIM_*` environment variables read via `GetEnvironmentVariable` but absent from docs | 1 runtime (`JIM_CACHE_WARM_PARALLELISM`), 2 build-only (`JIM_OPENAPI_*`, allow-list with reason); a naive string grep also hits 2 SQL alias constants, so match call sites, not strings | |
| Em dashes in `docs/` | 0 | |
| Implementation nouns in customer pages (row, column, "the database", cascade, enum, entity) | 548 hits, concentrated in the SQL and File Connector pages and portal-lists (where "row" and "column" are the reader's own words): confirms `docs/CLAUDE.md`'s decision that this is rubric, not lint | |

### 2.3 Tool evaluation

#### MkDocs 1.6 built-in validation plus `--strict` at PR time

- **Enforces**: missing pages and nav targets, broken cross-page and same-page anchors (since 1.6), omitted files, absolute links. Does not check unrecognised links (the Scalar reference), raw HTML links, or front matter validity (it silently drops invalid YAML).
- **Licence / maintenance**: BSD-2-Clause; 1.6.1 released August 2024 and no 1.x release since; Material for MkDocs in maintenance mode until about 5 November 2026; MkDocs 2.0 is incompatible (Material's banner: plugins and theme overrides break, no migration path). Zensical (Alpha, MIT, same team) has equivalent validation including `invalid_link_anchors` and a strict mode (https://zensical.org/docs/setup/validation/).
- **Install**: the same bounded pip ranges already accepted in `DEPENDENCY_PINNING.md` row 8; no new dependency. The OpenAPI generation step is not needed for validation (the 36 Scalar links are left as-is), so the PR-time build needs no .NET.
- **False positives**: none observed.
- **Fit**: excellent and free today; temporary by nature. Also add `overrides/` to `exclude_docs`.

#### Vale (prose linter)

- **Enforces**: anything expressible as existence, substitution, occurrence, repetition, consistency, conditional, capitalisation, metric, readability, spelling, sequence or script rules, scoped to Markdown structure (headings, table header/cell, list, link text, alt text, paragraph, sentence). Inline disable comments per rule or match; vocabularies (`accept.txt` feeds `Vale.Terms`, `reject.txt` feeds `Vale.Avoid`); per-section configuration by glob (lets `developer/**` use a different profile); JSON output for a PowerShell wrapper.
- **Licence / maintainer**: MIT; Joseph Kato (errata-ai); actively released (v3.20.0 to v3.24.0 between 2 September and 1 October 2026); releases are immutable and tags signed; a `vale_<ver>_checksums.txt` with SHA-256 per asset is published (verified for 3.24.0).
- **Install in CI**: download the pinned Linux tarball, verify against a SHA-256 **pinned in the repository** (not merely the downloaded checksums file), extract; about 13 MB; single static binary, no runtime. Vendor the JIM style in the repository and do **not** use `Packages`/`vale sync` (which fetches from GitHub at run time). Do not use `errata-ai/vale-action`: it downloads Vale at run time without verifying Vale's checksum and adds reviewdog; a PowerShell wrapper that converts Vale's JSON into `::error file=...,line=...::` annotations gives the same inline PR experience with no extra dependency.
- **False-positive profile**: depends entirely on the style. Off-the-shelf packages: unusable here (38,998 alerts). A JIM-only style: low for the substitution and existence rules with a short exception list. Known gaps: Vale treats MkDocs content that is indented after a blank line (tab bodies, multi-paragraph admonitions) as an indented code block and skips it: about 109 lines in 20 files today. Admonition bodies directly under the `!!!` line are linted (verified). Mitigations: accept the gap; lint the built HTML for those pages; or a PowerShell pre-pass that writes de-indented shadow copies for Vale (line numbers preserved).
- **British English**: Vale's default dictionary did not flag "synchronisation" and friends; American spellings are best caught by a JIM substitution rule. Spelling proper (`Vale.Spelling`, 2,456 hits, nearly all product vocabulary: Metaverse 637, JIM's 245, Podman 156, cmdlet(s) 171) needs about 100 vocabulary entries before it is useful; leave it off initially.
- **Agent fit**: Vale's own guide for AI agents recommends running Vale from an edit-time hook and gating on errors only: "an exit code is a fact where 'I followed the style' is a claim" (https://docs.vale.sh/guides/agents.md). JIM's docs are largely agent-authored, so a Claude Code `PostToolUse` hook on edits to `docs/**/*.md` returning Vale errors is high-leverage. Vale's MCP server is hosted and paid (Vale CMS): not applicable.
- **Precedent**: GitLab (error-level fails CI; warnings in MR diff), Datadog (house style as Vale rules; "a crisp set of computer-understandable style rules" as the prerequisite for LLM editing), Spectro Cloud (changed files only; each rule has pass and fail test cases; Vale "does not replace the need for an editorial review").
- **Fit**: good, with approval. It is the only candidate that understands Markdown scopes and supports inline, reasoned exceptions.

#### markdownlint-cli2 (structure linter)

- **Enforces**: about 50 structural Markdown rules; custom rules in JavaScript; SARIF/JUnit output.
- **Licence / maintainer**: MIT; David Anson (also maintains markdownlint); v0.23.3 published 2026-09-20.
- **Install**: npm; pulls **81 packages** (globby, js-yaml, markdown-it, micromatch and others); would need a committed lockfile with integrity hashes and `npm ci`, plus Dependabot coverage.
- **False positives**: high on MkDocs Material syntax (see 2.2): MD030 on card grids, MD007 because Python-Markdown requires 4-space nesting, MD046 on admonitions, MD051 because JIM's emoji-led headings slug differently in GitHub-style anchors than in Python-Markdown (13 false "broken fragment" reports; MkDocs itself reports 0).
- **Fit**: weak. After tuning, the genuinely useful rules (MD001 heading increment, MD025 single H1, MD040 fence language, MD036 emphasis as heading) are each a few lines of PowerShell over a fence-aware line scan that JIM already has in `Lint-DocExamples.ps1`. Not recommended; if adopted anyway, use the tuned configuration recorded in 2.2.

#### lychee (link checker)

- **Enforces**: links in HTML and Markdown, including fragments (`--include-fragments`), offline (`--offline`, file links only) or online; caching; JSON/Markdown output.
- **Licence / maintainer**: Apache-2.0 OR MIT; lycheeverse organisation; v0.24.2 (2026-05-01) plus nightlies; a `.sha256` per release asset (verified).
- **Install**: single static Rust binary; same pinned download and SHA-256 pattern as Vale. GitLab uses lychee for its docs link jobs.
- **False positives**: offline over the built site with `--index-files index.html --fallback-extensions html` and `overrides/` excluded: none (1 true positive). Online external checks: 6 of 28 were 403s from bot-blocking, the classic reason external checks must not block PRs (Cloudflare reaches the same conclusion).
- **Fit**: good. Run offline over the built site at PR time (blocking), online weekly on a schedule (non-blocking, updating a tracking issue). The dependency-free alternative is a PowerShell resolver for the one class MkDocs ignores (links to `/api/reference/` resolved against the page URL); it would have caught today's defect but not links in raw HTML, SVG `href`s or snippet includes, so lychee is the more durable choice.

#### Front matter schema validation

- **Enforces**: STR-1 (valid YAML, required keys, enumerated `type`/`audience`, `description` length), STR-2 (title equals H1), VER-1/FRS-1 formats.
- **Options**: (a) PowerShell has no built-in YAML parser (`ConvertFrom-Yaml` needs the third-party `powershell-yaml` module). Restricting front matter to a flat `key: value` subset, with a rule that any value containing `: ` or ` #` is quoted (exactly Microsoft Learn's rule), lets a PowerShell lint parse it with no dependency and reject both of today's invalid blocks. (b) Validate with a JSON Schema via PowerShell 7's built-in `Test-Json -Schema` after converting the flat map to JSON: no dependency, and the same schema can give editors IntelliSense, as Cloudflare does with Zod. (c) Python plus PyYAML (present in the docs job) violates the PowerShell-only rule.
- **Fit**: (a) plus (b). No dependency.

#### Custom PowerShell lints (house style)

- **Enforces**: everything structural or set-based: headings, fences, stubs, nav membership, page-type shapes for opted-in pages, cmdlet and environment-variable coverage, unknown parameters (ACC-2, by extending `Lint-DocExamples.ps1`), glossary coverage (TERM-2), Mermaid accessibility (A11Y-2), title/H1/nav agreement, docs-coupling 2.0 (COV-3: a map from source areas to pages), rename sentinel (FRS-2), the baseline ratchet itself, and competitor names (LANG-14).
- **LANG-14 subtlety**: the rule "never name competing products" means a lint cannot contain their names in plain text. A PowerShell lint can instead hold SHA-256 hashes of the lower-cased names (and two-word phrases) and hash each word and bigram of the docs; no name ever appears in the repository.
- **Fit**: native; no approval; Pester-testable like the existing lints. Cost is authoring and maintenance, so keep each rule tied to a demonstrated defect.

#### LLM-assisted review against a written rubric (advisory)

Pattern recommended for JIM:

1. **Rubric as code**: a rubric section of the spec listing the judgement rules by ID (AUD-1 audience fit, LANG-11/12/13, SAFE-1/3, Diátaxis mode purity, progressive disclosure, one home per fact, ACC-3/4 claims about source), each with one pass and one fail example drawn from JIM's own history (the LDAP "will not stop you" stale claim; "The row stays, marked Cancelled"; the certificate-store deletion example from `src/JIM.PowerShell/CLAUDE.md`).
2. **Workflow** `docs-review.yml` (not a required check, so a path filter on `docs/**` is safe): `pull_request`, skip drafts, gate on OWNER/MEMBER/COLLABORATOR exactly as `claude.yml` does, GitHub-hosted runner, reuse the already-pinned `anthropics/claude-code-action`, permissions `contents: read`, `pull-requests: write`, read-only tools only (Read, Grep, Glob; no Bash, no Write), `continue-on-error: true`. Optionally trigger by label to control cost.
3. **Output contract**: one sticky PR comment; per changed page, only failed rubric items, each with a verbatim quoted excerpt as evidence and a concrete rewrite; a hard cap on findings; no praise. Requiring quoted evidence is the main guard against invented findings.
4. **The most valuable mode for JIM is grounded fact-checking**: for each changed doc hunk, verify parameter names, output property names, setting keys, defaults and routes against `src/` (the model can grep the checkout). This targets the defect class `docs/CLAUDE.md` calls the likeliest (claims about source) and that no build catches.
5. **Calibration and promotion**: track which findings authors act on; a rubric item whose findings are phrase-shaped and reliably accepted gets promoted to a Vale rule (the escalation path in `test/CLAUDE.md`); an item with low precision gets rewritten or dropped.
6. **Risks**: non-determinism (advisory only, never a gate); prompt injection (PR text is untrusted input to a model with tools: hence read-only tools, trusted authors only, no secrets beyond the action token); cost.

How comparable teams handle it: Datadog treats machine-readable Vale rules as the prerequisite for LLM editing; GitBook advises a non-AI linter for style in git workflows and LLMs for deviations rules miss, warning that a prompt can only hold so much of a style guide (https://gitbook.com/docs/guides/docs-workflow-optimization/introducing-ai-into-your-product-documentation-workflow); Corelight pairs Vale findings with an LLM fixer (https://corelight.com/blog/microsoft-style-guide-llm); Cloudflare uses AI to score in-product strings and API docs and to draft from content-type prompts with human review; Vale's agent guide gates on errors and leaves warnings for people. I found no published outcome study for combined Vale-plus-LLM review; the evidence is practitioner reports.

#### Tool summary

| Tool | Licence | Maintenance | Install | False positives (JIM) | Recommendation |
|---|---|---|---|---|---|
| MkDocs `validation` + `--strict` at PR | BSD-2 | Frozen 1.x; generator being replaced | Existing pip ranges | None | Adopt now; expect to re-express under Zensical |
| Vale (JIM-only style) | MIT | Active, signed tags | Pinned binary + repo-pinned SHA-256 | Low with exceptions; skips indented-after-blank content | Adopt (needs approval) |
| lychee (offline, built site) | Apache-2.0 OR MIT | Active | Pinned binary + SHA-256 | None offline; online 403s | Adopt (needs approval); online check scheduled only |
| markdownlint-cli2 | MIT | Active | npm, 81 packages | High on MkDocs syntax | Do not adopt; port 4 rules to PowerShell |
| Front matter schema | n/a | n/a | PowerShell + `Test-Json` | None | Adopt |
| Custom PowerShell lints | n/a | In-house | None | Controlled | Adopt for structure, coverage, ratchet |
| LLM rubric | Existing action | n/a | Already pinned | Unknown; advisory | Adopt as advisory after the spec exists |

---

## Part 3: Recommended tiered design

### 3.1 Tiers

**Tier A: blocking (fails `changelog-lint`).** Only rules with zero or near-zero false positives and a demonstrated defect class. Applied in full to every new page; applied to existing pages through the ratchet.

| Rule | Tool | Baseline today |
|---|---|---|
| LNK-1 internal links and anchors | MkDocs strict, tightened `validation:` | 0 |
| LNK-2 built-site links (including Scalar) | lychee offline | 1 (fix) |
| STR-1 front matter parses; flat-YAML quoting rule | PowerShell | 2 (fix) |
| STR-3 one H1, no skipped levels; STR-4 fence language; STR-5 page in nav | PowerShell / MkDocs | 1 / 7 / 0 |
| LANG-1 em dash; LANG-2 American spellings (with proper-noun exceptions such as "Global Catalog") | Vale | 0 / about 7 |
| LNK-4 link text | Vale | 1 |
| ACC-1 aliases (exists); ACC-2 unknown parameters | PowerShell AST | 0 / 1 (fix) |
| COV-1 cmdlet reference coverage; COV-2 environment variables (allow-list with reasons) | PowerShell | 1 / 1 |
| A11Y-1 non-text alternatives | PowerShell | 0 |
| LANG-14 competitor names (hashed) | PowerShell | not measured |
| Ratcheted (blocking for new occurrences, baseline for old): LANG-3 domain nouns (194), STR-1 required keys (94 descriptions, 99 types), STR-2 title = H1 (8), STR-6 stubs (2 pages), A11Y-2 Mermaid (54), TERM-2 glossary coverage (about 10), TYP-2..7 for opted-in pages | Vale / PowerShell | as shown |

**Tier B: warnings (inline `::warning` annotations and a step summary; never fail).** LANG-4 abbreviations, LANG-5 time-relative words, LANG-6 future promises, LANG-7 minimisers, LANG-8 Latin, LANG-10 "please", STR-7 opening paragraph, page-size report (pages over 5,000 words), COV-3 mapped-page coupling, FRS-2 rename sentinel, SAFE-2 forced bulk deletes without a dry-run note, TYP rules on pages that have not opted in. Promote a warning to Tier A once its baseline reaches zero and it has run for a release without a false positive. LANG-9 directional words stays a local suggestion only (editor/hook), since 213 hits for little reader value would train people to ignore annotations.

**Tier C: rubric only (LLM advisory plus human review).** AUD-1 audience fit, LANG-11 voice, LANG-12 hedging, LANG-13 implementation nouns (per `docs/CLAUDE.md`'s explicit decision, confirmed by 548 mostly legitimate hits), SAFE-1 "is anything lost?", SAFE-3 failure plus remedy, Diátaxis purity, progressive disclosure, one home per fact, ACC-3/ACC-4 claims about source, example realism, tutorial correctness.

**Tier D: scheduled (weekly, non-blocking, one tracking issue).** LNK-3 external links (lychee online), FRS-1 staleness report against `VERSION`, glossary and coverage drift summary.

### 3.2 Rollout without a big bang

Mechanisms:

1. **New pages are born conformant.** Baselines exist only for files present when a rule is introduced; any file absent from the baseline has an implicit allowance of zero. This is the property that stops debt growing from day one.
2. **Shrink-only per-file, per-rule baseline**, modelled on ESLint bulk suppressions (https://eslint.org/blog/2025/04/introducing-bulk-suppressions/ , https://eslint.org/docs/head/use/suppressions): counts, not line numbers (so unrelated edits do not churn it); exceeding a count fails and annotates only that rule's hits in that file; a count that has dropped also fails until `Test-DocsConformance.ps1 -UpdateBaseline` locks the improvement in (ESLint fails on unused suppressions for the same reason: otherwise the baseline silently re-admits debt). Touching a large page never obliges its author to fix pre-existing debt, only not to add to it.
3. **Baseline format that survives parallel sessions**: one sorted line per entry (`rule<TAB>path<TAB>count`) rather than one JSON document, so concurrent branches touch different lines and merge cleanly; JIM runs many sessions in parallel and a single JSON object would conflict constantly.
4. **Permanent exceptions are marker comments with a reason, in the page**, never baseline entries: Vale's `<!-- vale JIM.ProductNouns = NO -->` ... `= YES`, or `<!-- conformance: allow STR-7 "landing page opens with cards by design" -->`. A PowerShell rule fails any disable without a reason and reports the per-page count. This follows `test/CLAUDE.md`'s rule that an escape hatch is a visible marker where the next author reads it.
5. **Per-page and per-folder opt-in for page types**: a page that declares `type:` is checked against that type's shape (opt-in by page). A folder listed as migrated in the conformance configuration must have `type:` on every page and an empty baseline for every rule (opt-in by folder). Each folder migration is one PR that rewrites the folder to the spec and flips its flag; the flag never flips back.
6. **Promotion ladder**: suggestion (editor only) to warning (annotation) to ratcheted error to plain error; demotion if a rule produces a false positive that cannot be fixed by an exception list.

Phases (each a normal PR; blocking only where the baseline is zero or ratcheted):

| Phase | Content | Blocking from day one |
|---|---|---|
| 0 | Tighten `mkdocs.yml` `validation:` and `exclude_docs`; PR-time `mkdocs build --strict` and lychee offline inside `changelog-lint`; fix the four defects (two front matter blocks, the API reference link, the `-All` example); extend `Lint-DocExamples.ps1` with unknown-parameter detection | LNK-1, LNK-2, ACC-2 |
| 1 | Write the spec with rule IDs; front matter schema; `Test-DocsConformance.ps1` framework, baseline file and Pester tests; structural, coverage, glossary and Mermaid rules; retire or rewrite `engineering/prompts/api-docs-section.md`; remove or finish the two stub pages | STR-1/3/4/5, COV-1/2, A11Y-1, ratcheted STR/TERM/A11Y-2 |
| 2 | Vendored JIM Vale style plus vocabulary; PowerShell installer pinning Vale and lychee by version and SHA-256 (also used by the devcontainer), added to `tooling-pin-check` and to `DEPENDENCY_PINNING.md`; Claude Code `PostToolUse` hook running Vale on edited docs | LANG-1/2, LNK-4, ratcheted LANG-3 |
| 3 | Folder migrations in order of reader impact and size: `getting-started/` (4 pages, first impression), `administration/` (13 + 3, operators), `configuration/` (16, the largest customer area), `concepts/` (9), `connectors/` (7), `api/` (3), `powershell/` (23; pair with a decision on generating reference from comment-based help, since generation removes the drift class instead of linting it), `developer/` last under its own contributor profile | Folder-level TYP and zero baseline |
| 4 | `docs-review.yml` LLM rubric, advisory; calibrate against JIM's historical failures | None (advisory) |
| 5 | Promote warnings whose baselines have reached zero; drop low-precision rules | Per rule |

### 3.3 Where it runs

- **Blocking and warning tiers: inside the existing `changelog-lint` job**, as further steps calling one orchestrator, `scripts/Test-DocsConformance.ps1`, which is also what authors and agents run locally. This keeps the ruleset unchanged, matching the job's own documented rationale. Cheap checks (PowerShell rules, Vale, about 3 seconds) always run, so the job always reports; the MkDocs build and lychee (about 30 seconds including pip) short-circuit inside the step unless the PR touches `docs/**`, `mkdocs.yml`, `CHANGELOG.md` (included by snippet), `test/integration/docker/**` (included by snippet), `src/JIM.PowerShell/Public/**` (coverage and parameter checks) or the conformance configuration, exactly as the diagram step does today.
- **Make failures legible despite the job name**: emit `::error file=docs/x.md,line=N,title=LANG-3::...` and `::warning` annotations so findings appear on the PR diff, and write a per-rule table to `$GITHUB_STEP_SUMMARY`. The job now does seven or more unrelated things under the name "changelog-lint"; renaming it (for example to `pr-lint`) is a one-time ruleset edit that the user may prefer to keep deferring.
- **Advisory LLM review**: separate `docs-review.yml`, not required.
- **Scheduled health**: separate weekly workflow on a GitHub-hosted runner, updating one tracking issue.
- **Post-merge**: `docs.yml` keeps its strict build before deploy; it becomes a backstop rather than the first gate.
- **Tests**: every new script gets `scripts/Tests/<Script>.Tests.ps1` with pass and fail fixtures per rule (Spectro Cloud's practice), run by `build-and-test`; Vale rules get fixture files exercised from the same Pester suite.

### 3.4 Weak points to resolve before building

1. **Rule fatigue versus JIM's own doctrine.** `test/CLAUDE.md` reserves sweeps for conventions that have regressed more than once. The Tier A list above is limited to rules with a measured defect or zero cost; resist adding Google- or Microsoft-derived rules without JIM evidence.
2. **Vale's MkDocs blind spot** (indented content after a blank line is skipped: about 109 lines in 20 files). Decide between accepting it, linting built HTML for those pages, or a de-indenting pre-pass.
3. **Generator sunset.** Material's support window closes in weeks. Keep checks source- and HTML-based; plan to re-express the `validation:` block for Zensical (which has equivalents) or pin MkDocs 1.6.1 deliberately.
4. **Unversioned docs for a multi-version on-premises product.** Version markers mitigate it. Shipping version-matched docs inside the air-gapped release bundle would need the site to stop loading Mermaid from `unpkg.com` and fonts from Google Fonts at run time (Material's privacy plugin can self-host them); a product decision, not a lint.
5. **Title Case edge cases.** "metaverse pattern" (a generic architecture term) and "the **metaverse**" are arguably generic; the spec must say which uses are the named entity, or the ProductNouns rule will be disabled inline everywhere.
6. **PowerShell reference pages** are the largest drift surface (hand-maintained against DTOs in another project). Linting them is a stopgap; generating them is the durable fix and should be decided before migrating `powershell/`. platyPS is Microsoft-maintained (top of the dependency preference order) but still needs approval.
7. **Developer pages** are a different audience. Either keep them under a contributor profile, or move them out of the customer navigation; the issue's complaint is partly that customer pages borrow their vocabulary.
8. **LLM review** is advisory by construction; if it is ever proposed as a gate, it should be rejected.

---

## Appendix: reproduction notes

All tooling was installed into a scratch directory, never the repository: MkDocs 1.6.1 / Material 9.7.7 / GLightbox in a venv; Vale 3.24.0 (SHA-256 `867534dd...b214` verified against the release checksums); lychee 0.24.2 (SHA-256 `1f4e0ef7...c9a` verified); markdownlint-cli2 0.23.3 via npm. Builds and lints ran on a copy of `docs/`, `mkdocs.yml`, `CHANGELOG.md` and `test/integration/docker/`. The prototype JIM Vale style (11 rules), the unknown-parameter probe and the raw outputs were throwaway and were not kept.
