# JIM Development Quick Reference

> Identity Management System - .NET 9.0, EF Core, PostgreSQL, Blazor

## Context7 MCP

Always use Context7 MCP when you need library/API documentation, code generation, setup or configuration steps without the user having to explicitly ask. The MudBlazor library ID is `/mudblazor/mudblazor`.

## CRITICAL REQUIREMENTS

**YOU MUST BUILD AND TEST BEFORE EVERY COMMIT AND PR (for .NET code):**

1. **ALWAYS** build and test before committing - zero errors and warnings required
2. **Prefer targeted builds**: Build only affected projects and their dependents, not the full solution. For example, if you changed `JIM.Connectors` and `JIM.Worker.Tests`, run `dotnet build test/JIM.Worker.Tests/` (which transitively builds its dependencies). Only use `dotnet build JIM.sln` for the **final pre-PR check** or when changes span many projects.
3. **Prefer targeted tests**: Run only the test projects that cover your changes. For example, `dotnet test test/JIM.Worker.Tests/` instead of `dotnet test JIM.sln`. Only run `dotnet test JIM.sln` for the **final pre-PR check**.
4. **NEVER** commit code that hasn't been built and tested locally
5. **NEVER** create a PR without verifying **full solution** build and tests pass
6. **NEVER** assume tests will pass without running them

**EXCEPTIONS - changes that do NOT require dotnet build/test:**
- **Scripts** (.ps1, .sh, etc.) - do not affect compiled code
- **Static assets** (CSS, JS, images) - served directly without compilation
- **Documentation** (`.md` files, `docs/` changes) - no compiled code
- **Configuration files** (`.env.example`, `docker-compose.yml`, `Dockerfile`, `.gitignore`, `.editorconfig`, etc.) - not compiled
- **CI/CD workflows** (`.github/workflows/`) - run remotely, not compiled locally
- **Diagrams** (`workspace.dsl`, exported SVGs) - non-code assets
- **Plan documents** (`docs/plans/`) - documentation only

**Partial exception:**
- **UI-only changes** (Blazor pages, Razor components) require `dotnet build` but do NOT require `dotnet test` - there are no UI tests, so running tests just wastes time

**YOU MUST FOLLOW TEST-DRIVEN DEVELOPMENT (TDD):**

JIM uses TDD as the standard development workflow. Tests are written **before** implementation — not after.

**TDD Workflow (Red → Green → Refactor):**
1. **Write the test first** — write a failing test that describes the expected behaviour
2. **Confirm it fails (Red)** — run the test and verify it fails for the right reason (not just a compile error)
3. **Implement the fix or feature (Green)** — write the minimum code to make the test pass
4. **Verify it passes** — run the test again and confirm it is green
5. **Refactor if needed** — clean up without breaking the test

**For bug fixes specifically:**
1. Write a test that **reproduces the bug** (it must fail before your fix)
2. Implement the fix
3. Run the test — it must now pass
4. This proves the fix is correct and guards against regression

**Rules:**
1. **ALWAYS** write the test before implementing new classes, methods, or logic
2. **ALWAYS** write a failing test before fixing a bug — the test must fail first to be meaningful
3. **ALWAYS** write tests for new API endpoints, extension methods, and utilities
4. **NEVER** implement a fix and then retrofit a test — that is not TDD
5. **NEVER** commit new functionality without corresponding tests
6. Tests should cover: happy path, edge cases, error conditions

**YOU MUST ASK BEFORE IMPLEMENTING SIGNIFICANT CHANGES:**

1. **ALWAYS** ask the user before implementing new features or significant functionality
2. **ALWAYS** confirm the approach when multiple implementation options exist
3. **ALWAYS** clarify requirements when there is ambiguity about what the user wants
4. **NEVER** assume you know what the user wants for non-trivial changes
5. **NEVER** implement architectural decisions without user confirmation
6. Present options with trade-offs when relevant, and let the user decide

**COMMIT WITHOUT ASKING:**

1. When the user says "yes" to committing, just commit - don't ask for confirmation
2. When work is complete and tests pass, commit automatically if the user has indicated approval
3. Don't ask "Would you like me to commit?" - if the context is clear, just do it

**YOU MUST PLAN BEFORE BUILDING:**

1. **ALWAYS** enter plan mode for any non-trivial task (3+ steps or architectural decisions)
2. Use plan mode for verification steps, not just building — plan how you will prove it works
3. **If you've spent more than 2–3 attempts fixing an issue without progress, STOP.** Do not keep pushing down the same path. Step back, re-enter plan mode, review the problem from scratch, and consider whether you are missing something or there is a better approach. If still stuck, ask the user — they have deep developer experience and can often see what you are missing.
4. **NEVER** mark a task complete without proving it works (build passes, tests pass, behaviour verified)
5. When relevant, diff behaviour between `main` and your changes to confirm correctness

