# Connector Message Catalogues: Implementation Plan

- **Status:** Doing (Phases 1-2 complete; contract message slots next)
- **Issue:** [#3103](https://github.com/TetronIO/JIM/issues/3103)
- **PRD:** [PRD_CONNECTOR_MESSAGE_CATALOGUES.md](../../prd/doing/PRD_CONNECTOR_MESSAGE_CATALOGUES.md)

## Overview

Make everything a Connector shows an administrator renderable in the reader's language, through one mechanism that serves the built-in Connectors now and Connectors that administrators install later. This plan details Part 1 of the PRD (built-in Connectors, shaped for installed ones) phase by phase, and outlines Part 2 (stored catalogues), which waits for the Connector plugin model.

## Business Value

- About 50 export failures, plus import errors, import warnings and progress narration, are English-only today, and the contract cannot carry a message for three of them.
- The rendering path assumes a Connector's code is loaded where its text is shown, and shows a raw key when a catalogue or key is missing. Both break once Connectors can be installed, upgraded and removed.
- Fixing the renderer's fallback also protects JIM's own messages: a record always reads in English at worst.

## Technical Architecture

### Current state

| Piece | Today |
|---|---|
| `JimMessage(Catalogue, Key, Arguments, PluralCount)` | Catalogue named `"<Assembly>:<ResxName>"`, e.g. `JIM.Connectors:ConnectorMessages` |
| `MessageRenderer.CatalogueFor` | `Assembly.Load(name)` then a `ResourceManager`; cached per name |
| `MessageRenderer.Render` | A missing key renders its English; a missing catalogue or key altogether renders the key; `string.Format` may throw on a bad template |
| `MessageDisplay.Show(detail, english)` | Uses the stored English only when there is no message at all |
| Connector identity | `IConnector.Name` / `ConnectorDefinition.Name` ("JIM LDAP Connector"), a display name |
| Connector catalogue | One shared `JIM.Connectors/Resources/ConnectorMessages.resx` (173 keys, all in `PublishedMessageKeys.txt`) |
| Setting labels, descriptions, options, groups | `JIM.Web/Resources/ConnectorSettingText.resx` (233 entries), keyed `{Definition}_{Setting}_Label`, looked up by `ConnectorSettingDisplay` |
| Phase names | `JIM.Web/Resources/RunPhaseText.resx`, keyed by phase key, looked up by `RunPhaseDisplay` |
| Contract text slots | Export: `Failed(JimMessage)` exists. Setting validation: `ErrorDetail` exists. Import object `ErrorMessage`, import `WarningMessage`, `IConnectorProgress.ReportAsync(string)` and `EnterPhaseAsync(key, string?)`: string only |

### Proposed design

```
            write (Worker)                                   read (Web, Worker)
Connector ──JimMessage──► record: English + DetailJson ──► MessageRenderer
                                                              │
                                                  MessageCatalogueSources (registry)
                                                   ├─ AssemblyResxCatalogueSource "JIM.Application:ActivityMessages"
                                                   ├─ ConnectorResxSource  "connector:jim.ldap/Messages"   (Part 1: built-in)
                                                   └─ StoredCatalogueSource "connector:acme.workday/Messages" (Part 2: installed)
                                                              │
                                          template in language → English template → stored English
```

- **Catalogue sources.** `IMessageCatalogueSource` in `JIM.Models.Localisation` answers one question: the template for a catalogue, key and language, or none. `MessageCatalogueSources` holds the registered sources and asks each in turn. `AssemblyResxCatalogueSource` is today's behaviour, unchanged for every existing catalogue name. Sources are registered at start-up in the Web app and the Worker; the Models default registers `AssemblyResxCatalogueSource` alone, so tests and tools keep working.
- **Connector identity.** `IConnector` gains `Identifier`, a stable, lower-case, dotted id (`jim.ldap`, `jim.scim`, `jim.sql`, `jim.graph`, `jim.file`), validated by pattern `^[a-z0-9]+(\.[a-z0-9-]+)+$`. The display name stays free to change. `ConnectorDefinition` gains a nullable `Identifier` column (one migration), filled for built-in Connectors when seeding converges (`SeedingServer`), so the portal can go from a Connected System to its Connector's catalogue without loading the Connector.
- **Catalogue names.** A Connector's catalogue is `connector:<identifier>/Messages`. `ConnectorResxSource` maps a built-in identifier to its embedded `.resx` (`JIM.Connectors/Resources/Ldap/LdapConnectorMessages.resx` and so on), registered by `ConnectorFactory`. The existing `JIM.Connectors:ConnectorMessages` catalogue stays, as the shared catalogue for text common to all Connectors (certificates, TLS, `ServerNoun`); its keys are published and cannot move.
- **Fallback.** `Render` gains a "could not render" outcome. On a missing catalogue, a missing key, or a template whose placeholders do not fit the arguments (format failure caught, never thrown), `MessageDisplay.Show(detail, english)` shows the stored English. `Show()` with no English (live messages that are never stored) renders the catalogue's English, then the key, as today. Built-in keys are kept from reaching that last step by the typed `nameof(...)` accessors, which fail the build on a mistyped key, and by tests that render every variant of keys composed at run time (the per-recipient-type Exchange keys, for example). `MessageRenderer`'s comment names a `JimMessageConventionTests` that does not exist; Phase 1 corrects it.
- **API key names.** `ApiMessage.KeyOf` strips the assembly prefix only for assembly catalogues; a Connector catalogue publishes as `connector:jim.ldap/Messages.KeyName`. `PublishedMessageKeyTests` learns the per-Connector catalogue folders and that naming.
- **Declared text.** A Connector's catalogue holds its declared text under conventional keys:

  | Text | Key |
  |---|---|
  | Connector description | `Connector_Description` |
  | Setting label, description | `Setting_<Setting>_Label`, `Setting_<Setting>_Description` |
  | Setting choice | `Setting_<Setting>_Option_<Value>` |
  | Setting group heading | `SettingGroup_<Group>` |
  | Phase the Connector declares | `Phase_<PhaseKey>` |

  `ConnectorSettingDisplay` and `RunPhaseDisplay` look these up through `MessageCatalogueSources` by the definition's `Identifier`, falling back to the stored English exactly as today. The 233 `ConnectorSettingText` entries move into the Connectors' catalogues. Phases JIM itself declares stay in `RunPhaseText.resx`.

### What stays the same

- Every stored record keeps its English beside the message; the REST API and PowerShell keep returning the English, plus the key.
- Every existing catalogue name and published key.
- Another system's words are passed as arguments (`Verbatim`), never translated.

## Implementation Phases

Each phase is test-first, ships its own PR, and leaves the build green.

### Phase 1: Renderer resilience and catalogue sources ✅

- `IMessageCatalogueSource`, `MessageCatalogueSources` registry (`MessageCatalogues` already names JIM.Models' catalogue constants), `AssemblyResxCatalogueSource` (today's lookup moved behind it).
- `Render` reports "could not render"; safe formatting (catch `FormatException`).
- `MessageDisplay.Show(detail, english)` falls back to the stored English on any "could not render".
- Correct `MessageRenderer`'s comment, which names a test that does not exist.
- Tests: missing catalogue, missing key, bad template, each rendering the stored English; existing localisation suites unchanged.

### Phase 2: Connector identity and per-Connector catalogues ✅

- `IConnector.Identifier` on every Connector (`jim.ldap`, `jim.file`, `jim.scim.client`, `jim.scim.serviceprovider`, `jim.sql`, `jim.entraid`; the mocks `jim.mock.call` and `jim.mock.file`), constants in `ConnectorConstants`; `ConnectorIdentifierTests` checks the pattern and uniqueness over every `IConnector` implementation and pins the built-in identifiers.
- `ConnectorDefinition.Identifier`: nullable column with a filtered unique index; seeding sets it on create and records it as an audited update on a definition that predates it; the configuration snapshot carries it. Not exposed through the REST API yet. Real-PostgreSQL test in `SeedingIdempotencyDatabaseTests`.
- `ConnectorCatalogueNames` (JIM.Models) names a Connector catalogue, `connector:<identifier>/Messages`, whichever source serves it. `BuiltInConnectorCatalogueSource` serves the built-in Connectors' catalogues, one empty resource file each, flat in `JIM.Connectors/Resources/` (`LdapConnectorMessages.resx`; the Entra ID Connector's is `GraphConnectorMessages.resx` after its class). `JimApplication`'s static constructor registers it, so every host and test has it.
- No per-Connector helper class: call sites use `JimMessage.Create(BuiltInConnectorCatalogues.Ldap, nameof(LdapConnectorMessages.X), ...)`, the same shape as JIM's own catalogues, and the generated accessor still makes a mistyped key a build error.
- `ApiMessage.KeyOf` keeps a Connector catalogue's whole name (`connector:jim.ldap/Messages.Key`); `PublishedMessageKeyTests` publishes the built-in Connector files under it.

