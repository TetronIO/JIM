[![.NET Build & Test](https://github.com/TetronIO/JIM/actions/workflows/dotnet-build-and-test.yml/badge.svg)](https://github.com/TetronIO/JIM/actions/workflows/dotnet-build-and-test.yml)

# Junctional Identity Manager (JIM)
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

## Deployment
JIM runs in a Docker stack using containers and can be deployed to on-premises infrastructure (no Internet connection required for air-gapped networks), or Cloud container services, such as Microsoft Azure or AWS.

Various topologies are planned, depending on your needs:
- Standalone (single-server, built-in database): Perfect for smaller organisations or pre-production environments. The current topology.
- External database: Use an existing database platform for reslliancy and scale.
- Scaled-out web frontends: For organisations who need redundancy and/or to support a larger number of users accessing the web app.

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
1. Configure SSO with your IdP (see below).
1. Make sure Docker Desktop is installed and running.
1. Clone this repo.
1. Create a `.env` file in the repo root for your secrets (see below).
1. Run the Docker Compose configuration in your favourite IDE, configured for your platform (see below).

### Setup SSO
JIM uses SSO to authenticate and authorise users. Create an OIDC SSO configuration in your IdP for JIM using the [Code Authorisation Grant](https://oauth.net/2/grant-types/authorization-code/) flow. Keep a note of the authority URL, client id and secret for use in the `.env` file below.

> Note: JIM uses PKCE with this flow for improved security. Also, JIM has been tested with Microsoft Entra ID so far, but should work with all OIDC-compliant Identity Providers (IdPs).

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

### Configuring your IDE to start Docker Compose
- Visual Studio on Windows: Just press the play button, making sure the Docker project is selected.
- JetBrains Rider on Windows: As above.
- JetBrains Rider on macOS: Create a Run/Debug Configuration for macOS buy cloning the default one and changing the docker-compose.override.yml file for the docker-compose.override.macos.yml one. Play this one.
- JetBrains Rider on Linux: As above, but use docker-compose-override.linux.yml

<img width="1142" alt="jim-rider-docker-windows" src="https://github.com/user-attachments/assets/801ba32b-c436-4b76-87d4-00e73800da01"><br>
macOS/Linux Docker Setup: Clone the docker Compose configuration and name it for Windows.<br><br>


<img width="1151" alt="jim-rider-docker-macos" src="https://github.com/user-attachments/assets/81a295f1-080f-49e2-bc8f-35e0724b2e9b"><br>
macOS/Linux Docker Setup: With the new cloned configuration, name it for macOS and change the override file to the macOS one.<br><br>


<img width="590" alt="jim-rider-docker-play" src="https://github.com/user-attachments/assets/f15ef378-d88b-4a51-9b11-4f01529d7f77"><br>
macOS/Linux Docker Setup: Then change the active configuration and press the play button.

## State of Development
In JIM currently, you can setup connectors to LDAP-based systems (tested against Active Directory so far) and CSV files and perform imports. Synchronisation Rules can also be created, though synchronisation of objects (from connected systems to the Metaverse) is currently under development, with unit tests being worked on. Once that's complete, export functionality will be next, to target an Minimum Viable Product (MVP) status.

If you don't have any connected systems available, you can also use the Example Data feature to fill JIM with thousands of users and groups. The UI for users and groups is basic, with a fully customisable UI planned.

## More Information
Please go to https://tetron.io/jim for more information.
