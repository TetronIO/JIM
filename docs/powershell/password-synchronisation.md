---
title: Password Synchronisation
description: "The cmdlets that set a Metaverse Object's password, and configure which Connected Systems receive synchronised passwords."
type: reference
audience: automation-author
---

# Password Synchronisation

These cmdlets set a Metaverse Object's password, and configure which Connected Systems receive passwords through [Password Synchronisation](../concepts/password-synchronisation.md) and how hard JIM tries to deliver them. Reading the queue of password changes on their way to your Connected Systems, and releasing or cancelling stuck ones, is on [Password Queue](password-queue.md).

```jim-diagram
archetype: comparison
title: The two ways Set-JIMMetaverseObjectPassword is aimed
desc: "With -ConnectedSystemId, the reset case, where you chose the password for the person, the password goes to the Metaverse Object's Connected System Objects in the systems named, and the command refuses, setting nothing, where one is missing. It waits up to 10 seconds by default, requires a change at the next sign-in by default, and delivers even to a system whose Password Synchronisation is switched off. Without it, the event case, where the password changed somewhere, it goes to every Connected System configured for Password Synchronisation. It returns at once by default, leaves the password to expire by each target's own policy, and holds a change for a switched-off system until it is switched on. Either way, -Password supplies the password, or -Generate has JIM make one, returned once on GeneratedPassword."
columns:
  - { label: "With `-ConnectedSystemId`", sub: "the reset case: you chose the password" }
  - { label: Without it, sub: "the event case: it changed somewhere" }
rows:
  - label: Goes to
    cells:
      - { text: the Connected System Objects in the systems named, sub: "refused, setting nothing, where one is missing" }
      - { text: every Connected System configured for Password Synchronisation }
  - label: Defaults
    cells:
      - { text: "`RequireChangeAtNextSignIn`", sub: "as `-ExpiryBehaviour`; waits up to 10 seconds" }
      - { text: "`ExpiresAccordingToTargetPolicy`", sub: "as `-ExpiryBehaviour`; returns at once" }
  - label: System switched off
    cells:
      - { text: still delivered to }
      - { text: held until it is switched on }
footer: "Either way, `-Password` supplies it, or `-Generate` makes one, returned once on `GeneratedPassword`"
```

!!! note "No password you supplied is ever returned"
    Nothing here returns a password you supplied, in any form: the queued value is encrypted in the database and has no representation in any response. The one password returned is one JIM generated for `Set-JIMMetaverseObjectPassword -Generate`, once, to the caller who asked for it.

---

## :material-form-textbox-password: Set-JIMMetaverseObjectPassword

Sets a Metaverse Object's password, on the Connected System Objects you name or on every Connected System configured for Password Synchronisation. `Set-JIMConnectedSystemObjectPassword` ([Connected Systems](connected-system-objects.md#set-jimconnectedsystemobjectpassword)) is the same operation with one Connected System Object named; both return the same outcome for each target.

One command, aimed one of two ways. With `-ConnectedSystemId`, the password goes to the Metaverse Object's Connected System Objects in those systems: the reset case, where you chose the password for the person. Without it, the password goes to every Connected System configured for [Password Synchronisation](../concepts/password-synchronisation.md) in which the Metaverse Object has a Connected System Object: the event case, where their password changed somewhere and the rest should hold it. Both go through the same queue and the same [Password Delivery Service](../concepts/password-synchronisation.md#the-password-delivery-service), which makes the first attempt within about a second, whatever the synchronisation engine is doing; what differs is the defaults, set out under Parameters.

Supply the password with `-Password`, or have JIM generate one that satisfies the discovered policy of every Connected System the Metaverse Object has a Connected System Object in with `-Generate`. A generated password is returned to you, once, on `GeneratedPassword`; JIM holds its own copy, encrypted, only until the systems have it.

!!! tip "Use `-Generate` rather than choosing a password yourself"
    One password has to satisfy the strictest of several systems at once, and their password policies are not
    something you can see in order to reason about them. JIM can: `-Generate` has it reconcile the discovered
    policies of every Connected System the password is going to (the longest minimum length any of them demands,
    and only the character categories all of them count) and produce a password that satisfies all of them.

    Where no single password can satisfy them all, JIM refuses outright rather than handing back one that would
    be accepted on the first Connected System Object and refused on the second, after the first has already changed. A system
    JIM could read no policy from is reported as a warning, because the password is about to go there and JIM
    cannot promise it will be accepted.

    The generated password is returned on the result's `GeneratedPassword` property as a SecureString.
    **That is the only chance to capture it.**

