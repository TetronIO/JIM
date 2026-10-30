# Export Execution Flow

> Last updated: 2026-10-29, JIM v0.17.0

This diagram shows how Pending Exports are executed against Connected Systems via connectors. The export processor (`SyncExportTaskProcessor`) uses `ISyncServer` to delegate to `ExportExecutionServer` for the core execution logic, and `ISyncRepository` for bulk data access. Supports batching, parallelism, deferred reference resolution, a retry of every failing export at every export run, and per-Run Profile limits on how many creates, updates and deletes a run may send.

Since v0.10.0, connector exceptions thrown during export are always reported as RPEIs. The file-based outer catch in `ExportExecutionServer` and the call-based batch catches (immediate, sequential deferred and parallel) each create `ProcessedExportItems` for every export in the affected scope, naming its Pending Export and object. Previously, a thrown connector exception set `FailedCount` without creating RPEIs, so the activity could complete successfully despite silent export failures. Per-batch streaming via `batchCompletedCallback` keeps in-memory `ProcessedExportItem` accumulation bounded at 100K+ exports.

## Export Task Processing

```mermaid
flowchart TD
    Start([PerformExportAsync]) --> CountPE[Count Pending Exports<br/>for Connected System]
    CountPE --> HasExports{Pending Exports<br/>> 0?}
    HasExports -->|No| NoWork[Update activity:<br/>No exports to process]
    NoWork --> Done([Return])

    HasExports -->|Yes| CheckConnector{Connector supports<br/>export?}
    CheckConnector -->|No| FailActivity[FailActivityWithErrorAsync:<br/>Connector does not support export]
    FailActivity --> Done

    CheckConnector -->|Yes| CheckCancel{Cancellation<br/>requested?}
    CheckCancel -->|Yes| CancelMsg[Update activity:<br/>Cancelled before export]
    CancelMsg --> Done

    CheckCancel -->|No| Options[Resolve export parallelism<br/>explicit Max Export Parallelism, else<br/>connector recommendation, else 1<br/>Carry the Run Profile's Max creates,<br/>Max updates and Max deletes, #1629]
    Options --> Execute[ExportExecutionServer.ExecuteExportsAsync<br/>See Export Execution below<br/>The managed scope is stated as each<br/>connector is prepared, #1250 #1764]
    Execute --> ProcessResult[ProcessExportResultAsync<br/>RPEIs were streamed per batch:<br/>- Create --> Exported<br/>- Update --> Exported<br/>- Delete --> Deprovisioned<br/>- Deferred whole --> Pending Export item<br/>- Failed --> classified error type, #2118,<br/>with its attempt count<br/>- Unresolved reference --> UnresolvedReference]

    ProcessResult --> Withheld{Any change type<br/>withheld by a limit,<br/>or queued changes<br/>withdrawn?}
    Withheld -->|Yes| WarnWithheld[Record withheld counts<br/>Activity warning names the limit,<br/>the pending count and the remedy,<br/>or the withdrawn change count]
    Withheld -->|No| CheckContainers
    WarnWithheld --> CheckContainers{New containers<br/>created during export?}
    CheckContainers -->|Yes| AutoSelect[Auto-select new containers<br/>Refresh and select containers<br/>by created external IDs<br/>Ensures they appear in future imports<br/>A cancelled or failed run selects<br/>them too, best-effort, #2197]
    CheckContainers -->|No| Done
    AutoSelect --> Done
```

## Export Execution (ExportExecutionServer)

