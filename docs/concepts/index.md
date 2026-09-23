# Core Concepts

JIM (Junctional Identity Manager) is a self-hosted identity lifecycle management platform that synchronises identity data between Connected Systems through a centralised metaverse hub. This section introduces the foundational concepts you need to understand how JIM works.

For per-object documentation (Connected Systems, Synchronisation Rules, Schedules, etc.) see the [Configuration](../configuration/index.md) section.

--8<-- "assets/diagrams/hub-and-spoke.svg"

<p class="jim-diagram-caption">The Connected Systems shown are illustrative examples.<span class="jimdg-caption-motion"> Moving dots trace identity data flowing through JIM.</span></p>

## 🏗️ Architecture

JIM follows a hub-and-spoke **metaverse pattern** where all identity data flows through a central authoritative repository. No data moves directly between Connected Systems -- every change passes through the metaverse, giving you a single point of governance and control. Learn about JIM's components, layers, and deployment model in the [Architecture](architecture.md) guide.

## ⚙️ Synchronisation Pipeline

JIM processes identity data in three distinct phases: **Import**, **Sync**, and **Export**. This pipeline ensures data is validated, transformed, and reconciled at each stage before reaching its destination. The [Synchronisation Pipeline](synchronisation-pipeline.md) page explains each phase in detail.

## 🔄 JML Lifecycle

The **Joiner/Mover/Leaver** lifecycle is the core automation model for identity management. JIM handles new starters, role changes, and leavers through configurable rules that provision, update, and deprovision accounts across your estate. The [JML Lifecycle](jml-lifecycle.md) page covers each phase.

## 🥇 Attribute Priority

When more than one Connected System feeds the same Metaverse attribute, **Attribute Priority** decides which value wins, deterministically, whatever order synchronisations happen to run in. The [Attribute Priority](attribute-priority.md) page explains how resolution works, what "Null is a value" asserts, and how a value hands over to the next contributor when its source departs.

## 🔑 Passwords

Where a Connector supports it and you have configured it, JIM can set passwords on the accounts it manages. It does so through a **password channel** that runs parallel to attribute flow and never through it: nothing is held in the Metaverse, staged as a Pending Export, or read back. The [Passwords](passwords.md) page covers how JIM discovers what a target will accept, where a password comes from, what happens to an account whose password a target refuses, and the security rules that hold across every surface. It also covers **Password Synchronisation**: one password change queued, encrypted and delivered to every Connected System configured to receive it.

## 🧮 Expressions

JIM includes a built-in **expression language** for transforming and mapping identity attributes. Expressions let you build email addresses, control account states, handle missing values, and much more -- all without writing code. See the [Expression Language Guide](expressions.md) for syntax, functions, and examples.

## 🏷️ Object Naming

Wherever JIM shows you an object, it names it the same way: an ordered list of naming attributes, then an identifier as the fallback. The [Object Naming](object-naming.md) page gives the order for Connected System Objects and Metaverse Objects.

## 🔡 Case Sensitivity

JIM compares identity data exactly (case-sensitive) by default, while keeping configuration names and search forgiving (case-insensitive). The [Case Sensitivity](case-sensitivity.md) page explains where each rule applies, and how to relax matching and scoping per rule where a data source is inconsistent.
