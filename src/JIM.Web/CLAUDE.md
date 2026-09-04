# JIM.Web UI Conventions

> Blazor/MudBlazor presentation rules for `src/JIM.Web`. These are JIM-specific conventions **beyond** standard MudBlazor practice. This file loads automatically (alongside `src/CLAUDE.md`) when working anywhere under `JIM.Web`.

Universal text rules (British English, no em dashes, copyright headers) and general C# conventions live in `src/CLAUDE.md` and the root `CLAUDE.md`. This file covers only the UI layer.

## Conventions hierarchy (read first)

Prefer enforcement over documentation. When a UI convention is repeated across pages, the right home for it is a **shared component or CSS class** that makes the wrong thing inexpressible, not a paragraph here that every author must remember. A convention only lives as prose in this file when it genuinely cannot be componentised (sizing defaults, spacing, comment style). If you find yourself copy-pasting the same markup onto a third page, extract a component into `JIM.Web/Shared/` and document it in the table below instead of adding another prose rule.

## Shared UI components (use these; do not hand-roll)

These components exist so a convention has a single source of truth. Prefer the component over copy-pasting markup; they live in `JIM.Web/Shared/` and are globally available (no `@using` needed).

| Component | Use for | See |
|-----------|---------|-----|
| `<TableDensityToggle @bind-Dense="_dense" />` | The compact/normal row toggle in a table's `ToolBarContent` | "Row density" below |
| `<EmptyValue />` | A table cell or inline value that is null/empty | "Empty values" below |
| `<WhitespaceValue Value="@x" />` | A value that is present but consists only of whitespace (the `<EmptyValue />` sibling) | "Empty values" below |
| `<TextValueDisplay Value="@x" />` | Any text attribute-value display: dispatches to `<EmptyValue />` / `<WhitespaceValue />` / the value | "Empty values" below |
| `<BooleanValue Value="@x" />` | Any Boolean attribute value: the tick or cross, and the word beside it | "Boolean values" below |
| `<PrefilledFormValidator />` | Inside any `MudForm` prefilled with an existing entity, so validity-gated buttons enable on load | "Form action gating" below |
| `<CollapsibleStackTrace StackTrace="@x" />` | Any place an error's stack trace is offered alongside its message | "Errors and stack traces" below |
| `<SearchField @bind-Value="_searchString" />` | Every box that filters a list, table or dialog as the user types | "Search and filter boxes" below |
| `<RunPhaseStepper Phases="@x" />` | The steps of a Run Profile execution on an Activity | `engineering/notes/RUN_PROFILE_PHASES.md` |
| `<RunProgressMetrics ObjectsProcessed="@x" ObjectsToProcess="@y" ... />` | A running Activity's progress bar and its count, rate and time remaining | "Live progress figures" below |
| `<TooltipText Text="@x" />` | A multi-sentence tooltip explanation, inside `TooltipContent` | "Tooltips" below |
| `<NavigableMudTabs>` | Top-level page tabs (syncs the active tab to `?t=slug`) | "Tabs" below |
| `<ActivityScheduleContext ScheduleExecutionId="@x" ScheduleStepIndex="@y" />` | Saying that a Schedule produced an Activity, and linking back to its Schedule Execution | "Activity Schedule context" below |
| `<ScopedHierarchyPicker Partition="@p" OnChanged="@h" />` | Choosing which Containers in a partition JIM manages, and each one's Container Scope | "Choosing Containers" below |
| `<AttributeChip Kind="@k" Name="@n" />` | Any attribute shown as belonging to a side of the Metaverse: the `CS` / `MV` / `Ex` avatar chip | "Attribute chips" below |
| `<ObjectChip Kind="@k" TypeName="@t" Name="@n" Href="@url" />` | Any **object** shown as belonging to a side of the Metaverse: the `CS` / `MV` avatar chip naming a Connected System Object or a Metaverse Object | "Object chips" below |
| `<TableObjectCount Count="@x" Total="@y" ... />` | The object count in a table toolbar's title slot | "Object counts in table toolbars" below |
| `<TableEmptyState PrimaryText="..." ... />` | A table or data grid's no-rows fragment | "Table empty states" below |
| `<VirtualisedDataGrid T="X" LoadWindow="..." ... />` | Every virtualised (infinite-scroll) list | "Virtualised lists" below |
| `<OneLineText Text="@x" Secondary="@y" />` | A cell's text (and the secondary text that would otherwise sit under it) kept to one line | "One line per row" below |
| `<OverflowList TItem="X" Items="@xs" ItemTemplate="..." Title="Roles" />` | A cell holding a list: the first item, then "+n more" | "One line per row" below |
| `<SyncRuleIdentityStrip Direction="@d" MetaverseObjectTypeName="@mv" ConnectedSystemObjectTypeName="@cs" ConnectedSystemId="@id" ConnectedSystemName="@n" />` | A Synchronisation Rule's Connected System, direction and object types, beneath the page's breadcrumbs | "Synchronisation Rule identity strip" below |

## Choosing Containers

**A control for picking Containers out of a hierarchy is `<ScopedHierarchyPicker />`.** It renders one partition's Containers with the filter, the selection count, the Clear action, the tick boxes and the Container Scope control, and edits the partition's graph in place, raising `OnChanged` so the host can refresh its summary, its pending-changes state and its stale-preview notice.

Four rules are baked into it rather than left to each call site, because each has a failure mode that is invisible until a customer hits it:

- **The Container's own name leads the row; its Distinguished Name is a tooltip.** Printing the full DN on every row restates the ancestry the indentation already draws and buries the one component that tells two Containers apart. (What made this possible is a Connector fix: discovered Containers used to be *named* after their whole DN on every directory except Active Directory.)
- **Rows are ~32px and there is a filter.** A directory with a couple of hundred OUs has to be navigable, not merely renderable.
- **A hierarchy above `AutoExpandThreshold` Containers opens only as far as its selections.** Small hierarchies still open fully, which is what they have always done; expanding a large one on arrival is a wall to scroll. Expansion is seeded once, so a branch the administrator collapses stays collapsed.
- **A Container that cannot be ticked says why** ("Covered by ou=People") rather than being greyed out with no explanation.