### Phase 3: Contract message slots

- `ConnectedSystemImportObject.ErrorMessageDetail` and `ConnectedSystemImportResult.WarningMessageDetail`, each setting the English as `Activity.MessageDetail` does.
- `IConnectorProgress.ReportAsync(JimMessage)` and `EnterPhaseAsync(string, JimMessage)`, implemented by `ConnectorProgress` through the existing message path in `ActivityProgressThrottle` and `ActivityPhaseReporter` (#3039).
- Worker carries them through: import errors via `ActivityMessage.SetError`, import warnings onto the Activity's `WarningMessageDetail`, narration onto progress `MessageDetail`.
- Tests: each slot stored with its English and rendered in French.

### Phase 4: Declared text from Connector catalogues

- Move `ConnectorSettingText` entries into each Connector's catalogue under the conventional keys, French and German included; delete `ConnectorSettingText.resx`.
- `ConnectorSettingDisplay`, `RunPhaseDisplay` and the Connector description display read through `MessageCatalogueSources` by identifier.
- `ConnectorSettingDisplayTests` keeps failing the build for a built-in setting with no label, now checking the Connector's catalogue.

### Phase 5: Convention test and conversion, one Connector per PR

- `ConnectorTextConventionTests` (JIM.Worker.Tests), modelled on `TimeOrderedIdConventionTests`: scans `src/JIM.Connectors` for a string literal or interpolated string passed to `ConnectedSystemExportResult.Failed`, `IConnectorProgress.ReportAsync` or `EnterPhaseAsync`, or assigned to an import `ErrorMessage` or `WarningMessage`. A deliberate exception carries `// connector-text: <reason>` on the line or one of the three above it. It starts with a list of known offenders per Connector that shrinks to empty; the list is the one tolerated allowlist, and it may only lose entries.
- Convert in this order, each with French and German and its English unchanged (asserted against the old English, as #3088 did): SCIM (pilot, mostly JIM's own wording), LDAP, SQL, Graph, File. The Mock Connector is test support and exempt.
- When the list is empty, the allowlist is deleted and the test runs with no exceptions but the marked ones.

### Phase 6: Documentation

- `engineering/developer/connectors.md`: "Writing what your Connector says": when text is a message, the Connector's catalogue and helper, conventional keys for declared text, the other system's words, the convention test.
- `engineering/LOCALISATION.md`: catalogue sources, Connector catalogues, the fallback order.
- Ships with Phases 1 to 3, and is extended by Phase 4.

### Part 2 outline: stored catalogues (with the Connector plugin work)

Not to be built until the plugin model's packaging and install design exists; recorded so that design leaves room for it.

- **Package format:** the Connector SDK compiles the author's `.resx` files into `messages.json`, `{"en": {"Key": "Text", ...}, "fr": {...}}`, inside the package.
- **One validator, two callers:** `MessageCatalogueValidator` in `JIM.Models` checks the PRD's rules (English for every key, placeholder parity, counted pairs, no markup, known languages, size). The install path and the SDK's author-run test base class call the same code, so a catalogue that passes the author's build passes install.
- **Storage:** one row per Connector identifier and language holding the catalogue JSON; only the installed version's catalogue is kept, since every record keeps its English as the final fallback.
- **`StoredCatalogueSource`:** cached per identifier and language, invalidated through a PostgreSQL `NOTIFY` channel, as the existing real-time notifications do (`DEVELOPER_GUIDE.md`, Real-Time Notifications).
- **Uninstall** keeps the stored catalogue; **upgrade** replaces it.
- **Built-in Connectors** move to the same packaging, so the third-party path is the one JIM uses.

### Open questions from the PRD, answered

1. **Connector identity:** a new `IConnector.Identifier` and `ConnectorDefinition.Identifier`, not the display name (Phase 2). The plugin model adopts it as the package's id.
2. **Package format:** JSON compiled from `.resx` by the SDK (Part 2); authors keep `.resx`.
3. **Versions:** latest only; records keep their English.
4. **Size limit:** 5,000 keys and 1 MB per catalogue, revisited when the first third-party Connector exists.

## Success Criteria

- No reader sees a raw key, for JIM's own catalogues or a Connector's.
- Every person-facing string a built-in Connector writes is a message with French and German; the convention test's offender list is empty and deleted.
- Setting, phase and description text for every built-in Connector comes from its catalogue; `ConnectorSettingText.resx` is gone.
- Import errors, import warnings and narration render in the reader's language.
- The REST API, PowerShell and every stored English are unchanged.

## Benefits

- **Administrators:** Connector errors and progress in their language; history readable after Connectors change.
- **Authors:** ordinary `.resx`, a typed helper, and a build that catches mistakes.
- **Architecture:** one catalogue mechanism with pluggable sources, ready for installed Connectors without the portal loading their code.

## Dependencies

- None for Part 1 beyond the existing localisation work (#3030, #3039).
- Part 2 depends on the Connector plugin model.

## Risks and Mitigations

| Risk | Mitigation |
|---|---|
| The fallback hides a built-in message whose key is wrong | Typed `nameof` accessors fail the build on a mistyped key, and tests render every variant of a composed key; the fallback is for installed and removed Connectors |
| Published keys constrain moving text | Existing `ConnectorMessages` keys stay in place as the shared catalogue; only new and declared text uses the per-Connector catalogues |
| Moving 233 setting entries loses or alters a translation | A one-off test compares each moved entry's English, French and German with its old value before `ConnectorSettingText.resx` is deleted |
| Rendering cost of an extra lookup layer | Sources cache per catalogue and language, as `CatalogueFor` does today |
| The convention test is too blunt and blocks valid code | The marked-exception comment, reviewed in the PR, as with `random-id:` and `bulk-sql-column-exemption:` |
| Identifier collides between Connectors | Pattern and uniqueness test over built-ins now; uniqueness enforced at install in Part 2 |
