# Changelog

All notable changes to JIM (Junctional Identity Manager) will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.2.0] - Unreleased

### Added

#### PowerShell Module (61 new cmdlets, 64 total)
- Connected Systems management: `Get-JIMConnectedSystem`, `New-JIMConnectedSystem`, `Set-JIMConnectedSystem`, `Remove-JIMConnectedSystem`
- Schema management: `Import-JIMConnectedSystemSchema`, `Set-JIMConnectedSystemObjectType`, `Set-JIMConnectedSystemAttribute`
- Hierarchy management: `Import-JIMConnectedSystemHierarchy`
- Partition and container management: `Get-JIMConnectedSystemPartition`, `Set-JIMConnectedSystemPartition`, `Set-JIMConnectedSystemContainer`
- Connector definitions: `Get-JIMConnectorDefinition`
- Sync Rules: `Get-JIMSyncRule`, `New-JIMSyncRule`, `Set-JIMSyncRule`, `Remove-JIMSyncRule`
- Sync Rule Mappings with expression support: `Get-JIMSyncRuleMapping`, `New-JIMSyncRuleMapping`, `Remove-JIMSyncRuleMapping`
- Object Matching Rules: `Get-JIMMatchingRule`, `New-JIMMatchingRule`, `Set-JIMMatchingRule`, `Remove-JIMMatchingRule`
- Scoping Criteria: `Get-JIMScopingCriteria`, `New-JIMScopingCriteriaGroup`, `Set-JIMScopingCriteriaGroup`, `Remove-JIMScopingCriteriaGroup`, `New-JIMScopingCriterion`, `Remove-JIMScopingCriterion`
- Run Profiles: `Get-JIMRunProfile`, `New-JIMRunProfile`, `Set-JIMRunProfile`, `Remove-JIMRunProfile`, `Start-JIMRunProfile`
- Real-time progress tracking for run profile executions
- Activities: `Get-JIMActivity`, `Get-JIMActivityStats`
- Metaverse: `Get-JIMMetaverseObject`, `Get-JIMMetaverseObjectType`, `Set-JIMMetaverseObjectType`, `Get-JIMMetaverseAttribute`, `New-JIMMetaverseAttribute`, `Set-JIMMetaverseAttribute`, `Remove-JIMMetaverseAttribute`
- MVO deletion rule configuration
- API Keys: `Get-JIMApiKey`, `New-JIMApiKey`, `Set-JIMApiKey`, `Remove-JIMApiKey`
- Certificates: `Get-JIMCertificate`, `Add-JIMCertificate`, `Set-JIMCertificate`, `Remove-JIMCertificate`, `Export-JIMCertificate`, `Test-JIMCertificate`
- Security: `Get-JIMRole`
- Data Generation: `Get-JIMDataGenerationTemplate`, `Get-JIMExampleDataSet`, `Invoke-JIMDataGenerationTemplate`
- Expressions: `Test-JIMExpression`
- History: `Get-JIMDeletedObject`, `Get-JIMHistoryCount`, `Invoke-JIMHistoryCleanup`
- Name-based parameter alternatives for all cmdlets (e.g., `-ConnectedSystemName` instead of `-ConnectedSystemId`)

#### API Endpoints
- CRUD endpoints for Connected Systems (`POST`, `PUT` `/api/v1/synchronisation/connected-systems`)
- CRUD endpoints for Sync Rules (`POST`, `PUT`, `DELETE` `/api/v1/synchronisation/sync-rules`)
- CRUD endpoints for Run Profiles (`POST`, `PUT`, `DELETE` `/api/v1/synchronisation/connected-systems/{id}/run-profiles`)

#### Infrastructure
- Release workflow for automated builds and publishing
- Air-gapped deployment bundle support
- PowerShell Gallery publishing

### Changed
- Server-side filtering and sorting for MVO type list pages

## [0.1.0-alpha] - 2025-12-12

### Added

#### Core Platform
- Initial development release
- Core identity management functionality
- Blazor web interface
- REST API
- PostgreSQL database support
- Docker containerisation
- CSV connector
- Basic synchronisation engine

#### PowerShell Module (3 cmdlets)
- Initial preview release published to [PSGallery](https://www.powershellgallery.com/packages/JIM/0.1.0-alpha)
- Connection management: `Connect-JIM`, `Disconnect-JIM`, `Test-JIMConnection`

#### Infrastructure
- Release workflow for automated builds and publishing
- Air-gapped deployment bundle support
- PowerShell Gallery publishing

[Unreleased]: https://github.com/TetronIO/JIM/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/TetronIO/JIM/compare/v0.1.0-alpha...v0.2.0
[0.1.0-alpha]: https://github.com/TetronIO/JIM/releases/tag/v0.1.0-alpha