The selection rules themselves are **not** in the component: `ContainerSelectionEditor` (JIM.Utilities) owns the cascade down a branch, the roll-up to a parent, the partition auto-selection, the filter predicate and the coverage recalculation, and is unit-tested there. They decide what a Full Import returns, so they must be testable without rendering anything. Coverage in particular is `ConnectedSystemUtilities.ApplyContainerInclusion`'s, shared with the import's own search-root resolution, so the tree can never show one scope while the import performs another.

## Attribute chips (which side of the Metaverse an attribute is on)

**An attribute rendered anywhere near an Attribute Flow is an `<AttributeChip />`.** It carries the `CS` / `MV` / `Ex` avatar that says whether the attribute belongs to a Connected System, to the Metaverse, or is a computed value.

```razor
<AttributeChip Kind="AttributeChipKind.ConnectedSystem" Name="@attribute.Name" />
<AttributeChip Kind="AttributeChipKind.Metaverse" Name="@attribute.Name" Href="@url" />
<AttributeChip Kind="AttributeChipKind.Expression" Expression="@source.Expression" />
```

The marker is not decoration. Both sides of a flow are just names, and which side is the source swaps between Inbound and Outbound, so on any surface listing flows the avatar is the only thing distinguishing "reads `department` from the directory" from "writes `department` to it". `Kind` should be derived from the data (does the source carry a Metaverse Attribute id or a Connected System one?) rather than from the flow's direction, so a malformed row cannot be labelled as the side its direction implies.

- `Tooltip` defaults to naming the side; pass a richer one where the call site knows the attribute's data type and plurality.
- `Href` makes the chip a link. Expressions never take one; they have nowhere to point.
- Expression chips render the expression itself with syntax highlighting, not the word "Expression": the expression text is the only thing telling two computed sources apart.

**Known duplicate:** `SyncRuleAttributeFlowTab.razor` still hand-rolls this markup in eleven places, in two clusters that disagree with each other (one wraps the whole chip in a rich type/plurality tooltip, the other tooltips only the avatar with generic text). Migrating it to this component is worth doing, and needs the tooltip inconsistency resolved deliberately rather than folded into an unrelated change. Do not add a twelfth copy.

## Object chips (which side of the Metaverse an object is on)

**A reference to a Connected System Object or a Metaverse Object is an `<ObjectChip />`.** It is the object sibling of `<AttributeChip />` above: the `CS` / `MV` avatar, the object's type as an accented prefix, and its name or identifier.

```razor
<ObjectChip Kind="ObjectChipKind.ConnectedSystem" TypeName="@cso.Type.Name" Name="@externalId" Href="@url" />
<ObjectChip Kind="ObjectChipKind.Metaverse" TypeName="@mvo.Type.Name" Name="@displayName" Href="@url" Class="ma-0" />
```

- **`Href` makes it a link, and only a linked chip gets the hover treatment.** `jim-chip-link` goes on the link wrapper rather than the chip because the link is the hover target; the component owns that, so no call site places the class.
- **A chip with no `Name` renders the type without a trailing colon.** The colon joins the type to the identifier, so a record with no external ID yet (nothing exported) would otherwise trail punctuation pointing at nothing.
- **A chip with no `TypeName` renders the name alone.** The inverse: where the surrounding table already carries a Type column (a Configuration Change Preview's drill-down), the chip is the side marker and the link, and repeating the type on it would spend the row's one line saying it twice.
- **`Class` is the call site's, for the surrounding geometry only:** `ma-0` inside a detail table's cell, nothing in a stack of its own. Do not restyle the chip itself per call site.
- The avatar colours (`Color.Secondary` for CS, `Color.Primary` for MV) are load-bearing: the hover rule in `site.css` recolours `mud-avatar-filled-secondary` and `mud-avatar-filled-primary` by name, and both must stay in step or a badge stops responding to its own chip's hover.

**Why this is a component.** The markup was duplicated by hand across `ActivityRunProfileExecutionItemDetail` and `PendingExportDetail`, and that duplication is exactly what let a defect live: the avatar hover rule was written for the MV badge only, so every CS badge kept its resting colour when its chip filled. `PendingExportDetail` rendered both badges side by side and still nothing tied them together. It was also measurably wrong for the MV badge it did cover (1.2:1 to 1.7:1 against the fill); see the rule's comment in `site.css` for the palette measurements behind the treatment that replaced it.

## Synchronisation Rule identity strip

**A Synchronisation Rule's Connected System, direction and object types are stated once, by `<SyncRuleIdentityStrip />` beneath the page's breadcrumbs.** They are facts about the rule, not settings on it, so they do not belong among the Details tab's fields (where they used to sit between Description and the first switch), and they are context for Matching, Scope and Attribute Flow as much as for Details, so they belong where every tab can see them: the slot under the breadcrumbs that the Connected System page uses for its own strip.

- **The Metaverse type is always on the left and the Connected System type on the right; only the arrow turns.** The create form draws the pair the same way, and a rule that read one way while being created and another once saved would be worse than either. The arrowhead sits against the side that receives the data, drawn in CSS from the direction class (`jim-identity-flow-outbound` / `-inbound`), so the shape cannot disagree with the label. The row this replaced drew a fixed arrow, so every Inbound rule read as an export.
- The two type chips are `<ObjectChip />`s with no `Name`, which is the supported "type alone" case; do not hand-roll MV/CS avatars here.
- Nothing is shown on the New page: those facts are chosen in its form, exactly as `<AuditInfo />` is hidden until the rule exists.

## Form action gating and input immediacy

Three interaction rules that have repeatedly regressed (multiple times each on a single branch). Treat them as defaults for every form and dialog with inputs.

**1. Gate the action button on the mandatory fields; never validate-on-click only.** A primary action (Save / Add / Create / Update / Execute) MUST be `Disabled` until its mandatory inputs are present and valid. A handler that pops a `Snackbar` warning and `return`s (or worse, silently `return`s) when a required field is empty is **not** a substitute: the user can still click an obviously-incomplete form, and a silent return gives no feedback at all. Two ways, in order of preference:
- **Preferred (enforcement):** wrap the inputs in `<MudForm @bind-IsValid="_formValid">` and set `Disabled="@(!_formValid)"` on the submit button. The form derives validity from each field's `Required`/validation, so there is no separate rule to keep in sync. See `ConnectedSystemCreate.razor`, `ConnectedSystemDetailsTab.razor`.
  - **Prefilled edit forms MUST include `<PrefilledFormValidator />` inside the `MudForm`.** `MudForm.IsValid` starts `false` (its validity requires every `Required` control to have been *touched*, and its own first-render callback forces `IsValid` to `false` whenever a `Required` control exists), so a form prefilled with an existing, valid entity leaves its gated button disabled until the user pointlessly clicks in and out of a field. The shared component (`Shared/PrefilledFormValidator.razor`) receives the form via its cascading value and runs the initial validation at the right point in the form's own lifecycle, which also makes it work inside dialogs (dialog content renders through the dialog provider, so the opening component's `OnAfterRenderAsync` cannot see the form render). Do NOT hand-roll this with `@ref` + `OnAfterRenderAsync`; the parent's callback runs before `MudForm`'s and the result gets overwritten. Create forms start empty, so starting invalid is correct there; do not add this to them. See `ConnectedSystemDetailsTab.razor`, `ConnectedSystemRunProfilesTab.razor` (edit dialog).
