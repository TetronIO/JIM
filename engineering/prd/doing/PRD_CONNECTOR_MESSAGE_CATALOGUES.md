# Connector Message Catalogues

- **Status:** Doing (Phase 1 in progress)
- **Created:** 2026-11-25
- **Author:** Jay Van der Zant (drafted with Claude)
- **Issue:** #3103
- **Plan:** [CONNECTOR_MESSAGE_CATALOGUES.md](../../plans/doing/CONNECTOR_MESSAGE_CATALOGUES.md)

## Problem Statement

JIM now stores the text it writes for administrators as messages and renders them in the reader's language (#3030, #3039, #3084, #3094). Connectors are the remaining gap, and they matter twice over: they are JIM's largest remaining source of English-only text, and Connectors are planned to become extensible, so third parties can build their own and administrators can install them into their own JIM instances.

Today:

- **Much of what a Connector says is English only.** About 50 export failures are built from strings, and the contract has no message form for import object errors (`ConnectedSystemImportObject.ErrorMessage`), import warnings (`ConnectedSystemImportResult.WarningMessage`) or progress narration (`IConnectorProgress.ReportAsync`, `EnterPhaseAsync`). Nothing stops a new string being written.
- **The catalogue mechanism assumes the Connector's code is loaded where the text is shown.** `MessageRenderer.CatalogueFor` finds a catalogue with `Assembly.Load(name)`. Connectors run in the Worker; their messages are read in the Web app. A Connector loaded into its own load context cannot be found by name, so the portal would have to load third-party code just to show a sentence.
- **A missing catalogue or key renders the raw key.** After a Connector is upgraded (a key renamed) or uninstalled, every Activity it wrote would read `ExportRefusedOne`, although the English is stored beside the message. For JIM's own catalogues a build-time test prevents this; nothing can for an installed Connector.
- **A malformed translation can break a page.** A translation whose placeholders do not match the English makes formatting throw while rendering.
- **Translations of Connector settings, phases and capabilities are display-time maps in the portal**, keyed by name and falling back to English for any Connector JIM does not ship. An installed Connector could never be translated.
- **Authors get no help.** They would have to copy JIM's internal resource build conventions, and nothing checks their translations.

## Goals

- Every piece of text a Connector shows an administrator can be a message rendered in the reader's language: runtime messages (export failures, import errors and warnings, progress narration, connection tests, setting validation) and declared text (setting names and help, phase names, capability descriptions).
- Built-in and installed Connectors use one mechanism; the built-in Connectors are its first users and prove it.
- The portal renders a Connector's text without loading the Connector's code.
- A reader never sees a raw key: missing catalogues, missing keys and malformed templates fall back to the English stored with the record.
- A Connector author writes ordinary `.resx` files and gets tests that catch mistakes in their own build.
- The build fails when a built-in Connector passes English text where a message belongs.

## Non-Goals

