# One Vocabulary for Comparisons and Expressions

- **Status:** Planned
- **Created:** 2026-10-11
- **Author:** Drafted by Claude Code for product owner review
- **Issue:** [#207](https://github.com/TetronIO/JIM/issues/207) (parent), with sub-issues [#2051](https://github.com/TetronIO/JIM/issues/2051) (case-sensitivity policy), [#2052](https://github.com/TetronIO/JIM/issues/2052) (Phase 1), [#2053](https://github.com/TetronIO/JIM/issues/2053) (Phase 2), [#2054](https://github.com/TetronIO/JIM/issues/2054) (Phase 3)
- **UI mocks:** [Matching Expressions (#207)](https://claude.ai/artifact/U7urHxpx49uGm4EBi5Ckme) (Claude artifact, private to the product owner)

## Problem Statement

### Object Matching cannot match values that are formatted differently

An Object Matching Rule compares one Connected System attribute with one Metaverse attribute for equality, optionally ignoring case. That is the whole of it. Real systems format the same identifier differently:

- an HR system holds `EMP-48213` where the directory holds `48213`;
- an account name arrives with trailing whitespace;
- a key is only unique once two attributes are taken together.

When values differ like this, the rule matches nothing. On import, the account projects a duplicate identity instead of joining the existing one. On export, JIM provisions a second account beside the one that already exists.

The Metaverse side can already be reshaped, through an import Attribute Flow or a derived Metaverse attribute (#1750). The Connected System side cannot. There are no derived Connected System attributes, and a matching rule cannot transform its source.

The data model anticipated this. `ObjectMatchingRuleSource.Expression` exists and is documented as "DynamicExpresso syntax", but nothing evaluates it, and every surface either refuses or ignores it:

- the Advanced mode dialog shows "Sorry, expressions are not yet supported";
- the REST API has no field for it;
- the configuration preview blocks it;
- on import, the repository throws `InvalidDataException`, which `ObjectMatchingServer` swallows into a log warning before trying the next rule.

### The original proposal would have made matching less safe

#207 proposed new matching operators: Contains, StartsWith, EndsWith and Regex. They would make matching worse:

- **A rule runs in both directions.** It joins on import (Connected System value, find a Metaverse Object) and checks for an existing account before provisioning on export (Metaverse value, find a Connected System Object). Equality reverses cleanly. "Contains" has no meaningful reverse, so export would either provision without checking or ask the administrator which side contains which.
- **A fuzzy match is an ambiguous or wrong join.** `123` is contained in `1234` and `51234`. A wrong join sends one person's data to another identity, which is the most damaging failure an identity system has.
- **Only equality uses the indexes.** The attribute value indexes are b-trees (plain and `lower()`). `LIKE '%x%'`, suffix matching and regex scan every value of the attribute on every join attempt.
- **Every example in the issue is "normalise, then compare equal".** Normalisation is a transform, and JIM already has a transform language.

### JIM has five vocabularies for "compare"

Administrators express comparisons in five places, and each has its own vocabulary and rules:

| Where | Vocabulary | Runs in | Operators | Defects found (2026-10-11) |
|---|---|---|---|---|
| Synchronisation Rule scoping | `SearchComparisonType` | .NET | 12 | No "has a value" or "has no value"; only an Equals with no value, through the API |
| Predefined Searches | `SearchComparisonType` | PostgreSQL | 12 | `%` and `_` in a value act as wildcards, so a search can disagree with scoping on the same criterion |
| Example Data dependencies | `ComparisonType` (a second enum, with different member names) | .NET | 7 declared, 1 runs | Six advertised operators throw `NotSupportedException` at generation time |
| Expressions | Functions and C# operators | .NET | `StartsWith`, `EndsWith`, `Contains`, `Eq`, `==`, `<`, `>` | `StartsWith`/`EndsWith` compare by culture (en-GB through ICU), `Contains` and `Eq` by ordinal; no ignore-case option; no regex |
| Object Matching | Implicit equals | PostgreSQL | 1 | Case-sensitive default differs by surface; expression source modelled but never evaluated |

The same intent ("starts with Fin") therefore behaves differently in a scoping criterion, a Predefined Search and an expression. An administrator has to learn three sets of rules to predict JIM, and the differences show up only as surprising results.

### Case sensitivity is documented once and implemented several ways

`docs/concepts/case-sensitivity.md` states one policy. An audit of every comparison site found:

- the code departs from it in External ID matching, reference matching, LDAP renames, two Connectors, several configuration-name lookups and two PowerShell cmdlets;
- the policy contradicts itself on whether linking identities should respect case.

See [Case sensitivity: one policy](#case-sensitivity-one-policy).

## Decisions

Recorded with the product owner on 2026-10-11.

1. **Object Matching stays equals-only and gains expression sources.**<br /> The value is transformed, then compared for equality. No fuzzy operators are added to matching. Where the Metaverse side needs reshaping too, the rule matches on a derived Metaverse attribute, which is stored and therefore indexed.
2. **One vocabulary, two shapes.**<br /> "One system" means one operator catalogue, one function library and one meaning for each, shared by structured criteria and free-text expressions. It does not mean replacing criteria with expressions, because that would lose:
   - SQL execution for searches and matching lookups;
   - plain-English scoping explanations;
   - save-time validation;
   - the simplest authoring path for the common case.
3. **Delivery is phased as sub-issues of #207:**
   - #2051, the case-sensitivity policy;
   - #2052, Phase 1;
   - #2053, Phase 2;
   - #2054, Phase 3, blocked by #2053.
4. **Export matching for an expression rule uses a per-run match value map.**<br /> The map is built lazily, once per rule and Connected System Object Type, over unjoined Connected System Objects. Making expression rules import-only was rejected: it leaves a window in which JIM provisions a duplicate account.
5. **Case sensitivity: awaiting confirmation.**<br />
   - The decision first approved was "align Object Matching to the documented case-sensitive default".
   - Validating that against the documented policy and the code overturned it. The policy contradicts itself, and the code already treats identifiers as case-insensitive in External ID and reference matching.
   - The revised proposal is the four-question policy below, under which Object Matching ignores case by default unless the attribute is declared case-exact. This needs the product owner's confirmation before #2051 starts.

## Goals

- An administrator can join and match objects whose identifiers differ only in format, by matching on an expression over the Connected System Object. This works in Simple and Advanced matching mode, on import and export, across the portal, REST API and PowerShell.
- A matching failure never creates an identity or an account. It is reported on the object, and the object is retried on the next synchronisation.
- What a matching rule computed is visible:
  - when authoring (Try it);
  - before saving (Preview Impact);
  - after joining (connection explanations and Activity items).
- One operator catalogue drives every criteria editor, API validation and PowerShell `ValidateSet`. Every operator means the same thing in .NET and in SQL, enforced by tests.
- One function library drives the documentation, the expression editor's suggestions and save-time validation. Comparison functions mean exactly what the matching operators mean.
- One case-sensitivity policy, organised by the question being asked, governs every comparison in JIM. Enforcement makes a culture-sensitive comparison impossible to reintroduce.
- No measurable regression in the Scenario 014 performance baselines.

## Non-Goals

- Contains, StartsWith, EndsWith, Regex or any other non-equality operator on Object Matching Rules.
- Metaverse-side expressions in a matching rule. A derived Metaverse attribute does this, stored and indexed.
- Multi-source matching rules. An expression can combine several Connected System attributes; the Metaverse side combines through a derived attribute.
- Persisting computed match values on Connected System Objects. Revisit only if Scenario 014 shows the per-run map is too costly.
- Replacing structured criteria with expressions, or a free-form "expression is true" criterion.
- Expression sources or regex in contexts that run as SQL (Predefined Searches, matching lookups).
- Reference operators ("is a member of"). The catalogue makes room for them; #1463 delivers them.
- Curated transform recipes. The function library is their foundation; #1128 delivers them.
- Replacing the expression engine (DynamicExpresso).
- A configurable missing-input behaviour on matching rules. An absent input means no match value, as an absent attribute does today.

## User Stories

1. As an identity administrator joining an HR system to Active Directory, I want to match `Trim(Replace(cs["employeeNumber"], "EMP-", ""))` against Employee ID, so that accounts join the right identity instead of projecting duplicates.
2. As an administrator provisioning to a directory that already holds unmanaged accounts, I want JIM to find an existing account by a computed value before creating a new one, so that JIM never creates a second account for the same person.
3. As an administrator building a rule, I want to see the value my expression computes for a sample and, before saving, for my real objects, so that I can fix a rule that would miss or misjoin before it affects anyone.
4. As an administrator investigating a join, I want to see which rule joined the objects and the value it computed, so that a surprising join explains itself.
5. As an operator, I want a matching rule that cannot be evaluated to stop the object with an error, so that a broken rule never fills the Metaverse with duplicates.
6. As an administrator who automates JIM, I want expression rules in the REST API and PowerShell with the same validation as the portal, so that my scripts can configure everything the portal can.
7. As an administrator writing scoping criteria, Predefined Searches and expressions, I want "starts with" to mean the same thing in all of them, with the same case options, so that I learn one set of rules.
8. As an administrator, I want to say "has a value", "has no value" and "matches this pattern" in scoping, so that I stop working around missing operators.
9. As an administrator writing expressions, I want the editor to suggest functions and show what they do, so that I don't have to keep the function reference open beside me.
10. As an administrator scoping a Synchronisation Rule, I want a criterion to compare a reshaped value, so that `Lower(Trim(cs["department"]))` starts with "fin" covers every way HR writes Finance.
11. As an administrator, I want JIM to treat values that name the same identity the same way everywhere (joining, uniqueness, External IDs and references), so that `JBloggs` and `jbloggs` are never one identity in one place and two in another.

## Requirements

### Functional Requirements

#### Phase 1: Object Matching Rules match on an expression (#2052)

1. **Source.** An Object Matching Rule has exactly one source: a Connected System attribute, or an expression.
   - Saving a rule with no source, more than one source, or a source carrying both is refused on every surface. Today the REST API accepts several sources, and the matching engine skips them at run time.
2. **Expression context.** A matching expression reads the Connected System Object being matched, through `cs["..."]`.
   - It cannot read `mv["..."]`: the Metaverse side of the comparison is always the rule's Metaverse attribute.
   - Attribute names resolve ignoring case, as in every expression.
3. **Validation at save.** These are refused with a message naming the fault:
   - an expression that does not parse;
   - one that reads `mv["..."]`;
   - one that reads no attribute;
   - one longer than the expression length limit;
   - one that uses a function returning a different value on each evaluation (`Now`, `Today`, `RandomPassword`, `RandomPassphrase`, per `NonRepeatableFunctionDetector`).
   - The portal's **Add Matching Rule** stays disabled while any of these hold.
4. **Result.** The expression's result becomes the match value or values:
   - **No value:** null, an empty string or a whitespace-only string means no match value, and the next rule is tried.
   - **Several values:** a multi-valued result (a string array) is tried value by value, exactly as a multi-valued attribute source is today.
   - **Type:** each value is converted to the Metaverse attribute's data type, using the same conversion Attribute Flow applies to expression results.
5. **Missing input.** An input attribute with no value is not an error. The expression is evaluated with the input absent; if it then produces no match value, the next rule is tried.
6. **Failure.** An expression that throws, or a result that cannot be converted to the Metaverse attribute's type, is an error on the object, recorded on its Activity item under a new error type.
   - On import the object is neither joined nor projected.
   - On export it is not provisioned.
   - It is retried on the next synchronisation.
   - **This applies to every matching failure, not only expression ones.** Today `ObjectMatchingServer` logs a warning and tries the next rule, and `ExportEvaluationServer.AttemptExportMatchingAsync` swallows every exception and provisions.
7. **Import.** The expression is evaluated in .NET for each Connected System Object, then the existing indexed equality lookup runs unchanged.
   - Zero, one and several matches behave as for attribute rules: next rule, join, or an AmbiguousMatch error.
8. **Export.** Before provisioning, an expression rule is evaluated against a per-run match value map.
   - **Built lazily:** the first time a provisioning decision in the run needs the rule.
   - **Built once:** the expression is evaluated once for every unjoined, Normal-status Connected System Object of the type.
   - **Keyed:** by computed value, case-folded when the rule ignores case.
   - **Kept current:** claiming a Connected System Object removes it from the map.
   - **Looked up by:** the Metaverse Object's value for the rule's Metaverse attribute, read as attribute rules read it today.
9. **Case sensitivity.** The rule's case setting applies to the comparison between computed value and Metaverse value. New rules take their default from the policy settled in #2051.
10. **Portal.** One shared editor serves both the Connected System's Object Matching tab (Simple mode) and the Synchronisation Rule's Matching tab (Advanced mode). It offers:
    - a **Match on: Attribute | Expression** choice (a `SegmentedToggle`);
    - an `ExpressionEditor`, with **Insert Connected System attribute**;
    - a **Try it** tester showing the computed match value and its type;
    - the operator, shown as the catalogue's label "equals";
    - the Metaverse attribute (and, in Simple mode, the Metaverse Object Type);
    - the case setting.

    The Simple mode grid's first column becomes **Match Value**, rendering an `AttributeChip` of the source (`CS` or `Ex`). Advanced mode rule cards render the same chips.
11. **Previews.** The Object Matching configuration preview and Sync Preview evaluate expression rules (the Blocking refusal is removed) and show each object's computed match value beside its outcome.
12. **Explanations.** Where JIM explains a join (Metaverse Object connection explanations, the Activity item for the join), it names the rule, the expression, the inputs read and the computed value.
13. **REST API.** `Expression` is added to the source in create and update requests (Simple and Advanced mode), in the preview request and in responses.
    - Each source carries exactly one of `ConnectedSystemAttributeId` and `Expression`.
    - Validation failures return 400 with the same messages as the portal.
14. **PowerShell.** `New-JIMMatchingRule`, `Set-JIMMatchingRule`, `New-JIMSyncRuleMatchingRule` and `Set-JIMSyncRuleMatchingRule` gain `-SourceExpression`, in a parameter set exclusive with `-SourceAttributeId`.
    - `New-JIMConfigurationChangePreview -MatchingRule` accepts an `Expression` key.
    - `Get-` output includes the expression.
15. **Mode switch, history, security.**
    - Switching between Simple and Advanced mode copies the expression.
    - Configuration change history records it.
    - `engineering/EXPRESSION_SECURITY.md` lists the input as reachable.

#### Phase 2: One operator catalogue and function library (#2053)

1. **Operator catalogue.** `SearchComparisonOperators` becomes the single source of operators, labels, the operators valid for each data type, negation, and the contexts in which each operator may be offered. Every criteria editor, REST validator and PowerShell `ValidateSet` reads it.
2. **New operators.**
   - **has a value** and **has no value**, for every data type.
   - **matches pattern** and **does not match pattern**, for Text. These:
     - use .NET regular expressions with a time limit;
     - are validated on save;
     - are offered only where criteria run in .NET (scoping, Example Data).
3. **One comparison implementation.** A single comparer implements every operator. The scoping evaluator, Example Data and the expression comparison functions use it. The Predefined Search SQL builder is held to it by parity tests across every operator, data type, case setting and multi-valued case, extending the existing text-operator agreement test.
4. **Retire `ComparisonType`.**
   - Example Data dependencies use the catalogue, and stored values are migrated.
   - The REST API and PowerShell accept the catalogue's operator names.
   - Every operator works.
5. **Wildcard escaping.** `%`, `_` and `\` are escaped in Predefined Search criteria and in every free-text `ILIKE` search, so a value means what it says.
6. **Function library.** Each built-in function's metadata becomes data:
   - name and category;
   - signature, parameter descriptions and return type;
   - description and example;
   - whether it returns the same value every time.

   That one catalogue drives:
   - the documentation's function reference (generated, or tested for agreement);
   - the expression editor's suggestions;
   - save-time rejection of unknown functions;
   - `NonRepeatableFunctionDetector`.
7. **Functions mirror operators.**
   - `StartsWith`, `EndsWith` and `Contains` compare ordinally (a behaviour change for `StartsWith` and `EndsWith`, which compare by culture today) and take an optional `ignoreCase` argument.
   - New functions: `HasValue`, `IsMatch`, `RegexReplace` and `RegexExtract`, the regex ones with time limits.
8. **Multi-valued attributes in expressions.** Every expression context reads a multi-valued attribute the same way (see [Open Question 2](#open-questions)). Today:
   - synchronisation keeps the first Connected System value or the last Metaverse value;
   - Example Data supplies them all;
   - the documentation shows examples, such as `Join(mv["Group Memberships"], "|")`, that only work in Example Data.
9. **Expression editor.**
   - Suggestions as you type, for functions and attributes.
   - A browsable function list, with signature help.
   - The portal validates expressions when saving an Attribute Flow, as the REST API already does.
10. **Reference operators.** The catalogue's shape accommodates "is a member of" and "is not a member of" (#1463) without another redesign.

#### Phase 3: Expression value sources in scoping criteria (#2054)

1. **Value source.** A scoping criterion's left-hand side is an attribute or an expression. It reads `cs["..."]` on import rules and `mv["..."]` on export rules, the same choice the attribute picker offers.
2. **Compare as.** The administrator chooses the type the computed value is compared as (Text by default). That choice decides which catalogue operators are offered and which value editor is shown.
3. **Validation at save.** Refused:
   - an expression that does not parse;
   - one that reads no attribute;
   - one that uses a function returning a different value each time (relative-date criteria already cover time-based scope);
   - one whose "compare as" type does not suit the operator.
4. **Explanations.** Scoping explanations, previews and connection explanations show the expression, its inputs and the computed value. They never say only that "the expression returned false".
5. **Failure.** A criterion that cannot be evaluated is an error on the object, and the object's scope state is left as it was.
6. **Surfaces.** The portal's Add Criteria dialog gains **Compare: Attribute | Expression**, with the same editor and tester as Phase 1. The REST API, PowerShell and the scoping preview reach parity.

#### Case sensitivity: one policy

Delivered by #2051, with its Object Matching default work landing first as the bottom layer beneath Phase 1.

**Proposed policy (awaiting the product owner's confirmation).** JIM compares text to answer one of four questions, and each question has one rule:

| Question | Where it is asked | Rule | Configurable |
|---|---|---|---|
| Has this value changed? | Change detection, export confirmation, drift detection, no-net-change | Exact | No |
| Are these the same thing? | External IDs, references, Object Matching Rules, generated-value uniqueness, configuration names | Ignore case, unless the attribute is declared case-exact | Per Object Matching Rule (defaulting from the attribute); per attribute declaration |
| Does this value meet a condition? | Scoping criteria, Predefined Search criteria, Example Data dependencies, expression comparisons | Exact | Per criterion, or the function's `ignoreCase` argument |
| Finding something in the portal | Search boxes, list filters, log search | Ignore case | No |

**Why "the same thing" ignores case:**

- **The systems that hold identifiers ignore case.** The current policy page already says so to justify case-insensitive uniqueness: directory account names, UPNs, email addresses, LDAP DNs (RFC 4514), SCIM `userName` and SQL Server's default collation all do.
- **Exact matching breaks consistency.** Joining exactly while checking uniqueness without regard to case means JIM refuses to issue `jbloggs` because `JBloggs` exists, yet by default will not join HR's `JBloggs` to the directory's `jbloggs`, and projects a duplicate instead.
- **The code already behaves this way.** External ID and reference matching ignore case today. The policy is what's out of step.

**Case-exact attributes.** These keep "ignore case" safe for systems whose identifiers really do differ only in case:

- The declaration is discovered where the Connected System says so: SCIM `caseExact: true`, an LDAP equality matching rule of `caseExactMatch`, or a case-sensitive SQL column collation.
- The administrator can override it.
- Generated-value uniqueness ignores case regardless, because a generated value has to satisfy the strictest of the systems it is exported to.

**References.** A reference's value is the object it points to. A change of case in a DN that resolves to the same object is not a change.

**Deviations the policy must close** (full list with code locations in #2051):

- External ID duplicate detection and lookup (they ignore case, but do not honour case-exact attributes);
- export confirmation of references (exact, while resolution ignores case);
- LDAP case-only DN and RDN renames (never sent);
- CSV header matching (exact, clearing values when a header's case changes);
- SQL Connector anchor keys (ordinal, against a possibly case-insensitive collation);
- configuration-name lookups in drift detection, schema refresh, attribute-name API filters, Activity filters, Predefined Search URIs and Example Data;
- `Set-JIMScopingCriterion` and `Set-JIMPredefinedSearchCriterion` silently resetting the case setting;
- the in-memory test repository diverging from PostgreSQL;
- the Object Matching Rule default differing across model, REST API, portal, PowerShell, preview and docs.

**Enforcement:** the .NET analysers CA1307, CA1309 and CA1310 become errors in `src/`.

### Non-Functional Requirements

- **Performance:**
  - **Import:** an expression rule adds one compiled-expression evaluation per Connected System Object, and no database round trips.
  - **Export:** the map costs one evaluation per unjoined Connected System Object of the type, once per run per rule, built only when a provisioning decision needs it. Its memory is proportional to the unjoined objects.
  - **Baselines:** Scenario 014 baselines must not regress. An expression rule on a 100,000-object Connected System must not make a full synchronisation materially slower than the equivalent attribute rule.
- **Synchronisation integrity:**
  - No matching or scoping failure creates, joins, projects, provisions or changes the scope of an object.
  - Every failure is reported on the object's Activity item.
  - Batch summaries count matching errors.
- **Security:**
  - Expressions keep the sandbox in `engineering/EXPRESSION_SECURITY.md` and the existing length limit.
  - Regular expressions always run with a time limit.
  - No new expression input is reachable without being added to the security inventory.
- **Consistency:** every behaviour is equivalent across the portal, the REST API and PowerShell, with tests on each.
- **Accessibility:** new controls follow the portal conventions: labelled inputs, keyboard-reachable segmented toggles, and error text tied to its field.

## Examples and Scenarios

### Scenario 1: A prefixed employee number joins its identity

**Given:**
- an import rule's Object Matching Rule matches `Trim(Replace(cs["employeeNumber"], "EMP-", ""))` against Employee ID;
- a Metaverse Object, Priya Shah, has Employee ID `48213`.

**When:** a full synchronisation processes the HR object whose `employeeNumber` is `EMP-48213 `.

**Then:**
- the expression computes `48213`;
- the indexed lookup finds exactly one Metaverse Object, and the objects join;
- the connection explanation reads: joined by rule 1, `cs["employeeNumber"]` = `EMP-48213 `, computed `48213`, equals Employee ID `48213` on Priya Shah.

### Scenario 2: Export finds an existing account before provisioning

**Given:**
- a directory's Connected System has an unjoined account whose `employeeID` is `E48213`;
- the export rule matches `Replace(cs["employeeID"], "E", "")` against Employee ID.

**When:** export evaluation would provision an account for Priya Shah (Employee ID `48213`).

**Then:**
- JIM builds the run's match value map for the rule (one evaluation per unjoined account);
- it finds `48213` on that account, claims the account and joins it;
- no second account is created.

### Scenario 3: A missing input falls through to the next rule

**Given:** rule 1 matches on an expression over `employeeNumber`, and rule 2 matches `mail` against Email.

**When:** an object with no `employeeNumber` is synchronised.

**Then:**
- rule 1 produces no match value, and no error is recorded;
- rule 2 joins the object on `mail`.

### Scenario 4: A failing expression stops the object

**Given:** a rule's expression is `FormatDate((DateTime)cs["hireDate"], "yyyyMMdd") + cs["sn"]`.

**When:** an object whose `hireDate` is `TBC` is synchronised.

**Then:**
- the object's Activity item records a matching error naming the rule, the expression, the input and "Specified cast is not valid";
- the object is neither joined nor projected;
- it is tried again on the next synchronisation.

### Scenario 5: Unworkable expressions are refused when saving

**Given:** an administrator adding a matching rule.

**When:** they enter `cs["givenName"] + "." + mv["Surname"]`, or `cs["sAMAccountName"] + Now()`, or `"48213"`.

**Then:** the portal, the REST API and PowerShell each refuse it, with a message saying respectively:
- that a matching expression reads only the Connected System Object;
- that `Now()` returns a different value each time;
- that the expression reads no attribute.

### Scenario 6: A composite key without multi-source rules

**Given:**
- a derived Metaverse attribute, Match Key, built from `Lower(mv["First Name"]) + "|" + Lower(mv["Last Name"]) + "|" + FormatDate((DateTime)mv["Date of Birth"], "yyyyMMdd")`;
- an import rule matching `Lower(cs["givenName"]) + "|" + Lower(cs["sn"]) + "|" + cs["dob"]` against Match Key.

**When:** a contractor feed with no shared identifier is synchronised.

**Then:** contractors join on the combination of name and date of birth, through an indexed equality lookup.

### Scenario 7: Preview shows why a rule misses

**Given:** a proposed expression rule.

**When:** the administrator chooses **Preview Impact**.

**Then:**
- the preview lists, per object, the input, the computed match value and the outcome (would join, no match, no match value, ambiguous);
- an object computing `0042` against a Metaverse value of `42` shows both values, so the leading-zero problem is visible before saving.

### Scenario 8: Has no value, and a pattern, in scoping (Phase 2)

**Given:** an import rule scoped to "employeeType has a value" and "mail matches pattern `^[a-z.]+@example\.org$`".

**When:** objects are synchronised.

**Then:**
- objects with no `employeeType`, or with a mail address that doesn't fit the pattern, are out of scope;
- the scoping explanation names the failing criterion in plain English.

### Scenario 9: A Predefined Search value containing an underscore (Phase 2)

**Given:** a Predefined Search criterion "Account Name starts with `svc_`".

**When:** the search runs.

**Then:** it returns accounts starting with the literal `svc_`, not `svcX`, and agrees with a scoping criterion carrying the same operator and value.

### Scenario 10: A scoping criterion on a reshaped value (Phase 3)

**Given:** an import rule's criterion compares `Lower(Trim(cs["department"]))`, as Text, with "starts with" `fin`.

**When:** HR objects with departments `Finance`, ` FINANCE ` and `Facilities` are synchronised.

**Then:**
- the first two are in scope and the third is not;
- each explanation shows the computed value.

### Scenario 11: Identifiers differing only in case (case-sensitivity policy, if confirmed)

**Given:** HR holds `JBloggs`, the directory holds `jbloggs`, and a matching rule on account name is created with its default case setting.

**When:** the directory is synchronised.

**Then:** the account joins Joe Bloggs's identity rather than projecting a duplicate, because the attribute is not declared case-exact. A SCIM attribute the provider declares `caseExact` would instead default the rule to exact matching.

## Constraints

- **Air-gapped and self-contained:** no new external services. Any new package goes through the dependency governance in `CLAUDE.md`; none is expected.
- **Migrations are append-only.** Retiring `ComparisonType` and adding case-exact declarations use new migrations that convert existing data.
- **Existing configuration keeps its behaviour.** Stored Object Matching Rules, scoping criteria and Predefined Searches keep their stored case settings. Changed defaults affect only what is created afterwards.
- **Surface parity:** every capability ships in the portal, the REST API and PowerShell in the same PR, per `CLAUDE.md`.
- **Writing rules:** British English, no em dashes, Title Case for JIM's domain nouns.

## Affected Areas

| Area | Impact |
|------|--------|
| Models (`JIM.Models/Logic`, `Search`, `Expressions`, `ExampleData`) | Source validation; match value results; operator catalogue additions; `ComparisonType` retired; function metadata; case-exact attribute declaration; new Activity error type |
| Application (`ObjectMatchingServer`, `ExportEvaluationServer`, `SyncEngine.ExportStaging`, `ScopingEvaluator`, `ExampleDataServer`, `DynamicExpressoEvaluator`, Preview adapters, `SyncPreviewServer`) | Expression evaluation in matching; per-run match value map; failures reported instead of swallowed; shared comparer; function semantics |
| Worker (`SyncTaskProcessorBase`, `SyncImportTaskProcessor`) | Join attempts surface matching errors; External ID and reference casing per policy |
| Data (`MetaverseRepository`, `ConnectedSystemRepository`, `SyncRepository`, in-memory `SyncRepository`) | Matching lookups take computed values; wildcard escaping; casing parity between PostgreSQL and the in-memory repository |
| Database | Migrations for retiring `ComparisonType`, case-exact declarations and the Object Matching default |
| Connectors (LDAP, SCIM, SQL, File) | Case-exact discovery; LDAP case-only rename; CSV headers; SQL anchor collation |
| API (`SynchronisationController`, `ExampleDataController`, `PredefinedSearchesController`, DTOs) | `Expression` on matching sources; catalogue-driven validation; consistent defaults |
| UI (`ConnectedSystemObjectMatchingTab`, `SyncRuleMatchingTab`, `SyncRuleDetailScopingCriteriaGroup`, `PredefinedSearchDetail`, `ExpressionEditor`, `ExpressionTester`) | Shared matching rule editor; Match Value column; expression criteria; suggestions and function list |
| PowerShell (`MatchingRules`, `ScopingCriteria`, `Search`, `ExampleData`, `Previews`, `Expressions`) | `-SourceExpression`; catalogue `ValidateSet`s; case settings preserved on `Set-` |

## Documentation Impact

| Doc | Change |
|------|--------|
| `docs/configuration/synchronisation-rules.md` | Object Matching Rules: expression sources, equals-only rationale, derived Metaverse attributes for the Metaverse side. Remove the unsupported `firstName` + `lastName` multi-source suggestion. Fix the "Criteria expressions use the JIM expression language" claim (scoping uses criteria until Phase 3). New operators. |
| `docs/concepts/expressions.md` | Matching and scoping contexts; generated function reference; `StartsWith`/`EndsWith` semantics; multi-valued attributes; remove the claims that scoping is expression-based; fix `FormatDate` without a cast |
| `docs/concepts/case-sensitivity.md` | Rewritten as the four-question policy |
| `docs/configuration/predefined-searches.md` | New operators; wildcard behaviour |
| `docs/powershell/synchronisation-rules.md` | `-SourceExpression`; correct `-CaseSensitive` defaults |
| `docs/powershell/previews.md`, `docs/powershell/expressions.md`, `docs/powershell/example-data.md` | Expression in matching previews; remove the non-existent `Mid()`; Example Data operators from the catalogue |
| `docs/reference/glossary.md`, `docs/reference/roadmap.md` | Object Matching Rule entry; the roadmap's expression engine line |
| `engineering/EXPRESSION_SECURITY.md` | Matching and scoping expression inputs now reachable; Example Data expressions authorable (already true, inventory stale); package version |
| `engineering/SYNC_RULE_SCOPING.md`, `engineering/CONFIGURATION_CHANGE_CLASSIFICATION.md` | Expression criteria; classification of matching expressions |

## Dependencies

- #1750 Metaverse derived attribute flows (done): the way to normalise the Metaverse side of a match.
- #1458 Save-time validation of Object Matching Rules (done): the pattern this extends to expressions.
- #2051 must settle the case-sensitivity policy before Phase 1 sets defaults for new rules.
- #2053 before #2054 (native blocked-by).
- Related: #1463 (reference operators in scoping), #1128 (curated transform recipes, which build on the function library).

## Open Questions

1. **Ambiguous matches on export.** Today, when an export match finds more than one Connected System Object, JIM logs a warning and claims the lowest id. Import treats the same situation as an error.
   - **Recommendation:** make an ambiguous export match an error on the object, for attribute and expression rules alike, so that JIM never chooses between accounts on an administrator's behalf.
2. **Multi-valued attributes in expressions.** Synchronisation keeps one value per attribute, while Example Data passes all of them.
   - **Option A:** every context passes all values. This is correct, but changes existing Attribute Flow expressions that read a multi-valued attribute.
   - **Option B:** keep `cs["x"]` as one value everywhere and add an explicit `Values(cs["x"])` that returns them all. This changes no existing behaviour.
   - **Recommendation:** Option B, settled in Phase 2.
   - Until then, a matching rule on a multi-valued Connected System attribute should use an attribute source, which already tries every value.
3. **Phase 3 "compare as".** Should the type be chosen explicitly (proposed), or inferred from a test evaluation? Inference fails for expressions that return null for the sample.
4. **Case-sensitivity policy.** Confirm the four-question policy (Decision 5) before #2051 starts.

## Acceptance Criteria

### Phase 1 (#2052)

- [ ] Expression rules join on import and are matched on export, in Simple and Advanced mode.
- [ ] Unworkable rules (including multiple sources) are refused at save on every surface, with the messages in Scenario 5.
- [ ] Matching failures never join, project or provision. They are reported on the object, for attribute and expression rules alike.
- [ ] Try it, Preview Impact, Sync Preview and the join explanations show computed match values.
- [ ] The REST API and PowerShell reach parity, with tests. Docs are updated.
- [ ] No regression in the Scenario 014 baselines.

### Phase 2 (#2053)

- [ ] One catalogue drives every operator list, validator and `ValidateSet`. `ComparisonType` is gone and its stored data is migrated.
- [ ] Parity tests prove every operator agrees between .NET and SQL. Wildcards are escaped.
- [ ] The function library drives docs, suggestions and validation.
- [ ] `StartsWith`/`EndsWith` are ordinal with `ignoreCase`. New operators and functions are documented.

### Phase 3 (#2054)

- [ ] Scoping criteria accept expression sources across every surface, with explanations showing computed values.
- [ ] Evaluation failures never change an object's scope.

### Case sensitivity (#2051)

- [ ] One policy page, and every audited site follows it.
- [ ] The Object Matching default is the same on every surface.
- [ ] CA1307, CA1309 and CA1310 are enforced.

## Additional Context

- **Research, 2026-10-11:** end-to-end maps of Object Matching, the expression engine and every operator-using feature. Findings are summarised in the tables above, with code locations in the sub-issues.
- **UI mocks:** the reconciliation overview, the Simple and Advanced mode editors, guard rails and errors, Preview Impact and join explanations, the rejected fuzzy-operator design (for the record), Phase 2's function library, and Phase 3's expression criteria.
- **Prior decisions this builds on:**
  - `engineering/plans/done/CASE_SENSITIVITY_STRATEGY.md` (superseded in part by the four-question policy, once confirmed);
  - `engineering/plans/done/SIMPLE_MODE_OBJECT_MATCHING_IMPORT_AND_EXPORT.md`;
  - `engineering/prd/done/PRD_METAVERSE_DERIVED_ATTRIBUTE_FLOWS.md`;
  - `engineering/EXPRESSION_SECURITY.md`.
