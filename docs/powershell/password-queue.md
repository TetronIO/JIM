---
title: Password Queue
description: "The cmdlets that read the queue of password changes on their way to Connected Systems, and release or cancel stuck ones."
type: reference
audience: automation-author
---

# Password Queue

These cmdlets read the [Password Synchronisation](../concepts/password-synchronisation.md) queue (the password
changes on their way to your Connected Systems) and do the two things you can do about the ones that are stuck:
release them for another attempt, or cancel them.

They exist because a recovery is not a job for a browser. When a directory has been refusing passwords and
somebody has finally fixed the cause, what you want is one command that releases everything parked behind it,
not a page of rows to click through.

!!! note "No password is ever returned"
    Nothing here returns a password in any form: a queued password is encrypted in the database and has no
    representation in any response.

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

## See also

- [Password Synchronisation](password-synchronisation.md): set a Metaverse Object's password, and configure which Connected Systems receive passwords
- [Password Synchronisation concepts](../concepts/password-synchronisation.md): how a password change reaches a system, and how long it is kept
