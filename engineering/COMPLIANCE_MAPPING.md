# JIM Security Compliance Mapping

| | |
|---|---|
| **Version** | 1.2 |
| **Last Updated** | 2026-04-10 |
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

## Operational Considerations

### Upstream-only base image CVEs

JIM's container base images derive from Microsoft's `mcr.microsoft.com/dotnet/<runtime|aspnet|sdk>:10.0-noble` images. Vulnerability scanning (Trivy via the `scan-base-images` CI job) can correctly flag CVEs as "fixable upstream" during the gap between an Ubuntu security release and a Microsoft refresh of the `10.0-noble` digest. JIM cannot apply the fix directly in those cases; the fix lives in a layer JIM does not own.

The response procedure for this situation is documented in [`engineering/DEVELOPER_GUIDE.md`](DEVELOPER_GUIDE.md) under "When the scan-base-images gate blocks on an upstream-only CVE". The available options range from waiting for Microsoft's next rebuild (default), through targeted in-Dockerfile mitigations, to documented temporary gate downgrades. The choice is case-by-case based on CVE severity and timing rather than a pre-baked policy, because the right answer genuinely depends on the specific CVE.

This is a known and intentional limitation of digest-pinned base images. It is not a compliance gap: digest pinning, vulnerability scanning, and SBOM generation all operate correctly. The constraint is purely on remediation latency for one class of finding.

### Controls scale with team size

Tetron currently runs small, focused development teams on JIM. The security and compliance controls described in this document are designed to provide consistent baseline enforcement at any team size, with additional human-review layers that can be added as development capacity grows.

- **Branch protection ruleset ("Protect Main")**: all changes to `main` must land via a pull request, all required status checks must pass before merge, branches must be up to date with `main`, and all review comment threads must be resolved. Direct pushes and force-pushes are blocked. See [`DEVELOPER_GUIDE.md`](DEVELOPER_GUIDE.md) section 7 for the full ruleset specification.
- **Automated baseline review**: every pull request is reviewed by an automated code review job (`claude-review` in `.github/workflows/claude-code-review.yml`) before it can be merged, regardless of author. This is a required status check in the branch protection ruleset, providing a consistent independent review across all changes.
- **CI-enforced quality gates**: build and test success, CodeQL static analysis, container base image vulnerability scanning, and dependency scanning are all required status checks in the branch protection ruleset. These gates are machine-enforced (not advisory) and operate identically whether the team has one developer or many.
- **Signed commits**: all contributors sign their commits via the devcontainer's automated signing setup. The pre-commit hook at `.githooks/pre-commit` enforces this locally. Server-side enforcement via `required_signatures` in the branch protection ruleset is planned once all contributor environments are reliably producing signed commits.
- **Scalable human review**: additional reviewer requirements can be layered onto the branch protection ruleset as team composition supports them. The current configuration is designed to extend cleanly; no restructuring is needed to add reviewer requirements.

### Commit provenance and author attribution

All commits to JIM's `main` branch carry cryptographic signatures verifying the committer's identity. The signing setup is automated in `.devcontainer/setup.sh` (which delegates to `.devcontainer/configure-signing.sh`) and works in both GitHub Codespaces (via the built-in `gh-gpgsign` helper) and local devcontainers (via forwarded SSH agent keys). Contributors cannot commit without signing: the pre-commit hook at `.githooks/pre-commit` refuses unsigned commits with clear recovery instructions. See [`engineering/DEVELOPER_GUIDE.md`](DEVELOPER_GUIDE.md) under "Commit Signing" for the full setup and policy.

This directly supports NIST SP 800-53 SI-7 (Software, Firmware, and Information Integrity) and NCSC Secure Development Principle "Protect your code repository".

---

## NIST Cybersecurity Framework (CSF) 2.0 Mapping

### GOVERN (GV) - Establish and maintain cybersecurity governance

| CSF Control | JIM Feature / Practice | Status |
|-------------|------------------------|--------|
| GV.OC - Organisational Context | JIM designed for regulated environments; air-gapped deployment supported | Implemented |
| GV.RM - Risk Management Strategy | Threat modelling required for new features (CLAUDE.md) | Implemented |
| GV.SC - Supply Chain Risk Management | Dependency review, SBOM generation, pinned versions (CLAUDE.md). Production Docker base images are digest-pinned and the policy is enforced by CI (`.github/workflows/ci.yml` `discover-base-images` job), not just documented. | Implemented |

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
| 5 | Protect the build environment | GitHub Actions CI/CD with every action pinned by immutable 40-character commit SHA (not mutable version tags), preventing tag-rewrite supply chain attacks. Dependabot tracks SHA-pinned updates. All CI checks are required status checks in the branch protection ruleset, ensuring the pipeline cannot be bypassed. See DEVELOPER_GUIDE.md "GitHub Actions" and "Branch Protection Ruleset" sections. | Aligned |
| 6 | Secure the development tools and processes | Devcontainer with controlled toolchain, dependency pinning | Aligned |
| 7 | Manage and secure third-party components | Dependency review policy, SBOM generation. Container base image vulnerability scanning runs on every push and PR with results surfaced to GitHub code scanning (SARIF). Digest-pinning of production base images is enforced by CI rather than by convention. | Aligned |

### Theme 3: Deployment and Maintenance

| Principle | Requirement | JIM Alignment | Status |
|-----------|-------------|---------------|--------|
| 8 | Deploy securely | Docker containerisation, deployment best practices in SECURITY.md. **Planned**: pre-release integration test gate so no release can be cut unless the full integration test suite has passed (tracked in #518). | Aligned |
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
| Protect your code repository | GitHub with branch protection ruleset enforcing: required PRs, required status checks (build, test, CodeQL, vulnerability scanning, automated code review), up-to-date branches, conversation resolution, anti-deletion, and anti-force-push. No secrets in code. See DEVELOPER_GUIDE.md section 7. | Aligned |
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
| SI-3 | Malicious Code Protection | Container base image scanning (Trivy) on every push/PR with results in GitHub code scanning; dependency vulnerability checks via Dependabot |
| SI-7 | Software Integrity | SHA256 checksums on releases, signed commits |
| SI-10 | Information Input Validation | Input validation at all API boundaries, DTO annotations |

### SA - System and Services Acquisition

| Control | Description | JIM Implementation |
|---------|-------------|-------------------|
| SA-11 | Developer Testing and Evaluation | Mandatory build/test before commit, security test requirements. **Planned**: pre-release integration test gate enforcing that no release can be cut unless the full integration test suite has passed (tracked in #518). |
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
