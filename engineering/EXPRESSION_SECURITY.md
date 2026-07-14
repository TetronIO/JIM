# Expression Evaluation Security Review

| | |
|---|---|
| **Created** | 2026-07-11 |
| **Last Updated** | 2026-07-11 |
| **Status** | Complete |
| **Issue** | [#500](https://github.com/TetronIO/JIM/issues/500) (OWASP Top 10:2025 assessment, gap 5: "DynamicExpresso input path review") |

This document is the security review of JIM's expression evaluation system: every path by which an
administrator-authored expression string reaches the DynamicExpresso evaluator, what the evaluator exposes and
blocks, and the defence-in-depth guardrails added as a result. It closes gap 5 of the [OWASP Top
10:2025 assessment](plans/doing/OWASP_TOP_10_ASSESSMENT.md).

**Cardinal constraint carried through this review:** JIM's expression system is a product strength and a
best-in-class attribute transformation capability. No guardrail may impair legitimate expression functionality,
now or in the future. Guardrails were added only where they are strictly non-subtractive; several tempting
measures were considered and explicitly rejected (see "Rejected measures" below).

## Purpose and threat model

Expressions in JIM are **Administrator-authored configuration**, not end-user input. They are written by an
Administrator into a Synchronisation Rule's Attribute Flow (or, in principle, an Object Matching Rule or Example
Data Template attribute; see the inventory below for which of these are actually reachable today) and evaluated
later, unattended, by the sync engine, export pipeline, or drift detection.

This framing drives the whole review:

- **An administrator who can author an expression can already configure Attribute Flow to any Connected
  System.** Attribute Flow is the mechanism JIM uses to move data between the metaverse and every connected
  directory, HR system, or SaaS application the deployment has configured. An Administrator with expression
  authoring rights already has read/write reach over every object those systems expose to JIM; an expression
  cannot grant a privilege its author does not already hold through the ordinary configuration surface.
- **There is no untrusted-input path.** No end-user-supplied value ever becomes expression *source code*.
  Connected System and Metaverse Object *attribute values* flow through `mv[...]` / `cs[...]` accessors at
  evaluation time, but those are data lookups against a fixed dictionary, not text that gets parsed as part of
  the expression. A malicious value in, say, an imported `displayName` cannot inject new expression syntax; see
  "What the evaluator exposes" below for the proof that this holds even when the value looks like an escape
  attempt.
- **The residual risk is therefore not sandboxing a hostile author.** It is: (a) an *accidental* runaway or
  malformed input (a pasted multi-megabyte blob, a script fragment mistakenly pasted into an expression field)
  causing memory growth or clutter in a shared cache; and (b) defence-in-depth against a **compromised admin
  session** (a stolen JWT or hijacked browser tab), where a shallow guardrail costs nothing for a legitimate
  administrator but adds one more speed bump for an attacker who has otherwise won the session.

Because the threat model is "trusted author, defend against accidents and session compromise" rather than
"sandbox a hostile author," the guardrails below are sanity bounds and observability, not a capability
allowlist. See "Rejected measures" for why an allowlist approach was ruled out.

## Input path inventory

Three JIM model fields carry a DynamicExpresso expression string. Only one has a live authoring surface today;
the other two are model-level and evaluation-ready but not yet wired to any UI, REST, or PowerShell surface
(this was verified by tracing every controller, Razor page, and PowerShell cmdlet touching each model, not
assumed).