- Designing the Connector plugin model itself (packaging, loading, isolation, signing, install user interface). This PRD defines what that model must carry for localisation and builds the parts that do not depend on it.
- Translating text another system words (directory, database or API errors). It stays as written, passed into JIM's message as an argument.
- Translating the REST API, PowerShell, logs or the documentation site. They keep returning English, plus the message key where the API already exposes one (#3039).
- Letting a Connector add portal languages. JIM's language list stays JIM's; a Connector translates into the languages JIM offers.
- Letting administrators reword a Connector's text.

## User Stories

1. As an administrator reading in French, I want a Connector's errors, warnings and progress in French, so that I can act on them without switching language.
2. As an administrator, I want Activities a Connector wrote to stay readable after I upgrade or uninstall that Connector, so that history does not turn into keys.
3. As a Connector author, I want to keep my translations in ordinary `.resx` files and have my build tell me when one is wrong, so that I ship a correct catalogue without learning JIM's internals.
4. As a JIM maintainer, I want the build to fail when a Connector passes English text where a message belongs, so that the gap cannot reopen.
5. As a security-conscious operator, I want the portal never to load third-party Connector code just to display text, so that an installed Connector's reach stays where JIM runs it.

## Requirements

### Functional Requirements

**Part 1: now (built-in Connectors, shaped for installed ones)**

1. The Connector contract accepts a message wherever a Connector reports text to a person: import object errors, import warnings, progress narration and phase entry, alongside the existing export failure (`ConnectedSystemExportResult.Failed(JimMessage)`) and setting validation (`ErrorDetail`) forms. The string forms remain only for another system's own words.
2. Message rendering resolves catalogues through a catalogue-source abstraction. The first source serves JIM's own `.resx` catalogues; a later source (Part 2) serves stored Connector catalogues. No caller changes when a source is added.
3. Connector catalogues are named by the Connector's stable identity, not its assembly: for example `connector:jim.ldap/Messages`. Each built-in Connector has its own catalogue; the shared `ConnectorMessages.resx` is split accordingly.
4. Rendering never shows a raw key. It tries, in order: the catalogue's text in the reader's language; the catalogue's English; the English stored on the record. A template whose placeholders do not fit its arguments falls back the same way instead of throwing. This applies to JIM's own catalogues as well.
5. A Connector's declared text (setting labels and help, phase names, capability descriptions) is looked up in its catalogue by conventional keys (for example `Setting.<name>.Label`, `Setting.<name>.Help`, `Phase.<key>.Name`), falling back to the English the Connector declares. This replaces the portal's per-Connector display maps for these.
6. A convention test fails the build when code in `JIM.Connectors` passes a string literal or interpolated string to an export failure, progress report, import error or import warning. A deliberate exception is marked by a visible comment at the call site, never an allowlist in the test.
7. Every existing built-in Connector string a person reads is converted, with French and German, and its English unchanged. The work is split by Connector.

**Part 2: with the Connector plugin work**

8. A Connector package carries its catalogue as data (for example a `messages.json` with one block per language), produced from the author's `.resx` files by a Connector SDK build step.
9. Installing a Connector validates its catalogue and stores it in the database, keyed by the Connector's identity. Validation refuses the install, with the reason, when:
   - a key has no English text;
   - a translation's placeholders differ from the English;
   - a counted key lacks its `One` or `Other` partner;
   - a text contains markup;
   - a language is not one JIM offers;
   - the catalogue exceeds a size limit.
10. Upgrading a Connector replaces its catalogue; uninstalling keeps it, so records it wrote still render.
11. A stored-catalogue source serves these catalogues to both the Web app and the Worker. Neither loads the Connector's code to render its text.
12. The Connector SDK ships a test base class authors run in their own build, checking the same rules as install-time validation plus that every key the code uses exists.
13. The built-in Connectors move to the same packaging and install path, so the third-party path is the one JIM itself uses.

### Non-Functional Requirements

- **Security:** catalogue text is rendered as text, never markup. Validation runs before anything is stored. The Web app gains no code-loading path for Connectors.
- **Performance:** catalogue lookups are cached per catalogue and language; rendering a page of Activities adds no per-row database round trip.
- **Air-gapped:** catalogues travel inside the Connector package; nothing is fetched at install or render time.
- **Compatibility:** records written before this change, and by writers not yet converted, keep rendering their stored English. The REST API and PowerShell output are unchanged.

## Examples and Scenarios

### Scenario 1: An installed Connector's error in French

**Given**: a third-party Workday Connector is installed, and its catalogue has French
**When**: an export fails and a French-speaking administrator opens the execution item
**Then**: the item reads "Accordez User.ReadWrite à l’inscription d’application…", while the REST API returns the English and the key `connector:acme.workday/Messages.PermissionMissing`

The record holds:

```
ErrorMessage:            "Grant User.ReadWrite to the app registration…"
ErrorMessageDetailJson:  {"Catalogue":"connector:acme.workday/Messages","Key":"PermissionMissing","Arguments":[{"t":"User.ReadWrite"}]}
```

### Scenario 2: A language the Connector does not ship

**Given**: the same Connector ships English and French only
**When**: a German-speaking administrator opens the item
**Then**: it reads in the catalogue's English, key by key; nothing else on the page changes language

### Scenario 3: The Connector has been uninstalled

**Given**: the Connector was uninstalled after writing the record
**When**: anyone opens the item
**Then**: it reads from the stored catalogue, or, if that is gone, from the English stored on the record; never "PermissionMissing"

### Scenario 4: A catalogue with a broken translation

**Given**: a package whose French `PermissionMissing` uses `{1}` where the English has only `{0}`
**When**: an administrator installs it
**Then**: the install stops with "fr / PermissionMissing: uses {1}, but the English has only {0}", and nothing is stored

### Scenario 5: A built-in Connector author writes a string

**Given**: a change in `JIM.Connectors` adds `ConnectedSystemExportResult.Failed($"The directory refused {dn}.")`
**When**: the build runs
**Then**: the convention test fails, naming the file and line, and pointing to the Connector's catalogue

## Constraints

- Released migrations stay append-only; Part 2's storage is a new migration.
- No new third-party dependency; `.resx` and `ResourceManager` remain the authoring and built-in formats.
- British English source text; French and German follow `engineering/LOCALISATION.md` and the glossary.
- Material about this feature names no other identity product.

## Affected Areas

| Area | Impact |
|------|--------|
| Models | Message slots on `ConnectedSystemImportObject`, `ConnectedSystemImportResult` and `IConnectorProgress`; catalogue-source abstraction; `MessageRenderer` fallback and safe formatting |
| Connectors | Per-Connector catalogues; conversion of every person-facing string; declared-text keys |
| Worker | Carries the new message slots onto execution items, Activities and progress |
| Web | Declared Connector text read from catalogues instead of display maps; rendering through the catalogue sources |
| Database | Part 2: stored Connector catalogues |
| Tests | Convention test over `JIM.Connectors`; renderer fallback tests; Part 2 validation and SDK test base |

## Documentation Impact

| Doc | Change |
|------|--------|
| `engineering/developer/connectors.md` | New section, "Writing what your Connector says": messages, catalogue keys, the convention test, the other system's words |
| `engineering/LOCALISATION.md` | Connector catalogues, catalogue sources and the fallback order |
| `docs/` | Part 2: installing a Connector covers what its catalogue check refuses and why |

## Dependencies

- Part 2 depends on the Connector plugin model (packaging, loading and install), which is yet to be designed. Part 1 fixes no packaging choice.

## Open Questions

1. The Connector identity format used in catalogue names (`connector:<id>/<name>`): does the plugin model's Connector id serve, and is it stable across versions?
2. The catalogue file format inside a package: JSON compiled from `.resx`, or `.resx` itself validated at install?
3. Should Part 2 store catalogue versions per Connector version, or only the latest, given that records keep their English as the final fallback?
4. The size limit for a catalogue.

## Acceptance Criteria

- [ ] Import object errors, import warnings and progress narration accept messages, and the Worker stores them on execution items and Activities with their English.
- [ ] No reader sees a raw key: missing catalogue, missing key and malformed template all render the stored English; tested for JIM's own catalogues too.
- [ ] Catalogues resolve through a catalogue-source abstraction; Connector catalogues are named by Connector identity.
- [ ] Each built-in Connector has its own catalogue, every person-facing string it writes is a message with French and German, and its English is unchanged.
- [ ] Setting labels and help, phase names and capability descriptions come from the Connector's catalogue.
- [ ] The convention test fails on a string passed where a message belongs in `JIM.Connectors`.
- [ ] `engineering/developer/connectors.md` and `engineering/LOCALISATION.md` describe the rules.
- [ ] Part 2: an installed Connector's catalogue is validated, stored and rendered without loading its code; an invalid catalogue stops the install with its reason; uninstalling keeps history readable.

## Additional Context

- Concept design: https://claude.ai/artifact/6bsMK798pwRmHfaK8a7TE5
- Prior work: #3030 (portal localisation), #3039 (stored messages and API keys), #3084, #3088, #3094, #3099.
