---
title: Password Synchronisation
---

# Password Synchronisation

These cmdlets are the queue behind [Password Synchronisation](../concepts/passwords.md#-password-synchronisation): the password changes on their way to your Connected Systems, and the two things you can do about the ones that are stuck.

They exist because a recovery is not a job for a browser. When a directory has been refusing passwords and somebody has finally fixed the cause, what you want is one command that releases everything parked behind it, not a page of rows to click through.

`Sync-JIMMetaverseObjectPassword` ([Metaverse](metaverse.md)) is what puts a change on this queue in the first place.

!!! note "No password is ever returned"
    Nothing here returns a password, in any form. The queued value is encrypted in the database and has no representation in any response.

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
| `Status` | `string` | No | | One of `Pending`, `Parked`, `Expired`, `Cancelled`. |
| `FailureReason` | `string` | No | | One of `None`, `Transient`, `ConfigurationFault`, `PolicyRejection`, `TargetObjectNotFound`, `UnsupportedOperation`. Only meaningful for changes that have been attempted. |
| `MetaverseObjectId` | `guid` | No | | Restrict to one identity's queued changes. |
| `Search` | `string` | No | | Free-text search over the identity and Connected System names. |
| `SortBy` | `string` | No | `queued` | One of `queued`, `identity`, `system`, `status`, `attempts`, `nextAttempt`, `expires`. |
| `SortDirection` | `string` | No | `asc` | `asc` or `desc`. |
| `Page` | `int` | No | `1` | Page number. Not available with `-All`. |
| `PageSize` | `int` | No | `50` | Results per page (maximum 100). |
| `All` | `switch` | Yes (ListAll set) | | Retrieve every page. Stops after 1000 pages with a warning unless `-Force` is supplied. |
| `Force` | `switch` | No | | Fetch beyond the `-All` page ceiling. |
| `Summary` | `switch` | Yes (Summary set) | | Return the queue's counts by state instead of its rows. |

### Output

In the default and `-All` parameter sets, one `PSCustomObject` per queued change:

| Property | Description |
|----------|-------------|
| `Id` | The change's unique identifier, as passed to `Resume-` and `Stop-JIMPendingPasswordChange`. |
| `MetaverseObjectId`, `MetaverseObjectDisplayName` | The person whose password this is. |
| `MetaverseObjectTypePluralName` | Their Metaverse Object Type's plural name, which is what a link to them is built from. |
| `ConnectedSystemId`, `ConnectedSystemName` | Where it is going. |
| `Status` | `Pending`, `Parked`, `Expired` or `Cancelled`. |
| `Due` | Whether a delivery pass would attempt this change right now. A `Pending` change may be waiting out a retry backoff, which `Status` alone cannot tell you. |
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
Get-JIMPendingPasswordChange -Status Pending | Where-Object { -not $_.Due }
```

---

## Resume-JIMPendingPasswordChange

Makes matching changes due immediately and raises a delivery pass for them.

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
    However many changes are piped in, this is a single request and a single Activity. A retry over a directory that has just come back is one decision, and an Activity per row would bury it in its own consequences.

---

## Stop-JIMPendingPasswordChange

Stops JIM delivering matching changes.

The rows are kept, marked `Cancelled`, recording who cancelled them and when. They are not deleted: that person's password is still divergent on that Connected System, and the cancelled row is the only thing that says so. Retention removes them on the same schedule as any other finished change, and a cancelled change can be put back on the queue with `Resume-JIMPendingPasswordChange` provided it has not expired in the meantime.

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

- [Password Synchronisation](../concepts/passwords.md#-password-synchronisation) explains what the queue is and how a change moves through it
- [Metaverse](metaverse.md) covers `Sync-JIMMetaverseObjectPassword`, which puts changes on this queue
- [Connected Systems](connected-systems.md) covers `Get-` and `Set-JIMConnectedSystemPasswordSynchronisation`, which decide which systems receive them
