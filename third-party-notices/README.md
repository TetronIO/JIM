# Third-Party Notices

JIM's container images carry the third-party components listed here under their own licences. Every
component's licence text sits beside this file, in the repository and at `/app/third-party-notices/`
inside each image, so a distribution of JIM always carries the terms it is bound by. JIM itself is
licensed separately; see [LICENSE](../LICENSE) and https://junctional.io/license.

Approval and pinning of every dependency follows `engineering/DEPENDENCY_PINNING.md`.

| Component | Version | Used for | Licence | Text |
|-----------|---------|----------|---------|------|
| Oracle Data Provider for .NET, Managed Driver (`Oracle.ManagedDataAccess.Core`) | 23.26.300 | The JIM SQL Connector's Oracle Database dialect | Oracle Free Distribution, Hosting, and Use Terms and Conditions | [Oracle.ManagedDataAccess.Core-LICENSE.txt](Oracle.ManagedDataAccess.Core-LICENSE.txt) |

## Oracle Data Provider for .NET, Managed Driver

The Oracle Free Distribution, Hosting, and Use Terms permit the driver to be redistributed unmodified,
used in your business operations and hosted for third parties at no charge, on conditions that include
a copy of the terms accompanying any distribution, no additional fees being charged for the driver
itself, and Oracle's proprietary notices being left in place. JIM ships the driver unmodified and
charges nothing for it. If your organisation has received the driver under its own licence agreement
with Oracle, that agreement governs your use of it instead. Read the terms; nothing here is legal advice.

## Everything else

The remaining NuGet packages, base container images and operating system packages that make up the
images are under permissive licences (MIT, Apache-2.0, BSD and the like) whose notices are carried
inside the packages themselves. `engineering/DEPENDENCY_PINNING.md` records how each layer is pinned
and reviewed.
