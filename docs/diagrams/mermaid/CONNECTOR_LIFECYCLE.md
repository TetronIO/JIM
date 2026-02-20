# Connector Lifecycle

> Generated against JIM v0.2.0 (`988472e3`). If the codebase has changed significantly since then, these diagrams may be out of date.

This diagram shows how connectors are resolved, configured, opened, used, and closed across import and export operations. Connectors implement capability interfaces that determine their lifecycle shape.

## Connector Interface Hierarchy

```mermaid
flowchart TD
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
```

## Connector Resolution

```mermaid
flowchart TD
    TaskStart([Worker receives<br/>SynchronisationWorkerTask]) --> GetCS[Get ConnectedSystem<br/>with ConnectorDefinition]
    GetCS --> MatchName{ConnectorDefinition<br/>Name?}

    MatchName -->|LdapConnectorName| CreateLdap[new LdapConnector]
    MatchName -->|FileConnectorName| CreateFile[new FileConnector]
    MatchName -->|Unknown| ThrowError[throw NotSupportedException<br/>Activity fails with error]

    CreateLdap --> GetRunProfile[Resolve RunProfile<br/>from ConnectedSystem.RunProfiles]
    CreateFile --> GetRunProfile

    GetRunProfile --> RouteByType{RunProfile<br/>RunType?}
    RouteByType -->|FullImport<br/>DeltaImport| ImportProcessor[SyncImportTaskProcessor]
    RouteByType -->|FullSynchronisation| FullSyncProcessor[SyncFullSyncTaskProcessor]
    RouteByType -->|DeltaSynchronisation| DeltaSyncProcessor[SyncDeltaSyncTaskProcessor]
    RouteByType -->|Export| ExportProcessor[SyncExportTaskProcessor]
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
    SetCred --> Open[OpenImportConnection<br/>with system settings]

    Open --> PageLoop{More pages?<br/>initialPage OR<br/>tokens present}
    PageLoop -->|Yes| CallImport[connector.ImportAsync<br/>Pass ORIGINAL persisted data<br/>for consistent watermark]
    CallImport --> CaptureWatermark{First page with<br/>new persisted data?}
    CaptureWatermark -->|Yes| SaveNewWatermark[Capture new watermark<br/>Don't persist yet]
    CaptureWatermark -->|No| ProcessPage
    SaveNewWatermark --> ProcessPage[Process imported objects<br/>Create/update CSOs]
    ProcessPage --> NextPage[Pass pagination tokens<br/>for next page]
    NextPage --> PageLoop

    PageLoop -->|No| PersistWM{New watermark<br/>captured?}
    PersistWM -->|Yes| UpdateCS[Update ConnectedSystem<br/>PersistedConnectorData]
    PersistWM -->|No| Close
    UpdateCS --> Close[CloseImportConnection]
    Close --> Done([Import complete])

    %% --- File-based connector ---
    CheckType -->|IConnectorImportUsingFiles| FileImport[connector.ImportAsync<br/>Returns all objects at once<br/>No open/close lifecycle]
    FileImport --> FileProcess[Process all objects]
    FileProcess --> Done
```

## Export Lifecycle

```mermaid
flowchart TD
    Start([Export starts]) --> CheckType{Connector<br/>type?}

    %% --- Call-based connector ---
    CheckType -->|IConnectorExportUsingCalls| InjectCert[Inject CertificateProvider<br/>and CredentialProtection<br/>if connector supports them]
    InjectCert --> Open[OpenExportConnection<br/>with system settings]

    Open --> SplitExports[Split exports into<br/>immediate and deferred]
    SplitExports --> CheckParallel{MaxParallelism > 1?}

    CheckParallel -->|Yes| ParallelExport[Each batch gets:<br/>Own connector instance via factory<br/>Own OpenExportConnection<br/>Own DbContext]
    CheckParallel -->|No| SequentialExport[Single connector<br/>processes batches sequentially]

    ParallelExport --> BatchLoop[connector.ExportAsync<br/>per batch]
    SequentialExport --> BatchLoop

    BatchLoop --> Deferred[Process deferred exports<br/>Resolve references, export]
    Deferred --> CloseExport[CloseExportConnection<br/>in finally block - always called]
    CloseExport --> Done([Export complete])

    %% --- File-based connector ---
    CheckType -->|IConnectorExportUsingFiles| FileExport[connector.ExportAsync<br/>Single call with settings<br/>No open/close lifecycle]
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

## Key Design Decisions

- **Two connector families**: Call-based connectors (`IConnectorImportUsingCalls`/`IConnectorExportUsingCalls`) have an explicit open/close lifecycle with connection management. File-based connectors (`IConnectorImportUsingFiles`/`IConnectorExportUsingFiles`) handle everything in a single call with no connection state.

- **Service injection before open**: Certificate and credential providers are injected before `OpenImportConnection`/`OpenExportConnection` is called. This allows connectors to decrypt passwords and load certificates during connection setup.

- **Watermark consistency**: During paginated delta imports, the *original* persisted connector data is passed to every page. The new watermark from the first page is only saved after all pages complete, ensuring the connector sees a consistent view across pages.

- **Parallel connector isolation**: Each parallel export batch gets its own connector instance created via factory. This avoids shared connection state between concurrent batches — critical for connectors like LDAP that maintain stateful connections.

- **CloseExportConnection in finally**: The export connection is always closed, even if an exception occurs during export. This prevents connection leaks in long-running worker processes.

- **Hard-coded resolution**: Connectors are currently resolved by name matching against `ConnectorDefinition.Name`. This will be extended to support user-supplied connector lookup in the future.