```powershell title="Set one compliant password across two named systems, and read it back"
$result = Set-JIMMetaverseObjectPassword -Id 8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f -ConnectedSystemId 1,2 -Generate -Force
ConvertFrom-SecureString -SecureString $result.GeneratedPassword -AsPlainText
```

<!-- BEGIN GENERATED: Set-JIMMetaverseObjectPassword -->
### Syntax

```powershell
# PropagateSuppliedPassword (default)
Set-JIMMetaverseObjectPassword -Id <guid> -Password <SecureString> [-ExpiryBehaviour <string>] [-Wait <int>] [-Force] [-WhatIf] [-Confirm]

# NamedGeneratedPassword
Set-JIMMetaverseObjectPassword -Id <guid> -ConnectedSystemId <int[]> -Generate [-ExpiryBehaviour <string>] [-EnableAccount] [-Wait <int>] [-Force] [-WhatIf] [-Confirm]

# NamedSuppliedPassword
Set-JIMMetaverseObjectPassword -Id <guid> -ConnectedSystemId <int[]> -Password <SecureString> [-ExpiryBehaviour <string>] [-EnableAccount] [-Wait <int>] [-Force] [-WhatIf] [-Confirm]

# PropagateGeneratedPassword
Set-JIMMetaverseObjectPassword -Id <guid> -Generate [-ExpiryBehaviour <string>] [-Wait <int>] [-Force] [-WhatIf] [-Confirm]
```

It asks for confirmation before it acts: `-WhatIf` shows what it would do, and `-Force` or `-Confirm:$false` skips the prompt.

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes |  | The unique identifier (GUID) of the Metaverse Object whose password this is. Accepts pipeline input by property name. |
| `ConnectedSystemId` | `int[]` | Yes (NamedGeneratedPassword, NamedSuppliedPassword) |  | The Connected Systems to set the password in. The person must have an account in every one named; the command refuses, and sets nothing, where they do not. Omit it to propagate the password to every Connected System configured for Password Synchronisation instead. |
| `Password` | `SecureString` | Yes (PropagateSuppliedPassword, NamedSuppliedPassword) |  | The password, as a SecureString. Encrypted before JIM stores it and held only until delivered; never logged, never returned, and never recorded on an Activity. |
| `Generate` | `switch` | Yes (NamedGeneratedPassword, PropagateGeneratedPassword) |  | Has JIM generate one password that satisfies every Connected System the person has an account in, instead of you supplying one. With `-ConnectedSystemId`, only the systems named count. JIM reconciles their discovered policies (the longest minimum length any of them demands, and only the character categories all of them count) and refuses outright where no single password can satisfy them all, rather than handing back one accepted by the first system and refused by the second. A system JIM could read no policy from is reported as a warning, because the password is about to go there and JIM cannot promise it will be accepted. The generated password is returned on the result's GeneratedPassword property as a SecureString. That is the only chance to capture it. |
| `ExpiryBehaviour` | `string` | No |  | What happens to the password once each target has it. Defaults to RequireChangeAtNextSignIn with `-ConnectedSystemId` (somebody else chose this password) and to ExpiresAccordingToTargetPolicy without it (the person chose it, and should not be made to choose another). On Active Directory and Samba AD, with `-ConnectedSystemId`, NeverExpires sets Password never expires on each account and the other two clear it; a Synchronisation Rule that writes the account's userAccountControl with Enforce State on puts its value back at the next synchronisation. Without `-ConnectedSystemId`, Password never expires is left as each account has it, so NeverExpires is refused. One of `RequireChangeAtNextSignIn`, `ExpiresAccordingToTargetPolicy` or `NeverExpires`. |
| `EnableAccount` | `switch` | No (NamedGeneratedPassword, NamedSuppliedPassword) |  | Enables the named accounts as part of setting the password. Omit it to leave their enabled state as it is, with two exceptions: an Active Directory account whose enable JIM held back until it had a password, which the password enables as its Attribute Flow asks; and an account still waiting for its first password, which the password then is, enabled as its Synchronisation Rule's Initial Password says. Only available with `-ConnectedSystemId`: a password propagated to every configured system enables no account other than those two kinds, because it reaches accounts an administrator may have disabled on purpose. |
| `Wait` | `int` | No |  | How many seconds, from 0 to 30, to wait for the systems to answer before returning. Defaults to 10 with `-ConnectedSystemId` and to 0 without it. A wait ends early once every target has settled; a target still Queued or Delivering when it runs out is reported as such, with Settled false, and its outcome appears on the Activity and the person's Password tab once it lands. The ceiling is the API's; a script that needs to watch for longer should poll `Get-JIMPendingPasswordChange -MetaverseObjectId` instead. From 0 to 30. |
| `Force` | `switch` | No |  | Skips the confirmation prompt. |