**YOU MUST DEMAND QUALITY:**

1. **Simplicity First**: Make every change as simple as possible. Impact minimal code.
2. **Find Root Causes**: No temporary fixes, no workarounds, no band-aids. Diagnose the actual problem and fix it properly. Senior developer standards.
3. For non-trivial changes, pause and ask: "Is there a more elegant way to do this?"
4. If a fix feels hacky, step back and implement the clean solution — knowing everything you now know
5. Skip this for simple, obvious fixes — don't over-engineer
6. Before presenting work, ask yourself: "Would a staff engineer approve this?"

**Test project locations:**
- API tests: `test/JIM.Web.Api.Tests/`
- Model tests: `test/JIM.Models.Tests/`
- Worker/business logic tests: `test/JIM.Worker.Tests/`

**Failure to build and test wastes CI/CD resources and delays the project.**

If you cannot build/test locally due to environment constraints, you MUST:
- Clearly state this limitation in the PR description
- Mark the PR as draft
- Request manual review and testing before merge

## Subagent Strategy

- Use subagents liberally to keep the main context window clean
- Offload research, exploration, and parallel analysis to subagents
- For complex problems, throw more compute at it — use multiple subagents in parallel
- One task per subagent for focused execution

## Synchronisation Integrity

**SYNCHRONISATION INTEGRITY IS CRITICAL - PRIORITISE IT ABOVE ALL ELSE.**

Sync operations are the core of JIM. Customers depend on JIM to synchronise their identity data accurately without corruption or data loss.

**Error Handling Philosophy:**
1. **Fast/Hard Failures**: Better to stop and report an error than continue with corrupted state
2. **Comprehensive Reporting**: ALL errors must be reported via RPEIs/Activities - no silent failures
3. **Defensive Programming**: Anticipate edge cases (duplicates, missing data, type mismatches) and handle explicitly
4. **Trust and Confidence**: Customers must be able to trust that JIM won't silently corrupt their data

**Key Rules:**
- NEVER use `First()`/`FirstOrDefault()` when expecting exactly one result - handle multiplicity explicitly
- All sync operation code MUST be wrapped in try-catch with RPEI error logging
- Activities with any UnhandledError RPEI items should fail the entire activity
- Log summary statistics at the end of every batch operation
- Never silently skip objects due to exceptions

> **Full detailed requirements (7 sections):** See `src/JIM.Application/CLAUDE.md`

## Scripting

**IMPORTANT: Use PowerShell for ALL scripts:**
- All automation, integration tests, and utility scripts MUST be written in PowerShell (.ps1)
- PowerShell is cross-platform and works on Linux, macOS, and Windows
- Exception: `.devcontainer/setup.sh` is acceptable as it runs during container creation
- Never create bash/shell scripts for project automation or testing

## Commands & Environment

> **Full command reference, shell aliases, Docker workflows, dependency policy, environment setup, and troubleshooting:** See `.devcontainer/CLAUDE.md`

**Quick reference:**
- `jim-compile` / `jim-test` / `jim-test-all` - Build and test
- `jim-db` / `jim-stack` - Start database / full Docker stack
- `jim-build-web` / `jim-build-worker` / `jim-build-scheduler` - Rebuild containers after code changes

## ASCII Diagrams

When creating ASCII diagrams in documentation or code comments, use only reliably monospaced characters:

| Use         | Instead of    | Purpose                        |
|-------------|---------------|--------------------------------|
| `->` `-->`  | `->` `-->` `>` | Arrows (horizontal)           |
| `<-` `<--`  | special chars | Arrows (reverse)               |
| `v` / `^`   | `v` `^`       | Arrows (vertical)             |
| `+`         | box-drawing chars | Corners and junctions      |
| `-` / `|`   | `=` special chars | Lines (horizontal/vertical)|
| `-`         | bullets       | Bullet points in diagrams      |

## Code Style & Conventions

**Key rules (always apply):**
- Use async/await for all I/O operations (method suffix: `Async`)
- Use constructor injection for all dependencies
- **CRITICAL: Use British English (en-GB) for ALL text** — "authorisation", "synchronisation", "behaviour", "colour", etc.
- Use `DateTime.UtcNow` — NEVER `DateTime.Now`
- One class per file, file names match class names exactly
- All models/POCOs live in `src/JIM.Models/` — never inline in service files

> **Full conventions (DateTime handling, raw SQL parameters, file organisation, naming patterns, UI sizing, MudBlazor tabs):** See `src/CLAUDE.md`

## Testing

