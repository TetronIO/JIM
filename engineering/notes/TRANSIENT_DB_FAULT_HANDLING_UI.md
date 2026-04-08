# Transient Database Fault Handling in Blazor UI

## Status: Not Planned (2026-04-08)

Investigation into improving the Blazor UI's resilience to transient database failures during heavy load. Decision: not implementing now, but documenting the analysis and approach for future reference.

## Background

During Scale100K testing (s8 template), while the worker was processing exports/confirming imports/syncs on ~100K objects, navigating to the MVO list page (`/t/users`) triggered an unhandled error page. The root cause was a Npgsql read timeout (`System.TimeoutException: Timeout during reading attempt`) on the paginated query in `MetaverseRepository.GetMetaverseObjectsOfTypeAsync`, which uses `AsSplitQuery()`.

The exception propagated through all layers (repository, application, Blazor page) to the `LoggingErrorBoundary` in `MainLayout.razor`, which replaced the entire page with the "Something Went Wrong" error display.

This was observed in a Codespaces devcontainer with constrained resources. It has not been observed in a properly resourced deployment.

## Current Error Handling

- **API requests** (`/api/*`): `GlobalExceptionHandler` middleware detects transient DB exceptions and returns HTTP 503 with `Retry-After` header. Well handled.
- **Blazor server-side**: No transient fault handling. Exceptions propagate to the error boundary, which replaces the entire page. The user must click "Try Again" or navigate away.
- **DbContext**: No `EnableRetryOnFailure` configured because manual transactions (`BeginTransactionAsync`) are incompatible with `NpgsqlRetryingExecutionStrategy` (see issue #408).
- **Command timeout**: Not explicitly set; defaults to Npgsql's 30 seconds.

## Proposed Approach (If Revisited)

### Shared utility: `TransientFaultHandler`

A static helper providing:
- `ExecuteAsync<T>(Func<Task<T>>)`: retries 1-2 times with short backoff for transient exceptions
- `IsTransient(Exception)`: reuses the detection logic already in `GlobalExceptionHandler` (checks for `NpgsqlException.IsTransient`, wrapping `DbUpdateException`/`InvalidOperationException` with "transient failure" in message)

### Per-page changes (~12 lines each, ~15 components affected)

1. Add `private string? _transientError;` field
2. Clear `_transientError` at top of `ServerReload`
3. Wrap the DB call in `TransientFaultHandler.ExecuteAsync()`
4. Add `catch (Exception ex) when (TransientFaultHandler.IsTransient(ex))` that sets `_transientError = "JIM is busy; click to retry."` and returns empty `TableData`
5. Add a `<MudAlert>` in markup to display the error inline with a retry action

This keeps the user on the page with context preserved rather than showing the full error boundary.

### Components that would need updating

All `ServerReload`/`ServerData` callback methods in:
- `Pages/Types/Index.razor`
- `Pages/Admin/PendingExportList.razor`
- `Pages/Admin/PendingDeletionList.razor`
- `Pages/Admin/SchemaObjectTypeList.razor`
- `Pages/Admin/ConnectedSystemObjectDetail.razor`
- `Pages/Admin/ConnectedSystemObjectList.razor`
- `Pages/Admin/Components/OperationsSchedulesTab.razor`
- `Pages/Admin/ApiKeyDetail.razor`
- `Pages/ActivityDetail.razor`
- `Pages/ActivityList.razor`
- `Shared/MvoDetailsTabs.razor`
- `Shared/MvoDetailsPanel.razor`
- `Shared/CsoMvaDialog.razor`
- `Shared/MvoMvaDialog.razor`
- `Shared/PendingExportMvaDialog.razor`

### Alternative quick win

Set an explicit command timeout on the web UI connection string (e.g., 60s instead of the 30s default). One-line config change, zero component changes. Reduces frequency but doesn't improve UX.

## Decision

**Not implementing.** Reasons:

1. Query performance improvements (paginated list optimisations, #482) have already reduced the likelihood of this scenario significantly
2. The existing error boundary UX is functional: it shows the error, offers "Try Again", and resets on navigation
3. Touching 15 components for a scenario primarily observed under Codespaces resource constraints is speculative work
4. Real customer deployments will have properly resourced database servers

**Revisit if:** transient DB timeouts are observed in a real customer deployment, or if new heavy-load scenarios (larger scale templates, concurrent users) make this more frequent.