```mermaid
flowchart TD
    Start([ExecuteExportsAsync]) --> Withdraw[Unless PreviewOnly: withdraw queued<br/>changes nothing authorises any more, #1885<br/>rule deleted or disabled, no enabled<br/>mapping, or CSO no longer joined<br/>Update PE left empty is deleted<br/>Executing PEs never touched]
    Withdraw --> GetExecutable[Establish whether there is executable work<br/>Database filter: Status Pending, ExportNotConfirmed<br/>or Exported, and an Update must have<br/>a change still to send<br/>No count or age of failed attempts<br/>keeps an export out, #2119<br/>Delete exports already exported are skipped<br/>Creates already exported are skipped while<br/>they await confirmation, #1687<br/>The same filters drive the batch sweep below]
    GetExecutable --> HasExports{Exports<br/>found?}
    HasExports -->|No| EmptyResult([Return empty result])

    HasExports -->|Yes| CheckPreview{Run mode =<br/>PreviewOnly?}
    CheckPreview -->|Yes| PreviewResult[Return export IDs<br/>without executing]
    PreviewResult --> Done([Return result])

    CheckPreview -->|No| Limits{Run Profile has<br/>export limits?}
    Limits -->|Yes| Ledger[Count executable exports per change type<br/>ExportChangeLimitLedger withholds, in full,<br/>any type whose count exceeds its limit<br/>Withheld exports stay Pending, untouched]
    Limits -->|No| ConnectorType
    Ledger --> ConnectorType{Connector<br/>export type?}

    ConnectorType -->|IConnectorExportUsingCalls| PrepareConnector[PrepareConnectorForExport, #1764:<br/>state the managed scope, #1250,<br/>container selections and exclusions,<br/>if the connector implements<br/>IConnectorManagedScope<br/>Inject CertificateProvider<br/>and CredentialProtection<br/>Every parallel worker's connector<br/>is prepared the same way]
    PrepareConnector --> OpenExport[OpenExportConnection<br/>with system settings]

    OpenExport --> ParallelCheck{MaxParallelism > 1,<br/>factories provided and<br/>more than one batch?}
    ParallelCheck -->|Yes| ParallelBatch[Parallel workers, #1820<br/>up to MaxParallelism, each keeping<br/>one connector for every batch it runs<br/>See Parallel Batch Architecture below<br/>A failed batch stops further<br/>batches being taken]
    ParallelCheck -->|No| SequentialBatch[One batch at a time<br/>on the primary connector<br/>and the caller's DbContext<br/>A failed batch ends the pass]
    ParallelBatch --> Collect
    SequentialBatch --> Collect

    %% --- Batch collection: single forward keyset sweep, read on demand ---
    Collect[Read the next page of Pending Exports<br/>on demand, one reader for both runners<br/>keyset cursor on CreatedAt, Id, #985<br/>never rescans from the start<br/>withheld change types excluded] --> Empty{Page<br/>empty?}
    Empty -->|Yes| HasDeferred
    Empty -->|No| SplitExports[Split the page into:<br/>- Immediate exports: no unresolved references<br/>- Deferred exports: have unresolved references,<br/>collected by id only]

    %% --- Immediate exports ---
    SplitExports --> HasImmediate{Immediate<br/>exports?}
    HasImmediate -->|Yes| RunBatch[Run the batch<br/>See Batch Execution Detail below<br/>ClearChangeTracker after each batch]
    RunBatch --> Collect

    %% --- All-deferred fast path ---
    HasImmediate -->|No, whole page deferred| Probe{Any executable<br/>non-deferred exports<br/>beyond the cursor?}
    Probe -->|Yes| Collect
    Probe -->|No| CollectRest[Fast path #985c:<br/>collect the ids of ALL remaining<br/>deferred exports in one query<br/>and stop the sweep]
    CollectRest --> HasDeferred

    %% --- Deferred exports, a chunk at a time ---
    HasDeferred{Deferred<br/>exports collected?} -->|Yes| LoadChunk[Load the next chunk of deferred<br/>exports by id, 2,000 at a time,<br/>DeferredChunkSize<br/>Fetch what each referenced MVO's<br/>object is written as in one<br/>Summary-tier query, ReferenceAnchorSummary]
    LoadChunk --> ResolveRefs[For each deferred export:<br/>Try to resolve MVO references<br/>to target system anchors]
    ResolveRefs --> Resolved{References<br/>resolved?}
    Resolved -->|Yes, or something<br/>writable now, #1398| PersistRefs[Persist the chunk's resolutions BEFORE<br/>its batches run, #994<br/>a parallel batch loads its own copy and<br/>would otherwise export raw MVO IDs]
    PersistRefs --> ExportResolved[Batch export as for immediate exports<br/>parallel when there is more than one batch<br/>A failed deferred batch leaves parallel<br/>batches running, and ends a sequential pass]
    Resolved -->|No, nothing writable| MarkDeferred[Mark as deferred<br/>Attempted again next run]
    ExportResolved --> MoreChunks{More<br/>chunks?}
    MarkDeferred --> MoreChunks
    MoreChunks -->|Yes| LoadChunk
    MoreChunks -->|No| CaptureContainers

    HasDeferred -->|No| CaptureContainers
    CaptureContainers[Capture created container external IDs<br/>from the connector before its connection<br/>closes, on every exit path, #2197]
    CaptureContainers --> CloseExport[CloseExportConnection<br/>persist any connector data it returns, #230]
    CloseExport --> SecondPass[Unless cancelled: re-resolve every export<br/>still waiting on references and save<br/>those now resolved for the next run<br/>single indexed query on the<br/>unresolved-references partial index, #1102]
    SecondPass --> Done

    ConnectorType -->|IConnectorExportUsingFiles| FileExport[File-based export<br/>with batching<br/>reserved against the ledger<br/>before the file is written<br/>never marked Executing<br/>Exported values recorded on the CSO<br/>Auto-confirming connectors delete<br/>each Pending Export once recorded, #1936]
    FileExport --> SecondPass
```

## Batch Execution Detail

Each batch follows this sequence, whether processed sequentially or in parallel:

