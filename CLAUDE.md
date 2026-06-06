# JIM Development Quick Reference

> Identity Management System - .NET 10.0, EF Core, PostgreSQL, Blazor

This file holds cross-cutting rules and pointers. Detailed conventions live in folder-specific CLAUDE.md files which load automatically when you work in those subtrees:
- `src/CLAUDE.md` - code style, copyright headers, layer rules, common dev tasks
- `src/JIM.Web/CLAUDE.md` - Blazor/MudBlazor UI conventions, shared UI components
- `src/JIM.Application/CLAUDE.md` - synchronisation integrity (full requirements)
- `test/CLAUDE.md` - TDD patterns, test data, integration testing
- `.devcontainer/CLAUDE.md` - commands, aliases, Docker, environment
- `engineering/CLAUDE.md` - PRDs, plans, changelog, release process
- `docs/CLAUDE.md` - MkDocs site, diagram embedding, doc style

## Context7 MCP

Always use Context7 MCP when you need library/API documentation, code generation, or setup steps without the user having to ask. The MudBlazor library ID is `/mudblazor/mudblazor`.

## Critical Rules

**Build and test before every commit (.NET code):**
- Prefer targeted: `dotnet build test/JIM.Worker.Tests/`, `dotnet test test/JIM.Worker.Tests/`
- Final pre-PR check: `dotnet build JIM.sln` and `dotnet test JIM.sln` - zero errors and warnings
- Never commit code that has not built and tested locally; never create a PR with failing build/tests
- If environmental constraints prevent local build/test, mark the PR as draft and state the limitation

**Build/test exceptions** (no `dotnet build`/`test` required):
- Scripts (`.ps1`, `.sh`), static assets (CSS/JS/images), `.md` docs, config (`.env.example`, compose files, Dockerfiles, `.gitignore`, `.editorconfig`), CI/CD workflows, diagrams, plan documents
- **Partial:** UI-only Blazor/Razor changes need `dotnet build` but not `dotnet test` (no UI tests exist)

**Test-Driven Development (Red -> Green -> Refactor):**
1. Write a failing test first; confirm it fails for the right reason
2. Implement the minimum code to pass
3. Run again, confirm green; refactor without breaking
4. For bug fixes the test must reproduce the bug and fail before the fix

Never retrofit a test after the fix; never commit new functionality without tests. See `test/CLAUDE.md` for patterns.

**Ask before significant changes:** New features, architectural choices, or any non-trivial change with multiple sensible approaches - present the options with trade-offs and let the user decide. Do not assume.

**Commit without asking once approved:** When the user has said "yes" or context is clear, just commit. Do not ask "would you like me to commit?".

**Plan before building:** For any task with 3+ steps or architectural decisions, use plan mode. If you have spent more than 2-3 attempts on the same problem without progress, stop, re-plan, and consider asking the user - they can often see what you are missing.

**Demand quality:** Simplicity first; find root causes, not band-aids; for non-trivial changes pause and ask "is there a more elegant way?"; would a staff engineer approve this? (Skip the introspection for simple obvious fixes.) Prefer durable, belt-and-braces fixes over minimal one-off patches; if you offer a smaller fix, call out the tradeoff explicitly so the user can choose.

**Debugging:** Lead with the diagnosis in one or two sentences; offer detail on request. Never reach for a hardcoded path or env-specific patch to make a symptom go away; investigate the underlying cause first. Sub-agent summaries describe intent, not observed behaviour: verify any subtle claim against the source before acting on it.

**Bulk edits:** Avoid `sed`-based bulk rewrites on files that may have been partially modified by hand or by earlier tool calls; prefer targeted `Edit` calls, or dry-run the diff first. After any bulk edit, grep the touched files for unintended duplicates (e.g. repeated `ValidateSet` entries, duplicated `using` lines).

**Pushback & honesty:**

Default to stress-testing, not validating. When I present an idea, plan, or opinion, your first move is to find the weakest point - unexamined assumptions, missed edge cases, the counter-argument I would lose to. Agreement comes after pressure-testing, not as a starting position. When you do agree, add something I did not already say.

No glazing. Do not call an idea "great", "brilliant", or "smart" without concrete reasons, and even then lead with what is wrong or missing. Compliments without substance are noise. Do not echo my framing back ("X is definitely the move", "that makes a lot of sense"); start with the most useful sentence you can write instead.

If the answer is "no" or "this will not work", say so in the first sentence. The more certain I sound, the more I need pushback.

## Synchronisation Integrity

**SYNCHRONISATION INTEGRITY IS PARAMOUNT.** Sync operations are the core of JIM; customers depend on it not corrupting their data.

