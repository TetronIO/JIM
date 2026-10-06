---
title: Metaverse
---

# Metaverse

The **metaverse** is JIM's central identity store. It contains object types (the schema), attributes (the fields), and the Metaverse Objects themselves. All synchronisation flows through the metaverse: import rules bring data in from [Connected Systems](connected-systems.md), and export rules push data out.

The metaverse schema is administrator-defined. JIM does not impose a fixed schema, so you can model any identity domain that fits your organisation, from the conventional `person` and `group` types through to bespoke types like `serviceAccount`, `mailbox`, or `device`.

--8<-- "assets/diagrams/metaverse-anatomy.svg"

<p class="jim-diagram-caption">An illustrative Metaverse. Object Types define the Attributes their Objects carry; Objects are Metaverse Objects, joined to their Connected System Objects by connector links.<span class="jimdg-caption-motion"> Moving dots trace identity data crossing the links during synchronisation.</span></p>

## Object types

**Object types** define the schema categories in the metaverse. Typical examples are `person` and `group`, but you can define any types your organisation needs. Each object type has its own attribute set and configurable deletion behaviour.

### Deletion behaviour

Each object type has its own rules for when its objects should be deleted from the metaverse, configured on the Object Type's detail page (and equally via [PowerShell](../powershell/metaverse.md) and the [REST API](../../api/reference/)). Three Deletion Rules are available:

- **Manual**<br /> Objects are never automatically deleted; an administrator must remove them.
- **When Last Connector Disconnected**<br /> Objects are deleted once no Connected System Objects remain linked to them.
- **When Authoritative Source Disconnected**<br /> Objects are deleted when the authoritative source system(s) you select disconnect, even while target-system links remain. This is the usual choice for source-to-target topologies, where an HR system leaving should deprovision the Metaverse Object everywhere.

