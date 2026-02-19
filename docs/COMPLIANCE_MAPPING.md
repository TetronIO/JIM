# JIM Security Compliance Mapping

| | |
|---|---|
| **Version** | 1.0 |
| **Last Updated** | 2026-02-16 |
| **Status** | Active |

---

## Purpose

This document helps customers understand how JIM aligns with international security frameworks and standards. Use this mapping to evaluate JIM's security features against your organisation's regulatory requirements, whether you're subject to government procurement rules, financial services regulations, healthcare standards, critical infrastructure guidance, or data protection legislation.

The frameworks and standards covered include those commonly required by:
- UK Government and Defence procurement
- US Federal and Defence procurement
- EU regulatory requirements
- Australian and New Zealand Government procurement
- Healthcare organisations
- Financial Services
- Critical National Infrastructure

---

## Standards Alignment Summary

| Standard / Framework | Region | Sector | JIM Alignment Status |
|----------------------|--------|--------|----------------------|
| ISO 27001 | International | All | Aligned - ISMS practices followed, security management embedded in development lifecycle |
| NCSC Secure Development Principles | UK | All | Aligned - embedded in CLAUDE.md |
| UK Software Security Code of Practice | UK | All | Aligned - 14 principles mapped below |
| CISA Secure by Design | US/International | All | Aligned - embedded in CLAUDE.md |
| Cyber Essentials / CE Plus | UK | Government | Supported - deployment guidance addresses required controls |
| OWASP ASVS v4.0 | International | All | Aligned - development guidelines reference |
| NIST SP 800-53 Rev 5 | US | Federal | Mapped - see control families below |
| NIST SP 800-171 Rev 2 | US | Defence (CUI) | Aligned - relevant controls addressed |
| NIST CSF 2.0 | US/International | All | Mapped - see functions below |
| ASD Essential Eight | Australia | Government/CNI | Supported - application-level controls aligned |
| NZISM | New Zealand | Government | Supported - application-level controls aligned |
| NIS2 Directive | EU | Essential/Important Entities | Aligned - security measures implemented |
| GDPR | EU/UK | All (personal data) | Aligned - data protection by design |

---

## NIST Cybersecurity Framework (CSF) 2.0 Mapping

### GOVERN (GV) - Establish and maintain cybersecurity governance

| CSF Control | JIM Feature / Practice | Status |
|-------------|------------------------|--------|
| GV.OC - Organisational Context | JIM designed for regulated environments; air-gapped deployment supported | Implemented |
| GV.RM - Risk Management Strategy | Threat modelling required for new features (CLAUDE.md) | Implemented |
| GV.SC - Supply Chain Risk Management | Dependency review, SBOM generation, pinned versions (CLAUDE.md) | Implemented |

### IDENTIFY (ID) - Understand cybersecurity risks

| CSF Control | JIM Feature / Practice | Status |
|-------------|------------------------|--------|
| ID.AM - Asset Management | Metaverse provides central identity inventory | Implemented |
| ID.RA - Risk Assessment | Threat assessment required for security-sensitive features | Implemented |

### PROTECT (PR) - Safeguard against cybersecurity risks

| CSF Control | JIM Feature / Practice | Status |
|-------------|------------------------|--------|
| PR.AA - Identity Management, Authentication, Access Control | SSO/OIDC mandatory, RBAC, API key auth, JWT Bearer | Implemented |
| PR.AT - Awareness and Training | Security development guidelines in CLAUDE.md | Implemented |
| PR.DS - Data Security | AES-256-GCM encryption at rest, TLS in transit, parameterised queries | Implemented |
| PR.PS - Platform Security | Docker containerisation, minimal base images, non-root execution | Implemented |
| PR.IR - Technology Infrastructure Resilience | Air-gapped deployment, no cloud dependencies | Implemented |

### DETECT (DE) - Find cybersecurity attacks and compromises

| CSF Control | JIM Feature / Practice | Status |
|-------------|------------------------|--------|
| DE.CM - Continuous Monitoring | Activity logging, structured logging via Serilog, OpenTelemetry-ready | Implemented |
| DE.AE - Adverse Event Analysis | Activity/RPEI error tracking, UnhandledError detection | Implemented |

