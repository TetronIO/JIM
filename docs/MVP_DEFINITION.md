# JIM MVP Definition

| | |
|---|---|
| **Version** | 1.14 |
| **Last Updated** | 2026-01-26 |
| **Status** | In Progress (~94% Complete) |

---

## MVP Completion Summary

| Category | Progress | Complete | Total | % |
|----------|----------|----------|-------|---|
| Connectors | `██████████` | 10 | 10 | 100% |
| Inbound Sync | `██████████` | 18 | 18 | 100% |
| Outbound Sync | `██████████` | 15 | 15 | 100% |
| Scheduling | `██████░░░░` | 5 | 10 | 50% |
| Admin UI | `█████████░` | 19 | 20 | 95% |
| Security | `██████████` | 5 | 5 | 100% |
| Operations | `██████████` | 6 | 6 | 100% |
| API Coverage | `██████████` | 7 | 7 | 100% |
| Tooling | `██████████` | 5 | 5 | 100% |
| Release | `██████████` | 9 | 9 | 100% |
| **Overall** | `█████████░` | **99** | **105** | **94%** |

### Priority Order for Remaining Work

**Critical Path (Required for MVP):**
1. **Scheduler Service** (#168) - Automate run profile execution (5 items remaining)

**Nice to Have (Can follow MVP):**
- Dashboard admin home page (#169)
- Unique value generation (#242)
- Full RBAC (#21) and Synchronisation Readers role (#9)
- Sync preview

### Recently Completed ✓
- ~~CSO/MVO Change Tracking (#14, #269)~~ - Full change history with timeline UI, initiator/mechanism tracking, deleted objects view, retention cleanup
- ~~Progress Indication (#246)~~ - Real-time progress bars, percentage tracking, and contextual messages on Operations page
- ~~Sync Processor Refactoring (#252)~~ - Extracted shared logic into SyncTaskProcessorBase, eliminated ~2,100 lines of duplication
- ~~WhenAuthoritativeSourceDisconnected (#115)~~ - Full deletion rule with UI configuration, API validation, unit and integration tests
- ~~Defensive Deduplication (#284)~~ - Multi-valued reference attribute deduplication during sync
- ~~PendingExportStatus Rename (#285)~~ - ExportNotImported renamed to ExportNotConfirmed for consistent terminology
- ~~API Container Selection (#283)~~ - Container selection endpoints for LDAP connector
- ~~Same-batch Import Deduplication (#280)~~ - Detect duplicate external IDs within a single import batch
- ~~Unconfirmed Export Surfacing (#287)~~ - Sync surfaces unconfirmed pending exports with confirmation stats
- ~~Integration Testing Framework (#173)~~ - Phase 1 complete with 5 scenarios (1, 2, 4, 5, 8), Samba AD infrastructure
- ~~Scenario 8 Cross-domain Entitlement Sync~~ - Groups sync between AD domains with reference attribute translation
- ~~Synchronous MVO Deletion~~ - MVOs with 0-grace-period now delete immediately during sync
- ~~Credential Encryption (#171)~~ - AES-256-GCM encryption for connector passwords at rest
- ~~Background MVO deletion job (#120)~~ - Housekeeping deletes orphaned MVOs after grace period expires
- ~~MVO Deletion Rules (#203)~~ - Pending deletions UI, API endpoints, background housekeeping job
- ~~PowerShell Module (#176)~~ - 35 cmdlets with full CRUD operations and name-based parameters
- ~~API Coverage (#183)~~ - Activity, Run Profiles, Connected Systems, Sync Rules, MVO query, Data Generation
- ~~Release Process (#188)~~ - GitHub Actions workflow, air-gapped bundles, PSGallery publishing
- ~~API Key Authentication (#175)~~ - X-API-Key header auth for CI/CD and automation
- ~~Server-side file browser (#177)~~ - File path selection for connector settings
- ~~Merged API into Web (#180)~~ - Simplified deployment architecture
- ~~API Authentication (#8)~~ - JWT Bearer with OIDC, role-based authorisation
- ~~Outbound Sync (#121)~~ - Implemented in ExportEvaluationServer.cs
- ~~Export Execution~~ - Implemented in ExportExecutionServer.cs
- ~~LDAP Connector Export~~ - Implemented in LdapConnectorExport.cs
- ~~Certificate Store~~ - Implemented for LDAPS trusted certificates
- ~~Pending Export review UI (#25)~~ - Server-side sorting, filtering, pagination

---

## Overview

This document defines the Minimum Viable Product (MVP) for JIM - Junctional Identity Manager. The MVP represents the minimum feature set required for JIM to be a functional, production-ready identity management system that can synchronise identities between connected systems.

## MVP Criteria

For JIM to be considered MVP-complete, it must support a complete identity lifecycle:

1. **Import** identities from source systems
2. **Reconcile** imported identities with the metaverse (join, create, update)
3. **Export** identity changes to target systems
4. **Automate** the synchronisation process via scheduling

---

## Feature Checklist

### 1. Connectors

#### 1.1 Connector Framework
- Connector interface abstraction (`IConnector`, `IConnectorCapabilities`) ✓
- Import capability interface (`IConnectorImportUsingCalls`, `IConnectorImportUsingFiles`) ✓
- Export capability interface (`IConnectorExportUsingCalls`, `IConnectorExportUsingFiles`) ✓
- Schema discovery capability ✓
- Partition support ✓
- Connector configuration and settings ✓

#### 1.2 At Least One Production-Ready Connector
- LDAP/Active Directory Connector - Import ✓
- LDAP/Active Directory Connector - Export (create, update, delete) ✓
- File Connector - Import ✓
- File Connector - Export ✓

### 2. Inbound Synchronisation (Source → Metaverse)

#### 2.1 Import Processing
- Full Import run profile ✓
- Object creation detection ✓
- Object update detection ✓
- Object deletion detection (obsoletion) ✓
- Multi-valued attribute handling ✓
- All data types supported (Text, Number, DateTime, Binary, Reference, Guid, Boolean) ✓

#### 2.2 Synchronisation / Reconciliation
- Full Synchronisation run profile ✓
- Join rules - match CSO to existing MVO ✓
- Projection - create new MVO from CSO when no match ✓
- Attribute flow - CSO attributes flow to MVO via sync rules ✓
- Attribute flow for new joins ✓
- Attribute flow for existing joins (updates) ✓
- Multi-valued attribute flow ✓

#### 2.3 MVO Lifecycle Management
- MVO deletion rules (Manual, WhenLastConnectorDisconnected, WhenAuthoritativeSourceDisconnected) ✓
- Deletion grace period support ✓
- Scheduled deletion date tracking ✓
- Reconnection clears scheduled deletion ✓
- Attribute recall on CSO obsoletion (`RemoveContributedAttributesOnObsoletion`) ✓
- Background job for processing scheduled deletions (#120) ✓

### 3. Outbound Synchronisation (Metaverse → Target)

#### 3.1 Export Triggering (#121)
- Detect MVO changes that require export ✓
- Create Pending Export for MVO attribute changes ✓
- Create Pending Export for new MVO (provisioning) ✓
- Create Pending Export for MVO deletion (deprovisioning) ✓
- Evaluate export sync rules to determine target CSO changes ✓

#### 3.2 Pending Export Management
- Pending Export data model ✓
- Pending Export confirmation (verify export was applied) ✓
- Pending Export execution (send to connector) ✓
- Pending Export retry logic ✓
- Pending Export error handling ✓

#### 3.3 Export Execution
- Export run profile processing ✓
- Connector export method invocation ✓
- Create object in target system ✓
- Update object in target system ✓
- Delete object in target system ✓

### 4. Scheduling & Automation

#### 4.1 Scheduler Service (#168)
- Scheduled task data model
- Scheduler service implementation
- Cron-style or interval-based scheduling
- Run profile scheduling configuration
- Scheduler configuration UI

#### 4.2 Background Processing
- Worker service for task execution ✓
- Task queuing and state management ✓
- Cancellation support ✓
- Activity tracking and logging ✓
- Background job for scheduled MVO deletions (#120) ✓

### 5. Administration UI

#### 5.1 Core Views
- Operations view (run activities, task status) ✓
- Connected System list and detail ✓
- Connector configuration ✓
- Schema inspection ✓
- Run Profile management ✓
- Sync Rule configuration ✓
- Metaverse Object Type management ✓
- Metaverse Object list and detail ✓
- Activity history ✓
- API Key management (#175) ✓
- Certificate management ✓
- Pending deletions view (#203) ✓
- Change history timeline and audit trail (#14) ✓
- Deleted objects view with change audit ✓

#### 5.2 Synchronisation Management
- Manual run profile execution ✓
- Activity monitoring ✓
- Pending Export review/management (#25) ✓
- Server-side file browser for connector settings (#177) ✓
- Progress indication for running operations (#246) ✓
- Sync preview (what-if analysis)

### 6. Security & Access Control

#### 6.1 Authentication
- SSO/OIDC authentication for Web UI ✓
- API authentication via JWT Bearer (#8) ✓
- API Key authentication for non-interactive access (#175) ✓

#### 6.2 Authorisation
- Basic role model ✓

#### 6.3 Data Protection
- Connector credential encryption at rest (#171) ✓

### 7. Operational Readiness

#### 7.1 Deployment
- Docker containerisation ✓
- Docker Compose for full stack ✓
- Environment-based configuration ✓

#### 7.2 Monitoring & Troubleshooting
- Activity logging ✓
- Run profile execution tracking ✓
- Error capture and display ✓

### 8. API Coverage (#183)

#### 8.1 Priority 1 - Critical for Automation
- Activity Controller - Monitor sync operations via API ✓
- Run Profile Execution - Trigger syncs via API ✓
- MVO List/Search - Query metaverse objects via API ✓

#### 8.2 Priority 2 - Full Automation
- Connected System CRUD - Full API management ✓
- Sync Rule CRUD - Full API management ✓
- Run Profile CRUD - Full API management ✓
- Pending Export Read - Monitor pending exports via API ✓

### 9. Tooling & Automation

#### 9.1 PowerShell Module (#176)
- Cmdlets for common administration tasks (35 cmdlets) ✓
- API key authentication support ✓
- Script-based automation with name-based parameters ✓

#### 9.2 Testing Framework (#173)
- End-to-end integration tests with real connected systems ✓
- Automated test scenarios (Scenarios 1, 2, 4, 5, 8 complete; 6-7 deferred pending Internal MVO design) ✓

### 10. Release & Deployment (#188)

#### 10.1 Release Process
- VERSION file for centralised version management ✓
- CHANGELOG.md following Keep a Changelog format ✓
- GitHub Actions release workflow ✓

#### 10.2 Air-Gapped Deployment
- Release bundle script (Build-ReleaseBundle.ps1) ✓
- Docker image export/import support ✓
- SHA256 checksums for integrity verification ✓

#### 10.3 Distribution
- Docker images to GitHub Container Registry ✓
- PowerShell module to PSGallery ✓
- GitHub Releases with downloadable bundles ✓

---

## Success Criteria

JIM MVP is complete when:

1. An administrator can configure a source system (e.g., HR CSV file) and a target system (e.g., Active Directory)
2. Identities are imported from the source and reconciled in the metaverse
3. New identities are automatically provisioned to the target system
4. Identity attribute changes flow from source to target
5. Identity deletions in the source result in appropriate action in the target (disable/delete)
6. The entire process can run on a schedule without manual intervention

---

## Non-Goals for MVP

The following are explicitly out of scope for MVP:

- ~~Delta/incremental sync~~ (implemented: delta import and delta sync processors)
- Multiple connector types beyond LDAP and File
- ~~Authoritative source deletion rules~~ (implemented: WhenAuthoritativeSourceDisconnected #115)
- Conditional deletion rules
- Soft delete / recycle bin
- Self-service portal
- Approval workflows
- Password synchronisation