An object can also reach zero connectors without any single disconnection event triggering the rule, most commonly after a [Connector Space clear](connected-systems.md#clearing-the-connector-space) whose objects never returned. JIM finds these on every stranded-value sweep, and applies the rule from state rather than from an event, for **When Last Connector Disconnected** and for **When Authoritative Source Disconnected** in **All sources disconnect** mode; **Specific source(s) disconnect** is event-only and is not reached this way, because state alone cannot tell a listed source's departure from one that never joined. The [Pending Deletions](#pending-deletions) list attributes these as "No connector remains" rather than naming a system.

!!! warning "Provisioned target accounts count as connectors"
    Under **When Last Connector Disconnected**, an account JIM has provisioned to a target system is a connector like any other. An object of a type with provisioning export Synchronisation Rules therefore outlives its last source while a target account exists, and the departed source's attribute values are preserved on it as last known state rather than recalled (see [when the winning source disconnects](../concepts/attribute-priority.md#when-the-winning-source-disconnects-or-withdraws)). Nothing is lost, but nothing is deprovisioned either. If the departure of your source of record should remove the target accounts, use **When Authoritative Source Disconnected** and list the source system(s). JIM shows this advisory on the Object Type's Deletion Rules panel when the combination applies, returns it on the REST object type responses, and `New-JIMMetaverseObjectType` and `Set-JIMMetaverseObjectType` surface it as a warning.

#### Authoritative source trigger modes

When the rule is **When Authoritative Source Disconnected**, a **Deletion Trigger** choice governs how the selected sources trigger deletion:

- **All sources disconnect**<br /> Delete only once every selected source has disconnected. Resilient to a single system failing or being rebuilt; while any selected source still holds a link, the object is kept. This is the default for newly configured object types.
- **Specific source(s) disconnect**<br /> Delete when any one of the selected sources disconnects, even if others remain connected.

Systems you do not select as sources (typically targets) never block or trigger deletion in either mode. At least one source must be selected, and only contributing systems (systems with inbound Synchronisation Rules for the object type) are offered. A live summary beneath the settings restates the configured behaviour in plain language before you save. Configurations created before trigger modes existed keep the **Specific source(s) disconnect** behaviour they were built with; nothing changes on upgrade.

!!! warning "Projecting systems that are not authoritative sources"
    The authoritative sources are a list you choose, so a Connected System onboarded later is not on it until you add it. That is exactly right for a system whose inbound Synchronisation Rules only **join** (projection off): it contributes attributes without governing lifecycle, an authoritative source such as HR leaving still deletes the object, and leaving it unselected is the recommended way to add an attribute-only contributor.

    A system that **projects** into the object type is different. Objects it creates that no selected source also holds are never deleted automatically, and objects it shares with a selected source are deleted when that source disconnects, even though the projecting system still holds them. Select it as an authoritative source, or turn projection off if it should only contribute attributes.

    JIM points this out in four places. The Authoritative Sources list marks each system **Projects** or **Joins only**, and a warning beneath it names every projecting system left unselected (with an extra line when no grace period is set, since a deletion would then be immediate). Saving a Synchronisation Rule that newly projects into the object type from such a system (a new projecting rule, enabling one, or switching projection on) asks you to confirm first. The REST object type responses list these systems as `deletionSourceWarnings`, and the Synchronisation Rule create and update responses carry `deletionSourceWarning`. `New-JIMMetaverseObjectType`, `Set-JIMMetaverseObjectType`, `New-JIMSyncRule` and `Set-JIMSyncRule` write them as warnings, and `Get-JIMMetaverseObjectType -Name` returns the list for health-check scripts. None of these blocks a save.

#### Grace period

Rather than deleting immediately when the Deletion Rule triggers, a configurable **grace period** holds the object in a pending-deletion state first, giving administrators time to intervene if a deletion was triggered in error. The grace period is the right default for production: it protects against transient source-system glitches that would otherwise wipe Metaverse Objects out.

If the Metaverse Object reappears during the grace period, the scheduled deletion is cancelled; but only when the reappearance undoes what triggered it. Under **When Last Connector Disconnected**, any system reconnecting cancels. Under **When Authoritative Source Disconnected**, a reconnection from any selected source cancels in All sources mode, while in Specific mode only the system whose disconnection scheduled the deletion cancels it. An unrelated system reconnecting never rescues an object whose trigger condition still holds.

When a reconnection cancels a scheduled deletion, JIM records it on the reconnecting Connected System Object's [Lineage](activities.md#execution-items): the system that rejoined, when the deletion had been due, and the Deletion Rule that permitted the cancellation. Without this, an administrator reading the Lineage would see a deletion scheduled and then nothing, with no way to tell whether it was cancelled or simply has not run yet.

#### Previewing a deletion settings change

Deletion settings are the one change in JIM that can make existing Metaverse Objects eligible for deletion the moment it is saved, with no synchronisation run in between. **Preview Changes**, beside Save on the Object Type's Deletion Rules panel, answers what the change would actually do before you make it.

The preview evaluates the objects already marked for deletion (those whose last, or authoritative, connector has gone) twice: once under the settings in force and once under the ones you have entered. Where the two answers differ, it reports which of three things would happen:

- **Would become eligible for deletion**<br /> The object is safe today and would be eligible under the proposed settings. This is the number to read before saving.
- **Would cease to be eligible for deletion**<br /> The object is eligible today and would stop being so.
- **Deletion date would change**<br /> The object is neither eligible now nor under the proposal, but the date it becomes eligible on moves.

Objects carrying no disconnection mark cannot be affected by any settings change, so they are not evaluated; the preview is quick even on a large metaverse.

The authoritative sources and the trigger mode are deliberately not part of that evaluation, and the preview says so rather than reporting a misleading zero. They are read at the moment a Connected System Object disconnects, not by the housekeeping pass that acts on objects already marked, so changing them moves no object's deletion date today; what they change is what happens the next time something disconnects.

Saving after a preview states its counts on the confirmation and records the preview against the change's [Activity](activities.md). See [Configuration changes](configuration-changes.md#previewing-a-change-before-you-make-it) for how previews work generally, and the [preview cmdlets](../powershell/previews.md) for the same evaluation from PowerShell.

### Custom object types

Alongside JIM's built-in `User` and `Group` types, administrators can create their own **custom object types** to model whatever categories their organisation needs (for example `Device`, `Room`, or `Contract`). Manage them from the **Object Types** tab of the Schema area, or via [PowerShell](../powershell/metaverse.md) and the [REST API](../../api/reference/).

- **Naming**<br /> Names and plural names are unique and compared case-insensitively; the portal validates both as you type. Names are shown exactly as you entered them.
- **Create with attributes**<br /> When creating a type you can optionally bind existing attributes to it there and then, or add them later from the type's **Attributes** tab.
- **Icon**<br /> An optional MudBlazor icon name (for example `Devices`) gives the type a recognisable glyph throughout the portal.
- **Rename and re-icon**<br /> Edit a custom type's name, plural name and icon from the Edit action on its row in the Object Types tab, or from the Edit button on its detail page.
- **Built-in protection**<br /> The `User` and `Group` types cannot be renamed, re-iconed or deleted; their deletion rules remain editable.

Above the list, filters narrow it by Deletion Rule, whether the type has Predefined Searches, and whether it is built-in; the Attributes tab carries the equivalent filters for Type, Plurality, Built-in status and bound Object Type.

### Deleting object types

Deleting a custom object type has two hard blocks, because either would otherwise be silently destroyed with the type:

- **Metaverse Objects of the type**<br /> If any object of the type exists, deletion is refused and the portal reports the count. Delete those objects first (for example by stopping the source flows and letting them deprovision).
- **Synchronisation Rules targeting the type**<br /> If any Synchronisation Rule targets the type, deletion is refused and the rules are listed. Remove those Synchronisation Rules first.

Once both are clear, the type can be deleted. Its softer references (its Predefined Searches, Example Data Template entries, and attribute bindings) are **cascade-removed** as a single operation behind a type-the-name confirmation; the bound attributes themselves are kept. The cascade is fully audited, with each removed reference recorded as a child Activity of the deletion.

## Attributes

**Attributes** define the fields available on Metaverse Objects. Examples include `displayName`, `mail`, and `employeeId`. Attributes can be:

- **Single-valued or multi-valued**<br /> A multi-valued attribute holds a list of values (e.g. group memberships, email aliases).
- **Of various data types**<br /> String, integer, datetime, boolean, reference (a link to another Metaverse Object), and so on.

Attributes are scoped to the object types that use them: an attribute is **bound** to one or more Object Types, and only appears on objects of those types. The same attribute name can carry different meanings on different object types if you genuinely need that, though in practice most attributes are reused identically across types where they apply.

### Built-in attributes

JIM's built-in attributes use **friendly, standard-neutral names** (`First Name`, `Job Title`, `Email`) rather than adopting the naming conventions of any one directory or provisioning standard, so the same schema reads naturally whether your Metaverse Objects come from Active Directory, an HR system, or a SCIM client. The built-in set covers the common identity domain, and includes attributes that make SCIM 2.0 resources easy to map, for example the multi-valued `Emails`, the boolean `Account Enabled` (the natural home for SCIM's `active` flag), `Nickname`, `Preferred Language`, `Locale`, `Time Zone`, `Middle Name`, `Honorific Prefix`, and `Honorific Suffix`.

