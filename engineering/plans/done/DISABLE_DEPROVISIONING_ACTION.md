# Disable Deprovisioning Action

- **Status:** Done
- **Note:** Disable for SCIM and SQL, refusing Disable on a group Object Type when a rule is saved, and stamping a Metaverse Object's last-connector date when a disable leaves it with none are deferred; see Not in scope.
- **Issue:** [#2085](https://github.com/TetronIO/JIM/issues/2085) (found blocking [#361](https://github.com/TetronIO/JIM/issues/361), Scenario 036)
- **Created:** 2026-11-22

## Overview

An export Synchronisation Rule's Outbound Deprovision Action gains a third value, **Disable**, beside Disconnect and Delete: when an account leaves the rule's scope, or its Metaverse Object is deleted, the Connector disables the account in the target system and JIM then breaks the join. The Connected System setting that did a weaker version of this, **Delete Behaviour** (and the LDAP Connector's **Disable Attribute**), is removed.

## Why

Delete Behaviour put a deprovisioning decision on the Connected System: the rule said Delete and the setting turned it into a disable. JIM, believing the rule, waited for the account to disappear, which a disabled account never does, so the Delete Pending Export stayed Exported and the account stayed joined. It could not vary by rule, and no integration scenario ever applied it (the AD test configuration declared `DeleteBehaviour = "Disable"`; nothing read it). Scenario 036 found it against a real Entra ID tenant.

JIM has no users yet, so the setting is removed outright rather than deprecated (agreed 2026-11-22); the change is still recorded as breaking.

## Design

1. **`OutboundDeprovisionAction.Disable = 2`.** Conflict precedence across matching export rules: Delete, then Disable, then Disconnect (the strongest wins, as Delete already beats Disconnect).
2. **`PendingExportChangeType.Disable = 3`.** A Connector is told explicitly to disable; nothing reads a setting to reinterpret a Delete. Every switch on `PendingExportChangeType` is audited for the new value.
3. **Staging.** MVO deletion: the CSO is disconnected at once, as for Delete, and a Disable Pending Export is staged against it. Scope-out: a Disable Pending Export is staged and the join kept until it succeeds. Reuse, replace and withdraw follow the Delete rules (#1970): a queued Disable is withdrawn when the rule turns to Disconnect, replaced when it turns to Delete, and cancelled when the object comes back into scope.
4. **Execution.** A Disable that succeeds is complete: the Pending Export is removed and the CSO, if still joined, is disconnected. The account stays in the Connector Space, unjoined; a later import reads it as it is. No confirming import is needed, since there is no generic attribute JIM could confirm it by.
5. **Rehire.** The object comes back into scope; the target system's Object Matching Rule rejoins the disabled account and the export rule's Account Enabled flow re-enables it.
6. **Capability.** `IConnectorCapabilities.SupportsDisable` (a `ConnectorDefinition` column, seeded, exposed on the API and the Connector page). A rule saved with Disable on a Connected System whose Connector cannot disable is refused, on every surface (the application server, so the portal, REST and PowerShell all get it).
7. **How each Connector disables.**
   - LDAP, by directory type, with no setting: Active Directory and Samba AD set the `userAccountControl` ACCOUNTDISABLE bit, keeping the rest; 389 Directory Server sets `nsAccountLock: TRUE`; OpenLDAP sets `pwdAccountLockedTime: 000001010000Z` (a permanent administrative lock, which needs the ppolicy overlay; a directory without it refuses, and the export reports why). A group cannot be disabled, and is refused at export with the remedy.
   - Microsoft Entra ID: `accountEnabled: false` on a user; a group is refused at export.
   - File, SCIM, SQL: `SupportsDisable = false` for now.
8. **Reporting and previews.** Staging reports Deprovision Queued; the export reports Deprovisioned, with the change type shown as Disable. Previews gain "would stage a Disable export" where they name "would stage a Delete export" (Sync Preview, Full Synchronisation preview, the destructive-toggle and scoping Configuration Change Previews, and the change consequences wording).
9. **Surfaces.** Portal: the rule's Deprovisioning radio group and the Metaverse Object Type page's per-rule select offer Disable, disabled with the reason where the Connector cannot. REST: the enum gains the value. PowerShell: `-OutboundDeprovisionAction Disable` on `New-JIMSyncRule`, `Set-JIMSyncRule` and `New-JIMConfigurationChangePreview`.

## Results

Verified against the local Samba AD and OpenLDAP labs (Scenario 004, Test 11b): the leaver's account is disabled (`userAccountControl`, `pwdAccountLockedTime`), kept and disconnected with nothing pending, and a rehire rejoins it, re-enabled on Samba AD. Scenario 036 carries the Entra ID case; it runs in the AD lab.

## Phases ✅

1. Model, capability and migration; rule-save validation.
2. Staging and execution in the engine and export execution, with unit, workflow and real-PostgreSQL tests.
3. Connectors: LDAP and Entra ID disable per change type; Delete Behaviour and Disable Attribute removed.
4. Previews and reporting.
5. Portal, REST, PowerShell; docs (Connector pages, deprovisioning, JML lifecycle, glossary, PowerShell reference) and changelog, including the Breaking changes section.
6. Integration: Scenario 004 gains a Disable leaver and a rehire (Samba AD, AD lab); Scenario 036 moves its disable leaver onto the rule; the unused `DeleteBehaviour` and `DisableAttribute` test settings are removed.

## Not in scope

- Disable for SCIM (`active: false`) and SQL (a named column): later, behind the same capability.
- Refusing Disable on a group Object Type when the rule is saved, rather than at export: needs per-Object-Type knowledge the capability does not carry.
- Stamping a Metaverse Object's last-connector date when completing a Disable leaves it with no joined object (the scope-out Disconnect path does), so a Deletion Rule of When Last Connector Disconnected applies to it. Only affects an object with no source system left joined.
- Re-enabling on OpenLDAP and 389 Directory Server: a rehire is rejoined, but only a rule that flows or clears the lock attribute unlocks the account.
