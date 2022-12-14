# Junctional Identity Manager (JIM)
A modern Identity Manager for organisations. Features:

- Synchronises objects between systems. Supports Users and Groups by default
- Supports custom object types, i.e. Departments, Qualifications, Courses, Licenses, Roles, Computers, etc.
- Transform data using a wide range of functions
- Extensible with custom functions
- Extensible with custom JIM connectors (fully unit-testable)
- A modern Web Portal and API
- Single Sign-On (SSO) using OpenID Connect

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

Various topologies supported, depending on your needs:
- Standalone (single-server, built-in database) - Perfect for smaller organisations or pre-production environments
- External database - Use an existing database platform for reslliancy and scale
- Scaled-out web frontends - To handle more users accessing the web app and for high-availability

## Connectors
JIM is currently targetting the following means of connecting to systems via it's built-in Connectors. More are anticipated, though people will also be able to develop their own custom Connectors for use with JIM to support bespoke scenarios.
- LDAP (incl. Active Directory)
- SQL Server Database
- PostgreSQL Database
- MySQL Database
- Oracle Database
- CSV
- PowerShell
- SCIM 2.0

## Roadmap
JIM is in active development. There are many plans for new features. Check back soon for more details.
