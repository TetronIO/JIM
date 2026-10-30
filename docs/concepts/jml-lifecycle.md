---
title: JML Lifecycle
description: How JIM provisions, updates and deprovisions accounts as people join, move within and leave an organisation.
type: concept
audience: implementer
validated: v0.17.0
---

# JML Lifecycle

The **Joiner/Mover/Leaver** (JML) lifecycle is the foundational automation model for identity management. It describes the three transitions a person goes through during their time in an organisation, and how JIM provisions, updates and deprovisions their accounts across Connected Systems in response.

--8<-- "assets/diagrams/jml-lifecycle.svg"

<p class="jim-diagram-caption">One identity's journey through JIM: events recorded in the source system flow through the Metaverse to every target system. Accounts are provisioned for joiners, updated for movers, and deprovisioned (disconnected or deleted) for leavers; the systems shown are illustrative.<span class="jimdg-caption-motion"> Moving dots trace the import and export flows.</span></p>

JIM handles each transition through its [Synchronisation Rules](../configuration/synchronisation-rules.md) and the [synchronisation pipeline](synchronisation-pipeline.md), acting on your configuration without anyone stepping in.

## :material-account-plus-outline: Joiner

A **joiner** event occurs when a new identity appears in a source system for the first time, as when HR creates a record for a new employee.

### What Happens

1. **Import**<br /> JIM imports the new record from the source system, creating a new Connected System Object in its Connector Space.

2. **Matching**<br /> During inbound synchronisation, JIM uses the [Object Matching Rules](../configuration/object-matching-rules.md) to check whether this person already exists in the Metaverse: a rehire, say, or a record that arrived from another source system first.

3. **Projection**<br /> If no existing Metaverse Object matches, JIM **projects** a new one. This is the person's arrival in the Metaverse.

4. **Inbound Attribute Flow**<br /> The new employee's attributes (name, department, employee ID and so on) flow from the Connected System Object into the new Metaverse Object.

5. **Outbound evaluation**<br /> Export Synchronisation Rules evaluate the new Metaverse Object. For each one that provisions and has it in scope, JIM **provisions** a new Connected System Object in that rule's target Connected System, unless an Object Matching Rule finds an existing account to join.

6. **Export**<br /> The provisioned objects are exported to the target systems, creating the actual accounts, such as an Active Directory user account or a mailbox.

### Example

A new employee, Jane Smith, is added to the HR system:

1. The HR import creates a Connected System Object with Jane's details.
2. Inbound synchronisation projects a new Metaverse Object of type User.
3. Attributes flow in: First Name, Last Name, Department, Employee ID.
4. Outbound synchronisation to Active Directory provisions a new Connected System Object.
5. An expression builds her email address: `jane.smith@company.com`.
6. An expression sets the account enabled: `userAccountControl = 512`.
7. Export creates Jane's Active Directory account. Active Directory will not create an enabled account that has no password, so the Synchronisation Rule that provisions it has an [Initial Password](passwords.md#giving-new-connected-system-objects-their-first-password): JIM creates the account disabled and queues her password.
8. The password is set within seconds, which enables the account, and the next import confirms it holds `512`.

## :material-account-switch-outline: Mover

A **mover** event occurs when an existing identity's attributes change: a department transfer, a new title, a name change, an office move, and so on.

### What Happens

1. **Import**<br /> JIM imports the updated record, detecting the changed attributes on the existing Connected System Object.

2. **Inbound synchronisation**<br /> The changed attributes flow to the joined Metaverse Object through the import Attribute Flows. Only changed attributes are processed.

3. **Outbound evaluation**<br /> Export Synchronisation Rules see that the Metaverse Object has changed, and work out whether the change affects their target systems.

4. **Attribute Flow**<br /> Changed attributes flow out to the target Connected System Objects, and expressions are evaluated again: if the department changes, for example, the target OU may need to change too.

5. **Export**<br /> The Pending Exports are sent to the target systems, updating the existing accounts.

### Example

Jane Smith transfers from the Marketing department to Engineering:

1. The HR import detects the department change on Jane's Connected System Object.
2. Inbound synchronisation flows the new department to her Metaverse Object.
3. Outbound synchronisation to Active Directory detects the change.
4. An expression works out her target OU from the new department.
5. Export updates Jane's Active Directory account with the new department, and moves it to the right OU.

### Scoping Changes

A mover event can also change whether an identity is **in scope** for a Synchronisation Rule. For example:

- An employee changes from "Full-Time" to "Contractor", and falls out of scope for the full-time employee Synchronisation Rule and into scope for a contractor one.
- An employee transfers to a department that a particular target system excludes.

When an identity falls out of scope for an export Synchronisation Rule, its account in that rule's target system is **deprovisioned**, as the leaver section describes.

