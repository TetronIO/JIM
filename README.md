[![.NET Build & Test](https://github.com/TetronIO/JIM/actions/workflows/dotnet-build-and-test.yml/badge.svg)](https://github.com/TetronIO/JIM/actions/workflows/dotnet-build-and-test.yml)

# Junctional Identity Manager (JIM)
A modern Identity Manager for organisations. Features:

- Synchronises objects between systems. Supports Users and Groups by default
- Supports custom object types, i.e. Departments, Qualifications, Courses, Licenses, Roles, Computers, etc.
- Transform data using a wide range of functions
- Extensible with custom functions
- Extensible with custom JIM connectors (fully unit-testable)
- A modern Web Portal and API
- Single Sign-On (SSO) using OpenID Connect

![A screenshot of JIM running](https://tetron.io/images/jim/jim-8.png "JIM Screenshot")

## Scenarios
JIM is designed to support the following common IGA scenarios:
- Synchronise users from HCM (aka HR) systems to directories and apps
- Synchronise attributes back to HCM systems, i.e. email address, telephone numbers, etc.
- Centrally manage user entitlements, i.e. group memberships in directories and apps
- Perform domain consolidations, i.e. to prepare for migrating to the cloud, simplification, or for organisational mergers
- Perform domain migrations, i.e. to facilitate divestitures
- Identity fusing - bring together user/entitlement data from various business apps and systems

## Deployment
JIM runs in Docker containers and can be deployed onto on-premises infrastructure (no Internet connection required for air-gapped networks) or Cloud container services, such as Microsoft Azure or AWS.

Various topologies are planned, depending on your needs:
- Standalone (single-server, built-in database) - Perfect for smaller organisations or pre-production environments. The current topology.
- External database - Use an existing database platform for reslliancy and scale
- Scaled-out web frontends - To handle more users accessing the web app and for high-availability

## Connectors
JIM is currently targetting the following means of connecting to systems via it's built-in Connectors. More are anticipated, though people will also be able to develop their own custom Connectors for use with JIM to support bespoke scenarios.
- LDAP (incl. Active Directory & AD-LDS)
- Microsoft SQL Server Database
- PostgreSQL Database
- MySQL Database
- Oracle Database
- CSV/Text files
- PowerShell (Core)
- SCIM 2.0

## Roadmap
JIM is in active development. There are many plans for new features. Check back soon for more details.

## Getting Started
To run JIM locally:
1. Clone the repo
1. Create a `.env` file in the repo root (see example below)
1. Run Docker Compose in your favourite IDE, configured for your platform (see examples below)

### `.env` Entra ID Example:
For federating JIM with Entra ID. Replace `<...>` elements with your real values.
```
DB_NAME=jim
DB_USERNAME=jim
DB_PASSWORD=password
DB_LOG_SENSITIVE_INFO=true
SSO_AUTHORITY=<your IDP URL, i.e. https://login.microsoftonline.com/f9953c7e-b69b-4cb1-ad60-b11df84f8af2>
SSO_CLIENT_ID=<your client id, i.e. 24d89e93-353e-45d6-9528-cc2dd2529dad>
SSO_SECRET=<your client secret, i.e. abcd1234>
SSO_UNIQUE_IDENTIFIER_CLAIM_TYPE=http://schemas.microsoft.com/identity/claims/objectidentifier
SSO_UNIQUE_IDENTIFIER_METAVERSE_ATTRIBUTE_NAME=Object Identifier
SSO_UNIQUE_IDENTIFIER_INITIAL_ADMIN_CLAIM_VALUE=<your user object identifier, i.e. 1a2e0377-e36c-4388-b185-c489ae7daa6a>
```

Note, the `SSO_UNIQUE_IDENTIFIER_INITIAL_ADMIN_CLAIM_VALUE` variable enables you to sign in to JIM as the initial admin.

### Configuring your IDE to start Docker Compose
Todo...

## More Information
Please go to https://tetron.io/jim for more information.
