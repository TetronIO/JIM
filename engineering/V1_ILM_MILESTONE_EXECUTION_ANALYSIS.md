# v1.0-ILM-COMPLETE Milestone: Execution Analysis

- **Date:** 2026-07-02
- **Scope:** All 57 open issues on the `v1.0-ILM-COMPLETE` milestone
- **Purpose:** Categorise the milestone by theme, prioritise for fastest route to a polished, production-safe v1.0, and define an execution plan (parallel lanes, serialisation constraints, design decisions needed) suitable for multi-agent delivery.

---

## 1. Headline Findings

1. **The milestone is over-scoped for a "fastest to market" goal.** 57 open issues include seven XL items (each needing a PRD and phased plan). Recommendation: demote roughly 7-10 issues to v1.x explicitly (see Section 3, tier P3). Cutting scope is the single biggest velocity lever available.
2. **Several issues are already done or nearly done.** #154 (API coverage) is complete on branch `claude/gh-154-powershell-coverage-a67jff` awaiting merge; #467 (role membership API) appears implemented in the codebase; #14 (change history) is largely delivered and needs a gap audit; #466 is partially superseded by the #154 branch; #126 duplicates #655; #294 double-tracks work owned by #861/#636/#518/#841. A hygiene pass closing these is nearly-free throughput.
3. **The scarce resource is not agents; it is the sync engine.** Three of the largest clusters (import processor, attribute flow, deprovisioning) all modify the same handful of files (`SyncImportTaskProcessor.cs`, `SyncEngine.AttributeFlow.cs`, `SyncTaskProcessorBase.cs`, `ExportEvaluationServer.cs`). Parallelising across those clusters guarantees merge conflicts and re-validation churn. Treat "sync engine" as one work lane with an ordered queue; everything else genuinely parallelises.
4. **Two launch-blocking capability gaps stand out:** #242 (unique value generation; without it a greenfield customer cannot provision accounts unless HR supplies IT identifiers) and #655 (leaver deprovisioning silently fails for Joined/Projected CSOs, i.e. the extremely common join-existing-accounts deployment). Both undermine the "ILM complete" claim directly.
5. **One data-loss landmine:** #421 (Refresh Schema applies destructive cascade deletes in one click). Highest-stakes safety item in the milestone.
6. **A design gate exists:** #827 (configuration change preview framework) explicitly gates #204, #421, #134 and #91's impact-analysis mode, and #288 (Sync Preview Mode) is the evaluation engine underneath it. The preview family must be designed once, early, or the per-surface previews will be built twice.

