# Several Export Synchronisation Rules per Connected System Object

- **Status:** Planned
- **Created:** 2026-11-24
- **Author:** JayVDZ (drafted via Claude Code)
- **Issue:** [#2826](https://github.com/TetronIO/JIM/issues/2826)

## Problem Statement

A Connected System Object can be written by only one export Synchronisation Rule. When two enabled export rules for the same Metaverse Object Type, Connected System and Connected System Object Type both have a Metaverse Object in scope, the run cannot tell which rule owns the object. Since #2820 it stages nothing for that object in that Connected System and records an `ExportRuleOverlap` execution item, on every run, until the scopes stop overlapping. Drift Detection and Enforce State do the same: with two enforcing rules covering the object, neither corrects anything. Saving the second rule warns about it whatever attributes the two rules write.

This forces administrators into one large export rule per account. A common need, "every employee gets a base account, and people leaders also get a few manager-only attributes", has to be written as a single rule in which every manager-only mapping repeats the same condition (`IIF(mv["isPeopleLeader"], "PeopleLeader", null)`). The manager-only attributes cannot have their own scope, their own Enforce State setting or their own owner, and the condition has to be kept in step by hand across every mapping that needs it.

The decisions below were made in the design review of 2026-11-24, one topic at a time, with the product owner. They follow one governing principle, carried over from the inbound side (#1537): **an add-on rule owns values, the rule that manages the account owns the account**, and **a rule that stops applying takes back what it wrote, unless the administrator says otherwise**.

## Vocabulary used in this document

- **Account**: a Connected System Object that a Metaverse Object is joined to. Used in examples because it reads naturally; the product surfaces say Connected System Object.
- **Covers**: an enabled export rule covers an account when the account's Metaverse Object is in the rule's scope and the account is of the rule's Connected System and Connected System Object Type.
- **Managing rule**: the export rule recorded on the account's join, because it provisioned the account or matched it on export. JIM already records this (`JoinSyncRuleId`).
- **Add-on rule**: any other export rule covering the same account. Typically one that does not provision (Provision to Connected System off).
- **Clash**: two rules covering one account both have an enabled Attribute Flow targeting the same attribute.

## Goals

- Several enabled export rules can cover one account, provided they write different attributes; their changes reach the target as one Pending Export.
- A rule that starts covering an account sends every attribute it writes, so an add-on rule's values reach new and existing accounts without waiting for a source change.
- A clash no longer blocks the whole account: everything else flows, the contested attribute is left alone, and the clash is reported.
- An export rule has an Out-of-Scope Action like an import rule's, deciding whether the values it wrote are recalled or left in place when it stops covering an account that stays.
- Only the managing rule (or another rule that provisions) decides whether an account exists; an add-on rule never keeps an account alive and its Deprovisioning Action never overrides the managing rule's.
- Each rule's Enforce State applies to the attributes it writes, and only those.
- Every behaviour change an upgrade brings to an existing configuration is stated in the changelog: the #2822 narrowing (E) and Recall on a hand-over between two provisioning rules (Scenario 8).
- The new setting and choices ship in the portal, the REST API and PowerShell together, with documentation.

## Non-Goals

- **Precedence between rules writing the same attribute.** A clash stays a reported clash. Ranking the rules per attribute (the export equivalent of Attribute Priority, with null handling) is [#2827](https://github.com/TetronIO/JIM/issues/2827).
- **Several rules contributing values to one multi-valued attribute** (for example two rules each adding group memberships). This is a clash in this PRD. Combining values with per-value ownership is in scope of #2827, alongside the inbound design in [PRD_MULTI_VALUED_CONTRIBUTION_MODES](PRD_MULTI_VALUED_CONTRIBUTION_MODES.md).
- **Several accounts per Metaverse Object in one Connected System.** A Metaverse Object still holds at most one Connected System Object per Connected System; rules targeting different Connected System Object Types in one system remain the #1331 / #2824 conflict.
- **Changing what a lone export rule does.** Where only one export rule covers an account, every behaviour is exactly as today.

## User Stories

1. As an identity administrator, I want a base account rule plus small add-on rules, each scoped to the people it applies to, so that I do not repeat the same condition across every mapping of one large rule.
2. As an identity administrator, I want an add-on rule's values to appear on an account as soon as the rule applies, including on a brand-new account, so that new managers are complete from day one.
3. As an identity administrator, I want to choose, per export rule, whether the values it wrote are removed or kept when it stops applying to an account, so that a manager flag that grants access is removed when someone steps down, while values worth keeping are not.
4. As an identity administrator, I want a stale flag in HR data never to keep a leaver's account alive, and an add-on rule's settings never to delete an account another rule manages, so that the account lifecycle stays where I set it: on the rule that manages the account.
5. As an identity administrator, I want a mistake where two rules write the same attribute to leave the rest of the account current and tell me exactly which attribute and rules clash, so that one wrong mapping does not freeze an account's updates.
6. As an automation engineer, I want the new setting and choices on the REST API and PowerShell, with the same defaults as the portal, so that scripted configuration behaves exactly as clicked configuration.

## Requirements

### Functional Requirements

**A. Combining rules on one account (topic 1)**

1. When more than one enabled export rule covers an account, each rule's attribute changes are staged into the account's one Pending Export. The run never stages two Pending Exports for one Connected System Object.
2. Grouping is by Connected System and Connected System Object Type. Rules for different Connected Systems are unaffected by each other, as today.
3. Where a rule provisions the account in this evaluation, it is staged first, so every other covering rule's changes join its Create (see C).
4. Every staged change continues to record the rule that produced it (`SyncRuleId` / `SyncRuleName` on the Pending Export change and the change history), so the causality views show which rule wrote what.
5. Where only one export rule covers an account, behaviour is unchanged.

**B. Clashes: two rules writing the same attribute (topic 2)**

6. A clash is decided per account: two covering rules each with an enabled Attribute Flow targeting the same attribute. Rules whose scopes never overlap, writing the same attribute, are not a clash and raise nothing at run time.
7. On a clash, every other attribute from every covering rule is staged as normal. The contested attribute is staged by neither rule; the account keeps whatever value it holds.
8. The run records one execution item per clashing account per run, naming the Connected System, the attribute(s) and the rules. It reuses the `ExportRuleOverlap` error type with its trigger narrowed (no new type, so existing API and PowerShell consumers keep working).
9. Drift Detection and Enforce State never correct a contested attribute.
10. When a clash ends (one of the rules stops covering the account, or a mapping is removed), the remaining owner of the attribute sends its value, as a rule starting to cover the attribute does (C).
11. Two rules adding values to the same multi-valued attribute is a clash under this rule. The values JIM adds itself for an auxiliary class (#492) are not counted as anyone's mapping.
12. Saving an enabled export rule warns only when another enabled export rule for the same Metaverse Object Type, Connected System and Connected System Object Type targets the same attribute, and the warning names the attribute(s). Two rules writing different attributes save without a warning. (Today's warning fires for any two such rules.)

**C. A rule starting to cover an account (topic 3)**

13. When an export rule starts covering an account, for a new or an existing account, it stages every attribute it writes, not only those whose source changed. No-net-change detection still drops values the account already holds.
14. A rule that does not provision contributes its attributes to the Create staged by a rule that does, in the same evaluation, so a new account is complete from its first export.
15. Where a Create is already queued and unsent from an earlier run, an add-on rule now covering the account contributes to that Create. Where the Create has already been sent and awaits confirmation, the add-on rule's changes wait and go as an Update once it is confirmed (the existing mechanism for changes to an exported-but-unconfirmed Create).
16. If today's single-rule behaviour has the same gap (a rule coming into scope of an existing account sends only changed attributes), the fix applies to it as well, and the changelog records it as a fix.

**D. An export rule stopping covering an account that stays (topic 4)**

17. Export Synchronisation Rules gain an **Out-of-Scope Action**, mirroring the import rule setting of the same name: **Recall its values** or **Leave them in place**. It applies when the rule stops covering an account that another export rule still covers (so the account stays). It has no effect on an account no other rule covers: there, the rule's Deprovisioning Action applies, as today.
18. **Recall its values** stages, for each attribute the rule wrote, a change that clears it, on that account only. Nothing is staged for an attribute another covering rule still writes; that rule's value stands (and C applies if it now becomes the only writer).
19. Recall is ownership-based. It only ever acts on accounts the rule actually covered. An account the rule never covered is never touched, whatever its values. JIM therefore records which export rules cover each account, and uses that record, not the current values, to decide.
20. Recall never clears an attribute the target cannot have cleared: naming and anchor attributes, attributes the Connected System accepts only on create (`WritableOnCreate`), and mappings set to Initial Export Only. Saving a rule set to Recall whose mappings include such an attribute shows a warning naming it.
21. **Leave them in place** stages nothing. The values stay on the account, and nothing enforces them (the rule no longer covers it).
22. **Deleting** an export rule that covers accounts other rules also cover offers the same choice as deleting an import rule (#1537): **Recall the attribute values** (default) or **Keep the attribute values**, with the impact (how many accounts, per attribute) stated first. Recall is queued as a Worker task with its own Activity, and deleting the rule is its final step.
23. **Removing a single Attribute Flow mapping** from such a rule offers the same choice, taking effect at the next synchronisation of the Metaverse Objects concerned, as it does inbound.
24. **Disabling** a rule or a mapping is a pause, as inbound: nothing is recalled, and the values stay ready for it to be re-enabled.
25. The Recall/Keep choices on delete exist in the portal, as a REST query parameter (`keepContributedValues`, as on the import delete endpoints) and as `-KeepContributedValues` on `Remove-JIMSyncRule` and `Remove-JIMSyncRuleMapping`.

**E. Which rule decides whether the account exists (topic 5)**

26. For an account with a managing rule, only the managing rule decides whether the account exists. An add-on rule covering the account never keeps it alive, and its Deprovisioning Action is not used for that account.
27. When the managing rule stops covering the account, another covering rule takes the account over (#2822) only if it provisions (Provision to Connected System on). Otherwise the account is deprovisioned by the managing rule's Deprovisioning Action, even while add-on rules still cover it.
28. When the managing rule and add-on rules stop covering an account at the same time, only the managing rule's Deprovisioning Action applies. (Today Delete beats Disconnect between rules; for an account with a managing rule, an add-on's Delete no longer wins.)
29. For an account with no managing rule (joined by an import rule), behaviour is as today: the account is deprovisioned when no export rule covers it, by the actions of the rules it left.
30. The portal shows, on an export rule that does not provision, that its Deprovisioning Action is not used for accounts another rule manages. The export rule documentation says the same.
31. Requirement 27 narrows shipped #2822 behaviour, which today lets any covering rule take an account over. The changelog records it as a behaviour change; #2822's own changelog entry describes the provisioning case, which is unchanged.

**F. Enforce State (topic 6)**

32. Drift Detection checks each attribute against the rule that writes it, using that rule's Enforce State setting. A rule with Enforce State on corrects only the attributes it writes; one with it off corrects none.
33. Corrections from several rules for one account join the account's one Pending Export, together with any export evaluation changes, as drift and export evaluation merge today.
34. Contested attributes (B) and values left in place (D, requirement 21) are never corrected.

**G. Previews and surfaces**

35. The synchronisation preview and the configuration change previews show the combined Pending Export, any clash with its attribute, any recall a scope change or rule change would stage, and any account a change would deprovision under E.
36. The Out-of-Scope Action for export rules is editable in the portal (the rule's Scope tab, beside the import rule setting it mirrors), on the REST API's Synchronisation Rule create and update DTOs, and as a parameter on `New-JIMSyncRule` and `Set-JIMSyncRule`, with the same default and documentation.
37. Every portal string added is localised (en-GB, en-US where spelling differs, and every language in progress).

### Non-Functional Requirements

- **Scale.** Grouping and ownership are decided in memory from configuration already cached for the run; they add no per-object query on the synchronisation hot path. The record of which rules cover each account (requirement 19) is written in bulk with the page, only when coverage changes, through the raw-SQL bulk paths and their column-list guards. It must hold at 1M accounts with several rules each.
- **Integrity.** Every recall, clash and deprovisioning decision is reported on an execution item; nothing is silent. A run never fails a page because of two rules on one account.
- **Determinism.** The outcome does not depend on rule order, run order or which rule was evaluated first.

## Examples and Scenarios

The examples share one cast. Corporate AD, Connected System Object Type `user`.

| | Rule A: Base account | Rule B: Manager attributes |
|---|---|---|
| Scope | `employmentStatus = Active` | `isPeopleLeader = true` |
| Provisions | Yes | No |
| Deprovisioning Action | Disconnect | Delete |
| Out-of-Scope Action | Recall its values | Recall its values |
| Enforce State | On | Off |
| Writes | `sAMAccountName`, `givenName`, `sn`, `displayName`, `mail` | `extensionAttribute10`, `departmentNumber` |

### Scenario 1: promotion

**Given** Alice has an account managed by A, and is not a people leader
**When** `isPeopleLeader` becomes true
**Then** B starts covering the account and stages `extensionAttribute10 = PeopleLeader` and `departmentNumber = CC-4410` as one Update, though `costCentre` did not change (C). No item is recorded.

### Scenario 2: new starter who is already a manager

**Given** no account exists for Dev, who is Active and a people leader
**When** the synchronisation runs
**Then** one Create carries A's five attributes and B's two. The join records A as the managing rule.

### Scenario 3: change while both rules cover

**Given** Alice is covered by A and B
**When** her surname changes
**Then** one Update carries `sn` and `displayName` from A. B stages nothing.

### Scenario 4: stepping down

**Given** Alice is covered by A and B
**When** `isPeopleLeader` becomes false
**Then** B's Out-of-Scope Action applies. With Recall, one Update clears `extensionAttribute10` and `departmentNumber`. With Leave, nothing is staged. Bob, who was never a people leader and has a hand-typed `extensionAttribute10`, is never touched in either case (D, requirement 19).

### Scenario 5: clash

**Given** both A and B map `title`, and Alice is covered by both
**When** her `title` source and her `sn` change
**Then** the Update carries `sn` and `displayName`. `title` is staged by neither rule and keeps its current value. One `ExportRuleOverlap` item names `title`, A and B. Drift Detection leaves `title` alone. Saving the second `title` mapping showed a warning naming the attribute.

### Scenario 6: leaver with a stale manager flag

**Given** Alice is covered by A and B
**When** `employmentStatus` becomes Leaver but `isPeopleLeader` stays true
**Then** A stops covering the account. B does not provision, so it cannot take the account over (E, requirement 27). The account is disconnected by A's Deprovisioning Action. B's Delete is not used.

### Scenario 7: leaving both at once

**Given** Alice is covered by A and B
**When** she leaves and her flag is cleared in the same import
**Then** only A's Deprovisioning Action (Disconnect) applies. Today, B's Delete would win.

### Scenario 8: Contractor to Staff, unchanged

**Given** two provisioning rules, Contractor and Staff, and Sam's account managed by Contractor
**When** Sam becomes Staff
**Then** Staff takes the account over, as #2822 shipped. Attributes only the Contractor rule wrote follow Contractor's Out-of-Scope Action: with the default, Recall its values, they are cleared. Today they are left in place, so this is a behaviour change on upgrade (see Constraints).

### Scenario 9: hand edit under Enforce State

**Given** Alice is covered by A and B
**When** someone edits `mail` and `extensionAttribute10` in Active Directory
**Then** the next import's Drift Detection puts `mail` back (A enforces) and leaves `extensionAttribute10` as edited (B does not).

### Scenario 10: deleting the add-on rule

**Given** B covers 120 accounts
**When** an administrator deletes B
**Then** the confirmation states 120 accounts and the two attributes, with **Recall the attribute values** selected. Recall queues a Worker task with its own Activity, which clears both attributes on those 120 accounts and then deletes B. Keep deletes B at once and leaves the values.

## Constraints

- **Upgrade default: Recall its values, for existing and new export rules alike** (decided 2026-11-24). The one existing configuration this changes is a hand-over between two provisioning rules (Scenario 8): attributes only the departing rule wrote are now recalled where today they are left. The changelog records it as a behaviour change, beside the #2822 narrowing, and says how to keep today's behaviour (set the rule to **Leave them in place**). Upgrading in place otherwise keeps working unchanged.
- **The coverage record starts empty.** It fills as objects are evaluated after the upgrade; an account without a record is treated as never covered, so nothing is recalled from it until a rule has been recorded covering it.
- Released migrations are append-only; the new setting and the coverage record arrive in a new migration.
- No new third-party dependencies.
- English text is written in British English with Title Case product nouns; every new portal string ships with its en-US, French and German entries per `engineering/LOCALISATION.md`, using the glossary's word for each term. No em dashes in any language.
- Air-gapped: nothing here needs connectivity.

## Affected Areas

| Area | Impact |
|------|--------|
| Database | Export rule Out-of-Scope Action column (or reuse of the existing one for export rules); a new table recording which export rules cover each Connected System Object; migration setting every export rule to Recall its values |
| Application | Export evaluation and staging (`ExportEvaluationServer`, `SyncEngine.ExportStaging`): grouping, ordering, clash detection per attribute, full staging on coverage start, recall on coverage end; deprovisioning decisions under E; Drift Detection per-attribute ownership; synchronisation and configuration change previews; save warnings; rule and mapping deletion recall |
| Worker | Coverage record writes in the page flush; recall task for rule deletion; execution item reporting for clashes and recalls; Enforce State |
| Models | Export rule setting; coverage record entity; narrowed overlap DTO naming attributes |
| API | Synchronisation Rule create and update DTOs gain the export Out-of-Scope Action; delete endpoints honour `keepContributedValues` for export rules |
| PowerShell | `New-JIMSyncRule`, `Set-JIMSyncRule` parameter; `Remove-JIMSyncRule`, `Remove-JIMSyncRuleMapping` `-KeepContributedValues` for export rules; Pester tests |
| UI | Scope tab setting for export rules; Deprovisioning Action note on non-provisioning rules; delete and mapping removal dialogs for export rules; clash and recall outcomes in execution items and causality views |
| Integration tests | A new scenario for base plus add-on rules against a directory; every scenario that reads `ExportRuleOverlap` items or relies on #2822 hand-over updated |

## Documentation Impact

| Doc | Change |
|------|--------|
| `docs/configuration/scoping-criteria.md` | "Several rules for the same objects": rewrite the single-export-rule guidance; base plus add-on pattern; the export Out-of-Scope Action |
| `docs/configuration/synchronisation-rules.md` | Export Out-of-Scope Action; deleting an export rule and removing a mapping with Recall/Keep; save warnings now naming attributes |
| `docs/configuration/deprovisioning.md` | Managing rule and add-on rules; which Deprovisioning Action applies; the #2822 narrowing |
| `docs/concepts/attribute-priority.md` | The "inbound concern" note gains a pointer: on export, rules write distinct attributes, and precedence is #2827 |
| `docs/administration/troubleshooting-synchronisation.md` | `ExportRuleOverlap` now means a shared attribute, and what to do |
| `docs/powershell/synchronisation-rules.md`, `activities.md`, `execution-items.md`, `pending-exports.md` | New parameter; narrowed error type description |
| `docs/api/index.md` | No breaking change; note the narrowed error type trigger |
| `engineering/DEVELOPER_GUIDE.md` | Export staging: one Pending Export per account from several rules; coverage record |

## Dependencies

- #2820 (run-time overlap check) and #2822 (scope-exit hand-over): both shipped; this PRD narrows each.
- Blocks #2827 (export attribute precedence).
- Reuses the inbound recall-on-delete machinery from #1537.

## Decisions Recorded After Review

1. **Upgrade default** (2026-11-24): Recall its values for existing rules as well as new ones; see Constraints.
2. **Import-joined accounts** (2026-11-24): an account joined by an import rule has no managing export rule, and stays as today under E (requirement 29).

## Acceptance Criteria

- [ ] Two export rules writing different attributes on one account stage one Pending Export carrying both rules' changes; no `ExportRuleOverlap` item is recorded.
- [ ] A new account in scope of a provisioning rule and an add-on rule is created with both rules' attributes in one Create.
- [ ] A rule that starts covering an existing account stages all its attributes, not only changed ones.
- [ ] A clash stages every other attribute, leaves the contested one, records one item naming it and the rules, and Drift Detection leaves it alone; the remaining owner sends its value when the clash ends.
- [ ] Save warnings fire only for a shared attribute, and name it.
- [ ] Recall clears only the attributes the departing rule wrote, only on accounts it covered, and never a naming, anchor, create-only or Initial Export Only attribute; Leave stages nothing; disabling recalls nothing.
- [ ] Deleting an export rule or removing one of its mappings offers Recall/Keep on all three surfaces, with Recall the default.
- [ ] An add-on rule never keeps an account alive, and its Deprovisioning Action never applies to an account another rule manages; Contractor to Staff hand-over is unchanged.
- [ ] Enforce State corrects only the attributes of rules that have it on.
- [ ] Previews show combined exports, clashes, recalls and deprovisioning as the run would.
- [ ] After upgrade, existing configurations behave as before except for the two changes the changelog records (the #2822 narrowing, and Recall on a hand-over between two provisioning rules).
- [ ] Unit tests (red first), `RequiresPostgres` tests for every new raw SQL write, an integration scenario for base plus add-on rules, Scenarios 008 and 032, and every scenario reading `ExportRuleOverlap` or relying on hand-over, all pass.
- [ ] Documentation and changelog updated in the same pull requests.

## Additional Context

- Design review walkthrough (2026-11-24) and its explainer artefact.
- [PRD_ATTRIBUTE_VALUE_RECALL_CHOICE](done/PRD_ATTRIBUTE_VALUE_RECALL_CHOICE.md): the inbound Recall/Keep choice this mirrors.
- [Attribute Priority](../../docs/concepts/attribute-priority.md): the inbound model #2827 will mirror on export.
- Delivery is planned as three stacked pull requests: E (managing rule decides) at the bottom, then A, B, C and F (combining rules), then D (Out-of-Scope Action and Recall).
