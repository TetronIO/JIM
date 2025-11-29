# Junctional Identity Manager (JIM)

[![.NET Build & Test](https://github.com/TetronIO/JIM/actions/workflows/dotnet-build-and-test.yml/badge.svg?branch=main)](https://github.com/TetronIO/JIM/actions/workflows/dotnet-build-and-test.yml)

JIM is a modern Identity Management system designed for organisations with non-trivial Identity Management and synchronisation requirements.
It's designed to be self-hosted, deployable on container platforms and is suitable for connected, or air-gapped networks. Features include:

- Synchronises objects between systems. Supports Users and Groups by default
- Supports custom object types, i.e. Departments, Qualifications, Courses, Licenses, Roles, Computers, etc.
- Transform data using a wide range of functions
- Extensible with custom functions
- Extensible with custom connectors (fully unit-testable)
- A modern Web Portal and API
- Single Sign-On (SSO) using OpenID Connect

![A screenshot of JIM running](https://tetron.io/images/jim/jim-8.png "JIM Screenshot")

## Scenarios
JIM is designed to support the following common Identity, Governance & Administration (IGA) scenarios:

- Automate JML by synchronising users from HR systems to directories, apps and systems
- Keep HR systems up to date by writing I.T related attributes back to HR systems, i.e. email address, telephone numbers, etc.
- Centrally manage user entitlements, i.e. group memberships in directories, apps and systems
- Facilitate domain consolidations, i.e. to prepare for migrating to the cloud, simplification, or for organisational mergers
- Facilitate domain migrations, i.e. divestitures
- Identity fusing - bring together user/entitlement data from various business apps and systems
## Benefits
Why choose JIM?

- It's modern. No legacy hosting requirements or janky old UIs
- Supports SSO to comply with modern security requirements
- Open Source. You can see exactly what it does and help improve it
- Flexible. We're developing it now, so you can suggest your must-have features
- Built by people with decades of experience of integrating IDAM systems into the real world
## Architecture
JIM is a container-based distributed application. It is comprised of:

- **JIM.Web** - A website, built using ﻿[﻿ASP.NET](https://asp.net/) Blazor Server.
- **JIM.API** - ﻿﻿A web API, built using ﻿[﻿ASP.NET](https://asp.net/) Web API
- **JIM.Scheduler** - A console app, built using .NET
- **JIM.Worker** - A console app, built using .NET
- A database - PostgreSQL
- A database admin website - Adminer
  
## Dependencies:
- A container host, i.e. Docker.
- An OpenID Connect (OIDC) identity provider, i.e. Entra ID, Keycloak, etc.

## Deployment
JIM runs in a Docker stack using containers and can be deployed to on-premises infrastructure (no Internet connection required for air-gapped networks), or Cloud container services, such as Microsoft Azure or AWS.

Various topologies are planned, depending on your needs:
- **Standalone (single-server, built-in database):** Perfect for smaller organisations or pre-production environments. The current topology.
- **External database:** Use an existing database platform for resiliency and scale.
- **Scaled-out web frontends:** For organisations who need redundancy and/or to support a larger number of users accessing the web app.

## Connectors
JIM is currently targeting the following means of connecting to systems via it's built-in Connectors. More are anticipated, though people will also be able to develop their own custom Connectors for use with JIM to support bespoke scenarios.

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

JIM uses GitHub Codespaces to provide a fully configured development environment in the cloud with all dependencies pre-installed (.NET 9.0, Docker, PostgreSQL).

**Quick Start:**

1. Click the **Code** button on the GitHub repository
2. Select **Codespaces** > **Create codespace on main**
3. Wait for the environment to provision (includes automatic `.env` creation)
4. Update the `.env` file with your SSO settings (see below)
5. Use the pre-configured shell aliases:
   - `jim-db` - Start PostgreSQL database
   - `jim-web` - Run Web UI locally (press F5 in VS Code)
   - `jim-api` - Run API locally
   - `jim-stack` - Start full Docker stack
   - `jim-migrate` - Apply database migrations

For local development instructions and advanced setup, see the [Developer Guide](docs/DEVELOPER_GUIDE.md).
   
### Setup SSO
JIM uses SSO to authenticate and authorise users. Create an OIDC SSO configuration in your IdP for JIM using the [﻿Code Authorisation Grant](https://oauth.net/2/grant-types/authorization-code/) flow. Keep a note of the authority URL, client id and secret for use in the `.env` file below.

>  Note: JIM uses PKCE for improved security. Also, JIM has been tested with Microsoft Entra ID to date, but should work with all OIDC-compliant Identity Providers (IdPs). 

Currently there can only be a single administrator, the one you setup in your `.env` file below. Later releases will include a full RBAC model. All other users accessing JIM will be standard users with no privileges.

### `.env` Entra ID Example:
Replace `<...>` elements with your real values. Suggested values are for Entra ID.

```
DB_NAME=jim
DB_USERNAME=jim
DB_PASSWORD=password
DB_LOG_SENSITIVE_INFO=true
SSO_AUTHORITY=<your IDP URL, i.e. https://login.microsoftonline.com/f9953c7e-b69b-4cb1-ad60-b11df84f8af2>
SSO_CLIENT_ID=<your client id, i.e. 24d89e93-353e-45d6-9528-cc2dd2529dad>
SSO_SECRET=<your client secret, i.e. abcd1234>
SSO_UNIQUE_IDENTIFIER_CLAIM_TYPE=<the unique identifier claim from your IdP, i.e. http://schemas.microsoft.com/identity/claims/objectidentifier for Entra ID>
SSO_UNIQUE_IDENTIFIER_METAVERSE_ATTRIBUTE_NAME=<the JIM Metaverse attribute the token unique identifier claim type maps to, i.e. Object Identifier>
SSO_UNIQUE_IDENTIFIER_INITIAL_ADMIN_CLAIM_VALUE=<your user object identifier, i.e. 1a2e0377-e36c-4388-b185-c489ae7daa6a>
```
Note, the `SSO_UNIQUE_IDENTIFIER_INITIAL_ADMIN_CLAIM_VALUE` variable enables you to sign in to JIM as the initial admin.

## State of Development
In JIM currently, you can setup connectors to LDAP-based systems (tested against Active Directory so far) and CSV files and perform imports. Synchronisation Rules can also be created, though synchronisation of objects (from connected systems to the Metaverse) is currently under development, with unit tests being worked on. Once that's complete, export functionality will be next, to target an Minimum Viable Product (MVP) status.

If you don't have any connected systems available, you can also use the Example Data feature to fill JIM with thousands of users and groups. The UI for users and groups is basic, with a fully customisable UI planned.

## Licensing
JIM uses a Source-Available model where it is free to use in non-production scenarios, but requires a commercial license for use in production scenarios. [﻿Full details can be found here](https://tetron.io/jim/#licensing).

## More Information
Please go to [﻿https://tetron.io/jim](https://tetron.io/jim) for more information.
