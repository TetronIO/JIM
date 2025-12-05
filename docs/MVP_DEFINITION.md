# JIM MVP Definition

> **Version**: 1.0
> **Last Updated**: 2025-12-02
> **Status**: In Progress

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
- [ ] LDAP/Active Directory Connector - Export (create, update, delete)
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
- [ ] Detect MVO changes that require export
- [ ] Create Pending Export for MVO attribute changes
- [ ] Create Pending Export for new MVO (provisioning)
- [ ] Create Pending Export for MVO deletion (deprovisioning)
- [ ] Evaluate export sync rules to determine target CSO changes

#### 3.2 Pending Export Management
- [x] Pending Export data model
- [x] Pending Export confirmation (verify export was applied)
- [ ] Pending Export execution (send to connector)
- [ ] Pending Export retry logic
- [ ] Pending Export error handling

#### 3.3 Export Execution
- [ ] Export run profile processing
- [ ] Connector export method invocation
- [ ] Create object in target system
- [ ] Update object in target system
- [ ] Delete object in target system

### 4. Scheduling & Automation

#### 4.1 Scheduler Service
- [ ] Scheduled task data model
- [ ] Scheduler service implementation
- [ ] Cron-style or interval-based scheduling
- [ ] Run profile scheduling configuration

#### 4.2 Background Processing
- [x] Worker service for task execution
- [x] Task queuing and state management
- [x] Cancellation support
- [x] Activity tracking and logging
- [ ] Scheduled MVO deletion processing (#120)

### 5. Administration UI

#### 5.1 Core Views
- [x] Dashboard / Operations view
- [x] Connected System list and detail
- [x] Connector configuration
- [x] Schema inspection
- [x] Run Profile management
- [x] Sync Rule configuration
- [x] Metaverse Object Type management
- [x] Metaverse Object list and detail
- [x] Activity history

#### 5.2 Synchronisation Management
- [x] Manual run profile execution
- [x] Activity monitoring
- [ ] Pending Export review/management (#25)
- [ ] Sync preview (what-if analysis)
- [ ] Scheduler configuration UI

### 6. Security & Access Control

#### 6.1 Authentication
- [x] SSO/OIDC authentication for Web UI
- [ ] API authentication (#8)

#### 6.2 Authorisation
- [x] Basic role model
- [ ] Full RBAC implementation (#21)
- [ ] Synchronisation Readers role (#9)

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

---

## MVP Completion Summary

| Category | Complete | Total | Status |
|----------|----------|-------|--------|
| Connectors | 7 | 8 | 88% |
| Inbound Sync | 14 | 15 | 93% |
| Outbound Sync | 2 | 10 | 20% |
| Scheduling | 4 | 6 | 67% |
| Admin UI | 10 | 14 | 71% |
| Security | 2 | 5 | 40% |
| Operations | 4 | 6 | 67% |
| **Overall** | **43** | **64** | **~67%** |

---

## Priority Order for Remaining Work

### Critical Path (Required for MVP)
1. **Outbound Sync** (#121) - Without this, JIM cannot provision/update target systems
2. **Export Execution** - Process pending exports via connectors
3. **LDAP Connector Export** - Enable write operations to AD
4. **Scheduler Service** - Automate run profile execution

### Important (Highly Desirable for MVP)
5. Background job for scheduled MVO deletions (#120)
6. Pending Export review UI (#25)
7. API authentication (#8)

### Nice to Have (Can follow MVP)
8. Full RBAC (#21)
9. Change history (#14)
10. Sync preview
11. Delta sync support

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
