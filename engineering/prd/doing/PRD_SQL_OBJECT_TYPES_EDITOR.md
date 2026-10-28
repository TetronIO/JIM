# SQL Connector: Object Types Editor

- **Status:** Doing (Phases 1-2 of 7 complete)
- **Created:** 2026-10-28
- **Author:** Jay
- **Issue:** #2570
- **Plan:** [`SQL_OBJECT_TYPES_EDITOR.md`](../../plans/doing/SQL_OBJECT_TYPES_EDITOR.md)

## Problem Statement

The SQL Connector needs to know things a database catalogue cannot tell it: which table or view holds each kind of object, which column or columns identify a row, which columns hold another object's key, and which child tables hold multi-valued attributes. Today the administrator supplies all of this by hand-writing a JSON document into the **Object Types** setting.

That works for REST and PowerShell, where a document is the natural shape, but it is a poor experience in the portal:

- **The administrator types names JIM already knows.** Schemas, tables, views and columns are all in the catalogue the connector reads during schema import, yet each has to be typed exactly, and a typo surfaces only when the setting is saved.
- **Errors arrive one at a time, on save.** There is no guidance while writing, only a rejection afterwards.
- **Foreign-key knowledge is stranded.** The connector already detects declared foreign keys that line up with another Object Type's anchor (`SqlConnectorSchema.AddForeignKeySuggestionsAsync`), but it can only say so in an attribute's Description on the Schema tab, telling the administrator to go back and edit the JSON.
- **It invites a false mental model.** A raw document gives no hint that most columns need no configuration at all. Administrators reasonably assume every column must be listed, which is the confusion that prompted this work (the portal could not even render the setting until PR #2571).

## Goals

- An administrator can configure every SQL Connector Object Type from the portal without seeing or typing JSON, choosing schemas, tables, views and columns from the database catalogue rather than typing their names.
- The editor asks only about what JIM cannot work out for itself: the source, the anchor, references, related tables, and the Delta Import additions the chosen mode needs.
- Foreign-key suggestions are offered where the decision is made, and can be accepted in one action.
- The JSON document remains the stored format and the REST and PowerShell interchange format, unchanged, and the portal offers it as a view for pasting and copying.
- Configuration that the editor cannot represent is never silently lost.

## Non-Goals

- **No change to the Object Types document's shape or meaning.** The editor reads and writes the existing document; the parser and its validation (`SqlSchemaConfiguration`) stay the source of truth.
- **No change to REST or PowerShell configuration.** They keep sending the document as the setting's value.
- **No editor for other Connectors' settings.** This is SQL Connector specific. A generic "structured setting" framework is out of scope unless the plan finds it falls out naturally (see Open Questions).
- **No new type inference.** Column types, plurality and attribute overrides stay where they are today: discovered by schema import and adjusted on the Schema tab.
- **No automatic configuration.** JIM suggests (primary keys as anchors, foreign keys as references); it never applies a suggestion without the administrator accepting it.

## User Stories

1. As an administrator setting up a SQL Connected System, I want to pick my tables and key columns from lists, so that I do not have to know or type exact names.
2. As an administrator, I want JIM to tell me which columns look like references to my other Object Types, so that I can accept them instead of working them out from the database myself.
3. As an administrator, I want to see at a glance what each Object Type is configured with, so that I can review a Connected System without expanding everything.
4. As an administrator who keeps configuration as code, I want to paste or copy the whole document, so that I can move a configuration between environments or into a script.
5. As an administrator whose database JIM cannot currently reach, I want to still view and change the configuration, so that a connectivity problem does not lock me out of it.

## Requirements

### Functional Requirements

**Layout (Option B, "stacked cards", chosen from the mocks)**

1. The Object Types setting is edited in a dedicated section that lists one card per Object Type, in document order.
2. A collapsed card shows the Object Type's name, its source (`SCHEMA.TABLE`, or "SELECT statement"), and summary chips for its anchor and its references and multi-valued attributes (for example "Anchor GROUP_ID", "Members: Person").
3. An expanded card shows only the configurable parts, each as its own sub-section:
   1. **Name, schema, and table or view**, chosen from the catalogue (schema and table are drop-downs; the name is free text). A link switches the source to a `SELECT` statement, stating that such an Object Type can be imported but not exported to.
   2. **Anchor**: the anchor columns as removable chips, in key order, with an action to add a column chosen from the source's columns.
   3. **References to other Object Types**: one row per configured reference (column, the Object Type it points at, remove), and an action to add one.
   4. **Multi-valued attributes**: one row per related table summarising the attribute name, child table, value column, join columns and any referenced Object Type, with edit and remove actions and an action to add one.
   5. **Delta Import additions**, shown only when the Connected System's Delta Import Mode needs them: the watermark column on the source and on each related table (Watermark Column mode), or the change-log table, its anchor, sequence and change-type columns and the create, update and delete values (Change-Log Table mode).
4. Each expanded card states how many other columns there are and that they are discovered and typed automatically on the Schema tab. The editor never lists every column as though each needed a decision.

**Suggestions**

5. When a source column has a declared foreign key to the anchor of another Object Type in the document, and that column is not already a reference, the References sub-section shows the suggestion inline with an action to accept it. Accepting adds the reference; dismissing hides it for the rest of the editing session.
6. When a column has a declared foreign key to a table that is not yet any Object Type's source, the suggestion offers to add an Object Type for that table, opening the Add Object Type flow with the table preselected.
7. When the source declares a primary key, the Add Object Type flow preselects its columns as the anchor, in key order, and says why. Where the source has no primary key (a view, typically), nothing is preselected and the step says so.

**Adding an Object Type**

8. **Add Object Type** opens a dialog of four steps: Source, Anchor, References, Multi-valued attributes. Each step can be completed or skipped where the document allows it; the result appears as a new card, expanded.

**JSON view**

9. A **View as JSON** action shows the whole document, formatted, with a Copy action. The administrator can edit or paste a document there.
10. Invalid JSON in the JSON view is reported inline against the line it concerns, using the parser's own messages, and blocks both switching back to the editor and saving until it is fixed.
10a. Where a field name is not recognised but is close to one that is (`anchorColumn` for `anchorColumns`), the message suggests the intended name. Today the parser only rejects an unrecognised field, so this is a change to `SqlSchemaConfiguration`'s validation, not the portal, and REST and PowerShell callers get the same suggestion.
11. A document that is valid but uses something the editor cannot represent (if any such case exists by implementation time) opens in the JSON view with a message saying why, rather than being simplified by the editor.

**Saving and validation**

12. The editor writes the same document the setting stores today, and saving goes through the existing setting validation, so the editor can never save something REST or PowerShell would reject.
13. Problems the editor can detect without saving (an Object Type with no anchor, a reference to an Object Type that no longer exists, a related table with no join columns) are shown on the card they concern, and the card shows a problem indicator while collapsed.
14. Removing an Object Type that another Object Type references asks for confirmation and names the references that will be removed with it.

**Catalogue access**

15. The editor reads the catalogue (schemas, tables and views, columns with types, primary keys, foreign keys) through the Connector using the Connected System's connectivity settings as currently entered, as the LDAP Connector's Discover action does for directory servers (`IConnectorDirectoryServers`).
16. When the catalogue cannot be read (settings incomplete, server unreachable, permission denied), the editor still shows and edits the existing configuration, with schema, table and column fields falling back to free text, and a banner says why the lists are unavailable and offers a retry.
17. Catalogue reads follow the connector's least-privilege model: the account needs only what schema import already needs (`docs/connectors/jim-sql-connector-security.md`).

**Surface parity**

18. Catalogue browsing is a new read capability and ships on all three surfaces: the portal editor, a REST endpoint returning the catalogue for a Connected System, and a PowerShell cmdlet wrapping it, each with tests and docs. (The Object Types setting itself already has write parity through the setting value.)

### Non-Functional Requirements

- **Scale:** the editor stays responsive against a database with thousands of tables and tens of thousands of columns. Table lists are searchable and loaded per schema; column lists are loaded per table when a card or step needs them, never the whole catalogue up front.
- **Accessibility:** every control is a real form control with a label; cards are keyboard operable with `aria-expanded`; suggestion and error messages are announced; status is never conveyed by colour alone.
- **Security:** catalogue access is authorised like other Connected System administration; nothing read from the catalogue is logged beyond counts, and identifiers are validated and quoted as the connector already does.

## Examples and Scenarios

Mocks: the "Object Types Editor Mocks" canvas (Option B is the chosen layout; it also holds the Add Object Type dialog and the JSON view). It is private to its owner; ask for access if you cannot open it.

```
Object Types                                   [View as JSON] [+ Add Object Type]
2 Object Types. Select one to change it.

+----------------------------------------------------------------------------+
| (person) Person   HR.EMPLOYEES                                         ^   |
|----------------------------------------------------------------------------|
| Name [Person]   Schema [HR v]   Table or view [EMPLOYEES v]                |
|                                                                            |
| Anchor          (EMPLOYEE_ID x) [Add column]   The table's primary key.    |
|                                                                            |
| References to other Object Types                                           |
|   MANAGER_EMPLOYEE_ID  ->  Person                                   [del]  |
|   ! DEPARTMENT_ID has a foreign key to HR.DEPARTMENTS, which is not an     |
|     Object Type yet.   [Add Department Object Type]  [Dismiss]            |
|   [+ Add reference]                                                        |
|                                                                            |
| Multi-valued attributes                                                    |
|   PhoneNumbers  from HR.EMPLOYEE_PHONES.PHONE_NUMBER, joined on            |
|                 EMPLOYEE_ID                                   [edit] [del] |
|   [+ Add related table]                                                    |
|                                                                            |
| (i) The other 7 columns are discovered and typed automatically on the      |
|     Schema tab.                                                            |
+----------------------------------------------------------------------------+
+----------------------------------------------------------------------------+
| (group) Group   HR.GROUPS          [Anchor GROUP_ID] [Members: Person]  v  |
+----------------------------------------------------------------------------+
                                                   [Discard changes]  [Save]
```

### Scenario 1: First Object Type on a new Connected System

**Given**: a SQL Connected System whose connectivity settings are complete and correct, with no Object Types configured
**When**: the administrator selects **Add Object Type**, chooses schema `HR` and table `EMPLOYEES`, and moves to the Anchor step
**Then**: `EMPLOYEE_ID` (the table's primary key) is preselected with an explanation; finishing the dialog adds an expanded **Person** card, and saving stores the same document a hand-written `{"objectTypes":[{"name":"Person","schema":"HR","table":"EMPLOYEES","anchorColumns":["EMPLOYEE_ID"]}]}` would.

### Scenario 2: Accepting a foreign-key suggestion

**Given**: Person (`HR.EMPLOYEES`) and Group (`HR.GROUPS`) are configured, and `HR.GROUPS.OWNER_EMPLOYEE_ID` has a declared foreign key to `HR.EMPLOYEES.EMPLOYEE_ID`, Person's anchor
**When**: the administrator expands the Group card
**Then**: the References sub-section suggests `OWNER_EMPLOYEE_ID -> Person`; accepting it adds `{"name":"OWNER_EMPLOYEE_ID","referencesObjectType":"Person"}` to Group's `columns`.

### Scenario 3: Database unreachable

**Given**: a configured Connected System whose database is currently unreachable
**When**: the administrator opens the editor
**Then**: every card shows its existing configuration; schema, table and column fields are free text; a banner says the catalogue could not be read, with the reason and a **Retry** action; saving still works and is validated as today.

### Scenario 4: Pasting a document

**Given**: an administrator with a document exported from a test environment
**When**: they open **View as JSON**, paste it with a typo (`anchorColumn`), and try to return to the editor
**Then**: line 7 is marked with "Object Type Person has an unrecognised field `anchorColumn`. Did you mean `anchorColumns`?", and returning to the editor and saving stay blocked until it is fixed.

### Scenario 5: Watermark Delta Import

**Given**: the Connected System's Delta Import Mode is **Watermark Column**
**When**: the administrator expands an Object Type with a related table
**Then**: the card shows a watermark column choice for the source and for the related table, and a missing one is flagged on the card before saving, matching the existing save-time rule.

## Constraints

- Air-gap safe: no external services; the catalogue is read only from the configured database.
- No new third-party dependencies (a code editor component for the JSON view included) without the dependency governance process.
- British English, Title Case JIM entity names, no em dashes in UI text.
- Pre-release: no compatibility shims; the stored document format is unchanged, so none should be needed.

## Affected Areas

| Area | Impact |
|------|--------|
| Connector (`JIM.Connectors/Sql`) | Catalogue browsing exposed through a capability interface (schemas, tables/views, columns, primary keys, foreign keys), reusing `SqlCatalogueReader`; nearest-field suggestions in `SqlSchemaConfiguration` validation messages |
| Models | Capability interface and catalogue result models in `JIM.Models` |
| Application | Server method to read a Connected System's catalogue from draft or saved settings |
| API | New read endpoint for a Connected System's catalogue; DTOs |
| PowerShell | New cmdlet wrapping the catalogue endpoint, with Pester tests |
| UI | New Object Types editor component on the SQL Connected System's settings, replacing the raw Text field for this setting; Add Object Type dialog; JSON view |
| Database | None expected |
| Worker | None |

## Documentation Impact

| Doc | Change |
|------|--------|
| `docs/connectors/jim-sql-connector.md` | Rewrite Step 2 around the editor, keeping the document reference for REST and PowerShell |
| `docs/connectors/jim-sql-connector-delta-import-*.md` | Describe the Delta Import fields in the editor alongside the document fields |
| `docs/powershell/` and `docs/api/` | The new catalogue cmdlet and endpoint (generated reference plus any how-to) |
| `engineering/developer/connectors.md` | The new catalogue capability interface, if it is generic |

## Dependencies

- PR #2571 (merged): the Object Types setting renders in the portal, and settings carry an Example.
- The existing catalogue reader (`SqlCatalogueReader`) and foreign-key detection (`SqlConnectorSchema`).

## Open Questions

1. **Settings tab or Schema tab?** The mocks place the editor on the Settings tab, where the setting lives. It could instead open the Schema tab, since it decides what schema import reads. Recommendation: Settings, because it is a setting and REST/PowerShell treat it as one; the Schema tab links to it.
2. **Generic or SQL-specific capability?** Is catalogue browsing a SQL-only interface, or a general "browse the source's structure" capability other Connectors (File, future database engines) would implement? Recommendation: define it in `JIM.Models` as a generic interface with the SQL Connector as its first implementer, but only if the plan finds the shape holds for a second Connector.
3. **How does a Connector supply a custom editor for one setting?** The settings tab renders settings generically today. Options: a setting type for "structured document" with a Connector-provided schema, or a portal component keyed to the SQL Connector's setting name. Recommendation: decide in the plan; the second is simpler but couples the portal to one Connector.
4. **Keys through views.** Views declare no primary or foreign keys, so for an Object Type sourced from a view neither the anchor nor reference suggestions have anything to work from. Should the editor look through a simple view to its base table's keys, or only ever suggest from declared keys? The mocks assumed the former; the catalogue reader does not do it today.
5. **SELECT-statement Object Types.** The editor can hold the statement as text, but cannot browse its columns without running it. Is describing the result set (for example via `sp_describe_first_result_set` / Oracle's equivalent) acceptable, or do these Object Types stay JSON-only in the first release?

## Acceptance Criteria

- [ ] Every field of the Object Types document can be configured from the editor, or the JSON view is offered for any that cannot, with a message saying which.
- [ ] Schemas, tables, views and columns are chosen from the catalogue when it can be read, and typed when it cannot (Scenario 3).
- [ ] Primary-key and foreign-key suggestions appear and can be accepted (Scenarios 1 and 2).
- [ ] The JSON view shows, copies and accepts pasted documents, with inline validation that blocks saving (Scenario 4).
- [ ] A document saved from the editor is byte-for-byte what the parser expects, and round-trips through REST and PowerShell unchanged.
- [ ] Delta Import fields appear for the configured mode and are validated on the card (Scenario 5).
- [ ] Catalogue browsing is available through REST and PowerShell, with tests and docs.
- [ ] Works at the stated scale without loading the whole catalogue up front.
- [ ] Keyboard and screen reader accessible.

## Additional Context

- Issue #2570.
- PR #2571: made the Object Types setting renderable as a multi-line field with a copyable example, the interim fix this editor replaces for the portal.
- Options considered in the mocks: **A, list and detail** (rejected: its per-column grid lists every column, reinforcing the idea that each needs configuring, when typically only the anchor and a few references do) and **B, stacked cards** (chosen).
