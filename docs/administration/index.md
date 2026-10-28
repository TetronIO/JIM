---
title: Administration Guide
description: Deploy, configure, secure, upgrade, back up and troubleshoot JIM in production, on Docker or Podman.
type: landing
audience: administrator
---

# Administration Guide

These pages take JIM into production and keep it there: installing it on Docker or Podman, signing in through your identity provider, securing it, configuring it, and upgrading, backing up and troubleshooting it.

```jim-diagram
archetype: map
title: How the Administration pages connect
desc: "Deployment installs JIM, on Docker or on Podman (Running on Podman), and leads to TLS and Reverse Proxy for JIM's certificate and to Single Sign-On Setup for sign-in, whose settings are in the Configuration Reference; the Configuration Reference leads to Preview Features for the Preview flags. Security Headers and Security Audit Events come from the same security assessment. Upgrading and a Factory Reset both start with a backup from Backup & Disaster Recovery, the only way to undo a reset. Troubleshooting leads to Troubleshooting Synchronisation for runs, and uses Logs. Each box links to its page."
nodes:
- {id: sso, label: Single Sign-On Setup, link: sso-setup.md, row: 0, col: 0}
- {id: deploy, label: Deployment, link: deployment.md, row: 0, col: 1}
- {id: tls, label: TLS and Reverse Proxy, link: tls-and-reverse-proxy.md, row: 0, col: 2}
- {id: headers, label: Security Headers, link: security-headers.md, row: 1, col: 3}
- {id: config, label: Configuration Reference, link: configuration.md, row: 1, col: 0}
- {id: podman, label: Running on Podman, link: podman.md, row: 1, col: 1}
- {id: audit, label: Security Audit Events, link: security-audit-events.md, row: 1, col: 2}
- {id: preview, label: Preview Features, link: preview-features.md, row: 2, col: 0}
- {id: upgrade, label: Upgrading, link: upgrading.md, row: 2, col: 1}
- {id: backup, label: Backup & Recovery, link: backup-recovery.md, row: 2, col: 2}
- {id: reset, label: Factory Reset, link: factory-reset.md, row: 2, col: 3}
- {id: logs, label: Logs, link: logs.md, row: 3, col: 0}
- {id: trouble, label: Troubleshooting, link: troubleshooting.md, row: 3, col: 1}
- {id: syncfix, label: Troubleshooting Synchronisation, link: troubleshooting-synchronisation.md, row: 3, col: 2}
edges:
- {from: deploy, to: sso, label: sign-in}
- {from: deploy, to: tls, label: certificate}
- {from: deploy, to: podman, label: on Podman}
- {from: audit, to: headers, label: see also}
- {from: sso, to: config, label: settings in}
- {from: config, to: preview, label: flags in}
- {from: upgrade, to: backup, label: back up first}
- {from: reset, to: backup, label: back up first}
- {from: trouble, to: syncfix, label: for runs}
- {from: trouble, to: logs, label: uses}
```

<div class="grid cards" markdown>

-   **[Working with Lists](portal-lists.md)**

    ---

    How every list in the portal behaves: continuous scrolling instead of paging, object counts, search and sorting across the whole list, and links that open where you left off.

-   **[Deployment](deployment.md)**

    ---

    Installing JIM for production on Docker or Podman, connected or air-gapped: the architecture, the installer, health monitoring, and the production readiness checklist.

-   **[TLS and Reverse Proxy](tls-and-reverse-proxy.md)**

    ---

    JIM's certificate, from the installer or your own certificate authority, renewing it, and running JIM behind a reverse proxy.

-   **[Running on Podman](podman.md)**

    ---

    JIM on Podman, the container engine Red Hat supports on RHEL: how it runs under systemd, rootful or rootless, day-to-day operation, and firewall and SELinux.

-   **[Installing on Podman by Hand](podman-by-hand.md)**

    ---

    What the installer does on Podman, step by step, for a policy that requires every step by hand.

-   **[Deploying with Ansible](deploying-with-ansible.md)**

    ---

    Deploying JIM across RHEL servers with Red Hat's podman system role, from the same files the installer uses.

-   **[Single Sign-On Setup](sso-setup.md)**

    ---

    Step-by-step instructions for configuring OIDC authentication with Microsoft Entra ID, AD FS, or Keycloak, including testing procedures and security considerations.

-   **[Security Headers](security-headers.md)**

    ---

    The defence-in-depth response headers JIM sends on every request, including the Content Security Policy, and what to know if you sit JIM behind a reverse proxy or corporate web proxy.

-   **[Security Audit Events](security-audit-events.md)**

    ---

    The authentication activity JIM records in its Activity log: interactive sign-in success and failure, and failed API key or SSO access token authentication, with its retention and SIEM integration.

-   **[Preview Features](preview-features.md)**

    ---

    What a Preview feature is, where to turn one on, and what to expect from a feature that is still being rolled out.

-   **[Configuration Reference](configuration.md)**

    ---

    Complete environment variable reference organised by category: database, authentication, logging, user identity mapping, and performance tuning.

-   **[Upgrading](upgrading.md)**

    ---

    Procedures for upgrading JIM in connected and air-gapped environments, including pre-upgrade backups, pausing Schedules, verification, and rolling back.

-   **[Upgrading on Podman](upgrading-podman.md)**

    ---

    Upgrading JIM on Podman, rootful or rootless, connected or air-gapped, and rolling it back to the previous pod files.

-   **[Backup & Disaster Recovery](backup-recovery.md)**

    ---

    What to back up (database plus encryption keys, as a matched pair), how to take and restore a backup, securing key backups, and recovery expectations if keys are lost.

-   **[Factory Reset](factory-reset.md)**

    ---

    Wiping JIM back to a fresh installation: what goes, what survives, and how to keep administrative access.

-   **[Logs](logs.md)**

    ---

    Reading and searching the logs of JIM's services from the portal, and how long each service keeps them.

-   **[Troubleshooting](troubleshooting.md)**

    ---

    An installation that will not start, serve HTTPS or let you sign in, by the message it shows, with its cause and fix.

-   **[Troubleshooting Synchronisation](troubleshooting-synchronisation.md)**

    ---

    What each synchronisation error and Activity message means, how to fix and check it, and what JIM keeps safe in the meantime.

-   **[Troubleshooting Podman](troubleshooting-podman.md)**

    ---

    A Podman installation that cannot be seen, started, reached or kept running, by the message it shows.

</div>
