# SQL Connector: Object Types Editor

- **Status:** Done
- **Issue:** [#2570](https://github.com/TetronIO/JIM/issues/2570)
- **PRD:** [`PRD_SQL_OBJECT_TYPES_EDITOR.md`](../../prd/done/PRD_SQL_OBJECT_TYPES_EDITOR.md)
- **Last Updated:** 2026-10-29

## Overview

Replace hand-writing the SQL Connector's **Object Types** JSON document in the portal with a catalogue-driven editor: one card per Object Type (Option B in the PRD), an Add Object Type dialog, and a JSON view. The document stays the stored format and the REST/PowerShell format. Browsing the database catalogue becomes a Connector capability exposed on all three surfaces.

Most of the database work already exists. The SQL providers already carry catalogue queries for tables, views, columns, primary keys and foreign keys, and `SqlCatalogueReader` can describe a `SELECT` statement's result columns without reading a row (`CommandBehavior.SchemaOnly`). This plan exposes those through a capability interface, makes the document model public so the editor round-trips exactly what the parser accepts, and builds the portal on top.

## Business Value

- **The problem:** the SQL Connector is the only Connector whose essential configuration is a hand-written document. Administrators type table and column names JIM already knows, learn about mistakes one at a time on save, and reasonably assume every column must be listed.
- **Who it affects:** every administrator configuring a SQL Connected System through the portal.
- **The benefit:** configuration by choosing from what the database actually contains, with JIM proposing the anchor and references it can see, and errors shown where they are made.

## Decisions on the PRD's Open Questions

| # | Question | Decision |
|---|---|---|
| 1 | Settings tab or Schema tab | **Settings tab**, in place of the Object Types field in the Schema category. It is a setting; REST and PowerShell treat it as one; the Delta Import Mode it depends on sits beside it. The Schema tab's existing foreign-key descriptions gain a link back to it. |
| 2 | Generic or SQL-specific catalogue capability | **Generic interface, database-shaped vocabulary.** `IConnectorCatalogue` in `JIM.Models` (schemas, tables and views, columns, keys), with the SQL Connector as its only implementer. It deliberately does not try to describe a directory or a file: the shape holds for any relational source, which is the realistic second implementer (another database engine). |
| 3 | How a Connector supplies an editor for one setting | **The Connector names the setting, as `IConnectorDirectoryServers` already does.** `IConnectorCatalogue.Catalogue.DocumentSettingName` names the setting the editor replaces; the settings tab renders the editor for that setting and the ordinary field for everything else. No setting-type change, no migration, and the settings tab still never asks which Connector it is showing. |
| 4 | Look through a view to its base table's keys | **No, not in this work.** A view can join, aggregate or rename, so its "base table" is often not one table, and the catalogue routes to it (`sys.sql_expression_dependencies`, `ALL_DEPENDENCIES`) do not say which column is which. A wrong preselected anchor is worse than none. The editor says plainly that a view declares no keys. Recorded as a possible follow-up. |
| 5 | SELECT-statement Object Types | **Fully editable.** `SqlCatalogueReader.ReadStatementColumnsAsync` already describes a statement's result columns without executing it for rows, so the anchor and reference choices work for statements as they do for tables. The statement itself is a monospaced text field, validated by the parser's existing `ValidateSelectStatement`. |

## Technical Architecture

### Current state

- `SqlConnector.GetSettings()` declares Object Types as a `Text` setting; since PR #2571 the portal renders it as a monospaced text area with the example beneath it.
- `SqlSchemaConfiguration.Parse` validates the document. Its document classes (`ObjectTypesDocument` and friends) are private. Errors are `SqlSchemaConfigurationException` with a message only: no location, and a `JsonException` (including an unrecognised field, via `JsonUnmappedMemberHandling.Disallow`) is wrapped with its text alone.
- `ISqlProvider` has `TablesCommandText`, `ViewsCommandText`, `ColumnsCommandText`, `PrimaryKeyColumnsCommandText` and `ForeignKeyColumnsCommandText`. Schema discovery uses the table, view and column queries through `SqlCatalogueReader` and `SqlConnectorSchema`; it reads foreign keys privately (`SqlConnectorSchema.ReadForeignKeysAsync`); **nothing reads `PrimaryKeyColumnsCommandText` today.**
- The LDAP Connector's directory server discovery is the pattern for a read-only, Connector-provided capability: `IConnectorDirectoryServers` names its target setting; `ConnectedSystemServer.GetConnectedSystemDirectoryServersAsync` applies draft settings and calls the Connector; `SynchronisationController` exposes it with 400/404/502 mapping; `Get-JIMConnectedSystemDirectoryServer` wraps it; the settings tab places the action beside the named setting.

### Proposed solution

```
Portal (JIM.Web)
  ConnectedSystemSettingsTab
    └─ for the setting named by IConnectorCatalogue.Catalogue.DocumentSettingName:
       SqlObjectTypesEditor ─── edits ──► SqlObjectTypesDocument (public, JIM.Connectors.Sql)
          │                                  └─ Validate() → structured problems (pure, no I/O)
          └─ catalogue reads ──► JimApplication.ConnectedSystems.GetCatalogue…Async(id, drafts)
                                     └─ IConnectorCatalogue (JIM.Models) ──► SqlConnector
                                                                               └─ SqlCatalogueReader + ISqlProvider
REST: GET  /synchronisation/connected-systems/{id}/catalogue
      GET  /synchronisation/connected-systems/{id}/catalogue/object?schema=&name=
      POST /synchronisation/connected-systems/{id}/catalogue/statement
PowerShell: Get-JIMConnectedSystemCatalogue
```

**The document model is the parser's model.** The private document classes become a public `SqlObjectTypesDocument` (with `SqlObjectTypeDocument`, `SqlColumnDocument`, `SqlRelatedTableDocument`, `SqlChangeLogDocument`) in `JIM.Connectors.Sql`, and `SqlSchemaConfiguration.Parse` deserialises into them as it does now. The editor binds to the same classes and serialises them back with the same options. One shape, so the editor cannot write a field the parser rejects or drop one it accepts, and PRD requirement 11 (configuration the editor cannot represent) has no case left to handle.

**Why the editor touches a Connector type directly.** The editor is SQL-specific UI by definition, and the document is a value, not data access: building and validating it does no I/O. Every catalogue read, which does do I/O, still goes through `JimApplication`. `JIM.Web` already references `JIM.Connectors`. The alternative, a document model in `JIM.Models`, would put one Connector's configuration format into the shared domain model, which is the worse coupling.

**Structured validation.** `SqlObjectTypesDocument.Validate(string json)` returns a list of `SqlObjectTypesProblem { Message, ObjectTypeIndex?, ObjectTypeName?, Field?, Line?, Column? }` instead of throwing on the first fault. `Parse` keeps throwing, built on `Validate` (first problem becomes the exception), so every existing caller and message is unchanged. JSON-level faults carry `JsonException.LineNumber` and `BytePositionInLine`; semantic faults carry the Object Type and field, which the editor maps to a card and the JSON view maps to a line by locating the Object Type's property in the text.

**Nearest-field suggestion (PRD 10a).** When `JsonException` reports an unmapped member, `Validate` compares the member name with the known property names of the class at that path (case-insensitive edit distance of 2 or less, or a plural/singular match) and appends "Did you mean 'anchorColumns'?". It is in the parser, so REST and PowerShell callers get it in the save-time validation message too.

**Catalogue capability (`JIM.Models`).**

```csharp
public interface IConnectorCatalogue
{
    ConnectorCatalogueDeclaration Catalogue { get; }   // DocumentSettingName; what the source calls a schema
    Task<List<ConnectorCatalogueObject>> GetCatalogueObjectsAsync(List<ConnectedSystemSettingValue> settings, ILogger logger, CancellationToken cancellationToken);
    Task<ConnectorCatalogueObjectDetail> GetCatalogueObjectAsync(List<ConnectedSystemSettingValue> settings, string? schemaName, string objectName, ILogger logger, CancellationToken cancellationToken);
    Task<ConnectorCatalogueObjectDetail> DescribeStatementAsync(List<ConnectedSystemSettingValue> settings, string statement, ILogger logger, CancellationToken cancellationToken);
}
```

- `ConnectorCatalogueObject { SchemaName, Name, Kind (Table | View) }`
- `ConnectorCatalogueObjectDetail { Columns [Name, DataTypeName, IsNullable], PrimaryKey [column names in key order], ForeignKeys [ConstraintName, Columns[], ReferencedSchemaName, ReferencedObjectName, ReferencedColumns[]] }`. Foreign keys are grouped per constraint, so a composite key is one entry; the existing per-column `SqlDiscoveredForeignKey` rows are grouped by constraint name.

**SQL implementation.** `SqlCatalogueReader` gains `ReadCatalogueObjectsAsync`, `ReadPrimaryKeyColumnsAsync` and `ReadForeignKeysAsync`, moved out of `SqlConnectorSchema` where they exist, so schema discovery and the catalogue share one reader (the class's own remarks give the reason: two parsers of the same rows eventually disagree). `SqlConnector` implements `IConnectorCatalogue` by opening a connection with the supplied settings, exactly as schema import does.

**Application layer.** `ConnectedSystemServer` gains `GetCatalogueDeclarationAsync(id)`, `GetCatalogueObjectsAsync(id, drafts)`, `GetCatalogueObjectAsync(id, schema, name, drafts)` and `DescribeCatalogueStatementAsync(id, statement, drafts)`, each mirroring `GetConnectedSystemDirectoryServersAsync`: load the Core Connected System untracked, apply drafts via `ConnectedSystemDraftSettings`, create the Connector, throw `NotSupportedException` when it is not an `IConnectorCatalogue`. Read-only, so no Activity, for the reason that method already documents.

**Editor state.** `SqlObjectTypesEditor` holds a `SqlObjectTypesDocument` and writes `SettingValue.StringValue` (the serialised document) after every change, then raises `ValueChanged`. The settings tab's save path, validation and change history are therefore untouched: as far as they know, the setting's text changed. Suggestion and problem logic lives in a plain class, `SqlObjectTypesEditorModel`, so it is unit-tested without rendering.

**Formatting.** Saving from the editor writes the document in one canonical layout (indented, two spaces, nulls omitted). A hand-formatted document is reformatted the first time it is saved from the editor; its content is unchanged. The JSON view saves the text as typed.

### Data flow: opening the editor

1. The settings tab asks `GetCatalogueDeclarationAsync` once (as it asks `GetDirectoryServerDiscoveryAsync` today) and renders `SqlObjectTypesEditor` for the named setting.
2. The editor parses the current value. A document that does not validate opens in the JSON view with its problems listed; a valid one opens as cards.
3. The editor calls `GetCatalogueObjectsAsync` with the on-screen connectivity settings as drafts. On failure it shows the banner (PRD 16) and switches table and column fields to free text; **Retry** calls it again.
4. Expanding a card, or reaching the Anchor step of Add Object Type, loads that one object's detail (`GetCatalogueObjectAsync`, or `DescribeCatalogueStatementAsync` for a statement), cached for the editing session.

## Implementation Phases

Each phase is test-first, builds and tests clean, and is mergeable on its own. Phases 1 to 3 deliver the REST/PowerShell capability and the better validation messages before any portal work, so they can ship ahead of the editor.

### Phase 1: Public document model and structured validation ✅

- Make the document classes public as `SqlObjectTypesDocument` and its members; `Parse` uses them unchanged.
- Add `Validate(string)` returning all problems with locations; reimplement `Parse` on it so existing messages and every existing test hold.
- Add the nearest-field suggestion for unrecognised fields.
- Add `Serialise()` producing the canonical layout.
- **Tests (JIM.Worker.Tests, `SqlObjectTypeConfigurationTests`, where the parser is tested today):** both shipped examples (`ObjectTypesExample`, `DeltaConfigurationExample`) round-trip through deserialise and `Serialise` to an equivalent document that `Parse` accepts; `anchorColumn` yields a problem on the right line suggesting `anchorColumns`; a document with two faults returns both; semantic faults name the Object Type and field.
- **User-facing:** the save-time message for a misspelt field now suggests the intended one (🐛/🔄 changelog entry, REST and PowerShell included).

### Phase 2: Catalogue capability in the Connector and Application layers ✅

- `IConnectorCatalogue` and its models in `JIM.Models`.
- `SqlCatalogueReader`: catalogue objects, primary keys (first use of `PrimaryKeyColumnsCommandText`), foreign keys grouped by constraint; `SqlConnectorSchema` switched to the shared reads.
- `SqlConnector` implements `IConnectorCatalogue`, with `DocumentSettingName = SettingObjectTypes`.
- `ConnectedSystemServer` methods as above.
- **Tests:** provider command shapes for both SQL Server and Oracle (existing provider test pattern); grouping of a composite foreign key; a view returns an empty primary key; Application methods apply drafts and refuse a Connector without the capability (`ConnectedSystemDirectoryServerDiscoveryTests` is the pattern); schema discovery's foreign-key suggestions are unchanged after the move.

### Phase 3: REST and PowerShell ✅

- `SynchronisationController`: `GET .../catalogue` (objects), `GET .../catalogue/object?schema=&name=` (detail; query parameters rather than path segments because schema and object names can contain characters a route would mangle), `POST .../catalogue/statement` with `{ "statement": "..." }` (POST because a statement does not belong in a URL or its logs). 400 for no capability or an invalid statement, 404 for no Connected System, 502 for a database that cannot be reached, as the directory-server endpoint does.
- DTOs in `JIM.Web/Models/Api/` with published `<summary>` text per `src/CLAUDE.md`.
- `Get-JIMConnectedSystemCatalogue -Id <int>` lists objects; `-Schema`/`-Name` returns one object's detail; `-Statement` describes a statement. Pester tests for each parameter set.
- **Validation:** `Generate-OpenApiDoc.ps1`, `Update-PowerShellReference.ps1` regenerated and checked (the step that failed PR #2571 CI), `Test-DocsCoverage.ps1`.
- **Docs:** `docs/connectors/jim-sql-connector.md` gains a short "Browse the database" note pointing at the cmdlet; generated API and PowerShell reference.

### Phase 4: The editor, editing existing Object Types ✅

- `SqlObjectTypesEditor.razor` (plus `SqlObjectTypeCard.razor`) under `JIM.Web/Pages/Admin/Components/Sql/`, and `SqlObjectTypesEditorModel`.
- Settings tab: render the editor for the declared setting; the example block from PR #2571 moves into the JSON view.
- Cards per the PRD and the chosen mock: collapsed summary chips; expanded name, source, Anchor chips, References rows, Multi-valued attribute rows (an edit dialog for a related table's fields), Delta Import fields driven by the on-screen Delta Import Mode value; "the other N columns are discovered automatically".
- Suggestions: primary key (used by Phase 5) and foreign keys to another Object Type's anchor (accept, dismiss for the session) or to a table that is no Object Type's source (offers Add Object Type, preselected).
- Problems on cards from `Validate`, a marker on a collapsed card with problems, and confirmation before removing an Object Type others reference, naming those references.
- Catalogue failure: banner with the reason and **Retry**; free-text fields.
- Searchable pickers (`MudAutocomplete`) for schema, table and column, so thousands of tables stay usable.
- **Tests (JIM.Web.Tests):** `SqlObjectTypesEditorModel` unit tests for every suggestion rule, summary chips, problem mapping and the reference-removal check; bUnit tests that editing a card rewrites `StringValue` with a document `Parse` accepts, that a catalogue failure leaves the configuration editable with the banner shown, and that Delta Import fields follow the mode. Presentation is not asserted (root `CLAUDE.md`).
- **Runtime verification:** sandbox light stack against a SQL Server container (Scenario 016's setup builds a suitable schema), screenshots in both themes.

### Phase 5: Add Object Type dialog ✅

- Four steps (Source, Anchor, References, Multi-valued attributes), each skippable where the document allows; Finish adds an expanded card.
- Anchor preselected from the primary key in key order, with the reason; a view says it declares no keys.
- **Tests:** step logic in a plain model class; bUnit test that finishing produces the same document a hand-written one would (PRD Scenario 1).

### Phase 6: JSON view ✅

- Toggle between cards and JSON. Reuses the overlay technique of `ExpressionEditor.razor` (a transparent `textarea` over a highlighted, `aria-hidden` `pre`) with `Helpers.HighlightJson`, adding a line-number gutter and a marked line for each located problem. No new dependency: a third-party code editor would need the dependency governance process for one field.
- Problems listed above the text with their line numbers; switching to cards and saving blocked while any remain.
- Copy and paste; the shipped example offered for an empty document.
- **Tests:** model tests for problem-to-line mapping (JSON-level and semantic); bUnit test that an invalid document blocks switching and saving (PRD Scenario 4).

### Phase 7: Documentation, changelog and lifecycle ✅

- `docs/connectors/jim-sql-connector.md` Step 2 rewritten around the editor, keeping the document reference for REST and PowerShell; the Delta Import pages describe the editor fields beside the document fields; new screenshots via `Capture-Screenshots.ps1`.
- `engineering/developer/connectors.md`: `IConnectorCatalogue`.
- Changelog: ✨ for the editor and for catalogue browsing over REST and PowerShell.
- PRD and this plan: `Doing` in the first implementation commit, `Done` in the PR that closes #2570.

## Success Criteria

- Every Object Types document the parser accepts opens in the editor, and saving it unchanged produces a document `Parse` accepts with the same content.
- An administrator can configure Scenario 016's schema end to end from the portal, with no JSON, against both SQL Server and Oracle.
- With the database unreachable, existing configuration is still viewable and editable.
- `Get-JIMConnectedSystemCatalogue` returns the same tables, columns and keys the portal shows.
- Build and test clean, OpenAPI and PowerShell reference checks pass, and both themes verified at runtime.

## Benefits

- **UX:** configuration by choice instead of recall; mistakes shown where they are made; JIM's existing knowledge of keys offered at the point of decision.
- **Architecture:** one document model for parser and editor; one catalogue reader for discovery and browsing; the settings tab gains a capability-driven seam instead of a Connector name check.
- **Scripting:** catalogue browsing becomes available to automation for the first time.

## Dependencies

- PR #2571 (merged): renderable Text settings and setting Examples.
- No new packages.

## Risks and Mitigations

| Risk | Mitigation |
|---|---|
| Making the document classes public lets other code depend on them | They are the document's published shape already (it is the REST/PowerShell format); keep them plain data with no behaviour beyond `Validate`/`Serialise`. |
| Reformatting a hand-formatted document on first editor save surprises an administrator who keeps it in source control | Formatting is canonical and stable, so it changes once; the JSON view saves text as typed for anyone who prefers it. Say so in the docs. |
| `DescribeStatementAsync` over REST runs administrator-supplied SQL | Same trust as the `select` field already configured through the same API; the parser's `ValidateSelectStatement` runs first, `CommandBehavior.SchemaOnly` reads no rows, and the endpoint requires the same administrator authorisation as editing the setting. |
| Large catalogues | Object list is one narrow query (schema, name); detail loads per object on demand and is cached; pickers are searchable. **Measured in Phase 4** against SQL Server 2022 with 10,006 tables: the list reads in 0.13 to 0.74 s over REST (540 KB), the editor and its catalogue are ready 1.6 s after navigating to the Settings tab, and a table search answers in 0.14 to 0.28 s. Loading per schema is not needed at this scale. |
| Oracle catalogue differences (case, synonyms) | Covered by the existing provider abstraction; Scenario 016 runs both engines. Synonyms stay out of scope, as they are for schema discovery today. **Verified in Phase 7:** Scenario 016's schema was configured in the portal alone (no JSON view) against SQL Server 2022 and Oracle Database 23 Free, every name chosen from the catalogue's lists, and the saved document was identical to the one `Setup-Scenario-016.ps1` writes. On Oracle the list of tables read in 1.4 s and each table's columns and keys in 1.6 to 3.1 s. |

## Deviations from the PRD

- **Phase 4, JSON fallback:** until Phase 6's JSON view existed, **View as JSON** showed the setting's existing text field (with its example). Phase 6 replaced it.
- **Phase 6, what blocks switching to cards:** only a problem in the JSON itself (it is not JSON, or has a field the document does not). A problem with what a readable document says (a missing anchor, a reference to an undeclared Object Type) no longer blocks **View as cards**: each card shows its own problems, and a card is the better place to fix a missing anchor, since it offers the columns to choose from. Save Settings is held while the parser finds any problem, in either view, with a caption under it saying why; that matches what saving would refuse.
- **Phase 6, locating a problem with what the document says:** it is marked on the line of the Object Type's field it concerns ('changeLog' for 'changeLog.table'), or on the line the Object Type opens on when that field is missing or the problem names none.
- **Phase 4, offering an Object Type for a foreign key's table:** until Phase 5's dialog existed, accepting added an expanded card reading from that table. Phase 5 replaced this with the Add Object Type dialog, opened with the table chosen, as PRD requirement 6 asks.
- **Phase 5, adding before the last step:** the dialog offers **Add Object Type** from the Anchor step on, since references and multi-valued attributes are optional; the PRD's "each step can be completed or skipped" is met by that rather than by a Skip button on each optional step.
- **Phase 5, Delta Import fields:** the dialog's four steps match the PRD and leave out the watermark column and change log; the new card opens with them, alone, so the mode's fields are the next thing seen.
- **Phase 4, Delta Import problems:** a missing watermark column or change log is shown on the card only for an Object Type selected on the Schema tab, matching the rule saving the Schema tab applies (#1424).

- **Phase 7, screenshots:** none were added. The documentation site has no screenshots by design (`engineering/DOCUMENTATION_STANDARD.md`, VOI-5): a screenshot is invalidated by any change to the control it shows and cannot be searched or read aloud. Step 2 names each control instead, with a diagram of the Add Object Type steps, and the two Delta Import pages name the card's fields beside the document's.
- **Phase 7, name pickers:** the Scenario 016 check found that typing into a name picker passed on every keystroke: the anchor picker added a column named after the first letter typed, and a table name read the catalogue for every prefix. A picker now passes a name on only once it is finished (chosen from the list, Enter, leaving the field, or the clear button), the list puts the name typed first, and the anchor picker accepts only the source's own columns where it knows them.
- **Phase 7, Delta Import pages:** both pages now choose the mode before configuring the Object Types, because a card shows its Delta Import fields only once **Delta Import Mode** is set on screen.

- **PRD non-functional requirement "loaded per schema":** the object list is loaded in one call rather than per schema. It is a narrow list of (schema, name, kind) and the provider queries are not schema-filtered today; one call is simpler and fast enough at the stated scale. Column detail is still per object. Revisit if the Phase 4 measurement says otherwise.
- **PRD open question 4:** declined for this work (see Decisions).