### Output

One object describing the change: the Activity that records it, and where delivery to each Connected System stands. No property carries the password you supplied.

| Property | Type | Description |
|----------|------|-------------|
| `ActivityId` | `guid` | The Activity recording the change. It is the durable record, outliving the queue, and is where the per-system outcomes appear once delivery has been attempted. |
| `Origin` | `string` | Which way the change was aimed: `Explicit` at the accounts the caller named, or `Propagated` to every Connected System configured for Password Synchronisation. One of `Propagated`, `Explicit` or `Provisioned`. |
| `Targets` | `object[]` | One entry per Connected System the change was queued for. Each has `ConnectedSystemId`, `ConnectedSystemName`, `Enabled`, `ConnectedSystemObjectId`, `State`, `NextAttemptAt`, `Message`, `AttemptCount`, `FailureReason` and `CompletedWithWarning`. |
| `QueuedForNoSystems` | `bool` | True when a propagated change found no Connected System configured for Password Synchronisation, so nothing was queued. Always false for named accounts, which are refused rather than queued for nothing. |
| `Settled` | `bool` | True when no target is still Queued or Delivering: every one has been set, is retrying on its own clock, is parked waiting on a person, is held behind a switched-off system, or has expired. A change queued for no systems is settled. |
| `GeneratedPassword` | `securestring` | The password JIM generated, present only with `-Generate`. This is the only chance to read it. |
<!-- END GENERATED: Set-JIMMetaverseObjectPassword -->

Each entry in `Targets`:

| Property | Description |
|----------|-------------|
| `ConnectedSystemId`, `ConnectedSystemName` | Where it is going. |
| `ConnectedSystemObjectId` | The Connected System Object the password is aimed at, or `$null` where the Metaverse Object has none in this system yet; a propagated change is queued regardless, bounded by its time to live, so it lands when provisioning catches up. |
| `Enabled` | Whether the system is currently taking propagated passwords. `$false` on a propagated change means it is held until somebody switches the system on; a named Connected System Object is delivered to either way. |
| `State` | `Queued`, `Delivering`, `Set`, `Retrying`, `Parked`, `Held`, `Expired` or `Cancelled`. |
| `NextAttemptAt` | When the next attempt falls due, for a target that is `Retrying`; `$null` otherwise. |
| `Message` | The system's own words on its most recent outcome (why it refused, or that the password was set), or `$null` before anything has been said. |
| `AttemptCount` | How many delivery attempts this system has had. |
| `FailureReason` | Why the most recent attempt failed: `Transient`, `ConfigurationFault`, `PolicyRejection`, `TargetObjectNotFound` or `UnsupportedOperation`. Empty before any attempt and once the password is set. |
| `CompletedWithWarning` | `$true` where the password was set but the system could not apply everything asked of it, such as a change at the next sign-in on OpenLDAP or 389 Directory Server, which leave expiry to their own policy. `State` is still `Set`, and `Message` says what was applied instead. |

A `Parked` target is also reported as a non-terminating error carrying the result as its `TargetObject`, so a script that stops on errors stops on a refusal and can still read the other targets from the exception it caught. A target that is `Set` with `CompletedWithWarning` is reported as a warning in its `Message`'s words, and so is a target still in flight when a wait ran out.

### Examples

```powershell title="Reset a password in two named Connected Systems and see what each did with it"
$password = Read-Host -AsSecureString "New password"
$result = Set-JIMMetaverseObjectPassword -Id 8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f -ConnectedSystemId 1,2 -Password $password
$result.Targets | Select-Object ConnectedSystemName, State, Message
```

```powershell title="Record a password change and return at once"
$password = Read-Host -AsSecureString "New password"
Set-JIMMetaverseObjectPassword -Id 8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f -Password $password -Force
```