**Before Committing .NET Code (MANDATORY):**
- Build and test affected projects - must complete with zero errors
- During development, prefer targeted builds: `dotnet build test/JIM.Worker.Tests/` (builds dependencies transitively)
- For final pre-PR check: `dotnet build JIM.sln` and `dotnet test JIM.sln` (full solution)
- **DO NOT proceed to commit if any tests fail or build has errors**
- See **Exceptions** under Critical Requirements for changes that do not require build/test

**Test Basics:**
- NUnit with `[Test]` attribute, `Assert.That()` syntax, Moq for mocking
- Test naming: `MethodName_Scenario_ExpectedResult`
- EF Core in-memory database auto-tracks navigation properties - this MASKS missing `.Include()` bugs. Run integration tests when modifying repository queries.

> **Full testing patterns, debugging tips, test data generation, and integration testing:** See `test/CLAUDE.md`

## Design Principles

**Minimise Environment Variables:**
- Prefer configuration through admin UI and guided setup wizards over environment variables
- Environment variables should be a fallback for container/automated deployments, not the primary configuration method
- Settings that administrators might need to change should be configurable through the web interface
- Only use environment variables for:
  - Bootstrap configuration (database connection for initial setup)
  - Secrets that cannot be stored in the database (encryption keys before encryption is configured)
  - Container orchestration overrides

**Self-Contained & Air-Gapped Deployable:**
- JIM must work in air-gapped environments with no internet connectivity
- No cloud service dependencies (no Azure Key Vault, AWS KMS, etc.)
- All features must work with on-premises infrastructure only

**No Third-Party Product References:**
- NEVER mention competing or third-party identity management products in code, comments, or documentation
- This includes products like MIM, FIM, Entra ID, Okta, SailPoint, etc.
- JIM documentation should stand on its own without comparisons to other products
- Use generic/abstract terms to preserve context without naming products:
  - "other identity management systems" or "traditional ILM solutions"
  - "SQL Server-based ILM systems" instead of naming specific products
  - "enterprise identity platforms" for general comparisons
- Exception: Generic industry terms and standards (SCIM, LDAP, OIDC, etc.) are acceptable

## Security

JIM is deployed in high-trust/assurance customer environments, i.e. healthcare, financial services, government, etc. All code MUST meet the security expectations of these sectors.

**Key security rules (always apply):**
- Use `[Authorize]` on all API controllers - deny by default
- NEVER hardcode secrets, credentials, or connection strings in source code
- NEVER log secrets, tokens, or personal data
- ALWAYS wrap user-controlled `string?` values with `LogSanitiser.Sanitise()` (from `JIM.Utilities`) before passing them as arguments to any `ILogger` or Serilog log call - prevents log injection (CWE-117). Integers, GUIDs, enums, and DateTimes do not need wrapping.
- Use parameterised queries (EF Core default) - never bypass with unparameterised raw SQL
- Validate ALL input at system boundaries (API controllers, Blazor form submissions)
- Use AES-256-GCM for encryption at rest, minimum TLS 1.2 for transit
- Use `System.Security.Cryptography.RandomNumberGenerator` for security-sensitive random values (never `System.Random`)

> **Full security development guidelines, OWASP Top 10 details, Secure by Design principles, and compliance mapping:** See `docs/COMPLIANCE_MAPPING.md` and `docs/DEVELOPER_GUIDE.md`

## Third-Party Dependency Governance

Before adding ANY new NuGet package or third-party dependency:
1. **Notify the user** - state the need and conduct a suitability analysis
2. **Research**: licence compatibility, maintainer reputation, maintenance status, known vulnerabilities
3. **Present findings** with comparison table if alternatives exist
4. **Await user approval** before adding the dependency

Prefer: Microsoft-maintained packages > established corporate-backed packages > .NET Foundation projects > well-maintained OSS with identifiable maintainers.

> **Full supply chain security requirements:** See `docs/COMPLIANCE_MAPPING.md` and `docs/DEVELOPER_GUIDE.md`

## Feature Planning

**When planning new features or significant changes:**
1. Run `jim-prd` to create a new PRD from the template (or copy `docs/prd/PRD_TEMPLATE.md` manually)
2. Fill in the required sections (Problem Statement, Goals, Non-Goals, User Stories, Requirements, Examples, Acceptance Criteria)
3. Create a GitHub issue linking to the PRD
4. Ask Claude to generate the implementation plan from the PRD

> **Full PRD template, plan structure guidelines, and documentation organisation:** See `docs/CLAUDE.md`

## Architecture Quick Reference

**Layer Dependencies:** JIM.Web -> JIM.Application -> JIM.Models -> JIM.Data/JIM.PostgresData. **NEVER bypass layers** — UI/API must only call `JimApplication`, never `Jim.Repository.*` directly.

