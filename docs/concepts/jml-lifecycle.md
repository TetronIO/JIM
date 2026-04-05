# JML Lifecycle

The **Joiner/Mover/Leaver** (JML) lifecycle is the foundational automation model for identity management. It describes the three key transitions an identity goes through during its time in an organisation, and how JIM automates the provisioning, updating, and deprovisioning of accounts across connected systems.

## Overview

Every identity follows a predictable lifecycle:

```text
+---------------+       +---------------+       +---------------+
|    Joiner     | ----> |    Mover      | ----> |    Leaver     |
|               |       |               |       |               |
| New identity  |       | Role changes  |       | Identity      |
| appears in    |       | flow through  |       | removed or    |
| source system |       | the metaverse |       | disabled      |
+---------------+       +---------------+       +---------------+
        |                       |                       |
        v                       v                       v
   Provision             Update accounts          Deprovision
   accounts              across systems           accounts
```

JIM handles each phase through its [sync rules](sync-rules.md) and [sync pipeline](sync-pipeline.md), applying the appropriate actions automatically based on your configuration.

## Joiner

A **joiner** event occurs when a new identity appears in a source system for the first time. For example, when HR creates a record for a new employee.

### What Happens

1. **Import** -- JIM imports the new record from the source system, creating a new CSO in the connector space.

2. **Join attempt** -- During inbound sync, JIM evaluates the sync rule's join rules to check whether this identity already exists in the metaverse (e.g., a rehire or a record that arrived from another source system).

3. **Projection** -- If no existing MVO is found, JIM **projects** a new MetaverseObject. This is the birth of the identity in JIM's metaverse.

4. **Inbound attribute flow** -- The new employee's attributes (name, department, employee ID, etc.) flow from the CSO into the newly created MVO.

5. **Outbound evaluation** -- Outbound sync rules evaluate the new MVO. If the MVO is in scope for any outbound rules, JIM **provisions** new CSOs in the target connected systems.

6. **Export** -- The provisioned CSOs are exported to the target systems, creating the actual accounts (e.g., an Active Directory user account, an email mailbox).

### Example

A new employee, Jane Smith, is added to the HR system:

1. HR import creates a CSO with Jane's details
2. Inbound sync projects a new MVO of type "Person"
3. Attributes flow: First Name, Last Name, Department, Employee ID
4. Outbound sync to Active Directory provisions a new CSO
5. Expression builds her email: `jane.smith@company.com`
6. Expression sets account to enabled: `userAccountControl = 512`
7. Export creates Jane's AD account

## Mover

A **mover** event occurs when an existing identity's attributes change. This covers any mid-lifecycle change: department transfers, title changes, name changes, office relocations, and so on.

### What Happens

1. **Import** -- JIM imports the updated record, detecting the changed attributes on the existing CSO.

2. **Inbound sync** -- The changed attributes flow from the CSO to the linked MVO through the inbound attribute flow rules. Only changed attributes are processed.

3. **Outbound evaluation** -- Outbound sync rules detect that the MVO has changed and evaluate whether the changes affect any target systems.

4. **Attribute flow** -- Changed attributes flow outbound to the target CSOs. Expressions are re-evaluated (e.g., if the department changes, the target OU may need to change).

5. **Export** -- Pending exports are sent to the target systems, updating the existing accounts.

### Example

Jane Smith transfers from the Marketing department to Engineering:

1. HR import detects the department change on Jane's CSO
2. Inbound sync flows the new department value to her MVO
3. Outbound sync to Active Directory detects the change
4. Expression re-evaluates her target OU based on the new department
5. Export updates Jane's AD account with the new department and moves her to the correct OU

### Scoping Changes

A mover event can also change whether an identity is **in scope** for a particular sync rule. For example:

- An employee changes from "Full-Time" to "Contractor" -- they may fall out of scope for the full-time employee sync rule and into scope for a contractor sync rule
- An employee transfers to a department that is excluded from a particular target system

When an identity falls out of scope for an outbound sync rule, this triggers **deprovisioning** behaviour for that specific target system (see Leaver below).

## Leaver

A **leaver** event occurs when an identity is removed or marked as inactive in the source system. For example, when HR processes an employee's termination.

### What Happens

1. **Import** -- JIM detects the change. This could be:
   - The record is deleted from the source system (the CSO is **obsoleted**)
   - A status attribute changes (e.g., `employeeStatus` changes to "Terminated")
   - The record falls out of scope for the inbound sync rule

2. **Inbound sync** -- The CSO is **disconnected** from the MVO. What happens next depends on the configured **deletion rules**.

3. **Outbound evaluation** -- If the MVO is disconnected or deleted, outbound sync rules trigger **deprovisioning** in target systems.

4. **Export** -- Deprovisioning actions are sent to the target systems.

### Deprovisioning

Deprovisioning is the process of disabling or removing accounts in target systems when an identity leaves. JIM supports several deprovisioning strategies:

| Strategy | Description |
|----------|-------------|
| **Disable** | Disable the account in the target system (e.g., set `userAccountControl` to 514 in Active Directory) |
| **Delete** | Remove the account from the target system entirely |
| **Disconnect** | Unlink the CSO from the MVO but leave the target account unchanged |

The strategy is configured per outbound sync rule, giving you fine-grained control over what happens in each target system when an identity leaves.

### Deletion Rules

Deletion rules control what happens to the **metaverse object** when connected system objects are disconnected:

| Rule | Behaviour |
|------|-----------|
| **Manual** | The MVO is never automatically deleted -- an administrator must manually remove it |
| **When Last Connector Disconnected** | The MVO is deleted when no CSOs remain connected to it |
| **When Authoritative Source Disconnected** | The MVO is deleted when the CSO from its authoritative source system is disconnected |

### Grace Periods

JIM supports **grace periods** for deletion. Rather than deleting an MVO immediately when the deletion rule is triggered, JIM waits for a configurable period (e.g., 30 days) before executing the deletion.

Grace periods are valuable for:

- **Rehires** -- if an employee leaves and is rehired within the grace period, their identity is restored rather than recreated
- **Data corrections** -- if an employee is accidentally removed from the source system, there is time to correct the error before downstream accounts are affected
- **Compliance** -- some organisations require identity data to be retained for a period after departure

During the grace period, the MVO remains in the metaverse but is marked for pending deletion. If the identity reappears in the source system before the grace period expires, the deletion is cancelled and the identity is restored.

## End-to-End Example

Here is a complete JML lifecycle for an employee:

### Joiner (Day 1)

HR creates a record for Alex Johnson, starting on Monday.

- HR import brings Alex into the connector space
- Inbound sync projects a new MVO of type "Person"
- Outbound sync provisions accounts in Active Directory and the email system
- Export creates the AD account (enabled, correct OU, group memberships) and email mailbox

### Mover (Month 6)

Alex transfers from Sales to Engineering.

- HR import detects the department change
- Inbound sync updates Alex's MVO with the new department
- Outbound sync detects the change and re-evaluates attribute flows
- Export updates Alex's AD account: new department, new OU, new group memberships

### Leaver (Year 2)

Alex resigns and HR marks the record as terminated.

- HR import detects the status change to "Terminated"
- Inbound sync disconnects Alex's HR CSO from the MVO
- The deletion rule triggers with a 30-day grace period
- Outbound sync disables Alex's AD account and email
- Export sends the disable operations to the target systems
- After 30 days (if Alex is not rehired), the MVO and remaining CSOs are deleted
