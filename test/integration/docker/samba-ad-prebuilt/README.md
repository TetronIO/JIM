# Pre-built Samba AD Images

Pre-initialised Samba AD Docker images for faster integration testing.

## Architecture Support

The images use `diegogslomp/samba-ad-dc` as the base, which provides native multi-architecture support for both:
- **AMD64** (x86_64) - Intel/AMD processors
- **ARM64** - Apple Silicon (M1/M2/M3), AWS Graviton, etc.

Docker automatically pulls the correct architecture for your platform, providing native performance without emulation.

## Why Pre-built Images?

The standard base image provisions a new Active Directory domain on every startup, which takes **3-5 minutes**. These pre-built images have domain provisioning already complete, reducing startup time to **~30 seconds**.

| Metric | Standard Image | Pre-built Image |
|--------|----------------|-----------------|
| Container start | ~3-5 min | **~10s** |
| Health check start_period | 180s | 30s |
| TLS setup | ~60s (post-start) | 0s (pre-configured) |
| **Total per instance** | **~5-6 min** | **~10s** |

## Available Images

| Image | Domain | Use Case |
|-------|--------|----------|
| `ghcr.io/tetronio/jim-samba-ad:primary` | `TESTDOMAIN.LOCAL` | Scenario 1 & 3 |
| `ghcr.io/tetronio/jim-samba-ad:source` | `SOURCEDOMAIN.LOCAL` | Scenario 2 |
| `ghcr.io/tetronio/jim-samba-ad:target` | `TARGETDOMAIN.LOCAL` | Scenario 2 |

## What's Pre-configured

Each image includes:

- Domain fully provisioned
- Administrator account (password: `Test@123!`)
- Password complexity disabled
- TLS/LDAPS enabled with self-signed certificates
- RFC2307 attributes configured
- SSH public key schema installed

## What's NOT Included

- Test users (created by `Populate-SambaAD.ps1`)
- Test groups
- Group memberships

This allows each test run to start with a clean, provisioned domain.

## Building Images Locally

```powershell
# Build all images
pwsh ./Build-SambaImages.ps1 -Images All

# Build specific image
pwsh ./Build-SambaImages.ps1 -Images Primary

# Build and push to registry
pwsh ./Build-SambaImages.ps1 -Images All -Push
```

## Using with Docker Compose

The `docker-compose.integration-tests.yml` automatically uses these images:

```bash
# Uses pre-built images (default, fast)
docker compose -f docker-compose.integration-tests.yml up -d

# Build images locally if not available
docker compose -f docker-compose.integration-tests.yml up -d --build

# Fall back to standard base image (slow, but no pre-build required)
SAMBA_IMAGE_PRIMARY=diegogslomp/samba-ad-dc \
SAMBA_START_PERIOD=180s \
docker compose -f docker-compose.integration-tests.yml up -d
```

## Image Structure

```
/usr/local/samba/
├── etc/
│   └── smb.conf              # Samba configuration
├── private/
│   ├── sam.ldb               # AD database
│   ├── secrets.ldb           # Machine secrets
│   ├── dns/                  # DNS zones
│   └── tls/                  # TLS certificates
│       ├── key.pem
│       ├── cert.pem
│       └── ca.pem
└── var/
    └── locks/                # Runtime data
```

## Troubleshooting

### Image not found

If the pre-built image isn't available from the registry, build it locally:

```powershell
pwsh ./Build-SambaImages.ps1 -Images Primary
```

### Container starts slowly

If using environment variable overrides that point to the standard image, startup will be slow:

```bash
# Check which image is being used
docker inspect samba-ad-primary --format='{{.Config.Image}}'
```

### TLS/LDAPS not working

Verify TLS is configured:

```bash
docker exec samba-ad-primary grep "tls enabled" /usr/local/samba/etc/smb.conf
```