```powershell title="Propagate, wait up to ten seconds, and report which systems took the password"
$result = Set-JIMMetaverseObjectPassword -Id $id -Password $password -Wait 10 -Force
$result.Targets | Select-Object ConnectedSystemName, State, Message
if (-not $result.Settled) {
    Write-Warning "Not every system had answered after 10 seconds; check the Metaverse Object's Password tab."
}
```

A service desk script uses this to tell the caller their reset has landed before they hang up: `State` is `Set` where it has, `Retrying` with a `NextAttemptAt` where a directory was unreachable, and `Parked` with the directory's own `Message` where it refused.

```powershell title="Catch the case where nothing was queued"
$result = Set-JIMMetaverseObjectPassword -Id $id -Password $password -Force
if ($result.QueuedForNoSystems) { Write-Warning "No system is configured to receive this Metaverse Object's password changes." }
```

```powershell title="Set one generated password on every Connected System Object the Metaverse Object has, whatever is configured"
$systems = (Get-JIMMetaverseObject -Id $id).ConnectedSystemObjects.ConnectedSystemId
Set-JIMMetaverseObjectPassword -Id $id -ConnectedSystemId $systems -Generate -Force
```

### Notes

- **This resets the passwords on whichever Connected System Objects you point it at.** Anyone who can call it can reset the password of any Connected System Object in these Connector Spaces, subject only to what each Connected System's service account is permitted to do.
- Each Connected System is delivered to on its own. One refusing does not stop the others, and the person is left with a different password there until you deal with it, so check every target rather than the first.
- A refused password will be refused again if you resend it. Generate a different one, and set it on every Connected System Object rather than only the one that failed, or the person ends up with two.
- A named system whose Password Synchronisation is switched off is still delivered to; the switch governs propagated changes, and you named the Connected System Object. A propagated change to that system is held until it is switched on.

---

## :material-shield-lock-outline: Get-JIMConnectedSystemPasswordPolicy

Gets what a Connected System said it will accept, as JIM read it during a previous connection. It opens no new
connection and changes nothing.

<!-- BEGIN GENERATED: Get-JIMConnectedSystemPasswordPolicy -->
### Syntax

```powershell
# ById (default)
Get-JIMConnectedSystemPasswordPolicy -Id <int>

# ByInputObject
Get-JIMConnectedSystemPasswordPolicy -InputObject <PSCustomObject>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) |  | The ID of the Connected System. Accepts pipeline input by property name. |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) |  | A Connected System from `Get-JIMConnectedSystem`, through the pipeline. Accepts pipeline input. |

### Output

The password policy JIM last read from the Connected System. An empty figure means JIM could not read that rule, not that the rule does not exist.

| Property | Type | Description |
|----------|------|-------------|
| `Discovered` | `datetime` | When JIM last read this from the Connected System. Empty when it has never been read, which is also when every other field is empty. |
| `MinimumLength` | `int` | The shortest password the system will accept. |
| `ComplexityRequired` | `bool` | Whether the system enforces a complexity rule at all. |
| `RequiredCharacterClassCount` | `int` | How many character categories a password must draw on. |
| `RecognisedCharacterClasses` | `string[]` | The character categories this system counts towards its complexity rule. A character from a category it does not count does nothing to help a password satisfy the rule. |
| `PasswordHistoryLength` | `int` | How many previous passwords the system remembers and refuses. |
| `MaximumPasswordAgeDays` | `int` | How long a password may live, in days. |
| `MinimumPasswordAgeDays` | `int` | How soon a password may be changed again, in days. |
| `PolicyOverrideSignal` | `string` | Whether the directory has password policies that apply to only some accounts (Active Directory's Fine-Grained Password Policies, OpenLDAP's per-entry policy subentries, 389 Directory Server's subtree policies). Where these are present, or where JIM was not permitted to find out, the figures above are a floor rather than a guarantee. One of `Absent`, `Present` or `CouldNotDetermine`. |
| `FurtherChecksApply` | `bool` | Whether the directory applies checks beyond the rules above that JIM cannot read, such as a dictionary check or a password-quality module. When true, a password satisfying everything above can still be refused. |
| `DiscoveryOutcome` | `string` | Why the policy says what it says: `Read`, `NotPublished` (the directory publishes no policy a client can read), `ConfigurationNotReadable` (the account JIM connects as cannot read the server configuration that holds it) or `NoPolicyConfigured` (the mechanism is loaded but no policy is configured). Empty when nothing has been read yet, which is not an outcome of reading. |
| `HasAnyDiscoveredConstraint` | `bool` | Whether JIM discovered anything at all. False means the figures above say nothing about what this system will accept, rather than that it accepts anything. |
<!-- END GENERATED: Get-JIMConnectedSystemPasswordPolicy -->

### Examples

```powershell title="By ID"
Get-JIMConnectedSystemPasswordPolicy -Id 3
```

```powershell title="From the pipeline"
Get-JIMConnectedSystem -Id 3 | Get-JIMConnectedSystemPasswordPolicy
```

!!! warning "A null means JIM could not read that rule, not that no such rule exists"
    A directory withholds what a caller may not see by omitting it rather than refusing, so a null minimum
    length does not mean any length is acceptable. Check `HasAnyDiscoveredConstraint` before treating the
    figures as a description of what the system will accept. Where `PolicyOverrideSignal` is `Present` or
    `CouldNotDetermine`, the figures are a floor rather than a guarantee, because some accounts may be governed
    by a stricter policy (Active Directory's Fine-Grained Password Policies, OpenLDAP's per-entry policy
    subentries, 389 Directory Server's subtree policies). Where nothing was discovered, `DiscoveryOutcome` says
    why, and whether there is anything to do about it: `NotPublished` means the directory has nothing to read,
    `ConfigurationNotReadable` means the account JIM connects as needs read access to the server configuration.

---

## :material-sync: Get-JIMConnectedSystemPasswordSynchronisation

Gets whether a Connected System receives synchronised passwords, and the settings governing how hard JIM tries
to deliver them. A Connected System that has never been configured is reported with `Configured` set to
`$false` and JIM's defaults in the remaining fields, so a script comparing systems does not have to treat the
untouched ones differently.

<!-- BEGIN GENERATED: Get-JIMConnectedSystemPasswordSynchronisation -->
### Syntax

```powershell
# ById (default)
Get-JIMConnectedSystemPasswordSynchronisation -Id <int>

