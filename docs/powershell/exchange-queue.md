---
title: Exchange Queue
description: "The cmdlets that read the queue of Exchange recipient changes and retry the ones JIM has stopped trying."
type: reference
audience: automation-author
---

# Exchange Queue

These cmdlets read the Exchange queue (the recipient changes JIM has queued for Exchange after an export) and retry the
ones that are stuck.

!!! warning "In development"
    Exchange recipients are still being built, behind the In Development feature flag `Features.ExchangeRecipients`.
    These cmdlets answer only where that flag is turned on, which is intended for development and testing.

```jim-diagram
archetype: comparison
title: What a retry does to each queued Exchange change
desc: "Resume-JIMPendingExchangeChange makes Parked changes, and Retrying changes that have been attempted, due at once: their attempts, failure and 24-hour park clock are reset and the Exchange delivery lane attempts them within seconds. It leaves Queued changes alone, because they are already due; Delivering changes, because Exchange is applying them now; and Refused and NotMade changes, which hold until what is declared for the account changes."
columns:
  - { label: "Retried", sub: due at once }
  - { label: "Left alone", sub: nothing for a retry to do }
rows:
  - label: States
    cells:
      - { text: "`Parked` and `Retrying`" }
      - { text: "`Queued`, `Delivering`, `Refused` and `NotMade`" }
  - label: Why
    cells:
      - { text: "JIM has stopped, or is waiting out a backoff", sub: "attempts, failure and park clock reset" }
      - { text: "already due, being applied now, or waiting on a different declared state" }
footer: "One request and one Activity, however many changes it covers"
```

A queued change is Queued, Delivering, Retrying, Parked, Refused or NotMade. A change JIM has stopped trying (Parked) is
retried once the cause is fixed. A change cannot be cancelled: to stop or correct one, change what is declared for the
account, its source data or its Attribute Flow, and the newer state replaces the queued change.

## :material-tray-full: Get-JIMPendingExchangeChange

Lists the changes on the Exchange queue, one row per account, naming its Connected System and Metaverse Object, or with
`-Summary` the queue's counts by state. A change leaves the queue once Exchange has it; its outcome stays on its
Activity.

<!-- BEGIN GENERATED: Get-JIMPendingExchangeChange -->
### Syntax

