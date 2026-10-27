---
title: Generated Values in Target Systems
description: How JIM checks a target system before choosing a generated value, and corrects a value the target refuses as already in use.
type: object
audience: administrator
---

# Generated Values in Target Systems

A [Generated Value](generated-values.md) must be free in every system it is exported to, not only in JIM's own records. JIM checks the target systems it can before choosing a value, and when a target still refuses a value as already in use, it corrects the value rather than leaving the export failing.

## :material-magnify: Checking availability in target systems

JIM's own records of a Connected System hold only the accounts JIM imports. An account outside the import scope (in an OU JIM does not manage, say, or created by hand) is invisible to them, so a value that looks free can already be in use. To catch these, JIM **probes** the target as well: before it chooses a value, it asks the target system directly whether the value is already in use, and if it is, moves on to the next candidate exactly as it would for a value it holds itself.

!!! note "A synchronisation that generates a value contacts its targets"
    A synchronisation that generates a value opens a connection to every Connected System the value is exported to whose Connector can probe (an HR import's synchronisation reaching Active Directory, for example), using that Connected System's own settings, credentials and certificates, exactly as its imports and exports do. The connection is opened the first time the run needs it, reused for the rest of the run, and closed when the run ends. The account JIM connects as needs to be able to read the probed attribute across the whole target: see the LDAP Connector's [Service Account Permissions](../connectors/jim-ldap-connector-permissions.md), the SCIM 2.0 Client Connector's [Security Considerations](../connectors/jim-scim-connector.md#security-considerations) and the SQL Connector's [Database account permissions](../connectors/jim-sql-connector-security.md#database-account-permissions).

    A target JIM cannot probe never fails the synchronisation. If the connection cannot be made, or the search is refused, times out or cannot be trusted, JIM stops probing that Connected System for the rest of the run, checks it against its own records only, and records one warning per Connected System on the synchronisation's Activity, naming the reason and how many values it chose without the probe. A probe that answers but cannot search everywhere the value must be unique (an Active Directory forest whose Global Catalog cannot be searched, say; see the LDAP Connector's [Forest-wide attributes and the Global Catalog](../connectors/jim-ldap-connector-values-in-use.md#forest-wide-attributes-and-the-global-catalog)) keeps its answers, and the run records one warning saying what it could not reach.

**What is probed.** A value exported **unchanged** (by an export Attribute Flow whose only source is the generated Metaverse attribute) to a text attribute, in a Connected System whose Connector can probe. Today that is the [LDAP Connector](../connectors/jim-ldap-connector-values-in-use.md#probing-for-values-already-in-use), the [SCIM 2.0 Client Connector](../connectors/jim-scim-connector.md#probing-for-values-already-in-use) and the [SQL Connector](../connectors/jim-sql-connector-values-in-use.md); see the Probe column on [Connectors](../connectors/index.md#available-connectors). A probe ignores case, as every uniqueness check does, so `JBloggs` in the target stops JIM choosing `jbloggs`; [Identifier uniqueness](../concepts/case-sensitivity.md#identifier-uniqueness-ignores-case) explains why, and the one kind of attribute a target cannot be asked to compare that way. A generated value on an export Synchronisation Rule is probed in its own Connected System on the same terms.

**What is not probed, and why.**

- **A Connector that cannot probe**<br /> JIM checks its own records of that Connected System only.
- **An attribute that only needs to be unique within its container**<br /> The Distinguished Name and the attributes that name entries (such as `cn`). Two entries in different OUs may share them, so a directory-wide search would report clashes that are not clashes.
- **A value that is not text**<br /> JIM checks its own records only.
- **A value exported through an expression, or combined with other sources**<br /> For example `mv["Account Name"] + "@corp.example"`. What is written there is not the generated value, so it is not checked as one. An expression built from exactly one generated value is checked, and corrected if refused, as a [value built from it](generated-values.md#values-built-from-a-generated-value); anything else is not checked, and a target that already holds it rejects the export with an ordinary export error.

**The control value.** Each probe also asks for one value JIM already holds for the attribute in that system. A target that hides the attribute from JIM's account answers "not found" for everything, which would look like a free value; the control value coming back is how JIM knows the answer can be trusted, and its absence is reported as a warning rather than believed. On a first load, before JIM holds any value for the attribute, there is no control value to ask for: the probe still runs, a value it finds is trusted (the value is in use), and a value it does not find is accepted without a warning, because an empty target is normal at that point.

**Where you see it.** The generated Attribute Flow form's **Checked for availability in** panel lists every Connected System the value is exported to, with its Connector, the attribute the value is exported as, and how it is checked: **JIM's records + probe**, **JIM's records only** or **Not checked**, each with the reason where it is less than both. The list follows the export Synchronisation Rules as they are saved, so adding or changing an export Attribute Flow changes it. The REST API's mapping endpoints carry the same rows as the `generation` object's `participants`, and `Get-JIMSyncRuleMapping` as `Generation.Participants`.

**Excluding a Connected System.** Switch **Include** off for a Connected System to leave it out of the value's availability checks: JIM neither checks its own records of it nor probes it, so a value already in use there no longer stops JIM choosing it. Use this for a system known to keep its values unique some other way, or one whose values never clash with the others'. The exclusion saves with the Attribute Flow, and is recorded in the Synchronisation Rule's change history. Only a Connected System the value is exported to unchanged can be excluded; a value on an export Synchronisation Rule is checked only in its own Connected System, so it has nothing to exclude. Excluding removes nothing from any system and changes no value already assigned; if a value JIM then chooses is already in use in the excluded system, that system rejects the export and JIM reports an ordinary export error. The same setting is the `generation` object's `exclusions` on the REST API, and `-ExcludeConnectedSystemId` on `New-JIMSyncRuleMapping` and `Set-JIMSyncRuleMapping` (`@()` clears it).

**Sync Preview.** A preview is a dry run and never contacts a Connected System, so it checks JIM's own records only. It checks the [values built from](generated-values.md#values-built-from-a-generated-value) each candidate too, and says when a value would wait for an input. Where the synchronisation would also probe for a value the preview generated, or one built from it, the preview says so, naming the Connected Systems it would probe and the value: if one of them already has an account using it, the synchronisation generates a different value instead. The REST API's Sync Preview response carries the same information as `generatedValueProbes`, with `builtFromAttributeName` on a value built from the generated one.

## :material-alert-octagon-outline: When a target rejects a value

Checking JIM's records and probing make a clash rare, not impossible: an account can be created in the target between the check and the export, or sit somewhere a probe cannot see. When that happens the target refuses the export because another object there already holds the value, and JIM corrects the value for you. This is **Collision Remediation**.

JIM corrects the value when all of these hold:

- The target's Connector recognises "already in use" refusals and says which value was refused. The LDAP, SCIM and SQL Connectors do; see each Connector's page.
- The refusal is about one generated value. The target named an attribute that carries the generated value, or one built from exactly one generated value whose **Keep unique** switch is on (a User Principal Name made from the Account Name, say, with [Deriving Metaverse attributes](derived-attributes.md); see [Values built from a generated value](generated-values.md#values-built-from-a-generated-value)), or it named nothing and the export carries exactly one generated value. JIM never guesses: a refusal it cannot tie to one generated value is an ordinary export error.
- No other target has already accepted the value, or a value built from it, for that person (see below).

**What JIM does.** It draws the next value exactly as it would have at the start (`joe.bloggs` was refused, so `joe.bloggs1`, checked against everything JIM knows), saves it on the Metaverse Object, and leaves the refused export queued. The next synchronisation of any Connected System carries the corrected value to every target's queued export, the refusing one included, and each system's synchronisation recalculates values built from it, such as an email address or User Principal Name. The export run after that sends the corrected value. JIM does not retry inside the export run that saw the refusal, because the export would still carry the values built from the old one. A value generated on an export Synchronisation Rule is corrected on the queued export itself, since it belongs to that one Connected System Object.

The refused value is [retired](generated-values.md#retired-values) when the flow never reuses values, so it is not issued to anyone else. The export run's Activity shows the export as **Value corrected**, naming the refused and the new value, and counts the corrections; the Metaverse Object's history records the change. Nothing about the export counts as a failure: its retry count is untouched.

**When JIM does not correct it.** Renaming an account that is already in use somewhere is not a decision JIM makes on its own. The value **needs a decision** when:

- another target has already accepted it for this person (its account in that system holds the value, or a value built from it such as the Email built from the Account Name), so correcting it would rename that account or change that address too;
- JIM cannot tell whether another target holds it, because that Connected System's Connector Space was cleared and no Full Import has completed since; or
- JIM has already corrected the value five times and the target keeps refusing, which points at something a further rename will not fix.

The export is then **parked**: it is not sent again, and the export run's Activity records a **Needs a Decision** error naming the refusing Connected System, the value, and the system that already holds it. Changing the Attribute Flow's generation settings, including switching Collision Remediation on or off, or switching **Keep unique** on or off for a value built from it, releases the parked export for the next export run to try again under the new settings.

**Acting on a held value.** Three answers are open to you:

- **Allow the rename.** JIM records that you allowed it and releases the export. At the next export that meets the refusal, JIM checks every system again, chooses the next free value and applies it everywhere the value is used, renaming the account that already holds the current one. The new value is decided then, not when you allow it. The allowance covers one rename; the history of the object and the export run's Activity both record that you allowed it. If that export succeeds instead, because the clash was resolved some other way, the allowance lapses: a later refusal asks you again rather than renaming the account.
- **Try again.** For when the clash has been resolved in the target (the other account renamed or removed there). The export is released and the next export run tries the same value; if the target refuses it again, it is held again.
- **Leave it.** Nothing needs doing to keep it held: the export stays parked until you act or change the Attribute Flow.

Every allowed rename and every try again is recorded as an Activity naming who took it.

In the portal, **Operations > Generated Values** lists every value waiting for a decision and every allowed rename still waiting for its export, with counts of each and of the values corrected in the last seven days. Filter it by Connected System, Synchronisation Rule or status; each row says why the value is held and offers **Allow the rename…** and **Try again**, and **Try again for all** acts on everything the filters match. **Allow the rename…** opens a confirmation naming every Connected System that will change and how (the account renamed, created or updated), the value JIM is likely to choose where it can say, and that the value is decided at the next export. The tab is badged with the number waiting. A warning chip on the **Synchronisation Rules** and **Connected Systems** lists counts the values held for each and opens the tab filtered to it, and a Metaverse Object with a held value shows it above its tabs, with both actions.

In PowerShell, `Get-JIMGeneratedValueDecision` lists what is held (with why, since when, and which systems are involved), `Approve-JIMGeneratedValueRename` allows the rename (it asks first, because it renames a live account), and `Reset-JIMGeneratedValueDecision` tries again, for one value, a pipeline of them, or everything matching a Connected System or Synchronisation Rule; see [Generated value decisions](../powershell/generated-values.md#get-jimgeneratedvaluedecision). On the REST API:

| Request | What it does |
|---|---|
| `GET /api/v1/generated-values/decisions` | The held values, newest first, paged; narrowed by `connectedSystemId` (the system that refused the value, the one anchoring it, or any system the value is exported to and checked in), `syncRuleId`, `status` (`NeedsDecision` or `RenameAllowed`) and `metaverseObjectId` |
| `GET /api/v1/generated-values/decisions/summary` | How many need a decision, how many renames are allowed and waiting, and how many values were corrected in the last seven days |
| `GET /api/v1/generated-values/decisions/{id}` | One value, whatever its state |
| `POST /api/v1/generated-values/{id}/allow-rename` | Allows the rename; answers with the value as it now stands |
| `POST /api/v1/generated-values/{id}/try-again` | Tries again; answers with the value as it now stands |
| `POST /api/v1/generated-values/decisions/try-again` | Tries again every held value matching `connectedSystemId`, `syncRuleId`, `metaverseObjectId` or `ids` in the body; a body naming none of these must set `applyToAllDecisions` |

Both actions take effect at once (they release the export for the next export run), so they answer `200` rather than queueing work; a value that is not waiting on a decision answers `409`.

**Which systems can report a collision.** A generated mapping's participants (the "Checked for availability in" list, `participants[].reportsCollisions` on the REST API, `Generation.Participants` in PowerShell) say for each Connected System whether its Connector reports a value as already in use. Collision Remediation acts only on refusals from those that do. Switch it off per Attribute Flow with the **Collision Remediation** switch below the panel, `collisionRemediation` on the mapping's `generation` object or `-CollisionRemediation $false` in PowerShell, and a refusal is reported as an ordinary export error instead. When none of the systems the value is exported to can report a collision, the portal's switch is unavailable and says so.

**When it is an ordinary export error.** A refusal JIM cannot tie to one generated value, a Connector that does not recognise "already in use" refusals, an Attribute Flow with Collision Remediation switched off, or a refused value built from a generated value with its **Keep unique** switch off: the export fails like any other, with the **Value Already in Use** error, and the value is left as it is for you to resolve.

## :material-book-open-variant-outline: Related

- [Generated Values](generated-values.md): generating a value, and keeping it unique in JIM's own records.
- [Connectors](../connectors/index.md): which Connectors can check availability and report a value already in use.