| Source field | Authoring surface | Required role | Evaluation site(s) |
|---|---|---|---|
| `SyncRuleMappingSource.Expression`<br />(`src/JIM.Models/Logic/SyncRuleMappingSource.cs`) | **Live.** REST API: `POST`/`PUT` on `api/v{version}/synchronisation/sync-rules/{id}/mappings` (`SynchronisationController`, `CreateSyncRuleMappingSourceRequest.Expression` / `SyncRuleMappingSourceDto.Expression`); Blazor UI: `SyncRuleAttributeFlowTab.razor` under `/admin/sync-rules/{id}`; PowerShell: `New-JIMSyncRuleMapping -Expression <string>` (wraps the same API). Ad-hoc testing (not persisted): `POST api/v{version}/synchronisation/test-expression` (`TestExpressionRequest`), Blazor UI expression tester, PowerShell `Test-JIMExpression`. | `Administrator` (`[Authorize(Roles = "Administrator")]` on `SynchronisationController`; `@attribute [Authorize(Roles = "Administrator")]` on `SyncRuleDetail.razor`, the host page for the Attribute Flow tab) | Import: `SyncEngine.AttributeFlow.cs` (`ProcessExpressionMapping`), invoked from `JIM.Worker/Processors/SyncTaskProcessorBase.cs`. Export: `ExportEvaluationServer.cs` (`CreateAttributeValueChanges` and `GetExpectedValue`, the latter shared with drift detection). Drift: `DriftDetectionService.cs` (`GetExpectedValue` / `IsContributorForExpressionAttributes`). Ad-hoc test: `SynchronisationController.TestExpression`. |
| `ObjectMatchingRuleSource.Expression`<br />(`src/JIM.Models/Logic/ObjectMatchingRuleSource.cs`) | **Not currently reachable.** The model supports it and `IsValid()` accepts it, but `CreateObjectMatchingRuleSourceRequest` (the REST create/update DTO) has no `Expression` property, and the Blazor matching-rule dialog (`SyncRuleMatchingTab.razor`) shows "Sorry, expressions are not yet supported." for this source type. No PowerShell cmdlet sets it either. | N/A today (would be `Administrator`, matching the rest of Object Matching Rule configuration, if wired up) | N/A today (dead code path: nothing populates `ObjectMatchingRuleSource.Expression`, so no evaluator ever sees one) |
| `ExampleDataTemplateAttribute.Expression`<br />(`src/JIM.Models/ExampleData/ExampleDataTemplateAttribute.cs`) | **Not currently reachable.** `ExampleDataController` exposes only Example Data Set CRUD and template *execution* (`POST templates/{id}/execute`); there is no Create/Update endpoint for a Template or its Attributes. `ExampleDataTemplateDetail.razor` only displays an existing Expression (read-only chip); it has no editor. No PowerShell cmdlet sets it. JIM's built-in seeded templates (`SeedingServer`) do not use expression-based attributes today either. | N/A today | `ExampleDataServer.EvaluateAttributeExpression`, called from `ExampleDataServer` during generation, but unreachable in practice because nothing populates the field |

**Implication:** the live, exercised attack surface today is exactly one field
(`SyncRuleMappingSource.Expression`), gated by the `Administrator` role at both the API and UI layer, consistent
with every other Synchronisation Rule configuration action in JIM. The other two fields are forward-looking
model capability; if either gains an authoring surface in future, it inherits this review's guardrails
automatically (they live in the shared `DynamicExpressoEvaluator`), and this table should be updated at that
point.

## What the evaluator exposes

`DynamicExpressoEvaluator` (`src/JIM.Application/Expressions/DynamicExpressoEvaluator.cs`) wraps
[DynamicExpresso](https://github.com/dynamicexpresso/DynamicExpresso) (`DynamicExpresso.Core` NuGet package,
pinned at 2.19.3). It constructs a plain `new Interpreter()` (equivalent to `new
Interpreter(InterpreterOptions.Default)`) and layers JIM's own built-in functions on top via `SetFunction`. The
claims below were verified against the DynamicExpresso 2.19.3 source (`dynamicexpresso/DynamicExpresso` tag
`v2.19.3` on GitHub) and proven with the unit tests named alongside each claim, not asserted from memory.

### DynamicExpresso defaults (`InterpreterOptions.Default`)

`InterpreterOptions.Default = PrimitiveTypes | SystemKeywords | CommonTypes` (`InterpreterOptions.cs`). This
registers, and nothing else:

- **Primitive types** (`LanguageConstants.PrimitiveTypes` / `CSharpPrimitiveTypes`): `object`, `bool`, `char`,
  `string`, `sbyte`, `byte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double`, `decimal`,
  `DateTime`, `TimeSpan`, `Guid`.
- **System keywords** (`LanguageConstants.Literals`): `true`, `false`, `null`.
- **Common types** (`LanguageConstants.CommonTypes`): `System.Math`, `System.Convert`, and
  **`System.Linq.Enumerable`**. The Enumerable exposure is easy to miss reading the package README summary
  (which mentions only Math/Convert); it was found by reading `LanguageConstants.cs` directly. `Enumerable` only
  grants in-memory sequence generation/LINQ operators (`Enumerable.Range`, `.Count()`, etc.); it has no
  filesystem, process, or network surface. Proven reachable, and that a moderately large range still evaluates
  promptly, by `Evaluate_EnumerableRangeCallsAreReachable_ButBounded`
  (`DynamicExpressoEvaluatorSecurityTests.cs`).