### RESPOND (RS) - Act regarding detected cybersecurity incidents

| CSF Control | JIM Feature / Practice | Status |
|-------------|------------------------|--------|
| RS.MA - Incident Management | Vulnerability disclosure policy (SECURITY.md), 48hr acknowledgement SLA | Implemented |
| RS.AN - Incident Analysis | Structured logging with correlation IDs, diagnostic spans | Implemented |

### RECOVER (RC) - Restore operations after incidents

| CSF Control | JIM Feature / Practice | Status |
|-------------|------------------------|--------|
| RC.RP - Incident Recovery Plan Execution | Sync operations are idempotent; re-run to recover state | Implemented |
| RC.CO - Recovery Communication | SECURITY.md defines coordinated disclosure process | Implemented |

---

## UK Software Security Code of Practice (May 2025) Mapping

The UK Government's Software Security Code of Practice defines 14 principles across 4 themes.

### Theme 1: Secure Design and Development

| Principle | Requirement | JIM Alignment | Status |
|-----------|-------------|---------------|--------|
| 1 | Ensure senior management commitment to software security | Security requirements take precedence over feature velocity (CLAUDE.md) | Aligned |
| 2 | Carry out risk assessments for software | Threat modelling required for security-sensitive features | Aligned |
| 3 | Adopt secure development practices | OWASP ASVS, NCSC Secure Development Principles embedded in CLAUDE.md | Aligned |
| 4 | Ensure secure by default configuration | SSO mandatory, encryption enabled, no default credentials | Aligned |

### Theme 2: Build Environment Security

| Principle | Requirement | JIM Alignment | Status |
|-----------|-------------|---------------|--------|
| 5 | Protect the build environment | GitHub Actions CI/CD, pinned action versions | Aligned |
| 6 | Secure the development tools and processes | Devcontainer with controlled toolchain, dependency pinning | Aligned |
| 7 | Manage and secure third-party components | Dependency review policy, SBOM generation, vulnerability scanning | Aligned |

### Theme 3: Deployment and Maintenance

| Principle | Requirement | JIM Alignment | Status |
|-----------|-------------|---------------|--------|
| 8 | Deploy securely | Docker containerisation, deployment best practices in SECURITY.md | Aligned |
| 9 | Provide timely security updates | SECURITY.md defines 30-day critical vulnerability resolution SLA | Aligned |
| 10 | Manage end of life securely | Only latest version supported, clear update guidance | Aligned |

### Theme 4: Communication and Vulnerability Management

| Principle | Requirement | JIM Alignment | Status |
|-----------|-------------|---------------|--------|
| 11 | Report vulnerabilities responsibly | SECURITY.md with dedicated security@tetron.io email | Aligned |
| 12 | Provide vulnerability information to customers | Coordinated disclosure, security advisories | Aligned |
| 13 | Publish accurate CVE information | In progress - GitHub Security Advisories integration | Aligned |
| 14 | Provide security documentation | SECURITY.md, deployment guidance, this compliance mapping | Aligned |

---

## NCSC Secure Development and Deployment Principles Mapping

| NCSC Principle | JIM Alignment | Status |
|----------------|---------------|--------|
| Secure development is everyone's concern | Security guidelines in CLAUDE.md for all developers | Aligned |
| Keep your security knowledge sharp | OWASP Top 10 awareness documented, security testing requirements | Aligned |
| Produce clean and maintainable code | Code style conventions, one class per file, async patterns | Aligned |
| Secure your development environment | Devcontainer with controlled toolchain, secrets via .env (gitignored) | Aligned |
| Protect your code repository | GitHub with branch protection, no secrets in code | Aligned |
| Secure the build and deployment pipeline | GitHub Actions CI/CD, SHA256 checksums on releases | Aligned |
| Continually test your security | Security test requirements in CLAUDE.md, input validation tests | Aligned |
| Plan for security flaws | SECURITY.md vulnerability disclosure, fast/hard failure philosophy | Aligned |

---

## NIST SP 800-53 Rev 5 - Relevant Control Families

This maps JIM's features to the NIST SP 800-53 control families most relevant to an identity management application.