**Metaverse Pattern:** All operations flow through the metaverse (MetaverseObject <-> SyncRule <-> ConnectedSystemObject). Never direct system-to-system.

> **Full N-tier rules, layer diagram, common development tasks (adding connectors, endpoints, migrations), and project locations:** See `src/CLAUDE.md`

## Integration Testing

Use `./test/integration/Run-IntegrationTests.ps1` (PowerShell) - never invoke scenario scripts directly. The runner handles setup, environment management, and teardown automatically.

> **Full commands, templates, and runner details:** See `test/CLAUDE.md`

## Workflow Best Practices

**Git:**
- **ALWAYS** work on a feature branch - NEVER commit directly to `main`
- **NEVER** automatically create a PR or merge to `main` - the user must explicitly instruct you to do so
- Branch naming: `feature/description`
- Commit messages: Descriptive, include issue reference if applicable
- Build and test before every commit of .NET code (see **Exceptions** under Critical Requirements)
- Push to feature branches, create PRs to main

**Development Cycle (FOLLOW THIS EXACTLY):**
1. Create/checkout feature branch (NEVER work on `main`)
2. Make changes
3. **For .NET code changes**: Build and test affected projects during development; run full solution build/test (`dotnet build JIM.sln` and `dotnet test JIM.sln`) as a final pre-PR check
4. For non-.NET changes (scripts, docs, config, CI/CD, diagrams): skip build/test (see Exceptions above)
5. If build or tests fail, fix errors and repeat step 3
6. **ONLY AFTER** build and tests pass (or are not required): Commit with clear message
7. **ONLY AFTER** successful commit and **ONLY when the user explicitly asks**: Push and create PR
8. **NEVER** create a PR with failing tests or build errors
9. **NEVER** merge PRs without explicit user instruction

## Changelog Maintenance

The project uses [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) format. Entries must be kept up to date as changes are made, not deferred until release time.

**Audience and tone:**
The changelog is a **customer-facing product document**. The audience is administrators and decision-makers wanting to know what's new. Entries should make JIM appear useful, reliable, sophisticated, and exciting.
- Write entries as product changes, not developer notes — focus on the benefit or outcome, not the implementation detail
- Use a leading emoji per entry to add energy and visual scanning context (e.g. ✨ for new features, 🐛 for fixes, ⚡ for performance, 🔒 for security)
- Do NOT include: internal refactoring, test changes, developer tooling, CI/CD tweaks, or anything that has no user-facing impact
- Trivial changes (renamed a CSS class, moved a file, updated a comment) do NOT belong in the changelog

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
- **Added** — new features or capabilities
- **Changed** — modifications to existing behaviour
- **Fixed** — bug fixes
- **Performance** — optimisations and performance improvements
- **Removed** — removed features (use sparingly)

**Formatting conventions (match existing style):**
- Use `####` subheadings to group related entries under a larger feature (e.g., `#### Scheduler Service (#168)`)
- Reference GitHub issue numbers where applicable (e.g., `(#123)`)
- Keep entries concise — one line per change, describe what changed from the user's perspective
- Lead each entry with an appropriate emoji (✨ new, 🐛 fix, ⚡ performance, 🔄 changed, 🗑️ removed, 🔒 security, 📦 deployment/infrastructure, 🖥️ UI/UX)
- Use imperative mood is not required — describe what was added/changed/fixed naturally

**At release time:** Move all `[Unreleased]` entries to a new version section and update comparison links at the bottom of the file. See Release Process below.

## Release Process

**CRITICAL: NEVER modify the `VERSION` file without explicit user instruction to create a release.**

The `VERSION` file is the single source of truth for JIM's version number. It feeds into:
- All .NET assembly versions (via `Directory.Build.props`)
- Docker image tags (via release workflow)
- PowerShell module version (updated at release time)
- Diagram metadata (via `export-diagrams.js`)

**Versioning scheme:** [Semantic Versioning](https://semver.org/) — `X.Y.Z` with optional prerelease suffix (e.g., `0.3.0-alpha`).

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

**To create a release:** Use `/release <version>` — the skill handles VERSION, CHANGELOG, PowerShell manifest, documentation review, commit, tag, and push.

> **Full release process, air-gapped deployment, and Docker image details:** See `docs/RELEASE_PROCESS.md`

## Resources

- **Full Architecture Guide**: `docs/DEVELOPER_GUIDE.md`
- **Repository**: https://github.com/TetronIO/JIM
- **Documentation**: `README.md`
- **.NET 9 Docs**: https://learn.microsoft.com/dotnet/
- **EF Core**: https://learn.microsoft.com/ef/core/
- **Blazor**: https://learn.microsoft.com/aspnet/core/blazor/
- **MudBlazor**: https://mudblazor.com/