# ByInputObject
Get-JIMConnectedSystemPasswordSynchronisation -InputObject <PSCustomObject>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) |  | The ID of the Connected System. Accepts pipeline input by property name. |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) |  | A Connected System from `Get-JIMConnectedSystem`, through the pipeline. Accepts pipeline input. |

### Output

The Connected System's Password Synchronisation configuration, or JIM's defaults with Configured `$false` when it has none.

| Property | Type | Description |
|----------|------|-------------|
| `Configured` | `bool` | Whether Password Synchronisation has been configured on this Connected System at all. False means every other field below is JIM's default rather than somebody's choice. |
| `ConnectorSupportsPasswordSet` | `bool` | Whether this Connected System's Connector can set passwords. False means Password Synchronisation cannot be configured here, whatever else is sent. |
| `Enabled` | `bool` | Whether queued password changes are delivered to this system. A configured but disabled system keeps accumulating them, and enabling it drains what accumulated. |
| `TargetObjectTypeId` | `int` | The Connected System Object Type that receives passwords, i.e. the one holding this system's user accounts. |
| `TargetObjectTypeName` | `string` | The name of TargetObjectTypeId, so a caller does not need a second request to report it. |
| `MaxRetries` | `int` | How many delivery attempts are made before a queued change is parked for an administrator. Zero means JIM's default applies; see EffectiveMaxRetries for what that resolves to. |
| `EffectiveMaxRetries` | `int` | The retry count actually applied, with JIM's default resolved. |
| `RetryBackoffBase` | `string` | The first retry interval; each subsequent attempt waits twice as long. Zero means JIM's default applies. |
| `EffectiveRetryBackoffBase` | `string` | The backoff base actually applied, with JIM's default resolved. |
| `RequireSecureTransport` | `bool` | Whether JIM refuses to send a password to this system over a connection it cannot confirm is encrypted. Reported here because it governs this feature, but it is set on the Connected System itself (requireSecureTransport) because it governs every password JIM sends to the system, including the initial password on an account it provisions and one an administrator sets by hand. Read-only on this resource; change it on the Connected System. |
| `EffectiveTimeToLive` | `string` | How long a queued password change waits for this system before it is expired rather than delivered. Reported here because it governs this feature, but it is set on the Connected System itself (initialPasswordTimeToLive), which is shared with initial password provisioning: the question both ask is how long this system may be unavailable before JIM stops trying. |
<!-- END GENERATED: Get-JIMConnectedSystemPasswordSynchronisation -->