```mermaid
flowchart TD
    Start([Process batch]) --> MarkExecuting[Mark all exports in batch<br/>as Status = Executing<br/>snapshotting each export's retry state<br/>and the time of the mark, #2083 #2197]
    MarkExecuting --> InitialPasswordLookup[Read once for the batch which<br/>provisioning rules ask for an<br/>Initial Password, and which<br/>updated accounts are still owed<br/>a password, #2039 #2158<br/>Flag those Creates and Updates<br/>InitialPasswordFollows]
    InitialPasswordLookup --> PreSend[Pre-send refusals, failed and not sent:<br/>more than one change for a<br/>single-valued attribute, #2182,<br/>a fault in JIM, or an object class<br/>added without values for its<br/>required attributes, #492<br/>ClassMembershipRequirementsNotMet]
    PreSend --> CallConnector[connector.ExportAsync<br/>Send the rest via ForConnector:<br/>only what can be written now<br/>LDAP on Active Directory: a flagged<br/>Create sends an enabled<br/>userAccountControl with<br/>ACCOUNTDISABLE added, #2039;<br/>a flagged Update refused for<br/>want of a password is sent<br/>again so, #2158<br/>Returns List of ExportResult]
    CallConnector -.->|Batch throws| BatchFail[Cancelled: put the batch back as it was,<br/>no attempt counted<br/>Otherwise one failed attempt on each<br/>export still Executing, RPEI each, #2083]
    CallConnector --> MarkOwed[Mark any first password still owed<br/>to an account whose enabled state<br/>the export wrote, #2064 #2173<br/>straight from the results, before<br/>anything that can throw]
    MarkOwed --> ProcessResults[For each export + result pair:<br/>classify only, nothing written yet<br/>A missing result is never a success, #2197]
    ProcessResults --> CheckResult{Export<br/>succeeded?}

    CheckResult -->|Not attempted,<br/>run cancelled| PutBack[Put back as it was<br/>before the mark, first<br/>No attempt counted, no RPEI]
    CheckResult -->|Yes, Create| HandleCreate[Record Exported<br/>Capture new external ID<br/>from ExportResult<br/>Set Status = Exported<br/>Mark the changes the connector<br/>wrote with an interim value<br/>AwaitsInitialPassword, #2039<br/>as for a flagged Update, #2158]
    CheckResult -->|Yes, Update| HandleUpdate[Record Exported<br/>Set Status = Exported<br/>Clear the failure record]
    CheckResult -->|Yes, but references<br/>still unresolved| HandlePartial[Written in part, #1398<br/>Status stays Pending with<br/>HasUnresolvedReferences<br/>A Create becomes an Update]
    CheckResult -->|Yes, Delete| CsoStatus{CSO status =<br/>PendingProvisioning?}
    CsoStatus -->|No, Normal| HandleDelete[Record Deprovisioned<br/>Set Status = Exported<br/>confirming import deletes PE and CSO]
    CsoStatus -->|Yes: provisioning was<br/>never confirmed| HandleUnconfirmedDelete[Record Deprovisioned<br/>Pending Export and CSO removed<br/>once the results are recorded:<br/>import deletion detection excludes<br/>PendingProvisioning objects, so a<br/>successful Delete is the only<br/>confirmation the object will ever get]
    CheckResult -->|Value already in use,<br/>Collision Remediation on| HoldCollision[Held for Collision Remediation, #242<br/>decided once the results are recorded]
    CheckResult -->|Failed, refused, no result,<br/>or not attempted while<br/>the run carries on| HandleFail[Record a failed attempt, #2119:<br/>ErrorCount + 1, FailingSince set<br/>on the first, error message kept<br/>Status = Pending, attempted again<br/>at the next export run<br/>RPEI carries the classified error, #2118]

    PutBack --> Record
    HandleCreate --> Record
    HandleUpdate --> Record
    HandlePartial --> Record
    HandleDelete --> Record
    HandleUnconfirmedDelete --> Record
    HoldCollision --> Record
    HandleFail --> Record

    Record[Record the batch's results first, #2197:<br/>Pending Export statuses and each Create's<br/>new External ID in one transaction<br/>A Create owed an Initial Password stays<br/>Executing until it is staged, #2270<br/>A failure here records nothing, and the<br/>batch is reported failed as a whole]
    Record --> Collision[Collision Remediation, each held value<br/>decided and recorded on its own:<br/>corrected, stays Pending for the next<br/>synchronisation to re-stage<br/>needs a decision, Parked<br/>neither, an ordinary failed attempt]
    Collision --> Housekeeping[Clear rename allowances the batch's<br/>successes made moot<br/>Remove the Pending Export and CSO of<br/>each never-confirmed provisioning's Delete]
    Housekeeping --> InitialPassword{Provisioning rule<br/>asks for an<br/>initial password?<br/>the batch's lookup}
    InitialPassword -->|Yes| StagePassword[Stage a Provisioned password change<br/>on the Password Synchronisation queue<br/>for the Password Delivery Service, #1706<br/>after the external ID is assigned<br/>then write the held Creates, #2270]
    InitialPassword -->|No| Optimistic
    StagePassword --> Optimistic
    Optimistic[Optimistic export apply #1079:<br/>write the exported values onto the CSO now<br/>rather than waiting for the confirming import<br/>Delete change types skipped, and so are<br/>changes written with an interim value #2039.<br/>A value of another type is not recorded #2183.<br/>Failures self-heal on the next import]
    Optimistic --> CaptureItems[Hand the batch's ProcessedExportItems<br/>to the processor, which writes<br/>its RPEIs, batchCompletedCallback]
    CaptureItems --> Done([Batch complete<br/>a parallel batch's DbContext is disposed<br/>and its worker keeps its connector])
```

## LDAP Export Consolidation and Chunking

For LDAP connectors, individual attribute changes are consolidated and chunked before being sent to the directory server. This ensures RFC 4511 compliance and prevents server rejection of oversized modify requests.

```mermaid
flowchart TD
    Input([Pending Export<br/>with attribute changes]) --> Consolidate[ConsolidateModifications:<br/>Group changes by attribute name<br/>and operation type]

    Consolidate --> Example1[Example: 200 individual<br/>member Add changes]
    Example1 --> Merged[Consolidated into single<br/>DirectoryAttributeModification<br/>with 200 values]

    Merged --> CheckSize{Values ><br/>batch size?}
    CheckSize -->|No| SingleRequest[Single ModifyRequest<br/>with all values]
    CheckSize -->|Yes| Chunk[ChunkModifyRequests:<br/>Split into batches<br/>of configurable size<br/>Default: 1000]
    Chunk --> MultiRequest[Multiple ModifyRequests<br/>sent sequentially<br/>e.g., 2 requests of 1000 values]

    SingleRequest --> Send([Send to LDAP server])
    MultiRequest --> Send
```

