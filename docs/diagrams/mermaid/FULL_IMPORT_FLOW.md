# Full Import Flow

> Generated against JIM v0.2.0 (`5a4788e9`). If the codebase has changed significantly since then, these diagrams may be out of date.

This diagram shows how objects are imported from a connected system into JIM's connector space. Both Full Import and Delta Import use the same processor (`SyncImportTaskProcessor`); the connector handles delta filtering internally via watermark/persisted data.

## Overall Import Flow

```mermaid
flowchart TD
    Start([PerformFullImportAsync]) --> ConnType{Connector\ntype?}

    %% --- Call-based connector (e.g., LDAP) ---
    ConnType -->|IConnectorImportUsingCalls| InjectServices[Inject CertificateProvider\nand CredentialProtection\nif connector supports them]
    InjectServices --> OpenConn[OpenImportConnection\nwith system settings]
    OpenConn --> InitPage[initialPage = true\npaginationTokens = empty\nCapture original PersistedConnectorData]

    InitPage --> PageLoop{More pages?\ninitialPage OR\ntokens present}
    PageLoop -->|Yes| Import[connector.ImportAsync\nPass original persisted data\nto ensure consistent watermark]
    Import --> UpdateProgress[Update activity progress\nImported N objects page M]
    UpdateProgress --> CollectExtIds[Add external IDs from this page\nto externalIdsImported collection]
    CollectExtIds --> CaptureWatermark{First page with\nnew persisted data?}
    CaptureWatermark -->|Yes| SaveWatermark[Capture new watermark\nDon't save yet - save after all pages]
    CaptureWatermark -->|No| ProcessPage
    SaveWatermark --> ProcessPage[ProcessImportObjectsAsync\nSee Per-Object Processing below]
    ProcessPage --> PassTokens[Pass pagination tokens\nfor next page]
    PassTokens --> PageLoop

    PageLoop -->|No| UpdatePersistedData{New watermark\ncaptured?}
    UpdatePersistedData -->|Yes| PersistWatermark[Update ConnectedSystem\nPersistedConnectorData]
    UpdatePersistedData -->|No| CloseConn
    PersistWatermark --> CloseConn[CloseImportConnection]

    %% --- File-based connector ---
    ConnType -->|IConnectorImportUsingFiles| FileImport[connector.ImportAsync\nReturns all objects at once]
    FileImport --> FileProgress[Update activity:\nProcessing N objects]
    FileProgress --> FileCollect[Add external IDs\nto collection]
    FileCollect --> FileProcess[ProcessImportObjectsAsync]
    FileProcess --> PostImport

    CloseConn --> PostImport{Full Import\nand objects > 0?}

    %% --- Deletion detection ---
    PostImport -->|Yes| DeletionDetection[Deletion Detection\nFor each selected object type:\nCompare imported ext IDs\nagainst existing CSO ext IDs\nMark missing CSOs as Obsolete]
    PostImport -->|No, Delta Import\nor 0 objects| RefResolution

    DeletionDetection --> RefResolution[Reference Resolution\nResolve unresolved reference strings\ninto CSO links by external ID]

    %% --- Persist ---
    RefResolution --> PersistCreate[Batch create new CSOs\nwith change objects]
    PersistCreate --> PersistUpdate[Batch update existing CSOs\nwith change objects]

    %% --- Reconciliation ---
    PersistUpdate --> Reconcile[Reconcile Pending Exports\nSee Confirming Import below]
    Reconcile --> ValidateRpeis[Validate RPEIs\nDetect orphaned create RPEIs\nwith no CSO assigned]
    ValidateRpeis --> PersistRpeis[Add RPEIs to Activity\nand persist]
    PersistRpeis --> End([Import Complete])
```

## Per-Object Processing

For each object in an import page, within `ProcessImportObjectsAsync`:

```mermaid
flowchart TD
    Entry([For each import object]) --> DupAttrs{Duplicate\nattribute names?}
    DupAttrs -->|Yes| DupAttrErr[RPEI: DuplicateImportedAttributes\nSkip object]

    DupAttrs -->|No| MatchType[Match string object type\nto schema ObjectType]
    MatchType --> TypeFound{Object type\nfound in schema?}
    TypeFound -->|No| TypeErr[RPEI: CouldNotMatchObjectType\nSkip object]

    TypeFound -->|Yes| ExtractExtId[Extract external ID value\nfrom import attributes]
    ExtractExtId --> CrossPageDup{Cross-page\nduplicate?}
    CrossPageDup -->|Yes| CrossPageErr[RPEI: DuplicateObject\nCross-page duplicate detected\nSkip object]

    CrossPageDup -->|No| SameBatchDup{Same-batch\nduplicate?}
    SameBatchDup -->|3rd+ occurrence| ThirdDupErr[RPEI: DuplicateObject\nSkip object]
    SameBatchDup -->|2nd occurrence| BothDupErr[Error BOTH objects:\nMark current as DuplicateObject\nGo back and mark first as DuplicateObject\nRemove first CSO from create list\nNo random winner]

    SameBatchDup -->|First occurrence| TrackExtId[Track external ID\nin seenExternalIds]
    TrackExtId --> CheckDelete{Connector says\nDelete?}

    %% --- Delete path ---
    CheckDelete -->|Yes| FindExisting[Find existing CSO\nby external ID]
    FindExisting --> ExistsDel{CSO\nexists?}
    ExistsDel -->|Yes| MarkObsolete[Set Status = Obsolete\nRPEI: Deleted\nAdd to update list]
    ExistsDel -->|No| IgnoreDel[No CSO to delete\nRemove RPEI, skip]

    %% --- Create/Update path ---
    CheckDelete -->|No| FindCso[Find existing CSO\nby external ID]
    FindCso --> CsoExists{CSO\nexists?}

    CsoExists -->|No| CreateCso[Create new CSO\nMap all import attributes\nto CSO attribute values\nRPEI: Added]
    CreateCso --> AddToCreate[Add to create list\nUpdate seenExternalIds\nwith CSO reference]

    CsoExists -->|Yes| CheckProvisioning{CSO status =\nPendingProvisioning?}
    CheckProvisioning -->|Yes| TransitionNormal[Transition to Normal status\nObject confirmed in target system]
    CheckProvisioning -->|No| UpdateCso
    TransitionNormal --> UpdateCso[Update CSO attributes\nCompare each import attribute\nagainst existing CSO values\nOnly stage actual changes]
    UpdateCso --> HasChanges{Attribute\nchanges?}
    HasChanges -->|Yes| RpeiUpdated[RPEI: Updated]
    HasChanges -->|No| RpeiNoChange[No RPEI created\nCSO still added to update list\nfor reference resolution]
    RpeiUpdated --> AddToUpdate[Add to update list]
    RpeiNoChange --> AddToUpdate
```

