---
title: Developer Guide
---

# Developer Guide

JIM (Junctional Identity Manager) is an enterprise-grade identity management system built on .NET 9.0. The project is open for contribution under a source-available licence.

This guide covers everything you need to get started as a contributor: setting up your development environment, understanding the architecture, building and testing the code, and writing new connectors.

## Getting Started

<div class="grid cards" markdown>

-   :material-laptop:{ .lg .middle } **Development Environment**

    ---

    Set up your environment with GitHub Codespaces or a local devcontainer; everything is pre-configured.

    [:octicons-arrow-right-24: Environment setup](dev-environment.md)

-   :material-layers-outline:{ .lg .middle } **Architecture**

    ---

    Understand JIM's layered architecture, the metaverse pattern, and how services fit together.

    [:octicons-arrow-right-24: Architecture overview](architecture.md)

-   :material-hammer-wrench:{ .lg .middle } **Building**

    ---

    Build from source, manage database migrations, and work with Docker Compose.

    [:octicons-arrow-right-24: Build guide](building.md)

-   :material-test-tube:{ .lg .middle } **Testing**

    ---

    TDD workflow, unit tests, worker tests, workflow tests, and integration tests.

    [:octicons-arrow-right-24: Testing guide](testing.md)

-   :material-power-plug-outline:{ .lg .middle } **Writing Connectors**

    ---

    Implement custom connectors to integrate JIM with new external systems.

    [:octicons-arrow-right-24: Connector guide](connectors.md)

-   :material-source-pull:{ .lg .middle } **Contributing**

    ---

    Code style, git workflow, security requirements, and contribution guidelines.

    [:octicons-arrow-right-24: Contributing](contributing.md)

</div>

## Quick Reference

| Resource | Description |
|----------|-------------|
| [Repository](https://github.com/TetronIO/JIM) | Source code on GitHub |
| [Expression Language Guide](../concepts/expressions.md) | C#-like expression language for attribute mappings |
| [SSO Setup Guide](../administration/sso-setup.md) | Configure OIDC authentication for any identity provider |
| [Deployment Guide](../administration/deployment.md) | Production deployment instructions |