- **When a MudForm does not fit** (inline editors, or non-field state such as "at least one day selected"): gate on a small predicate (`CanSave()` / `DisableXButton()`) that mirrors *exactly* the blocking checks in the handler, so the button and the handler cannot drift. See `ScheduleEditorDialog.CanSaveStep()`, `SyncRuleDetailScopingCriteriaGroup.DisableAddCriteriaButton()`.

**2. `Immediate="true"` on typed inputs that drive live UI.** `MudTextField` / `MudNumericField` commit their value on **blur** by default, so anything that reacts to the value (a gated button's `Disabled`, a live preview, inline `Required` validation) will not update until focus leaves the field. If the value drives live UI, set `Immediate="true"`. `MudSelect`, `MudCheckBox`, `MudRadioGroup`, `MudDatePicker` and `MudSwitch` commit on click and never need this. When the input lives inside a wrapper component (e.g. `ConnectedSystemSettingField`), the wrapper's `Immediate` parameter must be passed at **every** call site; a missed call site silently reverts that instance to blur-commit. Search and filter boxes are the one case where this rule is enforced rather than remembered; see the section below.

**3. A child value editor MUST notify its parent of edits.** A child component that mutates a by-reference model via `@bind` re-renders only itself; the parent's dependent UI (for example an Add button gated on that model) goes stale. Expose an `[Parameter] public EventCallback OnChanged`, raise it from each input via `@bind-Value:after`, and have the parent wire `OnChanged` to `StateHasChanged` (or its own handler). See `CriterionValueEditor.razor` and its hosts.

## Search and filter boxes

**A box that narrows a list as the user types is a `<SearchField />`.** Never hand-roll one from `MudTextField`: a bare `MudTextField` commits on blur, so the list sits unfiltered until focus leaves the box, and that has now regressed twice across the app while this was documented as prose alone (#864). The component bakes in `Immediate="true"`, a 300ms debounce, the magnifier adornment, the clear affordance and dense margin, so every search box behaves the same and no call site has to remember any of it.

```razor
@* Filters an in-memory list *@
<SearchField @bind-Value="_searchString" Class="mt-0" />

@* Handler reloads from the server or database: same component, longer debounce *@
<SearchField Value="@_filterInitiatedBy" ValueChanged="OnInitiatedByFilterChanged" DebounceInterval="500" />
```

- `Immediate` is deliberately **not** a parameter, and unmatched attributes are deliberately **not** splatted through, so a call site cannot reinstate blur-commit (the failure mode rule 2 above warns about for wrapper components). If a call site legitimately needs another `MudTextField` setting, add an explicit parameter to `SearchField` rather than reopening the splat.
- Raise `DebounceInterval` (to 500ms) where the change handler hits the server or database; leave it at the default where it filters an already-loaded list.
- Pass `Margin="Margin.None"` when the box sits in a form grid beside normal-density inputs; the default suits a table toolbar.

**Scope: this is about live filtering, not about the word "Search".** A field that is one criterion among several in a form the user submits with a button (Deleted Objects' query forms, the Logs filter behind **Refresh**) is not a search box; nothing filters as it is typed, so `Immediate` there changes nothing and `SearchField` would be the wrong component. Those are ordinary `MudTextField`s and carry a `@* search-convention: exempt - <why> *@` comment directly above, so the reason travels with the markup.

`SearchFieldConventionTests` (in `test/JIM.Web.Tests/`) sweeps every `.razor` file under `src/JIM.Web` and fails the build for a search-shaped `MudTextField` that is neither migrated nor exempted, so a new page cannot quietly reintroduce a blur-only box.

## Live progress figures

**A running Activity's numbers come from its counters, never from its message.** `Activity.Message` is narration: what the run is doing. The count, percentage, throughput and time remaining are derived from `ObjectsProcessed`/`ObjectsToProcess` and the `IActivityEtaTracker`, and `<RunProgressMetrics />` is the only thing that renders them.

The rule exists because the alternative shipped: the worker built progress messages that carried the count, a rate and a time remaining, the panel printed the count and percentage underneath, and the portal's own tracker printed the rate and time remaining again. Five facts appeared in nine places, and the two estimators disagreed on screen (148 obj/s beside 145 obj/s) because they sampled over different windows.

- Do not reintroduce numbers into a progress message on either side. A worker progress message that would only restate the counters should be `string.Empty`; the running step's name is the narration.
- The Activity's message belongs under the step it describes, inside `<RunProgressMetrics />`, not above the rail: context first, then the detail within it. A message that merely repeats a running step's name is suppressed there, so do not hand-roll that check at a call site.
- Two states have to say something rather than nothing, and both are easy to lose in a refactor: an unknown total (a paged import) reports what has been processed with no percentage or time remaining, and a counter that has reached its total while the step finishes reads "Finishing up". `RunProgressMetricsTests` pins both.
- PowerShell's `Get-JIMActivityProgressDisplay` is the sibling surface and follows the same rule; keep the two in step.
- **Every figure is scoped to the running step, and the readout must say so.** Each counting step resets `ObjectsToProcess`, and the ETA tracker discards its samples when that total changes, so the count, the percentage, the rate and the time remaining all describe one step. `<RunProgressMetrics />` names it ("Step 2 of 3: Processing Connected System Objects"), matching PowerShell's own phrasing. Naming rather than pointing ("the step running now") is deliberate: the stepper rail is `overflow-x: auto`, so on a long run in a narrow window the running step can be scrolled out of view while the readout is not.
- **Read a run's steps through `RunPhaseReading` (JIM.Models), never by filtering `ParentKey == null` at the call site.** Three surfaces have to agree on what "step 2 of 3" means: the progress API, the stepper and this readout. In particular a Connector's step is never the answer to "which step is running" for the figures, because the counters belong to the JIM step hosting it.

## Row density (compact-row toggle)

All data tables should let users switch between normal and compact row spacing, persisted globally so the choice follows the user across every table.

- Put `<TableDensityToggle @bind-Dense="_dense" />` as the **first** item in the table's `ToolBarContent`. If other controls sit to its left, follow it with a `<MudText Class="mx-2 mud-text-disabled">|</MudText>` separator.
- On the `MudTable` / `MudSimpleTable`: set `Dense="@_dense"`, and nothing else. The toggle only ever changes body-row spacing: `site.css` pins header cells to compact padding unconditionally (`.mud-table-root .mud-table-head .mud-table-cell`), so a header row is the same height in both states and no per-table class is involved. Do not make the `Class` attribute conditional on `_dense`; a ternary that varies the class list is a sign something is being styled that the density rule already owns. (A `dense-body-only` class was carried on fourteen tables for exactly this purpose and was never defined in any stylesheet; it was removed rather than implemented, because the global header rule already gives the intended result.)
- The page owns a `private bool _dense;` field and loads the saved preference on first render, so the table paints at the correct density immediately:
  ```csharp
  protected override async Task OnAfterRenderAsync(bool firstRender)
  {
      if (firstRender)
      {
          _dense = await PreferenceService.GetTableDenseAsync() == true;
          StateHasChanged();
      }
  }
  ```
  Inject `IUserPreferenceService PreferenceService`. No `try`/`catch` is needed here: `GetTableDenseAsync` swallows the "JS interop not ready" `InvalidOperationException` internally. Pages that gate their whole render on a `_preferencesLoaded` flag should load `_dense` alongside their other preferences inside that same gate (so there is no flash of normal-then-dense).
- `<TableDensityToggle>` owns the toolbar button and persists the toggle; do **not** add an `OnToggleDense` method or build the tooltip/icon button by hand.
- Default to normal spacing (`_dense = false`).

## Virtualised lists

**An infinite-scroll list is a `<VirtualisedDataGrid T="X">`; never hand-roll one around `MudDataGrid`'s `VirtualizeServerData`.** The component owns everything every virtualised list must get right and got wrong at least once while the pattern lived in a page: the URL round trip for search, sort and scroll position (`q`/`sort`/`desc`/`row`, written in place via `history.replaceState`, never `NavigationManager`); counting the match set once per filter change rather than once per scroll window; restoring a scroll position the virtualiser cannot reach yet; re-attaching the scroll listener when SPA navigation reuses the component; the density-dependent row height; the toolbar count; and the guarded empty-state slot. `Types/Index.razor` is the worked example.

**The grid sizes its own height; there is no height to pass.** `jimVirtualList.fit` measures where the page footer lands and gives the scroll container a height CEILING: a long list fills until the footer sits at the bottom of the viewport with the same breathing room below it as above it, on every list page, while a list of a few rows (or an empty one) collapses to its content like an ordinary table, the footer following it up the page. It re-applies on window resize and as late-loading content moves the footer, and a `site.css` fallback covers the first paint. Never reintroduce a per-page `Height="calc(100vh - Npx)"`: those constants were hand-tuned per page, went stale the moment the chrome above a grid changed, and are exactly what the measurement replaced.

A page supplies:

- **`LoadWindow`**: a `Func<VirtualisedWindowRequest, CancellationToken, Task<VirtualisedWindow<T>>>`. Two tiers, chosen by what the list can grow to:
  - **Unbounded data lists** (objects, activities, changes, pending deletions) call a repository **range read** (`offset`/`count`, not page/pageSize) that MUST honour `IncludeTotalCount` by skipping its count query and returning a null total when false; counting is the expensive half of a window read at scale. Null means "not counted", never zero. Pattern: `GetMetaverseObjectHeadersRangeAsync`.
  - **Configuration-sized lists** (Connectors, API Keys, Predefined Searches and friends) load the full list once, then filter and sort in the page and finish with `filtered.ToWindow(request)` (`VirtualisedWindowExtensions`); do not grow a database range read for a list that stays in the dozens. Pattern: `ConnectorList.razor`.
- **`Columns`**: `TemplateColumn`s, with `<VirtualisedSortHeader Title="..." />` in the `HeaderTemplate` for server-side sortable columns (the grid cascades itself as `IVirtualisedSortable`).
- **`EmptyContent`**: the context-aware empty states ("Table empty states" below); the fragment's context is the grid, so branch on `context.SearchText` and wire the action to `context.ClearSearchAsync()`.
- **`ContainerId`** (unique per grid) and, when two grids share one page, a **`UrlParameterPrefix`** each so their URL state cannot collide.
- **`StateKey`** (the route parameter) so a component instance reused across SPA navigations re-reads the URL and re-attaches its scroll tracking.

Page-owned filters (chips, presence deep links) call `RefreshAsync(invalidateTotals: true)` after changing what matches, plus `ResetScrollAsync()`; the old total describes a match set that no longer exists. Sorting deliberately does not invalidate the total.

**A conditional column is `Hidden`, never wrapped in an `@if`.** `MudDataGrid` orders its columns by the order they *register* with it (each `Column` adds itself on initialisation), not by their position in the markup. A column inside an `@if` whose condition is false on first render therefore registers after every column below it and appears at the far right when the condition later becomes true, however the markup reads. This is not hypothetical: the Connector Space's Secondary External Id column (derived from the first loaded window) and three of the Schema tab's attribute columns (derived from the selected Object Type, which changes under the reader) all rendered last. Write `Hidden="@(!_condition)"` on the column instead, so it registers in markup position and only its visibility changes. An `@if` remains correct where the condition is fixed for the component's lifetime (a Connector capability flag), because every column still registers on the first render.

## One line per row

**Every cell of a virtualised grid must render to exactly one line, in both densities.** The virtualiser positions rows arithmetically from a single fixed `ItemSize` (50px comfortable, 36px dense), so one taller row drifts the scroll position, the row index written to the URL and the reserved scroll space away from what is on screen, for every row below it. This is not a styling preference; it is what makes the grid able to place a row without having drawn the rows above it.

Three shapes break it, and each has one answer:

| Shape | Answer |
|-------|--------|
| A chip (or anything) per item from a `@foreach` | `<OverflowList>`: first item inline, "+n more" opens the whole set in a dialog |
| Two stacked block elements (`MudText` renders a `<p>`) | `<OneLineText Text Secondary>`: the secondary text reads inline after the value, low-lighted |
| Unbounded free text | `<OneLineText Text>`, or the `.jim-one-line` class where the content is markup |

- **Nothing may become unreachable.** What is clipped stays available on the element's `title`, in the `<OverflowList>` dialog, or on the detail page/panel the row already opens (the service log's Message column relies on all three: clipped, hoverable, and complete and copyable in the entry panel behind a row click).
- **`.jim-one-line` and `.jim-one-line-list` are the primitives** (`site.css`), and are used directly where a cell's content is markup rather than a string: a linkified description, an icon beside a name, a target chip followed by modifier chips. On `.jim-one-line-list`, only the child carrying `.jim-one-line-list-value` gives way; everything else on the row holds its size, so an affordance or a modifier chip can never be what a long value pushes out.
- **`<OneLineText>` uses the element's `title`, not a `MudTooltip`, and that is deliberate** rather than an oversight of the Tooltips rule below: a `MudTooltip` wraps its child in an inline-flex box of its own, which is exactly the box the ellipsis needs to be the block container, so the wrapper silently defeats the clipping it was added to explain. Do not "fix" it by migrating it.
- **Do not solve a too-tall row by raising `ItemSize`, turning virtualisation off, or dropping data.** The height is shared by every grid in the portal, and a cell's content is not the thing that should decide it.

## Object counts in table toolbars

A table toolbar has two anchored ends and nothing loose in between: **left** is how the table is displayed (the density toggle) followed by the page's own actions; **right** is the count followed by the search box. The count is always a `<TableObjectCount />`; the convention exists because two hand-rolled treatments had already diverged (a count baked into a title's parentheses on `PendingExportDetail`, a right-aligned "Showing x of y" on `PendingDeletionList`) before it was extracted.

The count is on the **right, immediately left of the search box**, for two reasons: "12 of 3,868" is feedback from that box, so it answers where the reader just typed; and the left slot then belongs to the primary action, which is what a page's Create button wants. It was briefly in the left slot instead, which pushed the action into the middle of the toolbar, where the loudest thing on the page was also the only one floating in open space. Where a table has a **title** and no search box (the child tables on detail pages), the count follows the title instead, since there is nothing on the right for it to answer to.

```razor
@* In a toolbar with a search box (every VirtualisedDataGrid): right side, nouns and all *@
<MudSpacer />
<TableObjectCount Count="@_totalItems" Total="@_unfilteredTotalItems"
                  SingularName="@type?.Name" PluralName="@type?.PluralName" Class="mr-3" />
<SearchField ... />

@* Beside a title: bare number, because the title already names the objects *@
<MudText Typo="Typo.h6">Attribute Changes</MudText>
<TableObjectCount Count="@_changeCount" Class="ml-2" />
```

- Pass the nouns **only when there is no title**; beside one, the bare number avoids restating it.
- Pass `Total` when the caller holds an unfiltered baseline; the text becomes "12 of 3,868 ..." while a filter narrows the list and collapses to the plain count when nothing is filtered.
- A null `Count` renders nothing (including its optional `ShowSeparator` separator, which the component owns so it cannot dangle): null is "not counted yet", and a zero in its place would read as an empty list.
- `<VirtualisedDataGrid>` builds this whole toolbar itself; a page only supplies its actions via `ToolBarExtras` (the grid adds the `|` separator after the density toggle when it has any) and never places the count or the search box by hand.

## Table empty states

A table's no-rows fragment renders a `<TableEmptyState />`, never a bare "No results" string: the message must say **why** the list is empty, because the situations mean different things and have different ways out. The Metaverse Object list (`Types/Index.razor`) is the worked example, distinguishing a search that matched nothing (with a "Clear Search" action button), a presence filter nothing satisfies, and a type with no objects at all.

- Only pass `ActionText` when the caller wires `OnAction`; a button with nothing behind it is a dead affordance.
- Inside a virtualised `MudDataGrid`, guard the fragment on a known-empty total (see the `NoRecordsContent` comment in `Types/Index.razor`): the grid renders the fragment between windows too, and an unguarded empty state flashes over data in flight.
- The block's centring and icon treatment live in `site.css` (`jim-table-empty-state`, `jim-table-empty-state-icon`); do not restyle per call site.

## Empty values

For a table cell (or inline value) that is null/empty, render `<EmptyValue />` (a low-lighted hyphen) rather than leaving the cell blank or hand-writing a dimmed span/`MudText`:

```razor
<MudTd DataLabel="Description">
    @if (string.IsNullOrEmpty(context.Description))
    {
        <EmptyValue />
    }
    else
    {
        @context.Description
    }
</MudTd>
```

- Only use it where the value can genuinely be empty (a nullable string, an optional date, etc.). Do **not** add a hyphen branch to columns that are always populated; that is dead code.
- `<EmptyValue />` renders inline. If a cell needs the placeholder centred to match the column's populated rows, wrap it: `<div class="d-flex justify-center align-center" style="height: 100%; width: 100%;"><EmptyValue /></div>`.

**Whitespace vs. empty (text attribute values).** A value can be *present but whitespace-only* (when a connected system imports whitespace and the mapping's "treat whitespace as no value" processing is off). Rendering it raw looks identical to no value, which is misleading. For any text **attribute-value** display, prefer `<TextValueDisplay Value="@x" />`: it renders `<EmptyValue />` for null/empty, `<WhitespaceValue />` (a low-lighted "(whitespace)" affordance with a tooltip visualising the characters) for whitespace-only, and the value itself otherwise. It is safe to pass a value that has already been formatted to a non-whitespace string for a non-text type (it simply renders unchanged), so string-returning value helpers can be wrapped directly: `<TextValueDisplay Value="@GetValueText(context)" />`. Use the bare `<EmptyValue />` for non-attribute fields (descriptions, names, etc.) where whitespace is not a meaningful distinction.

## Boolean values

**A Boolean attribute value is a `<BooleanValue Value="@x" />`:** the tick or cross, and "True" or "False" beside it. The label is load-bearing rather than decorative, which is why the component renders it rather than leaving it to the call site: a cross on its own reads almost identically to an attribute holding no value at all, and the Connector Space table said exactly that for as long as it rendered the icon alone.

The four surfaces showing Boolean attribute values (the Connector Space table, and the Metaverse Object detail's Form, Tabs and Table views) each carried their own copy of this markup, and the copies had already drifted: three rendered icon-plus-label at `Size.Small`, the fourth rendered a default-size icon and no label at all. The component is what stops them drifting again; `BooleanValueTests` pins the icons, the colours, the labels and the default icon size.

## Tooltips
- ALWAYS use `Arrow="true" Placement="Placement.Top"` on all `<MudTooltip>` components
- This ensures tooltips appear above the element with a downward-pointing arrow, consistent across the entire UI
- **Exception:** tooltips anchored to elements inside the mini-drawer (e.g. the `DrawerUserMenu` avatar when the drawer is collapsed) should use `Placement.Right` so they emerge into the main content area rather than overlapping the drawer itself. This exception is scoped to drawer-anchored tooltips only; do not extend it to other contexts.

### Tooltip text: one sentence per line

An explanatory tooltip that runs as one long line is hard to read and stretches across the page. Three rules, in the order you meet them:

1. **Write the explanation as sentences, not as one clause joined by "and".** Two facts get two sentences: "This object has been detected as deleted from the source system. It is pending removal during the next synchronisation." The same content joined with "and" is one sentence and renders as one unbroken line. This is the rule that actually decides the layout, and it lives in the string rather than in the component: `ExternalIdStatus.PendingRemoval` was the only one of three descriptions to render unbroken purely because it was written as a single clause while its two siblings were sentence pairs.
2. **Render it through `<TooltipText Text="@..." />`,** placed in `MudTooltip`'s `TooltipContent` fragment rather than its `Text` parameter. `Text` is encoded, so a `<br>` written into it renders literally. `TooltipText` splits on the sentence boundary and emits one line per sentence, passing each through Blazor's encoder rather than a `MarkupString`, so a description that ever interpolates a connected-system value cannot inject markup.
3. **Never hand-place the break.** No `<br>` and no line-break character written into a description string. Descriptions range from a few words to two sentences and get added over time, so a break authored for one string lands in the wrong place in the next and has to be re-judged every time one is added. Derive it or leave it.

A single-sentence description needs none of this and renders unchanged. The site-wide `24rem` measure cap and left-alignment (`site.css` > "Tooltip measure") is what keeps a long *single* sentence from running off the page; MudBlazor sets no `max-width` on `.mud-tooltip` at all.

## Alerts
- ALWAYS use `Variant="Variant.Outlined"` on all `<MudAlert>` components
- This ensures a consistent outlined style across the entire UI
- **A button placed inside an alert should carry `Color="Color.Inherit"`** unless it genuinely needs a colour of its own. `site.css` then paints it, and its icon, in the alert's severity colour, so the action reads as part of the message rather than as something dropped into it. This works for every severity and both themes; do not hand-pick a colour per call site. A button that names its own `Color` (the filled Primary/Warning/Info actions in the Schema, Partitions and Example Data alerts) is left exactly as specified.

## Custom CSS in `site.css` (look at the rendered page)

Three failure modes here are invisible to `dotnet build`, invisible to bUnit (which applies no stylesheet), and invisible in a screenshot unless you take one. All three shipped at once in the Set Password dialog's progress rail, which rendered as a row of bare floating icons because none of its four classes existed.

- **A class named in markup must exist in `site.css`.** A `.razor` file referencing `jim-whatever` compiles, renders, and silently lays out as an unstyled `<div>`. After adding any `jim-`-prefixed class, grep `site.css` for it. There is no test for this: a source sweep would have to separate `Class=` from `data-testid=` and enumerate the suffixes of interpolated modifiers (`jim-x--@State(y)`), which is more parsing than the defect is worth for one incident. If it regresses again, that sweep is the escalation (pattern: `SearchFieldConventionTests`).
- **A CSS custom property is scoped to whatever selector declares it.** `--jim-phase-marker-size` was declared on `.jim-phase-stepper-h`, so the second component to use those markers resolved it to nothing and every `width`/`height`/`calc()` depending on it collapsed. Tokens shared by more than one component belong on `:root`.
- **A JIM class alone frequently loses the cascade, from two directions.** MudBlazor's stylesheet is loaded after `site.css`, so it wins every tie on specificity: `.mud-input-control` carries a blanket `margin: 0`, and a bare `.jim-my-class { margin-top: ... }` on that element is silently discarded. Separately, `site.css` and the theme files carry many `html[lang] .mud-*` rules, most of them `!important` (alert tints, `.mud-alert-position` centring, dialog paper surfaces), which beat any `.jim-my-class.mud-*` selector on specificity even when yours is `!important` too. Qualify to win: `.jim-my-class.mud-input-control` for the MudBlazor-sheet case (as `.jim-interval-number.mud-input-control` already does), `html[lang] .jim-my-class...` for the JIM-rule case. This has now cost four rounds on one branch (a checkbox offset, an alert icon's alignment, an alert's background, and the dialog surface itself); **always confirm with `getComputedStyle` that the property actually took**, because a rule that lost this way looks exactly like a rule you got slightly wrong.

**`mud-text-secondary` is not the same as `color: var(--mud-palette-text-secondary)`.** `site.css` gives that class `opacity: 0.8 !important` on top of the colour, so the two routes to "secondary text" render as two different greys while reporting an identical computed `color`. Never mix them inside one block: a paragraph carrying the class above a list inheriting the colour looks like two deliberate styles. Pick one per block; setting the colour once on the container and letting its children inherit is usually cleanest, with the one emphasised line opting back out.

**Alignment and colour are measured, not eyeballed.** For "this control should line up with that text", read both bounding boxes off the rendered page and compare centres; a nudge that looks right in one screenshot is usually a few pixels out and will be sent back. For "these two lines look different", dump `fontSize`, `fontWeight`, `color` **and `opacity`** for each: colour alone would have missed the trap above.

## Errors and stack traces
- The **error message is the thing to read**; the stack trace is for the occasions it is not enough. Never render a stack trace unconditionally beside its message: it buries the sentence that actually answers the question, and stack traces routinely run to thousands of characters.
- Use `<CollapsibleStackTrace StackTrace="@x" />` wherever a trace is available. It renders nothing when there is no trace, shows a "Show stack trace" toggle when there is, and only puts the trace in the DOM once it has been asked for. Do not hand-roll the toggle, and do not wrap it in an expansion panel of its own; that is what it already is.

## Activity Schedule context

An Activity that a Schedule produced carries `ScheduleExecutionId` and `ScheduleStepIndex`; anywhere an Activity is presented, say so and link back to the Schedule Execution that produced it. Use `<ActivityScheduleContext />` rather than hand-rolling it: the duplicated part is the load-and-derive logic (look up the execution, guard the nulls, turn the 0-based step index into the 1-based "step 3 of 6" a person reads, build the href), and the two call sites want different visual treatments.

- It renders **nothing** when `ScheduleExecutionId` is null or the execution has since been pruned, so a call site can place it unconditionally without an `@if` of its own.
- `Compact="false"` (the default) is a page-width `MudPaper Outlined` panel headed "Part of a Schedule", built to match the detail page's own panels (`Typo.h5` heading, `pa-4`); `Compact="true"` is a panel section matching the sibling `MudPaper` sections of the Operations History side panel. It is deliberately **not** an alert: the context is another section of the page, not a notice interrupting it.
- **The page-width panel is a labelled multi-line field block, not one line of chips and links.** It uses the same `MudGrid Spacing="4"` / `MudItem xs="12" sm="6" md="4"` layout, uppercase `Typo.button` + `mud-text-secondary` labels and plain values as the Summary panel it sits directly beneath on `ActivityDetail`, so the two read as siblings rather than as two unrelated designs stacked on each other. The fields are **Schedule** (the name, linked to the Schedule Execution), **Step** ("3 of 6", 1-based, omitted entirely when the Activity carries no step index rather than rendered empty) and **Schedule Execution** (the run's status chip beside the "View Schedule Execution" link).
- **The status chip must stay labelled.** The Activity page shows two chips: the Summary panel's `ActivityStatus` and this panel's `ScheduleExecutionStatus`. They describe different objects, and unlabelled a reader cannot tell which is which; the "Schedule Execution:" label is the only thing telling a reader that this one is the whole run's outcome, not this Activity's. The two enums used to disagree on the word for success ("Complete" beside "Completed"), which read as an outright contradiction; the Schedule Execution vocabulary was aligned onto `Complete` in #1196 as a deliberate breaking API change (the REST API serialises enums by name, `JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false)` in `ApiJsonConfiguration.cs`, so the member name is the wire value). `ScheduleExecutionStatusWireContractTests` pins those names now; do not rename one again without the same deliberate decision, changelog entry and documentation sweep.
- The compact treatment keeps the single-sentence form ("Part of &lt;Schedule&gt;, step 3 of 6"); it has no heading of its own and sits in a narrow side panel, so a field grid would not fit.
- `Class` is the call site's to set, because only it knows the surrounding geometry. On `ActivityDetail` it sits directly below the Summary panel, so that is `mt-6` per the Panel spacing rules; inside the History panel's `gap-4` flex column it is nothing at all.
- The component guards its own lookup on the loaded execution id. Anything that polls (the History tab does) would otherwise query the database on every tick.
- Do **not** add it to `ActivityRunProfileExecutionItemDetail`: its subject is one object's per-item outcome, it is only ever reached from the Activity page whose panel already carries the context, and Schedule context two levels down is noise.

## Date and time display
- **Relative** ("2 hours ago"): `dateTime.ToRelativeTime()`, e.g. as the primary text under a tooltip
- **Full, human-friendly** ("12 Jul 2026 14:30:00"): `dateTime.ToFriendlyDate()` (both in `JIM.Web.Helpers`), e.g. as `MudTooltip` text revealing the precise value behind a relative-time display, or wherever a full timestamp needs to be shown. Never hand-roll a `.ToString("...")` format string for this; it duplicates a convention that already exists and drifts from it over time (this file's history: two competing inline formats had accumulated across six call sites before being consolidated back into `ToFriendlyDate()`).
- `ToFriendlyDate()` returns an unambiguous, culture-independent format (day-month-name-year, 24-hour clock with seconds); do not reintroduce culture-dependent short formats (`ToShortDateString()`/`ToShortTimeString()`) for this purpose.
- Both extension methods take a `DateTime`, not a `DateTimeOffset`; per the DateTime Handling rules in `src/CLAUDE.md`, call `.ToLocalTime()` first when the stored value is UTC and the display should be in the user's local time (the common case for tooltips over `Created`/`ChangeTime`-style fields).
- `ToShortDateString()` remains fine for a **date-only** value with no time component (e.g. `ExampleDataTemplateDetail.razor`'s Min/Max Date chips); `ToFriendlyDate()` is for full date **and** time.

## Panel spacing (target: uniform `mt-6` visual gaps between all block-level sections)
- Use `Class="pa-4 mt-6"` on `<MudPaper Outlined="true">` panels to ensure consistent vertical spacing between sections
- Exception: the **first** panel on a page should omit `mt-6` (use just `Class="pa-4"`) so there is no unnecessary top margin
- **After breadcrumbs, no intro text**: `MudBreadcrumbs` carries its own 16px bottom padding. If the first panel directly follows it with nothing in between, a bare `Class="pa-4"` (no margin) under-shoots the uniform gap (16px only); use `Class="pa-4 mt-2"` so the combined gap lands on the ~24px target, same reasoning as the "Tabs margin" rule below
- **After intro text**: `MudText` with `Typo.subtitle1` renders as a `<p>` with its own bottom margin (~16px). The first panel after intro text should use `mt-4` (not `mt-6`) so the combined gap matches `mt-6` visually
- **Tabs margin (breadcrumb-adjacent)**: `Class` on `NavigableMudTabs`/`MudTabs` **does** reach the root element (`MudTabs.TabsClassnames` includes `.AddClass(Class)`); pass it directly, never wrap in an extra `<div>`. `MudBreadcrumbs` carries its own 16px bottom padding, so when `NavigableMudTabs` directly follows a `MudBreadcrumbs` with nothing in between, use `Class="mt-2"` (not `mt-6`) so the combined gap lands on the uniform ~24px target, mirroring the "after intro text" `mt-4` rule above. Only reach for a full `mt-6` on `NavigableMudTabs` when it follows a plain block (e.g. a `MudPaper`) with no built-in padding of its own. See `ConnectedSystemDetail.razor`.
- **A notice sitting between the breadcrumbs and the tabs** (a page-level `MudAlert`, e.g. the configuration changed-since notice on `ConnectedSystemDetail.razor`) needs `Class="mt-2 mb-6"`. `mt-2` combines with the breadcrumbs' own 16px bottom padding for the 24px target above it; `mb-6` is needed below because adjacent block margins collapse to the larger of the two, and the tabs' `mt-2` alone leaves an 8px gap that reads as cramped next to every other section break on the page.
- **Tab content spacing**: Whether `TabPanelsClass` needs its own top spacing depends on the first tab's content. If the tab's content starts flush (e.g. a bare `MudPaper`/`MudText` with no top margin), use `TabPanelsClass="pt-5"`. If the content already supplies its own top margin (e.g. a table with `Class="mt-3"`), use `TabPanelsClass="pa-0"` and let the content's own margin stand; do not stack both, it double-counts.

## UI element sizing
- ALWAYS use normal/default sizes for ALL UI elements when adding new components
- Text: Use `Typo.body1` (default readable size)
- Chips: Use `Size.Medium` or omit Size parameter entirely (defaults to Medium)
- Buttons: Use `Size.Medium` or omit Size parameter entirely (defaults to Medium)
- Icons: Use `Size.Medium` or omit Size parameter entirely (defaults to Medium)
- Other MudBlazor components: Omit Size parameter to use default sizing
- Only use smaller sizes (`Typo.body2`, `Size.Small`, etc.) when explicitly requested by the user
- Users prefer readable, appropriately-sized UI elements by default

## Tabs
- Use `<NavigableMudTabs>` instead of `<MudTabs>` for all top-level page tabs; it syncs the active tab with a `?t=slug` query string, enabling browser back/forward navigation
- Use plain `<MudTabs>` only for tabs inside dialogs or nested sub-tabs where URL navigation is not needed
- **Do not pass the presentation parameters; the component's defaults are JIM's tab look.** `Elevation="0" Rounded="true" Outlined="true" ApplyEffectsToContainer="false"` are the defaults on `NavigableMudTabs` itself. MudTabs' own defaults render an unbordered, unrounded white band that reads on screen as a broken tab bar, and that is exactly what a page got by omitting them. No call site spells them out any more, so there is nothing to copy: a page that names one of the four is either opting out deliberately or restating a default. `NavigableMudTabsTests` pins the defaults and fails the build for any call site that passes `Outlined="false"` or `Rounded="false"`, so an opt-out has to be a deliberate, explained choice; `MvoDetailsTabs` is the worked example, naming only the `ApplyEffectsToContainer="true"` its tinted panel surface needs.
- Only `Class` and `TabPanelsClass` are routinely worth passing; see "Panel spacing" above for which values (`Class="mt-2"` after breadcrumbs, `TabPanelsClass="pt-5"` when the first tab's content starts flush)
- **A one-tab bar hides itself.** Where a page's tabs are conditional (a tab that appears only when there is something to put in it), `NavigableMudTabs` drops the bar and the panel's top padding once it sees a single panel, so the content lands where it would have with no tabs at all. Nothing to do at the call site; `HideBarWhenSingleTab="false"` opts out, for instance to stop a page's height jumping as a tab appears. Panels register during their own render, so this costs a second render pass and cannot be decided earlier.

## `@key` on loops whose contents can change

Any `@for`/`@foreach` rendering **components** whose set can change between renders MUST carry `@key` bound to something identifying the item (`@key="settingValue.Setting.Id"`), not the loop index. Without one, Blazor's diff matches children by position, so removing an item does not destroy its component: the instance is re-parameterised as its successor and keeps the internal state it built up. For a `MudTextField` inside a `MudForm` that state includes its validation result, and a stale "required" error on a field that no longer exists keeps `IsValid` false, disabling the form's submit button permanently.

That is exactly what `ConnectedSystemSettingsTab` did (found by driving the portal, not by any test): a connector whose settings are conditionally relevant via `RequiredWhenSetting`/`RequiredWhenValue` renders a different set of fields per drop-down value, so choosing any authentication method other than the default left the previous method's required-field error attached to whichever field took its place, and **Save Settings could never be enabled again**. Every field rendered correctly in isolation; the label said one setting and the error underneath named another.

- Applies to lists that are filtered, reordered, or conditionally rendered. A fixed list rendered in a fixed order does not need it, but adding it costs nothing.
- Key on a stable identity (a database id or a name), never the loop variable `i`; an index is the very thing positional matching already uses.
- Cover with a bUnit test that renders the parent, changes what the loop yields, re-renders, and asserts the vanished item's state is gone (`ConnectedSystemSettingsTabTests`). A per-component test cannot see this: the defect is in the parent's diffing, and the child is innocent.

## Razor comments
- **Section headers**: Use box-drawing delimiters: `@* ─── Section Title ─── *@` (U+2500 horizontal box-drawing character). One line, standing alone between markup blocks, to visually separate major page sections.
- **Inline comments**: Use plain comments: `@* Explanation of what follows *@`. Brief, contextual, placed immediately above or beside the relevant markup.
- Do NOT use multi-line banner comments (`===`, `amamam`, or similar filler characters). One line is enough.

## Nullable dereference in Razor
- When accessing a nullable `.Value` property in Razor markup (e.g. `context.LastUpdated.Value`), capture it into a local variable inside the `@if (x.HasValue)` block: `var lastUpdated = context.LastUpdated.Value;` then use the local variable in markup expressions.
- This is not just a style preference: CodeQL flags the bare dereference as "Dereferenced variable may be null" (`cs/dereferenced-value-may-be-null`), and unresolved findings block the merge. Pattern-matching guards (`is > 0`, `is not null`) do not satisfy the analyser any more than `HasValue` does, and the rule applies to every nullable value type: `int?`, `bool?` and friends need the local exactly as much as `DateTime?` (two findings on PR #1013 were an `int?` beside three correctly-captured `DateTime?` fields).
- Razor files are in scope of the pre-PR CodeQL shape sweep in `src/CLAUDE.md` (`git diff origin/main... -- '*.cs' '*.razor'`); do not treat markup as exempt from the shapes listed there.
