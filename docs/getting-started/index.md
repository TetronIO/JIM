---
title: Getting Started
description: Get up and running with JIM, from prerequisites through to your first identity synchronisation.
type: landing
audience: evaluator
---

# Getting Started

These pages take you from an empty server to JIM synchronising its first identities: what to have ready, installing JIM, and a first synchronisation you can follow step by step. If you get stuck, ask in the [Q&A category of GitHub Discussions](https://github.com/TetronIO/JIM/discussions/categories/q-a), or open a [GitHub Issue](https://github.com/TetronIO/JIM/issues) for a bug.

```jim-diagram
archetype: map
title: Three pages from an empty server to a first synchronisation
desc: "Getting Started is three pages, read in order. Prerequisites lists what to have ready; then Quick Start installs JIM and signs you in; then Your First Synchronisation takes 25 people from an HR file to a staff directory file. Each node links to its page."
nodes:
- {id: pre, label: Prerequisites, link: prerequisites.md, row: 0, col: 0}
- {id: quick, label: Quick Start, link: quickstart.md, row: 0, col: 1}
- {id: first, label: Your First Synchronisation, link: first-synchronisation.md, row: 0, col: 2}
edges:
- {from: pre, to: quick, label: then install}
- {from: quick, to: first, label: then synchronise}
```

<div class="grid cards" markdown>

-   :material-clipboard-check-outline:{ .lg .middle } **Prerequisites**

    ---

    What you need before deploying JIM: a container runtime, an identity provider, a certificate, a database, and the hardware to run them.

    [:octicons-arrow-right-24: Prerequisites](prerequisites.md)

-   :material-rocket-launch-outline:{ .lg .middle } **Quick Start**

    ---

    Install JIM on Docker or Podman with the installer, connected or air-gapped, and sign in.

    [:octicons-arrow-right-24: Quick Start](quickstart.md)

-   :material-sync:{ .lg .middle } **Your First Synchronisation**

    ---

    Take 25 people from an HR file, through the Metaverse, to a staff directory file, in the portal or with PowerShell.

    [:octicons-arrow-right-24: Your First Synchronisation](first-synchronisation.md)

</div>
