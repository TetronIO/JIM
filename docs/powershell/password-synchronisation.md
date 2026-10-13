---
title: Password Synchronisation
description: "The cmdlets that set a Metaverse Object's password, read the queue of password changes on their way to Connected Systems, and release or cancel stuck ones."
type: reference
audience: automation-author
---

# Password Synchronisation

These cmdlets put password changes on the [Password Synchronisation](../concepts/password-synchronisation.md) queue, read the queue (the password changes on their way to your Connected Systems), and do the two things you can do about the ones that are stuck.

The queue cmdlets exist because a recovery is not a job for a browser. When a directory has been refusing passwords and somebody has finally fixed the cause, what you want is one command that releases everything parked behind it, not a page of rows to click through.

!!! note "No password you supplied is ever returned"
    Nothing here returns a password you supplied, in any form: the queued value is encrypted in the database and has no representation in any response. The one password returned is one JIM generated for `Set-JIMMetaverseObjectPassword -Generate`, once, to the caller who asked for it.

---

## Set-JIMMetaverseObjectPassword

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
| `EnableAccount` | `switch` | No (NamedGeneratedPassword, NamedSuppliedPassword) |  | Enables the named accounts as part of setting the password. Omit it to leave their enabled state as it is, with two exceptions: a new Active Directory account JIM created disabled only until it had a password, which the password enables as its Attribute Flow asks; and an account still waiting for its first password, which the password then is, enabled as its Synchronisation Rule's Initial Password says. Only available with `-ConnectedSystemId`: a password propagated to every configured system enables no account other than those two kinds, because it reaches accounts an administrator may have disabled on purpose. |
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

A `Parked` target is also reported as a non-terminating error carrying the result as its `TargetObject`, so a script that stops on errors stops on a refusal and can still read the other targets from the exception it caught. A target still in flight when a wait ran out is reported as a warning.

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

## Get-JIMPendingPasswordChange

Gets queued password changes, or the queue's counts by state.

<!-- BEGIN GENERATED: Get-JIMPendingPasswordChange -->
### Syntax

