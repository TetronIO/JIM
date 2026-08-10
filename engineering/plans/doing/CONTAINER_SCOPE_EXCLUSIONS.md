# Container Scope: Exclusions and Advanced Mode

- **Status:** Doing (Phase 1 complete; model and persistence next)
- **Issue**: [#1255](https://github.com/TetronIO/JIM/issues/1255)
- **Related Issues**: [#351](https://github.com/TetronIO/JIM/issues/351) (Phase 1, OneLevel scope, landed), [#827](https://github.com/TetronIO/JIM/issues/827) (Configuration Change Preview), [#1250](https://github.com/TetronIO/JIM/issues/1250) (export managed scope), [#266](https://github.com/TetronIO/JIM/issues/266) (closed duplicate)
- **Related Plans**: [`CONFIGURATION_CHANGE_PREVIEW.md`](CONFIGURATION_CHANGE_PREVIEW.md)
- **Last Updated**: 2026-08-10

## Overview

Phase 1 (#351) gave each selected Container a `Scope` of `Subtree` or `OneLevel`. That expresses "everything beneath this" and "only what is held directly here", and nothing in between. Phase 2 covers the case that neither reaches: **select a parent and carve specific descendants out of it**, plus a text-based **Advanced Mode** for selections the tree control cannot practically draw.

The whole of Phase 2 rests on one change that is easy to underestimate: today, membership is an **OR** across selected Containers (`ConnectedSystemScope.Contains` asks `SelectedContainers.Any(...)`). An OR structurally cannot express an exclusion, because the including ancestor always matches. Resolution must become a **nearest-ancestor walk** before any exclusion means anything. That change lands first, on its own, with no behavioural difference, and everything else builds on it.

## Business Value

The current answer to "import Corp but not Service Accounts" is to deselect Corp and tick each of its other children individually. That is wrong on three counts:

- It is manual work proportional to the number of siblings, repeated after every reorganisation of the directory.
- It is **silently wrong over time**: a new OU created under Corp is not in the enumerated set, so its objects are never imported and nobody is told. The exclusion form has the opposite and correct failure mode: a new OU under Corp is imported, because Corp is what was selected.
- It buries intent. A reviewer reading eleven ticked siblings cannot tell whether the twelfth was deliberately left out or forgotten.

Service Account, mailbox-archive and staging OUs sitting inside an otherwise wholly-managed branch are the ordinary shape of a production directory, not an edge case.

## Technical Architecture

### Current state

| Concern | Where it lives |
|---|---|
| Per-Container scope (`Subtree` / `OneLevel`) | `ConnectedSystemContainer.Scope`, `ConnectedSystemEnums.cs` |
| The one membership question | `ConnectedSystemScope.Contains(partitionId, containerIdentifier)` |
| What containment *means* | `IConnectorContainment.IsWithinContainer`, implemented by the Connector |
| LDAP's answer | `LdapConnectorUtilities.IsDnWithinContainerScope` / `IsDnWithinAnyContainerScope` |
| Import search roots and row coverage | `ConnectedSystemUtilities.ApplyContainerInclusion` |
| Selection editing rules | `ContainerSelectionEditor` (JIM.Utilities) |
| Portal control | `ScopedHierarchyPicker.razor` |
| Export write guard | `IConnectorManagedScope`, `LdapConnectorExport` |
| Change classification and prose | `ConfigurationChangeConsequences` (`"scope"`, `"container"` keys) |

`ConnectedSystemScope` exists precisely so that import, export and preview cannot answer the same question three different ways. Exclusions must be expressed inside it, not beside it.

### Proposed solution

**1. Exclusion is a flag on the Container row, not a list of paths.**

Add `bool Excluded` to `ConnectedSystemContainer`, mutually exclusive with `Selected`. Containers are already rows in the hierarchy, and they already carry `StableId` (objectGUID / entryUUID), which is what lets a selection survive a rename or a move. An exclusion list keyed on Distinguished Name text would reintroduce exactly the bug `StableId` was added to fix: rename the excluded OU and the exclusion silently evaporates, exposing every object in it to import on the next run.

**2. Membership resolves by nearest ancestor, with specificity asked in the Connector's own terms.**

For a given object identifier, collect every Container whose containment test admits it, then let the **most specific** one decide. `Excluded` wins where it is most specific; `Selected` wins where it is most specific. Most-specific-match is the rule administrators already hold from file-system ACLs and firewall rules, and it composes without further machinery: exclude `OU=Service Accounts`, then re-include `OU=App1` beneath it, to any depth.

Specificity is decided by asking the *same* containment question with a Container's own identifier as the subject: a Container that holds another matching Container is, by definition, the more general of the two. So **`IConnectorContainment` does not change** and no Container hierarchy has to be loaded or walked. That second property is load-bearing rather than incidental: the import and export scope checks receive a flat `IReadOnlyCollection<ConnectedSystemContainer>` with no parent chain populated, so any rule needing `ParentContainer` would work in the preview and silently mis-rank everywhere else.

*(Implemented in `ContainerSpecificity` (JIM.Models). An earlier draft of this plan ranked by depth in JIM's own Container tree; that was wrong for the reason just given, and is superseded.)*

**3. Exclusion filters entries; it does not decompose searches.**

LDAP has no "search this subtree except that branch" primitive, and the issue names the choice between extra searches and client-side filtering as the main design question. **Take client-side filtering, and do not decompose.**

Decomposition (replace one subtree search at `OU=Corp` with a OneLevel search at Corp plus subtree searches at each known child except the excluded one) reads as the efficient answer and is a correctness trap: any child OU created since the last hierarchy refresh is absent from the enumerated set, so its objects are never searched, never imported, and marked obsolete on the next Full Import. That is a silent data-loss path gated on how recently an administrator clicked "Retrieve Hierarchy", and it is the same failure this feature exists to remove from the manual sibling-ticking workaround. Synchronisation integrity outranks bandwidth.

So: the search bases stay exactly as Phase 1 computes them, and `ConnectedSystemScope.Contains` discards excluded entries as they arrive. Being a pure predicate, it applies unchanged to full import, to the delta paths that read a directory-wide change log, to the export write guard and to the preview count, with no per-path reasoning.

The honest cost is transfer of entries that are then discarded. Mitigate by **reporting it rather than hiding it**: the import summary logs and the Activity records how many entries each exclusion discarded. An exclusion covering 500K objects inside a 510K-object parent then shows up as a number an administrator can act on, instead of as an unexplained slow import. Per-run counts also give the evidence any future optimisation would need to justify itself.

**4. Advanced Mode is authored text resolved against the hierarchy, and never fails silently.**

```
+ OU=Corp,DC=example,DC=com                      include Corp and all descendants
- OU=Service Accounts,OU=Corp,DC=example,DC=com  exclude Service Accounts
+ OU=App1,OU=Service Accounts,OU=Corp,DC=...     re-include one branch of it
```

Two rules carry the whole feature:

- **A path that resolves to no Container is an error**, surfaced on the Connected System and on the run that met it, never a no-op. Silence here means an administrator believes a branch is excluded when it is not (objects exposed to import that were meant to be kept out) or believes one is included when it is not (silent obsoletion). Both are the fast/hard-failure case.
- **Switching back to Simple Mode is deliberate and itemised.** Wildcards and paths naming undiscovered Containers have no tree representation. Where both modes can express the same selection it round-trips losslessly; where they cannot, the switch names each line that would be lost and requires confirmation. It never quietly drops one.

Advanced Mode is the last phase and can ship as its own PR: the issue lists two capabilities, and exclusions are complete and useful without it.

### Portal: the affordance appears where it means something

The picker already computes coverage and disables a Container that an ancestor's subtree covers, labelling the row "Covered by ou=People". Exclusion is meaningful on exactly those rows and nowhere else, so that label becomes an action. Four row states, one action each:

| Row state | Reads | Offers |
|---|---|---|
| Selected | (scope control) | Whole subtree / This level |
| Covered by an ancestor | "Covered by *X*" | **Exclude** |
| Excluded | "Excluded from *X*" | **Include** |
| Neither | (Container count) | tick box |

An exclusion beneath a `OneLevel` parent is inert, and falls out of the design for free: such a row is not Covered, so it is never offered Exclude.

## Implementation Phases

**Phase 1: Nearest-ancestor resolution (no behaviour change).** ✅
Replace the `Any(...)` OR in `ConnectedSystemScope.Contains` and in `LdapConnectorUtilities.IsDnWithinAnyContainerScope` with a most-specific-match resolution. With no exclusions in the model the most specific match is still a match, so results are identical; tests pin that equivalence explicitly, including the undetermined (`null`) cases, before anything else moves.

*Landed as `ContainerSpecificity` (JIM.Models), with `ConnectedSystemScope.Contains` and `LdapConnectorUtilities.ResolveMostSpecificContainerScope` resolving through it. `ContainerSpecificityTests` pins the ranking rule, `ConnectedSystemScopeTests` the membership answers including the undetermined ones, and `LdapConnectorUtilitiesTests` the ranking running on this Connector's own predicate.*

**Phase 2: Model and persistence.**
`Excluded` on `ConnectedSystemContainer` + migration; mutual exclusivity with `Selected` enforced in `ContainerSelectionEditor` and rejected at the API boundary; `ApplyContainerInclusion` extended so coverage recalculation understands an excluded branch; hierarchy refresh merge keyed on `StableId` carries exclusions through renames and moves, with a `RequiresPostgres` round-trip test.

**Phase 3: Import, export, and the discard count.**
Connector-side honouring via the shared predicate (full import, delta paths, export write guard); per-exclusion discarded-entry counts in the import summary statistics and on the Activity.

**Phase 4: Portal.**
The four row states above in `ScopedHierarchyPicker`, with the selection rules themselves in `ContainerSelectionEditor` where they stay unit-testable without rendering, plus bUnit coverage of the state transitions.

**Phase 5: Surface parity.**
`Excluded` on `UpdateConnectedSystemContainerRequest` and the read DTOs; `Set-JIMConnectedSystemContainer -Excluded` with Pester coverage; docs for both.

**Phase 6: Change classification, consequences and preview.**
An `"excluded"` key in `ConfigurationChangeConsequences` classified as a scope reduction (as the `"scope"` narrowing already is); the preview counts objects leaving scope through an exclusion; `SyncImportTaskProcessor`'s "Container Scope" unresolved-reference cause covers exclusions in its prose.

**Phase 7: Advanced Mode.**
Parser and canonical text projection, wildcard support, resolution against the hierarchy with hard errors on unresolvable paths, the itemised lossy-downgrade confirmation, and parity across all three surfaces.

## Success Criteria

- Selecting a parent and excluding a descendant imports the parent's objects and none from the excluded branch, on full import and on delta.
- An OU created under a selected parent since the last hierarchy refresh **is** imported; no phase of this design makes import scope depend on hierarchy freshness.
- Renaming or moving an excluded Container keeps it excluded.
- Export refuses to write into an excluded branch.
- The preview's count of objects leaving scope matches what the next import actually obsoletes.
- Every import that discarded entries through an exclusion reports how many.
- An Advanced Mode path that resolves to nothing raises an error on the Connected System and on the run.
- Portal, REST and PowerShell all express exclusions.

## Risks and Mitigations

| Risk | Mitigation |
|---|---|
| Client-side filtering transfers entries only to discard them | Report the discard count per exclusion per run, so the cost is visible and actionable; the search-base decomposition that would avoid it is rejected above on correctness grounds, and the counts are the evidence for revisiting that |
| Nearest-ancestor resolution changes behaviour unintentionally in Phase 1 | Ship it alone, with equivalence tests over the existing selections, before the model gains an `Excluded` flag |
| An exclusion and a selection on the same row | Mutually exclusive by construction in the editor, rejected with 400 at the API, asserted in tests |
| Advanced Mode and the tree disagree | The text is the canonical projection; downgrading to Simple Mode itemises every line it cannot represent and requires confirmation |
| Exclusions weaken reference resolution | An excluded object is an object a reference cannot resolve to, exactly as an unselected one is; the existing "Container Scope" cause covers it, with prose extended to say so |

## Rejected Alternatives

- **Decompose subtree searches to avoid excluded branches.** Makes import scope depend on how recently the hierarchy was refreshed; a new OU under a selected parent would be silently skipped and its objects obsoleted. Rejected on synchronisation integrity.
- **An exclusion list of Distinguished Names, separate from the Container rows.** Breaks on rename and move, which is the defect `StableId` exists to prevent.
- **An `Excluded` member on `ConnectedSystemContainerScope`.** Conflates two orthogonal facts (how far a selection reaches; whether a branch is carved out) into one enum, and leaves "excluded" needing a scope of its own.
- **Widening `IConnectorContainment` to report containment depth.** Unnecessary: the existing predicate already ranks two Containers when one of them is made the subject, so every Connector gets ranking for free from the method it has already implemented.
- **Ranking by depth in JIM's own Container tree.** Needs `ParentContainer` populated, which the import and export scope checks cannot rely on; it would rank correctly in the preview and silently mis-rank in the two paths that actually move data.

## Dependencies

None beyond #351, which has landed. No new packages.
