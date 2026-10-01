# Connector Lifecycle

> Last updated: 2026-09-29, JIM v0.16.0

This diagram shows how connectors are resolved, configured, opened, used, and closed across import, export and password operations. Connectors implement capability interfaces that determine their lifecycle shape. The built-in connectors are the LDAP, File, SCIM 2.0 Client and SQL Connectors; the File Connector is the only file-based one.

## Connector Interface Hierarchy

```mermaid
flowchart LR
    IConnector[IConnector<br/>Name, Description, Url] --> ICap[IConnectorCapabilities<br/>What the connector supports]
    IConnector --> ISettings[IConnectorSettings<br/>Configuration definitions<br/>+ validation]
    IConnector --> ISchema[IConnectorSchema<br/>Schema discovery]
    IConnector --> IPartitions[IConnectorPartitions<br/>Partition discovery]

    IConnector --> IImportCalls[IConnectorImportUsingCalls<br/>OpenImportConnection<br/>ImportAsync paginated<br/>CloseImportConnection]
    IConnector --> IImportFiles[IConnectorImportUsingFiles<br/>ImportAsync single call<br/>No open/close]

    IConnector --> IExportCalls[IConnectorExportUsingCalls<br/>OpenExportConnection<br/>ExportAsync batched<br/>CloseExportConnection]
    IConnector --> IExportFiles[IConnectorExportUsingFiles<br/>ExportAsync single call<br/>No open/close]

    IConnector --> ICredential[IConnectorCredentialAware<br/>SetCredentialProtection<br/>Password decryption]
    IConnector --> ICertificate[IConnectorCertificateAware<br/>SetCertificateProvider<br/>SSL/TLS certificates]
    IConnector --> IContainer[IConnectorContainerCreation<br/>Tracks containers created<br/>during export]

    IConnector --> IPhases[IConnectorPhases<br/>GetPhases: steps declared<br/>before a run starts, #454]
    IConnector --> IScope[IConnectorManagedScope<br/>SetManagedScope before export<br/>IConnectorContainment<br/>IsWithinContainer]
    IConnector --> IPassword[IConnectorPasswordManagement<br/>OpenPasswordConnection<br/>SetPasswordAsync<br/>ClosePasswordConnection<br/>IConnectorPasswordPolicyDiscovery]
    IConnector --> IDiscovery[Discovery and advice<br/>IConnectorDirectoryServers<br/>IConnectorObjectClassUsage<br/>IConnectorContainerObjectCounts<br/>IConnectorDetectedCapabilities<br/>IConnectorObjectTypeSelectionValidation<br/>IConnectorRecommendedExportParallelism<br/>IConnectorSecureEndpoint]
```