Scope also changes without the record changing. A relative date, such as a leave date, moves an identity across a boundary on its own, and [Temporal Scope Reconciliation](../configuration/schedules.md#built-in-schedules) flags it so the next synchronisation acts. An edited rule moves every identity it newly includes or excludes at the next synchronisation. [Scoping Criteria](../configuration/scoping-criteria.md#what-re-checks-an-object) shows what re-checks an identity, and when.

## :material-account-minus-outline: Leaver

A **leaver** event occurs when an identity is removed, or marked inactive, in the source system, as when HR processes an employee's termination.

### What Happens

1. **Import**<br /> JIM detects the change: the record has been deleted from the source system, so its Connected System Object is **obsoleted**; or a status attribute has changed, such as `employeeStatus` becoming "Terminated".

2. **Inbound synchronisation**<br /> An obsoleted Connected System Object, or one that has fallen out of the import Synchronisation Rule's scope, is **disconnected** from its Metaverse Object (unless the import rule's [Out-of-Scope Action](../configuration/scoping-criteria.md) keeps it joined), and the [Deletion Rules](#deletion-rules) decide what happens to the Metaverse Object. A status change flows in like any other change.

3. **Outbound evaluation**<br /> Each export Synchronisation Rule reacts. A Metaverse Object that is deleted, or falls out of the rule's scope, is **deprovisioned** from that rule's target system; a status change flows out through the rule's expressions, to disable an account, for example.

4. **Export**<br /> The changes are sent to the target systems.

### Deprovisioning

Deprovisioning is removing or releasing an identity's accounts in target systems when they leave. Each export Synchronisation Rule carries a **Deprovisioning Action**, which applies both when a Metaverse Object falls out of the rule's scope and when it is deleted:

| Action | Description |
|--------|-------------|
| **Disconnect** (default) | Unlinks the Connected System Object from the Metaverse Object, and leaves the target account in place, untouched |
| **Delete** | Removes the account from the target system entirely |

The action applies however JIM came to manage the account: accounts JIM provisioned, and existing accounts it matched (joined), are treated the same. So what happens when someone leaves is set per target system.

The Deprovisioning Action is set on each export Synchronisation Rule, and can be reviewed and changed for all of a Metaverse Object Type's export rules at once on its **Downstream Deprovisioning** panel.

### Deletion Rules

Deletion Rules control what happens to the **Metaverse Object** when its Connected System Objects are disconnected:

| Rule | Behaviour |
|------|-----------|
| **Manual** | The Metaverse Object is never deleted automatically; an administrator removes it |
| **When Last Connector Disconnected** | The Metaverse Object is deleted when no Connected System Object remains joined to it |
| **When Authoritative Source Disconnected** | The Metaverse Object is deleted when its selected authoritative source systems disconnect, as the Deletion Trigger sets |

**When Authoritative Source Disconnected** additionally carries a **Deletion Trigger** mode governing how the selected sources trigger deletion:

- **All sources disconnect**<br /> The Metaverse Object is deleted only once no selected source still has a Connected System Object joined to it. Resilient to a single system failing or being rebuilt; the default for new configurations.
- **Specific source(s) disconnect**<br /> The Metaverse Object is deleted when any one of the selected sources disconnects, even if others remain connected.

Target systems that are not selected as sources never block or trigger deletion in either mode. See [Metaverse: Deletion behaviour](../configuration/metaverse.md#deletion-behaviour) for configuration details.

### Grace Periods

JIM supports **grace periods** for deletion. Rather than deleting a Metaverse Object as soon as its Deletion Rule is met, JIM waits for a period you set (30 days, say) first.

Grace periods are valuable for:

- **Rehires**<br /> If an employee leaves and is rehired within the grace period, their identity is restored rather than recreated.
- **Data corrections**<br /> If an employee is removed from the source system by mistake, there is time to correct it before their downstream accounts are affected.
- **Compliance**<br /> Some organisations must keep identity data for a period after someone leaves.

During the grace period, the Metaverse Object stays in the Metaverse, marked for deletion. If the identity reappears before the period expires, the deletion is cancelled and the identity is restored, but only when the reappearance undoes what triggered the deletion. Under **When Last Connector Disconnected**, any system reconnecting cancels it. Under **When Authoritative Source Disconnected**, a reconnection from any selected source cancels it in All sources mode, while in Specific mode only the system whose disconnection scheduled the deletion can; an unrelated system reconnecting does not rescue the object.

## :material-file-document-outline: End-to-End Example

Here is a complete JML lifecycle for one employee.

### Joiner (Day 1)

HR creates a record for Alex Johnson, who starts on Monday.

- The HR import brings Alex into the HR system's Connector Space.
- Inbound synchronisation projects a new Metaverse Object of type User.
- Outbound synchronisation provisions accounts in Active Directory and the email system.
- Export creates the Active Directory account (enabled, in the right OU, with its group memberships) and the mailbox.

### Mover (Month 6)

Alex transfers from Sales to Engineering.

- The HR import detects the department change.
- Inbound synchronisation updates Alex's Metaverse Object with the new department.
- Outbound synchronisation detects the change, and evaluates the Attribute Flows again.
- Export updates Alex's Active Directory account: new department, new OU, new group memberships.

### Leaver (Year 2)

Alex resigns, and HR marks the record as terminated.

- The HR import detects the status change to "Terminated".
- Inbound synchronisation flows the new status to Alex's Metaverse Object.
- Outbound synchronisation evaluates the Attribute Flows again: expressions keyed on the status disable Alex's Active Directory account (`userAccountControl = 514`) and mailbox.
- Export sends those changes to the target systems.
- When HR later removes Alex's record, the import obsoletes it, synchronisation disconnects it, and the Deletion Rule starts a 30-day grace period. If Alex is not rehired by then, the Metaverse Object is deleted, and each target account is deprovisioned by its rule's Deprovisioning Action.
