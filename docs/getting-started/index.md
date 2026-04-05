---
title: Getting Started
description: Get up and running with JIM, from prerequisites through to your first identity synchronisation.
---

# Getting Started

Welcome to JIM. This section will guide you from initial setup through to running your first identity synchronisation.

Whether you are deploying JIM for production use or setting up a development environment, the pages below will walk you through each step.

## Where to Start

<div class="grid cards" markdown>

-   :material-clipboard-check:{ .lg .middle } **Prerequisites**

    ---

    What you need before deploying JIM: container runtime, identity provider, and hardware considerations.

    [:octicons-arrow-right-24: Prerequisites](prerequisites.md)

-   :material-rocket-launch:{ .lg .middle } **Quick Start**

    ---

    Deploy JIM using automated setup, manual Docker Compose, air-gapped installation, or a developer environment.

    [:octicons-arrow-right-24: Quick Start](quickstart.md)

-   :material-sync:{ .lg .middle } **Your First Sync**

    ---

    Set up a connected system, configure a sync rule, and run your first import, sync, and export cycle.

    [:octicons-arrow-right-24: Your First Sync](first-sync.md)

</div>

## Overview

Getting JIM running involves three main steps:

1. **Prepare your environment:** Ensure Docker is installed and you have access to an OpenID Connect identity provider for authentication.
2. **Deploy JIM:** Choose from automated setup, manual Docker Compose, or air-gapped deployment depending on your environment.
3. **Configure your first synchronisation:** Connect a source system, define sync rules, and verify data flows correctly through the metaverse.

Once deployed, JIM is accessible via a web portal at `http://localhost:5200` by default. You can also interact with JIM through its REST API or the cross-platform PowerShell module.
