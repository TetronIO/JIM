# Security Policy

## Supported Versions

| Version | Supported          |
| ------- | ------------------ |
| latest  | :white_check_mark: |

Only the latest version of JIM receives security updates. We recommend always running the most recent release.

## Reporting a Vulnerability

We take security vulnerabilities seriously. If you discover a security issue in JIM, please report it responsibly.

### How to Report

**Please do not report security vulnerabilities through public GitHub issues.**

Instead, please send an email to: **security@tetron.io**

Include as much of the following information as possible:

- Type of vulnerability (e.g., SQL injection, authentication bypass, privilege escalation)
- Full paths of affected source files (if known)
- Step-by-step instructions to reproduce the issue
- Proof-of-concept or exploit code (if available)
- Potential impact of the vulnerability

### What to Expect

- **Acknowledgement**: We will acknowledge receipt of your report within 48 hours
- **Assessment**: We will investigate and assess the vulnerability within 7 days
- **Updates**: We will keep you informed of our progress
- **Resolution**: We aim to resolve critical vulnerabilities within 30 days
- **Credit**: With your permission, we will credit you in the security advisory

### Disclosure Policy

- Please allow us reasonable time to address the vulnerability before public disclosure
- We will coordinate with you on the timing of any public announcements
- We will not take legal action against researchers who follow responsible disclosure practices

## Security Best Practices for Deployment

When deploying JIM, we recommend:

1. **Use HTTPS**: Always deploy behind TLS/SSL
2. **Secure database credentials**: Use strong passwords and restrict database access
3. **Configure OIDC properly**: Ensure your identity provider is correctly configured
4. **Network isolation**: Place JIM in a secured network segment
5. **Regular updates**: Keep JIM and its dependencies up to date
6. **Audit logging**: Monitor activity logs for suspicious behaviour
7. **Principle of least privilege**: Grant only necessary permissions to service accounts

## Security Features

JIM includes several security features:

- **Authentication**: SSO/OIDC integration for secure authentication
- **Authorisation**: Role-based access control
- **API Security**: JWT Bearer token authentication
- **Audit Trail**: Activity logging for all operations
- **Secure Connections**: Support for LDAPS with certificate validation
- **Credential Encryption**: AES-256-GCM encryption for all stored credentials (see below)

## Credential Encryption

JIM encrypts all sensitive credentials at rest using industry-standard cryptographic algorithms.

### Encryption Details

- **Algorithm**: AES-256-GCM (Galois/Counter Mode)
- **Authentication**: HMAC-SHA256 for integrity verification
- **Implementation**: ASP.NET Core Data Protection API
- **Key Management**: Automatic key rotation with configurable storage

AES-256-GCM is an authenticated encryption algorithm that provides both confidentiality and integrity protection. It is approved by NIST and widely used in government and enterprise security applications.

### What Gets Encrypted

All sensitive connector credentials are encrypted before storage, including:

- Service account passwords
- API keys and tokens
- Database connection passwords
- LDAP bind credentials
- Any credential marked as sensitive in connector settings

### Key Storage

Encryption keys are stored separately from the database at `/data/keys`. This path should be mounted to persistent storage to ensure keys survive container restarts.

To use a custom path, set the `JIM_ENCRYPTION_KEY_PATH` environment variable.

### Key Storage Security Recommendations

1. **Backup encryption keys**: Keys are required to decrypt credentials. Loss of keys means credentials must be re-entered.
2. **Separate from database backups**: Store key backups separately from database backups to maintain defence in depth.
3. **Use persistent storage**: Mount `/data/keys` to persistent storage to survive container restarts.
