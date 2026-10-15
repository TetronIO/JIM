# Public documentation audit, October 2026

## Status: Point-in-time evidence (2026-10-11) for [#1454](https://github.com/TetronIO/JIM/issues/1454)

The evidence behind [`PRD_PUBLIC_DOCUMENTATION_STANDARD.md`](../../prd/done/PRD_PUBLIC_DOCUMENTATION_STANDARD.md). Every page under `docs/` was read in full against the house rules in `docs/CLAUDE.md` and the root `CLAUDE.md`, and the product's customer-facing surfaces were inventoried from source and mapped to the docs.

Line numbers refer to `docs/` as it stood at commit `4ec9e65`. They drift as pages change; search for the quoted text rather than trusting the number. Do not update these files to track later changes: they record what was true when the standard was designed.

| File | Covers |
|------|--------|
| [`VOICE_AUDIT_CORE.md`](VOICE_AUDIT_CORE.md) | Home, Getting Started, Concepts, Configuration and the Glossary (31 pages, about 87,000 words): per-page audience fit, Diátaxis mode, developer-speak with quotes, and the systemic patterns |
| [`VOICE_AUDIT_OPERATIONS.md`](VOICE_AUDIT_OPERATIONS.md) | Administration, Connectors, API and PowerShell (49 pages, about 135,000 words), plus an audience classification of the 18 Developer Guide pages |
| [`COVERAGE_AUDIT.md`](COVERAGE_AUDIT.md) | What the product exposes (terms, portal pages, cmdlets, REST resources, Service Settings, environment variables, connector settings, expression functions) against what the docs cover, and a design for a mechanical coverage check |
| [`STANDARDS_AND_ENFORCEMENT_RESEARCH.md`](STANDARDS_AND_ENFORCEMENT_RESEARCH.md) | What makes public documentation good (with sources), a candidate rule set with IDs, the enforcement tools evaluated against JIM's constraints, measured baselines, and a tiered, ratcheted rollout |