### Examples

```powershell title="By ID"
Get-JIMConnectedSystemPasswordSynchronisation -Id 3
```

```powershell title="Which Connected Systems are switched on"
Get-JIMConnectedSystem | ForEach-Object {
    $p = Get-JIMConnectedSystemPasswordSynchronisation -Id $_.Id
    [PSCustomObject]@{ System = $_.Name; Configured = $p.Configured; Enabled = $p.Enabled }
}
```

---

## :material-tune-variant: Set-JIMConnectedSystemPasswordSynchronisation

Creates or updates a Connected System's Password Synchronisation configuration. Omitted parameters leave the
stored value unchanged; `-TargetObjectType` is required when creating one.

<!-- BEGIN GENERATED: Set-JIMConnectedSystemPasswordSynchronisation -->
### Syntax

```powershell
# ById (default)
Set-JIMConnectedSystemPasswordSynchronisation -Id <int> [-Enabled <bool>] [-TargetObjectType <int>] [-MaxRetries <int>] [-RetryBackoffBase <timespan>] [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]

# ByInputObject
Set-JIMConnectedSystemPasswordSynchronisation -InputObject <PSCustomObject> [-Enabled <bool>] [-TargetObjectType <int>] [-MaxRetries <int>] [-RetryBackoffBase <timespan>] [-ChangeReason <string>] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) |  | The ID of the Connected System. Accepts pipeline input by property name. |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) |  | A Connected System from `Get-JIMConnectedSystem`, through the pipeline. Accepts pipeline input. |
| `Enabled` | `bool` | No |  | Whether queued password changes are delivered to the Connected System. Enabling it delivers whatever queued while it was off. |
| `TargetObjectType` | `int` | No |  | The ID of the Object Type holding the Connected System's user accounts, which must be selected for synchronisation. Required when creating a configuration. Also accepted as `-TargetObjectTypeId`. |
| `MaxRetries` | `int` | No |  | How many delivery attempts are made before a queued change is parked for an administrator; 0 for JIM's default. |
| `RetryBackoffBase` | `timespan` | No |  | The first retry interval; each later attempt waits twice as long, up to the change's time to live. 0 for JIM's default. |
| `ChangeReason` | `string` | No |  | Why the change was made, recorded in the Connected System's change history. |
| `PassThru` | `switch` | No |  | Returns the configuration as changed. |

### Output

With `-PassThru`, the configuration as changed; otherwise nothing.

Properties as under [Get-JIMConnectedSystemPasswordSynchronisation](#get-jimconnectedsystempasswordsynchronisation).
<!-- END GENERATED: Set-JIMConnectedSystemPasswordSynchronisation -->

### Examples

```powershell title="Stage the configuration without switching it on"
Set-JIMConnectedSystemPasswordSynchronisation -Id 3 -TargetObjectType 7 -Enabled $false
```

```powershell title="Switch it on in the change window, delivering everything queued while it was off"
Set-JIMConnectedSystemPasswordSynchronisation -Id 3 -Enabled $true -ChangeReason 'CHG0041288'
```

```powershell title="Allow ten attempts before a change is parked"
Set-JIMConnectedSystemPasswordSynchronisation -Id 3 -MaxRetries 10 -PassThru
```

!!! note "Require Secure Transport is set on the Connected System"
    It governs every password JIM sends to the system, not only synchronised ones, so it is set with
    `Set-JIMConnectedSystem -Id 3 -RequireSecureTransport` and turned off with
    `-RequireSecureTransport:$false`. It is reported here as well, because it governs this feature too.

!!! note "There is no Remove cmdlet, and that is deliberate"
    Removing a configuration would discard every password change queued against it. Disabling it keeps them,
    and is reversible, so `-Enabled $false` is the supported way to stop delivery.

---

## :material-book-open-variant-outline: Related

- [Password Synchronisation](../concepts/password-synchronisation.md) explains what the queue is and how a change moves through it, and [The Password Delivery Service](../concepts/password-synchronisation.md#the-password-delivery-service) what delivers it
- [Metaverse](metaverse.md) covers finding the Metaverse Object whose password you set, and what its Connected System Objects are
- [Password Queue](password-queue.md) covers reading the queue, and releasing or cancelling the password changes stuck in it