```powershell
# List (default)
Get-JIMPendingPasswordChange [-ConnectedSystemId <int>] [-Status <string>] [-FailureReason <string>] [-MetaverseObjectId <guid>] [-Search <string>] [-SortBy <string>] [-SortDirection <string>] [-Page <int>] [-PageSize <int>]

# ListAll
Get-JIMPendingPasswordChange -All [-ConnectedSystemId <int>] [-Status <string>] [-FailureReason <string>] [-MetaverseObjectId <guid>] [-Search <string>] [-SortBy <string>] [-SortDirection <string>] [-PageSize <int>] [-Force]

# Summary
Get-JIMPendingPasswordChange -Summary
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | No (List, ListAll) |  | Only changes queued for this Connected System. Accepts pipeline input by property name. |
| `Status` | `string` | No (List, ListAll) |  | Only changes in this state. One of `Pending`, `Delivering`, `Parked`, `Expired` or `Cancelled`. |
| `FailureReason` | `string` | No (List, ListAll) |  | Only changes whose last attempt failed this way. A change that has never been attempted has no reason. One of `None`, `Transient`, `ConfigurationFault`, `PolicyRejection`, `TargetObjectNotFound` or `UnsupportedOperation`. |
| `MetaverseObjectId` | `guid` | No (List, ListAll) |  | Only this Metaverse Object's queued changes. |
| `Search` | `string` | No (List, ListAll) |  | Free-text search over the Metaverse Object and Connected System names. |
| `SortBy` | `string` | No (List, ListAll) |  | The column to sort by; queued when omitted. One of `queued`, `identity`, `system`, `status`, `attempts`, `nextAttempt` or `expires`. |
| `SortDirection` | `string` | No (List, ListAll) |  | The sort direction; ascending when omitted. One of `asc` or `desc`. |
| `Page` | `int` | No (List) | `1` | The page of changes to return. At least 1. |
| `PageSize` | `int` | No (List, ListAll) | `50` | How many changes to return per page. From 1 to 100. |
| `All` | `switch` | Yes (ListAll) |  | Returns every page. Stops after 1,000 pages with a warning unless `-Force` is given. |
| `Force` | `switch` | No (ListAll) |  | Fetches beyond the `-All` page ceiling. |
| `Summary` | `switch` | Yes (Summary) |  | Returns the queue's counts by state instead of its changes. |

### Output

One object per queued change, one entry per Metaverse Object per Connected System, or with `-Summary` one object of counts. No password is returned, in any form.

**By default, or with `-All`**:

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `guid` | The queued change's ID, which identifies it when retrying or cancelling it. |
| `MetaverseObjectId` | `guid` | The Metaverse Object whose password this is. |
| `MetaverseObjectDisplayName` | `string` | That Metaverse Object's display name. |
| `MetaverseObjectTypePluralName` | `string` | The plural name of that Metaverse Object's type, such as People. |
| `ConnectedSystemId` | `int` | The Connected System the change is queued for. |
| `ConnectedSystemName` | `string` | That Connected System's name. |
| `Status` | `string` | Where the change has got to: Pending (JIM still intends to deliver it), Delivering (the Password Delivery Service has claimed it and is delivering it now), Parked (the target refused it, or it ran out of attempts, so it waits on a person), Expired (it outlived its time to live and can no longer be delivered), or Cancelled (an administrator stopped it). One of `Pending`, `Parked`, `Expired`, `Cancelled` or `Delivering`. |
| `Origin` | `string` | Where the change came from: `Explicit` (an administrator's named set), `Propagated` (JIM carried it to every configured system after the person's own password change), or `Provisioned` (the first password for an account an export has just created). One of `Propagated`, `Explicit` or `Provisioned`. |
| `SyncRuleId` | `int` | The Synchronisation Rule whose Initial Password this change delivers a Connected System Object's first password under, or empty where it is no Connected System Object's first password. A `Provisioned` change generates its password from the rule at each delivery attempt; a change carrying a first password in place of the Initial Password keeps its own password and takes only the rule's settings for enabling the Connected System Object and for whether its password never expires. |
| `ProvisionedConnectedSystemObjectId` | `guid` | The Connected System Object this change also gives its first password, or empty. Set when the change was already waiting as JIM provisioned that Connected System Object, or replaced its Initial Password before it was delivered; delivering it then applies the provisioning Synchronisation Rule's Initial Password settings for enabling the Connected System Object and for whether its password never expires. Cancelling such a change leaves that Connected System Object without a password from JIM. For a `Provisioned` change, the Connected System Object it is the Initial Password of. |
| `Due` | `bool` | Whether a delivery pass would attempt this change right now. It tells a change waiting out a retry backoff from one that is due but not yet reached, which `Status` alone cannot. It is never true for a held change, whose Connected System is switched off. |
| `Held` | `bool` | Whether the change is waiting on somebody switching Password Synchronisation back on for its Connected System, rather than on JIM. A configured system that is switched off accumulates queued changes instead of discarding them, and enabling it delivers what accumulated; until then nothing is attempted, so the remedy is a person's rather than a retry's. |
| `FailureReason` | `string` | How the last attempt failed; empty for a change that has not been attempted. One of `None`, `Transient`, `ConfigurationFault`, `PolicyRejection`, `TargetObjectNotFound` or `UnsupportedOperation`. |
| `TargetMessage` | `string` | The target's own words for why the last attempt failed, which say where the remedy lies; empty for a change that has not been attempted. |
| `AttemptCount` | `int` | How many delivery attempts have been made. |
| `NextRetryAt` | `datetime` | When the next attempt falls due, or empty for a change that is due now or is no longer being attempted. |
| `CreatedAt` | `datetime` | When the change was queued. |
| `LastAttemptedAt` | `datetime` | When delivery was last attempted, or empty where it never has been. |
| `ExpiresAt` | `datetime` | When the change stops being deliverable, whether or not it has been delivered by then. |
| `CancelledAt` | `datetime` | When an administrator cancelled the change, or empty where nobody has. |
| `CancelledByName` | `string` | Who cancelled the change; empty where nobody has, and for a cancellation made with an API key, which has no person behind it. |

**With `-Summary`**:

| Property | Type | Description |
|----------|------|-------------|
| `WaitingCount` | `int` | Changes JIM still intends to deliver, whether due now or waiting out a backoff. |
| `DueCount` | `int` | How many of the waiting changes a delivery pass would attempt right now. A large waiting count with nothing due is a queue working through its backoffs; a large due count is a queue that is not being drained. A change queued for a Connected System that is switched off is waiting but not due: it is held until somebody enables the system. |
| `ParkedCount` | `int` | Changes the target refused, or that ran out of attempts. JIM has stopped trying, so these wait on a person. |
| `ExpiredCount` | `int` | Changes that outlived their Connected System's time to live. Nothing can deliver these now: the password they carried is gone. |
| `CancelledCount` | `int` | Changes an administrator stopped. Counted rather than hidden, because the Metaverse Object's password is still divergent on that system and the count is the only thing that says so. |
<!-- END GENERATED: Get-JIMPendingPasswordChange -->

### Examples

```powershell title="Is anything wrong?"
Get-JIMPendingPasswordChange -Summary
```

```powershell title="What needs a person"
Get-JIMPendingPasswordChange -Status Parked
```

```powershell title="Which systems the parked work is piling up behind"
Get-JIMPendingPasswordChange -Status Parked |
    Group-Object ConnectedSystemName |
    Select-Object Name, Count