**Batch size** is configurable via the "Modify Batch Size" connector setting (default: 1000, clamped to 10-5000, recommended 100-2000). The default was raised from 100 because several directory servers' per-modification cost grows with the group's current membership size, so fewer, larger requests cut total export time by an order of magnitude for groups with tens of thousands of members. Newly created Connected Systems pick up the new default; existing ones keep their stored value, since connector settings are captured per system at creation.

## Parallel Batch Architecture

When `MaxParallelism > 1` and there is more than one batch, both passes (the immediate exports and the deferred-reference pass) run their batches on up to `MaxParallelism` concurrent workers (`ProcessBatchesInParallelAsync`, #1820). Each batch is isolated on its own `DbContext` to avoid EF Core thread-safety issues, and each worker keeps one connector for every batch it runs.

```mermaid
flowchart TD
    Caller[Export Processor<br/>caller context] --> Gate[Caller-context gate<br/>SemaphoreSlim 1,1<br/>reading the next batch, progress,<br/>connector narration, the per-batch<br/>callback, persisting close data]

    Gate --> W1[Worker 0<br/>the primary connector<br/>takes the first batch]
    Gate --> W2[Worker 1<br/>own connector, opened<br/>on its first batch]
    Gate --> W3[Worker N-1<br/>own connector, opened<br/>on its first batch]

    W1 --> B1[Each batch: own DbContext<br/>Re-loads its PEs by ID<br/>Disposed as the batch completes]
    W2 --> B2[Each batch: own DbContext<br/>Re-loads its PEs by ID<br/>Disposed as the batch completes]
    W3 --> B3[Each batch: own DbContext<br/>Re-loads its PEs by ID<br/>Disposed as the batch completes]

    B1 --> ResultLock[Result Lock<br/>thread-safe aggregation]
    B2 --> ResultLock
    B3 --> ResultLock

    B1 -.->|next batch, on demand| Gate
    B2 -.->|next batch, on demand| Gate
    B3 -.->|next batch, on demand| Gate
```

- **Batches are taken on demand** - each worker starts on a batch of its own, taken in order so the primary connector gets the first, then takes the next whenever it is free; nothing is read ahead, so nothing is read and left unsent
- **Batch IDs are captured** when a batch is taken - the worker re-loads its exports from the batch's own DbContext by ID
- **The caller-context gate** serialises everything that touches the caller's shared DbContext: reading the next page, progress, connector narration, the per-batch RPEI callback and persisting what a connector returns at close
- **Result aggregation** uses a lock for thread-safe counter updates
- **Connector instances** belong to workers, not batches: a worker's own connector is created via the factory on its first batch and kept for every batch it runs; a connector whose batch fails is discarded (containers it created captured, its close data persisted) and the worker opens a fresh one for its next batch
- **A failed batch** stops further batches being taken on the first pass, while the batches already running finish and are recorded, as the sequential path stops at a failed batch; on the deferred pass the other batches carry on

## Key Design Decisions

- **Two-pass export**<br /> Exports without unresolved references are executed first (immediate). Exports with unresolved MVO references are deferred, collected by id only, then executed in a second pass a chunk at a time (`DeferredChunkSize`, 2,000): each chunk is loaded, its references resolved against one Summary-tier anchor query (`GetReferenceAnchorsByMetaverseObjectIdsAsync`), its resolutions saved, and its batches handed out before the next chunk is prepared. Loading, resolving and saving the whole deferred set before writing any of it left a 790,000-object export at 0% for six minutes and held most of the Worker's memory; a later chunk also resolves against objects an earlier chunk wrote.

- **Run Profile export limits (#1618, #1629)**<br /> An Export Run Profile may carry Max creates, Max updates and Max deletes. When any is set, the executable Pending Exports are counted per change type once at the start of the run, and `ExportChangeLimitLedger` withholds in full any type whose count exceeds its limit: never a head of the queue, so a frequent Schedule cannot trickle a wrong mass change through. Withheld exports stay `Pending` and untouched (not marked, not failed, given no RPEI), withheld types are excluded from the paging query, and one ledger is shared by both passes and both connector shapes. The Activity records the withheld counts and completes with a warning naming the limit and the remedy. An uncapped run does no extra database work.

- **An exported Create is never re-sent while it awaits confirmation (#1687)**<br /> A Create at `Exported` is filtered out of the executable set: re-sending it would ask the connector to create an object that already exists. Changes staged while it waits are appended to it as `Pending` attribute changes, and travel as one Update once the confirming import confirms the object and reconciliation turns the Create into an Update (see [Pending Export Lifecycle](PENDING_EXPORT_LIFECYCLE.md)). A Create at `ExportNotConfirmed` is still exportable, because it genuinely needs to go again.

- **Refusals JIM makes before, or instead of, the target system (#492, #1250, #2182)**<br /> Two refusals give an administrator an actionable error rather than whatever the target system would return. Before a batch is sent, any export that adds an object class without values for that class's required attributes is failed as `ClassMembershipRequirementsNotMet`, naming the attributes. During the export, a connector given a managed scope refuses per object any write outside the selected containers, so an Attribute Flow cannot move an object somewhere JIM could not read it back. A third refusal, checked first, points at JIM rather than configuration: an export carrying more than one change for a single-valued attribute (`SingleValuedAttributeChangeValidator`) is never sent, on the call-based and file-based paths alike, because a connector could apply the changes in an order that clears the value and report success; it is counted apart and logged as an Error. All three take the ordinary failure path, and the rest of the batch proceeds.

- **Initial passwords are staged, not delivered (#1121, #1706)**<br /> When a Create succeeds for an object whose provisioning Synchronisation Rule asks for an initial password, the export records a Provisioned password change on the Password Synchronisation queue, after the external ID has been assigned so the object can be addressed. The Password Delivery Service delivers it later over the connector's password channel; setting it inside the export would put a second network call in the loop persisting a write that has already succeeded. A staging failure is logged as an error and counted on the Activity, never turned into a failed export. The Create's Pending Export is written only after staging (#2270), so a password change waiting on the account (which waits while its Create is `Executing`, #2160) is still there for staging to find rather than delivered and removed first; a failure in between still writes it. Where the queue already holds a change for the person and system, one statement decides (#2160): a live change that will reach the account (a propagated change for the targeted Object Type, or an administrator's set naming it) carries the account's first password instead, keeping its own password and taking the obligation (`ProvisionedConnectedSystemObjectId`, `SyncRuleId`, the mark), so its delivery applies the rule's Enable Account and "Password never expires"; any other row is replaced (dead, parked beyond cure, held by a paused system, for another Object Type, an administrator's set whose account is gone or disjoined, an earlier Initial Password), since a person has one joined Connected System Object per Connected System and such a row cannot reach any of theirs there. Every outcome gets a staging Activity.

- **The connector is told a password will follow (#2039, #2158)**<br /> Two lookups, each made at most once per batch, before the batch is sent rather than after it succeeds, on the immediate, sequential-deferred and parallel paths alike (the parallel path on the batch's own repository, against the exports it re-loaded), and both setting the transient `InitialPasswordFollows` flag. A Create is flagged when its provisioning Synchronisation Rule asks for an Initial Password, and staging reuses the same answer, so the lookup moved rather than multiplied. The predicate is the rule's Initial Password being enabled, not its EnableAccount option: the delivery completes an enable JIM withheld whatever EnableAccount says (`AccountFlagsPolicy`, #2064), so the confirming import confirms the flow's value. An Update is flagged when its account's Metaverse Object is still owed a password on the Connected System (Pending, Delivering or Parked, any origin), the confirming import's own hold test (`GetMetaverseObjectIdsOwedPasswordAsync`); the lookup runs only for a batch holding an Update to a joined account, through a connector that implements `IConnectorPasswordManagement`. A failed rule lookup flags no Create and is reported through staging; a failed owed lookup flags no Update and is logged as a Warning naming the Connected System.

- **The update resend (#2158)**<br /> A flagged Update is sent as the flow asks. Only when the directory refuses it as an account-enable refusal (`LdapAccountEnableRefusalClassifier.IsAccountEnableRefusal`, `0000052D` on the Active Directory family) and the refused request carries exactly one `userAccountControl` modification, a Replace of a single integer without ACCOUNTDISABLE, does the LDAP Connector send the same modifications again with ACCOUNTDISABLE added, through the same `SendModify`/`SendModifyAsync` path (so Permissive Modify and the fallback in parts apply), on the sequential and concurrent paths, after a rename without repeating it. It reports `userAccountControl` in `AttributesAwaitingInitialPassword`, and from there the hold, the delivery's completion and the confirming import work exactly as for a Create. Reactive rather than proactive, so an account that already has a password, or a directory that never refuses (Samba AD today, or a domain whose policy allows an empty password), is enabled at once. A resend refused again fails with the original classification. The modify Active Directory refused applied nothing, so resending the whole request is safe even where the refusal came from one step of the fallback in parts.
- **The create rewrite (#2039)**<br /> Active Directory refuses an enabled account with no password (`0000052D`). In `BuildAddRequestWithOverflow`, the LDAP Connector adds ACCOUNTDISABLE (`0x2`) to the `userAccountControl` value an add carries when the directory is real Active Directory (`LdapDirectoryType.ActiveDirectory`, not the whole Active Directory family, so Samba AD is unchanged), the export is a Create flagged `InitialPasswordFollows`, and the value is a single integer without the flag: `512` is sent as `514`. Only the request's copy changes; the Pending Export keeps the flow's value, because that is what the confirming import is judged against and connectors never mutate an export. The connector names the attribute in `ConnectedSystemExportResult.AttributesAwaitingInitialPassword`, and `ProcessBatchSuccessAsync` marks each written change it names with the persisted `AwaitsInitialPassword` (clearing it on every other written change; a report on an unflagged export is a connector defect, logged as an error and ignored). The change stays `ExportedPendingConfirmation`, so every in-flight protection applies unchanged. Without an Initial Password, the refusal is classified as `PasswordRequiredToEnableAccount` with the remedies named, rather than an unhandled error.

- **The hold (#2039)**<br /> Every import of the Connected System judges every marked change, whether or not it returns the account. Reconciliation judges the changes on the objects the import returned, against the flow's value: when it matches, the change confirms as usual. When it does not and a password is still owed to the account (a password change for its Connected System and Metaverse Object that is Pending, Delivering or Parked, of any origin), the change is **held**: unchanged, not retried, no RPEI, counted in the reconciliation summary, and the Pending Export keeps its status, so nothing is sent. Once no password is owed (delivered with an administrator's explicit choice not to enable the account, expired, cancelled, never staged, or no Metaverse Object), it is **released**: back to `Pending` with the flag cleared, silently, so the next export writes the flow's value as an ordinary Update, which fails with the actionable error if the account still has no password. An expired or cancelled password, or one delivered with an explicit choice not to enable the account, usually changes nothing an import reads, so the account is not returned at all: a Full Import skips it by its content hash (the confirming import stamped the disabled state) and a Delta Import has nothing to report. After reconciliation, `SyncImportTaskProcessor.JudgeUnreconciledInitialPasswordHoldsAsync` therefore judges every marked change on an object the import did not reconcile, on Full and Delta Imports alike: one set-based query per run (`ISyncRepository.GetInitialPasswordHoldSummariesAsync`, led by the partial index on the flag, so a system with nothing held pays an empty index probe and nothing more), one owed-password lookup for all of them, and a Pending Export load only for the objects to release. Released exactly as reconciliation releases, and it touches nothing else on those objects: their imported values may predate the export, so no other change is confirmed or retried. A Create no import has confirmed keeps its status, because confirming is what turns it into an Update; its released change goes out once an import confirms it. The run logs a summary of held and released counts. Four guards keep a held change honest: Drift Detection still corrects the object's other attributes at once (#2501), in the run and in the synchronisation preview, but drops a correction restating a held change and carries the held change, with its status and attempt count, into the correction, because the page flush replaces the object's whole Pending Export with it (the correction keeps a still-queued export's id and its stagers, as export evaluation's merge does); export evaluation drops an incoming change restating exactly what a held change asserts, in every merge branch, while a different value still supersedes it, and where nothing else changes the persisted Pending Export is kept rather than rebuilt (anything the target no longer needs is withdrawn from it in place, and nothing else about it changes, including the item that queued it), which the synchronisation preview mirrors by dropping the same restatements before it proposes an export, while the configuration change preview keeps them as the saved configuration's side of the attribute's old-to-new pair; a held change is withdrawn like any queued change when the evaluation finds the target already holds what the Metaverse Object now wants (the leaver during the hold: the rule now yields the disabled value the account was created with, and the held enabled value, kept, would be released and enable the account), unless that already-current answer restates exactly what it asserts; and optimistic apply does not record the asserted value, since the Connector wrote an interim one, so the attribute records nothing until the confirming import reports what the target holds. Every path but the per-import held-set query is inert unless a change carries the flag. See [Pending Export Lifecycle](PENDING_EXPORT_LIFECYCLE.md).

- **The enabled state an export wrote (#2064)**<br /> On a successful export the Connector reports whether it wrote the attribute through which the account is enabled or disabled (`ConnectedSystemExportResult.SetsAccountEnabledState`; the LDAP Connector reports it on the Active Directory family for a Create's add, an Update's modify or a Delete performed as a disable that writes `userAccountControl`). `ProcessBatchSuccessAsync` stamps it on a Create's staged Provisioned password, and marks any first password still owed to another account it was reported for (its Provisioned row, or a change carrying the first password in its place, #2160), whatever its status, in one status-mark UPDATE, written first, straight from the Connector's results and before anything in result processing that can throw (#2173): the delivery waits while an account's export is in flight, so the mark must already be in place when the batch's Pending Exports leave `Executing`. The Password Delivery Service reads the mark afresh at each attempt and leaves that account's enabled state and "Password never expires" to the Attribute Flow (`AccountFlagsPolicy`, #2159), so an account the flow creates or sets disabled is never enabled by its Initial Password, and a flow's `0x10000` is never cleared by its expiry. A marking failure is logged with the accounts, counted (`InitialPasswordMarkingFailedCount`) and put on the export Activity as a warning, never failing the export.

- **Keyset batch collection (#985)**<br /> Batches are collected in a single forward sweep with a keyset cursor on `(CreatedAt, Id)`. Executed exports drop out of the query mid-run and deferred ones stay `Pending` while being accumulated in memory, so a strictly-increasing cursor never re-reads a row. The previous OFFSET implementation restarted its scan from zero for every batch and degraded to O(n²) page loads once thousands of deferred exports accumulated; at 200,000 objects with 10,000 reference-bearing groups it spent hours re-reading collected rows before the first group reached the target system. The sequential loop and the parallel workers share one page reader, which the workers call one at a time through the caller-context gate.

- **All-deferred fast path (#985c)**<br /> When an entire collected page turns out to be deferred, `AnyExecutableNonDeferredExportsAfterAsync` probes for executable exports beyond the cursor; if there are none, `GetRemainingDeferredExportIdsAsync` collects the ids of the rest in one set-based query and the sweep stops. The probe is mandatory: deferred and executable exports interleave in `(CreatedAt, Id)` order, so a full deferred page does not prove the remainder of the queue is deferred, and breaking out without it would silently skip executable exports created after a contiguous deferred run.

- **Partial writes for unresolved references (#1398)**<br /> A deferred export whose references still cannot all be resolved after the bulk pre-fetch is not held back whole. Its writable changes (everything but the reference values still owed) are handed to the connector alongside the resolved exports; on success the export stays `Pending` with `HasUnresolvedReferences`, its `ChangeType` flips from Create to Update (the row now exists, so it must never be inserted again), and only the reference changes remain for the deferred cadence. The connector always receives the export through `ForConnector`, a view carrying only what can be written now, on the immediate, sequential-deferred and parallel paths alike; the parallel path re-loads from persisted state, which is why the split is derivable from the persisted rows. Unresolved references are classified against the pre-fetched lookup: a referenced Metaverse Object with a Connected System Object but no anchor is waiting and reported nowhere; one with no Connected System Object in the target is unresolvable and reported per the Connected System's `UnresolvedReferenceHandling` (Error: an `UnresolvedReference` error on the referrer's RPEI, or on a `PendingExport`-type RPEI when nothing was written; Warn: a summary count on the Activity; Ignore: log only). Reconciliation on the confirming import confirms the written half of a still-`Pending` export.

- **SQL-filtered second pass (#1102)**<br /> `ExecuteDeferredReferencesAsync` runs after the connection closes, unless the run was cancelled: it re-resolves the references of every export still waiting on them and saves those now resolved, so the next export run sends them. It writes nothing to the Connected System itself. It filters in SQL against a partial index on unresolved references. It previously loaded and hydrated the Connected System's entire Pending Export set, including every attribute value change, then filtered client-side for the usually-zero unresolved rows; at 525,000 Pending Exports that cost around 11 minutes per run even when there was nothing to resolve.

- **Reference resolutions persisted before parallel dispatch (#994)**<br /> Deferred references are resolved in the caller's context, but each parallel batch re-loads its exports from its own `DbContext`. Each chunk's resolutions are therefore persisted (narrowly: the resolved columns of the changes and the export's flag) before that chunk's batches are handed out; without that, batches saw the pre-resolution values and sent raw internal identifiers to the target system (an LDAP directory rejects these as "invalid per syntax").

- **Optimistic export apply (#1079)**<br /> After a successful call-based export, the exported values are written straight onto the CSO instead of waiting for the confirming import to re-materialise them from the target system. This collapses the confirming import's write volume (measured: 524,997 exported CSOs previously re-materialised 9.8 million attribute values) and keeps the confirmed objects out of the next Delta Synchronisation a run sooner. Delete change types are skipped (the CSO is being removed), and so is a change the connector wrote with an interim value until an Initial Password is delivered (#2039), whose asserted value the target does not hold; any apply failure is safe: the next confirming import self-heals it. A change whose value is held in another data type's column than its attribute's (only a fault elsewhere produces one, since export staging converts expression results to the attribute's type, #2179; #2183) is not recorded either: an Update still removes the values it replaced, and the change is reported as unrecorded and logged at Warning. File-based exports apply too (#1936), and there an auto-confirmed export whose values cannot be recorded, whether the recording failed or the export carries such a change, is left `ExportNotConfirmed` to be sent again, because no import follows it. Applied counts are logged as a summary at the end of every export run.

- **Per-batch resource release (#1006)**<br /> Each parallel batch's `DbContext` (its repository scope) is disposed as that batch completes. Contexts were previously held for the remainder of the run, so a large reference-heavy export drained the connection pool after around 29 batches and failed with "the connection pool has been exhausted". Connectors are not per batch (#1820): each worker keeps one for every batch it runs and closes it when it finishes, or discards it after a failed batch.

- **Queued changes withdrawn once nothing authorises them (#1885)**<br /> Before anything is counted or sent (and never in a preview), `WithdrawQueuedChangesWithoutAuthorityAsync` withdraws each queued change (`Pending` or `ExportedNotConfirmed`) on an Update Pending Export whose export Synchronisation Rule is deleted or disabled, no longer has an enabled Attribute Flow for the attribute, or whose CSO is no longer joined; `ISyncEngine.SelectQueuedChangesWithoutAuthority` makes the decision, and an Update left empty is deleted. The same withdrawal runs as each export-relevant configuration change is saved; the export-time run is the backstop, and the only one that sees a join broken by scope exit. The Activity carries a warning with the counts, since a change an administrator may have expected did not go out. Creates, Deletes, unattributed changes, changes already sent and `Executing` Pending Exports are left alone.

- **An interrupted batch never stays `Executing` (#2083)**<br /> `MarkBatchAsExecutingAsync` snapshots each export's retry state (`PendingExportRetryState`) before the mark. On every call-based path (immediate, sequential deferred and parallel), a run cancelled while the Connector holds a batch puts it back to that snapshot (`ReleaseBatchAfterCancellationAsync`), with no attempt counted, and a batch that fails as a whole, including an `OperationCanceledException` nobody asked for such as an HTTP timeout, records one failed attempt per export with an RPEI naming its object (`RecordBatchFailureAsync`). A cancellation stops the run at the next batch boundary; a failed first-pass batch stops the first pass taking further batches (parallel batches already running finish and are recorded; the deferred pass still runs), a failed sequential deferred batch ends that pass, and a failed parallel deferred batch leaves the other batches running. Both writes go through `ReleaseExecutingPendingExportsAsync`, which writes only rows still `Executing`, so a result already recorded is never overwritten; `UpdatePendingExportsAsync` writes a Pending Export and its attribute changes in one transaction, so a row that has left `Executing` is wholly recorded. The parallel path does this on the batch's own repository while its scope is alive, and a failure closing a worker's connector after its batches' outcomes are recorded is reported once, as an item of its own. Results the Connector returned are recorded before anything that processes them further can fail (#2197): the result loop only classifies, and the batch's results and the External IDs its Creates were assigned are written in one transaction (`ISyncRepository.BeginTransactionAsync`) before Collision Remediation, rename-authorisation clearing, Initial Password staging or optimistic apply run; Collision Remediation then decides and records each value already in use on its own, so one it cannot decide fails alone. A failure of that first write records nothing, and the batch is reported failed as a whole. A missing result is never read as success: it is a failed attempt, or, once the run is cancelled, a change never started (`NoResultFromTheConnector`). The release is guarded on the batch's own mark as well as on status (`ExecutingBatchMark.MarkedAt`, matched against `LastAttemptedAt`), so a cancelled run still putting its batch back cannot overwrite a later run's mark on the same row. A Connector that stops on cancellation reports what it never started as `ConnectedSystemExportResult.NotAttempted()`: `ProcessBatchSuccessAsync` puts those back first, uncounted and with no RPEI, and records the rest; a not-attempted result while the run is not cancelled is a failed attempt, logged as an Error. The SQL and SCIM Connectors give a write already under way 30 seconds after the cancellation (`InFlightCancellation`), then abandon it and report it failed. When a run stops early, `HandOnProcessedExportItemsAsync` hands on the execution items already gathered, so a deferred pass's completed batches are recorded.

- **Executing recovered at worker start**<br /> A Pending Export left `Executing` by a worker crash or restart, or by the release write itself failing (logged as an Error naming the rows), is recovered when the worker starts (`RecoverStrandedExecutingPendingExportsAsync`): to `Exported` if any of its changes was already sent, so the next confirming import reconciles it, otherwise to `Pending`, so the next export retries it.

- **A failing export is retried at every export run (#2119)**<br /> JIM never gives up on a Pending Export. A failed attempt (`PendingExportRetryState.AfterFailedAttempt`) counts one more in `ErrorCount`, keeps the error, sets `FailingSince` on the first of a run of failures, and leaves the export `Pending`; a file-based export that throws does the same and marks the whole file's exports `ExportNotConfirmed`. Either way the next export run sends it again, with no wait in between, and reports it again until the cause is fixed. A successful attempt clears the failure record (`ClearFailure`). The `Failed` status, the five-attempt `MaxRetries` limit and the `NextRetryAt` backoff are gone: an export that gave up dropped out of the queue, swallowed later changes to the same object, and left later runs looking clean. No count or age of failed attempts takes an export out of the executable set.

- **No-net-change detection**<br /> Before exports are created during sync, the system checks if the target CSO already has the expected values. This happens upstream in `EvaluateExportRulesWithNoNetChangeDetectionAsync`, not during export execution. An attribute found already current there also withdraws any change still queued for it (#1883); see [Pending Export Lifecycle](PENDING_EXPORT_LIFECYCLE.md).

- **Container auto-selection**<br /> When exports create new containers (e.g., OUs in LDAP), their external IDs are captured and auto-selected so they appear in future imports without manual configuration. Each Connector's are captured just before its connection closes, which forgets them, on every exit path (#2197): a failed parallel batch's go onto the run's result, and the processor is told of them as they are captured (`containersCreated`), so a cancelled or failed run still selects them, best-effort, before it reports how it ended.

- **Preview mode**<br /> `SyncRunMode.PreviewOnly` returns the list of exports that would be processed without executing them, enabling dry-run functionality.

- **Per-batch isolation**<br /> Each parallel batch gets its own `DbContext`, and each parallel worker its own connector instance. EF Core is not thread-safe, so sharing a context across batches would cause data corruption, and a connection held by two workers at once would share connection state.

- **Batch persistence**<br /> A batch's results (Pending Export statuses and the CSO updates carrying each Create's new External ID) are written in one transaction on the repository the batch runs on (a parallel batch's own), before anything that can fail while processing them further (#2197). Its RPEIs are written separately, by the processor's per-batch callback (`PersistBatchRpeisAsync` through `BulkInsertRpeisAsync`), which splits a large enough insert across N concurrent PostgreSQL connections via `ParallelBatchWriter` (#394).

- **Max Export Parallelism applies to both passes (#1820)**<br /> After the switch to paged batch loading, the first pass (every export without unresolved references) ran one batch at a time whatever Max Export Parallelism or the connector recommended; only the deferred pass honoured it. Both passes now share `ProcessBatchesInParallelAsync`, with workers that keep one connector each for the whole run rather than opening and closing a connection per batch. With **Create containers as needed?** on, an LDAP export whose container a parallel batch created a moment earlier now finds it and creates the object in it.

- **LDAP consolidation**<br /> Multiple changes to the same attribute with the same operation type (e.g., 200 individual "member Add" operations) are consolidated into a single `DirectoryAttributeModification` before sending to the directory server. This is the correct RFC 4511 pattern and dramatically reduces the number of LDAP modify requests.

- **LDAP chunking**<br /> Consolidated modifications that exceed the configurable batch size (default: 1000) are split into multiple `ModifyRequest` objects sent sequentially. This prevents LDAP server rejection of oversized requests, which is important for large group membership changes.

- **Connector-recommended export parallelism**<br /> When a Connected System has no explicit Max Export Parallelism, the degree of parallelism now comes from the connector's recommendation rather than defaulting to sequential. The LDAP Connector recommends two parallel batch pipelines for directories tuned to a high Export Concurrency and stays sequential otherwise. An explicitly configured value always wins.

- **LDAP export concurrency auto-tuning**<br /> Export concurrency defaults are automatically tuned based on the detected directory server type. AD and OpenLDAP directories default to 16 concurrent export operations, while Samba and unknown directory types default to 4. This balances throughput against server stability; Samba's LDAP implementation is less tolerant of high concurrency.
