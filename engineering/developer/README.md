# Developer Guide

JIM (Junctional Identity Manager) is an enterprise-grade identity management system built on .NET 10.0. The project is open for contribution under a source-available licence.

This guide covers everything you need to get started as a contributor: setting up your development environment, understanding the architecture, building and testing the code, and writing new connectors.

## Getting Started

| Guide | What it covers |
|-------|----------------|
| [Development Environment](dev-environment.md) | Setting up GitHub Codespaces or a local devcontainer; everything is pre-configured. |
| [Architecture](architecture.md) | JIM's layered architecture, the Metaverse pattern, and how the services fit together. |
| [Building](building.md) | Building from source, database migrations, and Docker Compose. |
| [Testing](testing.md) | The TDD workflow, unit, worker and workflow tests, and integration tests. |
| [PowerShell Module](powershell-module.md) | Developing and testing the JIM PowerShell module. |
| [Writing Connectors](connectors.md) | Implementing a Connector inside JIM, which runs only the Connectors built into it. |
| [Contributing](contributing.md) | Code style, the git workflow, security requirements, and contribution guidelines. |
| [Process diagrams](diagrams/) | How imports, synchronisation, exports, deletion and Schedules run inside the Worker. |

The wider engineering documentation is one folder up: the [Developer Guide](../DEVELOPER_GUIDE.md) holds the architecture and conventions in depth.

## Quick Reference

| Resource | Description |
|----------|-------------|
| [Repository](https://github.com/TetronIO/JIM) | Source code on GitHub |
| [Discussions](https://github.com/TetronIO/JIM/discussions) | Q&A, feature ideas, and design conversations |
| [Issues](https://github.com/TetronIO/JIM/issues) | Bug reports and tracked work |
| [Expression Language Guide](../../docs/concepts/expressions.md) | C#-like expression language for attribute mappings |
| [SSO Setup Guide](../../docs/administration/sso-setup.md) | Configure OIDC authentication for any identity provider |
| [Deployment Guide](../../docs/administration/deployment.md) | Production deployment instructions |
