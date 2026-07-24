---
title: Metaverse
---

# Metaverse

The **metaverse** is JIM's central identity store. It contains object types (the schema), attributes (the fields), and the identity objects themselves. All synchronisation flows through the metaverse: import rules bring data in from [Connected Systems](connected-systems.md), and export rules push data out.

The metaverse schema is administrator-defined. JIM does not impose a fixed schema, so you can model any identity domain that fits your organisation, from the conventional `person` and `group` types through to bespoke types like `serviceAccount`, `mailbox`, or `device`.

--8<-- "assets/diagrams/metaverse-anatomy.svg"

<p class="jim-diagram-caption">An illustrative Metaverse. Object Types define the Attributes their Objects carry; Objects are the identity records, joined to their Connected System Objects by connector links.<span class="jimdg-caption-motion"> Moving dots trace identity data crossing the links during synchronisation.</span></p>

## Object types

**Object types** define the schema categories in the metaverse. Typical examples are `person` and `group`, but you can define any types your organisation needs. Each object type has its own attribute set and configurable deletion behaviour.

### Deletion behaviour

Each object type has its own rules for when its objects should be deleted from the metaverse. Common choices:

- **Immediate**<br /> The object is deleted as soon as all its connector links are removed.
- **Grace period**<br /> The object enters a pending-deletion state and is removed after a configurable period, giving administrators time to intervene if a deletion was triggered in error.

The grace period is the right default for production: it protects against transient source-system glitches that would otherwise wipe identities out.

### Custom object types

Alongside JIM's built-in `User` and `Group` types, administrators can create their own **custom object types** to model whatever categories their organisation needs (for example `Device`, `Room`, or `Contract`). Manage them from the **Object Types** tab of the Schema area, or via [PowerShell](../powershell/metaverse.md) and the [REST API](../../api/reference/).

- **Naming**<br /> Names and plural names are unique and compared case-insensitively; the portal validates both as you type. Names are shown exactly as you entered them.
- **Create with attributes**<br /> When creating a type you can optionally bind existing attributes to it there and then, or add them later from the type's **Attributes** tab.
- **Icon**<br /> An optional MudBlazor icon name (for example `Devices`) gives the type a recognisable glyph throughout the portal.
- **Rename and re-icon**<br /> Edit a custom type's name, plural name and icon from the Edit action on its row in the Object Types tab, or from the Edit button on its detail page.
- **Built-in protection**<br /> The `User` and `Group` types cannot be renamed, re-iconed or deleted; their deletion rules remain editable.

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

JIM's built-in attributes use **friendly, standard-neutral names** (`First Name`, `Job Title`, `Email`) rather than adopting the naming conventions of any one directory or provisioning standard, so the same schema reads naturally whether your identities come from Active Directory, an HR system, or a SCIM client. The built-in set covers the common identity domain, and includes attributes that make SCIM 2.0 resources easy to map, for example the multi-valued `Emails`, the boolean `Account Enabled` (the natural home for SCIM's `active` flag), `Nickname`, `Preferred Language`, `Locale`, `Time Zone`, `Middle Name`, `Honorific Prefix`, and `Honorific Suffix`.

Built-in attributes are read-only and cannot be deleted, and JIM looks after them for you: when an upgrade introduces new built-in attributes, they are added to your deployment automatically at service startup, with no administrator action needed.

### Standard Mappings

Every built-in attribute documents how it corresponds to its counterparts in the SCIM 2.0 and LDAP/Active Directory standards, so when you connect a system that speaks either standard you can see at a glance which Metaverse Attribute to target. Where the correspondence needs care, a note explains it: for example, SCIM's `active` maps to `Account Enabled`, while Active Directory's `userAccountControl` needs a transform rather than a direct flow.

Standard Mappings are **for guidance only**; they never affect synchronisation. What flows between your systems is always exactly what your [Attribute Flows](synchronisation-rules.md) say, nothing more. Over time the mappings will also power hints in the Attribute Flow editor, suggested default flows in connector wizards, and schema documentation.

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

**Objects** are the identity records: a single `person`, `group`, or whatever object types you have defined. Each object has a type, attribute values, and may be linked to one or more Connected System Objects in Connected Systems. Those links are how data flows between the external systems and the metaverse during synchronisation.

## Change history

Schema changes are recorded in [configuration change history](activities.md#configuration-change-history): creating, renaming or re-iconing an Object Type, changing an Object Type's deletion rules, deleting an Object Type, creating an Attribute, updating an Attribute's definition or its Object Type associations, and deleting an Attribute all capture a versioned snapshot alongside who made the change, when, and an optional reason.

Open an Object Type's history from the Changes tab on its detail page; open an Attribute's history from the history button on its row in the Schema area or on an Object Type's Attributes tab. When saving deletion rules in the admin portal, an optional "Reason for change" prompt lets you record why. Automation can pass the same reason via `-ChangeReason` on the Metaverse write cmdlets, or retrieve history with `Get-JIMConfigurationChangeHistory -Type MetaverseObjectType` / `-Type MetaverseAttribute` or the REST API.

## Pending deletions

Pending deletions track Metaverse Objects awaiting final deletion after all their connector space links have been removed. The grace period (configured per object type) gives administrators time to intervene before deletion is finalised.

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
