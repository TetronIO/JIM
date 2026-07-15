# v1.0-ILM-COMPLETE Milestone: Execution Analysis

- **Date:** 2026-07-02
- **Last updated:** 2026-07-15
- **Scope:** All 57 open issues on the `v1.0-ILM-COMPLETE` milestone at time of analysis. **Update (same day):** the Priority: Low scope cut was ratified and executed; the seven Low-priority issues now sit on `v1.x-CONNECTORS`, [#154](https://github.com/TetronIO/JIM/issues/154) has since been closed by PR [#897](https://github.com/TetronIO/JIM/pull/897), [#467](https://github.com/TetronIO/JIM/issues/467) was verified and closed, and [#466](https://github.com/TetronIO/JIM/issues/466) (`Watch-JIMLog`) was merged via PR [#901](https://github.com/TetronIO/JIM/pull/901) on 2026-07-03, leaving the milestone at 47 open issues. Priority and Effort ratings have been applied to every issue using the repository's GitHub issue fields.
- **Purpose:** Categorise the milestone by theme, prioritise for fastest route to a polished, production-safe v1.0, and define an execution plan (parallel lanes, serialisation constraints, design decisions needed) suitable for multi-agent delivery.

---

## 0. Progress at a Glance

> **Live view (source of truth):** [v1.0-ILM-COMPLETE milestone](https://github.com/TetronIO/JIM/milestone/4) shows real-time open/closed counts. This section is a curated snapshot; when the two disagree, trust the milestone. Keep the wave statuses and recent-activity list current as issues land.

**Milestone: 35 closed / 39 open (74 total, ~47% complete)** as of 2026-07-11. The milestone has grown beyond the original 57-issue analysis as new issues were raised, so the fixed "in-scope" denominator no longer tracks it; counts here follow the live milestone. **Reconciliation (2026-07-15):** every issue/PR reference in this document that is closed or merged has been struck through; the live milestone remains the authoritative source for current counts (the snapshot figure above is not re-counted here).

`███████████░░░░░░░░░░░░░` 35/74 closed

**New issues raised since the analysis:** [#959](https://github.com/TetronIO/JIM/issues/959) surface unresolved references clearly (v1.0); ~~[#979](https://github.com/TetronIO/JIM/issues/979) PowerShell PascalCase output~~ (**done:** merged), spun out of ~~[#813](https://github.com/TetronIO/JIM/issues/813)~~ (**done**) (v1.0); [#944](https://github.com/TetronIO/JIM/issues/944) GALSYNC cross-forest contact sync (placed on v1.x-CONNECTORS). Per-priority breakdowns are best read from the live milestone; the fixed per-priority table has been retired as unmaintainable against a moving milestone.

| Wave | Focus | Status |
|---|---|---|
| Wave 0 | Hygiene and safety net | 🟢 Done - [#154](https://github.com/TetronIO/JIM/issues/154), [#467](https://github.com/TetronIO/JIM/issues/467), [#466](https://github.com/TetronIO/JIM/issues/466), [#861](https://github.com/TetronIO/JIM/issues/861) and [#14](https://github.com/TetronIO/JIM/issues/14) merged; [#126](https://github.com/TetronIO/JIM/issues/126) and [#438](https://github.com/TetronIO/JIM/issues/438) consolidated (closed as duplicates); [#294](https://github.com/TetronIO/JIM/issues/294) rescoped and moved to v2.0; [#437](https://github.com/TetronIO/JIM/issues/437) assessed (recommend won't-do / defer) and removed from the milestone to the backlog; [#864](https://github.com/TetronIO/JIM/issues/864) implemented (branch pushed, PR pending) |
| Wave 1 | Launch blockers | 🟡 In progress - ~~[#813](https://github.com/TetronIO/JIM/issues/813) API contract normalisation merged (PR [#980](https://github.com/TetronIO/JIM/pull/980))~~ (**done**); ~~[#91](https://github.com/TetronIO/JIM/issues/91) MV attribute priority~~ and ~~[#85](https://github.com/TetronIO/JIM/issues/85) time-period searches~~ merged (**done**); [#487](https://github.com/TetronIO/JIM/issues/487) pagination safety hardening in progress (descoped to guard rails only); PRDs drafted and pushed for [#242](https://github.com/TetronIO/JIM/issues/242), [#288](https://github.com/TetronIO/JIM/issues/288), [#827](https://github.com/TetronIO/JIM/issues/827) and [#809](https://github.com/TetronIO/JIM/issues/809)+[#134](https://github.com/TetronIO/JIM/issues/134); remaining Wave-1 design decisions in Section 6 |
| Wave 2 | Core capability build-out | ⚪ Not started |
| Wave 3 | Polish and completion | ⚪ Not started |

---

## 1. Headline Findings

1. **The milestone is over-scoped for a "fastest to market" goal.** 57 open issues include seven PRD-scale (Effort: High) items. Recommendation: demote roughly 7-10 issues to v1.x explicitly (see Section 3, Priority: Low). Cutting scope is the single biggest velocity lever available. **Done:** the seven Low-priority issues were moved to `v1.x-CONNECTORS` on 2026-07-02.
2. **Several issues are already done or nearly done.** ~~[#154](https://github.com/TetronIO/JIM/issues/154) (API coverage) is complete on branch `claude/gh-154-powershell-coverage-a67jff` awaiting merge~~ (**done:** merged via PR [#897](https://github.com/TetronIO/JIM/pull/897) on 2026-07-02, issue closed); ~~[#467](https://github.com/TetronIO/JIM/issues/467) (role membership API) appears implemented in the codebase~~ (**done:** verified and closed 2026-07-02; safety rules, tests and docs all confirmed); ~~[#14](https://github.com/TetronIO/JIM/issues/14) (change history) is largely delivered and needs a gap audit~~ (**done:** delivered and closed; rollback deferred to [#942](https://github.com/TetronIO/JIM/issues/942)); ~~[#466](https://github.com/TetronIO/JIM/issues/466) is partially superseded by the [#154](https://github.com/TetronIO/JIM/issues/154) branch~~ (**done:** rescoped to `Watch-JIMLog`, merged via PR [#901](https://github.com/TetronIO/JIM/pull/901) on 2026-07-03); ~~[#126](https://github.com/TetronIO/JIM/issues/126) duplicates [#655](https://github.com/TetronIO/JIM/issues/655)~~ (**closed as duplicate**); [#294](https://github.com/TetronIO/JIM/issues/294) double-tracks work owned by ~~[#861](https://github.com/TetronIO/JIM/issues/861)~~ (**done**)/[#636](https://github.com/TetronIO/JIM/issues/636)/[#518](https://github.com/TetronIO/JIM/issues/518)/[#841](https://github.com/TetronIO/JIM/issues/841). A hygiene pass closing these is nearly-free throughput.
3. **The scarce resource is not agents; it is the sync engine.** Three of the largest clusters (import processor, attribute flow, deprovisioning) all modify the same handful of files (`SyncImportTaskProcessor.cs`, `SyncEngine.AttributeFlow.cs`, `SyncTaskProcessorBase.cs`, `ExportEvaluationServer.cs`). Parallelising across those clusters guarantees merge conflicts and re-validation churn. Treat "sync engine" as one work lane with an ordered queue; everything else genuinely parallelises.
4. **Two launch-blocking capability gaps stand out:** [#242](https://github.com/TetronIO/JIM/issues/242) (unique value generation; without it a greenfield customer cannot provision accounts unless HR supplies IT identifiers) and [#655](https://github.com/TetronIO/JIM/issues/655) (leaver deprovisioning silently fails for Joined/Projected CSOs, i.e. the extremely common join-existing-accounts deployment). Both undermine the "ILM complete" claim directly.
5. **One data-loss landmine:** [#421](https://github.com/TetronIO/JIM/issues/421) (Refresh Schema applies destructive cascade deletes in one click). Highest-stakes safety item in the milestone.
6. **A design gate exists:** [#827](https://github.com/TetronIO/JIM/issues/827) (configuration change preview framework) explicitly gates [#204](https://github.com/TetronIO/JIM/issues/204), [#421](https://github.com/TetronIO/JIM/issues/421), [#134](https://github.com/TetronIO/JIM/issues/134) and ~~[#91](https://github.com/TetronIO/JIM/issues/91)~~ (**done**)'s impact-analysis mode, and [#288](https://github.com/TetronIO/JIM/issues/288) (Sync Preview Mode) is the evaluation engine underneath it. The preview family must be designed once, early, or the per-surface previews will be built twice.

> **Caveat on "appears done" claims:** per project rules, sub-agent findings describe intent, not verified behaviour. Items marked "verify then close" (~~[#467](https://github.com/TetronIO/JIM/issues/467)~~ - since verified and closed, ~~[#14](https://github.com/TetronIO/JIM/issues/14)~~ gaps - now closed, ~~[#466](https://github.com/TetronIO/JIM/issues/466)~~ overlap - now closed) need a human-or-agent verification pass against actual code/branches before closure.

---

## 2. Thematic Categorisation

| Theme | Issues | Count |
|---|---|---|
| **Deprovisioning and deletion semantics** | [#655](https://github.com/TetronIO/JIM/issues/655), ~~[#126](https://github.com/TetronIO/JIM/issues/126)~~ (closed dup), [#809](https://github.com/TetronIO/JIM/issues/809), [#134](https://github.com/TetronIO/JIM/issues/134), [#116](https://github.com/TetronIO/JIM/issues/116), [#118](https://github.com/TetronIO/JIM/issues/118), [#119](https://github.com/TetronIO/JIM/issues/119) | 7 |
| **Attribute flow and sync semantics** | ~~[#91](https://github.com/TetronIO/JIM/issues/91)~~ (done), [#435](https://github.com/TetronIO/JIM/issues/435), [#223](https://github.com/TetronIO/JIM/issues/223), [#242](https://github.com/TetronIO/JIM/issues/242), [#399](https://github.com/TetronIO/JIM/issues/399), [#207](https://github.com/TetronIO/JIM/issues/207), [#204](https://github.com/TetronIO/JIM/issues/204) | 7 |
| **Sync engine robustness and performance** | [#872](https://github.com/TetronIO/JIM/issues/872), [#873](https://github.com/TetronIO/JIM/issues/873), [#874](https://github.com/TetronIO/JIM/issues/874), [#497](https://github.com/TetronIO/JIM/issues/497), [#498](https://github.com/TetronIO/JIM/issues/498), [#880](https://github.com/TetronIO/JIM/issues/880), [#437](https://github.com/TetronIO/JIM/issues/437), ~~[#438](https://github.com/TetronIO/JIM/issues/438)~~ (closed dup) | 8 |
| **Preview / what-if / impact analysis** | [#827](https://github.com/TetronIO/JIM/issues/827), [#288](https://github.com/TetronIO/JIM/issues/288), [#421](https://github.com/TetronIO/JIM/issues/421) (also [#134](https://github.com/TetronIO/JIM/issues/134), [#204](https://github.com/TetronIO/JIM/issues/204) above) | 3 (+2 shared) |
| **Metaverse schema and config management** | ~~[#377](https://github.com/TetronIO/JIM/issues/377)~~ (done), [#376](https://github.com/TetronIO/JIM/issues/376), [#348](https://github.com/TetronIO/JIM/issues/348), [#359](https://github.com/TetronIO/JIM/issues/359), ~~[#85](https://github.com/TetronIO/JIM/issues/85)~~ (done), ~~[#813](https://github.com/TetronIO/JIM/issues/813)~~ (done) | 6 |
| **API / PowerShell surface** | ~~[#154](https://github.com/TetronIO/JIM/issues/154)~~ (done), ~~[#467](https://github.com/TetronIO/JIM/issues/467)~~ (done), ~~[#466](https://github.com/TetronIO/JIM/issues/466)~~ (done), [#487](https://github.com/TetronIO/JIM/issues/487), [#186](https://github.com/TetronIO/JIM/issues/186) | 5 |
| **LDAP connector** | [#230](https://github.com/TetronIO/JIM/issues/230), [#231](https://github.com/TetronIO/JIM/issues/231), [#351](https://github.com/TetronIO/JIM/issues/351) | 3 |
| **Security, audit, RBAC** | [#9](https://github.com/TetronIO/JIM/issues/9), ~~[#14](https://github.com/TetronIO/JIM/issues/14)~~ (done), [#881](https://github.com/TetronIO/JIM/issues/881), ~~[#500](https://github.com/TetronIO/JIM/issues/500)~~ (done), [#464](https://github.com/TetronIO/JIM/issues/464) | 5 |
| **Operational monitoring / admin UI** | [#169](https://github.com/TetronIO/JIM/issues/169), [#453](https://github.com/TetronIO/JIM/issues/453), [#454](https://github.com/TetronIO/JIM/issues/454), [#864](https://github.com/TetronIO/JIM/issues/864), [#307](https://github.com/TetronIO/JIM/issues/307) | 5 |
| **Testing and release engineering** | ~~[#861](https://github.com/TetronIO/JIM/issues/861)~~ (done), [#636](https://github.com/TetronIO/JIM/issues/636), [#518](https://github.com/TetronIO/JIM/issues/518), [#519](https://github.com/TetronIO/JIM/issues/519), [#841](https://github.com/TetronIO/JIM/issues/841), [#294](https://github.com/TetronIO/JIM/issues/294), [#582](https://github.com/TetronIO/JIM/issues/582), [#877](https://github.com/TetronIO/JIM/issues/877) | 8 |

---

## 3. Priority Tiers

Priority and Effort below use the GitHub issue-field vocabulary configured for this repository (Priority: Urgent / High / Medium / Low; Effort: High / Medium / Low), and the same values have been applied to each issue's fields on GitHub. The original size scale maps S → Low, M → Medium, L and XL → High, with PRD-scale (former XL) items flagged in their notes. Priority is judged against one question: *what does an ILM customer in a high-trust environment need to run production synchronisation safely at first adoption?* Correctness and safety outrank performance and polish; performance outranks convenience.

### Priority: Urgent - must land before GA (13)

| Issue | Why it blocks launch | Effort |
|---|---|---|
| ~~[#91](https://github.com/TetronIO/JIM/issues/91) MV attribute priority (finish)~~ **Done:** merged (engine + view/reorder UI). | ~~High (remainder)~~ |
| [#655](https://github.com/TetronIO/JIM/issues/655) MVO deletion cascade: Provisioned-only delete exports | Leaver deprovisioning fails for Joined/Projected CSOs; breaks the core JML guarantee in the most common deployment shape. Merge ~~[#126](https://github.com/TetronIO/JIM/issues/126)~~ (closed dup) into it. | Medium |
| [#242](https://github.com/TetronIO/JIM/issues/242) Unique value generation | Table-stakes provisioning (sAMAccountName, UPN, mail). Without it greenfield provisioning is impossible. Needs PRD first. | High |
| [#421](https://github.com/TetronIO/JIM/issues/421) Schema refresh preview phase | One click can cascade-delete Synchronisation Rules and objects. Unacceptable in target sectors. | High |
| [#288](https://github.com/TetronIO/JIM/issues/288) Sync Preview Mode (what-if) | Admins will not run a first sync against production AD blind; also the engine the whole preview family builds on. Needs PRD. | High |
| ~~[#377](https://github.com/TetronIO/JIM/issues/377) CRUD custom Metaverse Attributes~~ **Done:** merged via PR [#1023](https://github.com/TetronIO/JIM/pull/1023) (app layer, REST API, PowerShell, Web UI, values-block/cascade/type-the-name safeguards, docs). | ~~Medium~~ |
| [#376](https://github.com/TetronIO/JIM/issues/376) CRUD custom Metaverse Object Types | Modelling beyond User/Group requires it; unlocks per-type deletion rules. Depends on ~~[#377](https://github.com/TetronIO/JIM/issues/377)~~ (done). | High |
| ~~[#154](https://github.com/TetronIO/JIM/issues/154) API Endpoint Coverage (merge branch)~~ | **Done:** merged via PR [#897](https://github.com/TetronIO/JIM/pull/897) on 2026-07-02; the API/PowerShell lane is unblocked. | ~~S~~ |
| ~~[#467](https://github.com/TetronIO/JIM/issues/467) Role membership API (verify + close)~~ | **Done:** verified (endpoints, lockout rules, cmdlets, 22 green tests, docs) and closed 2026-07-02. | ~~Low~~ |
| ~~[#14](https://github.com/TetronIO/JIM/issues/14) Change history (gap audit + close)~~ **Done:** delivered and merged (PR [#945](https://github.com/TetronIO/JIM/pull/945)); rollback deferred to [#942](https://github.com/TetronIO/JIM/issues/942). | ~~Medium~~ |
| [#9](https://github.com/TetronIO/JIM/issues/9) Synchronisation Readers role | "Give the helpdesk read-only access" is a first-week ask; everyone-as-Administrator is unacceptable. | Medium |
| ~~[#500](https://github.com/TetronIO/JIM/issues/500) OWASP remediation~~ **Done:** OWASP 2025 gaps remediated (rate limiting, privileged-operations audit trail, CSP, lockfiles) and closed. | ~~High (splittable)~~ |
| ~~[#861](https://github.com/TetronIO/JIM/issues/861) DB-backed test tier in CI~~ **Done:** real-PostgreSQL CI tier landed. | ~~Low~~ |

### Priority: High - should ship (strong pull, if capacity) (20)

[#435](https://github.com/TetronIO/JIM/issues/435) (MVA to SVA import flow; every RFC-compliant LDAP directory hits it), [#118](https://github.com/TetronIO/JIM/issues/118) (conditional MVO deletion; "never delete an active employee" safety net), [#223](https://github.com/TetronIO/JIM/issues/223) (export-only attribute flow; standard initial-password pattern), [#873](https://github.com/TetronIO/JIM/issues/873) (tolerate unresolved references; stops error-channel noise training operators to ignore errors), [#874](https://github.com/TetronIO/JIM/issues/874) (sync error object snapshots; core supportability), [#351](https://github.com/TetronIO/JIM/issues/351) Phase 1 (container/OU scoping; real directories always need exclusions), [#230](https://github.com/TetronIO/JIM/issues/230) (LDAP DC pinning; replication lag masquerades as sync bugs), [#881](https://github.com/TetronIO/JIM/issues/881) (sensitive attribute value gating in history), [#487](https://github.com/TetronIO/JIM/issues/487) (API pagination/rate hardening), ~~[#813](https://github.com/TetronIO/JIM/issues/813) (API contract normalisation; **v1.0 is the last free window to break the contract**)~~ (**done**), [#169](https://github.com/TetronIO/JIM/issues/169) (admin dashboard, health slice only), [#348](https://github.com/TetronIO/JIM/issues/348) (MVO detail "why not provisioned"; top helpdesk escalation), ~~[#85](https://github.com/TetronIO/JIM/issues/85) (relative-date search periods; note the re-evaluation problem is tracked separately in [#892](https://github.com/TetronIO/JIM/issues/892), outside this milestone)~~ (**done**; [#892](https://github.com/TetronIO/JIM/issues/892) also closed), [#827](https://github.com/TetronIO/JIM/issues/827) (preview framework **design**; gates four other issues), [#134](https://github.com/TetronIO/JIM/issues/134) + [#809](https://github.com/TetronIO/JIM/issues/809) (Connected System deletion preview/execute pair; co-design), [#864](https://github.com/TetronIO/JIM/issues/864) (search-as-you-type; cheap credibility polish), [#186](https://github.com/TetronIO/JIM/issues/186) (PowerShell Gallery ownership; supply-chain optics), [#294](https://github.com/TetronIO/JIM/issues/294) (rescope to the GALSYNC CSV-export scenario only), [#518](https://github.com/TetronIO/JIM/issues/518) (for GA: a documented manual "full integration suite green" checklist step; the automated gate is v1.x).

### Priority: Medium - nice to have (opportunistic; do not schedule ahead of Urgent/High) (16)

[#116](https://github.com/TetronIO/JIM/issues/116), [#119](https://github.com/TetronIO/JIM/issues/119) (deletion rule refinements), [#207](https://github.com/TetronIO/JIM/issues/207) (matching rule operators), [#204](https://github.com/TetronIO/JIM/issues/204) (scope-change warnings; fold into [#827](https://github.com/TetronIO/JIM/issues/827)), [#872](https://github.com/TetronIO/JIM/issues/872) (SVA update in place), [#497](https://github.com/TetronIO/JIM/issues/497) (import parallelism), [#399](https://github.com/TetronIO/JIM/issues/399) (causality tracking; ~~[#91](https://github.com/TetronIO/JIM/issues/91)~~ (done) already shipped the highest-value piece), [#453](https://github.com/TetronIO/JIM/issues/453), [#454](https://github.com/TetronIO/JIM/issues/454) (Activity detail visualisations), ~~[#466](https://github.com/TetronIO/JIM/issues/466) (remainder: `Watch-JIMLog` only)~~ (**done**), [#519](https://github.com/TetronIO/JIM/issues/519) (continuous SBOMs), [#841](https://github.com/TetronIO/JIM/issues/841) (preview releases), [#636](https://github.com/TetronIO/JIM/issues/636) Phases 1-3 (parallel integration tests), [#231](https://github.com/TetronIO/JIM/issues/231) (surface LDAP capabilities), [#437](https://github.com/TetronIO/JIM/issues/437), ~~[#438](https://github.com/TetronIO/JIM/issues/438)~~ (closed dup) (partition scoping **evaluations**; cheap design docs, and "won't do" is a valid outcome that deletes future scope; do the write-up early).

### Priority: Low - demoted to v1.x (7) - **executed 2026-07-02, all seven moved to the v1.x-CONNECTORS milestone**

| Issue | Rationale |
|---|---|
| [#307](https://github.com/TetronIO/JIM/issues/307) Real-time notifications | Rewires inter-service comms at exactly the wrong moment; polling works. |
| [#359](https://github.com/TetronIO/JIM/issues/359) Configuration migration | XL, everything open; DB-backup-based config backup is a documented workaround. Design after [#376](https://github.com/TetronIO/JIM/issues/376)/~~[#377](https://github.com/TetronIO/JIM/issues/377)~~ (done) stabilise the config surface anyway. |
| [#464](https://github.com/TetronIO/JIM/issues/464) External task step types | Arbitrary code execution shipped half-hardened into healthcare/government is worse than not shipping it. Defer unless a full security pass fits. |
| [#498](https://github.com/TetronIO/JIM/issues/498) Sync phase parallelism | Slow-and-correct is shippable; corrupt-and-fast is not. Design spike only if benchmarks demand it. |
| [#880](https://github.com/TetronIO/JIM/issues/880) Export expression re-evaluation optimisation | Issue itself says pursue only if profiling proves the cost; failure mode is wrong export values. |
| [#582](https://github.com/TetronIO/JIM/issues/582) Screenshot automation | Manual refresh for v1.0 docs; automate after UI churn settles. |
| [#877](https://github.com/TetronIO/JIM/issues/877) Example data boolean distribution | Demo realism only; do opportunistically alongside [#582](https://github.com/TetronIO/JIM/issues/582). |

Also v1.x: ~~[#438](https://github.com/TetronIO/JIM/issues/438) implementation (evaluation may close it entirely)~~ (**closed as duplicate**, folded into [#437](https://github.com/TetronIO/JIM/issues/437)), [#636](https://github.com/TetronIO/JIM/issues/636) Phase 4 (blocked on self-hosted runners), [#294](https://github.com/TetronIO/JIM/issues/294) scenarios 6-7 (blocked on Internal MVO design, [#614](https://github.com/TetronIO/JIM/issues/614)/~~[#600](https://github.com/TetronIO/JIM/issues/600)~~ (closed dup), not on this milestone).

---

## 4. Serialisation Constraints (file-collision domains)

These are the hard "do not parallelise" rules. Each domain is a set of issues that modify the same files; within a domain, work must be sequenced (or combined into one branch).

| Domain | Files | Ordered queue |
|---|---|---|
| **D1: Import processor** | `SyncImportTaskProcessor.cs` | [#872](https://github.com/TetronIO/JIM/issues/872) → [#873](https://github.com/TetronIO/JIM/issues/873) → [#874](https://github.com/TetronIO/JIM/issues/874) → ([#497](https://github.com/TetronIO/JIM/issues/497) last, if kept: parallelise only after semantics settle) |
| **D2: Attribute flow engine** | `SyncEngine.AttributeFlow.cs`, `SyncTaskProcessorBase.cs`, `ISyncEngine` | ~~[#91](https://github.com/TetronIO/JIM/issues/91) (finish engine edges)~~ (done) → [#435](https://github.com/TetronIO/JIM/issues/435) → [#223](https://github.com/TetronIO/JIM/issues/223) → [#242](https://github.com/TetronIO/JIM/issues/242). [#399](https://github.com/TetronIO/JIM/issues/399) slots in any time after ~~[#91](https://github.com/TetronIO/JIM/issues/91)~~ (done). [#873](https://github.com/TetronIO/JIM/issues/873) also brushes this surface; coordinate D1/D2 merges. |
| **D3: Deprovisioning / export evaluation** | `ExportEvaluationServer.cs`, obsoletion processors | [#655](https://github.com/TetronIO/JIM/issues/655) (decision + implementation, with ~~[#126](https://github.com/TetronIO/JIM/issues/126)~~ (closed dup) merged in) → then [#809](https://github.com/TetronIO/JIM/issues/809) + [#134](https://github.com/TetronIO/JIM/issues/134) as one co-designed preview/execute pair. [#223](https://github.com/TetronIO/JIM/issues/223) and [#880](https://github.com/TetronIO/JIM/issues/880) also touch export evaluation; sequence around them. |
| **D4: MVO Deletion Rule config** | `MetaverseObjectType` model, deletion evaluator, object type admin page | [#116](https://github.com/TetronIO/JIM/issues/116) → [#118](https://github.com/TetronIO/JIM/issues/118) → [#119](https://github.com/TetronIO/JIM/issues/119), or one combined "deletion rule enhancements" branch. Only [#118](https://github.com/TetronIO/JIM/issues/118) is High priority; [#116](https://github.com/TetronIO/JIM/issues/116)/[#119](https://github.com/TetronIO/JIM/issues/119) can slip. |
| **D5: API controllers + PowerShell module** | `MetaverseController`, `SecurityController`, PS module | ~~[#154](https://github.com/TetronIO/JIM/issues/154) merge~~ (done, PR [#897](https://github.com/TetronIO/JIM/pull/897)) → ~~[#467](https://github.com/TetronIO/JIM/issues/467) verify/close~~ (done) → ~~[#466](https://github.com/TetronIO/JIM/issues/466) rescope~~ (done, PR [#901](https://github.com/TetronIO/JIM/pull/901)) → ~~[#813](https://github.com/TetronIO/JIM/issues/813) (normalise contract **before** new surfaces are added)~~ (done) → [#487](https://github.com/TetronIO/JIM/issues/487) → then ~~[#377](https://github.com/TetronIO/JIM/issues/377)~~ (done)/[#376](https://github.com/TetronIO/JIM/issues/376) API gap-fill |
| **D6: Preview family** | New preview framework + sync engine read paths | [#827](https://github.com/TetronIO/JIM/issues/827) design → [#288](https://github.com/TetronIO/JIM/issues/288) engine → adapters ([#421](https://github.com/TetronIO/JIM/issues/421), [#204](https://github.com/TetronIO/JIM/issues/204), [#134](https://github.com/TetronIO/JIM/issues/134), ~~[#91](https://github.com/TetronIO/JIM/issues/91)~~ (done) mode 2). See tension note below. |
| **D7: Audit / security** | `ChangeHistoryServer`, history UI, roles/policies | ~~[#14](https://github.com/TetronIO/JIM/issues/14) gap-close~~ (done) → [#9](https://github.com/TetronIO/JIM/issues/9) → [#881](https://github.com/TetronIO/JIM/issues/881). ~~[#500](https://github.com/TetronIO/JIM/issues/500)~~ (done)'s audit-trail item was designed against ~~[#14](https://github.com/TetronIO/JIM/issues/14)~~ (done) (one audit system, not two); its other items are independent. |
| **D8: Integration runner + CI workflows** | `Run-IntegrationTests.ps1`, `ci.yml`, `release.yml` | ~~[#861](https://github.com/TetronIO/JIM/issues/861)~~ (done) → [#636](https://github.com/TetronIO/JIM/issues/636) (Phases 1-3) → [#841](https://github.com/TetronIO/JIM/issues/841) → [#518](https://github.com/TetronIO/JIM/issues/518). [#294](https://github.com/TetronIO/JIM/issues/294)'s scenario work serialises with [#636](https://github.com/TetronIO/JIM/issues/636). [#519](https://github.com/TetronIO/JIM/issues/519) anytime (trivial `ci.yml` merge overlap with ~~[#861](https://github.com/TetronIO/JIM/issues/861)~~ (done)/~~[#500](https://github.com/TetronIO/JIM/issues/500)~~ (done) lockfiles). |
| **D9: Activity detail page** | `ActivityDetail.razor` | [#453](https://github.com/TetronIO/JIM/issues/453) → [#454](https://github.com/TetronIO/JIM/issues/454) → [#864](https://github.com/TetronIO/JIM/issues/864) sweep last |
| **D10: LDAP connector connection path** | `LdapConnector*`, persisted connector data | [#230](https://github.com/TetronIO/JIM/issues/230) → [#231](https://github.com/TetronIO/JIM/issues/231). [#351](https://github.com/TetronIO/JIM/issues/351) touches different files (containers/partitions) and is parallel-safe. |

**The [#421](https://github.com/TetronIO/JIM/issues/421) vs [#827](https://github.com/TetronIO/JIM/issues/827) tension, resolved:** [#421](https://github.com/TetronIO/JIM/issues/421) is an Urgent safety fix; [#827](https://github.com/TetronIO/JIM/issues/827) is a High-priority design framework that claims to gate it. Do not let the framework block the safety fix. Time-box the [#827](https://github.com/TetronIO/JIM/issues/827) design (it is a design document, not a build); build [#421](https://github.com/TetronIO/JIM/issues/421) as the framework's first adapter if the design lands in time, otherwise ship [#421](https://github.com/TetronIO/JIM/issues/421) standalone behind a minimal interface seam and retrofit. Safety beats architectural purity here.

**Cross-domain rule:** D1, D2, D3 (and [#288](https://github.com/TetronIO/JIM/issues/288)'s engine work in D6) all live in the sync engine. Cap sync-engine work-in-progress at **two concurrent branches maximum**, and prefer one. All other domains are genuinely independent of each other.

---

## 5. Execution Plan: Lanes and Waves

Ten parallel lanes, one agent (or agent session) per lane, serialised within each lane per Section 4. Suggested waves:

### Wave 0 - Hygiene and safety net (days, mostly Effort: Low items)

- ~~Merge the [#154](https://github.com/TetronIO/JIM/issues/154) branch~~ (**done:** PR [#897](https://github.com/TetronIO/JIM/pull/897) merged 2026-07-02); ~~verify and close [#467](https://github.com/TetronIO/JIM/issues/467)~~ (**done:** verified and closed 2026-07-02); ~~gap-audit [#14](https://github.com/TetronIO/JIM/issues/14)~~ (**done**); ~~reconcile [#466](https://github.com/TetronIO/JIM/issues/466) naming against the merged [#154](https://github.com/TetronIO/JIM/issues/154) cmdlets~~ (**done**).
- Merge ~~[#126](https://github.com/TetronIO/JIM/issues/126)~~ (closed dup) into [#655](https://github.com/TetronIO/JIM/issues/655) (one design thread); rescope [#294](https://github.com/TetronIO/JIM/issues/294) to the GALSYNC scenario and fold its CI/release bullets into [#518](https://github.com/TetronIO/JIM/issues/518)/[#841](https://github.com/TetronIO/JIM/issues/841).
- ~~Land [#861](https://github.com/TetronIO/JIM/issues/861) (CI PostgreSQL tier) before any sync-engine work starts.~~ (**done**)
- Write the [#437](https://github.com/TetronIO/JIM/issues/437) + ~~[#438](https://github.com/TetronIO/JIM/issues/438)~~ (closed dup) partition-scoping evaluation as one document (pure analysis; `PartitionId` already exists on `ConnectedSystemObject`); expected outcome may close both.
- Quick wins: [#864](https://github.com/TetronIO/JIM/issues/864) search sweep, [#877](https://github.com/TetronIO/JIM/issues/877) if convenient.
- **Ratify the Priority: Low scope cut and answer the Wave 1 design decisions (Section 6).**

### Wave 1 - Launch blockers, maximum parallel spread

| Lane | Work |
|---|---|
| Sync engine (D2/D3) | ~~Finish [#91](https://github.com/TetronIO/JIM/issues/91) engine edges + UI~~ (done); [#655](https://github.com/TetronIO/JIM/issues/655) decision + implementation |
| Preview (D6) | [#827](https://github.com/TetronIO/JIM/issues/827) design PRD (time-boxed); [#421](https://github.com/TetronIO/JIM/issues/421) build begins as first adapter or standalone |
| Design/PRD lane | PRDs in parallel: [#242](https://github.com/TetronIO/JIM/issues/242), [#288](https://github.com/TetronIO/JIM/issues/288), [#809](https://github.com/TetronIO/JIM/issues/809)+[#134](https://github.com/TetronIO/JIM/issues/134) pair (PRDs do not collide with code) |
| API (D5) | ~~[#813](https://github.com/TetronIO/JIM/issues/813) contract normalisation~~ (done) → [#487](https://github.com/TetronIO/JIM/issues/487) |
| Schema (part of D5 tail) | ~~[#377](https://github.com/TetronIO/JIM/issues/377) Web UI + safeguards~~ (done, merged via PR [#1023](https://github.com/TetronIO/JIM/pull/1023)) |
| Security (D7) | [#9](https://github.com/TetronIO/JIM/issues/9) Readers role; ~~[#500](https://github.com/TetronIO/JIM/issues/500) rate limiting, CSP, lockfiles~~ (done) |
| LDAP (D10) | [#230](https://github.com/TetronIO/JIM/issues/230) DC pinning |
| UI (D9) | [#453](https://github.com/TetronIO/JIM/issues/453) → [#454](https://github.com/TetronIO/JIM/issues/454) |
| Ops | [#186](https://github.com/TetronIO/JIM/issues/186) (user action + small workflow); [#519](https://github.com/TetronIO/JIM/issues/519) |

### Wave 2 - Core capability build-out

Sync engine: [#435](https://github.com/TetronIO/JIM/issues/435) → [#873](https://github.com/TetronIO/JIM/issues/873)/[#872](https://github.com/TetronIO/JIM/issues/872) (D1); Preview: [#288](https://github.com/TetronIO/JIM/issues/288) engine build; [#242](https://github.com/TetronIO/JIM/issues/242) implementation once PRD approved; Schema: [#376](https://github.com/TetronIO/JIM/issues/376); Security: [#881](https://github.com/TetronIO/JIM/issues/881), ~~[#500](https://github.com/TetronIO/JIM/issues/500) audit trail (unified with [#14](https://github.com/TetronIO/JIM/issues/14))~~ (both done); LDAP: [#351](https://github.com/TetronIO/JIM/issues/351) Phase 1, [#231](https://github.com/TetronIO/JIM/issues/231); UI: [#169](https://github.com/TetronIO/JIM/issues/169) health-slice dashboard; Release: [#636](https://github.com/TetronIO/JIM/issues/636) Phases 1-3.

### Wave 3 - Polish and completion

[#223](https://github.com/TetronIO/JIM/issues/223), [#874](https://github.com/TetronIO/JIM/issues/874), [#118](https://github.com/TetronIO/JIM/issues/118) (plus [#116](https://github.com/TetronIO/JIM/issues/116)/[#119](https://github.com/TetronIO/JIM/issues/119) if retained), [#134](https://github.com/TetronIO/JIM/issues/134) implementation, [#348](https://github.com/TetronIO/JIM/issues/348), ~~[#85](https://github.com/TetronIO/JIM/issues/85)~~ (done), [#204](https://github.com/TetronIO/JIM/issues/204) (as [#827](https://github.com/TetronIO/JIM/issues/827) adapter), ~~[#466](https://github.com/TetronIO/JIM/issues/466) remainder~~ (done), [#841](https://github.com/TetronIO/JIM/issues/841), [#518](https://github.com/TetronIO/JIM/issues/518) as a documented manual release-gate checklist, [#294](https://github.com/TetronIO/JIM/issues/294) GALSYNC scenario, [#399](https://github.com/TetronIO/JIM/issues/399) opportunistically.

### Explicitly not scheduled (v1.x)

[#307](https://github.com/TetronIO/JIM/issues/307), [#359](https://github.com/TetronIO/JIM/issues/359), [#464](https://github.com/TetronIO/JIM/issues/464), [#498](https://github.com/TetronIO/JIM/issues/498), [#880](https://github.com/TetronIO/JIM/issues/880), [#582](https://github.com/TetronIO/JIM/issues/582) and [#877](https://github.com/TetronIO/JIM/issues/877) are now on the v1.x-CONNECTORS milestone (moved 2026-07-02). [#497](https://github.com/TetronIO/JIM/issues/497) stays on the milestone at Medium priority but should slip unless benchmarks force it; ~~[#438](https://github.com/TetronIO/JIM/issues/438)'s implementation half is also unscheduled (the evaluation may close it)~~ (**closed as duplicate**).

---

## 6. Decisions Needed From the Product Owner

Batched so they can be answered in one sitting. Wave 1 cannot fully start without the first group.

**Blocking Wave 1:**
1. **Scope cut:** ratify (or amend) the Priority: Low demotions in Section 3. **RATIFIED 2026-07-02:** [#307](https://github.com/TetronIO/JIM/issues/307), [#359](https://github.com/TetronIO/JIM/issues/359), [#464](https://github.com/TetronIO/JIM/issues/464), [#498](https://github.com/TetronIO/JIM/issues/498), [#880](https://github.com/TetronIO/JIM/issues/880), [#582](https://github.com/TetronIO/JIM/issues/582) and [#877](https://github.com/TetronIO/JIM/issues/877) moved to the v1.x-CONNECTORS milestone. The v1.0-ILM-COMPLETE milestone now holds 50 open issues.
2. **[#655](https://github.com/TetronIO/JIM/issues/655):** per-Synchronisation-Rule `DeprovisionScope` setting vs honouring `OutboundDeprovisionAction` unconditionally; is a confirmation/dry-run safety net required for v1.0?
3. **[#242](https://github.com/TetronIO/JIM/issues/242):** expression function vs dedicated generator; uniqueness scope (Metaverse vs Connected System vs live target query); collision suffix strategy; intra-batch collision handling. **PRD drafted** (`feature/242-prd`) with a recommendation for each; awaiting review.
4. **[#827](https://github.com/TetronIO/JIM/issues/827):** where preview computation runs (worker job vs synchronous); how unsaved proposed config is represented; do preview results persist as Activities; which preview tiers are v1.0-mandatory. **PRD drafted** (`feature/827-prd`) with a recommendation for each; awaiting review.
5. **[#288](https://github.com/TetronIO/JIM/issues/288):** approach to guaranteeing zero side effects (reuse real sync code paths read-only vs shadow evaluation); sampling strategy at 100K+ objects; UI-only for v1.0 or API/PowerShell too. **PRD drafted** (`feature/288-prd`) with a recommendation for each; awaiting review.
6. **[#421](https://github.com/TetronIO/JIM/issues/421):** recompute-diff-on-apply vs holding the previewed diff; stale-diff behaviour.
7. **[#9](https://github.com/TetronIO/JIM/issues/9):** exactly what a Synchronisation Reader can see (Activities, CSO/MVO detail, Pending Exports; presumably not connector credentials) - interlocks with decision 8.
8. **[#881](https://github.com/TetronIO/JIM/issues/881):** "sensitive" as a per-attribute schema flag vs fixed list; which role gates it; read-time redaction (recommended) vs write-time.

**Needed before their respective builds:**
9. ~~**[#500](https://github.com/TetronIO/JIM/issues/500):** is the privileged-operations audit trail an extension of [#14](https://github.com/TetronIO/JIM/issues/14)'s change history (recommended: one audit system) or separate; CSP strictness compatible with Blazor Server (nonce vs unsafe-inline).~~ **Done:** [#500](https://github.com/TetronIO/JIM/issues/500) remediated; audit trail unified with [#14](https://github.com/TetronIO/JIM/issues/14)'s change history.
10. **[#874](https://github.com/TetronIO/JIM/issues/874):** snapshot setting default on or off (personal data exposure vs supportability); retention/size limits.
11. **[#873](https://github.com/TetronIO/JIM/issues/873):** what "warn" means in the RPEI/Activity model; do ignored unresolved references still count in batch statistics.
12. ~~**[#85](https://github.com/TetronIO/JIM/issues/85):** accept that relative-date scopes only re-evaluate on full synchronisation for v1.0.~~ **Done:** [#85](https://github.com/TetronIO/JIM/issues/85) merged.
13. ~~**[#813](https://github.com/TetronIO/JIM/issues/813):** adopt the nested `type: {id, name}` shape?~~ **Decided (yes) and done:** merged via PR [#980](https://github.com/TetronIO/JIM/pull/980); spun out ~~[#979](https://github.com/TetronIO/JIM/issues/979)~~ (**done**) (PowerShell PascalCase output, v1.0).
14. **[#351](https://github.com/TetronIO/JIM/issues/351):** confirm Phase 1 (OneLevel per container) only for v1.0.
15. **[#118](https://github.com/TetronIO/JIM/issues/118):** confirm fail-safe semantics (condition unevaluable = do not delete).
16. **[#518](https://github.com/TetronIO/JIM/issues/518):** accept a documented manual "full integration suite green against the release commit" checklist step for the v1.0 release, automation in v1.x.

**Personal actions only you can do:**
17. ~~**[#154](https://github.com/TetronIO/JIM/issues/154):** review and merge branch `claude/gh-154-powershell-coverage-a67jff`.~~ **Done:** merged via PR [#897](https://github.com/TetronIO/JIM/pull/897) on 2026-07-02.
18. **[#186](https://github.com/TetronIO/JIM/issues/186):** create the Tetron service account and PowerShell Gallery co-owner invite; provide the API key for CI secrets.

---

## 7. Process Recommendations for Multi-Agent Throughput

**Issue pickup protocol (mandatory):** the moment an agent picks up an issue, it must (1) assign the issue to `JayVDZ` and (2) mark it in progress by applying the `in progress` label. Before declaring implementation work complete, the agent must also **verify the change at runtime**, not just via unit tests: web changes are exercised and screenshotted in a live sandbox stack, and PowerShell changes are executed against a live instance, per `engineering/SANDBOX_RUNTIME_VERIFICATION.md`. When the work lands (or is abandoned), remove the label; closing the issue does not remove it automatically. Note: the repository has no "Status" issue field and Projects board columns are not reachable through the current tooling, so the label is the canonical in-progress signal; if a Status issue field is created at org level later (alongside Priority/Effort), switch to it.

1. **Verification sweep before building.** Cheapest wins in the milestone are closures, not code. Run a small agent pass that verifies "appears done" claims (~~[#467](https://github.com/TetronIO/JIM/issues/467)~~ (done), ~~[#14](https://github.com/TetronIO/JIM/issues/14)~~ (done), ~~[#466](https://github.com/TetronIO/JIM/issues/466)~~ (done)) against actual code and closes or re-scopes issues. Do this before scheduling any implementation agents at those surfaces.
2. **Schedule by collision domain, not by theme.** Themes are for humans; the file-collision domains in Section 4 are the scheduling primitive for agents. One branch per issue, one active branch per domain (two max for the sync engine overall), merge to `main` quickly and often. Long-lived parallel branches plus squash merges is the worst combination.
3. **PRDs fan out freely.** Design documents never collide in code. All Wave 1 PRDs ([#242](https://github.com/TetronIO/JIM/issues/242), [#288](https://github.com/TetronIO/JIM/issues/288), [#827](https://github.com/TetronIO/JIM/issues/827), [#809](https://github.com/TetronIO/JIM/issues/809)+[#134](https://github.com/TetronIO/JIM/issues/134)) can be drafted by parallel agents immediately; each PRD should end with its own "decisions needed" list feeding the Section 6 batch. Review them as a batch, not one by one.
4. **Batch product decisions.** Agents stall on small owner decisions far more than on hard engineering. Keep Section 6 as a living decision log; answer in batches; agents record the answer in the relevant issue so the next session does not re-ask.
5. **Safety net scales with parallelism.** More concurrent agents means more regression surface. ~~[#861](https://github.com/TetronIO/JIM/issues/861)~~ (done) (real-PostgreSQL CI tier) lands first, and any sync-engine branch runs the relevant integration scenarios before merge, per existing project rules.
6. **Label the lanes in GitHub.** Add `lane:*` labels (or a Project field) matching Section 5 so any session can see at a glance which lanes are occupied. Parallel chat sessions tracked by branch (the existing convention) then map cleanly onto lanes.
7. **Consolidate before implementing.** Duplicate/overlapping issue pairs ([#655](https://github.com/TetronIO/JIM/issues/655)+~~[#126](https://github.com/TetronIO/JIM/issues/126)~~ (closed dup), [#294](https://github.com/TetronIO/JIM/issues/294) vs [#518](https://github.com/TetronIO/JIM/issues/518)/[#841](https://github.com/TetronIO/JIM/issues/841)/[#636](https://github.com/TetronIO/JIM/issues/636), ~~[#466](https://github.com/TetronIO/JIM/issues/466)~~ (done) vs ~~[#154](https://github.com/TetronIO/JIM/issues/154)~~ (done), [#204](https://github.com/TetronIO/JIM/issues/204) vs [#827](https://github.com/TetronIO/JIM/issues/827)) must be merged or re-scoped *before* an agent picks them up, or two agents will build the same thing differently.
8. **Sequence UI sweeps last within a page.** Mechanical sweeps ([#864](https://github.com/TetronIO/JIM/issues/864)) go after feature work on the same pages ([#453](https://github.com/TetronIO/JIM/issues/453)/[#454](https://github.com/TetronIO/JIM/issues/454)/[#169](https://github.com/TetronIO/JIM/issues/169)) to avoid trivial-but-constant conflicts.

---

## 8. Full Issue Index

Rows marked **Low → v1.x** were moved to the v1.x-CONNECTORS milestone on 2026-07-02 and are no longer on v1.0-ILM-COMPLETE; they are retained here for the record. Rows marked **Done** / **Closed** have landed or been closed since the analysis.

| # | Title (abbreviated) | Theme | Priority | Effort | Domain/Lane | Key dependency notes |
|---|---|---|---|---|---|---|
| 9 | Synchronisation Readers role | Security/RBAC | Urgent | Medium | D7 | Before [#881](https://github.com/TetronIO/JIM/issues/881); collides with [#154](https://github.com/TetronIO/JIM/issues/154)/[#467](https://github.com/TetronIO/JIM/issues/467) controllers - after D5 merge |
| ~~14~~ | ~~Change history config + business data~~ | Audit | **Done** | ~~Medium~~ | D7 | Delivered and closed; rollback deferred to [#942](https://github.com/TetronIO/JIM/issues/942); fed [#881](https://github.com/TetronIO/JIM/issues/881), [#500](https://github.com/TetronIO/JIM/issues/500) audit trail |
| ~~85~~ | ~~Time periods on searches~~ | Schema/search | **Done** | ~~Medium~~ | Schema | Merged; re-evaluation problem was tracked in [#892](https://github.com/TetronIO/JIM/issues/892) (also closed) |
| ~~91~~ | ~~MV attribute priority (finish)~~ | Attribute flow | **Done** | ~~High~~ | D2 | Merged (engine edges + UI + tests); foundation for [#134](https://github.com/TetronIO/JIM/issues/134)/[#809](https://github.com/TetronIO/JIM/issues/809)/[#399](https://github.com/TetronIO/JIM/issues/399) |
| 116 | ExcludedFromLastConnectorCheck | Deletion rules | Medium | Low | D4 | Fold into one deletion-enhancements branch |
| 118 | Conditional MVO deletion | Deletion rules | High | Medium | D4 | Reuses expression engine; fail-safe semantics decision |
| 119 | Authoritative source hierarchy | Deletion rules | Medium | Medium | D4 | Threshold vocabulary decision if kept |
| ~~126~~ | ~~CSO deletion behaviour options~~ | Deprovisioning | **Closed (duplicate)** | ~~Low~~ | D3 | Folded into outbound sync deprovisioning design; work continues under [#655](https://github.com/TetronIO/JIM/issues/655) |
| 134 | CS deletion attribute impact analysis | Deprovisioning/preview | High | High | D3+D6 | Co-design with [#809](https://github.com/TetronIO/JIM/issues/809); ride [#827](https://github.com/TetronIO/JIM/issues/827) framework |
| ~~154~~ | ~~API endpoint coverage~~ | API/PS | **Done** | ~~Low~~ | D5 | Merged via PR [#897](https://github.com/TetronIO/JIM/pull/897) on 2026-07-02; issue closed |
| 169 | Admin dashboard | Monitoring UI | High | High | UI | Health slice only for v1.0; share chart component with [#453](https://github.com/TetronIO/JIM/issues/453) |
| 186 | PS Gallery ownership transfer | Release ops | High | Low | Ops | User action; before [#841](https://github.com/TetronIO/JIM/issues/841) automation |
| 204 | Scope management enhancements | Sync config safety | Medium | Medium | D6 | Fold into [#827](https://github.com/TetronIO/JIM/issues/827) as an adapter |
| 207 | Matching rule expression operators | Matching | Medium | Medium | Standalone | SQL vs in-process decision; parallel-safe |
| 223 | Export-only attribute flow | Attribute flow | High | High | D2+D3 | After ~~[#91](https://github.com/TetronIO/JIM/issues/91)~~ (done); drift-exemption design |
| 230 | LDAP DC discovery/pinning | LDAP | High | Medium | D10 | Before [#231](https://github.com/TetronIO/JIM/issues/231) |
| 231 | Surface directory capabilities | LDAP | Medium | Medium | D10 | After [#230](https://github.com/TetronIO/JIM/issues/230); design capability contract |
| 242 | Unique value generation | Provisioning | Urgent | High | D2 | PRD first; last in D2 queue |
| 288 | Sync Preview Mode | Preview | Urgent | High | D6 | Engine for [#827](https://github.com/TetronIO/JIM/issues/827); PRD first; coordinate with sync-engine lanes |
| 294 | Integration testing deferred tasks | Testing | High | Medium (rescoped) | D8 | Rescope to GALSYNC scenario; rest superseded |
| 307 | Real-time notifications | Platform | **Low → v1.x** | High | - | Defer to v1.x |
| 348 | MVO detail metadata/connectors | Observability | High | High | D5 tail | Share scope-evaluation with [#288](https://github.com/TetronIO/JIM/issues/288); after ~~[#813](https://github.com/TetronIO/JIM/issues/813)~~ (done) |
| 351 | Container/OU selection | LDAP | High | High | LDAP | Phase 1 only; parallel-safe vs D10 |
| 359 | Configuration migration | Config mgmt | **Low → v1.x** | High | - | Defer; design after [#376](https://github.com/TetronIO/JIM/issues/376)/~~[#377](https://github.com/TetronIO/JIM/issues/377)~~ (done) stabilise |
| 376 | CRUD custom MV Object Types | Schema | Urgent | High | D5 tail | After ~~[#377](https://github.com/TetronIO/JIM/issues/377)~~ (done) |
| ~~377~~ | CRUD custom MV Attributes | Schema | **Done** | ~~Medium~~ | D5 tail | Merged via PR [#1023](https://github.com/TetronIO/JIM/pull/1023) (all 4 phases + docs) |
| 399 | Sync rule causality tracking | Observability | Medium | Medium | D2 | De-duplicate plan vs [#91](https://github.com/TetronIO/JIM/issues/91) provenance first |
| 421 | Schema refresh preview | Safety | Urgent | High | D6 | Do not let [#827](https://github.com/TetronIO/JIM/issues/827) block it; time-box |
| 435 | MVA to SVA import flow | Attribute flow | High | Medium | D2 | After ~~[#91](https://github.com/TetronIO/JIM/issues/91)~~ (done); well-specified |
| 437 | Evaluate sync partition scoping | Design | Medium | Low | Doc | Combined eval with [#438](https://github.com/TetronIO/JIM/issues/438); may close as won't-do |
| ~~438~~ | ~~Evaluate export partition scoping~~ | Design | **Closed (duplicate)** | ~~Low~~ | Doc | Folded into partition-scoping work under [#437](https://github.com/TetronIO/JIM/issues/437) |
| 453 | Live throughput graph | Monitoring UI | Medium | Low | D9 | Raw SVG, no new dependency |
| 454 | Phase stepper | Monitoring UI | Medium | Medium | D9 | Worker phase field; coordinate with [#497](https://github.com/TetronIO/JIM/issues/497)/[#498](https://github.com/TetronIO/JIM/issues/498) owners |
| 464 | External task step types | Scheduler | **Low → v1.x** | High | - | Defer on security-hardening grounds |
| ~~466~~ | ~~PS Log cmdlets~~ | API/PS | **Done** | ~~Low~~ | D5 | Rescoped to `Watch-JIMLog`, runtime-verified, merged via PR [#901](https://github.com/TetronIO/JIM/pull/901) on 2026-07-03 |
| ~~467~~ | ~~Role membership API~~ | API/PS | **Done** | ~~Low~~ | D5 | Verified (endpoints, lockout rules, cmdlets, tests, docs) and closed 2026-07-02 |
| 487 | Pagination safety hardening | API/PS | High | Medium | D5 | After ~~[#154](https://github.com/TetronIO/JIM/issues/154)~~ (done); feeds [#500](https://github.com/TetronIO/JIM/issues/500) |
| 497 | Import parallelism | Perf | Medium | High | D1 | Last in D1; candidate to defer |
| 498 | Sync phase parallelism | Perf | **Low → v1.x** | High | - | Design spike only if benchmarks demand |
| ~~500~~ | ~~OWASP remediation~~ | Security | **Done** | ~~High~~ | D7 + misc | OWASP 2025 gaps remediated (rate limiting, audit trail, CSP, lockfiles); audit trail unified with [#14](https://github.com/TetronIO/JIM/issues/14) |
| 518 | Release gate: integration suite | Release eng | High | High (automated gate) / Low (manual step) | D8 | Manual checklist for GA; automated gate v1.x |
| 519 | Continuous SBOMs | Compliance | Medium | Low | D8 | Parallel-safe |
| 582 | Screenshot automation | Docs tooling | **Low → v1.x** | Medium | - | Manual refresh for v1.0 |
| 636 | Parallel integration tests | Testing | Medium | High | D8 | Phases 1-3 only; Phase 4 blocked on runners |
| 655 | Deletion cascade Provisioned-only | Deprovisioning | Urgent | Medium | D3 | Merge ~~[#126](https://github.com/TetronIO/JIM/issues/126)~~ (closed dup) in; first D3 decision |
| 809 | CS deletion sync deprovisioning | Deprovisioning | High | High (design) | D3 | Co-design with [#134](https://github.com/TetronIO/JIM/issues/134); execute post-GA if needed |
| ~~813~~ | ~~API response normalisation~~ | API/PS | **Done** | ~~Medium~~ | D5 | Merged via PR [#980](https://github.com/TetronIO/JIM/pull/980); spun out [#979](https://github.com/TetronIO/JIM/issues/979) (also done) |
| 827 | Config change preview framework | Preview | High | High (design) | D6 | Time-boxed design; gates [#204](https://github.com/TetronIO/JIM/issues/204)/[#421](https://github.com/TetronIO/JIM/issues/421)/[#134](https://github.com/TetronIO/JIM/issues/134)/[#91](https://github.com/TetronIO/JIM/issues/91)-mode2 |
| 841 | Automated preview releases | Release eng | Medium | Medium | D8 | Build as [#518](https://github.com/TetronIO/JIM/issues/518)'s phase 1 |
| ~~861~~ | ~~DB-backed test tier in CI~~ | Testing | **Done** | ~~Low~~ | D8 | Real-PostgreSQL CI tier landed; protects everything |
| 864 | Search-as-you-type | UI polish | High | Low | D9 last | Sweep after other UI work |
| 872 | SVA update in place | Perf | Medium | Medium | D1 | First in D1 (stable persistence before parallelism) |
| 873 | Tolerate unresolved references | Robustness | High | Medium | D1 | Warn semantics decision |
| 874 | Sync error object snapshots | Supportability | High | Medium | D1 | Interlocks with [#881](https://github.com/TetronIO/JIM/issues/881) on sensitive data |
| 877 | BoolTrueDistribution | Demo data | **Low → v1.x** | Low | - | Opportunistic |
| 880 | Export re-evaluation optimisation | Perf | **Low → v1.x** | Medium | - | Only if profiling justifies |
| 881 | Sensitive value access control | Audit/security | High | Medium | D7 | After ~~[#14](https://github.com/TetronIO/JIM/issues/14)~~ (done) and [#9](https://github.com/TetronIO/JIM/issues/9) |

---

*This analysis was produced from the full issue bodies as of 2026-07-02, cross-referenced against the current codebase. Effort ratings use the GitHub Effort field vocabulary and assume agent-driven implementation with human review. Re-validate "appears done" items before closing them.*