- Fast/hard failures over corrupted state
- All errors reported via RPEIs/Activities; never silent
- Log summary statistics at the end of every batch operation

> **Full requirements:** `src/JIM.Application/CLAUDE.md`

## Code Style

Universal rules (apply across code, scripts, docs, comments, UI text):
- **British English (en-GB) for ALL text** - "authorisation", "synchronisation", "behaviour", "colour"
- **Never use em dashes (`—`)** - use semicolons, commas, colons, or parentheses instead
- All new source files carry the Tetron copyright header (`.editorconfig` enforces it for `.cs`)

> **Full conventions** (DateTime quirks, raw SQL parameters, exception handling, copyright header table per file type, retrieval-method taxonomy, Razor/MudBlazor UI rules)**:** `src/CLAUDE.md`

## Testing

- NUnit `[Test]`, `Assert.That()`, Moq; test naming `MethodName_Scenario_ExpectedResult`
- EF Core in-memory database auto-tracks navigation properties - this masks missing `.Include()` bugs. Run integration tests when modifying repository queries.

Test project locations: `test/JIM.Web.Api.Tests/`, `test/JIM.Models.Tests/`, `test/JIM.Worker.Tests/`.

> **Full patterns, debugging, integration testing runner:** `test/CLAUDE.md`

## Commands & Environment

Quick reference:
- `jim-compile` / `jim-test` / `jim-test-all` - Build and test
- `jim-db` / `jim-stack` - Start database / full Docker stack
- `jim-build-light` - Start db + Keycloak, run JIM.Web natively
- `jim-build-web` / `jim-build-worker` / `jim-build-scheduler` - Rebuild containers after code changes

> **Full commands, aliases, Docker workflows, dependency policy, troubleshooting:** `.devcontainer/CLAUDE.md`

## Scripting

Use PowerShell (`.ps1`) for ALL automation, integration tests, and utility scripts; it is cross-platform. Never create bash/shell scripts for project automation. Exception: `.devcontainer/setup.sh` runs during container creation.

## Design Principles

**Minimise environment variables:** Prefer admin UI / setup wizards. Reserve env vars for bootstrap (initial DB connection), pre-encryption secrets, and container orchestration overrides.

**Self-contained and air-gap deployable:** No internet connectivity required. No cloud-service dependencies (Azure Key Vault, AWS KMS, etc.). All features must work on-premises only.

**No third-party product references:** Never name competing identity products (MIM, FIM, Entra ID, Okta, SailPoint, etc.) in code, comments, or docs. Use generic terms ("traditional ILM solutions", "SQL Server-based ILM systems"). Industry standards (SCIM, LDAP, OIDC) are fine.

## Security

JIM is deployed in high-trust environments (healthcare, finance, government); all code must meet those expectations.

- `[Authorize]` on all API controllers - deny by default
- Never hardcode secrets, credentials, or connection strings; never log secrets, tokens, or personal data
- Validate ALL input at system boundaries (API controllers, Blazor form submissions)
- AES-256-GCM at rest, minimum TLS 1.2 in transit
- Use `System.Security.Cryptography.RandomNumberGenerator` for security-sensitive random values - never `System.Random`

**CVE suppressions (`.trivyignore`):**
- Only suppress when verified false positive, mitigated at the application layer, or genuinely unreachable
- Every entry needs CVE ID(s), affected component, why Trivy flags it, where the real mitigation lives, and a review date (~3 months out)
- Never suppress to skip investigation. When touching `.trivyignore`, re-evaluate entries past their review date.

> **Full guidelines, OWASP details, Secure-by-Design:** `engineering/COMPLIANCE_MAPPING.md` and `engineering/DEVELOPER_GUIDE.md`

## Third-Party Dependency Governance

Before adding ANY new NuGet package or third-party dependency:
1. Notify the user; state the need and rationale
2. Research: licence compatibility, maintainer reputation, maintenance status, known vulnerabilities
3. Present findings (comparison table if alternatives exist)
4. Await user approval before adding

Preference order: Microsoft-maintained > established corporate-backed > .NET Foundation > well-maintained OSS with identifiable maintainers.

## Architecture Quick Reference

**Layer dependencies:** JIM.Web -> JIM.Application -> JIM.Models -> JIM.Data/JIM.PostgresData. **Never bypass layers** - UI/API must only call `JimApplication`, never `Jim.Repository.*` directly.

**Metaverse pattern:** All operations flow through the metaverse (MetaverseObject <-> SyncRule <-> ConnectedSystemObject). Never direct system-to-system.

> **Full N-tier rules, layer diagram, common dev tasks (adding connectors, endpoints, migrations):** `src/CLAUDE.md`

## Integration Testing

