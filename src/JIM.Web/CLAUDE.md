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
| `<SearchField @bind-Value="_x" />` | Any list, table or dialog search/filter box (search-as-you-type, debounced). Use `Value` + `ValueChanged` instead of `@bind-Value` when the handler drives a server reload | "Form action gating and input immediacy" below |

## Form action gating and input immediacy

Three interaction rules that have repeatedly regressed (multiple times each on a single branch). Treat them as defaults for every form and dialog with inputs.

**1. Gate the action button on the mandatory fields; never validate-on-click only.** A primary action (Save / Add / Create / Update / Execute) MUST be `Disabled` until its mandatory inputs are present and valid. A handler that pops a `Snackbar` warning and `return`s (or worse, silently `return`s) when a required field is empty is **not** a substitute: the user can still click an obviously-incomplete form, and a silent return gives no feedback at all. Two ways, in order of preference:
- **Preferred (enforcement):** wrap the inputs in `<MudForm @bind-IsValid="_formValid">` and set `Disabled="@(!_formValid)"` on the submit button. The form derives validity from each field's `Required`/validation, so there is no separate rule to keep in sync. See `ConnectedSystemCreate.razor`, `ConnectedSystemDetailsTab.razor`.
- **When a MudForm does not fit** (inline editors, or non-field state such as "at least one day selected"): gate on a small predicate (`CanSave()` / `DisableXButton()`) that mirrors *exactly* the blocking checks in the handler, so the button and the handler cannot drift. See `ScheduleEditorDialog.CanSaveStep()`, `SyncRuleDetailScopingCriteriaGroup.DisableAddCriteriaButton()`.

**2. `Immediate="true"` on typed inputs that drive live UI.** `MudTextField` / `MudNumericField` commit their value on **blur** by default, so anything that reacts to the value (a gated button's `Disabled`, a live preview, inline `Required` validation) will not update until focus leaves the field. If the value drives live UI, set `Immediate="true"`. For search/filter-as-you-type, add `DebounceInterval="300"` so it does not fire every keystroke. `MudSelect`, `MudCheckBox`, `MudRadioGroup`, `MudDatePicker` and `MudSwitch` commit on click and never need this.

**All admin/list/dialog search and filter boxes MUST use the shared `<SearchField>` component** (see the shared-components table above); do not hand-roll one. `<SearchField>` bakes in `Immediate="true"`, the `DebounceInterval` (300ms default; raise it for server or database backed searches), the search adornment icon, dense margin and a clear affordance, so search boxes filter as you type by default. A bare `MudTextField` search box, which commits only on blur, is **not acceptable**. Componentising the pattern is what stops new search boxes regressing to blur-only. The only sanctioned exceptions are multi-criteria forms behind an explicit **Search** button and the log filter behind an explicit **Refresh** (both deliberately not search-as-you-type, because each keystroke would fire an expensive server query); those keep a plain `MudTextField`.

**3. A child value editor MUST notify its parent of edits.** A child component that mutates a by-reference model via `@bind` re-renders only itself; the parent's dependent UI (for example an Add button gated on that model) goes stale. Expose an `[Parameter] public EventCallback OnChanged`, raise it from each input via `@bind-Value:after`, and have the parent wire `OnChanged` to `StateHasChanged` (or its own handler). See `CriterionValueEditor.razor` and its hosts.

## Row density (compact-row toggle)

All data tables should let users switch between normal and compact row spacing, persisted globally so the choice follows the user across every table.

- Put `<TableDensityToggle @bind-Dense="_dense" />` as the **first** item in the table's `ToolBarContent`. If other controls sit to its left, follow it with a `<MudText Class="mx-2 mud-text-disabled">|</MudText>` separator.
- On the `MudTable` / `MudSimpleTable`: set `Dense="@_dense"` and add the `dense-body-only` class, e.g. `Class="@(_dense ? "mt-5 mb-5 dense-body-only" : "mt-5 mb-5")"`. The `dense-body-only` class keeps header rows at normal height while compacting body rows.
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

## Tooltips
- ALWAYS use `Arrow="true" Placement="Placement.Top"` on all `<MudTooltip>` components
- This ensures tooltips appear above the element with a downward-pointing arrow, consistent across the entire UI
- **Exception:** tooltips anchored to elements inside the mini-drawer (e.g. the `DrawerUserMenu` avatar when the drawer is collapsed) should use `Placement.Right` so they emerge into the main content area rather than overlapping the drawer itself. This exception is scoped to drawer-anchored tooltips only; do not extend it to other contexts.

## Alerts
- ALWAYS use `Variant="Variant.Outlined"` on all `<MudAlert>` components
- This ensures a consistent outlined style across the entire UI

## Panel spacing (target: uniform `mt-6` visual gaps between all block-level sections)
- Use `Class="pa-4 mt-6"` on `<MudPaper Outlined="true">` panels to ensure consistent vertical spacing between sections
- Exception: the **first** panel on a page should omit `mt-6` (use just `Class="pa-4"`) so there is no unnecessary top margin
- **After intro text**: `MudText` with `Typo.subtitle1` renders as a `<p>` with its own bottom margin (~16px). The first panel after intro text should use `mt-4` (not `mt-6`) so the combined gap matches `mt-6` visually
- **Tab content spacing**: Use `TabPanelsClass="pa-0 mt-6"` on `NavigableMudTabs` / `MudTabs` so the gap between tab headers and tab panel content matches the surrounding spacing
- **Tabs margin**: `NavigableMudTabs` may not honour `Class` for outer margin; wrap in `<div class="mt-6">` to guarantee spacing above the tab bar

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

## Razor comments
- **Section headers**: Use box-drawing delimiters: `@* ─── Section Title ─── *@` (U+2500 horizontal box-drawing character). One line, standing alone between markup blocks, to visually separate major page sections.
- **Inline comments**: Use plain comments: `@* Explanation of what follows *@`. Brief, contextual, placed immediately above or beside the relevant markup.
- Do NOT use multi-line banner comments (`===`, `amamam`, or similar filler characters). One line is enough.

## Nullable dereference in Razor
- When accessing a nullable `.Value` property in Razor markup (e.g. `context.LastUpdated.Value`), capture it into a local variable inside the `@if (x.HasValue)` block: `var lastUpdated = context.LastUpdated.Value;` then use the local variable in markup expressions. This avoids repeated nullable dereference warnings from code analysis.
