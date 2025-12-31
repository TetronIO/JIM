# E2E Import to Provisioning Process

```mermaid
sequenceDiagram
    participant Source as Source CS<br/>(e.g. HR)
    participant Import as Import Processor
    participant CSO as CSO Repository
    participant MV as Metaverse
    participant Export as Export Evaluator
    participant PE as PendingExport
    participant ExecProc as Export Processor
    participant Target as Target CS<br/>(e.g. AD)
    participant ConfImport as Confirming Import

    rect rgb(230, 245, 255)
        Note over Source,MV: Phase 1: Initial Import
        Source->>Import: Import objects
        Import->>CSO: Create CSO (Status: Normal)
        Import->>MV: Sync to Metaverse
        MV->>MV: Create/Update MVO
    end

    rect rgb(255, 245, 230)
        Note over MV,PE: Phase 2: Export Evaluation
        MV->>Export: MVO changed
        Export->>CSO: Does CSO exist for Target?
        CSO-->>Export: No
        Export->>CSO: CreatePendingProvisioningCsoAsync()
        CSO-->>Export: New CSO (Status: PendingProvisioning)
        Export->>PE: Create PendingExport
        Note right of PE: ConnectedSystemObjectId = CSO.Id<br/>ChangeType = Create
    end

    rect rgb(230, 255, 230)
        Note over PE,Target: Phase 3: Export Execution
        ExecProc->>PE: Get PendingExports
        ExecProc->>CSO: Get CSO for DN calculation
        alt CSO Found with attributes
            CSO-->>ExecProc: CSO with DN attributes
            ExecProc->>Target: Execute export (LDAP Add)
            Target-->>ExecProc: Success
            ExecProc->>PE: Status = Exported
        else CSO NULL or missing attributes
            CSO-->>ExecProc: NULL / No attributes
            ExecProc->>ExecProc: ERROR: DN could not be determined
        end
    end

    rect rgb(255, 230, 245)
        Note over Target,PE: Phase 4: Confirming Import
        Target->>ConfImport: Import objects
        ConfImport->>CSO: Find CSO by Primary ExtID
        alt Found by Primary
            CSO-->>ConfImport: Existing CSO
        else Not found - try Secondary
            ConfImport->>CSO: Find by Secondary ExtID (DN)
            CSO-->>ConfImport: PendingProvisioning CSO
            ConfImport->>CSO: Transition to Normal
        end
        ConfImport->>CSO: Update CSO with imported values
        ConfImport->>PE: ReconcilePendingExportsAsync()
        PE->>PE: Compare exported vs imported
        alt All confirmed
            PE->>PE: Delete PendingExport ✓
        else Not confirmed
            PE->>PE: Retry or Fail
        end
    end

    rect rgb(255, 230, 230)
        Note over ConfImport,CSO: Phase 5: Deletion Detection (PROBLEM AREA)
        ConfImport->>CSO: Get all ExtIDs (excl. PendingProvisioning)
        ConfImport->>ConfImport: Compare with import results
        alt ExtID not in import
            ConfImport->>CSO: Mark as Obsolete ⚠️
            Note right of CSO: CSOs incorrectly deleted here
        end
    end
```
