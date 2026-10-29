---
title: Core Concepts
description: The ideas JIM is built on, from the Metaverse and the synchronisation pipeline to Attribute Priority and expressions.
type: landing
audience: implementer
validated: v0.17.0
---

# Core Concepts

JIM (Junctional Identity Manager) is a self-hosted identity lifecycle management platform that synchronises identity data between Connected Systems through a central Metaverse. This section introduces the foundational concepts you need to understand how JIM works.

For per-object documentation (Connected Systems, Synchronisation Rules, Schedules, etc.) see the [Configuration](../configuration/index.md) section.

--8<-- "assets/diagrams/hub-and-spoke.svg"

<p class="jim-diagram-caption">The Connected Systems shown are illustrative examples.<span class="jimdg-caption-motion"> Moving dots trace identity data flowing through JIM.</span></p>

## :material-layers-outline: Architecture

JIM follows a hub-and-spoke pattern: all identity data flows through a central, authoritative **Metaverse**. No data moves directly between Connected Systems; every change passes through the Metaverse, giving you a single point of governance and control. Learn about JIM's components, layers, and deployment model in the [Architecture](architecture.md) guide.

## :material-transit-connection-variant: Synchronisation Pipeline

JIM processes identity data in three distinct phases: **Import**, **Sync**, and **Export**. This pipeline ensures data is validated, transformed, and reconciled at each stage before reaching its destination. The [Synchronisation Pipeline](synchronisation-pipeline.md) page explains each phase in detail.

## :material-account-sync-outline: JML Lifecycle

The **Joiner/Mover/Leaver** lifecycle is the core automation model for identity management. JIM handles new starters, role changes, and leavers through configurable rules that provision, update, and deprovision accounts across your estate. The [JML Lifecycle](jml-lifecycle.md) page covers each phase.

## :material-sort-variant: Attribute Priority

When more than one Connected System feeds the same Metaverse Attribute, **Attribute Priority** decides which value wins, deterministically, whatever order synchronisations happen to run in. The [Attribute Priority](attribute-priority.md) page explains how resolution works, what "Null is a value" asserts, and how a value hands over to the next contributor when its source departs.

## :material-key-outline: Passwords

Where a Connector supports it and you have configured it, JIM can set passwords on the accounts it manages. It does so through a **password channel** that runs alongside the Attribute Flows and never through them: nothing is held in the Metaverse, staged as a Pending Export, or read back. The [Passwords](passwords.md) page covers how JIM discovers what a target will accept, where a password comes from, what happens to an account whose password a target refuses, and the security rules that hold across every surface. [Password Synchronisation](password-synchronisation.md) covers the other half: one password change queued, encrypted and delivered to every Connected System configured to receive it.

## :material-function-variant: Expressions

JIM includes a built-in **expression language** for transforming and mapping identity attributes. Expressions let you build email addresses, control account states, handle missing values, and much more, all without writing code. See [Expressions](expressions.md) for syntax, functions, and examples, and [Nulls and Missing Inputs](expression-nulls.md) for what an expression does with an attribute an object does not have.

## :material-tag-outline: Object Naming

Wherever JIM shows you an object, it names it the same way: an ordered list of naming attributes, then an identifier as the fallback. The [Object Naming](object-naming.md) page gives the order for Connected System Objects and Metaverse Objects.

## :material-format-letter-case: Case Sensitivity

Whenever JIM compares text it is asking one of four questions, and each has one rule: whether a value has changed is exact, whether two values are the same thing ignores case unless an identifier is declared case-exact, whether a value meets a condition is exact unless you say otherwise, and finding things ignores case. The [Case Sensitivity](case-sensitivity.md) page explains why, and lists every feature with the rule it follows.
