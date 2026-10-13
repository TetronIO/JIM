# JIM PowerShell Module Reference

> Conventions for `src/JIM.PowerShell` and the customer-facing cmdlet documentation under `docs/powershell/`. Loads automatically (alongside `src/CLAUDE.md`) when working in this subtree.
>
> Every rule below exists because it was broken and shipped. Two of the resulting examples destroyed data when copy-pasted.

## Why this module needs its own rules

PowerShell fails silently in ways C# does not, and the module's documentation is hand-maintained against DTOs that live in another project. Those two facts combine badly:

- **An unknown property returns `$null`, it does not throw.** `$_.Enabled` on an object whose property is `IsEnabled` yields `$null` and no error, at any strictness level. A `Select-Object` renders an empty column; a `Where-Object` filter matches nothing.
- **`$null` is falsy, so a negated filter matches everything.** `Where-Object { -not $_.Enabled }` on that same object is `-not $null`, which is `$true` for every row. A shipped example read `Get-JIMCertificate | Where-Object { -not $_.Enabled } | Remove-JIMCertificate -Force`, which deleted the caller's entire trusted certificate store rather than its disabled certificates.
- **Nothing in the build catches it.** `dotnet build`, `dotnet test` and Pester all pass with a wrong property name in a doc example. Only reading the DTO catches it.

Treat a property name in an example or an Output table as a factual claim about another project's source, and verify it there.

## Parameter aliases

Aliases are the module's sharpest edge. Read this section before adding one.

**What they are for.** Pipeline binding matches on property *name*. A Connected System object exposes `Id`, not `ConnectedSystemId`, so `Get-JIMRunProfile` carries `[Alias('Id')]` on its `-ConnectedSystemId` parameter purely so this works:

```powershell
Get-JIMConnectedSystem -Name "Active Directory" | Get-JIMRunProfile
```

**An alias cannot be pipeline-only.** The same declaration also makes `-Id` a valid command-line spelling. There is no way to have one without the other. This is the hazard: on a cmdlet whose noun is the *child*, `-Id 42` reads as "Run Profile 42" and means "every Run Profile on Connected System 42". A shipped example piped exactly that into `Remove-JIMRunProfile -Force`, which would have deleted every Run Profile on that Connected System instead of one.

Rules:

