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

1. Run `jim-prd`; it prompts for a feature name and creates `docs/prd/PRD_YOUR_FEATURE_NAME.md` from the template
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

3. **Add the issue number back to the plan document header:**
   - After creating the issue, add an `Issue` line to the plan document header referencing the GitHub issue
   - Example: `- **Issue:** [#123](https://github.com/TetronIO/JIM/issues/123)`
   - This creates a two-way link: issue → plan doc, and plan doc → issue

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
| `docs/plans/` | `Planned` | Not yet started: design and future work |
| `docs/plans/doing/` | `Doing` | Partially implemented or actively being worked on |
| `docs/plans/done/` | `Done` | Fully implemented (or remaining items explicitly deferred/dropped) |

**Move plans between folders** as their status changes. Use `git mv` to preserve history.

**IMPORTANT: Update all cross-references when moving documents:**
When moving a document between `docs/plans/`, `docs/plans/doing/`, and `docs/plans/done/` (or anywhere in the `docs/` tree), you **MUST** search for and update all relative links that reference the moved file. This includes:
- Links **to** the moved file from other documents (the path has changed)
- Links **from** the moved file to other documents (the relative path depth has changed)

Use `grep` or equivalent to find all references to the filename before completing the move.

### Plans and specs never belong under `docs/`

The `docs/` directory is reserved for the customer-facing MkDocs site. Implementation plans, design specs, and any other internal engineering artefacts MUST live under `engineering/plans/` (or the appropriate `engineering/` subdirectory), never under `docs/`.