```

```powershell title="Everything queued for one Connected System"
Get-JIMConnectedSystem -Name "Corporate AD" | Get-JIMPendingPasswordChange -All
```

```powershell title="Changes waiting out a retry backoff, as opposed to those due now"
Get-JIMPendingPasswordChange -Status Pending | Where-Object { -not $_.Due -and -not $_.Held }
```

```powershell title="Which systems are holding password changes because they are switched off"
Get-JIMPendingPasswordChange -Status Pending -All |
    Where-Object Held |
    Group-Object ConnectedSystemName |
    Select-Object Name, Count
```

---

## Resume-JIMPendingPasswordChange

Makes matching changes due immediately. The Password Delivery Service is woken by the change and attempts them within about a second, whatever the synchronisation engine is doing.

Run it once the reason a Connected System was refusing passwords has been dealt with. It applies to `Pending`, `Parked` and `Cancelled` changes; an `Expired` change is left alone, because the password it carried is gone. Retrying clears the failure recorded against a change and resets its attempt count.

!!! tip "Named `Resume-` rather than `Retry-`"
    `Retry` is not a PowerShell approved verb, and a module exporting one warns on import. `Resume` is the approved verb for starting something that was suspended, which is what a parked or cancelled change is.

<!-- BEGIN GENERATED: Resume-JIMPendingPasswordChange -->
### Syntax

```powershell
Resume-JIMPendingPasswordChange [-Id <guid[]>] [-ConnectedSystemId <int>] [-Status <string>] [-FailureReason <string>] [-MetaverseObjectId <guid>] [-Search <string>] [-EntireQueue] [-Force] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid[]` | No |  | The IDs of the queued changes to retry. Combines with the other criteria rather than replacing them, so `-Status` Parked alongside piped changes means these changes, if they are still parked. Accepts pipeline input by property name. |
| `ConnectedSystemId` | `int` | No |  | Retry the changes queued for this Connected System. Accepts pipeline input by property name. |
| `Status` | `string` | No |  | Retry only changes in this state. An Expired change is never retried, so `-Status` Expired matches nothing. One of `Pending`, `Parked`, `Expired` or `Cancelled`. |
| `FailureReason` | `string` | No |  | Retry only changes whose last attempt failed this way. One of `None`, `Transient`, `ConfigurationFault`, `PolicyRejection`, `TargetObjectNotFound` or `UnsupportedOperation`. |
| `MetaverseObjectId` | `guid` | No |  | Retry only this Metaverse Object's queued changes. |
| `Search` | `string` | No |  | Retry only changes matching this free-text search over the Metaverse Object and Connected System names. |
| `EntireQueue` | `switch` | No |  | Retry every queued password change in the deployment. Required when nothing else narrows the request, so a command that names no criteria cannot act on everything by accident. |
| `Force` | `switch` | No |  | Skips the confirmation prompt. |

### Output

One object, whose AffectedCount says how many changes were made due again. Zero is an answer, not an error.

| Property | Type | Description |
|----------|------|-------------|
| `AffectedCount` | `int` | How many queued password changes the action applied to. May be lower than the number of identifiers a caller named, and that is not an error: a change delivered, expired or already cancelled between the caller reading the queue and acting on it no longer matches. |
<!-- END GENERATED: Resume-JIMPendingPasswordChange -->

### Examples

```powershell title="After fixing the directory that was refusing passwords"
Resume-JIMPendingPasswordChange -ConnectedSystemId 3
```

```powershell title="Retry everything parked, in a single request"
Get-JIMPendingPasswordChange -Status Parked | Resume-JIMPendingPasswordChange -Force
```

```powershell title="See what would be retried without retrying it"
Resume-JIMPendingPasswordChange -Status Parked -FailureReason Transient -WhatIf
```

```powershell title="Report how much the retry covered"
$result = Resume-JIMPendingPasswordChange -ConnectedSystemId 3 -Force
"$($result.AffectedCount) password change(s) will be attempted again."
```

!!! note "One request, one Activity"
    However many changes are piped in, this is a single request and a single Activity. A retry over a directory that has just come back is one decision, and an Activity per change would bury it in its own consequences.

---

## Stop-JIMPendingPasswordChange

Stops JIM delivering matching changes.

The changes are kept, marked `Cancelled`, recording who cancelled them and when. They are not deleted: that person's password is still divergent on that Connected System, and the cancelled change is the only thing that says so. Retention removes them on the same schedule as any other finished change, and a cancelled change can be put back on the queue with `Resume-JIMPendingPasswordChange` provided it has not expired in the meantime.

Applies to `Pending` and `Parked` changes. An `Expired` or already `Cancelled` change is left alone rather than having its recorded outcome overwritten.

<!-- BEGIN GENERATED: Stop-JIMPendingPasswordChange -->
### Syntax

```powershell
Stop-JIMPendingPasswordChange [-Id <guid[]>] [-ConnectedSystemId <int>] [-Status <string>] [-FailureReason <string>] [-MetaverseObjectId <guid>] [-Search <string>] [-EntireQueue] [-Force] [-WhatIf] [-Confirm]
```

It asks for confirmation before it acts: `-WhatIf` shows what it would do, and `-Force` or `-Confirm:$false` skips the prompt.

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid[]` | No |  | The IDs of the queued changes to cancel. Combines with the other criteria rather than replacing them, so `-Status` Parked alongside piped changes means these changes, if they are still parked. Accepts pipeline input by property name. |
| `ConnectedSystemId` | `int` | No |  | Cancel the changes queued for this Connected System. Accepts pipeline input by property name. |
| `Status` | `string` | No |  | Cancel only changes in this state. An Expired or Cancelled change keeps the outcome it has, so those states match nothing. One of `Pending`, `Parked`, `Expired` or `Cancelled`. |
| `FailureReason` | `string` | No |  | Cancel only changes whose last attempt failed this way. One of `None`, `Transient`, `ConfigurationFault`, `PolicyRejection`, `TargetObjectNotFound` or `UnsupportedOperation`. |
| `MetaverseObjectId` | `guid` | No |  | Cancel only this Metaverse Object's queued changes. |
| `Search` | `string` | No |  | Cancel only changes matching this free-text search over the Metaverse Object and Connected System names. |
| `EntireQueue` | `switch` | No |  | Cancel every queued password change in the deployment. Required when nothing else narrows the request, so a command that names no criteria cannot act on everything by accident. |
| `Force` | `switch` | No |  | Skips the confirmation prompt. |

