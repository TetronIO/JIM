# Native Multi-Valued Date and Time Attributes

- **Status:** Doing (F0, the LDAP stop-gap, the fix-first issues #2179 to #2183, slice S1, canonical dates, and slice S3, plurality widening, delivered; slices S2, S4 to S6 and L1 outstanding)
- **Created:** 2026-10-12
- **Author:** Jay Van der Zant (drafted with Claude)
- **Issue:** [#2153](https://github.com/TetronIO/JIM/issues/2153)

> **How to read the references.** Every `file:line` was checked against the source on branch `claude/serene-cerf-3zn0qu` in October 2026. Line numbers drift, so treat them as pointers. A statement about a directory's own schema, a database engine or a library that cannot be checked in this repository is marked *(external)*. A statement that follows from verified code but was not observed running is marked *(inference)*.

## Problem Statement

Connected Systems hold multi-valued date and time attributes natively, and JIM's schema discovery already records many of them as multi-valued DateTime attributes. JIM itself holds one date per attribute from end to end. Depending on the Connector, a second value is dropped, an arbitrary value wins, or the whole attribute is cleared. None of these outcomes is reported to the administrator.

The Metaverse should be able to hold whatever a Connected System holds, without gaps and without silent mishandling. The one deliberate exception is a multi-valued Boolean, which carries no information a single value does not (see [The Boolean rule](#the-boolean-rule)).

### Where JIM holds one value today

| Where | What it does | Effect |
|---|---|---|
| Import contract, `ConnectedSystemImportObjectAttribute.cs:27-39` | One `DateTime? DateTimeValue` and one `bool? BoolValue` ("DateTimes are in practice, single-valued") | A Connector cannot hand JIM a second value |
| LDAP Connector, `LdapImportValueReader.cs:82-115` via `SingleTextValue` (357-363); `LdapConnectorImport.cs:1067-1077` | Reads nothing and reports nothing when a date or Boolean attribute has more than one value | The attribute is treated as empty and every stored value is removed (chain below) |
| SQL Connector, `SqlConnectorImport.cs:1331-1335` (called per related-table row) | Assigns rather than appends | One arbitrary row survives |
| SCIM Connector, `ScimResourceReader.cs:212-221` and the date branch after it | Assigns inside the loop over the array | The last element survives |
| File Connector, `FileConnectorImport.cs:224-239` | `GetField<DateTime>` and `GetField<bool>`, no delimiter split | One value at most |
| Import diff, `SyncImportTaskProcessor.cs:3743-3757` (Boolean twin at 3928-3945) | `SingleOrDefault` on the Connected System Object's values, scalar `!=` | Throws when two values are held; compares a 100ns import against a microsecond stored value |
| Import create, `SyncImportTaskProcessor.cs:3580-3596` | Adds exactly one row, even when the value is null | A null-holding row can reach change history, which throws |
| Content hash, `ImportContentHashCalculator.cs:173-178, 193-197` | Encodes one value | Consistent with the contract, so the gap never shows as a hash mismatch |
| Inbound Attribute Flow, `SyncEngine.AttributeFlow.cs:1147-1188` | `FirstOrDefault()` on the Connected System Object, `SingleOrDefault()` on the Metaverse Object | Only the first value flows; two Metaverse values throw an UnhandledError |

### The LDAP silent clear, link by link (verified)

1. `LdapImportValueReader.ReadDateTime` and `ReadBoolean` call `SingleTextValue`, which returns null for an attribute with two or more values (`LdapImportValueReader.cs:357-363`). Nothing is read and nothing is reported.
2. `LdapConnectorImport.cs:1073-1077` sets `DateTimeValue = null` and adds the attribute with no value.
3. `RemoveNullImportObjectAttributes` drops the empty attribute (`SyncImportTaskProcessor.cs:3105-3119`).
4. The absent-attribute branch then queues every stored value for removal (`SyncImportTaskProcessor.cs:3953-3957`).
5. No Run Profile Execution Item error is raised. The removal flows through Attribute Flow and can reach exports in other Connected Systems.

This happens on Full and Delta Import alike, because every LDAP delta source reuses the same conversion. A test pins today's behaviour, including hiding an unreadable second value (`LdapImportValueReaderTests.cs:126-150`).

### A precision fault that already bites single values

PostgreSQL stores `timestamptz` to the microsecond; .NET's `DateTime` holds 100ns ticks. Nothing in `src` normalises between them; the repository's own round-trip test truncates its inputs to make the comparison exact (`CsoBulkCreateColumnRoundTripDatabaseTests.cs:179-182`). The import diff compares the raw import with the stored value (`SyncImportTaskProcessor.cs:3751`), so a source value with a sub-microsecond part is "changed" on every honest diff. In Verification Mode the stored hash (computed from the raw value) matches the incoming hash while the diff finds a change, which raises a false `ImportHashVerificationFailed` "DANGEROUS DISAGREEMENT" (`SyncImportTaskProcessor.cs:2853-2872`).

### Evidence: attributes Connected Systems hold natively

| Connected System | Attribute | Syntax and plurality | Discovered by JIM today |
|---|---|---|---|
| Active Directory, Samba AD | `dSCorePropagationData` | GeneralizedTime, multi-valued *(external)* | Yes: AD discovery reads `systemmaycontain` (`LdapConnectorSchema.cs:195-198`), and omSyntax 23 and 24 map to DateTime (`LdapConnectorUtilities.cs:586`); the attribute's place in `top`'s system-may-contain set is *(external)* |
| Active Directory, Samba AD | `meetingStartTime`, `meetingEndTime` | UTC-Time, multi-valued, on the `meeting` class *(external)* | Yes, by the same path, where the class is discovered |
| Active Directory, Samba AD | `msDS-DateTime` | Named in the issue; not verified *(external)* | Confirm against the Samba AD schema during planning |
| OpenLDAP with the password policy overlay | `pwdFailureTime`, `pwdGraceUseTime` | GeneralizedTime with microseconds, multi-valued, `USAGE directoryOperation` *(external)* | **No.** RFC 4512 discovery builds an Object Type's attributes only from its classes' MUST and MAY sets, plus the External ID and `distinguishedName` (`LdapConnectorSchema.cs:487-543`), and import requests only selected attributes (`LdapConnectorImport.cs:805, 923`). See decision D8 |
| SQL databases | A related table of date rows | Always declared multi-valued (`SqlConnectorSchema.cs:236-243`) | Yes |
| SCIM service providers | A multi-valued `dateTime` attribute | Schema-defined | Yes |
| Delimited files | A repeated date column | Multi-valued only through repeated headers (`FileConnector.cs:296-313`) | Partly; see decision D6 |

## The Rules

These rules govern every design choice below.

### The set rule

A directory's multi-valued attribute is an unordered set of unique values. JIM compares multi-valued dates as sets: order never matters, duplicates collapse, and nothing is keyed on position.

### The precision rule

1. **JIM holds a date and time to the microsecond, in UTC.** One canonical form, `DateTimeAttributeValue.ToCanonical`: a `Local` value is converted to UTC, an `Unspecified` value is taken as UTC, and the result is floored to a whole microsecond with `Kind = Utc`. `DateTime.MinValue` and `MaxValue` pass through unchanged.
2. **Canonicalise once, where a value enters JIM**: the import normaliser, expression results, values the sync engine creates, Pending Export values, expression inputs, and REST DTO mapping.
3. **Compare canonically everywhere**: every comparison, set key, merge key and hash uses the canonical ticks (`long`), never `==` on raw `DateTime`, never a `Kind`-dependent string.
4. **Values that collide are reported, not merged silently.** Two different source values that canonicalise to the same microsecond are reported per value through the attribute value error channel (#2066), and the attribute is left as JIM held it.
5. **Floor, not round**, so a value never moves into the next second, minute or day. *(external)* Npgsql's own conversion truncates toward 2000-01-01, which rounds pre-2000 values up; flooring in JIM first makes the stored value exact for every year. A `RequiresPostgres` test pins this.

Decision D1 confirms the microsecond; full 100ns fidelity is the rejected alternative.

### The Boolean rule

A multi-valued Boolean can only hold {TRUE}, {FALSE} or {TRUE, FALSE}. The first two are one value; the third is a contradiction, not information. Therefore:

- **No surface creates a multi-valued Boolean Metaverse Attribute** (portal, REST, PowerShell).
- **A Boolean a Connected System declares multi-valued is materialised as single-valued**, with the change shown as a Plurality definition change in the schema refresh.
- **A second Boolean value on import is reported, never silently cleared or chosen.** JIM reports each value through the attribute value error channel and leaves the attribute as it held it.

## Goals

- A multi-valued date held by a Connected System imports every value, on Full and Delta Import, through all four Connectors. Verified by unit tests per Connector and by integration tests against OpenLDAP, Samba AD, SQL Server and Oracle.
- Every value flows to a multi-valued Metaverse Attribute and on to multi-valued Connected System attributes, with precedence, "Null is a value" and recall behaving exactly as for other multi-valued types.
- A second Full Import of unchanged data stages no change, including for values with sub-microsecond source precision.
- Nothing is cleared, truncated or chosen silently: a collision, a Boolean surplus or a value JIM cannot hold is reported per value on the Activity, on all three surfaces.
- The Metaverse and Connected System schemas obey the same type and plurality capability rule.
- No regression in import or synchronisation throughput for single-valued dates and Booleans (measured, not assumed).

## Non-Goals

- **Multi-valued Boolean.** Deliberately unsupported, by [the Boolean rule](#the-boolean-rule).
- **100ns fidelity.** JIM holds microseconds (D1). Sources that carry finer precision are floored, and collisions are reported.
- **Defining how expressions read multi-valued inputs.** That model is already decided in #2053 (Decision 8 of [PRD_COMPARISON_AND_EXPRESSION_VOCABULARY.md](PRD_COMPARISON_AND_EXPRESSION_VOCABULARY.md)). This PRD covers only the result handling and the date forms that multi-valued dates need (D5).
- **A File Connector multi-valued overhaul** (plurality override, repeated-header reads, whole-set export). Its gaps affect every type, not only dates (D6).
- **Retiring Npgsql's legacy timestamp switch or pinning the session time zone.** Separate concerns; this delivery makes attribute values correct regardless.
- **Out of scope, filed separately** (each affects every type): the REST list DTO is single-valued, last value wins (`MetaverseObjectHeaderDto.cs:75-78`); the capped multi-valued value search is string-only (`MetaverseRepository.cs:4287-4294`, `ConnectedSystemRepository.cs:2263-2267`); the date-only criterion editor (`CriterionValueEditor.razor:36`); Example Data generating one value per attribute.

## User Stories

1. As an administrator, I want every value of a directory's multi-valued date attribute imported and flowed, so that the Metaverse reflects what the directory holds.
2. As an administrator, I want JIM to tell me when it cannot hold a value exactly, so that I never discover silently lost data downstream.
3. As an administrator, I want a Boolean that a schema declares multi-valued handled as a single value, with a clear report if the directory ever holds both TRUE and FALSE, so that JIM never guesses.
4. As an administrator who mapped a multi-valued directory date to a single-valued Metaverse Attribute, I want to widen that attribute to multi-valued without deleting it, so that the errors I see after upgrading have a lossless fix.
5. As an administrator scripting JIM, I want the same rules, values and reports through the REST API and PowerShell as in the portal.

## Requirements

### Functional Requirements

1. The import contract carries a list of dates and a list of Booleans for every attribute (`DateTimeValues`, `BoolValues`), replacing the two scalars outright. Only the four built-in Connectors exist (`ConnectorFactory.cs:36-48`), so no compatibility shim is needed; a first-value shim would reintroduce silent loss.
2. Each Connector reads every value of a multi-valued date attribute and reports each value it cannot read, per value.
3. JIM canonicalises every imported date before hashing, deduplication and diffing, and reports colliding values per value.
4. JIM reports a second Boolean value per value and leaves the attribute as held, whatever the schema's declared plurality.
5. The import diff, inbound Attribute Flow, export staging, export confirmation, no-net-change detection, drift detection, the Pending Export merge key, optimistic export apply and the provenance inspector all treat dates as canonical sets.
6. An empty typed set (a Connected System Object holding only wrong-typed or value-less rows) is resolved through "Null is a value" and priority, never by removing values directly.
7. The export change type follows the **target's** plurality: a single-valued Connected System attribute always gets one Update carrying the surviving value; an Update against a multi-valued target replaces the whole set in every Connector.
8. No surface can create a multi-valued Boolean Metaverse Attribute, and REST rejects `Type = NotSet`.
9. Schema materialisation, type overrides and auxiliary-class contributions force a Boolean Connected System attribute to single-valued, recorded as a Plurality definition change.
10. An administrator can widen a Metaverse Attribute from single-valued to multi-valued while it holds values, losslessly and audited (D3).
11. Exported dates keep their fractional seconds where present (at the target's resolution, D2).
12. Every surface describes a JIM-raised import report accurately (not as "the Connector could not read this value").

### Non-Functional Requirements

- **Throughput.** Single-valued dates and Booleans keep an allocation-free fast path at every hot site: the hash encoder, the import diff, the Attribute Flow writers and the MVA->SVA guard (count distinct values only when there is more than one). The new contract lists have lazy backing fields, so attributes of other types allocate nothing new. A throughput gate (a BenchmarkDotNet case or a Scale scenario comparing before and after) runs before S1, S4 and S5 merge.
- **No quadratic comparisons.** Each side of a set comparison is turned into canonical keys once, into a `HashSet<long>`. This includes no-net-change detection, which builds its key set once per (Connected System Object, attribute) cache entry rather than per change. Note that `ProcessLongNumberAttribute` uses nested `Any` (`SyncEngine.AttributeFlow.cs:1085-1092`), so it is not the template to copy; the import side's Number set diff (`SyncImportTaskProcessor.cs:3684-3701`) and the reconciliation index (`SyncEngine.Reconciliation.cs:559-564`) are.
- **No new database round trips** on the import, synchronisation or export paths (F3's live read is the one exception, and only for a SCIM multi-valued simple attribute removal).
- **Logging.** Every batch operation logs summary counts: dates canonicalised, exact duplicates collapsed, collisions reported, Boolean surpluses reported, value-less rows swept. Per-object duplicate collapses log at Debug.
- British English, Title Case JIM product nouns, no em dashes.

## Design by Subsystem

### 1. Canonical date and time

A new `JIM.Utilities/DateTimeAttributeValue.cs`, beside `DecimalAttributeValue.cs`:

- `ToCanonical(DateTime)`: the precision rule above.
- `Key(DateTime) -> long`: canonical ticks, for sets, hashes and indexes.
- `ToRoundTripString`: invariant `yyyy-MM-ddTHH:mm:ss[.ffffff, trailing zeros trimmed]Z`, for merge keys and text forms.
- `ToDisplayString`: the portal's friendly form, with fractional seconds only when they are non-zero.

Applied where a value enters JIM:

- the import normaliser (section 3);
- `ExpressionResultConverter.cs:75-81`, which today calls `DateTime.TryParse` with the current culture and no styles. Strings parse with `DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, ...)` and take `UtcDateTime`, so a zoneless string is UTC and an offset is honoured. **Not** `RoundtripKind | AdjustToUniversal`: .NET rejects that combination with `ArgumentException`, even from `TryParse` (verified by a runtime probe), and the converter has no catch. A `DateTimeOffset` result gets its own branch;
- Metaverse values created by `ProcessDateTimeAttribute` and `CreateMvoAttributeValueFromExpressionResult`;
- Pending Export values from export expressions (`SyncEngine.ExportStaging.cs:518-519`) and Metaverse copies (717-718);
- expression input dictionaries (`SyncEngine.AttributeFlow.cs` `ToExpressionValue`, export staging, `DriftDetectionService` `BuildAttributeDictionary`), so expressions always see UTC;
- REST DTO mapping (`MetaverseObjectDto.cs:194`, `ConnectedSystemDto.cs:650`, Pending Export DTOs), so the API always emits `Z`.

Used as the key at every comparison site: the import diff; the sync writers; `ValuesMatch` (`SyncEngine.ExportStaging.cs:1121-1124`) and the removal tuple (660-665); `GetAttributeChangeKey` (1275-1290), which today uses `ToString("O")` and so gives a database-read `Local` value and an in-memory `Utc` value of the same instant different merge keys; reconciliation (`SyncEngine.Reconciliation.cs:559-564`); optimistic export apply; drift detection. The raw readers that set `Kind` (`ConnectedSystemRepository.cs:6444` uses `SpecifyKind(Utc)` on a `Local` read, which is the wrong instant on a non-UTC host; `MetaverseRepository.cs:1258, 2904`) canonicalise at the read boundary. EF-materialised values are canonicalised once, when their key set is built.

*Rejected:* an EF value converter on every attribute-value `DateTimeValue`. It would change every portal and API read, interacts with Npgsql's legacy timestamp switch (`src/CLAUDE.md`), and building key sets once already makes the conversion linear.

### 2. Import contract and Connectors

- **Contract.** `List<DateTime> DateTimeValues` and `List<bool> BoolValues`, with lazy backing fields. The XML docs state: JIM canonicalises dates; a Boolean holds one value, and JIM reports any further value; Connectors must not normalise.
- **Why Booleans become a list too.** The surplus rule then lives once, in JIM, rather than in four Connectors. `JIM.Scim` references only `JIM.Models` and `JIM.Utilities`, so it cannot reach the worker's internal reasons.
- **LDAP.** `ReadDateTime` and `ReadBoolean` read every value through `TextValues`, as `ReadNumbers` does; `SingleTextValue` is removed; `LdapConnectorImport.cs:1067-1077` uses `AddRange`. `TryReadFraction` already truncates to 100ns (`LdapImportValueReader.cs:289-290`); it also reports whether digits were dropped. When a truncated value equals another value of the same attribute, the reader reports both with the collision reason, so two directory values differing below 100ns are never merged silently. `LdapImportValues` gains an optional reason per unreadable value for this.
- **SQL.** `ApplyTypedValue` appends (`SqlConnectorImport.cs:1331-1335`), so a related table imports every row. Row order does not matter, because the values form a set.
- **SCIM.** The reader appends; `HasValue` checks the counts.
- **File.** The date and Boolean branches follow the Number pattern (`FileConnectorImport.cs:150-170`): split on the multi-value delimiter when the attribute is multi-valued, parse each value (dates with the configured culture and `AssumeUniversal | AdjustToUniversal`), and report each unreadable value. `GetField<DateTime>` is dropped. File plurality still comes only from repeated headers (D6).

### 3. Import pipeline: `ImportValueNormaliser`

A new pure class in `JIM.Application/Servers`, beside `ImportContentHashCalculator`, called per import object at the top of `ProcessImportObjectsAsync` (`SyncImportTaskProcessor.cs:2386`), before `HydrateCsoPageAsync` computes incoming hashes. Hash, dedupe, diff and persisted values therefore all see one form. `ProcessImportObjectsAsync` is the only page entry point for Full and Delta Import.

- **Lookup.** A per-run cache, following `GetTypeFingerprint`'s (`SyncImportTaskProcessor.cs:2117-2125`): Object Type id -> attribute name (case-insensitive) -> attribute, holding only the DateTime and Boolean attributes. The normaliser touches only import attributes found there.
- **DateTime.** Canonicalise every value. Exact duplicates collapse (Debug log, counted). Distinct raw values that canonicalise to the same microsecond are reported with `AddAttributeValueError`, once per raw value, rendered at seven digits so the difference shows. Reason: "Differs from another of this attribute's values by less than a microsecond, the finest time JIM holds, so JIM cannot keep both."
- **Boolean.** Collapse exact duplicates. When both true and false arrive, report each. Reason: "One of several values; a true or false attribute holds one, so JIM cannot tell which is meant." This applies whatever plurality the schema declares.
- **Guards.** Skip an object that already carries an object-level error, because `AddAttributeValueError` sets `ErrorType = AttributeValueError` unconditionally (`ConnectedSystemImportObject.cs:78`) and would turn a skip-the-object error into an import-the-rest one. Skip attributes the Connector already named in `AttributeValueErrors`. Return counts for the end-of-import summary.
- **Effect of a report.** The attribute is left as JIM held it (`ConnectedSystemImportObject.cs:42-53`), and the object is never eligible for the hash skip (`SyncImportTaskProcessor.cs:2243`). The report reaches all three surfaces through the existing `AttributeValueErrorsJson`.
- **Create** (`SyncImportTaskProcessor.cs:3580-3596`): one row per value, never a null-holding row.
- **Update.** Dates use the Number set diff (`SyncImportTaskProcessor.cs:3684-3701`), keyed canonically, over rows that hold a date. Stored values come from the database at microseconds and imports are canonical, so a value and its re-imported self are equal: the sub-microsecond churn ends. Booleans use the same shape, with no `SingleOrDefault`, so a Connected System Object holding {true, false} converges.
- **Value-less rows.** `RemoveValuesHeldUnderAnotherDataType` (`SyncImportTaskProcessor.cs:3972-3990`) also sweeps rows of an attribute the import carries that hold nothing. This depends on F5: today change history throws on such a removal (section 12).
- **Empty attributes.** `RemoveNullImportObjectAttributes` (3105-3119) tests the list counts.
- **Plurality at import.** Not enforced for single-valued attributes of other types, consistent with every type today: the Attribute Flow MVA->SVA guard reports it at synchronisation.

### 4. Content hash

- Dates encode as canonical, distinct, ascending ticks; Booleans as distinct, ascending bytes. The calculator canonicalises internally as well, mirroring its Decimal normaliser (`ImportContentHashCalculator.cs:53-55`).
- A fast path encodes zero or one value without LINQ, which makes byte-identity with version 1 true by construction: a single whole-microsecond date and a single Boolean encode exactly as today (`ImportContentHashCalculator.cs:173-178, 193-197`).
- **No algorithm version bump.** Every input whose bytes change is one whose stored state differs from the payload (sub-microsecond values, or values the old readers dropped), so it mismatches and gets the honest diff it needs; a false match cannot occur. A golden-hash test computed under version 1 pins this, and the class doc states the precise bump rule.
- **One-off waves remain, and are documented** (see [Upgrade and Migration](#upgrade-and-migration)): the type fingerprint hashes every declared attribute's plurality and Selected flag (`ImportContentHashCalculator.cs:114-121`), so forcing even an unselected Boolean to single-valued, or removing SCIM `primary` sub-attributes, changes the fingerprint of that Object Type once.

### 5. Inbound Attribute Flow

- **`ProcessDateTimeAttribute`** (`SyncEngine.AttributeFlow.cs:1147-1188`) becomes a canonical set diff over rows that hold a date: keys built once, values added canonically, then `TakeOverProvenance`. No `SingleOrDefault`, so two Metaverse values no longer throw, and multi-valued targets receive every value.
- **`ProcessBooleanAttribute`** takes the same set shape. Under the Boolean rule a source holds at most one value; legacy multi-valued Boolean targets and Connected System Objects are handled without throwing.
- **Empty typed set.** When a Connected System Object holds rows for the attribute but none of the writer's type, every typed writer calls `ApplyNoValueOutcome`, exactly as Text does (`SyncEngine.AttributeFlow.cs:180-183`). Today `ProcessNumberAttribute` and its siblings remove every value instead (1052-1055), bypassing "Null is a value" and priority.
- **MVA->SVA guard** (`SyncEngine.AttributeFlow.cs:160`) counts distinct canonical values among rows that hold the source type, only when more than one row exists. Stale wrong-typed rows (#2128) no longer raise a false MultiValuedToSingleValued.
- **Typed writers** ignore rows that do not hold their type, so a value-less row is never staged.
- **Provenance inspector** (`MetaverseServer.Provenance.cs:252-275`) uses canonical text.

### 6. Export staging and confirmation

- **Change type follows the target's plurality (F4).** Today it follows the Metaverse source's (`SyncEngine.ExportStaging.cs:589, 646-676`). So a multi-valued source with one value, flowing to a single-valued target, stages Remove-old plus Add-new: the SQL Connector turns both into assignments to one column in one `UPDATE` (`SqlConnectorExport.cs:708-728`), which databases reject *(external)*, and a merge keyed on the attribute id (`SyncEngine.ExportStaging.cs:255-275, 1290-1299`) can leave a lone Remove that clears the target. New rule: Add and Remove only when both source and target are multi-valued; otherwise one Update carrying the surviving value, or a null-clearing Update.
- **An Update against a multi-valued target replaces the whole set, in every Connector (F4).** LDAP already maps Update to Replace (`LdapConnectorExport.cs:1407`). The SQL Connector does not: "Anything that is not a removal adds a row" (`SqlConnectorExport.cs:459-508`), so a single-valued source exported to a related table appends a row on every change. The SQL Connector deletes the anchor's related rows, then inserts, in the parent's transaction. SCIM sends `replace`.
- **Exact-set rule for Update against a multi-valued target.** No-net-change (`IsCsoAttributeAlreadyCurrent`, `SyncEngine.ExportStaging.cs:1035-1039`, today `FirstOrDefault`) and confirmation (`SyncEngine.Reconciliation.cs:707-713`, today "the value exists among the values") both require the Connected System Object to hold exactly that one value.
- **Merge key.** `GetAttributeChangeKey` builds from the canonical `long` key.
- **Optimistic export apply** and **reconciliation** index on canonical keys.

### 7. Expressions (F1 and D5)

- **Output side (F1, fix-first).** `EvaluateExpressionSource` treats any non-string `IEnumerable` other than `byte[]` as an array (today only `string[]` and `IEnumerable<string>`, `SyncEngine.AttributeFlow.cs:624-628`). Each element converts to the target type and is diffed as a typed value; StringValue rows are never written to a typed attribute (today 1527-1535). A non-Text scalar compares as a typed value (today it compares `StringValue`, which is null for typed targets, so it restages on every synchronisation: 712-713). A scalar into a multi-valued target replaces the set, with no `SingleOrDefault` (692-693). A conversion failure records an `AttributeFlowError` and keeps the existing value (today the removal is staged first, then the conversion returns null with a Warning: 717-718, 1557-1562). Export expression arrays stage a set into a multi-valued target, an MVA->SVA error into a single-valued one, and never the literal `System.String[]` (`SyncEngine.ExportStaging.cs:530-533`).
- **Input side (D5).** How `cs["x"]` and `mv["x"]` read multi-valued inputs is #2053's Decision 8, amended on 12 October 2026 and delivered in #2053's multi-valued slice, not here.
- **Library changes that ship with this work (D5, approved 12 October 2026),** in the F1 slice unless noted:
  - **N1, one text form.** Expressions run under an invariant JIM culture, so a date becomes `2026-03-15 10:30:00Z` (a fraction only when non-zero) and numbers use `.`, whatever the host's `LANG`. One helper turns every non-text result into text (Text targets, export, Example Data, the tester, provenance). Binary used as text is an expression error.
  - **N2, one date-reading rule.** Text is read as a date only in ISO 8601 (`yyyy-MM-dd`, optionally `T` or a space and `HH:mm[:ss[.fraction]]`, then `Z`, an offset or nothing, meaning UTC) by `ToFileTime`, `FormatDate`, `Eq` and `ExpressionResultConverter`. `03/15/2026` and `15/03/2026` are refused.
  - **ParseDate, new.** `ParseDate(value, format)` reads text in an explicit .NET date format with the invariant culture, taking it as UTC unless the format carries an offset, and returns the canonical date. Null or empty text gives null; text that does not match the format is an expression error, so the attribute keeps its value rather than being cleared. It is the counterpart of `FormatDate`, and the only way to read a non-ISO date once N2 lands.
  - **N3, FormatDate widened.** `FormatDate(value, format)` takes a date or ISO text (N2); null, empty or unreadable text gives null.
  - **N4, canonical dates handed to expressions** (S1): UTC, floored to the microsecond, on every path.
  - **N5, one expression error.** The evaluator wraps every failure an expression can cause in one `ExpressionEvaluationException`; every consumer catches that type and reports an expression error, never an unhandled error, and Drift Detection no longer clears on a failure.
  - **N6, copies of Binary values** are handed to expressions, so an expression cannot change stored bytes.

### 8. Drift detection (F2, fix-first)

In `DriftDetectionService`: an expression result for a multi-valued target is wrapped and compared as a canonical set (today a scalar is never wrapped, so `CreateCorrectiveExports` takes an empty expected set and removes every value: 644, 981); an evaluation exception or a null result stages nothing (today both clear the target: 646-651, 1027-1040); an MVA->SVA guard for single-valued targets; Missing Input Behaviour applied; canonical values boxed.

### 9. Schema capability rule (S2)

- `AttributeDataTypeCapabilities.SupportsMultiValued(type)` in `JIM.Models`: false for Boolean, true for every other type. A completeness test covers every `AttributeDataType`, and every built-in Metaverse Attribute (`BuiltInMetaverseSchema.cs`) obeys it.
- **Metaverse.** `CreateMetaverseAttributeAsync` (`MetaverseServer.cs:340-360`, which validates nothing today) and `ChangeMetaverseAttributeSchemaCoreAsync` (1000-1020) throw a typed exception that REST maps to 400. REST also rejects `Type = NotSet` (`[Required]` on a non-nullable enum never fails). The portal dialog (`MetaverseAttributeEditorDialog.razor:38-50`) disables Multi-valued while Boolean is selected, with helper text. `New-JIMMetaverseAttribute` and `Set-JIMMetaverseAttribute` surface the 400.
- **Connected System.** Schema materialisation (`ConnectedSystemServer.cs` around 2779-2865) sets single-valued when the effective type is Boolean, respecting an administrator's type override, and records a Plurality definition change. The type-override validation moves out of `SynchronisationController.cs:321-361` and `ConnectedSystemSchemaTab.razor:1733-1745` into one server method; overriding to Boolean sets single-valued, on all three surfaces including `Set-JIMConnectedSystemAttribute -Type Boolean` (`Set-JIMConnectedSystemAttribute.ps1:143`).
- **Auxiliary classes.** A refresh carries contributed rows across unchanged (`ConnectedSystemServer.cs:2751-2765, 2843-2847`), and `AuxiliaryClassAttributeMerger.cs:134-158` copies a fixed field list. Both apply the capability rule, and copy any new per-attribute field (D2's resolution).
- **SCIM.** Rule 3 and 4 flattening (`ScimAttributeMapper.cs:114-132`) stops emitting the Boolean sub-attributes of a multi-valued complex attribute (`roles.primary`, `entitlements.primary`, `x509Certificates.primary`, from `ScimCoreSchemas.cs:198-207`). Flattening decorrelates `primary` from its entry, and as a single-valued Boolean it would report a surplus on every import of every resource with mixed flags.
- **SQL related tables of Booleans** become single-valued Booleans by the central rule. With F4, JIM's own exports replace rather than append, so the table holds one row per parent unless something else writes more, which is then reported. *Rejected:* refusing a Boolean related-table value column at discovery; it would break schema refresh for existing configurations and is unnecessary once JIM stops appending.
- **No conversion of existing attributes (D4).** None are expected, and none is converted: JIM is pre-release and environments are reset between development cycles, so the rule refuses multi-valued Booleans everywhere they can be created and leaves nothing to remediate.

### 10. Plurality widening (S3, D3)

- `ChangeMetaverseAttributeSchemaCoreAsync` allows a same-type single-valued to multi-valued change while values exist. Every other change stays blocked by stored values.
- **The impact gate has to change with it.** `AttributeSchemaChangeImpact.BlockedByValues` is true whenever any value exists (`AttributeSchemaChangeImpact.cs:35`); the controller returns 409 on that flag after applying the change (`MetaverseController.cs:775-776`); the dialog locks both selects from it (`MetaverseAttributeEditorDialog.razor:286-287`). The impact evaluates a proposed (type, plurality) pair and exposes whether it is a lossless widening; the controller gates on `Applied`; the dialog unlocks only the widening transition; the published 409 DTO comment and the cmdlet help (`Set-JIMMetaverseAttribute.ps1:13-16`) change too, since the generated references read them.
- **Lossless for export only because of F4.** Widening flips the source to multi-valued; without F4, every single-valued target of the widened attribute would switch from Update to Remove plus Add.

### 11. Export fidelity and target resolution (S6)

- **LDAP.** `ConvertDateTimeToLdapFormat` (`LdapConnectorExport.cs:2245-2249`, a literal `.0Z` today) uses the canonical value with the invariant culture. Whole seconds keep `.0Z`, so nothing changes for any value AD holds today; a non-zero fraction is written with up to six digits, trailing zeros trimmed.
- **SCIM.** `InstantFormat` (`ScimValueFormatter.cs:18`) adds a fraction only when it is non-zero; `ScimResourceWriter.Matches` (212-215) compares canonical instants.
- **A per-attribute DateTime resolution (D2)** on `ConnectedSystemObjectTypeAttribute` (nullable; null means microseconds), discovered per Connector: LDAP seconds for AD and Samba AD, microseconds for RFC directories; SQL from the column type (`datetime2(n)`/`timestamp(n)` to 10^-min(n,6) s, legacy `datetime` 10 ms, `smalldatetime` minutes, `date` days, Oracle `DATE` seconds); SCIM seconds with an administrator override; File microseconds.
  - **Deltas are computed on floored sets, never by flooring individual deltas.** Adds are floor(new Metaverse set) minus floor(old Metaverse set), and removes the reverse. With {A, B} in the same second for AD, removing A stages nothing, because B still needs that second.
  - Values that collapse at the resolution are reported through a new `AttributeFlowError` kind. No-net-change, confirmation, drift and the SQL related-table DELETE-by-value (`SqlConnectorExport.cs:446-497`) compare at the resolution.
  - The resolution and its SCIM override join the configuration snapshot (`ConfigurationSnapshotService.cs:560-575` lists a fixed set of fields today) with a classification, and the auxiliary-class merger copies it.
  - Existing Connected Systems hold a null resolution (today's behaviour) until their schema is refreshed; a refresh cannot happen at startup because it needs the Connected System. The upgrade notes say so.

### 12. Change history (F5, fix-first)

`AddChangeAttributeValueObject` throws `InvalidDataException` for the removal of a value-less row: `HeldDataType()` is null, so the recorded type falls back to the attribute's type, whose arm needs a value, and the default arm throws (`ConnectedSystemServer.cs:6564-6631`). `LinkUpdateChangeRecords` catches it and records an UnhandledError (6194-6211), which fails the Activity. The removal of a row that held nothing records no value change, logs at Information with the Connected System Object id, and is counted in the import summary.

### 13. Display and surfaces

- **Read parity already exists per value.** `MetaverseObjectAttributeValueDto` (`MetaverseObjectDto.cs:142-194`), `ConnectedSystemObjectAttributeValueDto` (`ConnectedSystemDto.cs:620-659`) and the Pending Export DTOs carry one entry per value; PowerShell passes them through.
- **One formatter** shows fractional seconds only when they are non-zero; each surface keeps its time-zone policy. Applied to `ToStringNoName` (`ConnectedSystemObjectAttributeValue.cs:140-141`), the change-history DTOs, provenance (`MetaverseRepository.cs:2224-2225`), the previews (`PreviewValueRenderer.cs:33-34, 61-62`, `ToString("O")` today, which depends on `Kind` and disagrees with the current-value column beside it), and the portal's attribute-value call sites through a new `Helpers.ToFriendlyAttributeDate`. Activity and audit timestamps keep `ToFriendlyDate`.
- **`[null]` fallbacks.** `PendingExportDetail.razor:542-566` (and the same helper in `ConnectedSystemObjectDetail.razor` and `ActivityItemPendingExportPanel.razor`) shows `[null]` for Boolean, LongNumber, Decimal and Guid values. Fixed in the same edit.
- **Import report wording.** `AttributeValuesNotImportedSection.razor:22-24` says "The Connector could not read this value"; the DTO summary (`ActivityRunProfileExecutionItemDetailDto.cs:66-67`) and three docs pages say the same. Collisions and Boolean surpluses are raised by JIM, so the wording becomes "could not import", and each entry carries a source category (Connector or JIM).

### 14. LDAP operational attributes (L1, D8)

The OpenLDAP attributes the issue names are operational, so discovery never offers them. RFC 4512 discovery adds each readable attribute type whose usage is not `userApplications` to every structural Object Type as an unselected, read-only attribute, tagged so the schema screen can group them. Selection is the existing control, so portal, REST and PowerShell parity holds without a new setting. Import already requests selected attributes by name, which is how a directory returns an operational attribute *(external)*. The OpenLDAP fixture's ACLs grant read access, and the password policy must record failures (the overlay is attached with no default policy today: `01-add-second-suffix.sh:294`).

## Examples and Scenarios

### Scenario 1: OpenLDAP password failure times

**Given** an OpenLDAP Connected System with `pwdFailureTime` selected (after L1) and mapped to a multi-valued DateTime Metaverse Attribute
**When** a user fails to bind twice and a Full Import, then a Delta Import, runs
**Then** the Connected System Object holds both values to the microsecond, the Metaverse Object holds both, and a second Full Import stages nothing.

### Scenario 2: a value JIM cannot keep apart

**Given** a SQL related table holding `2026-03-01 09:00:00.1234561` and `2026-03-01 09:00:00.1234569` for one person (`datetime2(7)`)
**When** a Full Import runs
**Then** the Activity shows two entries under Attribute Values Not Imported, each with the seven-digit value and the collision reason; the attribute keeps what JIM held; the object's other attributes import.

### Scenario 3: a Boolean holding both values

**Given** an RFC directory whose custom schema declares a Boolean multi-valued, and an entry holding `TRUE` and `FALSE`
**When** the schema is refreshed and a Full Import runs
**Then** the refresh shows a Plurality definition change to single-valued; the import reports both values with the surplus reason; the stored value is unchanged.

### Scenario 4: a single-valued source exported to a SQL related table

**Given** a single-valued Metaverse date mapped to a SQL related-table attribute holding one row
**When** the Metaverse value changes and the export runs
**Then** the related table holds exactly one row, the new value (today a second row is appended), and the export confirms on the next import.

### Scenario 5: widening after upgrade

**Given** a single-valued Metaverse Attribute fed by AD's `dSCorePropagationData`, which after upgrade reports MultiValuedToSingleValued
**When** the administrator widens it to multi-valued (portal, `PATCH .../attributes/{id}/schema`, or `Set-JIMMetaverseAttribute`)
**Then** the change succeeds with values in place, is audited, the next synchronisation flows every value, and every single-valued target of the attribute still receives one Update.

### Scenario 6: a multi-valued Boolean Metaverse Attribute is refused

**Given** an administrator creating a Metaverse Attribute
**When** they choose Boolean and Multi-valued (portal), post it (REST), or run `New-JIMMetaverseAttribute -Type Boolean -AttributePlurality MultiValued`
**Then** the portal disables Multi-valued with helper text; REST answers 400 naming the rule; PowerShell surfaces the same message.

## Upgrade and Migration

JIM is pre-release and environments are reset between development cycles, so nothing in this work converts or repairs existing data. The notes below describe what a long-lived environment would see once, so the changelog can say so honestly.

- **No schema migration for multi-valued dates.** Values are already one row each, every column is `timestamptz`, and there is no unique index on `DateTimeValue` (`JimDbContext.cs:982-997`). Only D2 (a) adds a migration: one nullable resolution column on `ConnectedSystemObjectTypeAttributes`; extend any raw writer's `*BulkColumns` constant if one touches that table, and regenerate the migration if `main` gains migrations first.
- **Import contract.** Renamed outright; about 80 test references are updated mechanically.
- **Content hash.** Version 1 kept (section 4).

One-off waves after upgrade, each announced in the changelog:

1. **Restored values.** LDAP attributes with two or more dates that the old reader cleared are restored on the first import; SQL related tables and SCIM arrays of dates gain their missing values. These produce Connected System Object changes, Metaverse flows and possibly exports.
2. **Appended SQL rows surface.** Related tables that past single-valued exports appended to (every type before F4; dates and Booleans become visible with S5) read as multi-valued. Where JIM exports to them, the next export evaluation replaces the set; where they feed a single-valued Metaverse Attribute, MultiValuedToSingleValued is reported until cleaned.
3. **Sub-microsecond values** change once to the floored value; pre-2000 values that Npgsql rounded up change once *(inference)*.
4. **Honest re-diffs.** The first Full Import after the S2 schema refresh re-diffs every object of each Object Type whose fingerprint changed (a Boolean forced to single-valued, selected or not; SCIM `primary` sub-attributes removed). Administrators can refresh the schema before a quiet import window. Objects carrying dates the old readers dropped re-diff once. File Connector dates read with `Kind = Local` on a non-UTC host change canonical ticks once *(inference)*.
5. **Schema refresh changes.** Every multi-valued-declared Boolean becomes single-valued; SCIM `primary` sub-attributes are reported as removed. A test confirms how a removed attribute that a mapping references is handled (`ConnectedSystemServer.cs:2756-2765`) before S2 merges.
6. **Less churn.** Expression mappings to non-Text targets stop restaging (F1).
7. **Loud, correct errors.** Configurations that map a multi-valued directory date to a single-valued Metaverse Attribute start seeing MultiValuedToSingleValued once real values arrive. Widening (D3) is their lossless fix; no surface can clear Metaverse values, so without it they must delete and recreate the attribute.
8. **Persistent reports cost throughput.** An object with a reported collision or surplus is never hash-skipped and writes an error item on every run until the source changes.

Nothing in the session time zone or the Npgsql legacy switch changes.

## Surface Parity

| Capability | Portal | REST | PowerShell |
|---|---|---|---|
| Read multi-valued dates | Per-value lists with the shared formatter | Existing per-value DTOs, always `Z` | Existing passthrough |
| Boolean is never multi-valued (Metaverse) | Dialog disables Multi-valued | 400; also for `Type = NotSet` | `New-`/`Set-JIMMetaverseAttribute` surface the 400 |
| Boolean is never multi-valued (Connected System override) | Schema tab, shared server method | `SynchronisationController`, same method | `Set-JIMConnectedSystemAttribute -Type Boolean` |
| Widening (D3) | Plurality select unlocked for this one transition | `PATCH .../schema` returns 200 | `Set-JIMMetaverseAttribute` |
| Collision and surplus reports | Attribute Values Not Imported, new wording | `AttributeValueErrorsJson` on the item DTO, new summary | Existing activity cmdlets |
| DateTime resolution (D2) | Schema tab column, SCIM override | Attribute DTO and update request | `Set-JIMConnectedSystemAttribute` parameter |
| Operational attributes (D8) | Existing attribute selection, grouped by tag | Existing | Existing |

## Constraints

- No new third-party dependency; air-gapped; no cloud services.
- Migrations are append-only; released migrations are frozen.
- Raw SQL column lists come from `*BulkColumns` constants.
- Synchronisation integrity: fast failures over corrupted state, every error on the Activity, summary statistics per batch.
- UI changes need artefacts: the attribute editor (Boolean and widening), the resolution column, a multi-valued date value stack with fractions, the reworded import report.

## Affected Areas

| Area | Impact |
|---|---|
| `JIM.Utilities` | New `DateTimeAttributeValue` |
| `JIM.Models` | Import contract lists; capability rule; impact model for widening; resolution field (D2) |
| `JIM.Connectors` | LDAP, SQL, SCIM and File readers; SQL related-table Update as replace; LDAP and SCIM date formats; LDAP operational attributes (D8) |
| `JIM.Worker` | `ImportValueNormaliser` call, set diffs, sweep, summary counts |
| `JIM.Application` | Hash, Attribute Flow writers, export staging and confirmation, drift detection, expressions, schema materialisation, auxiliary merger, `MetaverseServer` rules, change history |
| `JIM.Web` | Attribute editor dialog, schema tab, import report wording, display formatter, `[null]` fallbacks, controllers |
| `JIM.PowerShell` | Metaverse and Connected System attribute cmdlets, `Test-JIMExpression` (D5), help text |
| Database | None for the core; one nullable column for D2 (a) |

## Test Plan

Every slice is test-first; every bug fix starts with a test that reproduces it.

- **`DateTimeAttributeValueTests`:** each `Kind`; flooring of positive and pre-2000 ticks; MinValue and MaxValue; key equality across kinds; round-trip string; display string.
- **`RequiresPostgres` round trips** (the `CsoBulkCreateColumnRoundTripDatabaseTests` pattern, deliberately untruncated): Connected System Object, Metaverse Object, Pending Export and change-value writers persist several dates for one attribute, including a seventh-digit value, `1999-12-31T23:59:59.9999995` and values near MinValue and MaxValue, and read back equal to `ToCanonical(input)`.
- **`ImportValueNormaliserTests`:** exact duplicates collapse; a sub-microsecond pair gives one error per raw value and leaves the attribute as held; {true, false} gives two errors; {true, true} collapses; an object-level error is untouched; Connector-flagged attributes are skipped; local and unspecified inputs canonicalise.
- **Import processor:** multi-valued add, remove, order-only no-op, payload duplicate, value held under another type, value-less row swept; create with N values and no null rows; a `.1234567` value unchanged on its second import; a Verification Mode regression (no false `ImportHashVerificationFailed`); {true, false} converges.
- **`ImportContentHashCalculatorTests`:** a multi-valued date fixture; order and duplicate insensitivity; a sub-microsecond pair; the version 1 golden hashes.
- **Connectors:** LDAP replaces the pinned "reads nothing" tests (`LdapImportValueReaderTests.cs:126-150`) with two values read, an unreadable second value reported, two Booleans read, and a sub-100ns collision reported; Full and Delta paths. SQL: three date rows import three values; Update on a related table replaces the rows. SCIM: a date array imports every element; F3's replace is built from a live read, including a provider holding a value JIM could not read. File: delimited dates and Booleans, per-value reports, zoneless and offset strings under a non-invariant culture.
- **Attribute Flow:** {A, B} to the Metaverse; {A, B} against {B, C}; the same instant in two kinds is not a change; one value against two Metaverse values (today's throw); "Null is a value" and priority abstention with a Connected System Object holding only a wrong-typed row, for every typed writer; recall and re-election; the MVA->SVA guard ignores wrong-typed rows; a legacy multi-valued Boolean target does not throw.
- **Export:** change type by target plurality (a multi-valued source with one value into an LDAP single-valued attribute, a SQL scalar column and a SCIM single-valued attribute); a widened attribute into the same three; the Kind-insensitive merge key; confirmation of a database-read value; the exact-set rule for Update against a multi-valued target; D2 floored-set deltas with {A, B} in one second, then remove A.
- **F1:** an unchanged expression into a DateTime or Number target stages nothing; a refused conversion keeps the value and records an error; `Z`, offset and zoneless strings; `DateTime[]` into a multi-valued target is stored typed; an export `string[]` is a set, never `System.String[]`.
- **F2:** an expression into a multi-valued target with matching values finds no drift; a throw or null stages nothing; a multi-valued source into a single-valued target reports the flow error.
- **F5:** removing a null date row and a null Boolean row through `LinkUpdateChangeRecords` with change tracking on records no error.
- **S2:** server tests for create and change; Web.Api 400s (Boolean multi-valued, NotSet); a bUnit test of the dialog; Pester for `New-`, `Set-JIMMetaverseAttribute` and `Set-JIMConnectedSystemAttribute -Type Boolean`; schema refresh forces single-valued and records the change; auxiliary-merger cases; SCIM `primary` no longer emitted; a `RequiresPostgres` `NoTracking` test for any new mutating repository method.
- **S3:** widening succeeds with values present and is audited; every other change stays 409; Web.Api, bUnit and Pester.
- **Integration** (`Run-IntegrationTests.ps1`):
  - OpenLDAP: a test-schema, user-application, multi-valued GeneralizedTime attribute (a test LDIF in the fixture), Full and Delta Import, export Add and Remove, a second import that stages nothing. With L1: `pwdFailureTime` after two failed binds, once the fixture's policy records failures.
  - Samba AD: import of a multi-valued date the directory maintains or the test can write (`dSCorePropagationData` or `meetingStartTime`; the plan picks one after checking Samba's schema and behaviour, which is unverified here).
  - SQL Server and Oracle (Scenario 016, which already runs a non-UTC database time zone): a date related table (N values, export Add and Remove, confirmation, a second import that stages nothing) and a Boolean related table.
  - Scenario 008 at Medium for every slice touching export staging or reconciliation, and a Medium run of an import-heavy scenario for S1 and S5 (a single-page run is not a sign-off).
- **Runtime verification** on the devcontainer stack after each slice: rebuild worker and web, query the value rows, confirm a second Full Import stages nothing, and check the reports on all three surfaces.

## Documentation Impact

| Doc | Change |
|---|---|
| `docs/connectors/jim-ldap-connector.md` | Dates read per value; multi-valued GeneralizedTime; Booleans hold one value; microsecond precision; export format; operational attributes (D8) |
| `docs/connectors/jim-sql-connector.md` | Related tables of dates import every row; an Update replaces a related table's rows; Boolean related tables hold one value. Keep the watermark precision statement at line 270 separate from attribute-value precision. Resolution by column type (D2) |
| `docs/connectors/jim-scim-connector.md` | Multi-valued `dateTime`; `primary` sub-attributes of multi-valued complex attributes no longer discovered; multi-valued simple attributes export as a replace built from a live read |
| `docs/connectors/jim-file-connector.md` | Dates and Booleans split on the multi-value delimiter; date parsing culture; Booleans single-valued |
| `docs/configuration/metaverse.md` | The type and plurality rule; widening (D3); precision |
| `docs/configuration/connected-system-attributes.md`, `docs/configuration/connected-system-schema.md` | Boolean override sets single-valued; the new Plurality definition change; resolution and its override (D2) |
| `docs/configuration/activity-execution-items.md`, `docs/powershell/activities.md`, `docs/concepts/synchronisation-pipeline.md` | Import reports raised by JIM as well as by Connectors |
| `docs/powershell/metaverse.md`, `docs/powershell/connected-systems.md` | Generated from cmdlet help and DTO comments, so change the sources the generator reads |
| `docs/concepts/expressions.md` | Typed array results (F1); collection accessors and functions (D5); check the `FormatDate` example (line 192), which a design review reported does not evaluate *(unverified)* |
| `engineering/developer/connectors.md` | The import contract: every type is a list; JIM canonicalises dates and reports collisions and Boolean surpluses; Connectors do not normalise; a Connector that can see sub-100ns differences reports colliding values |
| `engineering/notes/PENDING_EXPORT_LIFECYCLE.md` | Merge key built from canonical keys (lines 58-63) |
| `ImportContentHashCalculator` class doc | Canonical date encoding; the precise version-bump rule |
| `src/CLAUDE.md:86` | The "Npgsql returns DateTimeOffset" note did not reproduce under Npgsql 10.0.3 in a design probe; correct it and point to `DateTimeAttributeValue` (needs the user's go-ahead, as it edits a CLAUDE.md) |
| `CHANGELOG.md` | Per slice: the feature; each fix; the upgrade waves |

## Delivery Slices

Fix-first PRs go to `main` independently; S1, S4, S5 and S6 form a stack in that order; S2 and S3 are independent PRs merged before S5. Native blocked-by links record the order.

| Slice | Content | Depends on | Changelog |
|---|---|---|---|
| F0 (D7) | LDAP stop-gap: report two or more date or Boolean values instead of clearing | none | Fix |
| F1 | Expression results typed, inbound and on export | none | Fix |
| F2 | Drift detection correctness | none | Fix |
| F3 | SCIM multi-valued simple export as a replace built from a live read and its ETag | none | Fix |
| F4 | Export change type by target plurality; Update replaces a multi-valued target's set (SQL related tables); exact-set confirmation and no-net-change | none | Fix |
| F5 | Change history records the removal of a value-less row without throwing | none | Fix |
| S1 | Canonical value adopted everywhere while the contract is single-valued; hash fast path; read-boundary canonicalisation; REST `Z`; lifecycle note | none | Fix (sub-microsecond churn, false Verification Mode alarm) |
| S2 | Boolean capability rule on every surface; materialisation, overrides, auxiliary merger; SCIM `primary`; Boolean set writer | F4 | Changed |
| S3 (D3) | Lossless widening with the impact gate redesign | F4 | Added |
| S4 | Attribute Flow set writers; empty typed set via `ApplyNoValueOutcome`; MVA->SVA counting; exact-set no-net-change; provenance | S1 | Fix (first value only; Metaverse throw) |
| S5 | Import contract lists; normaliser; processor set diffs and sweep; hash lists; four Connector readers; display formatter, `[null]` fallbacks, report wording | S4, S2, S3, F5 | Added, plus fixes |
| S6 | Export fidelity; D2 resolution with floored-set deltas | S5, F4 | Changed (and Added for D2) |
| L1 (D8) | LDAP operational attribute discovery; ppolicy integration test after S5 | none | Added |

## Risks

- **Npgsql behaviour** (truncation toward 2000, `Local` reads) was observed only in a scratch probe *(inference)*. The `RequiresPostgres` round trip gates S1.
- **The in-memory repository** keeps 100ns values and shares instances, so unit tests cannot see precision faults; only `RequiresPostgres` tests and runtime imports prove S1 and S5.
- **Not bumping the hash version** departs from the class's conservative rule; the golden test pins the safety argument. Bumping costs one honest re-diff of every object.
- **Persistent collision reports** leave an attribute stale with an error on every run until the source changes; correct, never silent, but noisy for pathological data.
- **Ordering.** S5 before S4 would send N dates into the old `SingleOrDefault`; the stack enforces S4 first.
- **F3's replace** can still race a provider without ETag support between the read and the write.
- **AD and fractions.** Whether AD and Samba AD accept GeneralizedTime fractions is unverified; S6 keeps `.0Z` for whole seconds, and D2 (a) avoids sending fractions to second-resolution targets.
- **Under D2 (b) or (c)**, sub-second values exported to coarser targets never confirm and retry until they fail.
- **The ppolicy fixture** may need a policy that records failures before L1's test can pass; the harness prunes the `busybox` image in sandboxes.

## Dependencies

- #2066 (attribute value error channel) and #2128 (LDAP typed readers, values held under another type): both landed; this work builds on them.
- Fix-first slices F1 to F5 are independent of each other.

## Decisions

Decided by the product owner on 12 October 2026.

1. **D1. Precision:** microseconds, UTC, floored, collisions reported.
2. **D2. Target resolution for export:** a per-attribute resolution, discovered by each Connector, in S6.
3. **D3. Widening while values exist:** allowed, before S5, on all three surfaces.
4. **D4. Multi-valued Boolean Metaverse Attributes:** never supported. None exist, and none are converted: JIM is pre-release and environments are reset between development cycles, so no legacy data is remediated.
5. **D5. Expressions:** N1 to N6 and the new `ParseDate` ship with this work (section 7). The multi-valued input model stays with #2053's Decision 8, amended the same day (Count, Min and Max added; CollectionContains, HasAttribute and AttributeNames removed; Join keeps the order values are read in; one definition of no value; no upgrade report), with DynamicExpresso late binding adopted only after a security review, and a save-time refusal of `==` on untyped values as the fallback.
6. **D6. File Connector multi-valued gaps:** a separate issue.
7. **D7. LDAP stop-gap:** yes, F0 now, and the full fix (S5) is part of this work and tracked here.
8. **D8. OpenLDAP operational attributes:** discovered as unselected, read-only attributes (L1).

## Open Questions

None. Every decision is recorded above.

## Acceptance Criteria

- [ ] Each of the four Connectors imports every value of a multi-valued date attribute, Full and Delta, and reports each unreadable value
- [ ] A second Full Import of unchanged data stages no change, including values with a sub-microsecond part
- [ ] Verification Mode raises no false disagreement for sub-microsecond values
- [ ] Collisions and Boolean surpluses are reported per value on portal, REST and PowerShell, with accurate wording, and leave the attribute as held
- [ ] Inbound Attribute Flow, export staging, confirmation, no-net-change, drift detection, merge and optimistic apply treat dates as canonical sets
- [ ] A single-valued Connected System target always receives one Update; an Update against a multi-valued target replaces its set in every Connector
- [ ] No surface can create a multi-valued Boolean Metaverse Attribute; schema refresh, overrides and auxiliary classes make Boolean single-valued
- [ ] Widening works with values present on all three surfaces (D3)
- [ ] OpenLDAP, Samba AD, SQL Server and Oracle integration tests pass; Scenario 008 passes at Medium
- [ ] Single-valued date and Boolean throughput is not measurably worse
- [ ] Docs and changelog updated per slice

## Additional Context

- Design review (three critiques) changed this PRD in these ways: F4 and F5 were added as prerequisites; F3 reads the live resource; D2 computes deltas on floored sets; widening needs the impact gate redesigned; operational attributes became D8; the expression parse styles were corrected; empty typed sets go through `ApplyNoValueOutcome`; fast paths, key sets and a per-run lookup were added for throughput; the hash-wave claim was corrected; SQL integration coverage joined the plan.
- Related: #2066, #2128.
