# One Password Pipeline: Implementation Plan

- **Status:** Doing
- **Issue:** [#1635](https://github.com/TetronIO/JIM/issues/1635)
- **PRD:** [PRD_PASSWORD_SYNCHRONISATION.md](../../prd/doing/PRD_PASSWORD_SYNCHRONISATION.md) (see its Amendment section)
- **Created:** 2026-09-05

## Overview

Three connected changes, delivered as one stacked set of pull requests, bottom-up:

| Layer | Branch | Delivers |
|---|---|---|
| 1 | `feature/operations-health` | Operations gains a **Passwords** tab (the queue page moves in) and a **Service Health** strip fed by database heartbeats; an administrator-only banner when a service is not seen; `GET /api/v1/system/health` and `Get-JIMServiceHealth`. |
| 2 | `feature/operations-health-stack-password-delivery-service` | A dedicated **Password Delivery Service** replaces the `PasswordDeliveryWorkerTask`: woken by a database notification, claims rows per Connected System, delivers immediately, reports its own heartbeat. Callers can wait briefly for outcomes. |
| 3 | `feature/operations-health-stack-password-pipeline` | **Set Password** and **Synchronise Password** converge on one operation over the queue, with target mode as a parameter. Synchronise Password is removed from all three surfaces. |

Layer 2 comes before layer 3 deliberately. Converging first would route every administrator reset through a delivery path that waits behind sync runs; building the service first means the convergence lands on a pipeline that already answers in about a second.

## Business Value

- An administrator resetting a password for somebody on the telephone gets an answer in about a second, whatever the sync engine is doing. Today a queued change waits for the running import to finish.
- One verb, one queue, one record. Resets and propagated changes share retry, parking, coalescing, history and the person's Password tab; the "never synchronised a password change" gap after a reset closes by construction.
- Administrators can see whether the Worker and Scheduler are running, what they are doing, and whether a running task has stalled, without a container console. Version per service catches a web tier upgraded ahead of its worker.

## Resolved design decisions

Settled during review (2026-09-05); the plan implements them rather than revisiting them.

| # | Decision | Resolution |
|---|---|---|
| D1 | Explicit reset aimed at a system whose Password Synchronisation is switched off | Delivered anyway. The administrator named the account. The dialog says the system is paused for propagation. Propagated changes are still held. |
| D2 | Where the first delivery attempt runs | The Password Delivery Service, never inline in the caller. One deliverer, no double-delivery race. |
| D3 | Host process for the service | `jim.worker`, as a second `BackgroundService`. Code depends only on `JimApplication`, so a dedicated container later is a Program and compose change. Not `jim.web` (request-bound) and not `jim.scheduler` (no reason to reach directories). |
| D4 | The "stored nothing" promise | Replaced with "held encrypted only until delivered; kept on refusal until it expires or is retained out", worded that way in the dialog, endpoint remarks and docs. |
| D5 | PowerShell default with no system named | Every Connected System configured for Password Synchronisation (the event case). `-AllAccounts` retired; scripts wanting all of a person's accounts pass ids to `-ConnectedSystemId`. |
| D6 | API wait semantics | Named accounts: wait up to 10 s for outcomes, return 200 with per-target outcomes, or 202 with what is known. Every configured system: return on enqueue. An optional `wait` (seconds, 0 to 30) overrides either. |
| D7 | Initial passwords on provisioning | Out of scope. They keep their own store and the export run; they share only the extracted delivery core. |
| D8 | Health thresholds | Heartbeat every 5 s. Unhealthy ("no heartbeat") after 60 s (Worker) and 120 s (Scheduler), matching the container checks. Degraded ("stalled") when a running task's progress has not changed for 10 min; Degraded ("heartbeat overdue") after 15 s. Constants now; Service Settings later if anyone needs to tune them. |
| D9 | Operations tab label and old route | **Passwords**. `/admin/password-synchronisation` is unreleased and is dropped; internal links move to `/admin/operations?t=passwords`. |
| D10 | Where health shows | Operations (full strip, always); Administration index (red dot on the Operations tile when unhealthy); a banner above page content for administrators only when a service is not seen or a task has stalled; nothing on Home. |

## Technical Architecture

### Current state

- `PasswordSynchronisationServer.QueuePasswordChangeAsync` writes one `PendingPasswordChange` per configured system and calls `Tasking.RequestPasswordDeliveryAsync`, which enqueues a `PasswordDeliveryWorkerTask`. `Worker.ExecuteAsync` polls for new tasks only when `CurrentTasks` is empty, and the housekeeping tick that catches due retries runs only on that idle branch.
- `ConnectedSystemServer.SetPasswordOnAccountsAsync` writes passwords synchronously from the calling process (the web tier for the portal), with no queue row and no retry.
- Liveness: each of `jim.worker` and `jim.scheduler` touches `/tmp/healthcheck`; the container runtime checks its age. Nothing in the product reads it.
- Three copies of the open channel, security check, set, classify sequence: `ConnectedSystemServer.ApplyPasswordAsync`, `PasswordSynchronisationServer.DeliverDuePasswordChangesAsync`, `InitialPasswordDeliveryServer`.

### Layer 1: Operations and Service Health

**Data.** `ServiceHeartbeat` (`JIM.Models/System/`): `Id`, `Service` (`JimService` enum: `WorkerSync`, `WorkerDelivery`, `Scheduler`), `InstanceId` (host name plus a per-process id), `HostName`, `Version`, `StartedAt`, `LastSeenAt`, `CurrentWork` (nullable text, e.g. "Full Import: Corporate Directory"), `CurrentWorkStartedAt`, `LastProgressAt`, `Detail` (nullable text). Unique index on (`Service`, `InstanceId`). One migration.

**Writers.** `ServiceHeartbeatWriter` (JIM.Application) upserts a row every 5 s, called from the same place each loop touches the healthcheck file today (`Worker.cs`, `Scheduler.cs`). The Worker writes `CurrentWork` from its in-flight tasks. On startup a writer deletes rows for its own service older than 24 h.

**Read model.** `SystemHealthServer.GetServiceHealthAsync()` (exposed as `JimApplication.SystemHealth`) returns a `ServiceHealthReport`: per service the newest instance, its status (`Healthy`, `Degraded`, `Unhealthy`) and the condition behind it (`Heartbeating`, `HeartbeatOverdue`, `Stalled`, `NoHeartbeat`, `NeverStarted`), reason text, and the fields above; plus the web tier's version and a generated-at timestamp. `Stalled` is derived from the Processing worker task's Activity: if the Activity exposes a progress timestamp use it, otherwise omit the condition and say so in code. A service with no row at all: `Unhealthy` / `NeverStarted` with reason "Never started". The report's `Overall` is the worst state present.

**Portal.** `ServiceHealthStrip` component at the top of `Operations.razor`, a panel with a header (title, worst-first summary, **Live updates** indicator from `IUiNotificationService.IsRealTimeAvailable`) over a grid of identical cards, one per service, polling every 10 s (heartbeat writes are too frequent for the relay). `ServiceHealthBanner` rendered in `MainLayout` above `@Body` for Administrators only, visible only when a service is `Unhealthy` or `Stalled`, styled after `.jim-instance-bar` but in the page flow, linking to Operations and to Logs. Red dot on the Operations tile on the Administration index when unhealthy; the Password Synchronisation tile is removed.

**Passwords tab.** The body of `PasswordSynchronisationQueue.razor` becomes `OperationsPasswordsTab.razor` (a component, like the other three tabs). Tab text "Passwords", key icon, amber badge with parked plus expired counts. Query parameters the deep links use (`metaverseObjectId`, `connectedSystemId`) keep working on the Operations route. Deep links updated: `MetaverseObjectPasswordSynchronisationPanel.QueueHref`, the Connected Systems list indicator text, `AdminIndex`, and any docs.

**Parity.** `GET /api/v1/system/health` on `SystemController` (Administrator), returning the report; `Get-JIMServiceHealth` under `Public/System`. OpenAPI regenerated.

**Tests.** NUnit for state derivation (thresholds, never reported, no progress), the writer's upsert and prune; bUnit for the strip's states and for the Passwords tab rendering the queue with a person filter; Pester for the cmdlet.

### Layer 2: Password Delivery Service

**Claiming.** `PendingPasswordChange` gains `ClaimedAt` and `ClaimedBy`; `PendingPasswordChangeStatus.Delivering` is added. `SyncRepository.ClaimDuePasswordChangesAsync(connectedSystemId, instanceId, asOf, max)` claims with `FOR UPDATE SKIP LOCKED`, setting status and claim columns in one statement. A claim older than 60 s is reclaimable (lease). One migration, which also adds the triggers below.

**Wake-up.** Triggers on `PendingPasswordChanges` insert, update and delete call `pg_notify('jim_password_change', <ConnectedSystemId>)`, following the pattern in `20260723204302_AddRealTimeNotificationTriggers`. The listener is `PostgresNotificationListener` (already in JIM.PostgresData).

**Service.** `PasswordDeliveryService : BackgroundService` in JIM.Worker (registered with `AddHostedService` and `BackgroundServiceExceptionBehavior.Ignore`, wrapped in its own catch-all with a restart delay). Loop: wait on (notification | earliest `NextRetryAt` | 30 s safety poll); for each Connected System with due rows run one lane; lanes run in parallel across systems, bounded by a semaphore of 4, sequential within a system. A lane: claim, expire outlived rows, open the password channel once, deliver each row through the existing `DeliverOneAsync` logic, close, persist attempts, delete delivered. Writes the `WorkerDelivery` heartbeat with `CurrentWork` and queue counts. Uses a fresh `JimApplication` per lane, as worker tasks do.

**Removed.** `PasswordDeliveryWorkerTask`, `TaskingServer.RequestPasswordDeliveryAsync`, `HasQueuedPasswordDeliveryTaskAsync`, the housekeeping tick request in `Worker.PerformHousekeepingAsync`, and the per-pass Activity. Retry from the queue page and `ReleaseForDeliveryAsync` just touch rows; the trigger wakes the service.

**Feedback.** `PasswordSynchronisationServer.WaitForOutcomesAsync(activityId, timeout, cancellationToken)` subscribes to `jim_password_change` and `jim_activity_progress` through the listener already hosted in the web process (`NotificationListenerService` relays to `IUiNotificationService`), re-reads the rows and child Activities for the change on each notification, and returns when every row is terminal or rescheduled, or on timeout. The web dialog and REST use it. Response shape: per target `Set`, `Retrying` (next attempt), `Parked` (reason), `Held` (paused), `Queued` (not yet attempted).

**Health card.** `Worker · Passwords` card on the strip from layer 1: state, last delivery, due count, retrying count, next attempt.

**Tests.** Claim and lease semantics (two claimers, one winner; expired lease reclaimable); wake on notify; wake on earliest retry; lane isolation (one system down does not delay another); heartbeat written; `WaitForOutcomesAsync` returns on completion and on timeout. Integration scenario: a change queued while a Full Import is running is delivered within five seconds; a parked row retried from the queue page is attempted within a second.

### Layer 3: One password operation

**Application.** `PasswordSynchronisationServer.SetPasswordAsync(request)` where the request carries the Metaverse Object, password, `Targets` (null for every configured system; otherwise a list of Connected System Object ids), `ExpiryBehaviour`, `EnableAccount`, and the initiator. `PendingPasswordChange` gains `Origin` (`Explicit`, `Propagated`) and `EnableAccount`. Explicit rows resolve their account from `ConnectedSystemObjectId`, use `ConnectedSystemPasswordSynchronisation` defaults when the system has no configuration, and are delivered even when the configuration is disabled (D1). Propagated rows behave exactly as today, including `EnableAccount = null`. One Activity shape for both origins: parent (`ActivityTargetType.PasswordSynchronisation`) per change, child per system. `ConnectedSystemServer.SetPasswordOnAccountsAsync` and `SetConnectedSystemObjectPasswordAsync` are removed; the CSO page and account-scoped endpoint call the new operation with one target. The open, check, set, classify sequence is extracted once (`PasswordDeliveryCore`) and used by the service and by `InitialPasswordDeliveryServer`.

**Portal.** The person page gets the single **Password** tab designed in review: attention strip for parked or expired rows with Retry, the Set Password card with the capable-account count, Still to be delivered with per-row Retry, Recent password changes with a kind chip (`Set` or `Propagated`). `SetPasswordDialog` keeps its composition; its result stage shows `Set`, `Retrying` (next attempt, Stop trying), `Parked` (target's words, `Try another password` regenerates for all), `Held`, driven by `WaitForOutcomesAsync` and live relay updates. `SynchronisePasswordDialog` and the Actions tab are removed.

**REST and PowerShell.** `POST /api/v1/metaverse/objects/{id}/password` takes `password`, optional `connectedSystemObjectIds`, `expiryBehaviour`, `enableAccount`, `wait`; returns the per-target outcome shape and the Activity id. The account-scoped endpoint stays as a one-target wrapper with the same response. `Set-JIMMetaverseObjectPassword` gains the propagate default (D5) and `-Wait`; `Sync-JIMMetaverseObjectPassword` is removed. `Set-JIMConnectedSystemObjectPassword` unchanged in shape.

**Docs and changelog.** `docs/concepts/passwords.md`: one operation, two target modes, the delivery promise, the stored-until-delivered wording (D4). The Unreleased changelog entries that describe Synchronise Password as a separate action are amended so 0.15.0 reads as the final shape.

**Tests.** Explicit row without configuration delivers; explicit row bypasses a paused system; propagated row is held; `EnableAccount` honoured only on explicit rows; the Activity shape is identical for both origins; the person's history shows a reset; dialog result states; endpoint and cmdlet contract tests.

### Deviations

Recorded during layer 3 (2026-09-05), REST and PowerShell:

- **`Set-JIMConnectedSystemObjectPassword` changed shape after all.** The plan said "unchanged in shape", but once the account-scoped endpoint queues and waits, the old `-PassThru` object (applied expiry behaviour and a warning) no longer exists: the truthful result is the per-target outcome, and a refusal is a `Parked` target rather than a thrown error. The cmdlet now always returns the same outcome object `Set-JIMMetaverseObjectPassword` returns, `-PassThru` is removed, `-Wait` is added, and a generated password is carried on `GeneratedPassword` (was the lower-case `password`), matching the person-scoped cmdlet. Neither cmdlet shape had shipped.
- **A `Parked` target is also surfaced as a non-terminating error** by both cmdlets, with the result as the error's `TargetObject`, so a script that stops on errors stops on a refusal. The plan named only the outcome shape; without the error a refusal would be silent in `-ErrorAction Stop` scripts that never inspect `Targets`.
- **`-Generate` with no system named** generates against every Connected System the person has an account in, since the cmdlet cannot see which of those are configured for Password Synchronisation without a further call, and the strictest policy across all of them is the safe superset.

## Success Criteria

- A Set Password on three accounts, started while a Full Import is running, reports per-account outcomes in the dialog within five seconds (integration test).
- No `PasswordDeliveryWorkerTask` exists; the Worker's task queue never carries password work.
- Operations shows Worker, Scheduler and password delivery state, last heartbeat, current work and version; stopping `jim.worker` turns its cards red within 60 s and shows the banner to an administrator on any page.
- One REST endpoint and one cmdlet set a password in either target mode; `Sync-JIMMetaverseObjectPassword` and the Synchronise Password dialog no longer exist.
- `dotnet build JIM.sln` and `dotnet test JIM.sln` are clean at every layer; Pester passes; OpenAPI document regenerated.

## Dependencies

None new. Reuses `PostgresNotificationListener`, the SignalR relay, `PasswordChannelSecurity`, and the queue repository.

## Risks and Mitigations

| Risk | Mitigation |
|---|---|
| Service loop fault takes the sync loop down | Separate `BackgroundService`, catch-all with restart delay, `BackgroundServiceExceptionBehavior.Ignore` for the host. |
| Double delivery across the service and a second worker replica | Row claim with `SKIP LOCKED` and a lease; re-sending a password already set is harmless, and a stuck row is worse. |
| Trigger storm from bulk enqueue | The listener coalesces per system; the service runs one lane per system, bounded. |
| Intermediate state where resets are slow | Layer 2 lands before layer 3; the stack is merged from the top so `main` never carries layer 3 without layer 2. |
| Heartbeat writes competing with a busy database | One upsert per service per 5 s; the read side polls every 10 s from the strip only. |