### Output

One object, whose AffectedCount says how many changes were cancelled. Zero is an answer, not an error.

Properties as under [Resume-JIMPendingPasswordChange](#resume-jimpendingpasswordchange).
<!-- END GENERATED: Stop-JIMPendingPasswordChange -->

### Examples

```powershell title="Look before you cancel"
Get-JIMPendingPasswordChange -ConnectedSystemId 7
```

```powershell title="Cancel everything queued for a system being decommissioned"
Stop-JIMPendingPasswordChange -ConnectedSystemId 7
```

Each change this cancels leaves somebody's password unchanged on that system. Run the `Get-` above first and read what it lists; without `-Force` the cancellation prompts for confirmation.

```powershell title="See what would be cancelled for one Metaverse Object"
Stop-JIMPendingPasswordChange -MetaverseObjectId 8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f -WhatIf
```

---

## Related

- [Password Synchronisation](../concepts/password-synchronisation.md) explains what the queue is and how a change moves through it, and [The Password Delivery Service](../concepts/password-synchronisation.md#the-password-delivery-service) what delivers it
- [Metaverse](metaverse.md) covers finding the Metaverse Object whose password you set, and what its Connected System Objects are
- [Connected Systems](connected-systems.md) covers `Get-` and `Set-JIMConnectedSystemPasswordSynchronisation`, which decide which systems receive them