```powershell
# List (default)
Get-JIMPendingExchangeChange [-ConnectedSystemId <int>] [-State <string>] [-FailureKind <string>] [-MetaverseObjectId <guid>] [-ConnectedSystemObjectId <guid>] [-Search <string>] [-SortBy <string>] [-SortDirection <string>] [-Page <int>] [-PageSize <int>]

# ListAll
Get-JIMPendingExchangeChange -All [-ConnectedSystemId <int>] [-State <string>] [-FailureKind <string>] [-MetaverseObjectId <guid>] [-ConnectedSystemObjectId <guid>] [-Search <string>] [-SortBy <string>] [-SortDirection <string>] [-PageSize <int>] [-Force]

# Summary
Get-JIMPendingExchangeChange -Summary
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | No (List, ListAll) |  | Only changes for this Connected System. Accepts pipeline input by property name, `ConnectedSystemId` or `Id`. |
| `State` | `string` | No (List, ListAll) |  | Only changes in this state. One of `Queued`, `Delivering`, `Retrying`, `Parked`, `Refused` or `NotMade`. |
| `FailureKind` | `string` | No (List, ListAll) |  | Only changes whose last attempt failed this way. One of `ObjectNotFound`, `Refused`, `InsufficientPermissions`, `AuthenticationFailed` or `Unreachable`. |
| `MetaverseObjectId` | `guid` | No (List, ListAll) |  | Only this Metaverse Object's changes. |
| `ConnectedSystemObjectId` | `guid` | No (List, ListAll) |  | Only this account's change. The queue holds at most one per account. |
| `Search` | `string` | No (List, ListAll) |  | Free-text search over the Metaverse Object and Connected System names. |
| `SortBy` | `string` | No (List, ListAll) |  | The column to sort by; queued when omitted. One of `queued`, `identity`, `system`, `attempts`, `nextAttempt` or `lastAttempt`. |
| `SortDirection` | `string` | No (List, ListAll) |  | The sort direction; ascending when omitted. One of `asc` or `desc`. |
| `Page` | `int` | No (List) | `1` | The page of changes to return. At least 1. |
| `PageSize` | `int` | No (List, ListAll) | `50` | How many changes to return per page. From 1 to 100. |
| `All` | `switch` | Yes (ListAll) |  | Returns every page. Stops after 1,000 pages with a warning unless `-Force` is given. |
| `Force` | `switch` | No (ListAll) |  | Fetches beyond the `-All` page ceiling. |
| `Summary` | `switch` | Yes (Summary) |  | Returns the queue's counts by state instead of its changes. |

### Output

One object per queued change, or with `-Summary` one object of counts.

**By default, or with `-All`**:

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `guid` | The queued change's ID, which identifies it when retrying it. |
| `ActivityId` | `guid` | The change's own Activity, under which every attempt, retry and outcome is recorded. |
| `ConnectedSystemId` | `int` | The Connected System whose Exchange the change is for. |
| `ConnectedSystemName` | `string` | That Connected System's name. |
| `ConnectedSystemObjectId` | `guid` | The account in that Connected System the change makes a recipient, or empty once the account has been deleted. |
| `MetaverseObjectId` | `guid` | The Metaverse Object whose account it is, or empty once the Metaverse Object has been deleted. |
| `MetaverseObjectDisplayName` | `string` | That Metaverse Object's display name. |
| `MetaverseObjectTypePluralName` | `string` | The plural name of that Metaverse Object's type, such as People. |
| `RecipientType` | `string` | The kind of recipient declared: Mailbox, SharedMailbox, RoomMailbox, EquipmentMailbox, MailUser, MailContact, MailEnabledGroup or None. |
| `State` | `string` | Where the change stands: Queued (waiting for its first attempt), Delivering (Exchange is applying it now), Retrying (waiting out a backoff after a failure that may clear by itself), Parked (JIM has stopped trying, so it waits on a person), Refused (JIM refused the declared state before asking Exchange) or NotMade (it would change an existing recipient's type, which JIM does not do). A refused or not-made change holds until its declared state changes. One of `Queued`, `Delivering`, `Retrying`, `Parked`, `Delivered`, `Withdrawn`, `Replaced`, `Refused` or `NotMade`. |
| `FailureKind` | `string` | How the last attempt failed, or empty for a change not attempted since it was queued or retried. One of `ObjectNotFound`, `Refused`, `InsufficientPermissions`, `AuthenticationFailed` or `Unreachable`. |
| `FailureMessage` | `string` | Exchange's own words for the last failure, which say where the remedy lies, or JIM's reason for a refusal. |
| `AttemptCount` | `int` | How many attempts have failed since the change was queued or last retried. |
| `CreatedAt` | `datetime` | When the change was queued. |
| `LastAttemptedAt` | `datetime` | When it was last attempted, or empty if it has not been since it was queued or retried. |
| `FirstFailedAt` | `datetime` | When the current run of failures began. JIM parks a change whose failures have gone on for 24 hours. |
| `NextRetryAt` | `datetime` | When the next attempt falls due, or empty for a change due now or no longer attempted. |
| `Due` | `bool` | Whether the Exchange delivery lane would attempt the change now. |
| `CanRetry` | `bool` | Whether it can be retried: it is parked, or retrying after an attempt. A change not yet attempted is already due, one being delivered is getting its attempt, and a refused one waits on its declared state changing. |

**With `-Summary`**:

| Property | Type | Description |
|----------|------|-------------|
| `QueuedCount` | `int` | Changes waiting for their first attempt. |
| `DeliveringCount` | `int` | Changes Exchange is applying now. |
| `RetryingCount` | `int` | Changes waiting out a backoff after a failure that may clear by itself. |
| `ParkedCount` | `int` | Changes JIM has stopped trying, because Exchange refused them or 24 hours of retries failed. These wait on a person. |
| `RefusedCount` | `int` | Changes JIM refused before asking Exchange, which hold until their declared state changes. |
| `NotMadeCount` | `int` | Changes that would change an existing recipient's type, which JIM does not do. |
| `DueCount` | `int` | How many queued or retrying changes a delivery pass would attempt now. |
| `WaitingOnAPersonCount` | `int` | How many wait on a person: parked, refused or not made. |
<!-- END GENERATED: Get-JIMPendingExchangeChange -->

### Examples

```powershell title="Does Exchange need attention?"
Get-JIMPendingExchangeChange -Summary
```

```powershell title="The changes JIM has stopped trying, with Exchange's reason"
Get-JIMPendingExchangeChange -State Parked | Select-Object MetaverseObjectDisplayName, ConnectedSystemName, FailureKind, FailureMessage
```

```powershell title="What is behind the parked changes"
Get-JIMPendingExchangeChange -State Parked -All | Group-Object FailureKind | Select-Object Name, Count
```

---

## :material-play-circle-outline: Resume-JIMPendingExchangeChange

Makes matching changes that are parked, or retrying after an attempt, due at once, with their attempts and 24-hour park
clock reset; the Exchange delivery lane attempts them within seconds. A change not yet attempted is already due, one being
delivered is left to finish, and a Refused or NotMade change holds until what is declared for the account changes.

<!-- BEGIN GENERATED: Resume-JIMPendingExchangeChange -->
### Syntax

```powershell
Resume-JIMPendingExchangeChange [-Id <guid[]>] [-ConnectedSystemId <int>] [-State <string>] [-FailureKind <string>] [-MetaverseObjectId <guid>] [-ConnectedSystemObjectId <guid>] [-Search <string>] [-EntireQueue] [-Force] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid[]` | No |  | The IDs of the queued changes to retry. Combines with the other criteria rather than replacing them, so `-State` Parked alongside piped changes means these changes, if they are still parked. Accepts pipeline input by property name. |
| `ConnectedSystemId` | `int` | No |  | Retry the changes for this Connected System. |
| `State` | `string` | No |  | Retry only changes in this state. One of `Retrying` or `Parked`. |
| `FailureKind` | `string` | No |  | Retry only changes whose last attempt failed this way. One of `ObjectNotFound`, `Refused`, `InsufficientPermissions`, `AuthenticationFailed` or `Unreachable`. |
| `MetaverseObjectId` | `guid` | No |  | Retry only this Metaverse Object's changes. |
| `ConnectedSystemObjectId` | `guid` | No |  | Retry only this account's change. |
| `Search` | `string` | No |  | Retry only changes matching this free-text search over the Metaverse Object and Connected System names, as `Get-JIMPendingExchangeChange -Search` lists them. |
| `EntireQueue` | `switch` | No |  | Retry every parked or retrying change in the deployment. Required when nothing else narrows the request, so a command that names no criteria cannot act on everything by accident. |
| `Force` | `switch` | No |  | Skips the confirmation prompt. |

### Output

One object, whose AffectedCount says how many changes were made due again. Zero is an answer, not an error.

| Property | Type | Description |
|----------|------|-------------|
| `AffectedCount` | `int` | How many queued Exchange changes were made due again. Lower than the number named is not an error: a change delivered, claimed or already retried since the caller read the queue no longer matches. |
<!-- END GENERATED: Resume-JIMPendingExchangeChange -->

### Examples

```powershell title="After fixing what Exchange refused"
Resume-JIMPendingExchangeChange -ConnectedSystemId 3 -State Parked
```

```powershell title="Retry everything parked for want of permissions, in one request"
Get-JIMPendingExchangeChange -State Parked -FailureKind InsufficientPermissions -All | Resume-JIMPendingExchangeChange -Force
```

```powershell title="See what would be retried without retrying it"
Resume-JIMPendingExchangeChange -State Retrying -WhatIf
```

!!! note "One request, one Activity"
    However many changes are piped in, this is one request, recorded as one Activity attributed to you and under each
    retried change's own Activity.