### AC - Access Control

| Control | Description | JIM Implementation |
|---------|-------------|-------------------|
| AC-2 | Account Management | Metaverse identity lifecycle (join, provision, deprovision) |
| AC-3 | Access Enforcement | RBAC via claims-based authorisation |
| AC-6 | Least Privilege | Role-based access, API scopes |
| AC-7 | Unsuccessful Logon Attempts | Delegated to OIDC identity provider |
| AC-14 | Permitted Actions Without Identification | No unauthenticated access permitted |
| AC-17 | Remote Access | OIDC/SSO with PKCE for all remote access |

### AU - Audit and Accountability

| Control | Description | JIM Implementation |
|---------|-------------|-------------------|
| AU-2 | Event Logging | Activity logging for all sync operations and admin actions |
| AU-3 | Content of Audit Records | Structured logging with Serilog (timestamp, user, action, outcome) |
| AU-6 | Audit Record Review | Activity history UI, API for programmatic access |
| AU-12 | Audit Record Generation | Automatic logging via Serilog, Activity/RPEI system |

### IA - Identification and Authentication

| Control | Description | JIM Implementation |
|---------|-------------|-------------------|
| IA-2 | Identification and Authentication (Users) | SSO/OIDC mandatory with PKCE |
| IA-5 | Authenticator Management | API keys: cryptographically random; credentials: AES-256-GCM encrypted |
| IA-8 | Identification and Authentication (Non-Org Users) | JWT Bearer tokens for API access |

### SC - System and Communications Protection

| Control | Description | JIM Implementation |
|---------|-------------|-------------------|
| SC-8 | Transmission Confidentiality | TLS enforced for all communications |
| SC-12 | Cryptographic Key Management | ASP.NET Core Data Protection API, automatic key rotation |
| SC-13 | Cryptographic Protection | AES-256-GCM (NIST approved), HMAC-SHA256 |
| SC-28 | Protection of Information at Rest | Credential encryption via Data Protection API |

### SI - System and Information Integrity

| Control | Description | JIM Implementation |
|---------|-------------|-------------------|
| SI-2 | Flaw Remediation | Vulnerability disclosure policy, dependency scanning |
| SI-3 | Malicious Code Protection | Container scanning, dependency vulnerability checks |
| SI-7 | Software Integrity | SHA256 checksums on releases, signed commits |
| SI-10 | Information Input Validation | Input validation at all API boundaries, DTO annotations |

### SA - System and Services Acquisition

| Control | Description | JIM Implementation |
|---------|-------------|-------------------|
| SA-11 | Developer Testing and Evaluation | Mandatory build/test before commit, security test requirements |
| SA-15 | Development Process | Secure SDLC documented in CLAUDE.md |
| SA-22 | Unsupported System Components | Dependency management, outdated package monitoring |

---

## CISA Secure by Design Principles Mapping

| CISA Principle | JIM Alignment | Status |
|----------------|---------------|--------|
| Take ownership of customer security outcomes | Secure defaults, encryption by default, SSO mandatory | Aligned |
| Embrace radical transparency and accountability | SECURITY.md, vulnerability disclosure, CVE publishing via GitHub Security Advisories | Aligned |
| Build organisational structure so secure by design is a top priority | Security requirements in CLAUDE.md take precedence over velocity | Aligned |
| Eliminate default passwords | No default credentials; SSO/OIDC required | Aligned |
| Conduct field testing | Integration testing framework with real connected systems | Aligned |
| Drive adoption of MFA | OIDC with PKCE (MFA delegated to identity provider) | Aligned |
| Reduce attack surface | Minimal container images, no unnecessary services, air-gapped support | Aligned |
| Evidence of intrusion detection | Activity logging, RPEI error tracking, structured logging | Aligned |

---

## ASD Essential Eight Mapping (Australia)

