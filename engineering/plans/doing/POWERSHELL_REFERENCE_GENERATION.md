# Generated PowerShell Cmdlet Reference

- **Status:** Doing (Phase 1 complete)
- **Issue:** [#1083](https://github.com/TetronIO/JIM/issues/1083)
- **Blocks:** the `powershell/` folder migration of the documentation standard ([#1454](https://github.com/TetronIO/JIM/issues/1454), `PUBLIC_DOCUMENTATION_STANDARD.md` Phase 4)

## Overview

Each cmdlet section under `docs/powershell/` restates, by hand, three things the code already owns: the syntax, the parameter table and the output properties. They drift, and in PowerShell a wrong property name fails silently: `$_.Enabled` on an object whose property is `IsEnabled` is `$null`, so `Where-Object { -not $_.Enabled }` matched every certificate, and the documented example deleted the whole trusted store.

A script in this repository generates those three blocks from the module, in place inside the existing pages, and CI fails when a page and the module disagree. Everything else on a page (the introduction, examples, notes and links) stays hand-written.

## Business Value

The PowerShell pages are what automation authors copy from, and a copied example runs against production. An audit found eight of twenty pages naming properties that do not exist, and nothing in the build noticed: `dotnet build`, `dotnet test` and Pester all pass. Generation removes the defect class rather than policing it, and lets reviewers spend their attention on the prose and examples, which only a person can check.

## Decision: our own script, not platyPS

Chosen on 2026-10-12, after researching Microsoft.PowerShell.PlatyPS 1.0.3 (Microsoft, MIT, no dependencies, actively released) and HelpOut (an individual's project, MIT, lightly maintained).

- **platyPS solves a different problem.** It exists to author `Get-Help` content in Markdown and compile it to MAML. JIM's help is comment-based help in each cmdlet's `.ps1`, which that workflow would replace or duplicate.
- **Its output is the wrong shape.** One file per cmdlet (186 of them) in its own schema, with its own front matter and headings, where the site has 24 pages grouped by area and the documentation standard's front matter. It would need a post-processor, so it saves little code.
- **It does not address output shapes**, the hardest part and the one that caused the worst defect. No tool does: the cmdlets return the REST API's objects, which only the OpenAPI document describes.
- **The module already gives everything needed.** `Get-Command` returns every parameter set, parameter, type, mandatory flag, pipeline binding and validation attribute; `Get-Help` returns each parameter's description and default. `Lint-DocExamples.ps1` already reads the module the same way.

The cost is a script of a few hundred lines with Pester tests, owned here, and no new dependency.

## Technical Architecture

### What is generated, and from where

For each cmdlet section, the script replaces one marked block holding **Syntax**, **Parameters** and **Output**:

```markdown
## Get-JIMCertificate

Retrieves certificates from the trusted store.            <- hand-written

<!-- BEGIN GENERATED: Get-JIMCertificate -->
### Syntax
...
### Parameters
...
### Output
...
<!-- END GENERATED: Get-JIMCertificate -->

### Examples                                              <- hand-written
```

| Block | Source |
|-------|--------|
| **Syntax** | `Get-Command`: one line per parameter set, labelled with the set's name and `(default)`; mandatory parameters bare, optional ones in brackets, positional ones the cmdlet declares first in PowerShell's notation, types in the site's lower-case form (`<string>`, `<guid>`, `<int>`); common parameters left out, except `[-WhatIf] [-Confirm]` closing the line of a cmdlet that supports them, followed by a sentence when its confirmation impact is High, since it then asks before it acts. |
| **Parameters** | `Get-Command` for name, type, and whether it is required (with the sets it belongs to, where it is not in every set); `Get-Help` for the default and the description, the description taken from the cmdlet's `.PARAMETER` help. Generated suffixes state what the attributes already say: pipeline input, with the property names that bind it (an alias on such a parameter is how a parent object pipes in, so it is named as a property, never offered as a spelling to type), any other alias, `ValidateSet` values, `ValidateRange` limits, wildcard support. |
| **Output** | The cmdlet's `.OUTPUTS` help as prose, then each type the cmdlet declares with `[OutputType('JIM.<Schema>', ParameterSetName = '<Set>')]`, labelled by when it comes back (from the parameters that set its parameter sets apart: "By default", "With `-Id` or `-Follow`"; no label for the only type every set returns), and a table of that schema's properties from the OpenAPI document: each one's name in PascalCase (as `ConvertTo-JIMOutputObject` returns it), its type, and its description, which is the DTO's XML comment. No class name reaches the page: a property holding another object is an `object` naming its properties, and an enumeration a `string` naming its values. A schema is tabled where the page first returns it; a later cmdlet on the page refers back to that one. A declared type that is not an OpenAPI schema (the few cmdlets that build their own objects, and `string`, `bool` and the like) is named, in an allow-list that gives the reason, and a type the module builds lists its properties in `.OUTPUTS` (the type's name on one line, then `Name (type): description` per property), which is tabled like a schema; a name the module gives the API's own object is mapped to that schema instead, and tabled with its properties, followed by any the module adds to it, listed in `.OUTPUTS` the same way. A cmdlet that returns nothing says so with `[OutputType([void])]`. |

`[OutputType]` per parameter set is PowerShell's own way to say what a cmdlet returns, and `Get-JIMServiceHealth` already uses it with `JIM.`-prefixed names; the script reads it through `Get-Command`, so nothing parses free text. Single source per fact: a parameter's description lives in its `.PARAMETER` help, so `Get-Help` and the site say the same thing.

### The script

`scripts/Update-PowerShellReference.ps1`:

- Imports the module from `src/JIM.PowerShell/JIM.psd1` and, for every marked block under `docs/powershell/`, renders the cmdlet's block and writes it back.
- `-Check` writes nothing and fails, naming the cmdlet and the first differing line, when any block differs from what it would generate, or a block names a cmdlet the module does not export, or a cmdlet with a block has a parameter with no `.PARAMETER` help or declares no output type. During migration it also lists, as warnings, the exported cmdlets that have no block yet, counted from the module rather than from the pages' headings, so a cmdlet the pages give no section at all is listed too.
- `-OpenApiPath` reads the OpenAPI document; without it the script generates one with `Generate-OpenApiDoc.ps1`, which builds.

### Where it runs

In `build-and-test`, beside ACC-3 and COV-4, which already generate the OpenAPI document from the build and run on every pull request. It becomes rule **ACC-5** (Block) in the documentation standard: a cmdlet's Syntax, Parameters and Output on a `docs/powershell/` page are generated from the module and match it.

### Examples (ACC-6)

Generation fixes the tables, not the examples, and the motivating defect was an example. Once output types are declared, `Lint-DocExamples.ps1` gains **ACC-6** (Block): a property an example reads from a JIM cmdlet's output (`$_.Name` in a script block piped from it, `Select-Object Name`, `Sort-Object Name`) is a property of an output type of a parameter set the invocation can bind to. It checks only cmdlets that declare their output, so it can land before migration ends.

## Implementation Phases

### Phase 1: Generator, check and pilot ✅

The script, written test-first in Pester against fixture modules and pages under `$TestDrive`; ACC-5 in the standard; the `build-and-test` step; and `certificates.md` as the pilot: its six cmdlets declare their output types per parameter set, their `.PARAMETER` help takes the better wording from the page's tables, and their Syntax, Parameters and Output become generated blocks.

**Delivered** as planned, with three choices the pilot and the first migration settled:

- **Only the positions a cmdlet declares are shown.** A cmdlet that declares no `Position` still gets positions from PowerShell, in declaration order (67 of them do), and showing those would document an accident. One that declares any (21 do, such as `Connect-JIM`) gets no others, so every position it has was chosen: the syntax shows them in PowerShell's notation (`[-Url] <string>`), and the parameter's row says which argument it is.
- **Output tables carry each property's type and description, not just its name.** The first pages to migrate described their properties by hand, and a list of names would have lost that. The description is the DTO's XML comment, so documenting a DTO improves the API reference and these pages at once, and a schema whose properties are all undescribed is tabled without the empty column. Each schema is tabled once per page, where the page first returns it.
- **A switch that selects a parameter set is optional where the code says so.** `Get-JIMCertificate -EnabledOnly` is how the Enabled set is chosen, but the switch is not mandatory in it, so the syntax shows `[-EnabledOnly]`, as `Get-Command -Syntax` does. The generated page says what the code says; where that reads oddly, the fix is in the cmdlet.

### Phase 2: Migrate the remaining pages

Smallest first, a few per pull request. Each cmdlet gains its output types and the page's better parameter wording; `metaverse.md` and `schedules.md`, which give cmdlets H3 headings, move to the H2 layout every other page uses. A page that its migration would take over its size budget (STR-8) is split as it migrates, one sub-feature per page: the full Activity tables took `activities.md` over, so the execution item and causality cmdlets moved to `execution-items.md`, the Sync Preview cmdlets left `previews.md` for `sync-preview.md`, the cmdlets that start, follow and stop a Schedule's runs left `schedules.md` for `schedule-executions.md`, and the Metaverse schema cmdlets left `metaverse.md` for `metaverse-object-types.md` and `metaverse-attributes.md`. When the Metaverse Object cmdlets migrated, `Set-JIMMetaverseObjectPassword` joined the queue cmdlets on `password-synchronisation.md`, and the two generated-value cmdlets went to `generated-values.md`, where the generated-value cmdlets on `synchronisation-rules.md` join them when that page migrates. That page migrates in two parts: the scoping criteria and Object Matching Rule cmdlets moved first, to `scoping-criteria.md` and `object-matching-rules.md`, with the two groups of matching rule cmdlets (per Object Type and per Synchronisation Rule) becoming ordinary H2 sections. Then its generated-value cmdlets joined `generated-values.md`, and the two initial password cmdlets moved to `initial-password.md`, leaving the Synchronisation Rule and Attribute Flow cmdlets. The Attribute Flow cmdlets then moved to `attribute-mappings.md`, named for the configuration page it mirrors, so `synchronisation-rules.md` holds only the four Synchronisation Rule cmdlets. The four Attribute Flow cmdlets migrated on their new page, which stays under budget once their hand-written parameter tables are replaced by generated ones. `connected-systems.md` splits as it migrates: the schema import, Object Type and attribute cmdlets moved first, to `connected-system-schema.md`, and the auxiliary object class cmdlets, which would have taken that page over budget, to `connected-system-auxiliary-classes.md`. The partition and container cmdlets followed, to `partitions-and-containers.md`, named for the configuration page it mirrors. Then the Connected System Object, unresolved reference and object password cmdlets moved to `connected-system-objects.md`, and `Get-JIMPendingExport` to `pending-exports.md`, whose generated detail table would have taken the objects page over budget. Last, the server certificate cmdlets joined `certificates.md`, and the Password Synchronisation settings cmdlets joined `password-synchronisation.md`; with them there, that page's queue cmdlets moved to `password-queue.md` to keep it under budget, and `Get-JIMConnectorDefinition` moved to `connectors.md`. The seven cmdlets left on `connected-systems.md` migrated in place.

The first batches headed each table with the type's class name (`ActivityDetailDto`), and a docs review rightly flagged it (VOI-1): nobody can type one, since the module returns a `PSCustomObject`. Tables are now labelled by when they come back, and nested types are shown as `object` or `string` with their properties or values named. Where two types come from one parameter set (`Test-JIMConnection` returns its status, or with `-Quiet` a `bool`), no set can say which comes back, so the check fails until the `.OUTPUTS` help labels each with a line `<type>: <when it is returned>`. Doing this needed the OpenAPI document to describe enumerations as the names the API sends: it had declared every one an integer.

### Phase 3: ACC-6, the example property check ✅

As described under Examples above. **Delivered** in `Lint-DocExamples.ps1`, run with `-OpenApiPath` in `build-and-test`; `changelog-lint` runs the lint without the document and skips the rule. What a cmdlet returns is decided once, in `scripts/PowerShellReference.psm1`, which the generator and the lint both import, so the Output tables and the example check cannot disagree. Beyond the three forms above, it follows the output through stages that leave it unchanged (`Where-Object`, `Sort-Object`), and reads from a variable the output was assigned to, a `foreach` over that variable, and a parenthesised call. Its first run over every page and all 725 `.EXAMPLE` blocks found one example reading `StepDisplay` from `Get-JIMWorkerTask`: the property exists, but the cmdlet's `.OUTPUTS` help described it in prose, so the generated Output table had left it out too; it is now listed.

### Phase 4: Close

An exported cmdlet with no generated block fails ACC-5; `src/JIM.PowerShell/CLAUDE.md` and the standard drop the manual-verification guidance generation replaces; this plan moves to `done/`, closing #1083. The `powershell/` folder migration of #1454 (front matter, and splitting the three pages over their size budget) follows.

## Success Criteria

- Every cmdlet's Syntax, Parameters and Output on the site are generated from the module (#1083's first criterion).
- A pull request that changes a cmdlet without regenerating its block, or edits a block by hand, fails `build-and-test` (second).
- Every documented output property is read from the OpenAPI schema the cmdlet declares, and every property an example reads is checked against it (third).
- Introductions, examples and notes are untouched by generation (fourth).
- The CLAUDE.md guidance on hand-verifying names is replaced by a pointer to the script (fifth).

## Benefits

- The class of defect #1083 describes cannot recur: names, types, sets and defaults come from the code.
- `Get-Help` improves as a side effect, since the site's better parameter wording moves into the module's help.
- No new dependency to pin, scan or keep current.

## Dependencies

None new. The OpenAPI document comes from `Generate-OpenApiDoc.ps1`, already run in `build-and-test`.

## Risks & Mitigations

| Risk | Mitigation |
|------|------------|
| A declared output type is wrong (the cmdlet returns a different object) | The declaration is one line beside the code that makes the call, reviewed with it; the cmdlet's Pester tests can assert it. A later check could match the declared schema to the endpoint the cmdlet calls, if declarations prove error-prone |
| Generated tables push pages further over their size budget | The three pages already far over (Connected Systems, Synchronisation Rules, Metaverse) split in #1454's `powershell/` migration regardless |
| Parameter descriptions lose the links the hand tables carried | Links belong in the hand-written introduction and notes; `.PARAMETER` help is also read in a terminal, where a Markdown link is noise |
| A long migration leaves the site half generated | Cmdlets without blocks are listed by `-Check` on every run, and Phase 4 makes a missing block a failure |
