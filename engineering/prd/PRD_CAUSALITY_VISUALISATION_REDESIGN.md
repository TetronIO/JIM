# Causality Visualisation Redesign

- **Status:** Planned
- **Created:** 2026-07-22
- **Author:** JayVDZ (drafted with Claude Code)
- **Issue:** [#1087](https://github.com/TetronIO/JIM/issues/1087)

## Problem Statement

The Causality Tree on the Run Profile Execution Item detail page is a strong concept let down by its presentation. Administrators use it to answer "what happened to this object during this run, and what did that cause?", but today:

- The tree leads with technical vocabulary (MVO, CSO, Projected, Joined) that average administrators do not speak, with no plain-language framing.
- There is no at-a-glance answer; the reader must walk the whole tree and mentally assemble the story.
- Attribute change tables are embedded mid-tree and look poor, with clunky dropdown filtering.
- The overall visual quality does not match the "top-shelf SaaS" bar the rest of JIM is being held to.

The two data-fidelity gaps that previously blocked a redesign have now shipped: Out of Scope outcomes carry the scoping Synchronisation Rule (#1085), and MVO Deleted outcomes carry the deleted Identity's details and deletion reasoning (#1086).

## Goals

- An administrator can understand what a Run Profile Execution Item did, and what it caused, from a single summary sentence without expanding anything.
- Every event reads in plain language first, with the technical term still visible for practitioners (and a toggle to swap the emphasis).
- Every entity mentioned (Connected System, record, Identity, Synchronisation Rule, Pending Export, deletion record) links to its detail page.
- Attribute change detail is scannable: operation badges, monospace values, one search box, count-annotated filter chips.
- The presentation matches the approved interactive mock-up (fonts excepted; JIM's existing fonts are retained) in both light and dark themes.
- Each user's preferred view (Flow, Timeline or Graph) is remembered across sessions.

## Non-Goals

- Renaming MVO/CSO terminology across the rest of JIM; that is the Record/Identity umbrella (#1088) and its waves (#1089 to #1092). This feature introduces the terminology only within the causality visualisation.
- No changes to code identifiers, database schema, or the outcome capture pipeline; #1085/#1086 completed the data work.
- No new third-party dependencies (no graph/visualisation libraries); CSS grid and hand-rolled SVG only.
- No changes to the Activities list pages or run summary pages beyond the Run Profile Execution Item detail view.
- No animation/physics-based graph interactions; the Graph view is a static layered layout.

## User Stories

1. As an administrator, I want a one-sentence summary of what happened to an object during a run, so that I can triage without decoding a tree of technical events.
2. As an administrator, I want plain-language event names with the technical term alongside, so that I can learn the underlying model as I read.
3. As an identity practitioner, I want the technical vocabulary still visible (and promotable via a toggle), so that precision is never lost.
4. As an administrator investigating a deletion, I want the deleted Identity named and linked to its deletion record, so that destructive outcomes are fully auditable.
5. As an administrator, I want to click any Connected System, record, Identity or Synchronisation Rule mentioned, so that I can pivot straight to its detail page.
6. As a user, I want my chosen view (Flow, Timeline, Graph) remembered, so that the page opens the way I like it.

## Requirements

### Functional Requirements

1. **Summary band** at the top of the causality panel:
   - A run context chip (Connected System + Run Profile name) and the execution timestamp.
   - One plain-English sentence derived from the outcome data describing what happened and what it caused, with entity names (Run Profile, Connected Systems, Identities, Synchronisation Rules) highlighted as token-style inline chips in the theme's primary colour.
   - A strip of outcome category pills (MudChips) beneath the sentence, colour-coded by tone: created/primary, flowed/secondary, queued/info, warning, destructive/error.
2. **Plain-language event naming**: every outcome type renders as "Plain label · Technical label" (e.g. "Identity created · MVO Projected", "Export queued · CSO Pending Export", "Left scope · Out of Scope"). A "Technical names" toggle swaps which of the two is emphasised. The mapping covers every `ActivityRunProfileExecutionItemSyncOutcomeType` value.
3. **Three toggleable views** of the same outcome tree:
   - **Flow**: three-column horizontal pipeline (Source, Identity, Downstream); downstream events grouped per Connected System; SVG elbow connectors drawn between columns showing fan-out.
   - **Timeline**: vertical narrative; each event is a sentence with entity chips as links; nesting via indented rails; attribute detail expands inline beneath its event.
   - **Graph**: node-link rendering of the raw outcome tree using a hand-rolled layered SVG layout (no library); tone-coloured accent per node; legend beneath.
   - The selected view persists per user via the user preferences feature and is restored on next visit.
4. **Attribute change detail** (drawer in Flow/Graph, inline in Timeline):
   - Each row: operation badge (Set/Add/Remove, colour-coded), attribute name with type and plurality demoted to a sub-line, monospace value column (previous value struck through where applicable).
   - Toolbar: one search box filtering on name and value, filter chips (All/Set/Add/Remove) annotated with counts, and an "n of m" indicator.
5. **Fidelity rendering**: Out of Scope events name and link the scoping Synchronisation Rule (from `SyncRuleId`/`SyncRuleName`); MVO Deleted events name the deleted Identity, show the deletion reasoning (`DetailMessage`), and link to the deletion record browser.
6. **Linking**: every entity mention is a link: Connected Systems, records (Connected System Objects), Identities (Metaverse Objects), Synchronisation Rules, Pending Exports, deletion records. Entities that no longer exist (deleted Identities) link to the durable record instead.
7. **Graceful degradation**: outcomes recorded before #1085/#1086 (null `SyncRuleName`, no deleted-Identity snapshot) render without the enrichment rather than erroring; the tracking-level setting (None/Standard/Detailed) continues to gate how much detail exists.

### Non-Functional Requirements

- Faithful to the approved mock-up's spacing, colour tones, card and pill treatments in both light and dark themes, mapped onto JIM's existing MudBlazor palettes; JIM's fonts are retained.
- Responsive: Flow view columns stack on narrow screens (connectors hidden); attribute rows wrap value onto a full-width line.
- Accessible: toggles and clickable cards keyboard-operable with visible focus rings; `prefers-reduced-motion` respected.
- No measurable regression in page load for large trees (hundreds of attribute changes); attribute tables render filtered client-side without server round-trips.

## Examples and Scenarios

The interactive mock-up (internal) is the canonical example set: https://claude.ai/code/artifact/c928e648-1fb1-4f39-961d-9c73c497dacb. It covers three scenarios in all three views, light and dark, with the technical-names toggle.

### Scenario 1: New joiner

**Given**: a Full Synchronisation on "Yellowstone APAC" processes a new record for Liam Allen
**When**: the administrator opens the Run Profile Execution Item detail page
**Then**: the summary reads "A **Full Synchronisation** on **Yellowstone APAC** processed the record for **Liam Allen (S8-287551)**: a new Identity was created, 11 attributes flowed to it, and an export of 11 changes is now queued for **Glitterband EMEA**." (bold = primary-colour highlight), with pills "Identity created", "11 attributes flowed", "Provisioned · 1 system", "Export queued · 11 changes"; the Flow view shows Source → Identity created → Attributes flowed → Provisioned → Export queued, grouped under Glitterband EMEA.

### Scenario 2: Leaver

**Given**: a record leaves the scope of Synchronisation Rule "Yellowstone People - Inbound" and its Identity's last authoritative source disconnects
**When**: the administrator opens the item
**Then**: "Left scope · Out of Scope" names and links the Synchronisation Rule; "Identity deleted · MVO Deleted" names the deleted Identity, carries a "Destructive" badge, shows the deletion reasoning, and links to the deletion record browser; downstream "Deprovision queued" events group per affected Connected System.

### Scenario 3: Export failure

**Given**: an Export run's write is rejected by the Connected System
**When**: the administrator opens the item
**Then**: "Export attempted" shows the 3 attempted changes; the child "Export failed" event carries a "Needs attention" badge, shows the connector error verbatim, and links to the queued changes.

## Constraints

- No new NuGet packages or JavaScript libraries; must work air-gapped.
- Both light and dark themes.
- British English throughout; no em dashes in UI text; JIM domain nouns Title Cased ("Synchronisation Rule" in full).
- Code identifiers (MVO/CSO class and property names) unchanged.
- Changelog entry and public docs update in the same PR (user-facing change).

## Affected Areas

| Area | Impact |
|------|--------|
| UI (JIM.Web) | Replace the causality tree component set on the Run Profile Execution Item detail page; new view components; new CSS |
| Application | New/extended user preference for causality view choice; summary-derivation helper |
| Models | Display-mapping helper for outcome types (plain labels, tones, icons); no schema changes |
| Database | None |
| Worker | None |

## Documentation Impact

| Doc | Change |
|------|--------|
| `docs/configuration/activities.md` | Rewrite the outcome tree section around the new summary band and three views |
| `CHANGELOG.md` | ✨ entry under `[Unreleased]` |

## Dependencies

- #1085 (Out of Scope Synchronisation Rule attribution) - **shipped** in PR #1098.
- #1086 (MVO Deleted Identity details) - **shipped** in PR #1098.
- User preferences feature (existing) for view persistence.

## Open Questions

1. Should the Graph view ship in the first PR, or land as a follow-up once Flow and Timeline have bedded in? The mock-up notes it adds the least over Flow for mostly-linear causality. (Plan proposes shipping all three, Graph last, so it can be dropped from scope without rework if needed.)

## Acceptance Criteria

- [ ] Summary band with run chip, highlighted-entity sentence and colour-coded outcome pills renders for all outcome shapes at Standard and Detailed tracking levels
- [ ] Every `ActivityRunProfileExecutionItemSyncOutcomeType` value has a plain-language label, tone and icon; technical names remain visible; the toggle swaps emphasis
- [ ] Flow, Timeline and Graph views render the same outcome tree; the choice is remembered per user across sessions
- [ ] Attribute detail shows Set/Add/Remove badges, demoted type/plurality, monospace values, count-annotated filter chips and search
- [ ] Out of Scope events name and link the scoping Synchronisation Rule; MVO Deleted events name the deleted Identity and link to its deletion record
- [ ] All entity mentions link to their detail pages; light and dark themes both faithful to the mock-up; no new dependencies
- [ ] Pre-#1085/#1086 outcome data renders without error or enrichment
- [ ] Changelog and `docs/` updated in the same PR

## Additional Context

- Approved mock-up (canonical design reference): https://claude.ai/code/artifact/c928e648-1fb1-4f39-961d-9c73c497dacb
- Data foundations: `engineering/plans/SYNC_RULE_CAUSALITY_TRACKING.md` (attribution design) and PR #1098
- Terminology umbrella (rest of JIM): #1088