> **Caveat on "appears done" claims:** per project rules, sub-agent findings describe intent, not verified behaviour. Items marked "verify then close" (#467, #14 gaps, #466 overlap) need a human-or-agent verification pass against actual code/branches before closure.

---

## 2. Thematic Categorisation

| Theme | Issues | Count |
|---|---|---|
| **Deprovisioning and deletion semantics** | #655, #126 (dup), #809, #134, #116, #118, #119 | 7 |
| **Attribute flow and sync semantics** | #91, #435, #223, #242, #399, #207, #204 | 7 |
| **Sync engine robustness and performance** | #872, #873, #874, #497, #498, #880, #437, #438 | 8 |
| **Preview / what-if / impact analysis** | #827, #288, #421 (also #134, #204 above) | 3 (+2 shared) |
| **Metaverse schema and config management** | #377, #376, #348, #359, #85, #813 | 6 |
| **API / PowerShell surface** | #154, #467, #466, #487, #186 | 5 |
| **LDAP connector** | #230, #231, #351 | 3 |
| **Security, audit, RBAC** | #9, #14, #881, #500, #464 | 5 |
| **Operational monitoring / admin UI** | #169, #453, #454, #864, #307 | 5 |
| **Testing and release engineering** | #861, #636, #518, #519, #841, #294, #582, #877 | 8 |

---

## 3. Priority Tiers

Priority is judged against one question: *what does an ILM customer in a high-trust environment need to run production synchronisation safely at first adoption?* Correctness and safety outrank performance and polish; performance outranks convenience.

### P0 - Must land before GA (13)

| Issue | Why it blocks launch | Effort |
|---|---|---|
| #91 MV attribute priority (finish) | Half-shipped: engine enforces priorities but no UI to view/reorder them. Non-deterministic multi-source resolution is data corruption by another name. | L (remainder) |
| #655 MVO deletion cascade: Provisioned-only delete exports | Leaver deprovisioning fails for Joined/Projected CSOs; breaks the core JML guarantee in the most common deployment shape. Merge #126 into it. | M |
| #242 Unique value generation | Table-stakes provisioning (sAMAccountName, UPN, mail). Without it greenfield provisioning is impossible. Needs PRD first. | XL |
| #421 Schema refresh preview phase | One click can cascade-delete Synchronisation Rules and objects. Unacceptable in target sectors. | L |
| #288 Sync Preview Mode (what-if) | Admins will not run a first sync against production AD blind; also the engine the whole preview family builds on. Needs PRD. | XL |
| #377 CRUD custom Metaverse Attributes | No real customer fits built-in attributes only. API/cmdlets largely exist; Web UI + safeguards remain. | M |
| #376 CRUD custom Metaverse Object Types | Modelling beyond User/Group requires it; unlocks per-type deletion rules. Depends on #377. | L |
| #154 API Endpoint Coverage (merge branch) | Work is complete on a waiting branch; gates the whole API/PowerShell lane. | S |
| #467 Role membership API (verify + close) | Appears already implemented; verify safety rules then close. Baseline operational requirement. | S |
| #14 Change history (gap audit + close) | Audit history is table stakes; mostly delivered. Verify retention scheduling and the five stated requirements. | S-M |
| #9 Synchronisation Readers role | "Give the helpdesk read-only access" is a first-week ask; everyone-as-Administrator is unacceptable. | M |
| #500 OWASP remediation | Rate limiting and privileged-operations audit trail are what a regulated buyer's security review checks first. Splittable; other items are cheap. | L (splittable) |
| #861 DB-backed test tier in CI | Nearly-free regression protection for sync/persistence correctness; protects every other lane. Do first. | S |

### P1 - Should (strong pull, ship if capacity) (20)

#435 (MVA to SVA import flow; every RFC-compliant LDAP directory hits it), #118 (conditional MVO deletion; "never delete an active employee" safety net), #223 (export-only attribute flow; standard initial-password pattern), #873 (tolerate unresolved references; stops error-channel noise training operators to ignore errors), #874 (sync error object snapshots; core supportability), #351 Phase 1 (container/OU scoping; real directories always need exclusions), #230 (LDAP DC pinning; replication lag masquerades as sync bugs), #881 (sensitive attribute value gating in history), #487 (API pagination/rate hardening), #813 (API contract normalisation; **v1.0 is the last free window to break the contract**), #169 (admin dashboard, health slice only), #348 (MVO detail "why not provisioned"; top helpdesk escalation), #85 (relative-date search periods; note the re-evaluation problem is tracked separately in #892, outside this milestone), #827 (preview framework **design**; gates four other issues), #134 + #809 (Connected System deletion preview/execute pair; co-design), #864 (search-as-you-type; cheap credibility polish), #186 (PowerShell Gallery ownership; supply-chain optics), #294 (rescope to the GALSYNC CSV-export scenario only), #518 (for GA: a documented manual "full integration suite green" checklist step; the automated gate is v1.x).

### P2 - Nice (opportunistic; do not schedule ahead of P0/P1) (16)

#116, #119 (deletion rule refinements), #207 (matching rule operators), #204 (scope-change warnings; fold into #827), #872 (SVA update in place), #497 (import parallelism), #399 (causality tracking; #91 already shipped the highest-value piece), #453, #454 (Activity detail visualisations), #466 (remainder: `Watch-JIMLog` only), #519 (continuous SBOMs), #841 (preview releases), #636 Phases 1-3 (parallel integration tests), #231 (surface LDAP capabilities), #437, #438 (partition scoping **evaluations**; cheap design docs, and "won't do" is a valid outcome that deletes future scope; do the write-up early).

### P3 - Recommend demoting to v1.x (7)

| Issue | Rationale |
|---|---|
| #307 Real-time notifications | Rewires inter-service comms at exactly the wrong moment; polling works. |
| #359 Configuration migration | XL, everything open; DB-backup-based config backup is a documented workaround. Design after #376/#377 stabilise the config surface anyway. |
| #464 External task step types | Arbitrary code execution shipped half-hardened into healthcare/government is worse than not shipping it. Defer unless a full security pass fits. |
| #498 Sync phase parallelism | Slow-and-correct is shippable; corrupt-and-fast is not. Design spike only if benchmarks demand it. |
| #880 Export expression re-evaluation optimisation | Issue itself says pursue only if profiling proves the cost; failure mode is wrong export values. |
| #582 Screenshot automation | Manual refresh for v1.0 docs; automate after UI churn settles. |
| #877 Example data boolean distribution | Demo realism only; do opportunistically alongside #582. |

Also v1.x: #438 implementation (evaluation may close it entirely), #636 Phase 4 (blocked on self-hosted runners), #294 scenarios 6-7 (blocked on Internal MVO design, #614/#600, not on this milestone).

---

## 4. Serialisation Constraints (file-collision domains)

These are the hard "do not parallelise" rules. Each domain is a set of issues that modify the same files; within a domain, work must be sequenced (or combined into one branch).

| Domain | Files | Ordered queue |
|---|---|---|
| **D1: Import processor** | `SyncImportTaskProcessor.cs` | #872 → #873 → #874 → (#497 last, if kept: parallelise only after semantics settle) |
| **D2: Attribute flow engine** | `SyncEngine.AttributeFlow.cs`, `SyncTaskProcessorBase.cs`, `ISyncEngine` | #91 (finish engine edges) → #435 → #223 → #242. #399 slots in any time after #91. #873 also brushes this surface; coordinate D1/D2 merges. |
| **D3: Deprovisioning / export evaluation** | `ExportEvaluationServer.cs`, obsoletion processors | #655 (decision + implementation, with #126 merged in) → then #809 + #134 as one co-designed preview/execute pair. #223 and #880 also touch export evaluation; sequence around them. |
| **D4: MVO Deletion Rule config** | `MetaverseObjectType` model, deletion evaluator, object type admin page | #116 → #118 → #119, or one combined "deletion rule enhancements" branch. Only #118 is P1; #116/#119 can slip. |
| **D5: API controllers + PowerShell module** | `MetaverseController`, `SecurityController`, PS module | #154 merge → #467 verify/close → #466 rescope → #813 (normalise contract **before** new surfaces are added) → #487 → then #377/#376 API gap-fill |
| **D6: Preview family** | New preview framework + sync engine read paths | #827 design → #288 engine → adapters (#421, #204, #134, #91 mode 2). See tension note below. |
| **D7: Audit / security** | `ChangeHistoryServer`, history UI, roles/policies | #14 gap-close → #9 → #881. #500's audit-trail item must be designed against #14 (one audit system, not two); its other items are independent. |
| **D8: Integration runner + CI workflows** | `Run-IntegrationTests.ps1`, `ci.yml`, `release.yml` | #861 → #636 (P1-3) → #841 → #518. #294's scenario work serialises with #636. #519 anytime (trivial `ci.yml` merge overlap with #861/#500 lockfiles). |
| **D9: Activity detail page** | `ActivityDetail.razor` | #453 → #454 → #864 sweep last |
| **D10: LDAP connector connection path** | `LdapConnector*`, persisted connector data | #230 → #231. #351 touches different files (containers/partitions) and is parallel-safe. |

**The #421 vs #827 tension, resolved:** #421 is a P0 safety fix; #827 is a P1 design framework that claims to gate it. Do not let the framework block the safety fix. Time-box the #827 design (it is a design document, not a build); build #421 as the framework's first adapter if the design lands in time, otherwise ship #421 standalone behind a minimal interface seam and retrofit. Safety beats architectural purity here.

**Cross-domain rule:** D1, D2, D3 (and #288's engine work in D6) all live in the sync engine. Cap sync-engine work-in-progress at **two concurrent branches maximum**, and prefer one. All other domains are genuinely independent of each other.

---

## 5. Execution Plan: Lanes and Waves

Ten parallel lanes, one agent (or agent session) per lane, serialised within each lane per Section 4. Suggested waves:

### Wave 0 - Hygiene and safety net (days, mostly S items)

- Merge the #154 branch; verify and close #467; gap-audit #14; reconcile #466 naming against the #154 branch.
- Merge #126 into #655 (one design thread); rescope #294 to the GALSYNC scenario and fold its CI/release bullets into #518/#841.
- Land #861 (CI PostgreSQL tier) before any sync-engine work starts.
- Write the #437 + #438 partition-scoping evaluation as one document (pure analysis; `PartitionId` already exists on `ConnectedSystemObject`); expected outcome may close both.
- Quick wins: #864 search sweep, #877 if convenient.
- **Ratify the P3 scope cut and answer the Wave 1 design decisions (Section 6).**

### Wave 1 - Launch blockers, maximum parallel spread

| Lane | Work |
|---|---|
| Sync engine (D2/D3) | Finish #91 engine edges + UI; #655 decision + implementation |
| Preview (D6) | #827 design PRD (time-boxed); #421 build begins as first adapter or standalone |
| Design/PRD lane | PRDs in parallel: #242, #288, #809+#134 pair (PRDs do not collide with code) |
| API (D5) | #813 contract normalisation → #487 |
| Schema (part of D5 tail) | #377 Web UI + safeguards |
| Security (D7) | #9 Readers role; #500 rate limiting, CSP, lockfiles |
| LDAP (D10) | #230 DC pinning |
| UI (D9) | #453 → #454 |
| Ops | #186 (user action + small workflow); #519 |

### Wave 2 - Core capability build-out

Sync engine: #435 → #873/#872 (D1); Preview: #288 engine build; #242 implementation once PRD approved; Schema: #376; Security: #881, #500 audit trail (unified with #14); LDAP: #351 Phase 1, #231; UI: #169 health-slice dashboard; Release: #636 Phases 1-3.

### Wave 3 - Polish and completion

#223, #874, #118 (plus #116/#119 if retained), #134 implementation, #348, #85, #204 (as #827 adapter), #466 remainder, #841, #518 as a documented manual release-gate checklist, #294 GALSYNC scenario, #399 opportunistically.

### Explicitly not scheduled (v1.x)

#307, #359, #464, #497/#498 (unless benchmarks force it), #880, #582, #877 (unless done in Wave 0), #438 implementation.

---

## 6. Decisions Needed From the Product Owner

Batched so they can be answered in one sitting. Wave 1 cannot fully start without the first group.

**Blocking Wave 1:**
1. **Scope cut:** ratify (or amend) the P3 demotions in Section 3.
2. **#655:** per-Synchronisation-Rule `DeprovisionScope` setting vs honouring `OutboundDeprovisionAction` unconditionally; is a confirmation/dry-run safety net required for v1.0?
3. **#242:** expression function vs dedicated generator; uniqueness scope (Metaverse vs Connected System vs live target query); collision suffix strategy; intra-batch collision handling.
4. **#827:** where preview computation runs (worker job vs synchronous); how unsaved proposed config is represented; do preview results persist as Activities; which preview tiers are v1.0-mandatory.
5. **#288:** approach to guaranteeing zero side effects (reuse real sync code paths read-only vs shadow evaluation); sampling strategy at 100K+ objects; UI-only for v1.0 or API/PowerShell too.
6. **#421:** recompute-diff-on-apply vs holding the previewed diff; stale-diff behaviour.
7. **#9:** exactly what a Synchronisation Reader can see (Activities, CSO/MVO detail, Pending Exports; presumably not connector credentials) - interlocks with decision 8.
8. **#881:** "sensitive" as a per-attribute schema flag vs fixed list; which role gates it; read-time redaction (recommended) vs write-time.

**Needed before their respective builds:**
9. **#500:** is the privileged-operations audit trail an extension of #14's change history (recommended: one audit system) or separate; CSP strictness compatible with Blazor Server (nonce vs unsafe-inline).
10. **#874:** snapshot setting default on or off (personal data exposure vs supportability); retention/size limits.
11. **#873:** what "warn" means in the RPEI/Activity model; do ignored unresolved references still count in batch statistics.
12. **#85:** accept that relative-date scopes only re-evaluate on full synchronisation for v1.0 (documented), with the scheduled reconciler tracked in #892 for v1.x?
13. **#813:** adopt the nested `type: {id, name}` shape (issue's own recommendation)?
14. **#351:** confirm Phase 1 (OneLevel per container) only for v1.0.
15. **#118:** confirm fail-safe semantics (condition unevaluable = do not delete).
16. **#518:** accept a documented manual "full integration suite green against the release commit" checklist step for the v1.0 release, automation in v1.x.

**Personal actions only you can do:**
17. **#154:** review and merge branch `claude/gh-154-powershell-coverage-a67jff`.
18. **#186:** create the Tetron service account and PowerShell Gallery co-owner invite; provide the API key for CI secrets.

---

## 7. Process Recommendations for Multi-Agent Throughput

1. **Verification sweep before building.** Cheapest wins in the milestone are closures, not code. Run a small agent pass that verifies "appears done" claims (#467, #14, #466) against actual code and closes or re-scopes issues. Do this before scheduling any implementation agents at those surfaces.
2. **Schedule by collision domain, not by theme.** Themes are for humans; the file-collision domains in Section 4 are the scheduling primitive for agents. One branch per issue, one active branch per domain (two max for the sync engine overall), merge to `main` quickly and often. Long-lived parallel branches plus squash merges is the worst combination.
3. **PRDs fan out freely.** Design documents never collide in code. All Wave 1 PRDs (#242, #288, #827, #809+#134) can be drafted by parallel agents immediately; each PRD should end with its own "decisions needed" list feeding the Section 6 batch. Review them as a batch, not one by one.
4. **Batch product decisions.** Agents stall on small owner decisions far more than on hard engineering. Keep Section 6 as a living decision log; answer in batches; agents record the answer in the relevant issue so the next session does not re-ask.
5. **Safety net scales with parallelism.** More concurrent agents means more regression surface. #861 (real-PostgreSQL CI tier) lands first, and any sync-engine branch runs the relevant integration scenarios before merge, per existing project rules.
6. **Label the lanes in GitHub.** Add `lane:*` labels (or a Project field) matching Section 5 so any session can see at a glance which lanes are occupied. Parallel chat sessions tracked by branch (the existing convention) then map cleanly onto lanes.
7. **Consolidate before implementing.** Duplicate/overlapping issue pairs (#655+#126, #294 vs #518/#841/#636, #466 vs #154, #204 vs #827) must be merged or re-scoped *before* an agent picks them up, or two agents will build the same thing differently.
8. **Sequence UI sweeps last within a page.** Mechanical sweeps (#864) go after feature work on the same pages (#453/#454/#169) to avoid trivial-but-constant conflicts.

---

## 8. Full Issue Index

| # | Title (abbreviated) | Theme | Tier | Effort | Domain/Lane | Key dependency notes |
|---|---|---|---|---|---|---|
| 9 | Synchronisation Readers role | Security/RBAC | P0 | M | D7 | Before #881; collides with #154/#467 controllers - after D5 merge |
| 14 | Change history config + business data | Audit | P0 | S-M | D7 | Mostly done; gap audit; feeds #881, #500 audit trail |
| 85 | Time periods on searches | Schema/search | P1 | M | Schema | Re-evaluation problem tracked in #892 (outside milestone) |
| 91 | MV attribute priority (finish) | Attribute flow | P0 | L | D2 | Engine edges + 3 UI surfaces + test matrix; foundation for #134/#809/#399 |
| 116 | ExcludedFromLastConnectorCheck | Deletion rules | P2 | S | D4 | Fold into one deletion-enhancements branch |
| 118 | Conditional MVO deletion | Deletion rules | P1 | M | D4 | Reuses expression engine; fail-safe semantics decision |
| 119 | Authoritative source hierarchy | Deletion rules | P2 | M | D4 | Threshold vocabulary decision if kept |
| 126 | CSO deletion behaviour options | Deprovisioning | Merge | S | D3 | Merge into #655 |
| 134 | CS deletion attribute impact analysis | Deprovisioning/preview | P1 | L | D3+D6 | Co-design with #809; ride #827 framework |
| 154 | API endpoint coverage | API/PS | P0 | S | D5 | Merge waiting branch first; gates lane |
| 169 | Admin dashboard | Monitoring UI | P1 | L | UI | Health slice only for v1.0; share chart component with #453 |
| 186 | PS Gallery ownership transfer | Release ops | P1 | S | Ops | User action; before #841 automation |
| 204 | Scope management enhancements | Sync config safety | P2 | M | D6 | Fold into #827 as an adapter |
| 207 | Matching rule expression operators | Matching | P2 | M | Standalone | SQL vs in-process decision; parallel-safe |
| 223 | Export-only attribute flow | Attribute flow | P1 | L | D2+D3 | After #91; drift-exemption design |
| 230 | LDAP DC discovery/pinning | LDAP | P1 | M | D10 | Before #231 |
| 231 | Surface directory capabilities | LDAP | P2 | M | D10 | After #230; design capability contract |
| 242 | Unique value generation | Provisioning | P0 | XL | D2 | PRD first; last in D2 queue |
| 288 | Sync Preview Mode | Preview | P0 | XL | D6 | Engine for #827; PRD first; coordinate with sync-engine lanes |
| 294 | Integration testing deferred tasks | Testing | P1 | M (rescoped) | D8 | Rescope to GALSYNC scenario; rest superseded |
| 307 | Real-time notifications | Platform | **P3** | XL | - | Defer to v1.x |
| 348 | MVO detail metadata/connectors | Observability | P1 | L | D5 tail | Share scope-evaluation with #288; after #813 |
| 351 | Container/OU selection | LDAP | P1 | L | LDAP | Phase 1 only; parallel-safe vs D10 |
| 359 | Configuration migration | Config mgmt | **P3** | XL | - | Defer; design after #376/#377 stabilise |
| 376 | CRUD custom MV Object Types | Schema | P0 | L | D5 tail | After #377 |
| 377 | CRUD custom MV Attributes | Schema | P0 | M | D5 tail | API/cmdlets largely exist; UI + safeguards |
| 399 | Sync rule causality tracking | Observability | P2 | M | D2 | De-duplicate plan vs #91 provenance first |
| 421 | Schema refresh preview | Safety | P0 | L | D6 | Do not let #827 block it; time-box |
| 435 | MVA to SVA import flow | Attribute flow | P1 | M | D2 | After #91; well-specified |
| 437 | Evaluate sync partition scoping | Design | P2 | S | Doc | Combined eval with #438; may close as won't-do |
| 438 | Evaluate export partition scoping | Design | P2 | S | Doc | Cross-partition reference risk; likely defer impl |
| 453 | Live throughput graph | Monitoring UI | P2 | S | D9 | Raw SVG, no new dependency |
| 454 | Phase stepper | Monitoring UI | P2 | M | D9 | Worker phase field; coordinate with #497/#498 owners |
| 464 | External task step types | Scheduler | **P3** | XL | - | Defer on security-hardening grounds |
| 466 | PS Log cmdlets | API/PS | P2 | S | D5 | Rescope to Watch-JIMLog; reconcile naming with #154 |
| 467 | Role membership API | API/PS | P0 | S | D5 | Verify then close |
| 487 | Pagination safety hardening | API/PS | P1 | M | D5 | After #154; feeds #500 |
| 497 | Import parallelism | Perf | P2 | M-L | D1 | Last in D1; candidate to defer |
| 498 | Sync phase parallelism | Perf | **P3** | XL | - | Design spike only if benchmarks demand |
| 500 | OWASP remediation | Security | P0 | L | D7 + misc | Splittable; audit trail unified with #14 |
| 518 | Release gate: integration suite | Release eng | P1 | XL (auto) / S (manual) | D8 | Manual checklist for GA; automated gate v1.x |
| 519 | Continuous SBOMs | Compliance | P2 | S | D8 | Parallel-safe |
| 582 | Screenshot automation | Docs tooling | **P3** | M | - | Manual refresh for v1.0 |
| 636 | Parallel integration tests | Testing | P2 | XL | D8 | Phases 1-3 only; Phase 4 blocked on runners |
| 655 | Deletion cascade Provisioned-only | Deprovisioning | P0 | M | D3 | Merge #126 in; first D3 decision |
| 809 | CS deletion sync deprovisioning | Deprovisioning | P1 | XL (design) | D3 | Co-design with #134; execute post-GA if needed |
| 813 | API response normalisation | API/PS | P1 | M | D5 | Last free window before customers build on the contract |
| 827 | Config change preview framework | Preview | P1 | XL (design) | D6 | Time-boxed design; gates #204/#421/#134/#91-mode2 |
| 841 | Automated preview releases | Release eng | P2 | M | D8 | Build as #518's phase 1 |
| 861 | DB-backed test tier in CI | Testing | P0 | S | D8 | First; protects everything |
| 864 | Search-as-you-type | UI polish | P1 | S | D9 last | Sweep after other UI work |
| 872 | SVA update in place | Perf | P2 | M | D1 | First in D1 (stable persistence before parallelism) |
| 873 | Tolerate unresolved references | Robustness | P1 | S-M | D1 | Warn semantics decision |
| 874 | Sync error object snapshots | Supportability | P1 | M | D1 | Interlocks with #881 on sensitive data |
| 877 | BoolTrueDistribution | Demo data | **P3** | S | - | Opportunistic |
| 880 | Export re-evaluation optimisation | Perf | **P3** | M | - | Only if profiling justifies |
| 881 | Sensitive value access control | Audit/security | P1 | M | D7 | After #14 and #9 |

---

*This analysis was produced from the full issue bodies as of 2026-07-02, cross-referenced against the current codebase. Effort ratings assume agent-driven implementation with human review. Re-validate "appears done" items before closing them.*
