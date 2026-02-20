workspace "JIM Identity Management System" "C4 model for JIM - a central identity hub implementing the metaverse pattern" {

    !identifiers hierarchical

    configuration {
        scope softwaresystem
    }

    model {
        # People
        admin = person "Administrator" "Identity management administrator who configures connected systems, sync rules, and monitors synchronisation"
        automation = person "Automation Client" "CI/CD pipelines and scripts using API keys"

        # External Systems
        idp = softwareSystem "Identity Provider" "OIDC/SSO provider (Keycloak, Entra ID, Auth0, AD FS)" "External"
        ad = softwareSystem "Active Directory / LDAP" "Enterprise directory services for users and groups" "External"
        hr = softwareSystem "HR Systems" "Authoritative source for employee identity data" "External"
        files = softwareSystem "File Systems" "CSV file-based bulk import/export" "External"

        # Planned External Systems
        databases = softwareSystem "Enterprise Databases" "PostgreSQL, MySQL, Oracle, SQL Server with identity data" "External,Planned"
        scim = softwareSystem "SCIM 2.0 Systems" "Cloud applications supporting SCIM provisioning" "External,Planned"

        # JIM System
        jim = softwareSystem "JIM" "Central identity hub implementing the metaverse pattern. Synchronises identities across enterprise systems with bidirectional data flow and transformation" {

            webApp = container "Web Application" "Provides interactive admin UI and REST API endpoints" "ASP.NET Core 9.0, Blazor Server, MudBlazor" "Web Browser" {
                blazorPages = component "Blazor Pages" "Interactive admin UI - Dashboard, Activities, Connected Systems, Sync Rules, Schedules, Types" "Blazor Server Components"
                apiControllers = component "API Controllers" "REST endpoints for metaverse, synchronisation, schedules, activities, security, and more" "ASP.NET Core Controllers"
                authMiddleware = component "Authentication Middleware" "OIDC/SSO authentication and API key validation" "ASP.NET Core Middleware"
            }

            appLayer = container "Application Layer" "Business logic and domain services" "JIM.Application" {
                jimApplication = component "JimApplication Facade" "Single entry point to all domain services" "C# Facade Class"
                metaverseServer = component "MetaverseServer" "Metaverse object CRUD, querying, attribute management" "C# Service"
                connectedSystemServer = component "ConnectedSystemServer" "Connected system lifecycle, configuration, run profiles, sync rules, and attribute mappings" "C# Service"
                objectMatchingServer = component "ObjectMatchingServer" "Join logic between ConnectedSystemObjects and MetaverseObjects" "C# Service"
                exportEvaluationServer = component "ExportEvaluationServer" "Determines pending exports based on attribute changes" "C# Service"
                scopingEvaluationServer = component "ScopingEvaluationServer" "Evaluates sync rule scoping filters" "C# Service"
                exportExecutionServer = component "ExportExecutionServer" "Executes pending exports with retry logic" "C# Service"
                schedulerServer = component "SchedulerServer" "Schedule management, due time evaluation, execution advancement, crash recovery" "C# Service"
                searchServer = component "SearchServer" "Metaverse search and query functionality" "C# Service"
                securityServer = component "SecurityServer" "Role-based access control and user-to-role assignments" "C# Service"
                changeHistoryServer = component "ChangeHistoryServer" "Change history and audit trail for metaverse objects" "C# Service"
                certificateServer = component "CertificateServer" "Certificate store management for TLS/SSL with external systems" "C# Service"
                serviceSettingsServer = component "ServiceSettingsServer" "Global service configuration management" "C# Service"
                activityServer = component "ActivityServer" "Activity logging, audit trail, execution statistics" "C# Service"
                taskingServer = component "TaskingServer" "Worker task queue management" "C# Service"
                repository = component "IJimRepository" "Data access abstraction - interfaces defined in JIM.Data, implemented by JIM.PostgresData (EF Core)" "Repository Interface"
            }

            worker = container "Worker Service" "Processes queued synchronisation tasks - import, sync, export operations" ".NET 9.0 Background Service" {
                workerHost = component "Worker Host" "Main processing loop, polls for tasks, manages execution" ".NET BackgroundService"
                importProcessor = component "SyncImportTaskProcessor" "Imports data from connectors into staging area (full and delta)" "C# Task Processor"
                fullSyncProcessor = component "SyncFullSyncTaskProcessor" "Full synchronisation - processes all CSOs, applies attribute flows, projects to metaverse" "C# Task Processor"
                deltaSyncProcessor = component "SyncDeltaSyncTaskProcessor" "Delta synchronisation - processes only CSOs modified since last sync" "C# Task Processor"
                exportProcessor = component "SyncExportTaskProcessor" "Exports pending changes to connected systems with preview mode and retry" "C# Task Processor"
            }

            connectors = container "Connectors" "External system integration adapters" "JIM.Connectors" {
                ldapConnector = component "LDAP Connector" "Active Directory, OpenLDAP, AD-LDS - schema discovery, LDAPS, partitions, delta import" "IConnector Implementation"
                fileConnector = component "File Connector" "CSV import/export, configurable delimiters, schema discovery" "IConnector Implementation"
                databaseConnector = component "Database Connector" "PostgreSQL, MySQL, Oracle, SQL Server - SQL queries, stored procedures" "IConnector Implementation" "Planned"
                scimConnector = component "SCIM 2.0 Connector" "Cloud application provisioning via SCIM protocol" "IConnector Implementation" "Planned"
            }

            scheduler = container "Scheduler Service" "Evaluates schedule due times, triggers synchronisation jobs, and recovers stuck executions" ".NET 9.0 Background Service" {
                schedulerHost = component "Scheduler Host" "Polling loop that evaluates due schedules, starts executions, and performs crash recovery" ".NET BackgroundService"
            }

            database = container "PostgreSQL Database" "Stores configuration, metaverse objects, staging area, sync rules, activity history, task queue" "PostgreSQL 18" "Database"

            pwsh = container "PowerShell Module" "Cross-platform module with 64 cmdlets for automation and scripting" "PowerShell 7" "Client Library"

            !docs docs
            !adrs adrs
        }

        # ===== System Context Relationships =====
        admin -> jim "Manages via" "Blazor Web UI, PowerShell"
        automation -> jim "Automates via" "REST API, PowerShell"
        idp -> jim "Authenticates" "OIDC/OpenID Connect"
        jim -> ad "Synchronises" "LDAP/LDAPS"
        jim -> hr "Imports from" "CSV"
        jim -> files "Imports/Exports" "CSV files"
        jim -> databases "Synchronises" "SQL" "Planned"
        jim -> scim "Provisions to" "SCIM 2.0" "Planned"

        # ===== Container Relationships =====
        admin -> jim.webApp "Uses" "HTTPS"
        admin -> jim.pwsh "Scripts with" "PowerShell cmdlets"
        automation -> jim.webApp "Calls" "REST API"
        automation -> jim.pwsh "Automates via" "PowerShell scripts"
        jim.pwsh -> jim.webApp "Calls" "REST API /api/v1/"
        idp -> jim.webApp "Authenticates" "OIDC"

        jim.webApp -> jim.appLayer "Uses" "Method calls"

        jim.appLayer -> jim.database "Reads/Writes" "EF Core"
        jim.appLayer -> jim.connectors "Invokes" "Method calls"

        jim.worker -> jim.appLayer "Uses" "Method calls"

        jim.scheduler -> jim.appLayer "Uses" "Method calls"

        jim.connectors -> ad "Connects to" "LDAP/LDAPS"
        jim.connectors -> hr "Imports from" "CSV"
        jim.connectors -> files "Reads/Writes" "File I/O"
        jim.connectors -> databases "Connects to" "SQL" "Planned"
        jim.connectors -> scim "Provisions to" "HTTPS" "Planned"

        # ===== Web Application Component Relationships =====
        admin -> jim.webApp.blazorPages "Uses" "HTTPS"
        automation -> jim.webApp.apiControllers "Calls" "REST/JSON"
        jim.pwsh -> jim.webApp.apiControllers "Calls" "REST/JSON"
        jim.webApp.authMiddleware -> idp "Authenticates via" "OIDC"
        jim.webApp.authMiddleware -> jim.webApp.blazorPages "Validates requests" "ASP.NET Pipeline"
        jim.webApp.authMiddleware -> jim.webApp.apiControllers "Validates requests" "ASP.NET Pipeline"
        jim.webApp.blazorPages -> jim.appLayer.jimApplication "Uses" "Method calls"
        jim.webApp.apiControllers -> jim.appLayer.jimApplication "Uses" "Method calls"

        # ===== Application Layer Component Relationships =====
        jim.appLayer.jimApplication -> jim.appLayer.metaverseServer "Delegates to" "Method calls"
        jim.appLayer.jimApplication -> jim.appLayer.connectedSystemServer "Delegates to" "Method calls"
        jim.appLayer.jimApplication -> jim.appLayer.objectMatchingServer "Delegates to" "Method calls"
        jim.appLayer.jimApplication -> jim.appLayer.exportEvaluationServer "Delegates to" "Method calls"
        jim.appLayer.jimApplication -> jim.appLayer.scopingEvaluationServer "Delegates to" "Method calls"
        jim.appLayer.jimApplication -> jim.appLayer.exportExecutionServer "Delegates to" "Method calls"
        jim.appLayer.jimApplication -> jim.appLayer.schedulerServer "Delegates to" "Method calls"
        jim.appLayer.jimApplication -> jim.appLayer.searchServer "Delegates to" "Method calls"
        jim.appLayer.jimApplication -> jim.appLayer.securityServer "Delegates to" "Method calls"
        jim.appLayer.jimApplication -> jim.appLayer.changeHistoryServer "Delegates to" "Method calls"
        jim.appLayer.jimApplication -> jim.appLayer.certificateServer "Delegates to" "Method calls"
        jim.appLayer.jimApplication -> jim.appLayer.serviceSettingsServer "Delegates to" "Method calls"
        jim.appLayer.jimApplication -> jim.appLayer.activityServer "Delegates to" "Method calls"
        jim.appLayer.jimApplication -> jim.appLayer.taskingServer "Delegates to" "Method calls"

        jim.appLayer.objectMatchingServer -> jim.appLayer.metaverseServer "Uses" "Method calls"
        jim.appLayer.exportEvaluationServer -> jim.appLayer.connectedSystemServer "Uses" "Method calls"
        jim.appLayer.exportExecutionServer -> jim.appLayer.metaverseServer "Uses" "Method calls"
        jim.appLayer.exportExecutionServer -> jim.appLayer.connectedSystemServer "Uses" "Method calls"

        jim.appLayer.metaverseServer -> jim.appLayer.repository "Uses" "IJimRepository"
        jim.appLayer.connectedSystemServer -> jim.appLayer.repository "Uses" "IJimRepository"
        jim.appLayer.schedulerServer -> jim.appLayer.repository "Uses" "IJimRepository"
        jim.appLayer.searchServer -> jim.appLayer.repository "Uses" "IJimRepository"
        jim.appLayer.securityServer -> jim.appLayer.repository "Uses" "IJimRepository"
        jim.appLayer.changeHistoryServer -> jim.appLayer.repository "Uses" "IJimRepository"
        jim.appLayer.certificateServer -> jim.appLayer.repository "Uses" "IJimRepository"
        jim.appLayer.serviceSettingsServer -> jim.appLayer.repository "Uses" "IJimRepository"
        jim.appLayer.activityServer -> jim.appLayer.repository "Uses" "IJimRepository"
        jim.appLayer.taskingServer -> jim.appLayer.repository "Uses" "IJimRepository"

        jim.appLayer.repository -> jim.database "Reads/Writes" "EF Core (JIM.PostgresData)"

        # ===== Worker Component Relationships =====
        jim.worker.workerHost -> jim.appLayer.jimApplication "Polls for tasks" "Method calls"
        jim.worker.workerHost -> jim.worker.importProcessor "Dispatches" "Method calls"
        jim.worker.workerHost -> jim.worker.fullSyncProcessor "Dispatches" "Method calls"
        jim.worker.workerHost -> jim.worker.deltaSyncProcessor "Dispatches" "Method calls"
        jim.worker.workerHost -> jim.worker.exportProcessor "Dispatches" "Method calls"

        jim.worker.importProcessor -> jim.appLayer.jimApplication "Uses" "Method calls"
        jim.worker.fullSyncProcessor -> jim.appLayer.jimApplication "Uses" "Method calls"
        jim.worker.deltaSyncProcessor -> jim.appLayer.jimApplication "Uses" "Method calls"
        jim.worker.exportProcessor -> jim.appLayer.jimApplication "Uses" "Method calls"

        # ===== Scheduler Component Relationships =====
        jim.scheduler.schedulerHost -> jim.appLayer.jimApplication "Evaluates schedules and creates tasks" "Method calls"

        # ===== Connector Component Relationships =====
        jim.connectors.ldapConnector -> ad "Connects to" "LDAP/LDAPS"
        jim.connectors.fileConnector -> files "Reads/Writes" "File I/O"
        jim.connectors.fileConnector -> hr "Imports from" "CSV"
        jim.connectors.databaseConnector -> databases "Connects to" "SQL" "Planned"
        jim.connectors.scimConnector -> scim "Provisions to" "HTTPS" "Planned"
    }

    views {
        # Level 1: System Context
        systemContext jim "SystemContext" "System Context diagram for JIM" {
            include *
            autoLayout tb 300 300
        }

        # Level 2: Container Diagram
        container jim "Containers" "Container diagram for JIM" {
            include *
            autoLayout tb 300 300
        }

        # Level 3: Component Diagrams

        component jim.webApp "WebAppComponents" "Component diagram for JIM Web Application" {
            include *
            autoLayout tb 300 300
        }

        component jim.appLayer "AppLayerComponents" "Component diagram for JIM Application Layer" {
            include *
            autoLayout tb 300 300
        }

        component jim.worker "WorkerComponents" "Component diagram for JIM Worker Service" {
            include *
            autoLayout tb 300 300
        }

        component jim.connectors "ConnectorComponents" "Component diagram for JIM Connectors" {
            include *
            autoLayout tb 500 300
        }

        component jim.scheduler "SchedulerComponents" "Component diagram for JIM Scheduler Service" {
            include *
            autoLayout tb 300 300
        }

        styles {
            element "Software System" {
                background #1168bd
                color #ffffff
                shape RoundedBox
            }
            element "External" {
                background #999999
                color #ffffff
                shape RoundedBox
            }
            element "Person" {
                background #08427b
                color #ffffff
                shape Person
            }
            element "Container" {
                background #438dd5
                color #ffffff
                shape RoundedBox
            }
            element "Web Browser" {
                shape WebBrowser
            }
            element "Database" {
                shape Cylinder
            }
            element "Component" {
                background #85bbf0
                color #000000
                shape RoundedBox
                width 550
            }
            element "Client Library" {
                shape RoundedBox
                background #5fa8d3
                color #ffffff
            }
            element "Planned" {
                background #e0e0e0
                color #666666
                border dashed
                opacity 50
                shape RoundedBox
            }
            relationship "Relationship" {
                dashed false
            }
            relationship "Planned" {
                dashed true
                color #999999
            }

            dark {
                element "Software System" {
                    background #2b8ee6
                    color #ffffff
                }
                element "External" {
                    background #777777
                    color #ffffff
                }
                element "Person" {
                    background #1a6fbf
                    color #ffffff
                }
                element "Container" {
                    background #5aa5eb
                    color #ffffff
                }
                element "Component" {
                    background #4a90d9
                    color #ffffff
                }
                element "Client Library" {
                    background #6dbce8
                    color #ffffff
                }
                element "Planned" {
                    background #333333
                    color #999999
                }
                relationship "Relationship" {
                    color #cccccc
                }
                relationship "Planned" {
                    color #666666
                }
            }
        }
    }

}
