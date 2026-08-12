# Container Scope: Exclusions and Advanced Mode

- **Status:** Doing (Phases 1-6 complete; Advanced Mode outstanding)
- **Issue**: [#1255](https://github.com/TetronIO/JIM/issues/1255)
- **Related Issues**: [#351](https://github.com/TetronIO/JIM/issues/351) (Phase 1, OneLevel scope, landed), [#827](https://github.com/TetronIO/JIM/issues/827) (Configuration Change Preview), [#1250](https://github.com/TetronIO/JIM/issues/1250) (export managed scope), [#266](https://github.com/TetronIO/JIM/issues/266) (closed duplicate)
- **Related Plans**: [`CONFIGURATION_CHANGE_PREVIEW.md`](CONFIGURATION_CHANGE_PREVIEW.md)
- **Last Updated**: 2026-08-12 (Phase 6 completed)

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
| LDAP's answer | `LdapConnectorUtilities.IsDnWithinContainerScope` / `IsDnInScope` (renamed from `IsDnWithinAnyContainerScope` in Phase 3, where "within any" stopped being what it answers) |
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
| Excluded by an ancestor | "Excluded by *X*" | tick box |
| Neither | (Container count) | tick box |

*(The fourth row was added during Phase 4. Rendered plain, a Container inside a carve-out reads as "nothing has been decided here" when something has, and ticking it is meaningful where ticking a covered one is not: it brings the branch back into scope.)*

An exclusion beneath a `OneLevel` parent is inert, and falls out of the design for free: such a row is not Covered, so it is never offered Exclude.

## Implementation Phases

**Phase 1: Nearest-ancestor resolution (no behaviour change).** ✅
Replace the `Any(...)` OR in `ConnectedSystemScope.Contains` and in `LdapConnectorUtilities.IsDnWithinAnyContainerScope` with a most-specific-match resolution. With no exclusions in the model the most specific match is still a match, so results are identical; tests pin that equivalence explicitly, including the undetermined (`null`) cases, before anything else moves.

*Landed as `ContainerSpecificity` (JIM.Models), with `ConnectedSystemScope.Contains` and `LdapConnectorUtilities.ResolveMostSpecificContainerScope` resolving through it. `ContainerSpecificityTests` pins the ranking rule, `ConnectedSystemScopeTests` the membership answers including the undetermined ones, and `LdapConnectorUtilitiesTests` the ranking running on this Connector's own predicate.*

**Phase 2: Model and persistence.** ✅
`Excluded` on `ConnectedSystemContainer` + migration; mutual exclusivity with `Selected` enforced in `ContainerSelectionEditor`; `ApplyContainerInclusion` extended so coverage recalculation understands an excluded branch; hierarchy refresh merge keyed on `StableId` carries exclusions through renames and moves, with a `RequiresPostgres` round-trip test.

*Landed. `Excluded` (mapped) and `ExcludedByAncestor` (derived, alongside `Included`) on `ConnectedSystemContainer`; migration `AddConnectedSystemContainerExcluded`; `ContainerSelectionEditor.ToggleExcluded` plus the mutual-exclusivity and clear-selection rules; `ApplyContainerInclusion` rewritten as a nearest-ancestor-statement walk. Two adjustments to what this phase was scoped to:*

- *The API-boundary rejection moves to Phase 5, where the field is first exposed. Until a surface can set `Excluded`, there is nothing to reject; the invariant is enforced in the editor, so it cannot be expressed through the portal either way.*
- *`Scope` now describes how far a Container's own statement reaches, whether that statement is a selection or an exclusion, rather than applying to selections alone. This costs nothing (the property already existed) and makes a OneLevel exclusion expressible; the portal offers the control only on selected rows, so an exclusion made there carries the Subtree default.*

*Two rules fell out of the work rather than the design. Roll-up must refuse to select an excluded parent, or completing the selection of every re-inclusion inside a carved-out branch silently undoes the exclusion. And an exclusion must not change the import's search roots: `GetTopLevelSelectedContainersTests` pins that, because the alternative is the search decomposition rejected below.*

**Phase 3: Import, export, and the discard count.** ✅
Connector-side honouring via the shared predicate (full import, delta paths, export write guard); per-exclusion discarded-entry counts in the import summary statistics and on the Activity.

*Landed. The phase turned on a distinction the code did not previously draw: the **selected** Containers say where JIM may search and write, and they do not say which Container decides an object's fate. `ConnectedSystemExtensions.GetScopeDecidingContainers` collects the selections and the exclusions together, and `ContainerSpecificity.IsInScope` ranks them so the most specific statement wins. Four paths ask it and all reach the same answer: the full import and the AD USN delta (filtering the entries their searches return, in `ConvertLdapResults`, since a Subtree search cannot exclude a branch server-side), the changelog and accesslog deltas, the export write guard, and `ConnectedSystemScope` so the preview cannot state a count the next import contradicts.*

*Three decisions taken during the work:*

- *Where containment is not a hierarchy, two Containers can admit an object without either holding the other, which `ContainerSpecificity` explicitly refused to resolve for opposing meanings. **Excluded wins** such a tie: importing an object an administrator excluded is the worse of the two failures.*
- *`ConnectedSystemScope` reads which Containers carve out from the **proposal**, not from the Containers' stored `Excluded` flags, for the same reason the selection is a parameter at all. A preview that read the stored flags would evaluate the configuration the administrator is trying to move away from.*
- *A Container the Connector creates mid-run is in scope because JIM selects it as the run ends, but not when an exclusion carves out the branch it was created in.*

*The per-exclusion counts are reported as import summary statistics in the log. **Putting them on the Activity is deferred to Phase 6**: the Activity has no informational channel, only `WarningMessage`, and an exclusion doing exactly what it was configured to do is not a warning; flagging every exclusion-configured import as warned would train administrators to ignore the field. Phase 6 already opens the Activity and preview surfaces for this feature.*

*No changelog entry or public documentation: nothing an administrator can reach has changed, because no surface can set `Excluded` until Phases 4 and 5. The user-facing entry belongs with the phase that makes exclusions settable.*

*Runtime-verified against OpenLDAP on the sandbox light stack, not by unit tests alone. `ou=Corp` selected as a subtree imported 4 objects; excluding `ou=Service Accounts` beneath it imported 2 and logged "Discarded 2 entries read from excluded Containers across 1 exclusion(s)" attributed to that Container; selecting `ou=App1` beneath the exclusion brought its service account back, importing 3 and discarding 1.*

**Phase 4: Portal.** ✅
The four row states above in `ScopedHierarchyPicker`, with the selection rules themselves in `ContainerSelectionEditor` where they stay unit-testable without rendering, plus bUnit coverage of the state transitions.

*Landed, as **five** row states rather than four. The model's `ExcludedByAncestor` needed one of its own: a Container inside a carve-out rendered plain reads as "nothing has been decided here" when something has, and ticking it is meaningful (it brings the branch back), which is what separates it from a covered row. Both actions sit in the column the scope control already occupies, so no row grows a sixth column and the actions line up down the tree. **Exclude** appears on hover and on keyboard focus, because every Container beneath a selected branch is a candidate for it; **Include** stays at rest, because an exclusion is a deliberate configuration that should be reversible without discovering a hover. An excluded row's tick box is disabled: ticking it would clear the exclusion by a second route meaning something different from the Include beside it. `ContainerSelectionEditor.DecidingAncestor` names the Container that decided a row, walking the hierarchy the way `ApplyContainerInclusion` does so a row can never name one Container while another governs it, and the scope summary reports how many Containers are excluded where there are any.*

**Phase 5: Surface parity.** ✅
`Excluded` on `UpdateConnectedSystemContainerRequest` and the read DTOs; `Set-JIMConnectedSystemContainer -Excluded` with Pester coverage; docs for both.

*Landed with Phase 4 rather than after it, per the surface-parity rule: portal-only editing would have left administrators unable to script the thing they most often script. Both surfaces refuse the contradiction with a 400, evaluated against the state the request would leave behind, so naming one half against a stored other is refused too and stating both halves is how a Container moves from a selection to an exclusion. This is the API-boundary rejection Phase 2 deferred to the phase that first exposes the field.*

**Phase 6: Change classification, consequences and preview.** ✅
An `"excluded"` key in `ConfigurationChangeConsequences` classified as a scope reduction (as the `"scope"` narrowing already is); the preview counts objects leaving scope through an exclusion; `SyncImportTaskProcessor`'s "Container Scope" unresolved-reference cause covers exclusions in its prose.

*The classification and consequences half landed with Phases 4 and 5, because shipping settable exclusions without it meant an administrator could carve out a branch and save in silence while narrowing a Container to One Level warned. It could not be done in isolation: the configuration snapshot captured selected Containers only, so an exclusion left no trace in the change history at all, and the same filter dropped any Container captured only as the route to a statement below it (losing a selection on a nested Container, and every re-inclusion inside an exclusion). Containers are now captured when they state something or hold something that does, with selection and exclusion both recorded by presence. A collection item can also carry a truer word than "Added" for what it did, because an exclusion arrives in the snapshot as a whole node exactly as a selection does, and the confirmation otherwise described a carve-out as an addition over prose about objects coming into scope.*

*The remaining three landed together, all of them about an exclusion being visible rather than silent.*

- ***The preview already counted objects leaving through an exclusion; nothing could ask it to.** `ConnectedSystemScope` has resolved exclusions since Phase 3, and the portal builds its proposal from the edited graph, so the portal could preview a carve-out from the day it could make one. The REST endpoint carried the stored exclusions forward with a comment saying Phase 5 would let callers propose them, and Phase 5 shipped the write surfaces without it. `ExcludedContainerIds` now exists on the request and on `New-JIMConfigurationChangePreview`, with the same omitted-means-unchanged and empty-means-lift semantics the selection lists carry, and a Container named in both lists is refused with a 400 rather than resolved. The adapter had one genuine gap the tests found: a proposal that brings scope in by **lifting** an exclusion moves no tick box, so `SelectsSomethingNew` read it as bringing nothing in and the "objects JIM has never imported cannot be counted" finding never fired.*
- ***The Activity's discard count needed an informational channel, and the stat counter table was already one.** `(ActivityId, Dimension, Key, Count)` is the shape, and its incremental upsert is what makes a paged import's per-page reports add up; a new `ExcludedContainer` dimension carries them, and the counts travel from the Connector on `ConnectedSystemImportResult` so they are a Connector contract rather than an LDAP detail. Two consequences worth recording. Finalisation could no longer wipe an Activity's counters wholesale before recomputing them, because a discarded entry produced no Run Profile Execution Item to recompute from; `RunProfileExecutionStatsDimensions.RecomputedFromExecutionItems` now names what finalisation owns, stated positively rather than as an exception at the delete. And the key is the Container's **id**: the key column holds 200 characters and Distinguished Names do not, and a count is a historical record that has to survive the Container being renamed, so the portal resolves the name at read time through a new Summary-tier projection and says plainly when the Container has since gone.*
- ***The unresolved-reference prose named one of the two ways an object leaves scope.** "Make sure Container Scope includes the location of the referenced object" is unhelpful advice when an exclusion is what took it out: the branch above the object is selected, so an administrator checks the tick box, finds it ticked, and has learnt nothing.*

*Runtime-verified against OpenLDAP on the sandbox light stack, not by unit tests alone: `ou=Corp` selected as a subtree with `ou=Service Accounts` excluded and `ou=App1` re-included beneath it discarded exactly one entry, attributed to the excluded Container, surviving the Activity's completion (which is what the finalisation carve-out exists for) and read back through `Get-JIMActivityStats`.*

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