| Essential Eight Control | JIM Application-Level Alignment | Status |
|-------------------------|--------------------------------|--------|
| Application Control | Not directly applicable (infrastructure control) | N/A |
| Patch Applications | Dependency management, vulnerability scanning | Aligned |
| Configure Microsoft Office Macro Settings | Not applicable | N/A |
| User Application Hardening | Blazor CSP headers, XSS protection | Supported |
| Restrict Administrative Privileges | RBAC, claims-based authorisation, least privilege | Aligned |
| Patch Operating Systems | Docker base image updates, .NET runtime updates | Aligned |
| Multi-Factor Authentication | SSO/OIDC with PKCE (MFA via identity provider) | Aligned |
| Regular Backups | Database backup guidance in deployment docs | Aligned |

---

## Cyber Essentials (UK) Mapping

| CE Control | JIM Alignment | Status |
|------------|---------------|--------|
| Firewalls | Deployment guidance recommends network isolation | Aligned |
| Secure Configuration | Secure defaults, no unnecessary services | Aligned |
| User Access Control | SSO/OIDC, RBAC, API key authentication | Aligned |
| Malware Protection | Container scanning, dependency vulnerability checks | Aligned |
| Patch Management | Dependency updates, .NET runtime updates | Aligned |

---

## NHS DSPT v8 (UK Healthcare) Mapping

| DSPT Assertion | JIM Alignment | Status |
|----------------|---------------|--------|
| 4.5.6 - Identity federation support | SSO/OIDC with any compliant identity provider (including NHS CIS, NHSmail) | Aligned |
| 4.5.6 - MFA support | OIDC with PKCE (MFA delegated to identity provider) | Aligned |
| Incident notification | Aligned - configurable incident response SLAs to meet sector requirements | Aligned |
| Software Security Code of Practice | Mapped above - 14 principles aligned | Aligned |
| Audit logging | Activity logging for all operations | Aligned |

---

## NIS2 Directive (EU) Mapping

| NIS2 Requirement | JIM Alignment | Status |
|------------------|---------------|--------|
| Risk analysis and information system security | Threat modelling, secure development practices | Aligned |
| Incident handling | SECURITY.md vulnerability disclosure, activity logging | Aligned |
| Business continuity and crisis management | Air-gapped deployment, idempotent sync operations | Aligned |
| Supply chain security | Dependency management, SBOM, vulnerability scanning | Aligned |
| Security in network and information systems acquisition | Secure SDLC, security testing requirements | Aligned |
| Policies and procedures for cryptography and encryption | AES-256-GCM, TLS, NIST-approved algorithms | Aligned |
| Use of MFA or continuous authentication | SSO/OIDC with PKCE | Aligned |

---

## GDPR / UK Data Protection Act 2018 Mapping

| Requirement | JIM Alignment | Status |
|-------------|---------------|--------|
| Data protection by design (Article 25) | Encryption at rest, access controls, audit logging | Aligned |
| Data protection by default (Article 25) | Secure defaults, minimal data exposure | Aligned |
| Security of processing (Article 32) | AES-256-GCM encryption, access controls, pseudonymisation support | Aligned |
| Records of processing activities (Article 30) | Activity logging, sync operation audit trail | Aligned |
| Data portability (Article 20) | API access to metaverse data, export capabilities | Aligned |

---

## References

- [NCSC Secure Development and Deployment Guidance](https://www.ncsc.gov.uk/collection/developers-collection)
- [UK Software Security Code of Practice](https://www.gov.uk/government/publications/software-security-code-of-practice)
- [CISA Secure by Design](https://www.cisa.gov/resources-tools/resources/secure-by-design)
- [OWASP Application Security Verification Standard](https://owasp.org/www-project-application-security-verification-standard/)
- [NIST SP 800-53 Rev 5](https://csrc.nist.gov/publications/detail/sp/800-53/rev-5/final)
- [NIST Cybersecurity Framework 2.0](https://www.nist.gov/cyberframework)
- [ASD Essential Eight](https://www.cyber.gov.au/resources-business-and-government/essential-cyber-security/essential-eight)
- [NIS2 Directive](https://digital-strategy.ec.europa.eu/en/policies/nis2-directive)
- [UK Cyber Essentials](https://www.ncsc.gov.uk/cyberessentials/overview)
- [NHS DSPT](https://www.dsptoolkit.nhs.uk/)
- [NZISM](https://nzism.gcsb.govt.nz/ism-document)