If a Claude Code plugin or agent skill (for example, the `superpowers` plugin's plan/spec generators) proposes writing to a path like `docs/superpowers/plans/…` or `docs/superpowers/specs/…`, **redirect the output to `engineering/plans/`** (or `engineering/plans/done/` if the plan is already implemented). Do not accept the plugin default.

Symptoms that indicate this has happened:
- A new `docs/superpowers/` (or any other top-level `docs/<tool-name>/`) directory appears
- `mkdocs build --strict` warns "The following pages exist in the docs directory, but are not included in the 'nav' configuration" for files under that directory
- Internal plan or spec content becomes publicly reachable at `https://docs.junctional.io/<tool-name>/...`

The fix is the same in each case: `git mv` the files into `engineering/plans/` (matching the `Status` of the work: top-level for planned, `doing/` for in-progress, `done/` for completed), remove the now-empty `docs/<tool-name>/` tree, and add the standard Status + Issue header to the plan document.

### Document Header

Every plan document must include a status line and an issue link near the top:

```markdown
- **Status:** Planned
- **Issue:** [#123](https://github.com/TetronIO/JIM/issues/123)
```

```markdown
- **Status:** Doing (Phases 1–3 complete)
- **Issue:** [#123](https://github.com/TetronIO/JIM/issues/123)
```

```markdown
- **Status:** Done
- **Issue:** [#123](https://github.com/TetronIO/JIM/issues/123)
```

Only use these three values. For done plans where some items were deferred, add a brief **Note** line below:

```markdown
- **Status:** Done
- **Note:** Phase 4 (OpenLDAP support) deferred; implement when OpenLDAP connector is needed.
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

**Document Versioning:**
Both documents include a `Document Version` field in their header. This version tracks the document content independently from the JIM product version and makes it easy to verify whether the latest version is deployed to external AI assistant platforms.

- Increment the version (e.g., `1.1` → `1.2`) whenever the document content is updated
- Update the `Last Updated` date at the same time
- The version allows quick comparison between the repository copy and deployed copies

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

---

## Changelog Maintenance

The project uses [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) format. Entries must be kept up to date as changes are made, not deferred until release time.

**Audience and tone:**
The changelog is a **customer-facing product document**. The audience is administrators and decision-makers wanting to know what's new. Entries should make JIM appear useful, reliable, sophisticated, and exciting; written with light product/marketing polish that conveys a capable team shipping a great product.
- Write entries as product changes, not developer notes: focus on the benefit or outcome, not the implementation detail
- Use a leading emoji per entry to add energy and visual scanning context (e.g. ✨ for new features, 🐛 for fixes, ⚡ for performance, 🔒 for security)
- Do NOT include: internal refactoring, test changes, developer tooling, CI/CD tweaks, or anything that has no user-facing impact
- Trivial changes (renamed a CSS class, moved a file, updated a comment) do NOT belong in the changelog

**Length and style — keep entries succinct:**
- **Target one sentence per entry; two at most.** If you cannot say it in two sentences, the entry is doing too much; trim or split it.
- A reader should be able to scan the entry in a few seconds and know whether it affects them.
- Lead with the customer-visible outcome ("Safari sign-in no longer fails with...", "The bundled X template now persists at production speed without..."), then optionally one short clause naming the underlying mechanism at a high level (e.g. "rewritten to use PostgreSQL `COPY` binary import"). Stop there.
- **Do NOT include in changelog entries** (these belong in commit messages, PR descriptions, or code comments — not the changelog):
  - Internal class, method, file, or property names (e.g. `SaveChanges`, EF change tracker, `SecurePolicy=SameAsRequest`)
  - Step-by-step explanations of what was wrong and how it was fixed
  - Browser/framework default behaviour explanations
  - Quantitative internals (parameter counts, SQL round-trip counts, exact byte sizes) unless the number itself is the customer-visible benefit
  - Multi-clause sentences chaining "previously X happened because Y, now Z does W because..."
- **Test before committing:** would an administrator skimming release notes care about this sentence? If the detail only matters to someone reading the diff, cut it.

**When to add an entry:**
- Add an entry under `## [Unreleased]` with each commit or PR that introduces user-facing changes
- This includes: new features, bug fixes, performance improvements, changed behaviour, and removed functionality

**When NOT to add an entry:**
- Documentation-only changes (`.md` files, `docs/` updates)
- CI/CD workflow changes (`.github/workflows/`)
- Development tooling changes (`.editorconfig`, devcontainer config)
- Refactoring with no user-facing impact
- Test-only changes
- Trivial UI tweaks with no meaningful user impact

**Categories (use as applicable):**
- **Added**: new features or capabilities
- **Changed**: modifications to existing behaviour
- **Fixed**: bug fixes
- **Performance**: optimisations and performance improvements
- **Removed**: removed features (use sparingly)

**Formatting conventions (match existing style):**
- Use `####` subheadings to group related entries under a larger feature (e.g., `#### Scheduler Service (#168)`)
- Reference GitHub issue numbers where applicable (e.g., `(#123)`)
- Keep entries concise: one line per change, describe what changed from the user's perspective
- Lead each entry with an appropriate emoji (✨ new, 🐛 fix, ⚡ performance, 🔄 changed, 🗑️ removed, 🔒 security, 📦 deployment/infrastructure, 🖥️ UI/UX)

**At release time:** Move all `[Unreleased]` entries to a new version section and update comparison links at the bottom of the file. See Release Process below.

---

## Release Process

**CRITICAL: NEVER modify the `VERSION` file without explicit user instruction to create a release.**

The `VERSION` file is the single source of truth for JIM's version number. It feeds into:
- All .NET assembly versions (via `Directory.Build.props`)
- Docker image tags (via release workflow)
- PowerShell module version (updated at release time)
- Diagram metadata (via `export-diagrams.js`)

**Versioning scheme:** [Semantic Versioning](https://semver.org/), `X.Y.Z` with optional prerelease suffix (e.g., `0.3.0-alpha`).

**What triggers a version bump:**
- New feature releases
- Breaking changes
- Significant bug fix batches
- User explicitly requesting a release

**What does NOT trigger a version bump:**
- Documentation changes
- Diagram regeneration
- CI/CD improvements
- Development tooling changes
- Refactoring without user-facing changes

**To create a release:** Use `/release <version>`; the skill handles VERSION, CHANGELOG, PowerShell manifest, documentation review, commit, tag, and push.

> **Full release process, air-gapped deployment, and Docker image details:** See `RELEASE_PROCESS.md`