## Deletion Detection (Full Import Only)

```mermaid
flowchart TD
    Start([For each selected object type]) --> GetExisting[Get all existing CSO external IDs\nfor this object type from database]
    GetExisting --> GetImported[Get all imported external IDs\nfor this object type from collection]
    GetImported --> Compare[Except: find CSO external IDs\nnot in imported set]
    Compare --> Loop{More missing\nexternal IDs?}
    Loop -->|No| Done([Next object type])
    Loop -->|Yes| FindCso[Find CSO by external ID\nand attribute ID]
    FindCso --> CheckProcessed{CSO already processed\nin this import run?}
    CheckProcessed -->|Yes| SkipLog[Skip - ext ID may have\nbeen updated during import]
    SkipLog --> Loop
    CheckProcessed -->|No| Obsolete[Set CSO Status = Obsolete\nSet LastUpdated = UtcNow\nRPEI: Deleted\nAdd to update list]
    Obsolete --> Loop

    style Start fill:#f9f,stroke:#333
```

**Safety rule**: If zero objects were imported, deletion detection is skipped entirely. This prevents accidental mass-deletion when the connected system returns no data due to connectivity issues.

**Delta Import exception**: Deletion detection only runs for Full Import. Delta Imports handle explicit deletes via `ObjectChangeType.Deleted` from the connector (e.g., LDAP tombstone/changelog entries).

## Confirming Import - Pending Export Reconciliation

After CSOs are persisted, the import processor reconciles previously exported changes against the freshly imported values. This is how JIM confirms that exports actually took effect.

```mermaid
flowchart TD
    Start([ReconcilePendingExportsAsync]) --> LoadPE[Bulk fetch pending exports\nfor updated CSOs\nStatus = Exported]
    LoadPE --> Loop{More CSOs\nwith pending exports?}
    Loop -->|No| Summary[Log reconciliation summary:\nConfirmed / Retry / Failed]
    Summary --> Done([Done])

    Loop -->|Yes| Compare[For each attribute change\nin pending export:\nCompare expected value\nagainst CSO current value]
    Compare --> Result{All attributes\nconfirmed?}

    Result -->|All confirmed| Delete[Queue pending export\nfor batch deletion\nExport successfully applied]
    Result -->|Some confirmed| Partial[Remove confirmed attributes\nKeep unconfirmed attributes\nIf was Create, change to Update\nIncrement error count\nQueue for batch update]
    Result -->|None confirmed| AllFailed[Increment error count\nQueue for batch update]
    Result -->|Max retries exceeded| PermanentFail[RPEI: ExportConfirmationFailed\nManual intervention required]

    Delete --> Loop
    Partial --> Loop
    AllFailed --> Loop
    PermanentFail --> Loop
```

## Key Design Decisions

- **Cross-page duplicate detection**: A `HashSet<string>` tracks external IDs across all pages of a paginated import. This is defence-in-depth against directory servers with faulty paging (e.g., Samba AD).

- **Same-batch duplicate handling**: When duplicates are found within a single page, BOTH objects are rejected (no "random winner" based on file order). This forces data owners to fix the source data.

- **Watermark consistency**: For paginated delta imports, the original persisted connector data (watermark/USN) is passed to every page. The new watermark from the first page is only saved after all pages complete, ensuring consistent queries.

- **RPEI list separation**: RPEIs are maintained separately from the Activity during import to avoid EF Core accidentally persisting CSOs before they're ready (EF would follow the Activity -> RPEI -> CSO navigation chain during `SaveChanges`).

- **Zero-import safety**: If no objects are imported, deletion detection is skipped entirely to prevent accidental mass-deletion when connectivity to the source system fails.

- **PendingProvisioning transition**: CSOs created during export (provisioning) start with `PendingProvisioning` status. The confirming import transitions them to `Normal` when the object is confirmed to exist in the target system.
