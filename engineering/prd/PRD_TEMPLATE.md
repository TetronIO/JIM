# [Feature Name]

<!--
  PRD Template — Product Requirements Document for JIM
  =====================================================
  This template defines WHAT to build and WHY. It is the input that a developer
  writes to communicate their requirements. Claude then uses it to generate an
  implementation plan (the HOW) in the standard docs/plans/ format.

  Workflow:
    1. Copy this template to a new file: docs/plans/YOUR_FEATURE_NAME.md
    2. Fill in all required sections (marked REQUIRED)
    3. Fill in optional sections where relevant
    4. Delete this comment block and any unused optional sections
    5. Ask Claude to create the implementation plan from this PRD

  Tips:
    - Be specific: "filter the activity list by outcome type" is better than
      "improve the activity page"
    - Include examples: show what the user sees, what the API returns, what
      the data looks like
    - State what you DON'T want: explicit scope exclusions prevent wasted work
    - Reference existing patterns: "like the existing CSO change tracking
      setting" tells Claude exactly where to look
-->

- **Status:** Planned
- **Created:** YYYY-MM-DD
- **Author:** [Name]
- **Issue:** #[number] *(create a GitHub issue and link it here)*

## Problem Statement
<!-- REQUIRED — What problem does this solve? Why does it matter? -->
<!-- Describe the current pain point or gap from the user/administrator perspective. -->



## Goals
<!-- REQUIRED — What are the measurable outcomes? -->
<!-- Use bullet points. Each goal should be verifiable ("we can confirm this works by..."). -->

-
-
-

## Non-Goals
<!-- REQUIRED — What is explicitly out of scope? -->
<!-- Being clear about what you're NOT building prevents scope creep and wasted effort. -->

-
-

## User Stories
<!-- REQUIRED — Who benefits and how? -->
<!-- Format: "As a [role], I want [action], so that [benefit]." -->
<!-- Include at least the primary user story. Add more if multiple roles are affected. -->

1. As a [role], I want [action], so that [benefit].

## Requirements

### Functional Requirements
<!-- REQUIRED — What must the feature do? -->
<!-- Numbered list. Be specific about behaviour, not implementation. -->
<!-- Example: "The activity list must show outcome chips per row" not "Add a MudChip component" -->

1.
2.
3.

### Non-Functional Requirements
<!-- OPTIONAL — Performance, security, accessibility, or operational constraints. -->
<!-- Include if the feature has specific targets (e.g., "must handle 100k objects without timeout"). -->

-

## Examples and Scenarios
<!-- REQUIRED — Concrete examples of how the feature behaves. -->
<!-- Show inputs, outputs, UI mockups (ASCII), API responses, data samples, etc. -->
<!-- The more concrete you are here, the better the implementation plan will be. -->

### Scenario 1: [Name]

**Given**: [precondition]
**When**: [action]
**Then**: [expected result]

### Scenario 2: [Name]

**Given**: [precondition]
**When**: [action]
**Then**: [expected result]

## Constraints
<!-- OPTIONAL — Technical, regulatory, or business constraints that must be respected. -->
<!-- Examples: "Must work in air-gapped environments", "Must not add new NuGet packages", -->
<!-- "Must be backward compatible with existing data", "Must use British English" -->

-

## Affected Areas
<!-- OPTIONAL but recommended — Which parts of the system does this touch? -->
<!-- Helps Claude scope the implementation plan correctly. -->
<!-- Reference specific layers, projects, or UI pages where relevant. -->

| Area | Impact |
|------|--------|
| Database | e.g., new table, migration, index |
| API | e.g., new endpoint, modified DTO |
| Worker | e.g., processor changes |
| Application | e.g., new server method |
| UI | e.g., new page, modified component |

## Dependencies
<!-- OPTIONAL — Other features, issues, or external factors this depends on. -->
<!-- Example: "Requires #288 (Sync Preview) to be completed first" -->

-

## Open Questions
<!-- OPTIONAL — Unresolved decisions or areas where you want Claude to propose options. -->
<!-- Example: "Should we use a tree view or indented list for the outcome display?" -->
<!-- Claude will address these in the implementation plan and may ask for clarification. -->

1.

## Acceptance Criteria
<!-- REQUIRED — How do we know this is done? -->
<!-- Checkboxes that can be ticked off during review. Should map back to the goals. -->

- [ ]
- [ ]
- [ ]

## Additional Context
<!-- OPTIONAL — Links, screenshots, related discussions, prior art, or anything else relevant. -->
<!-- Reference existing plan documents, GitHub issues, or documentation where helpful. -->

-
