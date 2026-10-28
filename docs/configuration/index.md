---
title: Configuration
description: The objects that configure JIM, from Connected Systems to Service Settings, each with its concepts, workflows and where to manage it.
type: landing
audience: administrator
---

# Configuration

JIM is configured through a small set of objects: Connected Systems, Run Profiles, Synchronisation Rules, the Metaverse schema, Schedules, and the API Keys, Certificates, Service Settings and Roles that support them. Each page here explains one object: what it is, how it fits with the others, its common workflows, and where to manage it, in the portal, with PowerShell or through the REST API. In the portal, the info button beside a page's title links to the page here that covers it.

```jim-diagram
archetype: map
title: How the configuration objects connect
desc: 'A Schedule sequences Run Profiles, which run against a Connected System and are recorded as Activities. Each Connected System has a Connector Space, and Synchronisation Rules relate it to the Metaverse, whose objects Predefined Searches search. Data Flow lists every Attribute Flow the rules carry. API Keys carry Roles, which decide what they may do; Certificates and Service Settings support them all. Each object links to its page.'
nodes:
- {id: schedules, label: Schedules, link: schedules.md, row: 0, col: 0}
- {id: rp, label: Run Profiles, link: run-profiles.md, row: 0, col: 1}
- {id: activities, label: Activities, link: activities.md, row: 0, col: 2}
- {id: cs, label: Connected Systems, link: connected-systems.md, row: 1, col: 1}
- {id: sr, label: Synchronisation Rules, link: synchronisation-rules.md, row: 1, col: 2}
- {id: mv, label: Metaverse, link: metaverse.md, row: 1, col: 3}
- {id: space, label: Connector Space, link: connector-space.md, row: 2, col: 1}
- {id: dataflow, label: Data Flow, link: data-flow.md, row: 2, col: 2}
- {id: searches, label: Predefined Searches, link: predefined-searches.md, row: 2, col: 3}
- {id: keys, label: API Keys, link: api-keys.md, row: 3, col: 0}
- {id: roles, label: Roles, link: roles.md, row: 3, col: 1}
- {id: certs, label: Certificates, link: certificates.md, row: 3, col: 2}
- {id: settings, label: Service Settings, link: service-settings.md, row: 3, col: 3}
edges:
- {from: schedules, to: rp, label: sequence}
- {from: rp, to: activities, label: recorded as}
- {from: rp, to: cs, label: run against}
- {from: cs, to: sr, label: related by}
- {from: sr, to: mv, label: to}
- {from: cs, to: space, label: each has a}
- {from: sr, to: dataflow, label: flows listed in}
- {from: searches, to: mv, label: search}
- {from: keys, to: roles, label: carry}
```

## :material-sync: Synchronisation

The objects that move identities between your systems and the Metaverse.

- [Connected Systems](connected-systems.md): the directories, databases, SCIM 2.0 services and files JIM synchronises with
    - [Connector Space](connector-space.md): what JIM holds for each Connected System, and clearing it
    - [Partitions and Containers](partitions-and-containers.md): which parts of a Connected System JIM manages
    - [Connected System Schema](connected-system-schema.md): which Object Types and attributes JIM uses, and refreshing them
    - [Connected System Attributes](connected-system-attributes.md): whether JIM can write each attribute, and its data type
    - [Connected System Passwords](connected-system-passwords.md): password policy, setting a password, and Password Synchronisation
    - [Unresolved References](unresolved-references.md): why a reference can stay unresolved, and finding what would resolve it
    - [Removing a Connected System](removing-a-connected-system.md): what deleting one does, and previewing it first
- [Run Profiles](run-profiles.md): the import, synchronisation and export operations run against a Connected System
- [Synchronisation Rules](synchronisation-rules.md): how a Connected System and the Metaverse relate
    - [Scoping Criteria](scoping-criteria.md): which objects a rule applies to
    - [Object Matching Rules](object-matching-rules.md): how an object joins an existing Metaverse Object
    - [Attribute Mappings](attribute-mappings.md): which values flow, and which import rule's value wins
    - [Generated Values](generated-values.md): unique values JIM generates, such as account names
    - [Generated Values in Target Systems](generated-values-in-target-systems.md): checking a target before choosing a value, and correcting one it refuses
    - [Deriving Metaverse Attributes](derived-attributes.md): values derived from other Metaverse values
    - [Deprovisioning](deprovisioning.md): what happens when an object leaves a rule's scope
    - [Initial Password](initial-password.md): the first password JIM gives an object it provisions
- [Data Flow](data-flow.md): every Attribute Flow across all Connected Systems, in both directions, in one list

## :material-shield-check-outline: Changing configuration safely

How JIM judges a save, and how to see what a change would do before you make it.

- [Configuration Changes](configuration-changes.md): which edits JIM confirms, and the preview of the objects an edit would affect
- [Sync Preview](sync-preview.md): what synchronising one object would do with the configuration already saved, changing nothing

## :material-hub-outline: Metaverse

The central identity store and the searches over it.

- [Metaverse](metaverse.md): Object Types, attributes, objects, and pending deletions
    - [Metaverse Objects](metaverse-objects.md): where each value comes from, and why an object is connected, or not
- [Predefined Searches](predefined-searches.md): named, reusable searches over Metaverse Objects
- [Deleted Objects](deleted-objects.md): the record JIM keeps of each object it deletes
- [Example Data](example-data.md): realistic users and groups generated for a demonstration or a proof of concept

## :material-calendar-clock-outline: Automation and Operations

Scheduled runs, the record of what JIM did, and whether the services doing it are alive.

- [Operations](operations.md): the queue, history, Schedules and Passwords tabs, with the Service Health of the Worker and Scheduler
- [Schedules](schedules.md): automated, ordered sequences of operations
- [Activities](activities.md): the record of every operation, with its status and outcome
    - [Execution Items](activity-execution-items.md): what a run did to each object
    - [Causality](causality.md): what caused a change, and what it went on to cause
    - [Live Progress](activity-progress.md): following a running Run Profile step by step

## :material-shield-lock-outline: Security and Configuration

The objects that control access, trust and how JIM runs.

- [API Keys](api-keys.md): credentials for scripts and automation
- [Certificates](certificates.md): the certificate authorities Connectors trust
- [Service Settings](service-settings.md): settings that change how JIM runs
- [Roles](roles.md): what each user can do, and who holds each role
