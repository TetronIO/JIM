# Metaverse Object Connection Explanations

- **Status:** Doing (Phases 0-3 and layers 1-2 complete)
- **Created:** 2026-10-05
- **Author:** Jay
- **Issue:** [#348](https://github.com/TetronIO/JIM/issues/348)
- **Plan:** [`../../plans/doing/METAVERSE_OBJECT_CONNECTION_EXPLANATIONS.md`](../../plans/doing/METAVERSE_OBJECT_CONNECTION_EXPLANATIONS.md)
- **UI mockups:** [MVO Connections Mocks](https://claude.ai/artifact/4Tj5DYpEMR7c8g9pSoAqD9) (board 1B is the chosen layout; 1A is the rejected alternative)

## Problem Statement

The question administrators are asked most often about an identity is "why hasn't this person been provisioned to X?", closely followed by "why do they have an account in Y?". JIM cannot answer either from the Metaverse Object's own page today.

The Connections tab (#1519) lists the Connected System Objects joined to a Metaverse Object with their role, join type and State. It says *that* an object is joined, but not *why*: the Synchronisation Rule that projected or provisioned it is only discoverable by finding the right entry on the Changes tab, and nothing shows whether the object is still in scope of the rules that put it there. It says nothing at all about the Connected Systems the object is **not** connected to.

Underneath, scoping evaluation is a bare true/false (`ScopingEvaluationServer.IsMvoInScopeForExportRule` and `IsCsoInScopeForImportRule`), so no surface can say which criterion failed. The export preview drops exactly the case this PRD is about: an export rule the object is out of scope of, with no joined Connected System Object, produces no entry and no reason.

So an administrator answering a helpdesk ticket has to open each export Synchronisation Rule in turn, read its scoping criteria, and compare them by hand against the person's attribute values, remembering that a missing value fails almost every comparison and that top-level criteria groups are ORed. That is slow, error-prone, and impossible to hand to a colleague.

## Goals

- From a Metaverse Object's Connections tab, an administrator can see, for every Connected System that has an enabled export Synchronisation Rule for the object's type, whether the object is connected and, if not, why, without opening any other page.
- For a "not in scope" answer, the administrator can see exactly which criteria fail, the expected value and the person's current value, including "no value".
- For each joined connection, the administrator can see how the join came about (the Synchronisation Rule, date and Activity) and whether the object is still in scope of the relevant rules.
- An administrator can copy a plain-text summary of why a person is not connected and paste it into a message to a colleague or the helpdesk.
- The explanation can never disagree with what synchronisation actually does: it is produced by the same evaluation code path, proven by tests across every operator, value type and group shape.
- Every capability above is available through the REST API and PowerShell with the same content.
- How a join came about is recorded durably from now on, so the answer does not age out with Activity history.

## Non-Goals

- **Historical re-evaluation.** Criteria are evaluated against the object's current values. "Why was this person projected last March" is answered by the recorded Synchronisation Rule and Activity, not by reconstructing past attribute values.
- **Explaining missing joins to source systems.** A Connected System with only import Synchronisation Rules and no joined object cannot be explained from the Metaverse Object's side; that is a join-rule question about objects in another Connector Space. Such systems are not listed.
- **Changing scoping semantics.** This PRD explains the evaluator as it behaves; it does not change how any criterion evaluates. In particular, the evaluator's handling of multi-valued attributes (only the first value is compared) is out of scope and tracked in [#1923](https://github.com/TetronIO/JIM/issues/1923).
- **Explaining Attribute Flow or export values.** What would be exported is already answered by Preview Sync and Preview Exports.
- **Remediation actions.** No "fix it" buttons; the page explains, it does not edit rules or attribute values.
- **Persisting explanations.** Explanations are computed on request and never stored.
- **Changing who can see existing views.** The Inspect view, the Changes tab and the Activity pages keep their current access. Who may see which systems hold or contribute data about a person is re-evaluated as part of the RBAC work. The new explanations live on the Connections tab, which is already Administrator-only.

## User Stories

1. As a helpdesk-facing administrator, I want to open a person's Metaverse Object and immediately see which systems they are not provisioned to and why, so that I can answer "why doesn't Jane have a Finance App account?" in seconds rather than by reading Synchronisation Rules.
2. As an administrator handing a ticket to a colleague, I want to copy a plain-text summary of why a person is out of scope, so that I can paste it into a message or ticket without retyping or screenshotting.
3. As an identity architect troubleshooting a rule, I want to see the full criteria tree with the person's current value against each criterion, so that I can tell a data problem (missing Cost Centre) from a rule problem (wrong comparison).
4. As an administrator investigating an existing account, I want to see which Synchronisation Rule projected or provisioned it, when, and whether the person is still in scope, so that I can explain why the account exists and whether it is about to be deprovisioned.
5. As an administrator who scripts JIM, I want the same explanations from PowerShell and the REST API, so that I can report on out-of-scope people in bulk or build them into my own tooling.

## Requirements

### Functional Requirements

#### A. Explained scoping evaluation

1. JIM must be able to evaluate a Synchronisation Rule's scoping criteria against an object and return an explanation tree that mirrors the rule exactly:
   - The rule level, whose top-level criteria groups are ORed (the object is in scope if any top-level group is met).
   - Each group, with its type (`All` or `Any`), its outcome, and the number of its children met.
   - Each criterion, with the attribute name, comparison, expected value, case sensitivity (text), the actual value evaluated, and an outcome.

   Where a rule has a single top-level group (the common case), surfaces present that group directly; where it has several, they are presented as "any one of these groups".
2. Criterion outcomes must distinguish: **Met**, **Not met**, **No value** (the attribute has no value, which fails every comparison except an `Equals` criterion with an empty expected value), **Attribute missing** (the criterion references no attribute, for example after an attribute was deleted), and **Invalid** (the operator is not valid for the attribute's type).
3. Relative date criteria must show both the relative expression (for example "on or before today") and the instant it resolved to; all relative criteria in one evaluation must resolve against the same instant, as they do in synchronisation.
4. A rule with no scoping criteria must be reported as "in scope: no scoping criteria".
5. The boolean scoping methods used by synchronisation must return exactly the explanation's outcome for every input. They must share one evaluation code path, so that a future change to evaluation changes both together; tests must prove agreement across every operator, value type, group shape, nesting depth, missing value and relative date.
6. Synchronisation's behaviour on an **Invalid** criterion is unchanged (it fails loudly). The explanation must instead report the criterion as Invalid and the rule's outcome as undetermined, so that one misconfigured rule does not prevent the rest of the page from loading.
7. Where an attribute holds more values than the evaluator compares, the explanation must show the value that was actually compared and flag that further values were not evaluated.
8. Values of credential attributes must never appear in an explanation, summary, hint or copied text; such values are masked.

#### B. Joined connections: why connected

9. Each row of the Connections tab's joined table must expand to show:
   - **How it joined**: the join type, the Synchronisation Rule that projected, provisioned or joined it, the date joined, and a link to the Activity where history still holds it. The joining rule is recorded on the Connected System Object at the moment of joining (requirement 28); for objects joined before that, it is derived from Activity history where possible. Where neither source has a value, the expansion must say "not recorded" rather than guess.
   - **Scoping, evaluated now**: the explanation (requirement 1) for each enabled Synchronisation Rule of the connection's Connected System and object type that is relevant to its role (import rules evaluated against the Connected System Object's values for a source, export rules evaluated against the Metaverse Object's values for a target).
10. The scoping section must be labelled as evaluated now, against current values, with the evaluation time, so it is not mistaken for the reason at the time of joining.
11. A joined connection that is now out of scope of a relevant rule (for example an import rule set to remain joined, or an export rule whose deprovisioning is pending) must show that clearly, with the failing criteria.
12. Where another enabled export rule targets the same Connected System for a different object type, the joined row's expansion must say that rule cannot connect because the Metaverse Object's single slot in that Connected System is already held by an object of another type (an **Object Type conflict**). This appears here, not under Not connected, because the Connected System does hold a joined object.
13. A connection that is still being provisioned (its State is Pending export or Awaiting confirmation) is already a joined row; it must not also appear under Not connected.
14. The joined table's existing columns, State chips and Preview Sync action are unchanged.

#### C. Not connected

15. Below the joined table, the Connections tab must show a separate, collapsible **Not connected (n)** section, collapsed by default and visually low-lit, listing one entry per enabled export Synchronisation Rule for the Metaverse Object's type whose Connected System holds no Connected System Object joined to this Metaverse Object. Rules whose Connected System is being deleted are excluded; rules whose Connected System is disabled are included, with the hint saying so.
16. Each entry must carry exactly one reason:
    - **Not in scope**: the object fails the rule's scoping criteria.
    - **Rule doesn't provision**: the object is in scope, but the rule does not provision new objects (`ProvisionToConnectedSystem` is off or unset), and no object exists to join.
    - **Rule misconfigured**: the rule's scoping cannot be evaluated because a criterion is Invalid (requirement 6); the expansion names the criterion.
    - **Not yet provisioned**: the object is in scope and the rule provisions, but nothing has been staged. Synchronisation evaluates provisioning only when an object's attribute values change, so this is the state of every already-in-scope object after an export rule is created, has provisioning switched on, or has its criteria widened. The expansion must say what will and will not trigger provisioning, accurately for the engine as it then behaves (see Resolved Decisions).

    Provisioning that is already under way, and Object Type conflicts, always involve a joined object, so they appear on the joined rows (requirements 12 and 13), never here.
17. Each entry's row must show the Connected System, the Synchronisation Rule, the reason chip followed by a one-line hint, and a **Copy summary** action. The hint names the failing attributes without their values (for example "Fails on Department; Cost Centre or Job Title") or briefly qualifies a non-scoping reason ("In scope; nothing staged yet"). The hint must not wrap; overflow is truncated with an ellipsis.
18. Expanding an entry must show:
    - For **Not in scope**: the "To come into scope" bullet list (requirement 21) above the explanation tree.
    - For the other reasons: a short list of what happens next or what would connect the object.

    The bullets appear only in the expansion, never in the row.
19. If there are no not-connected entries, the section must say so rather than disappear, so that "connected everywhere it could be" is a visible answer.

#### D. Summaries and hints

20. The hint, the "To come into scope" bullets and the plain-text summary must be generated once, server-side, from the explanation tree, so that the portal, REST API and PowerShell return identical text.
21. The "To come into scope" bullets must describe each failing branch of the tree in plain British English:
    - Each failing criterion of an `All` group is one bullet ("Department must equal Finance (currently Engineering)").
    - A failing `Any` group is one bullet offering its alternatives ("either Cost Centre starts with FIN (currently no value), or Job Title contains Accountant (currently Software Engineer)").
    - Where several top-level groups exist, the bullets present them as alternatives.
    - Missing values read as "needs a value" where that is the reason, including for negated operators that a missing value fails ("Worker Type needs a value that does not equal Test").
22. The plain-text summary must contain the person's display name, the Connected System, the reason, the Synchronisation Rule, the bullets (where applicable) and the evaluation time in UTC (so the text is identical on every surface and unambiguous when forwarded), formatted to paste cleanly into plain-text messages. The **Copy summary** action must copy it without the row being expanded, and confirm the copy.

#### E. REST API

23. A new Administrator-only endpoint must return a Metaverse Object's connections: the joined connections with their recorded history and scoping explanations, and, when requested, the not-connected entries with their reasons, explanations, hints, bullets and summaries.
24. The existing Metaverse Object DTO must gain Created By and Last Updated By, matching the portal Properties tab.

#### F. PowerShell

25. A new cmdlet, `Get-JIMMetaverseObjectConnection`, must return the same information, with `-Id`, `-ConnectedSystemName` and `-IncludeNotConnected` parameters, and accept pipeline input from `Get-JIMMetaverseObject`.
26. Criteria must be returned as flat, filterable objects carrying a path that locates each in the tree, so that `Where-Object { -not $_.Met }` lists every failing criterion. The path is the top-level group's position followed by each child's position within its group, one-based and dot-separated, counting criteria before child groups as the evaluator does (`1.3.2` is the second child of the third child of the first top-level group).
27. `Get-JIMMetaverseObject` must surface Created By and Last Updated By.

#### G. Durable join record

28. When a Connected System Object is joined to a Metaverse Object (by projection, inbound matching, export matching or provisioning), JIM must record the Synchronisation Rule responsible and a snapshot of its name on the Connected System Object, and clear both when the join is broken.
29. Deleting a Synchronisation Rule must not fail or remove the Connected System Object; the recorded rule reference is cleared and the name snapshot kept.
30. Recording must not change any synchronisation outcome or add measurable cost to synchronisation.

### Non-Functional Requirements

- **Performance:** opening the Connections tab must issue a bounded number of database queries regardless of how many criteria or rules are evaluated; evaluation itself runs in memory against the already-loaded objects. The not-connected section may load on first expansion if that keeps the tab's first paint fast.
- **Synchronisation performance:** the shared evaluation path must not add measurable overhead or per-evaluation allocations to the synchronisation hot path when no explanation is requested.
- **Security:** all new endpoints and UI require the Administrator role; credential attribute values are masked (requirement 8); no explanation content is logged.
- **Accessibility:** outcomes are conveyed by icon and text as well as colour; expand and copy controls are real buttons with accessible names.
- **Language:** British English, Title Case JIM entity names, no em dashes, in UI text, summaries and docs.

## Examples and Scenarios

### Scenario 1: Not in scope, with a missing value

**Given** Jane Smith (Department Engineering, Job Title Software Engineer, no Cost Centre) and an enabled export rule "Finance App Users Export" with the criteria `Employee Status equals Active AND Department equals Finance AND (Cost Centre starts with FIN OR Job Title contains Accountant)`
**When** an administrator opens Jane's Connections tab and expands Not connected
**Then** a Finance App row shows "Not in scope · Fails on Department; Cost Centre or Job Title". Expanding it shows:

```
To come into scope
  • Department must equal Finance (currently Engineering)
  • and either Cost Centre starts with FIN (currently no value), or Job Title contains Accountant (currently Software Engineer)

Scoping (evaluated now)
  All of these must be met (not met)
    ✓ Employee Status equals Active            is Active
    ✗ Department equals Finance                is Engineering
    Any one of these (none met)
      − Cost Centre starts with FIN            has no value
      ✗ Job Title contains Accountant          is Software Engineer
```

and **Copy summary** puts this on the clipboard:

```
Jane Smith is not provisioned to Finance App.
Reason: not in scope of the Synchronisation Rule "Finance App Users Export".
To come into scope:
- Department must equal "Finance" (currently "Engineering")
- and either Cost Centre starts with "FIN" (currently no value), or Job Title contains "Accountant" (currently "Software Engineer")
Evaluated 4 Oct 2026 10:41 UTC.
```

### Scenario 2: In scope, not yet provisioned

**Given** a new export rule "Learning Platform Users Export" (provisioning on) was created after Jane's attribute values last changed, and Jane meets its criteria
**When** the administrator expands Not connected
**Then** a Learning Platform row shows "Not yet provisioned · In scope; nothing staged yet", and its expansion explains what will stage the provisioning export, worded for the engine's behaviour at the time (today: the next change to Jane's attribute values; see Resolved Decisions).

### Scenario 3: In scope, rule does not provision

**Given** "Contractor Portal Users Export" has no scoping criteria and Provision to Connected System switched off, and Jane has no object in Contractor Portal
**Then** a Contractor Portal row shows "Rule doesn't provision · In scope; provisioning is off", and its expansion says the rule only manages objects that join, so either provisioning must be switched on or an object must be created in Contractor Portal to join.

### Scenario 4: Why a joined account exists

**Given** Jane's HR System object was projected by "HR Users Import" on 14 Mar 2026
**When** the administrator expands the HR System row in the joined table
**Then** "How it joined" shows "Projected by Synchronisation Rule HR Users Import, 14 Mar 2026 09:12", with a link to the Activity, and "Scoping (evaluated now)" shows "HR Users Import · In scope" with each criterion met.

### Scenario 5: Misconfigured rule

**Given** a legacy export rule carries a criterion whose operator is invalid for its attribute's type
**When** the administrator opens the Connections tab
**Then** the rule's entry shows the criterion as Invalid and the rule's outcome as undetermined; every other entry still loads.

### Scenario 6: PowerShell

```powershell
PS> Get-JIMMetaverseObjectConnection -Id $jane.Id -IncludeNotConnected | Format-Table ConnectedSystem, Object, Role, Join, State

ConnectedSystem      Object                   Role    Join         State
---------------      ------                   ----    ----         -----
HR System            Jane Smith (E10442)      Source  Projected    InSync
Corporate Directory  CN=Jane Smith,OU=Staff   Target  Provisioned  UpdatePending
Finance App                                   Target               NotInScope
Learning Platform                             Target               NotYetProvisioned

PS> $c = Get-JIMMetaverseObjectConnection -Id $jane.Id -ConnectedSystemName 'Finance App' -IncludeNotConnected
PS> $c.Scoping.Criteria | Where-Object { -not $_.Met } | Format-Table Path, Attribute, Comparison, Expected, Actual

Path    Attribute    Comparison  Expected    Actual
----    ---------    ----------  --------    ------
1.2     Department   Equals      Finance     Engineering
1.3.1   Cost Centre  StartsWith  FIN         (no value)
1.3.2   Job Title    Contains    Accountant  Software Engineer

PS> $c.Summary   # the same plain text as the portal's Copy summary
```

## Constraints

- No new NuGet packages or third-party dependencies.
- Self-contained and air-gap deployable; no external services.
- One schema change only: two nullable columns on Connected System Objects for the durable join record (section G). The explanations themselves need none.
- Must not change any synchronisation outcome. The shared evaluation path is a refactor for synchronisation, verified by the existing synchronisation test suites passing unchanged.
- Surface parity: portal, REST and PowerShell ship in the same PR (or stack).

## Affected Areas

| Area | Impact |
|------|--------|
| Application | `ScopingEvaluationServer` refactored around one evaluator that can produce an explanation; new connection-explanation method on the Metaverse server building joined and not-connected entries, reasons, hints, bullets and summaries |
| Models | Explanation tree types, not-connected entry and reason enum, extended connection DTO |
| API | New `GET` connections endpoint on `MetaverseController`; Created By and Last Updated By on the Metaverse Object DTO |
| PowerShell | New `Get-JIMMetaverseObjectConnection`; `Get-JIMMetaverseObject` output gains two properties |
| UI | `View.razor` Connections tab (row expansion, Not connected section, Copy summary); shared explanation tree component |
| Worker | Records the joining rule at the join sites; otherwise none (synchronisation keeps calling the boolean methods) |
| Database | Two nullable columns on Connected System Objects (joining rule reference, name snapshot); one migration |

## Documentation Impact

| Doc | Change |
|------|--------|
| `docs/configuration/metaverse.md` | New "Connections" section under Objects: joined rows and their expansion, the Not connected section, reasons, Copy summary, "evaluated now" |
| `docs/configuration/synchronisation-rules.md` | Short cross-link from scoping criteria to the Connections explanation, including how missing values and top-level groups evaluate |
| `docs/configuration/sync-preview.md` | Update the passing mention of the Connections tab |
| `docs/powershell/metaverse.md` | New `Get-JIMMetaverseObjectConnection` section with output shapes; Created By and Last Updated By on `Get-JIMMetaverseObject` |
| `docs/api/` | New endpoint, where the API reference lists Metaverse endpoints |
| `engineering/SYNC_RULE_SCOPING.md` | The shared evaluator and the explanation path |

## Dependencies

- None blocking. Builds on the Connections tab (#1519) and value provenance (#399).
- Related: [#204](https://github.com/TetronIO/JIM/issues/204) scope management enhancements (its "validate whether an object is in scope" item is partly delivered here, for saved rules), [PRD_SCOPING_CRITERIA_EVALUATION_MATRIX.md](../PRD_SCOPING_CRITERIA_EVALUATION_MATRIX.md) (integration coverage of the same evaluator; complementary, and an explanation endpoint could later give it a no-commit evaluation path), [#1463](https://github.com/TetronIO/JIM/issues/1463) group-based scoping (any new criterion kind must extend the explanation too).

## Resolved Decisions

1. **Scoping groups nested more than two levels deep never loaded (confirmed, fixed).** The criteria editor lets a group hold child groups at any depth, and such trees saved correctly, but every rule loader included only two levels: a deeper group was absent at evaluation, its parent evaluated as an empty group (which counts as met), and scope silently widened. The editor's loader, configuration drift scope and the attribute-in-use check shared the assumption. Fixed first, supporting any depth, in its own stack layer beneath this work: [#1928](https://github.com/TetronIO/JIM/pull/1928). The explanation loads rules through the same loader, so the two cannot disagree.
2. **Multi-valued attributes in scoping** compare only the first value. Filed as [#1923](https://github.com/TetronIO/JIM/issues/1923); this PRD only makes the behaviour visible (requirement 7), and the explanation follows whatever #1923 decides because it shares the evaluator.
3. **How a join came about is recorded durably** (section G): two nullable columns on Connected System Objects, set where joins are made, with derivation from Activity history as the fallback for existing objects. Deriving only was rejected because the rule behind an inbound or export-matching join is recorded nowhere today, and everything derived ages out with Activity retention.
4. **Access to existing views is unchanged.** An interim Administrator restriction on the Inspect view and the Changes tab was considered and dropped: the Activity pages show the same per-run information to every signed-in user, so the restriction would narrow exposure without closing it. All three are re-evaluated together in the RBAC work.
5. **Rules on disabled Connected Systems** are listed with their reason and a "Connected System disabled" qualifier in the hint; rules on a Connected System being deleted are excluded (requirement 15).
6. **"Provisions at next sync" was the wrong promise.** Synchronisation evaluates provisioning only when an object's attribute values change, so creating an export rule, switching provisioning on, or widening criteria does not provision objects that are already in scope until their data next changes. The reason is therefore named **Not yet provisioned**, and its wording describes what actually triggers provisioning (requirement 16). A workflow test confirms the engine behaviour, which is a defect in its own right: filed as [#1925](https://github.com/TetronIO/JIM/issues/1925). When it is fixed, the Not yet provisioned wording changes with it.
7. **An Invalid criterion makes the outcome undetermined only where synchronisation would fail.** Synchronisation stops at the first met top-level group, so an invalid criterion in a later group is never reached and the object is in scope. Reporting such a rule as undetermined would contradict requirement 5, so the explanation reports the outcome synchronisation reaches (in scope there) and still marks the criterion Invalid; requirement 6's undetermined outcome applies where synchronisation would reach the invalid criterion first. Either way the criterion is visible, so Rule misconfigured is shown wherever it changes the answer.
8. **Masking (requirement 8)** applies to attributes on the credential denylist, and to attributes whose name looks like a credential when their type could hold one (text or binary). A credential-like name on a date, number or flag (`pwdLastSet`, `badPwdCount`) is shown, since it cannot carry credential material and is often exactly why a rule scopes someone out.
9. **The join record also says how the join was made** (projection, provisioning, inbound matching or export matching), not only which rule. A Connected System with no import rule for an object type joins on its own Object Matching Rules, so no rule is responsible; without the method, those joins (common for target systems whose existing accounts are matched up) would read "not recorded", exactly like joins made before recording began. The method also tells an inbound match apart from export matching ("found an existing object instead of provisioning one"). It is one more nullable column in the same migration.

## Acceptance Criteria

- [ ] Explained evaluation agrees with the boolean evaluation for every criterion type, comparison, group nesting, missing value and relative date (shared code path, covered by tests), and synchronisation test suites pass unchanged.
- [ ] Joined rows expand to show the projecting, provisioning or joining Synchronisation Rule, date joined and Activity link (or "not recorded"), and the current scoping evaluation, labelled as evaluated now.
- [ ] A separate "Not connected (n)" section, collapsed by default and visually distinct, lists each not-connected entry with its reason chip and a single-line, truncating hint naming the failing attributes.
- [ ] Expanding an entry shows the "To come into scope" bullet list and the explanation tree (or the what-happens-next list); the bullets are not repeated in the row.
- [ ] Copy summary on the row copies the plain-text summary without expanding; the same summary, bullets and hint are returned by REST and PowerShell.
- [ ] An Invalid criterion is reported per rule without breaking the page; credential values never appear.
- [ ] `Get-JIMMetaverseObjectConnection` and the new REST endpoint return the same information, with tests and docs.
- [ ] Created By and Last Updated By on the Metaverse Object DTO and `Get-JIMMetaverseObject`.
- [ ] Each new join records the responsible Synchronisation Rule on the Connected System Object; deleting the rule clears the reference and keeps the name; synchronisation outcomes are unchanged.
- [ ] Changelog entry and the public docs listed above.

## Additional Context

- Scoping evaluator: [src/JIM.Application/Servers/ScopingEvaluationServer.cs](../../../src/JIM.Application/Servers/ScopingEvaluationServer.cs)
- Scoping behaviour reference: [engineering/SYNC_RULE_SCOPING.md](../../SYNC_RULE_SCOPING.md)
- Connections tab: [src/JIM.Web/Shared/MetaverseObjectConnectionsTable.razor](../../../src/JIM.Web/Shared/MetaverseObjectConnectionsTable.razor), [src/JIM.Web/Pages/Types/View.razor](../../../src/JIM.Web/Pages/Types/View.razor)
- The original #348 also asked for Metaverse Object metadata and attribute provenance; those shipped in the Properties tab, #1519 and #399, and are not repeated here.
