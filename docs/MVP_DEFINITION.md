# JIM MVP Definition

> **Version**: 1.7
> **Last Updated**: 2025-12-16
> **Status**: In Progress (~90% Complete)

---

## MVP Completion Summary

| Category | Progress | Complete | Total | % |
|----------|----------|----------|-------|---|
| Connectors | `██████████` | 10 | 10 | 100% |
| Inbound Sync | `█████████░` | 14 | 15 | 93% |
| Outbound Sync | `██████████` | 15 | 15 | 100% |
| Scheduling | `████░░░░░░` | 4 | 9 | 44% |
| Admin UI | `█████████░` | 13 | 14 | 93% |
| Security | `███████░░░` | 5 | 7 | 71% |
| Operations | `██████████` | 7 | 7 | 100% |
| API Coverage | `██████████` | 7 | 7 | 100% |
| PowerShell | `██████████` | 3 | 3 | 100% |
| **Overall** | `█████████░` | **78** | **87** | **90%** |

### Priority Order for Remaining Work

**Critical Path (Required for MVP):**
1. **Scheduler Service** (#168) - Automate run profile execution (4 items remaining)

**Important (Highly Desirable for MVP):**
2. Background job for scheduled MVO deletions (#120)
3. **Connector credential encryption** - Encrypt passwords at rest (#171)

**Nice to Have (Can follow MVP):**
- Dashboard admin home page (#169)
- Full RBAC (#21)
- Change history (#14)
- Sync preview
- Delta sync support
- End-to-End Integration Testing Framework (#173)

### Recently Completed ✓
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
- [x] Connector interface abstraction (`IConnector`, `IConnectorCapabilities`)
- [x] Import capability interface (`IConnectorImportUsingCalls`, `IConnectorImportUsingFiles`)
- [x] Export capability interface (`IConnectorExportUsingCalls`, `IConnectorExportUsingFiles`)
- [x] Schema discovery capability
- [x] Partition support
- [x] Connector configuration and settings

#### 1.2 At Least One Production-Ready Connector
- [x] LDAP/Active Directory Connector - Import
- [x] LDAP/Active Directory Connector - Export (create, update, delete)
- [x] File Connector - Import
- [x] File Connector - Export

### 2. Inbound Synchronisation (Source → Metaverse)

#### 2.1 Import Processing
- [x] Full Import run profile
- [x] Object creation detection
- [x] Object update detection
- [x] Object deletion detection (obsoletion)
- [x] Multi-valued attribute handling
- [x] All data types supported (Text, Number, DateTime, Binary, Reference, Guid, Boolean)

#### 2.2 Synchronisation / Reconciliation
- [x] Full Synchronisation run profile
- [x] Join rules - match CSO to existing MVO
- [x] Projection - create new MVO from CSO when no match
- [x] Attribute flow - CSO attributes flow to MVO via sync rules
- [x] Attribute flow for new joins
- [x] Attribute flow for existing joins (updates)
- [x] Multi-valued attribute flow

#### 2.3 MVO Lifecycle Management
- [x] MVO deletion rules (Manual, WhenLastConnectorDisconnected)
- [x] Deletion grace period support
- [x] Scheduled deletion date tracking
- [x] Reconnection clears scheduled deletion
- [x] Attribute recall on CSO obsoletion (`RemoveContributedAttributesOnObsoletion`)
- [ ] Background job for processing scheduled deletions (#120)

### 3. Outbound Synchronisation (Metaverse → Target)

#### 3.1 Export Triggering (#121)
- [x] Detect MVO changes that require export
- [x] Create Pending Export for MVO attribute changes
- [x] Create Pending Export for new MVO (provisioning)
- [x] Create Pending Export for MVO deletion (deprovisioning)
- [x] Evaluate export sync rules to determine target CSO changes

#### 3.2 Pending Export Management
- [x] Pending Export data model
- [x] Pending Export confirmation (verify export was applied)
- [x] Pending Export execution (send to connector)
- [x] Pending Export retry logic
- [x] Pending Export error handling

#### 3.3 Export Execution
- [x] Export run profile processing
- [x] Connector export method invocation
- [x] Create object in target system
- [x] Update object in target system
- [x] Delete object in target system

### 4. Scheduling & Automation

#### 4.1 Scheduler Service
- [ ] Scheduled task data model
- [ ] Scheduler service implementation
- [ ] Cron-style or interval-based scheduling
- [ ] Run profile scheduling configuration
- [ ] Scheduler configuration UI

#### 4.2 Background Processing
- [x] Worker service for task execution
- [x] Task queuing and state management
- [x] Cancellation support
- [x] Activity tracking and logging

### 5. Administration UI

#### 5.1 Core Views
- [x] Operations view (run activities, task status)
- [x] Connected System list and detail
- [x] Connector configuration
- [x] Schema inspection
- [x] Run Profile management
- [x] Sync Rule configuration
- [x] Metaverse Object Type management
- [x] Metaverse Object list and detail
- [x] Activity history
- [x] API Key management (#175)
- [x] Certificate management

#### 5.2 Synchronisation Management
- [x] Manual run profile execution
- [x] Activity monitoring
- [x] Pending Export review/management (#25)
- [x] Server-side file browser for connector settings (#177)
- [ ] Sync preview (what-if analysis)

### 6. Security & Access Control

#### 6.1 Authentication
- [x] SSO/OIDC authentication for Web UI
- [x] API authentication via JWT Bearer (#8)
- [x] API Key authentication for non-interactive access (#175)

#### 6.2 Authorisation
- [x] Basic role model
- [ ] Full RBAC implementation (#21)
- [ ] Synchronisation Readers role (#9)

#### 6.3 Data Protection
- [ ] Connector credential encryption at rest (#171)

### 7. Operational Readiness

#### 7.1 Deployment
- [x] Docker containerisation
- [x] Docker Compose for full stack
- [x] Environment-based configuration
- [ ] Continuous deployment to demo site (#39)

#### 7.2 Monitoring & Troubleshooting
- [x] Activity logging
- [x] Run profile execution tracking
- [x] Error capture and display
- [ ] Change history / audit trail (#14)

### 8. API Coverage (#183)

#### 8.1 Priority 1 - Critical for Automation
- [x] Activity Controller - Monitor sync operations via API
- [x] Run Profile Execution - Trigger syncs via API
- [x] MVO List/Search - Query metaverse objects via API

#### 8.2 Priority 2 - Full Automation
- [x] Connected System CRUD - Full API management
- [x] Sync Rule CRUD - Full API management
- [x] Run Profile CRUD - Full API management
- [x] Pending Export Read - Monitor pending exports via API

### 9. Tooling & Automation

#### 9.1 PowerShell Module (#176)
- [x] Cmdlets for common administration tasks (35 cmdlets)
- [x] API key authentication support
- [x] Script-based automation with name-based parameters

#### 9.2 Testing Framework (#173)
- [ ] End-to-end integration tests with real connected systems
- [ ] Automated test scenarios

### 10. Release & Deployment (#188)

#### 10.1 Release Process
- [x] VERSION file for centralised version management
- [x] CHANGELOG.md following Keep a Changelog format
- [x] GitHub Actions release workflow

#### 10.2 Air-Gapped Deployment
- [x] Release bundle script (Build-ReleaseBundle.ps1)
- [x] Docker image export/import support
- [x] SHA256 checksums for integrity verification

#### 10.3 Distribution
- [x] Docker images to GitHub Container Registry
- [x] PowerShell module to PSGallery
- [x] GitHub Releases with downloadable bundles

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

- Delta/incremental sync (full sync is sufficient for MVP)
- Multiple connector types beyond LDAP and File
- Advanced deletion rules (authoritative source, conditional deletion)
- Soft delete / recycle bin
- Group membership management
- Self-service portal
- Approval workflows
- Password synchronisation