- **Assignment operators** are left at the DynamicExpresso default (`AssignmentOperators.All`); JIM does not
  call `EnableAssignment` to restrict them. DynamicExpresso's own README recommends disabling assignment for
  untrusted-author scenarios, but this is a non-issue here: `AttributeAccessor`'s `mv`/`cs` indexer
  (`src/JIM.Models/Expressions/AttributeAccessor.cs`) is **get-only**, so there is no settable member for an
  assignment expression to target. Proven by `Evaluate_AssignToMvIndexer_ThrowsParseException`.
- No `LambdaExpressions`, no `CaseInsensitive` interpreter option, no `LateBindObject`. JIM's own attribute-name
  case-insensitivity is implemented in `AttributeAccessor`, not via a DynamicExpresso interpreter option.

On top of this, `RegisterBuiltInFunctions` (`DynamicExpressoEvaluator.cs`) registers JIM's own functions, all
pure, side-effect-free, in-process transforms: string functions (`Trim`, `Upper`, `Lower`, `Capitalise`, `Left`,
`Right`, `Substring`, `Replace`, `StartsWith`, `EndsWith`, `Length`, `IsNullOrEmpty`, `IsNullOrWhitespace`),
containment (`Contains`, `CollectionContains`), collection (`Split`, `Join`), conditional (`Coalesce`, `IIF`,
`Eq`), date (`Now`, `Today`, `FormatDate`, `ToFileTime`, `FromFileTime`), conversion (`ToString`, `ToInt`), a DN
helper (`EscapeDN`), password generation (`RandomPassword`, `RandomPassphrase`, both backed by
`RandomNumberGenerator`, not `System.Random`), and AD `userAccountControl` bitwise helpers (`EnableUser`,
`DisableUser`, `SetBit`, `ClearBit`, `HasBit`). Every one of these is exercised end-to-end, with expressions
taken verbatim from the customer-facing function reference in `docs/concepts/expressions.md`, by
`DynamicExpressoEvaluatorFunctionalityRegressionTests.cs` (see "Guardrails added" below for why this suite
exists).

The `mv` and `cs` parameters passed into every evaluation (`Evaluate`, `Test`, and the typed parse in
`Validate`) are `AttributeAccessor` instances: a case-insensitive, get-only dictionary wrapper. They expose
exactly one indexer (`this[string attributeName]`) and a `HasAttribute` / `AttributeNames` pair; there is no
route from an `AttributeAccessor` back into reflection, the containing `Dictionary`, or any other JIM type.

### What is blocked

DynamicExpresso installs a `DisableReflectionVisitor` on every `Interpreter` by default (removable only via the
explicit opt-in `Interpreter.EnableReflection()`, which JIM never calls). It rejects, at parse time, any method
call or member access whose *static* receiver type is already `System.Type` or `System.Reflection.MemberInfo`
(except the single permitted member, `Type.Name`), throwing `ReflectionNotAllowedException` (a subclass of
`DynamicExpresso.Exceptions.ParseException`). An identifier for a type or namespace that was never registered
(anything outside the default set above, e.g. `System.Diagnostics.Process`, `System.IO.FileStream`,
`Environment`, `Activator`, `AppDomain`) fails earlier still, as an `UnknownIdentifierException` (also a
`ParseException` subclass) before the reflection visitor is ever reached.

`DynamicExpressoEvaluatorSecurityTests.cs` proves this against the real evaluator, not by inspecting
DynamicExpresso's source alone:

- `Evaluate_ChainedGetTypeGetMethods_ThrowsReflectionNotAllowedException`,
  `Evaluate_ChainedGetTypeAssembly_ThrowsReflectionNotAllowedException`,
  `Evaluate_ChainedGetTypeInvokeMember_ThrowsReflectionNotAllowedException`,
  `Evaluate_TypeofDoubleGetMethods_ThrowsReflectionNotAllowedException`,
  `Evaluate_TypeofDoubleAssembly_ThrowsReflectionNotAllowedException`: chained reflection escape attempts
  (`mv["a"].GetType().GetMethods()`, `.GetType().Assembly`, `.GetType().GetMethod(...)`, and DynamicExpresso's
  own documented blocked example, `typeof(double).GetMethods()` / `.Assembly`) all throw
  `ReflectionNotAllowedException`.