Use `./test/integration/Run-IntegrationTests.ps1` (PowerShell) - never invoke scenario scripts directly. The runner handles setup, environment management, and teardown automatically.

> **Full commands, templates, runner details:** `test/CLAUDE.md`

## Feature Planning

For new features or significant changes:
1. Run `jim-prd` to create a new PRD from the template
2. Fill in required sections; create a GitHub issue linking to the PRD
3. Ask Claude to generate the implementation plan from the PRD

> **PRD template, plan structure, plan filing (planned/doing/done):** `engineering/CLAUDE.md`

## Workflow & Git

- Always work on a feature branch; never commit directly to `main`
- Branch naming: `feature/description`
- **Reuse the existing feature branch; never create a new one because the current branch's name "doesn't fit" the task.** If you start work and find yourself already checked out on a feature branch (i.e. not `main`), commit your changes there. Do not create `feature/<something-else>` for an unrelated task. The user runs multiple chat sessions in parallel and tracks work by branch; spawning new branches makes commits invisible across sessions. If the scope has genuinely broadened, surface it: "this commit is unrelated to the current branch name; want me to rename the branch or stay on it?" and let the user decide. Only branch off `main` when the user explicitly asks, or when you have just merged/finished the previous branch and are starting fresh from a clean `main`.
- Never automatically create a PR or merge to `main` - the user must explicitly instruct
- Build and test pass before commit (per Critical Rules); push and PR only when the user asks
- Before filing a new GitHub issue, ALWAYS search existing open and closed issues for duplicates: `gh issue list --state all --search "<keywords>"`. Surface any close matches to the user before creating a new one.
- Dependabot does not auto-rebase PRs when they fall behind `main`. After merging any PR in a batch, comment `@dependabot rebase` on each remaining open Dependabot PR via `gh pr comment <num> --body '@dependabot rebase'`.

### Merging via gh CLI

`main` is protected by a ruleset that requires seven status checks to pass before a merge is allowed: `build-and-test`, `discover-base-images`, `scan-base-images-summary`, the three CodeQL analyses (`Analyze (actions)`, `Analyze (csharp)`, `Analyze (javascript-typescript)`), and `claude-review`. Strict mode is on, so the PR must be up to date with `main`. Zero approvals are required, but unresolved review threads block the merge.

- Default merge command: `gh pr merge <n> --squash --delete-branch --auto`. The `--auto` flag queues the merge so it lands the moment all required checks go green.
- An immediate `gh pr merge` failure right after `gh pr create` is **expected**, not a blocker. The checks haven't started yet. Don't escalate it; just use `--auto`.
- **Never use `--admin` to bypass.** It overrides the required checks and the harness will (correctly) refuse it.

### Closing the loop after `--auto`

Use the **Bash tool with `run_in_background: true`** and an until-loop so the harness notifies you when the PR transitions to `MERGED`:

```bash
until [ "$(gh pr view <n> --json state -q .state)" = "MERGED" ]; do sleep 30; done
```

This is the right primitive per the Monitor tool's own guidance: a single completion event ("tell me when X is true") is a Bash background command that exits when the condition is satisfied. The Monitor tool is for per-occurrence streams (a notification per matching log line) and is the wrong fit here.

Don't ScheduleWakeup, don't sleep-poll between turns, don't proactively re-check. Wait for the notification.

When it fires, run cleanup:

```bash
git checkout main
git pull --ff-only
git fetch --prune
git branch -D <feature-branch>
```

Use `-D` (capital D), not `-d`. We squash-merge by default, so the feature branch's commits are not ancestors of `main` (main gets one new squash commit instead). `git branch -d` refuses with *"not fully merged"* in that case; `-D` is safe because the squash commit is already on `main`. If you were checked out on the feature branch when `gh pr merge` ran, the local ref may already have been auto-deleted, in which case `-D` will report "branch not found"; that is success, not failure.

## Changelog & Release

- Add entries under `## [Unreleased]` in `CHANGELOG.md` for user-facing changes (features, fixes, performance, changed behaviour, removals)
- Skip changelog entries for docs, CI/CD, dev tooling, refactoring, test-only, and trivial UI tweaks
- **Never modify `VERSION` without explicit user instruction.** Use `/release <version>` to create a release.

> **Full audience/tone, categories, formatting, release procedure:** `engineering/CLAUDE.md`

## Resources

- **Architecture Guide:** `engineering/DEVELOPER_GUIDE.md`
- **Repository:** https://github.com/TetronIO/JIM
- **.NET 10:** https://learn.microsoft.com/dotnet/
- **EF Core:** https://learn.microsoft.com/ef/core/
- **Blazor:** https://learn.microsoft.com/aspnet/core/blazor/
- **MudBlazor:** https://mudblazor.com/
