# Contributing to JIM

Thanks for your interest in contributing to JIM. JIM is a source-available commercial product, and we welcome contributions from the community. This document explains how to contribute and what to expect when you do.

A quick note on what JIM is. JIM is the Junctional Identity Manager from [Tetron](https://tetron.io), an enterprise identity lifecycle management platform. It's self-hosted, container-native, and designed to run anywhere, from internet-connected enterprise environments to fully air-gapped deployments in healthcare, finance, government, and defence. JIM is source-available, free to use in non-production scenarios, with a commercial licence required for production. See the [licensing page](https://tetron.io/jim/#licensing) for full details.

## Ways to contribute

There are several ways to get involved, from low-effort to high-effort:

- **Answer questions** in [Discussions Q&A](https://github.com/TetronIO/JIM/discussions/categories/q-a). Helping someone unblock is one of the most valuable contributions you can make.
- **Improve the documentation.** Typo fixes, clarifications, missing examples, or full new guides, all welcome.
- **Report bugs.** A clear reproduction is enormously valuable, even without a fix.
- **Suggest features** in [Discussions Ideas](https://github.com/TetronIO/JIM/discussions/categories/ideas). Upvoting existing ideas is itself a useful signal.
- **Tell us about a connector you'd find valuable.** JIM is exploring an extensible connector framework. The design is not yet finalised, but we're keen to understand which systems people want to integrate. See [A note on connectors](#a-note-on-connectors) below.
- **Contribute code.** Bug fixes, doc improvements, and small enhancements are welcome via pull request. Read the section on [what we can absorb at our current stage](#what-we-can-absorb-at-our-current-stage) before starting work on anything substantial.

## Where to ask, report, or propose

The right place depends on what you have:

| You have... | Go to... |
|---|---|
| A question about how to use JIM | [Discussions → Q&A](https://github.com/TetronIO/JIM/discussions/categories/q-a) |
| A defect to report (something is broken) | [Issues → New Issue](https://github.com/TetronIO/JIM/issues/new) |
| A feature suggestion | [Discussions → Ideas](https://github.com/TetronIO/JIM/discussions/categories/ideas) |
| A security vulnerability | **Don't post publicly.** See [SECURITY.md](SECURITY.md) |
| Code to contribute | A pull request, with the caveats below |

If you're unsure, post in Discussions and we'll redirect if needed.

## Reporting bugs

Open an [Issue](https://github.com/TetronIO/JIM/issues/new) and include:

- The version of JIM you're running (visible in the UI footer)
- Your deployment method (Docker Compose, Kubernetes, etc.)
- Connector(s) involved, if relevant
- Clear reproduction steps
- What you expected to happen, and what actually happened
- Relevant log excerpts, **with sensitive data redacted** (real usernames, hostnames, LDAP DNs, tokens, customer identifiers)

If you're not sure whether it's a bug or a configuration issue, ask in Q&A first. We'll convert to an Issue if it turns out to be a defect.

## Suggesting features

Start in [Discussions → Ideas](https://github.com/TetronIO/JIM/discussions/categories/ideas) rather than opening an Issue or PR directly. Describe the problem you're trying to solve before the solution you have in mind. It lets us consider alternative approaches that might solve your need better. The upvote signal on Ideas genuinely informs roadmap priorities.

## What we can absorb at our current stage

JIM is at an early stage, and Tetron is a small team. We genuinely welcome contributions, but our capacity to review and integrate them is currently limited, and we'd rather be honest about that than nominally accept a PR and leave it stalled. Three flavours of contribution, with different paths:

**Welcome via PR with no prior discussion.** Typo fixes, documentation improvements, clear bug fixes (with a regression test, see [Tests](#tests) below), and small targeted improvements within existing functionality. These are within our review capacity and we'll get to them quickly.

**Raise in Discussions Ideas, but please don't open a PR yet.** New features, behavioural changes to existing functionality, refactors of any meaningful scope, and anything that touches the metaverse or synchronisation core. At our current stage we are likely to thank you for the suggestion, log it on the roadmap, and pick it up ourselves later, rather than commit to reviewing a substantial external PR straight away. This is purely a function of our review bandwidth, not a comment on the value of the contribution. As the team and project mature we expect to be able to support more direct community contribution to substantive features. When that changes, we will be explicit in the Idea thread to indicate that PRs are welcome on that specific item.

**Connectors.** A separate path is being designed for connectors. See the next section.

### A note on connectors

JIM is exploring an extensible connector framework. The current thinking is that connectors, whether developed by customers, partners, or the community, would live in their own git repositories rather than in the JIM core repo, and JIM deployments would install them either by referencing a repository directly (in connected environments) or by uploading a release artefact via the admin UI (in air-gapped environments).

**This is current thinking, not a finalised design.** Loading third-party code into a system that handles identity data is one of the more security-sensitive architectural decisions JIM will make, and the design needs to be worked through using JIM's formal feature development process. That process applies [Secure By Design](https://www.cisa.gov/resources-tools/resources/secure-by-design) principles, treating security as a first-class design concern rather than something added later. The eventual shape of the framework, including whether the loading-from-repository model is appropriate at all, will depend on the outcomes of that work. The framework may differ materially from the description above.

What we can say at this stage:

- Connectors are a strong candidate to live out-of-tree as extensions, both because that fits JIM's air-gapped deployment posture and because it lets each connector move at its own pace under its own maintainer rather than being gated by JIM's core release cadence.
- Whatever the eventual shape of the framework, it will be Tetron-maintained as a piece of the core platform.
- The work belongs in the v1.x-CONNECTORS roadmap milestone.

What we can't say yet:

- Any commitment to loading-from-git, the manifest format, the security boundary, the permissions model, or a delivery date.
- Whether community connectors will be supported in the same way as Tetron's first-party connectors, or differently.

The practical implication for contributors: please don't start writing code against the model described above. If you're interested in a connector for a specific system, raise it in [Discussions Ideas](https://github.com/TetronIO/JIM/discussions/categories/ideas). We're keen to understand which systems people want to integrate, both to inform the framework's design and to think about which connectors Tetron itself should ship as first-party. Watching the v1.x-CONNECTORS milestone is the way to track progress.

## Contributing code

If your contribution falls into the "PR welcome" tier above, here's how to proceed.

### Development setup

The [Developer Guide](docs/DEVELOPER_GUIDE.md) covers everything you need: GitHub Codespaces setup, local installation, the build, the test suite, the architectural patterns, and conventions for connectors, API endpoints, and schema migrations.

If you can run the test suite locally, you have everything you need.

### Workflow

1. Fork the repository (or branch directly if you have write access).
2. Create a branch named `feature/short-description` or `fix/short-description`.
3. Make your changes, with tests where appropriate (see [Tests](#tests) below).
4. Run `dotnet build JIM.sln` and `dotnet test JIM.sln` locally. Both must succeed before you commit.
5. Commit with a clear message that describes what changed and why. Reference the issue number if applicable (e.g. "Fix attribute flow regression in CSV connector (#234)").
6. Push and open a pull request against `main`.
7. The PR template will prompt you to confirm tests pass and (for first-time contributors) to sign the CLA.
8. We'll review, suggest changes if needed, and merge.

We aim to respond to PRs within five UK business days. Smaller, focused PRs are reviewed and merged faster than large multi-concern PRs.

### Coding standards

The Developer Guide covers our coding standards in detail. The points worth highlighting up front:

- **British English (en-GB)** for all user-facing text, comments, and documentation. We use "synchronisation", "authorisation", "behaviour", "colour", and so on.
- **Test naming** follows `MethodName_Scenario_ExpectedResult` and uses NUnit `[Test]` attributes.
- **Layer boundaries** are real. Upper layers depend on lower layers; never the reverse. See the Developer Guide for the full architecture.

### Tests

JIM is developed using test-driven development, and we hold contributions to the same standard we hold ourselves. Specifically:

- **New functionality must include tests** that exercise the behaviour being added. Without tests, the PR will not be merged.
- **Bug fixes must include a regression test** — a test that fails against `main` without your fix and passes with it. This is how we ensure the bug stays fixed across future changes.
- **Documentation-only changes** don't need tests.
- **Refactors** must be covered by existing tests; if no test exists for the code being refactored, please add one before changing the behaviour.

We have three test categories (unit, workflow, and integration), and the [Developer Guide](docs/DEVELOPER_GUIDE.md) and [Testing Strategy](docs/TESTING_STRATEGY.md) explain when each applies. The short version: most contributions should include unit tests; multi-step business logic should include workflow tests; full end-to-end behaviour is covered by integration tests, which are usually a maintainer responsibility.

A PR with failing tests will not be merged. Run `dotnet test JIM.sln` before pushing, and ensure all tests pass.

If you're stuck on how to test something, ask in the PR or in [Discussions → General](https://github.com/TetronIO/JIM/discussions/categories/general). We'd rather help you write the test than reject a useful contribution because the test approach was unclear.

## Contributor Licence Agreement (CLA)

JIM requires contributors to sign a Contributor Licence Agreement before pull requests can be merged. This is automated via [CLA Assistant](https://cla-assistant.io). On your first PR, you'll see a one-time prompt to sign in with your GitHub account and accept the agreement. Subsequent PRs require nothing further.

The CLA exists because JIM is offered under both source-available and commercial terms, and grants Tetron the rights necessary to maintain that dual-licensing model, indemnify customers, and adapt to the evolving licensing landscape over time. It does not transfer copyright. You retain ownership of your contribution. The agreement itself is short and modelled on the widely-adopted Apache 2.0 ICLA.

A point worth being clear about: the licence you grant under the CLA is irrevocable. Once you've signed and submitted a contribution, you cannot later withdraw permission for Tetron to use, distribute, or relicense it. You retain copyright in your work; what you cannot do is take back the licence to the project. This is standard for CLAs and is the basis on which Tetron and our customers can confidently rely on contributed code over the long term. If that's not a commitment you're comfortable making, please don't sign the CLA, and we won't be able to merge your PR — but you remain welcome to suggest the change as an Idea instead.

If you're contributing on behalf of an employer, or if your contribution is substantial enough that your employer has rights in it under your employment contract, please get in touch before submitting. We may need a Corporate CLA from your employer in addition to your individual signature.

If you have concerns about the CLA, raise them in [Discussions → General](https://github.com/TetronIO/JIM/discussions/categories/general).

## Documentation contributions

Documentation lives in two places:

- The repo's `docs/` directory, in Markdown. PRs against this directory go through the same flow as code.
- The Tetron documentation site (when launched at `junctional.io`). Source for that site lives in a separate repository; significant doc contributions to the public site can be discussed in Discussions.

Typo fixes in `docs/` can go straight to PR with no prior discussion.

## Code of Conduct

JIM follows the [Contributor Covenant Code of Conduct](CODE_OF_CONDUCT.md). By participating in Discussions, Issues, or contributing code, you agree to abide by its terms. Report unacceptable behaviour to the address listed in the Code of Conduct.

## Security

Do not report security vulnerabilities in Discussions, Issues, or pull requests. Use [GitHub's private vulnerability reporting](https://github.com/TetronIO/JIM/security) on this repository, or follow the instructions in [SECURITY.md](SECURITY.md). We'll acknowledge receipt promptly and coordinate responsible disclosure with you.

## Licensing

JIM is source-available under terms described at [tetron.io/jim/#licensing](https://tetron.io/jim/#licensing). The full licence text is in [LICENSE](LICENSE) at the repository root. By contributing under the CLA, you grant Tetron the rights necessary to include your contribution under both the existing licence and any commercial licences Tetron grants.

## Recognition

Significant contributors are credited in release notes for the release that includes their contribution. We don't maintain a separate `CONTRIBUTORS` file. Git history is the canonical record.

## Questions

If anything in this document is unclear, ask in [Discussions → General](https://github.com/TetronIO/JIM/discussions/categories/general) and we'll improve the document. We'd genuinely rather you ask than guess.

Welcome to JIM, and thanks again for contributing.

— *The JIM team at [Tetron](https://tetron.io)*