Built-in attributes are read-only and cannot be deleted, and JIM looks after them for you: when an upgrade introduces new built-in attributes, they are added to your deployment automatically at service startup, with no administrator action needed.

### Standard Mappings

Every built-in attribute documents how it corresponds to its counterparts in the SCIM 2.0 and LDAP/Active Directory standards, so when you connect a system that speaks either standard you can see at a glance which Metaverse Attribute to target. Where the correspondence needs care, a note explains it: for example, SCIM's `active` maps to `Account Enabled`, while Active Directory's `userAccountControl` needs a transform rather than a direct flow.

Standard Mappings are **for guidance only**; they never affect synchronisation. What flows between your systems is always exactly what your [Attribute Flows](synchronisation-rules.md) say, nothing more.

Where they earn their keep is the [Attribute Flow editor](synchronisation-rules.md#standard-mapping-hints): it shows each Metaverse Attribute's counterpart name beside it in the pickers, and names the attribute the standard says your chosen source corresponds to. Suggestions only; every attribute stays selectable, and an attribute with no counterpart is not flagged as a problem. Over time the mappings will also power suggested default flows in connector wizards, and schema documentation.

View a built-in attribute's Standard Mappings from the view action on its row in the Schema area's **Attributes** tab; JIM keeps them up to date automatically. Custom attributes can carry your own Standard Mappings too: add, edit or remove them from the attribute's edit dialog, the REST API (the attribute update endpoint), or PowerShell (`Set-JIMMetaverseAttribute -StandardMappings`), so scripted configuration can record them alongside the attributes themselves. Changes are audited in the attribute's [configuration change history](activities.md#configuration-change-history), and the mappings are returned by the REST API's attribute detail endpoint and `Get-JIMMetaverseAttribute`.

### Custom attributes

Alongside JIM's built-in attributes (which are read-only and cannot be deleted), administrators can create their own **custom attributes** to model organisation-specific data such as `costCentre` or `buildingCode`. Manage them from the **Attributes** tab of the Schema area, or via [PowerShell](../powershell/metaverse.md) and the [REST API](../../api/reference/).

- **Naming**<br /> Attribute names are unique and compared case-insensitively, so "CostCentre" is rejected if "costCentre" already exists. The portal validates the name as you type. Names are always shown exactly as you entered them.
- **Create and bind in one step**<br /> When creating an attribute you can optionally bind it to zero or more Object Types there and then; leave the binding empty to create it unbound and assign it later. An unbound attribute collects no data until it is bound, which the list flags as "Not assigned".
- **Bindings**<br /> Bind an existing attribute to an Object Type from that Object Type's **Attributes** tab (the "Add Attribute" picker), or unbind it with the row's remove action. Built-in attributes cannot be re-bound or unbound.
- **Rendering**<br /> Multi-valued attributes can carry a rendering hint (default, table, chip set, or list) controlling how their values display on object detail pages.

### Deleting attributes and removing bindings

Deleting a custom attribute, or removing one of its Object Type bindings, follows one rule: **the only hard block is stored data**. If any Metaverse Object holds a value for the attribute, the action is refused and the portal tells you how many objects are affected, so you clear the values first (for example by stopping the source flow and letting it deprovision).

When no values exist, the action is allowed even if configuration still references the attribute. Those references (the binding itself, Attribute Flows, scoping criteria, and Object Matching Rules) are **cascade-removed** as a single operation, in dependency order so nothing is left dangling. Because this changes your synchronisation configuration, it is guarded by a type-the-name confirmation, exactly as connector-space deletion is. The cascade is fully audited: each removed reference is recorded as a child Activity of the deletion.

## Objects

**Objects** are Metaverse Objects: a single `person`, `group`, or whatever object types you have defined. Each object has a type, attribute values, and may be linked to one or more Connected System Objects in Connected Systems. Those links are how data flows between the external systems and the metaverse during synchronisation.

### Where a value comes from

A Metaverse Object's **Details** tab offers three views: **Form**, **Tabs** and **Inspect**. Form and Tabs show the values. **Inspect** shows where each value comes from, and is the view to use when a value looks wrong. You can also open it directly with `?view=inspect` on the object's address.

- **Where this {Object Type} gets its values**<br /> A bar at the top shows each source's share of the object's attributes, with a key underneath. Select a source in the key to filter the table to it; select it again to clear the filter. When every attribute comes from one source, the bar gives way to a single sentence naming it.
- **Source**<br /> Each attribute's row names its source, with a dot in the same colour as that source's share of the bar: the Connected System and the Synchronisation Rule that contributed the value, or **Source not recorded** for values set before JIM recorded their source. A rule deleted since it contributed the value reads "rule deleted", and the Connected System is still named. A value an Attribute Flow generated (see [Generated values](synchronisation-rules.md#generated-values)) ends in **Generated Value**, for example "HR · HR Import Users · Generated Value", and has its own share of the bar. A multi-valued attribute whose values came from more than one source reads **Several sources**. Hover over a source to read it in full; the inspector links to the Connected System and the Synchronisation Rule.
- **Group by**<br /> Group the attributes by **Source**, to see everything one Synchronisation Rule contributes to this object (a header names each source, in place of the Source column), or by **Category**. JIM remembers your choice.

Select an attribute to open its inspector beside the table. The inspector answers "why is this value what it is":

- **Current value**<br /> The value, its source (the Connected System and Synchronisation Rule it came from), the Connected System Object that supplied it, and the Activity that last set it. For a generated value, a **Source type** of **Generated Value** replaces the Connected System Object, since the Attribute Flow produced the value rather than reading it. A source that positively asserts there is no value (see [Null is a value](../concepts/attribute-priority.md#null-is-a-value)) is shown as asserting no value.
- **Every source**<br /> Every Synchronisation Rule that can contribute the attribute, in [priority order](../concepts/attribute-priority.md), with the value each would supply for this object today (for a Generated Value Attribute Flow, the value it generated). The one supplying the current value is marked **In use**; a source with a value that lost on priority is marked **Outranked**; a joined source with nothing to supply is marked **No value**. A source whose object is not joined, whose Synchronisation Rule or Attribute Flow is disabled, or whose value cannot be worked out outside a synchronisation run says so. **Change priority** opens the Metaverse Object Type, where the order is set.
- **History**<br /> The attribute's most recent changes (up to 50), newest first, with the Activity and Synchronisation Rule behind each. **Open in Timeline** takes you to the object's **Changes** tab for its full change history.

Nothing in the Inspect view changes data: working out what each source would supply reads the joined Connected System Objects and evaluates each Attribute Flow without writing anything.

The same information is available through the REST API (`GET /api/v1/metaverse/objects/{id}/provenance`, and `.../attributes/{attributeId}/provenance` for one attribute; see the [interactive API reference](../../api/reference/)) and PowerShell ([`Get-JIMMetaverseObjectProvenance`](../powershell/metaverse.md#get-jimmetaverseobjectprovenance)).

A value's source is not only in the Inspect view: the object's **Changes** tab names each attribute change's source alongside the change itself, and a [Pending Export's detail page](connected-systems.md#pending-exports) shows both where the outbound value came from and which Attribute Flow staged it. Both link back into Inspect so you can jump straight from a change or a queued export to the full picture.

### Why it is connected, and why it is not

A Metaverse Object's **Connections** tab lists every Connected System Object joined to it, with its role, its state and a per-row **Preview Sync** (see [Sync Preview](sync-preview.md)). It also answers the two questions a helpdesk asks most: why does this person have an account in that system, and why don't they have one in this one? The tab is available to Administrators.

**Why it is connected.** Expand a connection's row to see:

- **How it joined**<br /> One sentence naming how the Connected System Object came to be joined and which Synchronisation Rule was responsible: projected by an import rule, provisioned by an export rule, joined by matching under an import rule, joined by matching on the Connected System's own Object Matching Rules (no rule is responsible), or joined by an export rule that found an existing object instead of provisioning a new one. The date it joined follows, with links to the Activity that made the join and to the rule. JIM records this at the moment of joining. For objects joined before JIM recorded it, the rule is taken from Activity history while that history is still kept, marked **Taken from Activity history**; where neither source holds it, the sentence says it was not recorded rather than guessing. A rule deleted since is still named, followed by "(since deleted)".
- **Scoping**<br /> Each enabled Synchronisation Rule relevant to the connection, evaluated against current values: import rules against the Connected System Object's values for a source, export rules against the Metaverse Object's values for a target. It is labelled with the time it was evaluated, because it answers whether the connection is in scope now, not why it joined. A connection that is now out of scope of a rule (an import rule set to keep the join, or an export rule whose deprovisioning is under way) shows that rule as **Not in scope**, with the criteria it fails.
- **Object Type conflicts**<br /> Another enabled export rule that targets the same Connected System for a different object type cannot connect this Metaverse Object, because a Metaverse Object has one Connected System Object per Connected System and this connection already holds it. The row says so, naming the rule.

**Why it is not connected.** Below the table, the **Not connected** section lists every enabled export Synchronisation Rule for the Metaverse Object's type whose Connected System holds no object joined to it. It is collapsed until you open it; its heading counts the entries. When there are none it says the object is connected everywhere an enabled export rule targets. Rules on a Connected System being deleted are left out; rules on a disabled Connected System are listed, and their hint says the Connected System is disabled. A connection still being provisioned (**Pending export** or **Awaiting confirmation**) is already a row in the table above, so it never appears here.

Each entry shows the Connected System, the rule, one reason and a one-line hint naming what it fails on, without values:

| Reason | What it means | What its expansion shows |
|---|---|---|
| **Not in scope** | The object fails the rule's scoping criteria. | **To come into scope**: each failing condition in words, with the current value, then the rule's criteria evaluated. |
| **Rule doesn't provision** | The object is in scope, but the rule's **Provision to Connected System** is off and no object exists to join. | What would connect it: switch provisioning on, or create an object in the Connected System that joins. |
| **Rule misconfigured** | A criterion uses a comparison its attribute's type cannot use, so the rule's scoping cannot be evaluated. | The criterion, and that it needs correcting or removing in the rule. |
| **Not yet provisioned** | The object is in scope and the rule provisions, but nothing has been staged. | When the object is marked for an export scope review (as every object of the type is after a change to the rule that can bring objects into scope), the next synchronisation of any Connected System stages its provisioning, and the hint says so. Otherwise provisioning is staged the next time the object's attribute values change, or when the rule is next saved with such a change; see [When a change to an export rule takes effect](synchronisation-rules.md#when-a-change-to-an-export-rule-takes-effect). |

**Copy summary** on an entry puts a plain-text explanation on the clipboard, ready for a ticket or a message: the object's name, the Connected System, the reason, the rule, the bullets and the time it was evaluated in UTC, so it reads the same wherever it is pasted. You do not need to expand the entry first. Where your browser does not allow JIM to use the clipboard (over plain HTTP, for example), a dialog shows the summary already selected, ready to copy.

**Reading the criteria.** The criteria are drawn as the rule defines them: each group says whether all or any of its conditions must be met, and whether it was. Each condition shows what the object held and its outcome: **Met**, **Not met**, **No value** (an attribute with no value fails every comparison except one that requires no value), a condition that references no attribute (for example, after its attribute was deleted), or one that cannot be evaluated. A rule with several top-level groups is in scope when any one of them is met, and the explanation presents them as alternatives. Relative dates resolve to the instant shown. Where an attribute holds several values, the condition shows the value it compared and says how many more there were. Values of credential attributes are never shown.

Nothing on the Connections tab changes data: the explanations are evaluated in memory against values already stored.

The same explanations, in the same words, are available through the REST API (`GET /api/v1/metaverse/objects/{id}/connections`, adding `?includeNotConnected=true` for the Not connected entries; see the [interactive API reference](../../api/reference/)) and PowerShell ([`Get-JIMMetaverseObjectConnection`](../powershell/metaverse.md#get-jimmetaverseobjectconnection)).

## Confirming a configuration change

Changing an object type's deletion behaviour, or an attribute's data type or plurality, is confirmed before it saves. Deletion settings are the one place in JIM where saving alone can make existing Metaverse Objects eligible for deletion, with no synchronisation run in between; the confirmation says so. See [Configuration changes](configuration-changes.md).

## Change history

Schema changes are recorded in [configuration change history](activities.md#configuration-change-history): creating, renaming or re-iconing an Object Type, changing an Object Type's deletion rules, deleting an Object Type, creating an Attribute, updating an Attribute's definition or its Object Type associations, and deleting an Attribute all capture a versioned snapshot alongside who made the change, when, and an optional reason.

Open an Object Type's history from the Changes tab on its detail page; open an Attribute's history from the history button on its row in the Schema area or on an Object Type's Attributes tab. When saving deletion rules in the admin portal, an optional "Reason for change" prompt lets you record why. Automation can pass the same reason via `-ChangeReason` on the Metaverse write cmdlets, or retrieve history with `Get-JIMConfigurationChangeHistory -Type MetaverseObjectType` / `-Type MetaverseAttribute` or the REST API.

## Pending deletions

Pending deletions track Metaverse Objects awaiting final deletion: objects whose last connector space link has been removed, and objects scheduled for deletion by an authoritative source disconnecting (which may still hold target-system links during their grace period). The grace period (configured per object type) gives administrators time to intervene before deletion is finalised.

The Pending Deletions page shows each object's status, when it becomes eligible for deletion, and a **Triggered By** column naming the Connected System whose disconnection scheduled the deletion, recorded at the moment the deletion was scheduled, so it stays accurate even if that system is later renamed or removed. An object the [stranded-value sweep](connected-systems.md#clearing-the-connector-space) found with no connector at all reads "No connector remains" in that column instead: no single system's disconnection triggered the decision, so naming one would be misleading rather than merely absent.

JIM exposes both the list of currently pending deletions and a summary view, which is useful for spotting unexpected mass-deletion events early.

## Searching the metaverse

The metaverse supports filtering and a fast named-search API. The named-search API is driven by [predefined searches](predefined-searches.md), which let administrators create reusable search definitions that the portal and integrations can call by URI.

### Search by attribute presence

You can filter a Metaverse Object Type's list down to just the objects that **hold a value for a given Metaverse Attribute**. An object matches when it holds at least one value for the named attribute; a multi-valued attribute counts once, however many values it carries. This is the same population the deletion safeguards report as "objects with a value", so the [Object Type](#deleting-object-types) and [attribute](#deleting-attributes-and-removing-bindings) deletion and unassign flows link straight to this filter to show an administrator exactly which objects are blocking a destructive action.

The attribute name is matched case-insensitively. An unrecognised name is not an error: it simply returns no objects, which the portal shows as a clear empty state.

The filter is available with the same behaviour across all three interfaces:

- **JIM portal**<br /> Open the object list with a `hasAttribute:` search, for example `/t/users?search=hasAttribute:costCentre` (the path uses the Object Type's plural name). The active filter appears as a chip above the list; clear the chip to return to the unfiltered list.
- **REST API**<br /> Add the optional `hasAttribute={attributeName}` query-string parameter to the named-search endpoint, for example `GET /api/v1/metaverse/objects/search/users?hasAttribute=costCentre`. See the [interactive API reference](../../api/reference/).
- **PowerShell**<br /> Pass `-HasAttribute` to [`Search-JIMMetaverseObject`](../powershell/metaverse.md#search-jimmetaverseobject), for example `Search-JIMMetaverseObject -PredefinedSearchUri "users" -HasAttribute "costCentre"`.

## Manage the metaverse

- **JIM portal**<br /> Metaverse area of the admin UI for objects, object types, attributes, and pending deletions
- **PowerShell**<br /> [Metaverse cmdlets](../powershell/metaverse.md) (`Get-JIMMetaverseObject`, `Get-JIMMetaverseObjectType`, `Get-JIMMetaverseAttribute`, etc.)
- **REST API**<br /> Metaverse endpoints in the [interactive API reference](../../api/reference/)

## See also

- [Concepts: Architecture](../concepts/architecture.md) -- how the metaverse fits into JIM's hub-and-spoke architecture
- [Concepts: Synchronisation Pipeline](../concepts/synchronisation-pipeline.md) -- how data flows through the metaverse during import, sync, and export
- [Concepts: JML Lifecycle](../concepts/jml-lifecycle.md) -- joiner/mover/leaver lifecycle and how it relates to Metaverse Object state
- [Synchronisation Rules](synchronisation-rules.md) -- how data flows in and out of the metaverse
- [Predefined Searches](predefined-searches.md) -- named, reusable searches over Metaverse Objects