Every `ImportAsync` and `ExportAsync` call is handed an `IConnectorProgress` (never null), through which a connector narrates what it is doing, enters the phases it declared via `IConnectorPhases`, and states how many objects it expects and has read (#1148). A connector that declares no phases still works: its messages appear under the JIM step that hosts the call.

## Connector Resolution

```mermaid
flowchart TD
    TaskStart([Worker receives<br/>SynchronisationWorkerTask]) --> GetCS[Get ConnectedSystem<br/>with ConnectorDefinition]
    GetCS --> Factory[IConnectorFactory.Create<br/>single dispatch point, #875<br/>Worker passes no providers here;<br/>the processors inject them]
    Factory --> MatchName{ConnectorDefinition<br/>Name?}

    MatchName -->|LdapConnectorName| CreateLdap[new LdapConnector]
    MatchName -->|FileConnectorName| CreateFile[new FileConnector]
    MatchName -->|ScimClientConnectorName| CreateScim[new ScimConnector]
    MatchName -->|SqlConnectorName| CreateSql[new SqlConnector]
    MatchName -->|Unknown| ThrowError[throw NotSupportedException<br/>Activity fails with error]

    CreateLdap --> GetRunProfile[Resolve RunProfile<br/>from ConnectedSystem.RunProfiles]
    CreateFile --> GetRunProfile
    CreateScim --> GetRunProfile
    CreateSql --> GetRunProfile

    GetRunProfile --> DeclarePhases[ActivityPhaseReporter.StartAsync<br/>Calls IConnectorPhases.GetPhases if implemented<br/>A declaration failure is logged, never fatal]
    DeclarePhases --> RouteByType{RunProfile<br/>RunType?}
    RouteByType -->|FullImport<br/>DeltaImport| ImportProcessor[SyncImportTaskProcessor<br/>ISyncEngine + ISyncServer + ISyncRepository]
    RouteByType -->|FullSynchronisation| FullSyncProcessor[SyncFullSyncTaskProcessor<br/>ISyncEngine + ISyncServer + ISyncRepository]
    RouteByType -->|DeltaSynchronisation| DeltaSyncProcessor[SyncDeltaSyncTaskProcessor<br/>ISyncEngine + ISyncServer + ISyncRepository]
    RouteByType -->|Export| ExportProcessor[SyncExportTaskProcessor<br/>ISyncServer + ISyncRepository]
```

## Import Lifecycle

```mermaid
flowchart TD
    Start([Import starts]) --> CheckType{Connector<br/>type?}

    %% --- Call-based connector ---
    CheckType -->|IConnectorImportUsingCalls| InjectCert{Implements<br/>IConnectorCertificateAware?}
    InjectCert -->|Yes| SetCert[SetCertificateProvider]
    InjectCert -->|No| InjectCred
    SetCert --> InjectCred{Implements<br/>IConnectorCredentialAware?}
    InjectCred -->|Yes| SetCred[SetCredentialProtection]
    InjectCred -->|No| Open
    SetCred --> Open[Connecting step<br/>OpenImportConnection<br/>with system settings]

    Open --> PageLoop{More pages?<br/>initialPage OR<br/>tokens present}
    Open -->|Fails to connect, #1875| Close
    PageLoop -->|Yes| CallImport[connector.ImportAsync<br/>Pass ORIGINAL persisted data<br/>for consistent watermark<br/>plus IConnectorProgress]
    CallImport --> CaptureWatermark{First page with<br/>new persisted data?}
    CaptureWatermark -->|Yes| SaveNewWatermark[Capture new watermark<br/>Don't persist yet]
    CaptureWatermark -->|No| ProcessPage
    SaveNewWatermark --> ProcessPage[Close the connector's phases<br/>Process imported objects<br/>Create/update CSOs]
    ProcessPage --> NextPage[Pass pagination tokens<br/>for next page]
    NextPage --> PageLoop

    PageLoop -->|No| Close[CloseImportConnection<br/>in finally block - always called]
    Close --> CloseData{Close returned<br/>connector data?}
    CloseData -->|Yes| PersistClose[Persist it now and drop the page watermark<br/>e.g. a domain controller pin<br/>the connection invalidated, #1169]
    CloseData -->|No| Stage[Stage the run: deletions, references,<br/>CSOs, reconciliation, results]
    PersistClose --> Stage
    Stage --> PersistWM{Page watermark<br/>still held?}
    PersistWM -->|Yes| UpdateCS[Update ConnectedSystem<br/>PersistedConnectorData<br/>only once staging succeeded, #1868]
    PersistWM -->|No| Done([Import complete])
    UpdateCS --> Done

    %% --- File-based connector ---
    CheckType -->|IConnectorImportUsingFiles| FileImport[connector.ImportAsync<br/>Returns all objects at once<br/>plus IConnectorProgress<br/>No open/close lifecycle]
    FileImport --> FileProcess[Process all objects]
    FileProcess --> Done
```

## LDAP Delta Import Change Sources

The LDAP Connector reads a Delta Import's changes through one change source per directory family (#1736), chosen once from the directory type the rootDSE reports: `uSNChanged` plus the Deleted Objects container for Active Directory and Samba AD, `cn=accesslog` for OpenLDAP, and `cn=changelog` for everything else (389 Directory Server among them).

```mermaid
flowchart TD
    Start([LDAP Delta Import, first page]) --> Pick[LdapDeltaSources.Create<br/>USN, Accesslog or Changelog<br/>from the directory type]
    Pick --> Capture[CaptureWatermarkAsync<br/>record the watermark this<br/>import will leave behind]
    Capture --> Continuity{Persisted watermark<br/>still valid here?}
    Continuity -->|No: another domain controller,<br/>one restored from a backup or snapshot,<br/>or a trimmed changelog| Refuse[The Delta Import fails:<br/>run a Full Import to<br/>re-establish the baseline]
    Continuity -->|Yes| Ready{Change source<br/>readable by the<br/>service account?}
    Ready -->|No| RefuseRead[Refuse the Delta Import<br/>naming what could not be read, #1737]
    Ready -->|Yes, possibly with notes| Baseline{Last import left<br/>a watermark?}
    Baseline -->|No| Fallback[Fall back to a Full Import<br/>to establish the baseline<br/>Warning on the Activity]
    Baseline -->|Yes| Read[ReadChangesAsync<br/>changes, deletions and renames<br/>paged across calls if needed]
```

A Full Import calls only `CaptureWatermarkAsync`, so it leaves the baseline the first Delta Import reads from; later Delta Import pages call only `HasBaseline` and `ReadChangesAsync`.

## Export Lifecycle

```mermaid
flowchart TD
    Start([Export starts]) --> CheckType{Connector<br/>type?}

    %% --- Call-based connector ---
    CheckType -->|IConnectorExportUsingCalls| ManagedScope{Implements IConnectorManagedScope<br/>and the system has container<br/>selections or exclusions?}
    ManagedScope -->|Yes| SetScope[SetManagedScope<br/>connector refuses, per object,<br/>writes outside it, #1250]
    ManagedScope -->|No| InjectCert
    SetScope --> InjectCert[Inject CertificateProvider<br/>and CredentialProtection<br/>if connector supports them]
    InjectCert --> Open[OpenExportConnection<br/>with system settings]

    Open -->|Fails to connect, #1875| CloseExport
    Open --> SplitExports[Split exports into<br/>immediate and deferred]
    SplitExports --> CheckParallel{MaxParallelism > 1?}

    CheckParallel -->|Yes| ParallelExport[Each batch gets:<br/>Own connector instance via factory<br/>Own OpenExportConnection<br/>Own DbContext]
    CheckParallel -->|No| SequentialExport[Single connector<br/>processes batches sequentially]

    ParallelExport --> BatchLoop[connector.ExportAsync<br/>per batch, plus IConnectorProgress]
    SequentialExport --> BatchLoop

    BatchLoop --> Deferred[Process deferred exports<br/>Resolve references, export]
    Deferred --> CloseExport[CloseExportConnection<br/>in finally block - always called<br/>Returned connector data? Persist it]
    CloseExport --> Done([Export complete])

    %% --- File-based connector ---
    CheckType -->|IConnectorExportUsingFiles| FileExport[connector.ExportAsync<br/>Single call with settings<br/>plus IConnectorProgress<br/>No open/close lifecycle]
    FileExport --> Done
```

## Parallel Export - Connector Isolation

```mermaid
flowchart TD
    Main[Main connector instance<br/>OpenExportConnection] --> Factory[Connector factory creates<br/>new instances per batch]

    Factory --> B1[Batch 1<br/>New LdapConnector<br/>Own OpenExportConnection<br/>Own DbContext]
    Factory --> B2[Batch 2<br/>New LdapConnector<br/>Own OpenExportConnection<br/>Own DbContext]
    Factory --> BN[Batch N<br/>New LdapConnector<br/>Own OpenExportConnection<br/>Own DbContext]

    B1 --> Close1[CloseExportConnection]
    B2 --> Close2[CloseExportConnection]
    BN --> CloseN[CloseExportConnection]

    Close1 --> MainClose[Main connector<br/>CloseExportConnection]
    Close2 --> MainClose
    CloseN --> MainClose
```

## Password Channel Lifecycle

Connectors that implement `IConnectorPasswordManagement` (the LDAP Connector) are driven through one sequence, `PasswordDeliveryCore`, whenever JIM writes a password: an initial password for an object it provisioned, a password an administrator sets, or a synchronised password change (#1635, #1639). The queued paths run in the Worker process's Password Delivery Service, not in a Worker Task, and open the channel once per Connected System for a batch of changes.

```mermaid
flowchart TD
    Start([Password to deliver]) --> Supports{Connector implements<br/>IConnectorPasswordManagement?}
    Supports -->|No| Leave[Leave the changes outstanding<br/>Connector cannot set passwords]
    Supports -->|Yes| Open[OpenPasswordConnection<br/>with system settings]
    Open -->|Throws| Transient[Transient failure<br/>nothing sent; changes stay due]
    Open --> Secure{Require Secure Transport on,<br/>and IsPasswordChannelSecure false?}
    Secure -->|Yes| Refuse[ClosePasswordConnection<br/>Configuration fault: nothing sent]
    Secure -->|No| Set[SetPasswordAsync per object<br/>a thrown connector is classified<br/>as transient, never escapes]
    Set --> Close[ClosePasswordConnection<br/>in finally block - always called]
    Close --> Done([Results recorded per change])
```

## Key Design Decisions

- **Two connector families**<br /> Call-based connectors (`IConnectorImportUsingCalls`/`IConnectorExportUsingCalls`) have an explicit open/close lifecycle with connection management. File-based connectors (`IConnectorImportUsingFiles`/`IConnectorExportUsingFiles`) handle everything in a single call with no connection state.

- **Service injection before open**<br /> Certificate and credential providers are injected before `OpenImportConnection`/`OpenExportConnection` is called. This allows connectors to decrypt passwords and load certificates during connection setup.

- **Watermark consistency**<br /> During paginated delta imports, the *original* persisted connector data is passed to every page, ensuring the connector sees a consistent view across pages. The new watermark from the first page is only saved once the run has staged everything the pages returned (#1868); a run that fails or is cancelled first keeps the watermark it started with, so the next run re-reads those changes instead of skipping them.

- **Parallel connector isolation**<br /> Each parallel export batch gets its own connector instance created via factory. This avoids shared connection state between concurrent batches, which is critical for connectors like LDAP that maintain stateful connections.

- **Close in finally, on every channel**<br /> The export connection is always closed, even if an exception occurs during export. The import connection is too, so an import that fails part-way still releases its connection and any temporary trust directory prepared for it, and the password channel likewise. This prevents connection leaks in long-running worker processes. Each connection is opened inside the block that closes it, including a parallel export batch's own connector, so a connection that fails to open is closed too (#1875): failing to connect is exactly when a connector has state to return at close, such as a pinned domain controller the failure invalidated.

- **Connector state returned at close wins**<br /> `CloseImportConnection` may return persisted connector data, persisted when the connection closes (even if the import failed) in place of the page watermark, which is then not persisted at all; the LDAP Connector uses this when using the connection invalidated a previously persisted domain controller pin (#1169). Null, the usual case, means nothing to override.

- **Metadata searches are paged (#1853)**<br /> The LDAP Connector reads its discovery searches (the Object Type list, the class and attribute schema, a partition's containers and the forest's domain controllers) page by page through `LdapPagedSearch` wherever the directory supports paging. Active Directory refuses an unpaged search that would exceed its MaxPageSize (1,000 by default) with "size limit exceeded", and a stock forest publishes well over 1,000 attribute definitions.

- **Settings reconciled at startup**<br /> When JIM starts, after each Connector Definition is brought into line with its Connector, every Connected System built on it gains a value for any setting the Connector has since added (carrying its default), and an unset value takes a newly declared default (`ConnectedSystemServer.ReconcileConnectedSystemSettingValuesAsync`). A Connected System that changes is saved under a System-attributed Activity recording what was added; one already in line is not written to, so an ordinary restart records nothing.

- **Managed scope is the connector's knowledge (#1250)**<br /> Container selection means the scope JIM manages, not merely what it reads. Before an export JIM states the Connected System's scope-deciding containers (selections and exclusions) to a connector implementing `IConnectorManagedScope`, and the connector refuses per object to write outside them, so the rest of the run proceeds. A Connected System with no container selections states nothing and permits everything. The scope is currently stated only on the connector instance the run was resolved with: the per-batch instances a parallel export creates through the factory do not receive it.

- **Declared phases (#454, #1148)**<br /> A connector that implements `IConnectorPhases` declares its internal steps before the run starts, and enters them through `IConnectorProgress`, so an administrator sees what the connector still has to do rather than only what it is doing now. A declaration that throws is logged and the run continues with JIM's own steps only.

- **Single dispatch point (#875)**<br /> Every connector instance comes from `IConnectorFactory.Create`, used by both the application layer and the Worker; previously each call site ran its own `new LdapConnector()`-style name switch. The factory matches `ConnectorDefinition.Name` against the built-in connectors and throws `NotSupportedException` for an unknown name. `Create` optionally takes a credential-protection and a certificate provider and applies them when the connector implements the matching capability interface: `ConnectedSystemServer` uses that overload, while the Worker creates a bare connector and its import/export processors inject the providers themselves immediately before opening the connection. User-supplied connector lookup will extend this factory rather than adding another switch.
