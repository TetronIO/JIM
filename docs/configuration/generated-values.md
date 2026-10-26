---
title: Generated Values
description: "Generate a value no one else holds, such as an account name or employee number, assign it once, and keep it unique across systems."
type: object
audience: administrator
---

# Generated Values

Some values have no source to copy: an account name that must not clash with anyone else's, an employee number, a badge number, a correlation identifier. For these, choose **Generated Value** as the mapping's Source Type. JIM generates the value, checks that no one else holds it, and assigns it once; the value then persists and is never regenerated.

A generated mapping is an ordinary Attribute Flow in every other respect: it is just another way to produce a value for the target attribute, like a direct or expression mapping, and it follows the same [Attribute Priority](attribute-mappings.md#attribute-priority) rules. The only difference is that it generates its value once.

A generated value is a **base expression** plus a **uniqueness token**:

| Uniqueness token | What JIM adds | Example |
|---|---|---|
| **Add a number only if the value is already taken** | Nothing, unless the base value is taken; then a number (or letter) suffix | `joe.bloggs`, then `joe.bloggs1`, `joe.bloggs2` |
| **Always add a sequence number** | The next number from a counter JIM keeps for the target attribute, optionally zero-padded to a fixed width | `00000100456`, or `G-100456` with the base `"G-"` |
| **Always add a random token** | A GUID, or a short hexadecimal or digit string from a cryptographic source; a clash draws again | `a3f9c2e1` |

The base expression is an ordinary [expression](attribute-mappings.md#expression-mappings), for example `Lower(cs["firstName"]) + "." + Lower(cs["lastName"])`, and is [checked when you save it](../concepts/expressions.md#validation) like any other. It is required for "only if taken" and optional for the other two; with no base expression, the value is the number or token on its own. A new generated value starts on a sequence number and moves to "only if taken" once you type a base expression, until you choose a token yourself. When the base value looks like an email address, the token goes before the `@`. A **Separator** (dot, hyphen, underscore, or up to three characters of your own) can sit between the base value and the token.

**When JIM generates a value.** **How it works**, at the foot of the editor's **Try it** pane, states the three conditions for the mapping:

1. The Metaverse Object has no value from a higher-priority source (see [Attribute Priority](attribute-mappings.md#attribute-priority)). This is the only precedence rule generation follows: it is a contribution like any other, and it contributes only when every higher-priority contribution is silent.
2. Every attribute the base expression reads has a value, and so does every input of the values [checked with it](#values-built-from-a-generated-value). Generated mappings default to "Wait until every input has a value" (see [Missing Input Behaviour](attribute-mappings.md#missing-input-behaviour)), because a value built from a missing input would be kept for ever.
3. Then once only. The value is kept even if its inputs later change: a surname change does not rename the account.

**Existing accounts (brownfield).** Generation only fills a gap, so keeping an existing target account's value is a priority decision, not a generation setting. Add an import Attribute Flow from that target for the same Metaverse attribute, at a higher priority than the generated flow; the target's own value then wins for every object it already holds, and generation supplies a value only for objects the target has nothing for (typically new starters). Once you do this, that import flow becomes the higher-priority contributor for every account the target holds, not only the ones that pre-existed it: from then on the target owns the value, so a rename made there flows into the Metaverse, and the generated value assignment JIM was keeping for an object is removed the moment the import flow takes over for it. This is the ordinary shape for a directory that owns account names once they exist; if you want JIM to keep owning the value instead, do not add that import flow, and make sure the target holds no conflicting values before the first export. On an export mapping there is no import flow to give priority to, because a generated export value belongs to that Connected System Object alone; to set a value only on accounts JIM itself creates, without touching accounts that already exist, use [Initial Export Only](attribute-mappings.md#initial-export-only-outbound) instead. Bringing a higher-priority import flow online safely, so it reaches the target's synchronisation before that target's export runs, is covered in [Initialising JIM](../concepts/synchronisation-pipeline.md#initialising-jim).

**When a higher-priority flow stops contributing.** If a higher-priority Attribute Flow for the attribute is disabled or removed, the generated flow becomes the winning contributor again and writes its own value, exactly as a direct or expression mapping would: the value it generated earlier if JIM still holds it for that object, otherwise a newly generated one. Whatever the higher-priority flow left on the Metaverse Object is replaced; JIM never takes that value over as a generated value.

!!! warning "Handing the attribute back can rename accounts"
    Generation never renames an account by itself. But when a higher-priority contributor is disabled or removed, the generated flow takes the attribute over like any Attribute Flow, and the ordinary export then renames the account in every target the attribute is exported to. For example, HR generated `percival.ashworth`; a higher-priority import flow from the directory then wrote `pashworth99`; when that directory flow is disabled, HR's next synchronisation writes `percival.ashworth` back and the export renames the directory account.

    To keep existing accounts as they are, use one of the standard protections:

    - Make the target's export of the attribute [Initial Export Only](attribute-mappings.md#initial-export-only-outbound), so JIM sets the value when it creates the account and never changes it afterwards.
    - Keep the target's higher-priority import flow enabled, so the target's own value keeps winning.

**Uniqueness.** Before assigning a value, JIM checks it and the values [built from it](#values-built-from-a-generated-value), ignoring case ([why](../concepts/case-sensitivity.md#identifier-uniqueness-ignores-case)), against the values already issued in the same run, every Metaverse Object's value for the attribute, and the Connector Space of every Connected System the attribute is exported to unchanged; where that system's Connector can, it also asks the target system itself (see [Checking availability in target systems](generated-values-in-target-systems.md#checking-availability-in-target-systems)). A value an object already holds is always free for that object, and so is a value held by the object's own joined account in a target: if Jane Smith's existing directory account is already `jsmith`, that is her, not a clash, so she gets `jsmith` (exactly what a direct or expression mapping would write), while another person's `jsmith` still forces `jsmith1`.

**Sequences.** The counter belongs to the target attribute, not to the mapping, so removing and re-creating the mapping continues where it left off, and it only moves forward. On first use it starts above the highest numeric value already present for the attribute. The next number is the higher of the counter and **Start at**; raising Start at is how you reserve a range or continue a sequence issued elsewhere, and the editor asks you to confirm the numbers it skips. Lowering it has no effect. With **Pad to a fixed width** on, choose whether a number that outgrows the width stops with an error or is allowed to grow longer. A sequence number is never reused. Numbers are issued in order across synchronisations, so joiners arriving a few at a time get consecutive numbers. A gap can still appear, and is never filled: when two runs draw from the same counter at the same time, when a run stops part-way, or when a number is skipped because it is already held. When the counter reaches numbers that are already held, by an object's value, a target account, another object's generated value or the retired values, JIM looks up where that run of held numbers ends and issues the first free number above it, in one step however long the run is; the skipped numbers cost no attempts, so the attempt limit only counts genuine collisions. A number held only by the object's own account is not skipped: the object gets it, as it would any value its own account already holds.

**Start again.** The Attribute Flow's **Start again…** action (typed confirmation) returns a sequence's counter to Start at, for example after rebuilding a solution. It also forgets the attribute's [retired values](#retired-values), so those numbers can be issued again; the confirmation says how many, and links to the list, before you commit. It changes no existing value and exports nothing; numbers still held when the counter reaches them are skipped, however many there are (see **Sequences** above). The action is recorded as an Activity, whose message includes how many retired values were forgotten.

## Testing the base expression

The **Try it** pane beside the editor's settings runs the base expression as a Synchronisation would, as you type. Give each attribute it reads a value, or load them from an object, and see:

- what the expression returns;
- the Attribute Flow's value processing;
- the base generation would start from, with what happens to it.

Under that, **Result** shows the value the token would give from that base, and the candidates that follow when it is taken. Which of them is free is decided when a Synchronisation runs, so the test never says which one an object would get. An input with no value shows what the mapping's [Missing Input Behaviour](attribute-mappings.md#missing-input-behaviour) does: by default generation waits (condition 2 above), and a mapping set to fail shows the error a Synchronisation would record. A base expression that returns several values, or a value with no text, also shows the error a Synchronisation would record.

Over the REST API and PowerShell, test the Attribute Flow with [`Test-JIMExpression`](../powershell/expressions.md#test-jimexpression) `-SyncRuleId` and `-MappingId`: `GeneratedValue` is `$true`, and `BaseOutcome` and `BaseValue` say what generation would be given.

A [configuration test case](configuration-tests.md) for a Generated Value keeps that check: it expects the base the expression gives, never a candidate, so it passes or fails the same way however many values are already taken.

## Checking and correcting values in target systems

Before choosing a value, JIM can ask a target system whether the value is already in use there, and when a target still refuses a value as taken, JIM corrects it for you. See [Generated Values in Target Systems](generated-values-in-target-systems.md).

## Values built from a generated value

A generated value rarely travels alone: an Email [derived](derived-attributes.md) from the generated Account Name, a User Principal Name from that Email, an export expression such as `mv["Account Name"] + "@corp.example"`. Each is a **value built from the generated value**, and changes whenever it does.

JIM works these out from the Synchronisation Rules alone, following derived flows step by step (a User Principal Name read from Email is built from Account Name *through* Email); there is nothing to declare. A value built from two generated values (`mv["Account Name"] + "-" + mv["Badge Number"]`) is never checked or corrected: JIM cannot tell which to change.

**Checked before issuing.** For each candidate, JIM works out every value built from it on the generated flow's own Synchronisation Rule (and every export expression reading it), and checks each as it checks the generated value, [probe](generated-values-in-target-systems.md#checking-availability-in-target-systems) and exclusions included. It issues the candidate only if all are free, so `joe.bloggs` and `joe.bloggs@corp.example` are issued together or not at all. The Activity records each candidate passed over (**Candidate passed over**), naming the value already held and where. Values are checked when chosen and never revisited, so clashes among values issued before these checks applied are not reported.

A value built on **another** Synchronisation Rule is only corrected after a refusal: that rule runs only for objects joined to its own Connected System, which a new starter usually is not yet. If its flow reads only Metaverse attributes, move it to the generated flow's rule to have it checked first.

**Waiting for an input.** An Email built from Account Name and Region cannot be worked out while the object has no Region, so JIM holds Account Name back, whatever the Missing Input Behaviour, until a synchronisation brings the Region. The item shows **Value held back**, naming the input, and the run warns with a count. Switch the value's **Keep unique** off if it should not wait.

**Provision once, with the final identifiers.** A generated value is issued only when every value checked with it is ready and free, and is never emptied, so scope each export Synchronisation Rule that provisions on it having a value (**Account Name** has a value): the account is then created once, with its final identifiers, never created and renamed. Inputs only the export reads need criteria of their own, and as scoping also decides whether an object stays provisioned, choose criteria an object keeps for as long as it should have the account.

**Keep unique with the generated value.** Each Attribute Flow building such a value has a **Keep unique with Account Name** switch (named after its generated value), on by default. On, the value is checked before issuing (when built on the generated flow's rule), and a target's refusal of it as already in use is [corrected](generated-values-in-target-systems.md#when-a-target-rejects-a-value) by correcting the generated value: `joe.bloggs@corp.example` is refused, so Account Name becomes `joe.bloggs1` and the Email follows. Off, for a value that need not be unique or whose clashes you would rather resolve yourself, it is neither checked nor corrected, never holds the generated value back, and a refusal of it is an ordinary **Value Already in Use** export error. The switch governs that value alone: with Email off, a User Principal Name built through Email is still checked and corrected. The generated flow's own **Collision Remediation** switch comes first for correction: while it is off, nothing built from the generated value is corrected, though values are still checked.

Off, the value still follows the generated value when it is corrected for another refusal. Changing the switch changes no value and exports nothing; it releases any value [held for a decision](generated-values-in-target-systems.md#when-a-target-rejects-a-value) on the generated value, so the next export run tries again, and is recorded in the Synchronisation Rule's change history.

The switch belongs to Expression mappings, import and export; JIM refuses to switch it off on a direct or generated mapping, and on an expression reading no generated value it has no effect.

**Accounts already using it.** JIM does not correct a generated value another target has already accepted for that person, or accepted a value built from it, whatever that value's switch: if a mail system holds the Email built from `joe.bloggs`, correcting Account Name would change that mailbox's address, so the value [needs a decision](generated-values-in-target-systems.md#when-a-target-rejects-a-value) instead. Only values written unchanged by a direct export Attribute Flow count; one written by an export expression does not yet.

**Where you see them.**

- **Portal**<br /> An Attribute Flow building its value from a generated value shows a **Built from generated value** block with the **Keep unique with Account Name** switch and what it does there. It is marked **Not in effect** while the generated flow's Collision Remediation is off (the switch stays changeable), and has no switch for a value built from several. The generated flow's dialog lists every value built from it under **Values built from Account Name**, in **How it works**: its rule and how it is built, where it is written unchanged, whether it is checked before issuing (where, or why not), and what happens if a target refuses it; its **Result** works out the values checked with each candidate. Change a value's switch on its own Attribute Flow; the list only reports it.
- **REST API**<br /> A mapping carries `keepUnique`; one that builds a value from a generated value also carries `builtFromGeneratedValue`, describing it as the panel does (`via`, `kind`, `exportedTo`, `checkedBeforeIssuing`, `probedIn`, `moveToSyncRuleName` and the generated flow's `collisionRemediation`). A generated mapping's `generation.builtValues` lists every value built from it. Create and update requests take `keepUnique`.
- **PowerShell**<br /> `-KeepUnique` on `New-JIMSyncRuleMapping` and `Set-JIMSyncRuleMapping` for Expression mappings (see [Synchronisation Rule cmdlets](../powershell/synchronisation-rules.md)); `Get-JIMSyncRuleMapping` returns `KeepUnique`, `BuiltFromGeneratedValue` and `Generation.BuiltValues`.

## Retired values

A generated value is often an identifier: an account name, an email address, an employee number. Issuing one to a second person is a security problem as much as a data one, because mail, group memberships and permissions keyed on that identifier quietly follow it to the new holder. So when JIM stops holding a generated value, it **retires** it: the value goes into the attribute's **retired values** list, and JIM never issues it again, whichever Attribute Flow generates the attribute.

A value is retired when:

| Why (as the portal shows it) | Stored reason | What happened |
|---|---|---|
| **Object deleted** | `ObjectDeleted` | The object that held the value was deleted: a leaver's Metaverse Object, or for an export flow the Connected System Object. |
| **No longer generated** | `Superseded` | Another Attribute Flow now supplies the attribute for that object (see [Existing accounts](#generated-values) above), so JIM stopped managing the generated value. |
| **Flow removed** | `Recalled` | The Attribute Flow that generated the value was removed. |
| **Regenerated** | `Regenerated` | A target refused the value as already in use and JIM [corrected it](generated-values-in-target-systems.md#when-a-target-rejects-a-value). |

The REST API and PowerShell report the stored reason.

**Never reuse a value.** Retiring is controlled by the mapping's **Never reuse a value** switch, which is on by default for "only if taken" and random tokens. With it on, a retired value is skipped when JIM generates: if `marisol.fenwick` left and was retired, the next Marisol Fenwick gets `marisol.fenwick1`. Turn it off only where a value may safely pass to a different person; with it off, nothing is retired, and a leaver's value is free for the next person who needs it. A sequence always behaves as though the switch is on, because its counter only moves forward, so the editor shows a locked line rather than a switch.

**Is anything lost?** No. Retiring removes nothing from any Connected System and changes no value an object still holds; it only stops JIM issuing the value again. Retired values are kept permanently, and nothing but **Start again** on a sequence clears them: there is deliberately no way to release one value, because that is how an identifier passes to a new person by accident.

**Where you see them.**

- On the Synchronisation Rule's **Attribute Flow** tab, a generated row shows an **N retired** chip once its attribute has any, and every generated row has a **View retired values** action. The list is per attribute, not per flow: two flows generating Account Name share one list, because neither may reissue the other's values. It is read-only and searchable by value or by the name of the object that held it; **Held by** links to the Metaverse Object while it still exists, and shows its last name, greyed, once it has been deleted.
- On a Metaverse Object's **Changes** tab, a retirement is an event in the timeline: the attribute, the retired value struck through, why, and the synchronisation that caused it. A deleted object's retirements are only in the list, since the object and its timeline are gone.
- A synchronisation that retires values records it on the object's summary in the Activity, under the change that caused it.
- In PowerShell, `Get-JIMRetiredGeneratedValue` (see [Generated Values cmdlets](../powershell/generated-values.md#get-jimretiredgeneratedvalue)); on the REST API, `GET /api/v1/metaverse/attributes/{id}/retired-generated-values` for an import flow's attribute and `GET /api/v1/synchronisation/connected-systems/{connectedSystemId}/object-types/{objectTypeId}/attributes/{attributeId}/retired-generated-values` for an export flow's, both with search and paging. A generated mapping's `generation` object on the mapping endpoints carries `retiredValueCount`.

**Number attributes.** A generated value can target a Text or a Number attribute. A Number attribute accepts only a sequence or a Digits random token, with no base expression, no separator and no padding, because a prefix or leading zeros would make the value text. The editor disables the incompatible choices and says why; the REST API and PowerShell refuse the same configurations with the same reasons.

**Where you see it.**

- The editor's **Result** shows the first value and the candidates that follow when it is taken, worked out from the values tried in its **Try it** pane, or from placeholder values until you try some. For a saved rule it also counts the existing Metaverse Objects that would receive a value on the next Full Synchronisation, and for a sequence it shows the counter's state.
- A synchronisation's Activity counts **Values Generated**, and each object's summary names the value it was given. A failure (no free value within the attempt limit, or a sequence that outgrew its width) is an ordinary Activity error naming the reason.
- On the Metaverse Object page's [Inspect view](metaverse-objects.md#where-a-value-comes-from), a generated value's source reads **Connected System · Synchronisation Rule · Generated Value**, with its own share of the source bar, and its inspector gives the Source type as **Generated Value** (marked **Corrected** if a collision revised it).

Generated values work on export mappings too: the value is generated for, and kept on, the Connected System Object alone, and never written to the Metaverse. That suits a reference number a target system needs but nothing else uses.

An export-mode generated value is drift-checked like any export Attribute Flow. When the Synchronisation Rule [enforces state](synchronisation-rules.md#export-outbound) and the value is changed in the target system outside JIM, the target system's next synchronisation corrects it back to the value JIM generated for that object (not a freshly evaluated base expression), and a change of letter case alone counts as a change, exactly as it does for any other Attribute Flow. Mark the mapping [Initial Export Only](attribute-mappings.md#initial-export-only-outbound) to have the value written once, when the object is provisioned, and then left alone. An object JIM has not yet generated a value for is not checked; the value is generated and exported instead.

Configure a generated mapping in the Attribute Flow editor, with `New-JIMSyncRuleMapping -Generate` and `Set-JIMSyncRuleMapping` in PowerShell (see [Synchronisation Rule cmdlets](../powershell/synchronisation-rules.md)), or with the `generation` object on the REST API's mapping endpoints. The REST API also lists a Metaverse Object's generated values (`GET /api/v1/metaverse/objects/{id}/generated-values`), reads a sequence's state (`GET /api/v1/synchronisation/sync-rules/{syncRuleId}/mappings/{mappingId}/sequence`) and starts a sequence again (`POST /api/v1/synchronisation/sync-rules/{syncRuleId}/mappings/{mappingId}/generation/restart`).

A value a target system rejects as already in use is corrected or held for a decision, as described in [When a target rejects a value](generated-values-in-target-systems.md#when-a-target-rejects-a-value). A generated value cannot yet be changed by hand.
