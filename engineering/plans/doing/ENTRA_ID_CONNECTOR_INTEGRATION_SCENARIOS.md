# Microsoft Entra ID Connector Integration Scenarios

- **Status:** Doing (Phases 1 to 3 complete; Scenario 038, hybrid, outstanding under #3029)
- **Issue:** [#361](https://github.com/TetronIO/JIM/issues/361) (Phase 9 of the Connector's design plan)
- **Related Issues:** [#3028](https://github.com/TetronIO/JIM/issues/3028) (import limited to Administrative Units, a possible later add-on), [#3029](https://github.com/TetronIO/JIM/issues/3029) (Scenario 038)
- **Related Plans:** [`MICROSOFT_GRAPH_CONNECTOR_DESIGN.md`](MICROSOFT_GRAPH_CONNECTOR_DESIGN.md), whose Testing Strategy this replaces in detail
- **Created:** 2026-11-22

## Overview

Integration scenarios that run the JIM Microsoft Entra ID Connector against a real tenant, in both directions:

1. **Entra ID as a target** (Scenario 036): JIM provisions, updates, disables and deletes users and groups in Entra ID.
2. **Entra ID as a source** (Scenario 037): JIM imports users and groups from Entra ID and projects them into the Metaverse.
3. **Hybrid and Source of Authority** (Scenario 038, later): objects synchronised from Active Directory by Entra Cloud Sync, whose Source of Authority is switched both ways.

Each scenario then adds the cases that break naive synchronisation: accounts that must never be touched (the tenant's administrators), guests, nested and hidden-membership groups, members outside scope, deletions, and Graph's eventual consistency.

## Business Value

Unit tests run every Graph call against a stateful fake tenant, which proves JIM's logic against what we believe Graph does. The first run against the real tenant (2026-11-22) found two bugs the fake could not: Graph refusing a certificate end date with fractional seconds, and a `-WhatIf` preview reading nothing back. Graph's real behaviour (validation, eventual consistency, sensitive-action rules on privileged accounts, delta semantics) is only observable live, and customers will run this Connector against production tenants.

## Decisions (agreed 2026-11-22)

1. **CI never grants permissions.** The scenarios use a permanent, pre-made app registration for the Connector, created once by an administrator with `Register-JIMEntraIdConnector`. No CI credential holds `AppRoleAssignment.ReadWrite.All`. The setup command stays covered by Pester and a manual run at release.
2. **Hybrid goes in a second, dedicated tenant**, after Scenarios 036 and 037. Turning on directory synchronisation is hard to reverse (switching it off takes up to 72 hours and leaves objects behind), and `tetronjim` also serves the Entra ID sign-in checks (Scenario 035).
3. **Administrators are excluded by Synchronisation Rule scoping for now.** The Connector has no import filter: it reads every user in the tenant. Limiting import to Administrative Units is a possible later add-on, [#3028](https://github.com/TetronIO/JIM/issues/3028).

4. **No stored credential for the Connector app.** Scenario 035 shows the way, already working in this tenant: its CI app signs in with GitHub's OIDC token through a federated credential, holds only `Application.ReadWrite.OwnedBy`, and owns the registration it manages, replacing that registration's credential on every run. The same here:
   - The CI app is made an owner of the Connector's app registration and its service principal, once, by an administrator.
   - Each run, the CI app gives the Connector app a fresh certificate, hands it to JIM, and removes the previous one. Nothing long-lived exists to leak, and nothing needs renewing by hand.
   - `Application.ReadWrite.OwnedBy` manages credentials of owned applications but cannot grant Microsoft Graph permissions, so decision 1 still holds.
   - Where its read paths work under that permission, the rotation is `Register-JIMEntraIdConnector -AccessToken <CI token> -ResetCertificate`, which also exercises the setup command's repair path in CI. If the tenant has drifted so that a permission needs granting, the run fails, which is the right signal.
5. **No Administrative Units** (design plan decision 20). Password management assigns User Administrator across the tenant, so the scenarios need no Administrative Unit, and the users JIM creates are covered by the role like any other.

## Technical Architecture

### Identities

| Identity | Signs in with | Holds | Used for |
|---|---|---|---|
| CI app (existing) | GitHub OIDC, federated credential | `Application.ReadWrite.OwnedBy` | Rotating the Connector app's certificate each run |
| Connector app "JIM Entra ID Connector (CI)" (new, permanent) | The certificate minted for this run | `User.ReadWrite.All`, `Group.ReadWrite.All`, `Member.Read.Hidden`, `User-PasswordProfile.ReadWrite.All`; User Administrator across the tenant | JIM's Connected System, and the harness's own Graph reads and writes |
| Administrator | Interactive, once | Global Administrator | Creating the Connector app and its role, and making the CI app an owner |

The harness seeds and checks data as the Connector app, but through its own Graph requests written in PowerShell, not through JIM. A mistake mirrored in JIM's import and export (an attribute mapped wrongly both ways) would cancel out if JIM created the data it then read. Writing it independently catches that.

### Harness

A new `test/integration/utils/EntraIdConnectorLab-Helpers.ps1`, with Pester tests beside it like `EntraIdLab-Helpers.Tests.ps1`:

- **Token:** a certificate client assertion built with the .NET base class library (no Graph SDK or MSAL). `Register-JIMEntraIdConnector` hands its certificate's private key only to JIM, so the harness adds a short-lived certificate of its own to the Connector app (named "JIM integration harness", valid two days), signs in with it, and removes it at the end; the next run removes one a failed run stranded. The setup command manages only certificates named after the app, so neither touches the other's.
- **Run prefix:** every object the run creates is named `jimit-<run id>-<name>` (UPN, `mailNickname`, display name), so runs never collide and cleanup never reaches anything else.
- **Waiting:** Graph is eventually consistent. A read straight after a write can miss it, and a delta query can lag. Every wait polls Graph until the expected state is visible or a timeout expires, never a fixed sleep. Before a Delta Import, the harness waits until a direct read shows the change; if the Delta Import still misses it, one bounded retry follows, and the lag is logged.
- **Snapshot of untouchable accounts:** before the run, the harness records every property of each account it did not create (the tenant's administrators and Scenario 035's test people). After the run it compares them, and any difference fails the run.
- **Cleanup:** delete everything carrying the run's prefix at the end, and at the start sweep anything with the scenario prefix older than 24 hours, left by a failed run.

### Deleted objects

Entra ID keeps a deleted user or group for 30 days, holding its UPN and counting in full towards the tenant's object quota while it can be restored (Microsoft's service limits page; 50,000 for a tenant with no verified domain, as `tetronjim` is). At about 40 objects a nightly run, deleted objects peak at about 1,200, well within it. Unique per-run names mean a soft-deleted object never blocks a later run. Permanent deletion needs a further permission and is deferred to the scale test, which would otherwise fill the quota.

### Where it runs

In the AD lab (`ad-lab.yml`, the `jim-ad-lab` environment), as its own harness step after the suite like Scenario 035, posting a commit status of its own, `jim-entra-connector`. An Entra ID outage then never colours the rest of the lab. It becomes a release gate (`JIM_ENTRA_CONNECTOR_GATE_ENFORCED`) only after a run of green nights, like the other gates. It also runs from the devcontainer, given a Graph token for the CI app in an environment variable, as Scenario 035 does.

## Scenarios

### Scenario 036: Entra ID as a target

Source: a CSV HR source projecting people and their departments, as Scenario 001 does; a group per department, with one nested group.

1. **Provision:** a Full Synchronisation and an Export create the users and groups. A confirming import, then the harness's own Graph reads, check every exported attribute and membership.
2. **Initial Password:** each new user's Initial Password is set through the password channel after the create, and the harness signs nobody in (the tenant's sign-in policy is Scenario 035's concern); it checks `passwordProfile` was accepted and the Pending Export completed.
3. **Update:** an attribute change, a UPN change, and a manager reference.
4. **Membership:** add and remove members; add a member beyond the 20 Graph takes on a create.
5. **Leaver:** with the rule's Deprovisioning Action Disable, a leaver is disabled and disconnected (#2085); with Delete, deleted, and the harness finds it in deleted items.
6. **Idempotence:** a second synchronisation and export with nothing changed produce no Pending Exports.
7. **Untouchable accounts:** the tenant's own accounts are imported, out of every rule's scope, and the snapshot comparison shows them unchanged.

Not tested live: a deliberate write to an administrator. Graph refuses sensitive actions (disabling, renaming, password, deletion) on privileged accounts even with `User.ReadWrite.All`, but a property that is not sensitive (a job title) would be written, and the tenant's administrators are shared with Scenario 035. That refusal stays covered by the Connector's unit tests of error classification.

### Scenario 037: Entra ID as a source

The harness seeds, through Graph: member users, a guest, security groups (one nested), a Microsoft 365 group with hidden membership, and a group with a member outside scope (one of the tenant's own accounts).

1. **Full Import and projection:** a Full Import, then synchronisation projects the run's objects into the Metaverse. The check covers attributes, `sourceOfAuthority` (`cloud`, and `externalEntraDirectory` for the guest), memberships, the nested group, and the hidden-membership group's members read through `Member.Read.Hidden`.
2. **Scoping:** projection is scoped to the run's prefix, and the tenant's own accounts never project.
3. **Delta Import:** the harness changes an attribute, adds and removes a member, and deletes a user and a group. A Delta Import picks each up, and the Metaverse follows its deletion rules.
4. **Delta integrity:** a Delta Import with nothing changed reads nothing, with the delta link reused (`readEveryObjectInScope=False`).

A true invited guest (`creationType` `Invitation`) needs `User.Invite.All`, which the Connector app does not hold, so the guest case uses a user created with `userType` `Guest`; the Connector reads either as `externalEntraDirectory`.

### Scenario 038: hybrid and Source of Authority (later)

In a second tenant, with the Entra Cloud Sync agent on the AD lab's Windows domain controller (outbound connections only):

1. Objects created in the lab's Active Directory arrive in Entra ID with `onPremisesSyncEnabled` true and read as `onPremisesDirectory`.
2. Export to such an object is refused under the Connector's protection, and reported as an error.
3. With Track Source of Authority Changes on: a user and a group switched to cloud management (`isCloudManaged` true) read as `cloud` after a Delta Import, which falls back to a full read when the set changes; switched back, they read as `onPremisesDirectory` again. This answers the open question of what `onPremisesSyncEnabled` reads after a switch back.

Tracked by [#3029](https://github.com/TetronIO/JIM/issues/3029), since it needs new infrastructure.

## Implementation Phases

### Phase 1: Tenant, identities and harness ✅

- ✅ The Connector app "JIM Entra ID Connector (CI)" was created on 2026-11-22 with `Register-JIMEntraIdConnector -PasswordManagement`: four Microsoft Graph permissions and User Administrator across the tenant. The CI app owns the app and its service principal, and a `-WhatIf` re-run found nothing to change. The run found two bugs in the setup command, both fixed: a new service principal Graph did not yet know (`Authorization_IdentityNotFound`) was not waited out, and existing certificates were sent back without `customKeyIdentifier` (`KeyNotUpdatable`).
- ✅ `Application.ReadWrite.OwnedBy` covers the rotation, by Microsoft's permissions reference: it lists every application and service principal, reads an owned service principal's app role assignments, and changes an owned app's credentials. Without `-PasswordManagement` the setup command reads no role assignments, so the CI run needs nothing more. The first AD lab run confirms it.
- ✅ Deleted objects count towards the quota (see Deleted objects).
- ✅ The harness helpers, `test/integration/utils/EntraIdConnectorLab-Helpers.ps1`, with Pester tests beside them.
- The scenarios registered with the runner, and the `ad-lab.yml` step and its `jim-entra-connector` status, arrive with Scenario 036, the first thing they run.

### Phase 2: Scenario 036 ✅

The scenario, its registration with the runner, and the `ad-lab.yml` step and status. It passed end to end in the AD lab on 2026-11-23, after fixes the live runs found: a retried Create no longer duplicates an object, a refused certificate is asked about again while Entra ID applies it, a Disable resolves the references other exports still owe its account, and removing a member deleted from the tenant counts as done. `entra-connector.yml` runs both scenarios from any branch on a GitHub-hosted runner, in the approval-gated `jim-entra-tenant` environment, so a Connector change can be run live before it merges.

### Phase 3: Scenario 037 ✅

The scenario, its registration with the runner, and the `ad-lab.yml` step, which runs Scenarios 036 and 037 one after the other (each replaces the certificate JIM holds) and reports both as `jim-entra-connector`. A tenant that refuses the Microsoft 365 group with hidden membership skips that check and says so. It passed in the AD lab on its first run, on 2026-11-23, the hidden-membership group included.

### Phase 4: Scenario 038 ([#3029](https://github.com/TetronIO/JIM/issues/3029))

Written, not yet run: it waits for the hybrid forest and tenant. Checked against Microsoft's documentation (2026-11-24): Cloud Sync from Active Directory and the Source of Authority switch both need only Entra ID Free, and no custom domain is needed (an unverified UPN suffix becomes the tenant's `onmicrosoft.com`). The agent runs on a domain-joined Windows Server with Desktop Experience, a domain controller included, and needs outbound HTTPS to Microsoft, which the lab's `Lab` switch does not give; switching a user back needs the tenant's `blockCloudObjectTakeoverThroughHardMatchEnabled` turned off once. The forest, `dc-hybrid`, is never reverted, since a revert would undo what Cloud Sync has copied and the agent's credentials. The setup is in `test/integration/ad-lab/README.md`, The hybrid forest; the scenario reports as `jim-entra-hybrid`.

### Phase 5: scale ([#3053](https://github.com/TetronIO/JIM/issues/3053))

Decided 2026-11-23: test up to the tenant's limit, in two parts.

**Part 1: Scenario 039, against the test tenant.** Implemented.
- **Shape:** the template's population is provisioned, changed, partly deprovisioned and imported again, and every step is timed.
- **Sizing:** the run first checks the template fits in the tenant's object limit with a tenth free.
- **Cleanup:** the CI app purges the run's users afterwards. It was granted `User.DeleteRestore.All` and `Organization.Read.All` for this.
- **The tenant:**
  - Its limit is 300,000 objects, after `tetronjim.tetron.io` was verified. A subdomain of a domain verified in another tenant can be verified with a TXT record of its own.
  - Measured: a purged user still counts for a quarter for 30 days.
  - Groups cannot be purged with an application permission, so runs keep groups to the template's few hundred.
- **Running it:** on request only, from `entra-connector.yml`.

**Part 2: Scenario 040, against a stand-in Microsoft Graph service, at every template up to Scale1m.** Implemented. It measures JIM reading a large tenant.
- **Reading, not writing (changed 2026-11-24):** the Connector writes no faster than Graph's 20 writes a second per application whatever answers it, so provisioning a million users would take days against any tenant. The stand-in is seeded in process with Scenario 039's population, and the scenario measures Full Import, Delta Import and their synchronisations, the shape a customer connecting an existing large tenant meets. Writing at scale stays Scenario 039's, against the real tenant.
- **How JIM reaches it:** the integration network resolves `login.microsoftonline.com` and `graph.microsoft.com` to the stand-in, whose certificate JIM's certificate store trusts, so JIM runs unchanged.
- **One implementation:** the stand-in (`test/JIM.TestGraphService`) serves `MockGraphTenant`, the unit tests' simulated tenant, indexed for a million objects, as the SCIM test provider shares `MockScimProvider` with the unit suite.

## Success Criteria

- Scenarios 036 and 037 pass three nights running in the AD lab, and their failures name the step and the Graph response.
- No run changes an object it did not create (the snapshot comparison).
- No run leaves objects behind for more than 24 hours.
- No credential is stored for the Connector app.

## Dependencies

- No new packages: the harness uses `Invoke-RestMethod` and the .NET base class library.
- For Scenario 038: a second tenant, and the Cloud Sync agent on the lab's domain controller.

## Risks and Mitigations

| Risk | Mitigation |
|---|---|
| Graph lag makes the scenarios flaky | Poll for visible state with timeouts; one bounded Delta retry, with the lag logged |
| A run corrupts the shared tenant | Per-run prefix; snapshot comparison of everything else; cleanup and a leftover sweep |
| A Graph or tenant outage fails the lab | Its own status, outside the suite, as Scenario 035 does |
| Throttling | The Connector's write pacing; the scenarios stay small |
| The tenant drifts (a permission removed by hand) | The per-run repair fails loudly and says what drifted |
