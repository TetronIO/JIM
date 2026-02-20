# Full Import Flow

> Generated against JIM v0.2.0 (`5a4788e9`). If the codebase has changed significantly since then, these diagrams may be out of date.

This diagram shows how objects are imported from a connected system into JIM's connector space. Both Full Import and Delta Import use the same processor (`SyncImportTaskProcessor`); the connector handles delta filtering internally via watermark/persisted data.

## Overall Import Flow

```mermaid
flowchart TD
    Start([PerformFullImportAsync]) --> ConnType{Connector<br/>type?}

    %% --- Call-based connector (e.g., LDAP) ---
    ConnType -->|IConnectorImportUsingCalls| InjectServices[Inject CertificateProvider<br/>and CredentialProtection<br/>if connector supports them]
    InjectServices --> OpenConn[OpenImportConnection<br/>with system settings]
    OpenConn --> InitPage[initialPage = true<br/>paginationTokens = empty<br/>Capture original PersistedConnectorData]

    InitPage --> PageLoop{More pages?<br/>initialPage OR<br/>tokens present}
    PageLoop -->|Yes| Import[connector.ImportAsync<br/>Pass original persisted data<br/>to ensure consistent watermark]
    Import --> UpdateProgress[Update activity progress<br/>Imported N objects page M]
    UpdateProgress --> CollectExtIds[Add external IDs from this page<br/>to externalIdsImported collection]
    CollectExtIds --> CaptureWatermark{First page with<br/>new persisted data?}
    CaptureWatermark -->|Yes| SaveWatermark[Capture new watermark<br/>Don't save yet - save after all pages]
    CaptureWatermark -->|No| ProcessPage
    SaveWatermark --> ProcessPage[ProcessImportObjectsAsync<br/>See Per-Object Processing below]
    ProcessPage --> PassTokens[Pass pagination tokens<br/>for next page]
    PassTokens --> PageLoop

    PageLoop -->|No| UpdatePersistedData{New watermark<br/>captured?}
    UpdatePersistedData -->|Yes| PersistWatermark[Update ConnectedSystem<br/>PersistedConnectorData]
    UpdatePersistedData -->|No| CloseConn
    PersistWatermark --> CloseConn[CloseImportConnection]

    %% --- File-based connector ---
    ConnType -->|IConnectorImportUsingFiles| FileImport[connector.ImportAsync<br/>Returns all objects at once]
    FileImport --> FileProgress[Update activity:<br/>Processing N objects]
    FileProgress --> FileCollect[Add external IDs<br/>to collection]
    FileCollect --> FileProcess[ProcessImportObjectsAsync]
    FileProcess --> PostImport

    CloseConn --> PostImport{Full Import<br/>and objects > 0?}

    %% --- Deletion detection ---
    PostImport -->|Yes| DeletionDetection[Deletion Detection<br/>For each selected object type:<br/>Compare imported ext IDs<br/>against existing CSO ext IDs<br/>Mark missing CSOs as Obsolete]
    PostImport -->|No, Delta Import<br/>or 0 objects| RefResolution

    DeletionDetection --> RefResolution[Reference Resolution<br/>Resolve unresolved reference strings<br/>into CSO links by external ID]

    %% --- Persist ---
    RefResolution --> PersistCreate[Batch create new CSOs<br/>with change objects]
    PersistCreate --> PersistUpdate[Batch update existing CSOs<br/>with change objects]

    %% --- Reconciliation ---
    PersistUpdate --> Reconcile[Reconcile Pending Exports<br/>See Confirming Import below]
    Reconcile --> ValidateRpeis[Validate RPEIs<br/>Detect orphaned create RPEIs<br/>with no CSO assigned]
    ValidateRpeis --> PersistRpeis[Add RPEIs to Activity<br/>and persist]
    PersistRpeis --> End([Import Complete])
```

## Per-Object Processing

For each object in an import page, within `ProcessImportObjectsAsync`:

