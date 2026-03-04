# Documentation Standards

> Guidelines for creating and maintaining JIM documentation.

## Feature Planning

JIM uses a two-document workflow for new features:

```
PRD (what & why)  -->  Implementation Plan (how)  -->  Code
   Developer writes      Claude generates             Claude implements
```

### Step 1: Write a PRD

A **Product Requirements Document** defines what to build and why. It is the input a developer writes to communicate requirements to Claude.

1. Run `jim-prd` — it prompts for a feature name and creates `docs/prd/PRD_YOUR_FEATURE_NAME.md` from the template
2. Fill in all **required** sections (Problem Statement, Goals, Non-Goals, User Stories, Functional Requirements, Examples, Acceptance Criteria)
3. Fill in optional sections where relevant
4. Delete the comment block and any unused optional sections
5. Create a GitHub issue linking to the PRD

### Step 2: Generate an Implementation Plan

Ask Claude to read the PRD and generate an implementation plan. Claude will:
- Research the codebase to understand affected areas
- Propose a phased implementation approach
- Identify files to modify, risks, and mitigations
- Add the plan sections below the PRD content (or in a separate document if the PRD is large)

The implementation plan follows the structure guidelines below.

### Step 3: Implement

Once the plan is approved, Claude implements phase by phase, building and testing as it goes.

### PRD Template

See [`docs/prd/PRD_TEMPLATE.md`](prd/PRD_TEMPLATE.md) for the full template with guidance comments.

**Required PRD sections:** Problem Statement, Goals, Non-Goals, User Stories, Functional Requirements, Examples and Scenarios, Acceptance Criteria.

**Optional PRD sections:** Non-Functional Requirements, Constraints, Affected Areas, Dependencies, Open Questions, Additional Context.

### Implementation Plan Structure

When creating the implementation plan (either appended to the PRD or as a separate document):

1. **Create a plan document in `docs/plans/`:**
   - Use uppercase filename with underscores: e.g., `PROGRESS_REPORTING.md`, `SCIM_SERVER_DESIGN.md`
   - Include comprehensive details: Overview, Architecture, Implementation Phases, Success Criteria, Benefits
   - Keep plan focused but detailed enough for implementation

2. **Create a GitHub issue:**
   - Brief description of the feature/change
   - Link to the plan document for full details
   - Assign to appropriate milestone
   - Add relevant labels (enhancement, bug, documentation, etc.)
   - Example: "See full implementation plan: [`docs/plans/PROGRESS_REPORTING.md`](plans/PROGRESS_REPORTING.md)"

3. **Plan structure guidelines:**
   - **Overview**: Brief summary of what and why
   - **Business Value**: Problem being solved and benefits
   - **Technical Architecture**: Current state, proposed solution, data flow
   - **Implementation Phases**: Numbered phases with specific deliverables
   - **Success Criteria**: Measurable outcomes
   - **Benefits**: Performance, UX, architecture improvements
   - **Dependencies**: External packages, services, infrastructure
   - **Risks & Mitigations**: Potential issues and solutions

---

## Plan Document Filing

Plan documents are filed in one of three locations based on their current state:

| Location | Status | Description |
|----------|--------|-------------|
| `docs/plans/` | `Planned` | Not yet started — design and future work |
| `docs/plans/doing/` | `Doing` | Partially implemented or actively being worked on |
| `docs/plans/done/` | `Done` | Fully implemented (or remaining items explicitly deferred/dropped) |

**Move plans between folders** as their status changes. Use `git mv` to preserve history.

**IMPORTANT — Update all cross-references when moving documents:**
When moving a document between `docs/plans/`, `docs/plans/doing/`, and `docs/plans/done/` (or anywhere in the `docs/` tree), you **MUST** search for and update all relative links that reference the moved file. This includes:
- Links **to** the moved file from other documents (the path has changed)
- Links **from** the moved file to other documents (the relative path depth has changed)

Use `grep` or equivalent to find all references to the filename before completing the move.

### Document Header

Every plan document must include a status line near the top:

```markdown
- **Status:** Planned
```

```markdown
- **Status:** Doing (Phases 1–3 complete)
```

```markdown
- **Status:** Done
```

Only use these three values. For done plans where some items were deferred, add a brief **Note** line below:

```markdown
- **Status:** Done
- **Note:** Phase 4 (OpenLDAP support) deferred — implement when OpenLDAP connector is needed.
```

### Phase Completion

Mark individual phase headings with `✅` when implemented:

```markdown
### Phase 1: Foundation ✅

### Phase 2: API Layer ✅

### Phase 3: UI (Future)
```

This makes it easy to see at a glance what has been done within a partially-implemented plan.

---

## Documentation Organisation

- `docs/prd/` - Product Requirements Documents (PRD template + feature PRDs)
- `docs/plans/` - New/planned feature plans and design documents
- `docs/plans/doing/` - Partially implemented or in-progress plans
- `docs/plans/done/` - Completed plans
- `docs/` - Active guides and references (current/completed work)
  - `COMPLIANCE_MAPPING.md` - Security framework and standards compliance mapping
  - `DATABASE_GUIDE.md` - PostgreSQL configuration, connection pooling, and backup/restore
  - `DEVELOPER_GUIDE.md` - Comprehensive development guide
  - `INTEGRATION_TESTING.md` - Integration testing guide
  - `plans/done/MVP_DEFINITION.md` - MVP completion record and future roadmap
  - `RELEASE_PROCESS.md` - Release and deployment procedures
  - `SSO_SETUP_GUIDE.md` - SSO configuration instructions

---

## AI Assistant Context Documents

JIM has context documents for use with AI assistant platforms (Claude Desktop, ChatGPT, etc.) for ideation and research:
- `docs/JIM_AI_ASSISTANT_INSTRUCTIONS.md` - System prompt/instructions to copy
- `docs/JIM_AI_ASSISTANT_CONTEXT.md` - Comprehensive context document to upload

**Keep these updated when:**
- Project status changes significantly (update Section 8 - Current Status)
- New connectors are added (update Section 4 - Connectors)
- Architecture changes materially (update Section 2 - Architecture)
- Key terminology or concepts change (update Section 11 - Glossary)

---

## Security and Compliance Documentation

JIM follows strict security development practices aligned with NCSC, CISA, OWASP ASVS, and UK Software Security Code of Practice standards. Full details are in:
- `docs/COMPLIANCE_MAPPING.md` - Complete security framework and standards mapping
- `docs/DEVELOPER_GUIDE.md` - Security development guidelines and patterns
