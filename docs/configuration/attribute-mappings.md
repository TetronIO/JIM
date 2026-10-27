---
title: Attribute Mappings
description: "Map source attributes and expressions to targets, process values, preview a change, and set which import rule's value wins."
type: object
audience: administrator
---

# Attribute Mappings

Attribute mappings define which attributes to synchronise and how to transform them. Each mapping maps a source attribute (or expression) to a target attribute.

**A target attribute can be flowed to by at most one mapping per Synchronisation Rule.** JIM refuses a second mapping targeting an attribute the rule already flows to, and a disabled mapping counts too, since re-enabling it later would recreate the clash. The refusal exists because the synchronisation engine evaluates one mapping per target attribute, so a same-rule duplicate would be accepted in configuration but silently never contribute. Express the intent like this instead:

- **Fall back between source attributes within one rule**<br /> Use a single [expression mapping](#expression-mappings): `Coalesce(cs["jimBadgeColour"], cs["roomNumber"])`, or the equivalent null-coalescing operator `cs["jimBadgeColour"] ?? cs["roomNumber"]`.
- **Arbitrate between sources by priority or authority**<br /> Define the second flow on a separate, differently-scoped Synchronisation Rule and order the contributors with [Attribute Priority](../concepts/attribute-priority.md).

## Direct mappings

A direct mapping copies the attribute value as-is, with no transformation:

| Source | Target |
|--------|--------|
| `givenName` | `First Name` |
| `sn` | `Last Name` |

## Expression mappings

An expression mapping applies a transformation using the JIM [expression language](../concepts/expressions.md). The Expression field in the Add/Edit Attribute Flow dialog highlights the expression as you type, with Metaverse (`mv["..."]`) and Connected System (`cs["..."]`) attribute references coloured differently; it grows with the expression (press Enter for a new line) and scrolls once it is long.

| Source | Target |
|--------|--------|
| `Lower(cs["givenName"]) + "." + Lower(cs["sn"]) + "@company.com"` | `Email Address` |
| `mv["First Name"] + " " + mv["Last Name"]` | `displayName` |
| `IIF(Eq(mv["Employee Status"], "Active"), 512, 514)` | `userAccountControl` |

On an export rule that provisions to Active Directory, an enabled value such as `512` needs an [Initial Password](initial-password.md) on the same rule: Active Directory refuses to create an enabled account that has no password, and with an Initial Password JIM creates the account disabled and enables it once the password is set. The accounts this expression creates disabled (`514`) stay disabled when their password is delivered: the expression decides the enabled state, not the Initial Password (see [Accounts your Attribute Flow creates disabled](../connectors/jim-ldap-connector-passwords.md#accounts-your-attribute-flow-creates-disabled)).

To keep a check that an Attribute Flow still gives the value you expect, and run it again after later changes, save a [configuration test case](configuration-tests.md) for it from the tester or the Synchronisation Rule's **Tests** tab. A later change that stops the case passing is not saved until you choose what to do about it.

### Missing Input Behaviour

An expression whose input has no value on the object does not fail; it evaluates and produces a structurally broken value (`jane.@company.com`, `CN=,OU=Users,...`) that nothing downstream can tell from a good one. **Missing Input Behaviour**, set under the expression on each expression source, decides what JIM does instead:

| Behaviour | Effect |
|-----------|--------|
| **Evaluate anyway** (default) | Runs the expression regardless; correct where the expression guards the absence itself with `IIF()` or `Coalesce()`. |
| **Contribute no value** | Skips the mapping without reporting anything; the outcome is resolved by [Attribute Priority](../concepts/attribute-priority.md). |
| **Fail this mapping** | Skips the mapping and records an **Expression Missing Input** error; the object's other attributes still flow. |
| **Fail the object** | Nothing flows for the object at all, and it is recorded as an **Expression Missing Input** error. For identity-critical values such as a Distinguished Name. |

JIM derives the inputs from the `mv["..."]` and `cs["..."]` accessors in the expression; you do not list them. An absent attribute, a null and an empty string all count as no value. A `cs["..."]` input the object holds only under another data type, because a [schema refresh changed its type](connected-system-schema.md#moving-an-attribute-to-its-new-type) and no import has read it since, is not missing: the expression is not evaluated, the Metaverse Object keeps its value (a [generated value](generated-values.md) waits), and JIM logs a warning, whatever the Missing Input Behaviour. See [Missing Input Behaviour](../concepts/expression-nulls.md#5-missing-input-behaviour-have-jim-refuse-rather-than-guess) for the full guidance.

### Changing a mapping after it is created

A mapping's **settings**, meaning how it behaves rather than what it reads and writes, can be changed at any time: Missing Input Behaviour and the expression itself, "Null is a value" and [inbound value processing](#value-processing-inbound) on an import mapping, Initial Export Only on an export mapping, a [generated value](generated-values.md)'s settings, and whether the mapping is enabled at all. Use the portal, `PATCH /api/v1/synchronisation/sync-rules/{syncRuleId}/mappings/{mappingId}`, or `Set-JIMSyncRuleMapping`.

### Disabling a single mapping

Every Attribute Flow mapping can be **disabled** individually, without touching the rest of its Synchronisation Rule. A disabled mapping is skipped by synchronisation in both directions: it contributes nothing inbound (and drops out of the attribute's [Attribute Priority](../concepts/attribute-priority.md) contention), flows nothing on export, at provisioning as much as on updates, and Drift Correction leaves its target attribute alone. Each run whose rules carry disabled mappings notes how many it skipped in the service log.

Disabling one mapping is the smallest safe response to a single source attribute that has been removed or redefined at the Connected System; disabling the whole rule stops every flow it carries. Either also covers changes already queued: a Pending Export change that a now-disabled (or removed, or deleted) export mapping or rule queued, and that has not been exported yet, is withdrawn from the queue as soon as the change is saved, from the portal, the REST API or PowerShell alike, so the Pending Exports page shows only what will actually be sent. Every export run checks the queue the same way before it starts, and its Activity carries a warning saying how many changes it withdrew. A change already sent and awaiting confirmation by import is left to complete. Where JIM disables a mapping, or a whole Synchronisation Rule, on your behalf (the [schema refresh decision](connected-system-schema.md#refreshing-the-schema)), it records why: the reason is shown on the Attribute Flow tab for a mapping, and beside the Enabled switch for a rule, and saving the item enabled clears it. Re-enabling is always a manual choice.

Disabling or removing the last enabled contributor of a Metaverse attribute that a [derived Attribute Flow](derived-attributes.md) reads goes ahead, and JIM names the derived flows it leaves with a missing input.

What a mapping **targets**, and whether its source is an attribute or an expression, is not editable. Retargeting revalidates against attribute types and plurality, and for an import mapping it reopens the mapping's place in the [Attribute Priority](../concepts/attribute-priority.md) order, so it is a delete and a create rather than an edit. That is deliberate: the priority position is lost either way, and an interface that hid it would lose it silently.

## Multi-source mappings

A multi-source mapping combines several source attributes into one target. This is the concept-level pattern; in practice, you typically express multi-source flows through expression mappings that reference each contributing attribute.

## Multi-valued attributes

Mappings support both single-valued and multi-valued attributes. A **Multi-Valued** attribute holds a list of values (group memberships, email aliases, and so on); a **Single-Valued** attribute holds at most one. How a mapping behaves depends on the plurality of the source and the target:

| Source | Target | Behaviour |
|--------|--------|-----------|
| Single-Valued | Single-Valued | :material-check-circle-outline:{ .jim-yes role="img" aria-label="Yes" } The value flows normally. |
| Multi-Valued | Multi-Valued | :material-check-circle-outline:{ .jim-yes role="img" aria-label="Yes" } Every value flows. |
| Single-Valued | Multi-Valued | :material-check-circle-outline:{ .jim-yes role="img" aria-label="Yes" } The value flows as a single-item list. On export it replaces any other values the target holds (see below). |
| Multi-Valued | Single-Valued | :material-alert-outline:{ .jim-warn role="img" aria-label="Caution" } See below. |

**Values flow as a set.** On import, JIM compares the values a Connected System Object supplies with the values the Metaverse Object holds as a set, for every data type: the order the Connected System lists them in never matters, a value supplied twice is held once, and only the values gained and lost change. Dates compare to the microsecond in UTC, so the same moment written with a different time zone or a finer fraction of a second is the same value. A Connected System Object that holds values for the attribute only under another data type, because a [schema refresh changed the attribute's type](connected-system-schema.md#moving-an-attribute-to-its-new-type) and no import has read it since, changes nothing: the Metaverse Object keeps its value, no null is asserted, and JIM logs a warning until an import reads the attribute again.

**One value written to a Multi-Valued attribute.** An export mapping that writes one value to a Multi-Valued attribute, whether from a Single-Valued source or from an [expression](#expression-mappings), owns that attribute's whole value set. Each export replaces every value the attribute holds with that one, so a value added outside JIM (an extra email alias, say) is removed the next time JIM exports the attribute, and JIM confirms the export only once the attribute holds exactly that value. If the Connected System puts values of its own back, the export is sent again at every export run and reported as not confirmed on every import, until the two agree. To manage some of an attribute's values and leave the rest alone, map a Multi-Valued source to it instead: JIM then adds and removes only the values that source gains and loses.

**Multi-Valued to Single-Valued.** A Single-Valued target can hold only one value, so this mapping is only meaningful when the object actually has one value:

- If the object has **one** value, it flows (import) or is exported normally: on export the target is set to that value, and cleared when the object has none.
- If the object has **more than one** distinct value, JIM does **not** pick one arbitrarily. A value supplied twice counts once. An arbitrary choice would be non-deterministic (a Connected System does not guarantee value order) and, on export, could never be reconciled on the next import. Instead, JIM flows nothing for that attribute and records an error against the object; the object's other attributes still synchronise. The error appears as a `Multi-Valued to Single-Valued` item in the run's [Activity](activities.md).

The Attribute Flow editor warns you at configuration time when a mapping is Multi-Valued to Single-Valued, and the mapping is flagged in the Attribute Flow list, so you can decide before running whether it is what you intend.

To flow a chosen value deterministically instead of erroring, either target a Multi-Valued attribute, or use an [Expression mapping](#expression-mappings) to select one value: `Where(cs["proxyAddresses"], "starts with", "SMTP:")`, `Min()`, `Max()`, or `Join()` to combine them (see [Multi-Valued Attributes](../concepts/expressions.md#multi-valued-attributes)). Reference attributes on import are exempt from this rule; they are resolved separately.

## Standard Mapping hints

Choosing which Metaverse Attribute a Connected System attribute belongs to is guesswork when the schema is unfamiliar, and the answer is usually already recorded: every built-in Metaverse Attribute documents its counterparts in the SCIM 2.0 and LDAP/Active Directory vocabularies as [Standard Mappings](metaverse.md#standard-mappings). The Attribute Flow editor shows them while you work:

- **The counterpart name sits beside each attribute in the picker**, so `First Name` reads as `givenName` on an LDAP system, and `name.givenName` on a SCIM one. A SCIM counterpart is the attribute name the [SCIM Connector](../connectors/jim-scim-connector.md#schema-discovery) gives it, so `Mobile Number` reads as `phoneNumbers.mobile` and `Department` as `enterpriseUser.department`. Where a mapping carries a note (`userAccountControl` needs a transform; SCIM addresses map to the work address), hovering the counterpart shows it.
- **The correspondence for the attribute you picked is stated in full.** Choose `givenName` as an import source and the editor says so: "In LDAP/AD, `givenName` corresponds to the Metaverse Attribute First Name", marks that attribute **Suggested** in the picker, and offers a one-click **Use First Name** button. Export works the same way in reverse, naming the Connected System attribute the standard says should receive the value.
- **More than one attribute can correspond to a name.** LDAP `mail` fits both the single-valued `Email` and the multi-valued `Emails`; both are offered, and JIM does not choose for you.
- **A correspondence you cannot act on is explained rather than hidden.** Where the standard names an attribute this mapping cannot target, the editor says which and why: the types differ (`accountExpires` arrives as text where `Account Expires` is a date, so an Expression source is needed to convert it), another Attribute Flow already targets it, or the Connected System reports it read-only. The mapping's note is shown alongside, which is usually where the conversion is described.

Which vocabulary applies comes from the Connector: the LDAP Connector declares LDAP/AD, so an LDAP system's editor shows LDAP counterparts and nothing else. Where a Connector declares no vocabulary (the File Connector, for example, since a delimited file's column names are whatever the file carries), the editor matches attribute names against every standard instead and labels whichever one answered.

**The hints are advisory, always.** Nothing is filtered, disabled, or chosen for you: every attribute stays selectable, and an attribute with no counterpart shows nothing, which is not an error. Standard Mappings are never consulted during synchronisation; what flows is exactly what your Attribute Flows say. Custom attributes behave identically once you record Standard Mappings against them, and JIM does not distinguish a mapping it seeded from one you authored.

## Value processing (inbound)

Source text is often dirty: stray padding, inconsistent casing, or a "value" that is really just spaces. For **import** mappings that target a **text** Metaverse attribute, you can clean and normalise the imported value before it flows to the Metaverse, configured per mapping under **Value handling** in the Attribute Flow dialog. Value processing applies to direct and expression mappings alike, and only to text attributes; it does not appear for export mappings or non-text targets.

Four controls are available:

- **Treat whitespace as no value**<br /> A whitespace-only or empty value is treated as no value: it does not flow, and clears any existing Metaverse value. This is **on by default**, so a stray space no longer masquerades as a real value. Switch it off where whitespace is genuinely meaningful.
- **Trim leading and trailing whitespace**<br /> Removes surrounding whitespace, so `··John··` becomes `John` (each `·` represents a space).
- **Collapse internal whitespace**<br /> Reduces runs of consecutive whitespace inside the value to a single space, so multiple spaces or tabs between words collapse to one. For example, `John···Smith` becomes `John Smith` (each `·` represents a space).
- **Case normalisation**<br /> Converts the value to `Upper`, `Lower`, or `Title` case, or leaves it unchanged (`None`). Useful for folding usernames or email addresses to a consistent case.

The transforms run in a fixed order: **trim, then collapse, then case normalisation, then the whitespace-as-no-value decision**. Because the whitespace decision runs last, a value that trims down to nothing is correctly treated as no value. Value processing is *normalisation*; it runs before [Attribute Priority](#attribute-priority) resolves which rule's value wins.

When **Treat whitespace as no value** is switched off and a whitespace-only value is therefore stored, the portal flags it with a `(whitespace)` indicator rather than rendering a misleading blank cell, so administrators can tell a real-but-invisible value apart from an absent one.

## Initial Export Only (outbound)

Some attributes should be set once when JIM creates an object, then left alone: a one-time setup value, or any attribute the target system should own after provisioning. An initial password is not one of them: JIM sets it through the rule's [Initial Password](initial-password.md) tab, never through Attribute Flow. For **export** mappings, enabling **Initial Export Only** on a mapping does exactly that:

- The Attribute Flows only when JIM **provisions** the object into the Connected System (the create export), and JIM retries until the initial export is confirmed.
- Once the object is provisioned, the attribute becomes **unmanaged** on that Connected System Object: later Metaverse changes are not exported for it, and Drift Correction ([enforce state](synchronisation-rules.md#export-outbound)) leaves it alone, so the value can be changed freely in the Connected System.
- Objects that **join** to pre-existing objects in the Connected System never receive the value; the external system already owns it.
- Import mappings for the same attribute are unaffected, so the externally-owned value can still flow back into the Metaverse if you map it inbound.

Configure it per mapping in the Attribute Flow editor (the option appears for export Synchronisation Rules only), via `New-JIMSyncRuleMapping -InitialExportOnly` in PowerShell, or via the REST API. The Connected System Object page marks affected attributes with an **Unmanaged** indicator once the object is past provisioning.

Two behaviours to be aware of:

- The setting is honoured live: enabling it on an existing rule stops future exports of that attribute to already-provisioned objects, and disabling it resumes normal management (the next synchronisation and Drift Correction re-assert the Metaverse value).
- If several export mappings target the same attribute for an object type, the attribute only becomes unmanaged when **every** such mapping is Initial Export Only; a single normally-managed mapping keeps it managed.

Initial Export Only is your choice about an attribute the Connected System would happily let JIM keep writing. Where the Connected System itself only accepts a value at creation, JIM applies the same create-once behaviour on its own, without the setting: see [Attribute writability](connected-system-attributes.md#attribute-writability).

## Previewing an Attribute Flow change

Changing a mapping rewrites an attribute on every object the rule manages at the next Full Synchronisation (for an export mapping, the target system's, while the rule [enforces state](synchronisation-rules.md#export-outbound)), and nothing on the editor says what the values become. An Expression edit that malforms one case in a thousand (`ada.@corp.local` for a person with no surname) is invisible until it has flowed.

The **Preview Attribute Flow Impact** button, which appears beside the editor's save button once you have edited a mapping, starts a [Configuration Change Preview](configuration-changes.md#previewing-a-change-before-you-make-it) of the mappings as they stand on the form, evaluated against the rule's saved mappings, changing nothing. It reports, per object and per attribute:

- **The value the object would end up with**<br /> Stated as an old-to-new pair, so a domain cutover reads as `ada@old.example` becoming `ada@new.example` rather than as a count. JIM groups identical pairs together and recognises the shape of the change (a changed domain, a changed container, a casing change, an added or removed prefix or suffix), so a thousand identical rewrites read as one line with a count beside it.
- **Values that would be withdrawn**<br /> Where a mapping would stop producing a value for an object, the attribute is left blank rather than rewritten, and that is counted separately.
- **Objects the mapping could not be evaluated for**<br /> An Expression that throws, or one whose [Missing Input Behaviour](#expression-mappings) fails the mapping because a required attribute has no value on that object. These are the handful of objects a cutover would otherwise leave without an address, and they are reported as their own outcome rather than as no change.

The evaluation is the synchronisation engine's own, run twice per object (once against the saved configuration and once against the proposal) and compared, so [Attribute Priority](#attribute-priority), Missing Input Behaviour and Expression evaluation are answered by the engine rather than approximated.

Both directions state a true before-and-after. An **import** mapping's old value is what the Metaverse Object holds in the Metaverse today; an **export** mapping's is what the saved configuration leaves the object holding in the target Connected System, including where it writes nothing because the target is already correct, which is exactly the case a domain cutover is.

Three answers are deliberately negative rather than reassuring:

- A proposed mapping that would **lose Attribute Priority** to another contributing rule is called out: a synchronisation would evaluate it and then write nothing, so reporting the values it produces would describe a write that never happens.
- **Removing a mapping outright is not counted as a value change.** The values it contributed are withdrawn at the next Full Synchronisation of its Connected System, or kept if you [choose to keep them](synchronisation-rules.md#deleting-a-synchronisation-rule) when you remove it, so the preview states which rather than counting a change the save itself does not make.
- The preview covers **this Connected System only**. Where another Connected System's rule also writes the attribute, that rule takes its turn on its own next synchronisation, so what it would write instead is named rather than guessed at.

Saving with a current preview on screen states its counts on the confirmation and records the preview against the change's [Activity](activities.md); edit the mappings afterwards and the preview is marked stale and contributes nothing. Automation gets the same evaluation through [`New-JIMConfigurationChangePreview -AttributeFlowMapping`](../powershell/previews.md) and the REST API's `POST sync-rules/{id}/mappings/preview` endpoint.


## Attribute Priority

When more than one import rule maps to the same Metaverse Object attribute, **Attribute Priority** decides which contributor wins, so the result never depends on the order your synchronisations happen to run in. It is an inbound concern: it governs how values flow from Connected Systems into the Metaverse, and does not change how the Metaverse is exported back out.

Priority is held **per attribute, per contributing rule**, not as a single level on the whole Synchronisation Rule. The same Connected System can therefore rank first for one attribute and second for another, and a single system can even contribute through several differently-scoped rules at different priorities.

### How a winner is chosen

For a given Metaverse attribute, JIM evaluates every contributing import rule in priority order (1 is highest):

- **The first contributor with a value wins.**<br /> Lower-priority contributors are not consulted.
- **A rule with no opinion is skipped.**<br /> If a rule does not apply to the object (it is disabled, no object from its Connected System is joined, or the joined object is out of the rule's scope), it is passed over and the next priority is considered.
- **If nobody contributes, the attribute is left unset.**

For example, a Metaverse Object drawing data from two source systems:

- HR system provides `First Name` and `Last Name` (priority 1: authoritative)
- Badge system also provides `First Name` (priority 2: secondary)

The HR system's `First Name` wins because its contribution ranks higher; the badge system only fills in where HR has no opinion.

### Null is a value

By default, if the highest-priority source has **no** value for an attribute, JIM falls through to the next source. That is usually right, but not always: when the authoritative source deliberately *clears* a value, you want the clear to propagate, not to be back-filled from a stale secondary copy that still holds the old value.

Enabling **Null is a value** on a contributor changes that. If the contributor is connected and in scope but supplies no value, JIM stops there and asserts "no value": the attribute is cleared downstream, and lower-priority sources are not consulted. This is distinct from a rule having no opinion; a rule that does not apply to the object is still skipped regardless of this setting.

Typical uses are a manager or department cleared at the authoritative source that must propagate as a clear, and a primary-system migration where the new system is authoritative for the people it knows about (including their blanks). It is deliberately powerful: a misbehaving priority-1 import (an empty file, a truncated delta) becomes a mass-clearing event rather than a harmless no-op, so treat Null is a value as an authoritative, considered choice.

### Configuring priority

Attribute Priority is configured per (Metaverse Object Type, attribute), not on the Synchronisation Rule editor. Open the Metaverse Object Type (**Administration → Schema → Object Types**), select the **Attributes** tab, and expand the **contributors** list for any attribute that has more than one contributor. From there you drag contributors to reorder them and toggle **Null is a value** per contributor. A newly added import mapping joins at the **lowest** priority, so a new source never silently takes over an attribute until you promote it explicitly. The same configuration is available via the [PowerShell cmdlets](../powershell/metaverse-attributes.md#get-jimmetaverseattributepriority) and the REST API.

> **Full detail:** [Attribute Priority](../concepts/attribute-priority.md) covers re-election when a winning source disconnects or withdraws, multi-valued attribute semantics, per-value provenance, and how to see resolution decisions in [Synchronisation Activities](activities.md).