- `Evaluate_UnregisteredSystemNamespaceIdentifier_ThrowsUnknownIdentifierException`,
  `Evaluate_UnregisteredFileType_ThrowsUnknownIdentifierException`,
  `Evaluate_UnregisteredEnvironmentType_ThrowsUnknownIdentifierException`,
  `Evaluate_UnregisteredActivatorType_ThrowsUnknownIdentifierException`,
  `Evaluate_UnregisteredAppDomainType_ThrowsUnknownIdentifierException`: attempts to reach process execution
  (`System.Diagnostics.Process.Start`), the filesystem (`System.IO.FileStream`), environment variables
  (`Environment.GetEnvironmentVariable`), arbitrary object construction (`Activator.CreateInstance`), and the
  hosting `AppDomain` all fail as unknown identifiers; none of these types are registered, so there is nothing
  for the reflection visitor to even evaluate.
- `Evaluate_AssignToMvIndexer_ThrowsParseException`: no settable surface for the left-at-default assignment
  operators (see above).
- `Validate_RejectsEveryEscapeAttempt` (a `[TestCase]`-parameterised sweep over all of the above plus the
  assignment attempt): `Validate()` rejects every one of them too, not just `Evaluate()`, so the same protection
  applies at save-time (when an Administrator saves a mapping) as at run-time.
- `Evaluate_UnbalancedParentheses_ThrowsParseException`,
  `Evaluate_SqlInjectionLookingString_TreatedAsInertLiteral`,
  `Evaluate_PathTraversalLookingString_TreatedAsInertLiteral`,
  `Evaluate_ScriptTagLookingString_TreatedAsInertLiteral`,
  `Evaluate_DeeplyNestedIIF_EvaluatesWithoutError`: a representative set of hostile-looking-but-illegitimate
  inputs (a SQL-injection-shaped string, a path-traversal-shaped string, an HTML/script-shaped string, and a
  deeply nested but syntactically ordinary `IIF` chain) either parse as inert string literals (expressions have
  no SQL, shell, or DOM surface at all, so these strings are just data) or evaluate as ordinary nested function
  calls with no special handling required.

**Open finding (not hardened, by design):** `Evaluate_BareGetType_ParsesAndReturnsTypeInstance` shows that
`mv["a"].GetType()` **does** parse and evaluate successfully, returning a `System.Type` instance, because the
`DisableReflectionVisitor` only rejects a call or member access whose receiver is *already* statically typed as
`Type`/`MemberInfo` (chained reflection); the initial `.GetType()` call receives on `object`, so it is not
itself blocked, only the reflection *chained onto its result* is (proven by the tests above). This matches
DynamicExpresso's own documented behaviour (`Type.Name` is explicitly the one reflection member left available)
and grants no further capability within the same expression: every attempt to chain a method call or another
member access onto the returned `Type` in the tests above was blocked. It is recorded here as an explicit,
non-hardened finding per this review's brief, rather than patched unilaterally: whether returning a bare `Type`
value is worth restricting further is a design call for the validator, not something to fix on this branch's own
initiative.

**Summary: every deliberate escape attempt exercised by this review failed to parse or evaluate**, with the one
narrow, non-exploitable, explicitly-flagged exception above.

## Guardrails added (non-subtractive)

Both guardrails are sanity bounds against accidents, not capability restrictions; neither changes what any
legitimate expression can do (proven by `DynamicExpressoEvaluatorFunctionalityRegressionTests.cs`, 46 tests
covering every built-in function and several composed real-world expressions, all passing with both guardrails
active).

1. **Expression length ceiling (`DynamicExpressoEvaluator.MaxExpressionLength = 10,000` characters).** `Evaluate`
   throws `ArgumentException`; `Validate` and `Test` return a validation/test failure result (they have a result
   type to carry the error, so they do not throw). 10,000 characters is roughly two orders of magnitude beyond
   any real expression in this codebase's own examples or documentation (the longest documented expression is a
   few hundred characters); it exists purely as a bound against an administrator accidentally pasting the wrong
   clipboard contents into an expression field. Tests: `Evaluate_ExpressionExceedsMaxLength_ThrowsArgumentException`,
   `Evaluate_ExpressionAtMaxLength_EvaluatesSuccessfully` (boundary proof: exactly at the limit still works),
   `Validate_ExpressionExceedsMaxLength_ReturnsFailure`, `Validate_ExpressionAtMaxLength_ReturnsSuccess`,
   `Test_ExpressionExceedsMaxLength_ReturnsFailureWithoutEvaluating` (`DynamicExpressoEvaluatorTests.cs`).
