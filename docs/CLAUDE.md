# Documentation Standards

> Guidelines for creating and maintaining JIM documentation.

## Feature Planning

**When creating plans for new features or significant changes:**

1. **Create a plan document in `docs/plans/`:**
   - Use uppercase filename with underscores: e.g., `PROGRESS_REPORTING.md`, `SCIM_SERVER_DESIGN.md`
   - Include comprehensive details: Overview, Architecture, Implementation Phases, Success Criteria, Benefits
   - Mark status (Planned/In Progress/Completed)
   - Keep plan focused but detailed enough for implementation

2. **Create a GitHub issue:**
   - Brief description of the feature/change
   - Link to the plan document in `docs/plans/` for full details
   - Assign to appropriate milestone
   - Add relevant labels (enhancement, bug, documentation, etc.)
   - Example: "See full implementation plan: [`docs/plans/PROGRESS_REPORTING.md`](docs/plans/PROGRESS_REPORTING.md)"

3. **Plan structure guidelines:**
   - **Overview**: Brief summary of what and why
   - **Business Value**: Problem being solved and benefits
   - **Technical Architecture**: Current state, proposed solution, data flow
   - **Implementation Phases**: Numbered phases with specific deliverables
   - **Success Criteria**: Measurable outcomes
   - **Benefits**: Performance, UX, architecture improvements
   - **Dependencies**: External packages, services, infrastructure
   - **Risks & Mitigations**: Potential issues and solutions

## Documentation Organisation

- `docs/plans/` - Feature plans and design documents (future work)
- `docs/` - Active guides and references (current/completed work)
  - COMPLIANCE_MAPPING.md - Security framework and standards compliance mapping
  - DEVELOPER_GUIDE.md - Comprehensive development guide
  - INTEGRATION_TESTING.md - Integration testing guide
  - MVP_DEFINITION.md - MVP completion record and future roadmap
  - RELEASE_PROCESS.md - Release and deployment procedures
  - SSO_SETUP_GUIDE.md - SSO configuration instructions

## AI Assistant Context Documents

JIM has context documents for use with AI assistant platforms (Claude Desktop, ChatGPT, etc.) for ideation and research:
- `docs/JIM_AI_ASSISTANT_INSTRUCTIONS.md` - System prompt/instructions to copy
- `docs/JIM_AI_ASSISTANT_CONTEXT.md` - Comprehensive context document to upload

**Keep these updated when:**
- Project status changes significantly (update Section 8 - Current Status)
- New connectors are added (update Section 4 - Connectors)
- Architecture changes materially (update Section 2 - Architecture)
- Key terminology or concepts change (update Section 11 - Glossary)

## Security and Compliance Documentation

JIM follows strict security development practices aligned with NCSC, CISA, OWASP ASVS, and UK Software Security Code of Practice standards. Full details are in:
- `docs/COMPLIANCE_MAPPING.md` - Complete security framework and standards mapping
- `docs/DEVELOPER_GUIDE.md` - Security development guidelines and patterns
