# Generated Value Forms

- **Status:** Planned
- **Created:** 2026-10-08
- **Author:** Drafted by Claude Code for product owner review
- **Issue:** [#2023](https://github.com/TetronIO/JIM/issues/2023)

## Problem Statement

A **Generated Value** Attribute Flow guarantees uniqueness for one Metaverse attribute: the one it generates. Before issuing a value, JIM checks it against its own records and, where the value is exported **unchanged** and the Connector can, probes the target system (#242 release 3; the LDAP, SCIM 2.0 Client and SQL Connectors).

Values **built from** a generated value get no such check: a Metaverse attribute derived from it (Derived Attribute Flows, #1750), or an export expression that reads it. These are only dealt with after a target refuses one. Collision Remediation (#242 release 4) follows the derived flow graph back to the one generated value a refused value was built from, draws the next value, and the next synchronisation re-derives the rest. That safety net has three costs:

1. **A refused export and a lost cycle.** The new starter's account is not created on the first synchronisation.
2. **A decision instead of a fix.** When another target has already accepted the generated value, correcting it would rename that account too, so the value waits for an administrator's decision.
3. **No help where nothing refuses.** Active Directory refuses a duplicate `sAMAccountName` (per domain) and `userPrincipalName` (per forest), but it accepts a duplicate `mail` or `proxyAddresses` entry. A duplicate email address built from a generated account name is never checked and never refused: two people end up sharing an address, and nobody is told.

JIM's reference setup is exactly this shape. Scenario 001 (`-GenerateAccountName -DeriveFromAccountName`) generates **Account Name** and derives **Email** (`mv["Account Name"] + "@panoply.local"`, step 2) and **User Principal Name** (`mv["Email"]`, step 3) on the same import Synchronisation Rule. The most visible identifier, the email address, is the one nothing protects.

There is also an understanding problem. To know what is protected, an administrator has to know which attribute is generated and which is derived, and that only the generated one is checked before export. The **Checked for availability in** panel lists only where the generated attribute itself is exported unchanged, so it cannot answer "is the email address checked?"

## Goals

- Before issuing a generated value, JIM checks every **form** of each candidate, and issues the candidate only if every form is free. A form is the generated value, or any value built from it along the derived chain on the generated flow's own Synchronisation Rule. Otherwise JIM moves every form on to the next candidate together, so they stay consistent. ("Form" is the engineering and code term; the portal, docs, messages, REST API and PowerShell say "values built from Account Name", per [Decision 2](#decisions).)
- "Checked before issuing" and "corrected after a refusal" cover the same forms, chosen by one rule, so administrators learn one behaviour. A value built from the generated value on **another** Synchronisation Rule is corrected after a refusal only, as today, and the panel says so and how to have it checked ([Decision 1](#decisions)).
- In JIM's reference setup, a duplicate Email (as `mail` or as an alias in `proxyAddresses`) and a duplicate UPN are caught before export, and the new starter's accounts are created on the first synchronisation.
- One panel on the generated Attribute Flow shows every value built from it, where each is exported, and how each is checked. One switch per derived value, **Keep unique with Account Name**, says whether it is checked and corrected with the generated value. This absorbs #2014.
- Checking more forms does not multiply directory searches: at most one search per Connected System and route (domain or forest) for each batch of candidates.
- Surface parity: the panel, the switch and the forms are available in the portal, the REST API and PowerShell.

## Non-Goals

- **Values built from two or more generated values.** They have no single value to move to the next candidate, so they are neither checked nor corrected, exactly as Collision Remediation treats them today.
- **Changing how derivation, Attribute Priority or synchronisation ordering work.** Forms are worked out early in order to be checked; the values themselves are still produced in their normal place.
- **Reissuing or renaming values already issued.** Forms apply when a value is being chosen.
- **Non-text values.**
- **Sync Preview contacting target systems.** Preview stays a dry run that checks JIM's records only, and says which forms it would probe.
- **Multi-forest searching** through one Connected System (see #1164).
- **Checking values built on other Synchronisation Rules before issuing.** They are corrected after a refusal only, as today ([Decision 1](#decisions)).
- **Finding clashes among values issued before this feature ships.** Only values chosen from then on are protected, and the docs and release notes say so ([Decision 3](#decisions)).

## User Stories

1. As an identity administrator, I want JIM to choose an account name only when the email address and UPN built from it are also free everywhere they go, so that new starters' accounts are created on the first synchronisation and no two people share an address.
2. As an identity administrator, I want one place on the generated Attribute Flow that lists every value built from it and how each is checked, so that I can see what is protected without knowing which attributes are generated and which are derived.
3. As an identity administrator, I want to leave a particular derived form out of the value, so that a form whose refusal means something else (a UPN suffix the directory does not allow, say) is neither checked nor allowed to rename accounts.
4. As an automation engineer, I want the values built from a generated value, how they are checked, and the **Keep unique** switch through the REST API and PowerShell, so that I can review and script them as I do the rest of a Synchronisation Rule.

## Requirements

### Functional Requirements

1. **What a form is.** A generated value's forms are:
   - the generated Metaverse attribute itself;
   - every Metaverse attribute produced by an enabled Derived Attribute Flow **on the generated flow's own Synchronisation Rule** whose Metaverse inputs, followed **transitively** through the derived flow graph, lead back to exactly one generated value, and that value is this one, with every derived attribute along the way also on that rule;
   - every export expression mapping whose Metaverse inputs resolve the same way, through the generated value and its forms only.

   The resolution is the one `GeneratedValueRejectionAttribution` already uses for Collision Remediation. Both features use one shared implementation, never two copies. It returns every value built from exactly one generated value, and marks which are forms. Inputs that are not generated values (a UPN suffix attribute, say) are allowed.

   A value built from the generated value by a Derived Attribute Flow on **another** Synchronisation Rule, or through one, is **corrected only**: it is not checked before issuing, and it is still corrected after a refusal, as today ([Decision 1](#decisions)). That rule's flows run only in its own Connected System's synchronisation, for objects joined to that system, which a new starter usually is not when the value is generated. Their other inputs often come from that system too, so waiting for them would hold the generated value back until after the account it is for exists.
2. **When a form applies to an object.** A form applies unless a contributor with higher Attribute Priority holds a value for that attribute on the object, the same condition generation already applies to itself. A form that does not apply is not checked. Every derived form is on the generated flow's own Synchronisation Rule, which applies whenever the value is generated, so no other rule's scoping is evaluated at generation time.
3. **Working out a candidate's forms.** For each candidate, JIM evaluates each applicable form's expression in derived step order, using the candidate in place of the generated value and the object's current values for every other input. A form whose other inputs have no value makes the generated value wait, as a generated value already waits for its own inputs ("Wait until every input has a value").
4. **Checks per form.** Each form passes through the gates a generated value passes through today, for its own attribute:
   - values already issued in the run;
   - every Metaverse Object's value for the attribute;
   - the connector space of each Connected System the form is exported to unchanged;
   - the probe of each such system, where its Connector can probe.

   An export expression form is checked in the Connected System and attribute its expression writes, with the expression's output. The retired values register applies to the generated value only: a retired generated value is never drawn, so neither are its forms.
5. **All or nothing.** A candidate is issued only if every applicable form passes. Otherwise JIM draws the next candidate for the generated value, and the rejection names the form and where it was found (for example "Email (built from Account Name) is in use in Corp AD").
6. **Fail closed.** If a form's expression fails for a candidate, JIM does not issue the candidate. It records the error on the object's execution item as a derived flow error is recorded today.
7. **One search per system and route.** The probe batches every form bound for the same Connected System into one search per route: one for the domain, and one through the Global Catalog for forest-wide attributes (#1940). For example, in Active Directory `sAMAccountName` goes in a domain search, and `mail` (with `proxyAddresses`) and `userPrincipalName` share one forest search. A Connector that cannot combine attributes may search once per attribute. Each attribute keeps its own control value.
8. **One switch per derived value.** Every value built from exactly one generated value, form or corrected only, carries **Keep unique with Account Name** (naming the generated attribute; on by default):
   - **On:** a form is checked before issuing and corrected after a refusal; a corrected-only value is corrected after a refusal.
   - **Off:** the value is neither checked nor corrected, does not hold the generated value back, and a refusal of it is an ordinary **Value Already in Use** export error.

   The generated flow's existing **Collision Remediation** switch still governs correction for the whole value. With it off, forms are still checked before issuing but nothing is corrected after a refusal. The switch lives on the derived or export expression mapping and is recorded in the Synchronisation Rule's change history.
9. **The panel.** The generated Attribute Flow's **Checked for availability in** panel lists every form under **Values built from Account Name**. For each it shows:
   - the form's Metaverse attribute, and how it is built ("via Email");
   - each Connected System it is exported to, and the attribute;
   - how it is checked (JIM's records, plus the directory or the whole forest);
   - its switch.

   Corrected-only values are listed beside them: "Not checked before issuing: built on the Mail Import Synchronisation Rule, which runs only once the person is in that system". Where the flow reads only Metaverse attributes, the panel adds "Move it to HR Import to have it checked first". Derived flows that read this value but are not built from it alone are listed with the reason (another generated value is also an input).
10. **On the derived mapping.** A Derived Attribute Flow or export expression built from a generated value says so where it is edited: "Built from generated value Account Name". It also shows the switch, what the switch does there ("checked before Account Name is issued, and corrected if a target refuses it", or for a corrected-only value "corrected if a target refuses it; not checked before issuing because it is on a different Synchronisation Rule"), and links to the docs. This is the visibility half of #2014.
11. **Preview.**
    - The generated flow editor's candidate preview ("Test this Expression") shows each candidate's forms.
    - Sync Preview checks every form against JIM's records, and names the Connected Systems and forms it would probe in `generatedValueProbes`.
12. **REST API and PowerShell.**
    - A generated mapping's `generation.participants` gains the values built from it. Each entry names the value, how it is built, and whether it is checked before issuing or corrected only.
    - Derived and export expression mappings built from a generated value expose `builtFromGeneratedValue`: the generated attribute, `checkedBeforeIssuing`, and the switch as `keepUnique`. `New-JIMSyncRuleMapping` and `Set-JIMSyncRuleMapping` take `-KeepUnique`, and `Get-JIMSyncRuleMapping` returns `KeepUnique` and `CheckedBeforeIssuing`.

### Non-Functional Requirements

- **Directory cost:** at most one probe search per Connected System per route for each batch of candidates, however many forms there are. Measure at Medium and at a Scale template on an initial load.
- **Evaluation cost:** at most one expression evaluation per form per candidate. Report the added generation time in the run's performance diagnostics.
- **Synchronisation integrity:** a candidate known to have a taken form is never issued. Every rejection, wait and expression failure is reported on the run, never silent.
- **Air-gapped:** no new dependencies.

## Examples and Scenarios

### Scenario 1: Email already used as an alias (reference setup)

**Given:** Corp AD has a contractor account whose `proxyAddresses` holds `smtp:joe.bloggs@panoply.local`. Account Name is generated; Email is `mv["Account Name"] + "@panoply.local"`; UPN is `mv["Email"]`.
**When:** a new starter, Joe Bloggs, is synchronised.
**Then:** candidate `joe.bloggs` is rejected ("Email (built from Account Name) is in use in Corp AD"), and `joe.bloggs1` is issued with Email and UPN `joe.bloggs1@panoply.local`. The account is created on the first export, with no refusal.

### Scenario 2: UPN held in another domain of the forest

**Given:** a multi-domain forest, a Global Catalog JIM can reach (#1940), and an account in another domain holding UPN `joe.bloggs@panoply.local`.
**When:** Joe Bloggs is synchronised.
**Then:** the UPN form is found through the Global Catalog, `joe.bloggs` is rejected before export, and `joe.bloggs1` is issued.

### Scenario 3: A form switched off

**Given:** the UPN mapping's **Keep unique with Account Name** switch is off.
**When:** the UPN exported for a new starter is refused as already in use.
**Then:** the export fails with **Value Already in Use**, Account Name is not changed, and UPN was not checked before issuing.

### Scenario 4: Two generated inputs

**Given:** a derived attribute `mv["Account Name"] + "-" + mv["Badge Number"]`, where both inputs are generated.
**Then:** it is not a form of either value. The panel lists it as "built from more than one generated value", and its refusals stay ordinary export errors.

### Scenario 5: A higher-priority source supplies the email address

**Given:** an import flow from the mail system supplies Email at a higher priority for existing mailboxes.
**When:** an object already has Email from that source and Account Name is being generated.
**Then:** the Email form does not apply to that object and is not checked.

### Scenario 6: A form's other input is missing

**Given:** UPN is `mv["Account Name"] + "@" + mv["UPN Suffix"]`, and the object has no UPN Suffix yet.
**Then:** Account Name waits, as a generated value waits for its own inputs. It is generated once the suffix arrives, and the run records why it waited.

### Scenario 7: Email derived on another Synchronisation Rule

**Given:** Account Name is generated on HR Import, and Email is derived from it (`mv["Account Name"] + "@panoply.local"`) on the Mail Import Synchronisation Rule instead.
**When:** a new starter, who is not yet in the mail system, is synchronised.
**Then:** Account Name is issued without Email being checked. The panel lists Email as "Not checked before issuing: built on the Mail Import Synchronisation Rule, which runs only once the person is in that system", and suggests moving the flow to HR Import, since it reads only Metaverse attributes. If a target later refuses the Email, Collision Remediation corrects it as today.

## Constraints

- Forms are resolved once, in one place, shared by generation, Collision Remediation, the panel and the REST API. No feature may carry its own idea of what a form is.
- British English. Title Case for JIM's domain nouns, as everywhere.
- Existing configurations keep today's behaviour unless the new checks find a clash: every value built from a generated value defaults to **Keep unique** on, matching today's correction default.

## Affected Areas

| Area | Impact |
|------|--------|
| Application | `UniqueValueGenerationServer`: form resolution, per-candidate form evaluation, form gates, all-or-nothing acceptance; shared form resolver with `GeneratedValueRejectionAttribution` |
| Worker | Probe session batches several attributes per Connected System and route |
| Models / Connectors | `IConnectorUniquenessProbe` gains a multi-attribute request, or the session groups requests; LDAP builds one filter across attributes (domain and Global Catalog routes kept apart) |
| Database | The **Keep unique** switch on Synchronisation Rule mappings (migration) |
| API | Participants gain the values built from the generated value; derived and export mappings expose `builtFromGeneratedValue` |
| PowerShell | `New-` / `Set-` / `Get-JIMSyncRuleMapping` parameter and output |
| UI | **Checked for availability in** panel; the derived and export expression mapping editors; the candidate preview |

## Documentation Impact

| Doc | Change |
|------|--------|
| `docs/configuration/synchronisation-rules.md` | Generated values: values built from a generated value, how they are chosen, all or nothing, and why only those on the generated flow's own Synchronisation Rule are checked first; Checking availability in target systems; When a target rejects a value (the **Keep unique** switch). State plainly that only values chosen after upgrading are protected, and that clashes among values issued earlier are not looked for |
| `docs/connectors/jim-ldap-connector.md` | Probing several attributes in one search |
| `docs/configuration/sync-preview.md` | Values built from a generated value in previews |
| `docs/powershell/...` | Mapping cmdlet parameter and output |

## Dependencies

- #1940: whole-forest and alias checks for `mail`, `proxyAddresses`, `userPrincipalName` and `servicePrincipalName`; Email and UPN forms rely on them.
- #2014: absorbed by this PRD (requirements 8 and 10); close it when this ships.
- #1750: Derived Attribute Flows and the derived flow graph.
- #242: Unique Value Generation; the probe and Collision Remediation it built.

## Decisions

Settled with the product owner on 2026-10-10.

1. **Only values built on the generated flow's own Synchronisation Rule are checked before issuing.** Values built on another rule are corrected after a refusal only, as today, and the panel says why and, where the flow reads only Metaverse attributes, suggests moving it. Checking them first would mean guessing: their rule runs only once the person is in that rule's Connected System, which a new starter usually is not, and waiting for inputs that system supplies would hold the generated value back until after the account it is for exists. The own-rule set covers the reference setup completely.
2. **The portal says "values built from Account Name", not "forms".** "Form" stays the engineering and code term. Every user-facing surface (portal, docs, run messages, REST API, PowerShell) uses this wording, naming the generated attribute:

   | Where | Wording |
   |------|--------|
   | Panel heading | Values built from Account Name |
   | The switch, on the derived or export expression mapping | Keep unique with Account Name |
   | Where space is tight (a chip) | Built from Account Name |
   | A rejection | Email (built from Account Name) is in use in Corp AD |

   "Derived values" was rejected: "derived" already covers every flow that reads the Metaverse, including ones this feature leaves out (values built from two generated values), so one word would name two different sets.
3. **Clashes among values issued before this feature are left alone.** Only values chosen from then on are protected, and the docs and release notes say so. JIM never renames values it has already issued, so a report would be a list with no fix; finding alias clashes means scanning whole directories, a different and much heavier job than checking a candidate; and generated values themselves (#242) shipped the same way. A duplicate values report can be its own feature if customers ask for one.
4. **A value whose switch is off never holds the generated value back.** It is neither checked nor corrected, and a refusal of it is an ordinary **Value Already in Use** export error (requirement 8).

## Acceptance Criteria

- [ ] A form is resolved by one shared implementation used by generation, Collision Remediation, the panel and the REST API.
- [ ] In the reference setup, a clash on Email (`mail` or a `proxyAddresses` alias) or on UPN rejects the candidate before export, and the next candidate is issued with matching forms (Scenario 001, Medium template).
- [ ] A refused value whose **Keep unique** switch is off is an ordinary **Value Already in Use** error and leaves the generated value unchanged (Scenario 023).
- [ ] Values built from two generated values are neither checked nor corrected, and the panel says why.
- [ ] Probing several forms bound for one Connected System uses one search per route, measured on an initial load.
- [ ] A value built on another Synchronisation Rule is not checked before issuing, is still corrected after a refusal, and the panel says so, suggesting the move where the flow reads only Metaverse attributes.
- [ ] The panel, the **Keep unique** switch and the values built from a generated value are available in the portal, REST API and PowerShell, with tests and docs, using the Decision 2 wording; "form" appears in no user-facing text.
- [ ] The docs and the release notes say that only values chosen after upgrading are protected.
- [ ] Sync Preview names the forms it would probe.

## Additional Context

- Unique Value Generation plan: [`engineering/plans/done/UNIQUE_VALUE_GENERATION.md`](../plans/done/UNIQUE_VALUE_GENERATION.md). Decision 9, as revised on 2026-10-06, is the attribution rule this PRD builds on.
- Today a derived value is "an ordinary contribution" with no uniqueness guarantee ([Deriving Metaverse attributes](../../docs/configuration/synchronisation-rules.md#deriving-metaverse-attributes)). This PRD keeps that true for values outside a generated value's forms.