2. **Bounded compiled-expression cache (`DynamicExpressoEvaluator.MaxCompiledExpressionCacheSize = 1,000`
   entries).** `_compiledExpressions` is a static `ConcurrentDictionary<string, Lambda>` shared across every
   evaluator instance for the process lifetime; ad-hoc expressions evaluated via `Test` (the admin UI's
   expression tester, and `Test-JIMExpression`) previously accumulated in it forever, since every distinct
   string typed while iterating on an expression became a permanent cache entry. On reaching the 1,000-entry
   bound, the cache is cleared and a Serilog warning is logged; recompilation is cheap (DynamicExpresso parsing
   is sub-millisecond for expressions of this size), so a clear-and-restart is simpler and more deterministic
   than an eviction policy (LRU bookkeeping, a second data structure, extra locking) for a bound whose only job
   is "stop this from growing forever." Cache hit/miss metrics (`GetCacheMetrics()`) are unaffected by a clear;
   they are cumulative counters, not cache-derived. Test:
   `GetOrCompileExpression_CacheExceedsBound_CacheSizeNeverExceedsMaximum` (`DynamicExpressoEvaluatorTests.cs`),
   which compiles `MaxCompiledExpressionCacheSize + 200` distinct expressions and asserts the cache size never
   exceeds the bound at any point during that run, regardless of what other tests left in the shared static
   cache beforehand.

## Rejected measures

Considered and explicitly rejected, per the product owner's cardinal constraint that no guardrail may impair
legitimate expression functionality now or in future:

- **Function or operator allowlists.** Every built-in function exists because a real transformation scenario
  needed it (see `docs/concepts/expressions.md`); restricting which ones an Administrator may call in a given
  expression would directly cut functionality for a use case not yet imagined, for no security benefit (the
  threat model is a trusted author, not a hostile one; see "Purpose and threat model" above).
- **Removing default reference types that expressions may legitimately use.** `Math`, `Convert`, and
  `Enumerable` (DynamicExpresso's `CommonTypes`) and the primitive types are all in scope for legitimate
  transformations (numeric conversions, sequence operations, date/time arithmetic). None grant filesystem,
  process, network, or reflection capability (see "What the evaluator exposes" above), so removing them would
  trade away functionality for no matching risk reduction.
- **Evaluation timeouts.** DynamicExpresso expressions have no loop or recursion construct; the only way to make
  an expression "run long" is a legitimately large nested/chained call graph, which the length ceiling already
  bounds indirectly (a 10,000-character source can only nest so deep). A wall-clock timeout would risk aborting
  a legitimate expression under host load (a noisy-neighbour container, GC pause) with no corresponding
  security gain, since there is no untrusted-input path that could construct an actually-unbounded expression in
  the first place.

## Status and limitations

- **Status:** Complete. All known input paths traced and documented; guardrails implemented and proven;
  DynamicExpresso's default exposure and blocking verified against its 2.19.3 source and proven with unit
  tests, not asserted from memory.
- **Limitation:** this review covers `DynamicExpressoEvaluator` and its current call sites as of 2026-07-11. If
  `ObjectMatchingRuleSource.Expression` or `ExampleDataTemplateAttribute.Expression` gain an authoring surface
  in future, update the input path inventory table above; no further evaluator-level work is expected to be
  needed, since both guardrails and all "what is blocked" behaviour live in the shared evaluator and apply
  automatically.
- **Open finding:** `mv["a"].GetType()` (bare, unchained) parses and evaluates, returning a `System.Type`
  instance; see "What is blocked" above for the detail and why it was left as a documented finding rather than
  patched.
- **Out of scope for this review** (explicitly deferred to other OWASP #500 sub-issues per the delivery plan):
  rate limiting, security response headers/CSP, and `Program.cs` changes generally.

See [issue #500](https://github.com/TetronIO/JIM/issues/500) and the [OWASP Top 10:2025
assessment](plans/doing/OWASP_TOP_10_ASSESSMENT.md) for the wider remediation programme this review is part of.
