---
title: Configuration
---

# Configuration

JIM configuration is centred on a small set of first-class objects: connected systems, run profiles, synchronisation rules, the metaverse schema, schedules, and a handful of supporting objects (API keys, certificates, service settings, roles). Understanding what each object is, how it relates to the others, and how to manage it is the core of running JIM in production.

The pages in this section explain each configuration object in plain terms, describe its key behaviours and the workflows it participates in, and point you at the three ways to manage it: the JIM portal, PowerShell, and the REST API.

## Synchronisation

The objects that drive identity flow between external systems and the metaverse.

- [Connected Systems](connected-systems.md) -- external directories, databases, and files that JIM synchronises with
- [Run Profiles](run-profiles.md) -- import, sync, and export operations executed against a connected system
- [Synchronisation Rules](synchronisation-rules.md) -- the relationship between a connected system and the metaverse: scoping, joining, projection, and attribute flows

## Metaverse

The central identity store and the searches that surface it.

- [Metaverse](metaverse.md) -- object types, attributes, objects, and pending deletions
- [Predefined Searches](predefined-searches.md) -- named, reusable searches over metaverse objects

## Automation and Operations

Scheduled execution and the audit trail of what JIM did.

- [Schedules](schedules.md) -- automated, ordered sequences of operations
- [Activities](activities.md) -- the audit trail of every operation, with status and execution detail

## Security and Configuration

Supporting objects that control access, trust, and runtime behaviour.

- [API Keys](api-keys.md) -- non-interactive credentials for scripts and automation
- [Certificates](certificates.md) -- trusted CA certificates used by connectors
- [Service Settings](service-settings.md) -- runtime configuration values
- [Roles](roles.md) -- role definitions and role membership

## How each page is structured

Each configuration object page covers:

- **What it is** and the key concepts you need to grasp before editing it
- **How it fits in** to JIM's overall model
- **Common workflows** at a concept level (the steps that matter, not click-by-click instructions)
- **Where to do the work** in three forms: the JIM portal, PowerShell, and the REST API

The portal is the right surface for most day-to-day administration. PowerShell is the right surface for repeatable automation, scripted onboarding, and integrations into existing operational tooling. The REST API is the right surface for purpose-built integrations and bespoke tooling.