```mermaid
flowchart TD
    Entry([For each import object]) --> DupAttrs{Duplicate<br/>attribute names?}
    DupAttrs -->|Yes| DupAttrErr[RPEI: DuplicateImportedAttributes<br/>Skip object]

    DupAttrs -->|No| MatchType[Match string object type<br/>to schema ObjectType]
    MatchType --> TypeFound{Object type<br/>found in schema?}
    TypeFound -->|No| TypeErr[RPEI: CouldNotMatchObjectType<br/>Skip object]

    TypeFound -->|Yes| ExtractExtId[Extract external ID value<br/>from import attributes]
    ExtractExtId --> CrossPageDup{Cross-page<br/>duplicate?}
    CrossPageDup -->|Yes| CrossPageErr[RPEI: DuplicateObject<br/>Cross-page duplicate detected<br/>Skip object]

    CrossPageDup -->|No| SameBatchDup{Same-batch<br/>duplicate?}
    SameBatchDup -->|3rd+ occurrence| ThirdDupErr[RPEI: DuplicateObject<br/>Skip object]
    SameBatchDup -->|2nd occurrence| BothDupErr[Error BOTH objects:<br/>Mark current as DuplicateObject<br/>Go back and mark first as DuplicateObject<br/>Remove first CSO from create list<br/>No random winner]

    SameBatchDup -->|First occurrence| TrackExtId[Track external ID<br/>in seenExternalIds]
    TrackExtId --> CheckDelete{Connector says<br/>Delete?}

    %% --- Delete path ---
    CheckDelete -->|Yes| FindExisting[Find existing CSO<br/>by external ID]
    FindExisting --> ExistsDel{CSO<br/>exists?}
    ExistsDel -->|Yes| MarkObsolete[Set Status = Obsolete<br/>RPEI: Deleted<br/>Add to update list]
    ExistsDel -->|No| IgnoreDel[No CSO to delete<br/>Remove RPEI, skip]

    %% --- Create/Update path ---
    CheckDelete -->|No| FindCso[Find existing CSO<br/>by external ID]
    FindCso --> CsoExists{CSO<br/>exists?}

    CsoExists -->|No| CreateCso[Create new CSO<br/>Map all import attributes<br/>to CSO attribute values<br/>RPEI: Added]
    CreateCso --> AddToCreate[Add to create list<br/>Update seenExternalIds<br/>with CSO reference]

    CsoExists -->|Yes| CheckProvisioning{CSO status =<br/>PendingProvisioning?}
    CheckProvisioning -->|Yes| TransitionNormal[Transition to Normal status<br/>Object confirmed in target system]
    CheckProvisioning -->|No| UpdateCso
    TransitionNormal --> UpdateCso[Update CSO attributes<br/>Compare each import attribute<br/>against existing CSO values<br/>Only stage actual changes]
    UpdateCso --> HasChanges{Attribute<br/>changes?}
    HasChanges -->|Yes| RpeiUpdated[RPEI: Updated]
    HasChanges -->|No| RpeiNoChange[No RPEI created<br/>CSO still added to update list<br/>for reference resolution]
    RpeiUpdated --> AddToUpdate[Add to update list]
    RpeiNoChange --> AddToUpdate
```

## Deletion Detection (Full Import Only)

```mermaid
flowchart TD
    Start([For each selected object type]) --> GetExisting[Get all existing CSO external IDs<br/>for this object type from database]
    GetExisting --> GetImported[Get all imported external IDs<br/>for this object type from collection]
    GetImported --> Compare[Except: find CSO external IDs<br/>not in imported set]
    Compare --> Loop{More missing<br/>external IDs?}
    Loop -->|No| Done([Next object type])
    Loop -->|Yes| FindCso[Find CSO by external ID<br/>and attribute ID]
    FindCso --> CheckProcessed{CSO already processed<br/>in this import run?}
    CheckProcessed -->|Yes| SkipLog[Skip - ext ID may have<br/>been updated during import]
    SkipLog --> Loop
    CheckProcessed -->|No| Obsolete[Set CSO Status = Obsolete<br/>Set LastUpdated = UtcNow<br/>RPEI: Deleted<br/>Add to update list]
    Obsolete --> Loop

    style Start fill:#f9f,stroke:#333
```

**Safety rule**: If zero objects were imported, deletion detection is skipped entirely. This prevents accidental mass-deletion when the connected system returns no data due to connectivity issues.

**Delta Import exception**: Deletion detection only runs for Full Import. Delta Imports handle explicit deletes via `ObjectChangeType.Deleted` from the connector (e.g., LDAP tombstone/changelog entries).

## Confirming Import - Pending Export Reconciliation

After CSOs are persisted, the import processor reconciles previously exported changes against the freshly imported values. This is how JIM confirms that exports actually took effect.

```mermaid
flowchart TD
    Start([ReconcilePendingExportsAsync]) --> LoadPE[Bulk fetch pending exports<br/>for updated CSOs<br/>Status = Exported]
    LoadPE --> Loop{More CSOs<br/>with pending exports?}
    Loop -->|No| Summary[Log reconciliation summary:<br/>Confirmed / Retry / Failed]
    Summary --> Done([Done])

    Loop -->|Yes| Compare[For each attribute change<br/>in pending export:<br/>Compare expected value<br/>against CSO current value]
    Compare --> Result{All attributes<br/>confirmed?}

    Result -->|All confirmed| Delete[Queue pending export<br/>for batch deletion<br/>Export successfully applied]
    Result -->|Some confirmed| Partial[Remove confirmed attributes<br/>Keep unconfirmed attributes<br/>If was Create, change to Update<br/>Increment error count<br/>Queue for batch update]
    Result -->|None confirmed| AllFailed[Increment error count<br/>Queue for batch update]
    Result -->|Max retries exceeded| PermanentFail[RPEI: ExportConfirmationFailed<br/>Manual intervention required]

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
