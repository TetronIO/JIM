# Pre-built Samba AD Images

Pre-initialised Samba AD Docker images for faster integration testing.

## Why Pre-built Images?

The standard `nowsci/samba-domain` image provisions a new Active Directory domain on every startup, which takes **3-5 minutes**. These pre-built images have domain provisioning already complete, reducing startup time to **~30 seconds**.

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

- Domain fully provisioned (smb.conf marker in `/etc/samba/external/`)
- Administrator account (password: `Test@123!`)
- Password complexity disabled
- TLS/LDAPS enabled with self-signed certificates (in smb.conf)
- RFC2307 attributes configured

## What's Created at Runtime

Due to Docker volume limitations, some items are created when the container starts:

- AD database `/var/lib/samba/private/sam.ldb` (provisioned from scratch, but fast because config exists)
- Test OUs: `OU=TestUsers`, `OU=TestGroups` (created by `Populate-SambaAD.ps1`)
- Test users and groups

**Note**: The base `nowsci/samba-domain` image declares `/var/lib/samba` as a VOLUME, so the AD database is recreated on each container start. However, because the smb.conf marker file exists, the init script skips the slow provisioning process and the domain is ready in ~10 seconds.

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

# Fall back to standard image (slow)
SAMBA_IMAGE_PRIMARY=nowsci/samba-domain \
SAMBA_START_PERIOD=180s \
docker compose -f docker-compose.integration-tests.yml up -d
```

## Image Structure

```
/var/lib/samba/
├── private/
│   ├── sam.ldb              # AD database
│   ├── secrets.ldb          # Machine secrets
│   ├── dns/                  # DNS zones
│   └── tls/                  # TLS certificates
│       ├── key.pem
│       ├── cert.pem
│       └── ca.pem
/etc/samba/
├── smb.conf                  # Samba configuration
└── external/
    └── smb.conf              # Marker file (tells init.sh to skip provisioning)
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
docker exec samba-ad-primary grep "tls enabled" /etc/samba/smb.conf
```
