# Core Concepts

JIM (Junctional Identity Manager) is a self-hosted identity lifecycle management platform that synchronises identity data between Connected Systems through a centralised metaverse hub. This section introduces the foundational concepts you need to understand how JIM works.

For per-object documentation (Connected Systems, Synchronisation Rules, Schedules, etc.) see the [Configuration](../configuration/index.md) section.

<img class="diagram-light" alt="JIM Containers" src="../diagrams/images/light/jim-structurizr-1-Containers.svg">
<img class="diagram-dark" alt="JIM Containers" src="../diagrams/images/dark/jim-structurizr-1-Containers.svg">

## 🏗️ Architecture

JIM follows a hub-and-spoke **metaverse pattern** where all identity data flows through a central authoritative repository. No data moves directly between Connected Systems -- every change passes through the metaverse, giving you a single point of governance and control. Learn about JIM's components, layers, and deployment model in the [Architecture](architecture.md) guide.

## ⚙️ Synchronisation Pipeline

JIM processes identity data in three distinct phases: **Import**, **Sync**, and **Export**. This pipeline ensures data is validated, transformed, and reconciled at each stage before reaching its destination. The [Synchronisation Pipeline](synchronisation-pipeline.md) page explains each phase in detail.

## 🔄 JML Lifecycle

The **Joiner/Mover/Leaver** lifecycle is the core automation model for identity management. JIM handles new starters, role changes, and leavers through configurable rules that provision, update, and deprovision accounts across your estate. The [JML Lifecycle](jml-lifecycle.md) page covers each phase.

## 🧮 Expressions

JIM includes a built-in **expression language** for transforming and mapping identity attributes. Expressions let you build email addresses, control account states, handle missing values, and much more -- all without writing code. See the [Expression Language Guide](expressions.md) for syntax, functions, and examples.

## 🔡 Case Sensitivity

JIM compares identity data exactly (case-sensitive) by default, while keeping configuration names and search forgiving (case-insensitive). The [Case Sensitivity](case-sensitivity.md) page explains where each rule applies, and how to relax matching and scoping per rule where a data source is inconsistent.
