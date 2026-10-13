# One Operator Catalogue and Function Library

- **Status:** Done
- **Note:** PR B went ahead without waiting for #2051 PR 2 (#2224): it makes `StartsWith`, `EndsWith` and `Contains` ordinal itself, matching #2224, and whichever lands second reconciles the evaluator.
- **Issue:** [#2053](https://github.com/TetronIO/JIM/issues/2053) (Phase 2 of [#207](https://github.com/TetronIO/JIM/issues/207))
- **PRD:** [PRD_COMPARISON_AND_EXPRESSION_VOCABULARY.md](../../prd/doing/PRD_COMPARISON_AND_EXPRESSION_VOCABULARY.md) (section "Phase 2: One operator catalogue and function library", Scenarios 8, 9 and 13)

## Overview

JIM compares values in five places, with three vocabularies and several implementations that disagree. This plan gives every comparison one meaning:

- one **operator catalogue** for criteria (scoping, Predefined Searches, Example Data), with the operators administrators are missing;
- one **comparison implementation** that every .NET context calls, with the SQL builder held to it by parity tests;
- one **function library**, described as data, that drives the docs, the expression editor and save-time validation;
- one **multi-valued model** for expressions, so no expression reads an arbitrary value of several.

It lands as three PRs, A to C. PR A needs nothing in flight; B and C each wait on work in other sessions.

## Business Value

- **The same criterion means the same thing everywhere.** "Starts with `svc_`" returns the same objects as a Predefined Search and as a scoping criterion, and an underscore is an underscore.
- **Administrators can say what they mean.** "has a value", "has no value" and "matches pattern" replace the "Equals with no value" workaround and the long chains of Contains criteria.
- **Example Data dependencies stop throwing.** Six of the seven advertised operators fail at generation time today.
- **Free-text searches stop treating `%` and `_` as wildcards**, in the portal's lists and the REST API alike.
- **Expressions read multi-valued attributes predictably** (PR C), instead of whichever value happened to be stored first or last.

## Technical Architecture

### Current state (2026-10-13)

| Context | Vocabulary | Runs in | Implementation |
|---|---|---|---|
| Scoping | `SearchComparisonType` | .NET | `ScopingEvaluator.CompareStrings/CompareOrdered/CompareEquality`, plus a `MatchesMissingValue` special case for "Equals with no value" |
| Predefined Searches | `SearchComparisonType` | PostgreSQL | `MetaverseRepository.BuildPredefinedSearchCriterionSql`; `LIKE`/`ILIKE` with the value unescaped |
| Example Data dependencies | `ComparisonType` (different member names and ordinals) | .NET | `ExampleDataServer.ExecuteTemplateAsync`: one inline `!=`, everything else throws `NotSupportedException` |
| Expressions | Functions and C# operators | .NET | `DynamicExpressoEvaluator`; culture-sensitive `StartsWith`/`EndsWith` until #2051 PR 2 |
| Free-text admin searches | none | PostgreSQL | About twenty `EF.Functions.ILike(x, $"%{text}%")` sites; two private escape helpers already exist for three of them |

`SearchComparisonOperators` (JIM.Utilities) already holds the operators valid for each data type and their labels. It knows nothing of contexts, value-less operators or patterns, and there is no `ValidateSet` agreement test.

### Proposed solution

**The catalogue (PR A).** `SearchComparisonOperators` becomes the only place an operator is described:

| Question | Member |
|---|---|
| Every operator, in display order | `All` |
| Which operators apply to a data type, in a context | `ValidOperatorsFor(type, context)`, `IsValid(op, type, context)` |
| What an operator is called | `LabelFor(op, type)` |
| Which operator negates which | `Negation(op)`, `IsNegated(op)` |
| Whether it compares against a value | `TakesValue(op)` |
| Whether it is a pattern | `IsPattern(op)`, `ValidatePattern(text)` |

`CriterionContext` names the three contexts (`Scoping`, `PredefinedSearch`, `ExampleData`). Pattern operators are offered only where criteria run in .NET; a context, not a data type, is what rules them out of Predefined Searches. "is a member of" (#1463) slots in as two more rows with their own data type (Reference) and contexts, without another redesign.

New operators, appended to `SearchComparisonType` so stored ordinals never move:

| Operator | Types | Contexts | Meaning over values |
|---|---|---|---|
| `HasValue` | every type | all | at least one value |
| `HasNoValue` | every type | all | no values |
| `MatchesPattern` | Text | Scoping, Example Data | any value matches the .NET regular expression |
| `NotMatchesPattern` | Text | Scoping, Example Data | at least one value, and none matches |

A "value" is what scoping already counts: a held attribute value, never an asserted-null marker (#91). Patterns use `RegexOptions.CultureInvariant`, `IgnoreCase` unless the criterion is case-sensitive, and a 100 ms match timeout. Compiled expressions are cached by pattern and case setting. A pattern is validated when saved, on every surface; a stored pattern that does not compile, or a match that times out, fails the object exactly as an invalid operator does, rather than silently scoping it out.

**One comparison (PR A).** `CriterionComparer` (JIM.Application) is the only implementation of a criterion against one value, and of the any/none rule across several. The scoping evaluator and Example Data call it; expression comparison functions join in PR B. The SQL builder stays SQL (it has to, to use the indexes) and is held to the comparer by a `RequiresPostgres` parity suite: every operator, every data type, both case settings, single and multi-valued, with values chosen to contain `%`, `_` and `\`.

**Retiring `ComparisonType` (PR A).** Example Data dependencies store a `SearchComparisonType`. A migration rewrites the stored ordinals (`Equals` 0 to 1, `NotEquals` 1 to 2, `LessThan` 2 to 9, `GreaterThan` 3 to 11, `GreaterThanOrEqual` 4 to 12, `LessThanOrEqual` 5 to 10, `Like` 6 to `Contains` 7). The dependency's text value is read as the dependent attribute's data type (invariant culture, ISO 8601 dates), validated on save.

**Retiring "Equals with no value" (PR A).** The same migration rewrites scoping criteria that relied on the workaround, where the rewrite is exactly equivalent: `Equals` with no value becomes `HasNoValue` for every type, and `NotEquals` with no value becomes `HasValue` for Text, Boolean and Guid. `NotEquals` with no value on a number or date has never matched anything and is left as it is. New value-less criteria for value-taking operators are refused on every surface.

**Wildcard escaping (PR A).** One `LikePattern` helper (JIM.PostgresData) escapes `\`, `%` and `_` and builds the contains, starts-with and ends-with patterns. Every `ILIKE`/`LIKE` site uses it, including the Predefined Search builder, and the two private helpers it replaces are removed.

**Function library (PR B).** Each built-in function becomes a `FunctionDefinition` record: name, category, parameters with descriptions, return type, description, example and whether it returns the same value every time. One catalogue drives the docs function reference (tested for agreement), the expression editor's suggestions, function list and signature help, save-time rejection of unknown functions (in the portal as well as the REST API), and `NonRepeatableFunctionDetector`. `StartsWith`, `EndsWith` and `Contains` gain `ignoreCase`; `HasValue`, `IsMatch`, `RegexReplace` and `RegexExtract` are added, the regex ones through the same cached, time-limited patterns as the operators.

**Multi-valued attributes (PR C).** Decision 8 as amended on 2026-10-12: `cs["x"]` and `mv["x"]` read every value in a fixed order; transforms apply per value; comparisons are true when any value satisfies them; `Count`, `Min`, `Max`, `Join` and `Where` work on the set; one value required and several held is an error on the object; several results written to a multi-valued attribute write every value (fixing `System.String[]`). It runs in #2055's shared evaluation pipeline, so the testers show it from the start. There is no upgrade report: the product owner dropped it on 2026-10-12 because JIM is pre-release, and Sync Preview and the Configuration Change Preview show the difference before it runs.

### Data flow (a criterion after PR A)

```
Editor / REST / PowerShell --(operator name)--> SearchComparisonOperators.IsValid(op, type, context)
                                               SearchComparisonOperators.ValidatePattern(...)
Scoping evaluator ----+
Example Data ---------+--> CriterionComparer --> one answer per value, then any/none
Expressions (PR B) ---+
Predefined Search SQL ----> parity tests against CriterionComparer
```

## Implementation Phases

### PR A: The operator catalogue and one comparison ✅

Needs neither in-flight dependency.

1. Catalogue: `CriterionContext`; the four new operators; `All`, `ValidOperatorsFor(type, context)`, `IsValid(op, type, context)`, `Negation`, `TakesValue`, `IsPattern`, `ValidatePattern`; labels.
2. `CriterionComparer` and `CriterionPatterns` (cached, time-limited regular expressions); the scoping evaluator moves onto them and loses `MatchesMissingValue`; scoping explanations word the new operators.
3. Predefined Search SQL: `HasValue`/`HasNoValue` for every type, escaped `LIKE`/`ILIKE` values, pattern operators refused. Parity suite across every operator and type.
4. Example Data: dependencies on `SearchComparisonType`, evaluated by `CriterionComparer`; migration of stored ordinals; text values read as the attribute's type.
5. Migration of the "Equals with no value" scoping criteria.
6. Write-path validation: context, value presence and pattern validity, in `ConnectedSystemServer`, both REST controllers and the Example Data controller.
7. Portal: scoping and Predefined Search editors list operators by context, hide the value for "has a value"/"has no value", and validate patterns as typed; the Data Generation Template page shows each attribute's dependency.
8. PowerShell: `ValidateSet`s and the Example Data dependency names, held to the catalogue by a test that reads the cmdlet source.
9. `LikePattern` across every free-text `ILIKE` search.
10. Docs (scoping criteria, Predefined Searches, Example Data, PowerShell, case sensitivity) and changelog (✨ operators, 🐛 escaping, 🔄 Example Data operator names).

### PR B: The function library ✅

**Waits on #2051 PR 2**, which makes expression `StartsWith`/`EndsWith` ordinal under CA1310 and is in flight in another session. Adding `ignoreCase` to functions whose base comparison is about to change underneath them would be done twice.

1. `FunctionDefinition` catalogue for every built-in function; `NonRepeatableFunctionDetector` reads it.
2. Docs function reference generated from, or tested against, the catalogue.
3. `ignoreCase` on `StartsWith`, `EndsWith` and `Contains`; new `HasValue`, `IsMatch`, `RegexReplace`, `RegexExtract`; comparison functions call `CriterionComparer`.
4. Save-time rejection of unknown functions in the portal (Attribute Flow save) as well as the REST API.
5. Expression editor: suggestions as you type for functions and attributes, a function list, signature help.

### PR C: Multi-valued attributes in expressions ✅

**Waits on #2055's shared evaluation pipeline**, also in flight elsewhere, and expects #2179's `ExpressionResultConverter` and `DynamicExpressoEvaluator` changes to have landed.

1. Accessors return every value in a fixed order; transforms per value; comparisons any-value; `Count`, `Min`, `Max`, `Join`, `Where` over the set.
2. One-value-required positions raise an error on the object naming the attribute and the count.
3. Several results written to a multi-valued attribute, on import and export.
4. One definition of no value; `CollectionContains`, `HasAttribute` and `AttributeNames` removed.
5. Late binding, subject to the security review against `EXPRESSION_SECURITY.md`; otherwise the save-time refusal fallback.
6. Scenario 13 as an end-to-end test; changelog 🔄 multi-valued semantics and 🐛 `System.String[]`.

As delivered: several values are one type, `ExpressionValues` (JIM.Models), which every accessor returns for two or more values and whose operators and `ToString` are the one-value positions; every expression dictionary builds through `ExpressionAttributeDictionary`. Late binding passed its review (recorded in `EXPRESSION_SECURITY.md`, "Late binding") and is on. `System.String[]` had already been fixed by #2179, so its changelog entry was not repeated; `==` comparing text by value is the fix this PR carries instead. Several values into a single-valued attribute name the attribute they came from. `Where` landed here rather than in PR B because Scenario 13 needs it.

## Success Criteria

- One catalogue drives the operator lists in the scoping, Predefined Search and Example Data editors, the REST validation and PowerShell's `ValidateSet`s, enforced by tests.
- Every operator agrees between .NET and SQL, enforced by `RequiresPostgres` tests.
- `ComparisonType` is gone and stored Example Data dependencies are migrated.
- A `%`, `_` or `\` in any search value matches itself.
- The docs function reference agrees with the function catalogue (PR B).
- No expression reads a multi-valued attribute arbitrarily (PR C).
- No measurable regression in the Scenario 014 performance baselines.

## Benefits

- **UX:** one set of words for one set of meanings; the operators administrators were working around.
- **Architecture:** one place to add an operator, one place it is evaluated, and tests that fail when the SQL drifts.
- **Performance:** pattern expressions compile once per pattern rather than once per value; presence operators are plain `EXISTS` tests on the existing index.

## Dependencies

- #2051 PR 2 (ordinal expression comparisons, `bool?` `CaseSensitive` on criterion request DTOs): PR B waits on it. PR A touches the same DTOs only in their operator field, and merges `origin/main` once it lands.
- #2055 (shared evaluation pipeline): PR C waits on it.
- No new third-party packages.

## Risks and Mitigations

| Risk | Mitigation |
|---|---|
| A pathological pattern stalls synchronisation | 100 ms match timeout; a timeout fails the object loudly; patterns compile once and are validated on save |
| The migration changes what a stored criterion selects | Only exactly-equivalent rewrites are made; the one non-equivalent case (`NotEquals` with no value on a number or date) is left alone |
| Example Data scripts using the old operator names break | Called out in the changelog; the names now match scoping and Predefined Searches, which is the point |
| SQL and .NET drift again when an operator is added | The parity suite iterates the catalogue, so a new operator without a SQL translation fails it |
| Escaping changes free-text search results | Only for searches containing `%`, `_` or `\`, which were wrong before |
