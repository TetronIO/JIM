---
title: Password Synchronisation
---

# Password Synchronisation

These cmdlets put a password change on the [Password Synchronisation](../concepts/passwords.md#-password-synchronisation) queue, read the queue (the password changes on their way to your Connected Systems), and do the two things you can do about the ones that are stuck.

The queue cmdlets exist because a recovery is not a job for a browser. When a directory has been refusing passwords and somebody has finally fixed the cause, what you want is one command that releases everything parked behind it, not a page of rows to click through.

!!! note "No password is ever returned"
    Nothing here returns a password, in any form. The queued value is encrypted in the database and has no representation in any response.

---

## Sync-JIMMetaverseObjectPassword

Records that a person's password has changed and queues it for every Connected System enabled for Password Synchronisation in which they have an account.

By default it returns as soon as the change is recorded; the [Password Delivery Service](../concepts/passwords.md#-the-password-delivery-service) makes the first attempt within about a second, whatever the synchronisation engine is doing. Pass `-Wait` to be told what each system did with the password before the command returns.

This is not `Set-JIMMetaverseObjectPassword` ([Metaverse](metaverse.md#set-jimmetaverseobjectpassword)), which sets a password you choose on the accounts you name, immediately, and reports per account. Use this one when the person has changed their own password somewhere and the rest should catch up.

### Syntax

```powershell
Sync-JIMMetaverseObjectPassword -Id <guid> -Password <securestring> [-ExpiryBehaviour <string>] [-Wait <int>]
                                [-Force] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes | | The Metaverse Object whose password changed. Accepts pipeline input by property name, so `Get-JIMMetaverseObject` output can be piped in. |
| `Password` | `securestring` | Yes | | The new password. Encrypted before JIM stores it; never logged, returned or recorded on an Activity. |
| `ExpiryBehaviour` | `string` | No | `ExpiresAccordingToTargetPolicy` | One of `RequireChangeAtNextSignIn`, `ExpiresAccordingToTargetPolicy`, `NeverExpires`. The default suits a password the person chose themselves. |
| `Wait` | `int` | No | `0` | How many seconds, 0 to 30, to wait for the systems to answer. The wait ends early once every target has settled. A script that needs to watch for longer should poll `Get-JIMPendingPasswordChange -MetaverseObjectId` instead. |
| `Force` | `switch` | No | | Skip the confirmation prompt. |

Which systems receive the password is their own configuration, not a choice made here; a system with Password Synchronisation switched off still accumulates the change and receives it when switched back on.

### Output

A `PSCustomObject` describing what was queued and, if you waited, where it got to:

| Property | Description |
|----------|-------------|
| `ActivityId` | The Activity recording the change. Its child Activities hold each system's outcome once delivery has been attempted. |
| `Settled` | Whether every target had reached an outcome a caller need not wait on by the time the command returned. Without `-Wait` this is `$false` unless nothing was queued. A target that is retrying counts as settled: its next attempt is minutes away. |
| `QueuedForNoSystems` | `$true` when no Connected System the person has an account in is enabled for Password Synchronisation, so nothing was queued. Worth checking: silence here would let a script believe a password propagated when nothing was recorded. |
| `Targets` | One entry per Connected System the change was queued for, in name order. |

Each entry under `Targets`:

| Property | Description |
|----------|-------------|
| `ConnectedSystemId`, `ConnectedSystemName` | Where it is going. |
| `Enabled` | Whether the system is currently taking synchronised passwords. `$false` means the change is held until somebody switches it on. |
| `ConnectedSystemObjectId` | The account the password is aimed at, or `$null` where the person has no account in this system yet; the change is queued regardless, bounded by its time to live, so it lands when provisioning catches up. |
| `State` | `Queued`, `Delivering`, `Set`, `Retrying`, `Parked`, `Held`, `Expired` or `Cancelled`. |
| `NextAttemptAt` | When the next attempt falls due, for a target that is `Retrying`; `$null` otherwise. |
| `Message` | The target's own words on its most recent outcome (why it refused, or that the password was set), or `$null` before anything has been said. |
| `AttemptCount` | How many delivery attempts this system has had. |

### Examples

```powershell title="Record a password change and return at once"
$password = Read-Host -AsSecureString "New password"
Sync-JIMMetaverseObjectPassword -Id 8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f -Password $password
```

```powershell title="Wait up to ten seconds and report which systems took the password"
$result = Sync-JIMMetaverseObjectPassword -Id $id -Password $password -Wait 10 -Force
$result.Targets | Select-Object ConnectedSystemName, State, Message
if (-not $result.Settled) {
    Write-Warning "Not every system had answered after 10 seconds; check the person's Password Synchronisation tab."
}
```

A service desk script uses this to tell the caller their reset has landed before they hang up: `State` is `Set` where it has, `Retrying` with a `NextAttemptAt` where a directory was unreachable, and `Parked` with the directory's own `Message` where it refused.

```powershell title="Catch the case where nothing was queued"
$result = Sync-JIMMetaverseObjectPassword -Id $id -Password $password -Force
if ($result.QueuedForNoSystems) { Write-Warning "No system takes synchronised passwords for this person." }
```

---

## Get-JIMPendingPasswordChange

Gets queued password changes, or the queue's counts by state.

### Syntax

```powershell
# List (default)
Get-JIMPendingPasswordChange [-ConnectedSystemId <int>] [-Status <string>] [-FailureReason <string>]
                             [-MetaverseObjectId <guid>] [-Search <string>] [-SortBy <string>]
                             [-SortDirection <string>] [-Page <int>] [-PageSize <int>]

# ListAll
Get-JIMPendingPasswordChange -All [-ConnectedSystemId <int>] [-Status <string>] [-FailureReason <string>]
                             [-MetaverseObjectId <guid>] [-Search <string>] [-SortBy <string>]
                             [-SortDirection <string>] [-PageSize <int>] [-Force]

# Summary
Get-JIMPendingPasswordChange -Summary
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | No | | Restrict to one Connected System. Accepts pipeline input by property name, so a Connected System can be piped in. |
| `Status` | `string` | No | | One of `Pending`, `Delivering`, `Parked`, `Expired`, `Cancelled`. |
| `FailureReason` | `string` | No | | One of `None`, `Transient`, `ConfigurationFault`, `PolicyRejection`, `TargetObjectNotFound`, `UnsupportedOperation`. Only meaningful for changes that have been attempted. |
| `MetaverseObjectId` | `guid` | No | | Restrict to one identity's queued changes. |
| `Search` | `string` | No | | Free-text search over the identity and Connected System names. |
| `SortBy` | `string` | No | `queued` | One of `queued`, `identity`, `system`, `status`, `attempts`, `nextAttempt`, `expires`. |
| `SortDirection` | `string` | No | `asc` | `asc` or `desc`. |
| `Page` | `int` | No | `1` | Page number. Not available with `-All`. |
| `PageSize` | `int` | No | `50` | Results per page (maximum 100). |
| `All` | `switch` | Yes (ListAll set) | | Retrieve every page. Stops after 1000 pages with a warning unless `-Force` is supplied. |
| `Force` | `switch` | No | | Fetch beyond the `-All` page ceiling. |
| `Summary` | `switch` | Yes (Summary set) | | Return the queue's counts by state instead of the changes themselves. |

### Output

In the default and `-All` parameter sets, one `PSCustomObject` per queued change:

| Property | Description |
|----------|-------------|
| `Id` | The change's unique identifier, as passed to `Resume-` and `Stop-JIMPendingPasswordChange`. |
| `MetaverseObjectId`, `MetaverseObjectDisplayName` | The person whose password this is. |
| `MetaverseObjectTypePluralName` | Their Metaverse Object Type's plural name, which is what a link to them is built from. |
| `ConnectedSystemId`, `ConnectedSystemName` | Where it is going. |
| `Status` | `Pending`, `Delivering`, `Parked`, `Expired` or `Cancelled`. `Delivering` is momentary: the Password Delivery Service is writing the change to the Connected System right now. |
| `Due` | Whether the Password Delivery Service would attempt this change now. A `Pending` change may be waiting out a retry backoff, or be `Held`, neither of which `Status` alone can tell you. Never `$true` while `Held` is. |
| `Held` | Whether the change is waiting on Password Synchronisation being switched back on for its Connected System, rather than on JIM. A switched-off system accumulates changes instead of discarding them; switching it on delivers what accumulated. |
| `FailureReason`, `TargetMessage` | How the last attempt failed, and the target's own words. Both `$null` for a change that has not been attempted. |
| `AttemptCount` | How many delivery attempts have been made. |
| `NextRetryAt` | When the next attempt falls due, or `$null` for a change that is due now or is no longer being attempted. |
| `CreatedAt`, `LastAttemptedAt`, `ExpiresAt` | When it was queued, last tried, and stops being deliverable. |
| `CancelledAt`, `CancelledByName` | When an administrator cancelled it, and who. `$null` where nobody has; the name is `$null` for a cancellation made with an API key. |

With `-Summary`, a single object with `WaitingCount`, `DueCount`, `ParkedCount`, `ExpiredCount` and `CancelledCount`.

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

### Syntax

```powershell
Resume-JIMPendingPasswordChange [-Id <guid[]>] [-ConnectedSystemId <int>] [-Status <string>]
                                [-FailureReason <string>] [-MetaverseObjectId <guid>] [-Search <string>]
                                [-EntireQueue] [-Force] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid[]` | No | | The changes to retry. Accepts pipeline input by property name, so queued changes can be piped straight in. |
| `ConnectedSystemId` | `int` | No | | Retry the changes queued for one Connected System. Accepts pipeline input by property name. |
| `Status` | `string` | No | | Retry only changes in this state. |
| `FailureReason` | `string` | No | | Retry only changes whose last attempt failed this way. |
| `MetaverseObjectId` | `guid` | No | | Retry only one identity's changes. |
| `Search` | `string` | No | | Retry only changes matching this search over the identity and Connected System names. |
| `EntireQueue` | `switch` | No | | Retry every queued password change. Required when nothing else narrows the request. |
| `Force` | `switch` | No | | Skip the confirmation prompt. |

The criteria combine rather than replace one another. Piping changes in alongside `-Status Parked` means "these, if they are still parked": one delivered since you listed it is not retried.

### Output

A `PSCustomObject` with an `AffectedCount` property: how many changes were made due again. Zero is a valid answer, not an error; it means nothing matched.

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

### Syntax

```powershell
Stop-JIMPendingPasswordChange [-Id <guid[]>] [-ConnectedSystemId <int>] [-Status <string>]
                              [-FailureReason <string>] [-MetaverseObjectId <guid>] [-Search <string>]
                              [-EntireQueue] [-Force] [-WhatIf] [-Confirm]
```

### Parameters

The same as `Resume-JIMPendingPasswordChange` above, and they combine the same way.

### Output

A `PSCustomObject` with an `AffectedCount` property: how many changes were cancelled.

### Examples

```powershell title="Look before you cancel"
Get-JIMPendingPasswordChange -ConnectedSystemId 7
```

```powershell title="Cancel everything queued for a system being decommissioned"
Stop-JIMPendingPasswordChange -ConnectedSystemId 7
```

Each change this cancels leaves somebody's password unchanged on that system. Run the `Get-` above first and read what it lists; without `-Force` the cancellation prompts for confirmation.

```powershell title="See what would be cancelled for one person"
Stop-JIMPendingPasswordChange -MetaverseObjectId 8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f -WhatIf
```

---

## Related

- [Password Synchronisation](../concepts/passwords.md#-password-synchronisation) explains what the queue is and how a change moves through it, and [The Password Delivery Service](../concepts/passwords.md#-the-password-delivery-service) what delivers it
- [Metaverse](metaverse.md#set-jimmetaverseobjectpassword) covers `Set-JIMMetaverseObjectPassword`, which sets a chosen password on named accounts immediately rather than through this queue
- [Connected Systems](connected-systems.md) covers `Get-` and `Set-JIMConnectedSystemPasswordSynchronisation`, which decide which systems receive them