- **Never remove such an alias to "fix" the ambiguity.** It would break the documented piping, which is the reason it exists.
- **In documentation, always spell the real parameter name out.** Never use an alias in an example. `scripts/Lint-DocExamples.ps1` enforces this over every ```powershell fence under `docs/` and every `.EXAMPLE` block, and runs in the `changelog-lint` workflow.
- **Never alias `Id` on a cmdlet that already has its own `-Id` parameter.** PowerShell rejects the command outright, even across mutually exclusive parameter sets and with no piping involved: `The parameter 'Id' cannot be specified because it conflicts with the parameter alias of the same name for parameter 'ScheduleId'`. The cmdlet fails to bind at all, not merely ambiguously.
- **When you need to accept a piped parent but the alias is unavailable**, take the whole object instead. `Get-JIMScheduleExecution` is the reference implementation: an `-InputObject` parameter with `ValueFromPipeline` across the relevant sets, resolved in `process` as "explicit `-ScheduleId` if supplied, else `$InputObject.Id`". Prefer the explicit parameter, because an object carrying a `scheduleId` property binds *both* parameters and the explicit one must win.

**Before adding `[Alias('Id')]`, ask whether the aliased parameter is the cmdlet's own noun.** `Start-JIMRunProfile` aliasing `-RunProfileId` is safe; `Get-JIMRunProfile` aliasing `-ConnectedSystemId` is the hazardous shape. Seven `Get-` cmdlets currently carry the hazardous form. Adding another is allowed when piping genuinely requires it, but say so in a comment at the declaration, as `Get-JIMScheduleExecution` does.

## The reference is generated from the cmdlet

Each cmdlet's Syntax, Parameters and Output on its `docs/powershell/` page are generated from the module by `scripts/Update-PowerShellReference.ps1`, between `<!-- BEGIN GENERATED: <cmdlet> -->` and `<!-- END GENERATED: <cmdlet> -->` (ACC-5 in `engineering/DOCUMENTATION_STANDARD.md`; #1083). `build-and-test` fails when a block and the module disagree. So the cmdlet is the source:

- **A parameter's description is its `.PARAMETER` help.** Write it for both `Get-Help` and the site: one or two plain sentences, no Markdown. Name cmdlets, parameters and `$true`/`$false`/`$null` plainly; the script puts them in code on the page, and writes "empty" where help or a DTO comment says null (VOI-1). Leave out what the attributes already say; the script adds pipeline input (naming the properties that bind it, so an `[Alias('Id')]` that lets a parent pipe in is stated as a property, never as a spelling to type), any other alias, `ValidateSet` values, `ValidateRange` limits and wildcard support itself, and the default comes from the `param` block.
- **Declare what each parameter set returns** with `[OutputType('JIM.<Schema>', ParameterSetName = '<Set>')]`, naming the response schema in the OpenAPI document (the DTO's class name); one without `ParameterSetName` covers every set. The script tables that schema's properties, with each one's type and the description from the DTO's XML comment, so they cannot be written from memory; document a DTO property there, not in the cmdlet's help. `[OutputType([PSCustomObject])]` says nothing and fails the check, as does declaring nothing at all: a cmdlet that returns nothing says `[OutputType([void])]`. A cmdlet that builds its own object declares a `JIM.` name of its own, listed with its reason in `docs/.conformance/powershell-reference.psd1` (`NonApiOutputTypes`), and lists its properties in `.OUTPUTS` as a paragraph whose first line is the type's name and whose others read `Name (type): description` (a line that does not runs on from the one above; `Connect-JIM` is the example), which the script tables; one that only gives the API's object a name of its own (`Get-JIMServiceHealth`) maps that name to the schema under `OutputTypeSchemas` in the same file, and the schema's properties are tabled for it.
- **Add to the API's object; never rebuild it.** A cmdlet that returns the API's response with something more (`Get-JIMConnectedSystemObjectSyncPreview`'s flattened `Outcomes`) adds it with `Add-Member` and returns the response. Rebuilding a new object from a list of the response's properties drops whatever the API adds later, silently: the sync preview cmdlets lost `GeneratedValueProbes` that way. Declare a name of its own, map it to the schema under `OutputTypeSchemas`, and list only the added properties in `.OUTPUTS` in the format above; the script tables them after the schema's, and fails if one duplicates a schema property.
- **Say when each type comes back where the parameter sets cannot.** The script labels each declared type by the parameters that set its parameter sets apart ("By default", "With -Id or -Follow"). Two types from one set (`Test-JIMConnection` returns its status, or with `-Quiet` a `bool`) fail the check until `.OUTPUTS` labels each, with the type's name, a colon and when it comes back: `System.Boolean: With -Quiet` on a line of its own, or as the first line of a property list (`JIM.ConnectionStatus: By default`).
- **`.OUTPUTS` is the prose above the generated table:** what comes back and when (`With -PassThru, ...; otherwise nothing.`), and what any property means where its name does not say. A property the module adds is listed in the `Name (type): description` form above, never described only in prose: the table and the example check (next) read only the list.
- **Examples read only what the cmdlet returns (ACC-6).** Every property an example reads from a JIM cmdlet's output, on a page or in `.EXAMPLE` help (`Select-Object Name`, `$_.Name`, `$widget.Name`), is checked against what that cmdlet returns for the parameter set the example binds, by `Lint-DocExamples.ps1 -OpenApiPath` in `build-and-test`. It reads the same declarations as the table, so a failure means the example is wrong or the declaration is incomplete.

After changing a cmdlet with a block, regenerate (`-OpenApiPath` takes a document `Generate-OpenApiDoc.ps1` wrote earlier; without it, the script generates one, which builds the solution):

```bash
pwsh ./scripts/Update-PowerShellReference.ps1 -OpenApiPath /tmp/openapi-v1.json
pwsh ./scripts/Lint-DocExamples.ps1 -OpenApiPath /tmp/openapi-v1.json   # ACC-6, the examples' property reads
```

The sections below still apply to every section that has no block yet; `-Check` lists them.

## Documenting output

**Check every property name against the DTO before writing it.** An audit found eight of twenty pages in `docs/powershell/` naming properties that do not exist. The Output tables in particular had been written from memory.

**Different parameter sets return different shapes, and this is the most common error.** A list or search returns a header DTO; `-Id` returns the full entity or detail DTO. They are not supersets of one another:

| Cmdlet | List / search returns | `-Id` returns |
|---|---|---|
| `Get-JIMMetaverseObject` | `MetaverseObjectHeaderDto` (`Attributes`, a dictionary) | `MetaverseObjectDto` (`AttributeValues`, a list) |
| `Get-JIMConnectedSystem` | `ConnectedSystemHeader` (`ConnectorId`) | `ConnectedSystemDto` (nested `Connector.Id`) |
| `Get-JIMPredefinedSearch` | `PredefinedSearchHeader` (`MetaverseAttributeCount`) | `PredefinedSearchDetailDto` (`Attributes`, `CriteriaGroups`) |
| `Get-JIMExampleDataSet` | `ExampleDataSetHeader` (`ValueCount`) | the entity, with its values |

State which shape an Output section describes, and never document a header's fields for the `-Id` form.

**Casing is not the trap; names are.** `ConvertTo-JIMOutputObject` normalises wire camelCase to PascalCase, and member access is case-insensitive regardless, so `$x.isEnabled` and `$x.IsEnabled` both work. `$x.Enabled` does not. Do not "fix" casing differences; do verify names.

**Do not blanket-replace a property name across the docs.** `SyncRuleHeader` really does name its field `Enabled`, so a global `Enabled` to `IsEnabled` sweep would have broken working examples while fixing others. Check each cmdlet's own DTO.

## Destructive examples

An example that deletes or clears data must not understate its scope in its title. Two shipped examples did.

- **Say what it deletes.** "Pipeline deletion" over a wildcard filter is wrong when the command force-deletes every match; `docs/powershell/schedules.md` gets this right with "Remove multiple schedules via pipeline".
- **Name the multiplicity when a filter is a wildcard.** `-Name "Decommissioned*"` piped into a removal deletes everything matching, and Connected System deletion cascades to the whole connector space.
- **Suggest a dry run.** For any forced bulk delete, tell the reader to run it without `-Force` first, or to inspect the `Get-` output, before committing.

## `-Force` skips the prompt, never `-WhatIf`

A cmdlet with both `SupportsShouldProcess` and a `-Force` switch decides whether to act with `Confirm-JIMAction -Cmdlet $PSCmdlet -Target <what> -Action <verb phrase> -Force:$Force`, never `if ($Force -or $PSCmdlet.ShouldProcess(...))`. The `-or` form never consults `ShouldProcess` once `-Force` is given, so `-WhatIf -Force` carries the action out instead of previewing it: 22 cmdlets did this, `Reset-JIMSystem` among them. `Confirm-JIMAction` lets `-Force` answer the prompt and leaves `-WhatIf` (or a session-wide `$WhatIfPreference`) in charge. The older idiom of setting `$ConfirmPreference = 'None'` under `-Force` and then calling `ShouldProcess` is also correct, and four Metaverse and Synchronisation Rule Mapping cmdlets use it. `Confirm-JIMAction.Tests.ps1` fails the build on any line that combines `$Force` with a `.ShouldProcess(` call.

## Every exported cmdlet is documented

A new exported cmdlet needs a heading (or a row in the first column of a table) on a `docs/powershell/` page in the same pull request, and a removed one must leave the docs with it: `scripts/Test-DocsCoverage.ps1` (COV-1) fails the `changelog-lint` check otherwise. Before it existed, `Get-JIMDataFlow` shipped with no reference page at all.

## Documentation and behaviour must agree

Where a cmdlet and its documentation disagree, fix the cmdlet if the documented behaviour is the desirable one; three cmdlets had promised behaviour they did not have. Where the behaviour cannot honestly exist, remove the parameter rather than documenting a broken one: `Invoke-JIMExampleDataTemplate -Wait` claimed to block until generation finished but only emitted a warning saying it was unimplemented, and it could not be implemented because the execute endpoint returns a bare `202` with no handle to poll.

A parameter that warns "not implemented" is worse than no parameter; it appears in tab-completion and in the parameter table, so users write scripts around it.

## Testing

Pester tests live in `src/JIM.PowerShell/Tests/`, one file per cmdlet area, and run in CI alongside `dotnet test`. Run them from the repository root:

```powershell
pwsh -NoProfile -Command '$c = New-PesterConfiguration; $c.Run.Path = @("./src/JIM.PowerShell/Tests","./scripts/Tests"); $c.Run.PassThru = $true; $r = Invoke-Pester -Configuration $c; "Passed=$($r.PassedCount) Failed=$($r.FailedCount)"'
```

- **Test binding behaviour, not source text.** Asserting that an attribute appears in a `.ps1` file proves nothing about what PowerShell does with it. Pipe a representative object in and assert on the request the cmdlet builds. The pipeline-binding tests in `Schedules.Tests.ps1` and `ServiceSettings.Tests.ps1` show the pattern.
- **Capture a baseline count before changing anything**, and require the after count to be higher with zero failures. A refactor that silently drops a `Describe` block otherwise looks green.
- **Follow the repository TDD rule**: the test fails first, for the right reason, before the fix exists.

## Related

- `src/CLAUDE.md` - API endpoint identifier rules and the clearing-optional-values convention, both of which bind cmdlets as well as the REST API.
- `engineering/DOCUMENTATION_STANDARD.md` - the documentation standard, including ACC-1 and ACC-2, which `Lint-DocExamples.ps1` enforces on the module's own `.EXAMPLE` help as well as on `docs/`.
- `test/CLAUDE.md` - the repository-wide TDD workflow.
