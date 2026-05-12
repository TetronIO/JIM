# Devcontainer SSH agent forwarding: risk on native Linux Docker hosts

## Status: Known risk, not remediated (2026-05-12). Recorded so we do not lose context.

The SSH agent forwarding added to `.devcontainer/devcontainer.json` (commit `27285950`, branch `feature/devcontainer-ssh-forwarding`) was introduced to make git commit signing work for developers using Zed and the official devcontainer CLI, which (unlike VS Code's Dev Containers extension) do not implicitly forward `SSH_AUTH_SOCK`. The fix bind-mounts Docker Desktop's host-services socket into the container:

```jsonc
"mounts": [
  "source=/run/host-services/ssh-auth.sock,target=/ssh-agent,type=bind"
]
```

This was verified end-to-end on Docker Desktop (macOS host, Zed launcher). It was **not** verified on native Linux Docker.

## The risk

On a host running native Linux Docker (no Docker Desktop), `/run/host-services/ssh-auth.sock` does not exist. The bind mount in devcontainer.json is `type=bind`, which corresponds to `docker run --mount type=bind,...`. Modern `--mount type=bind` is strict: if the source path does not exist on the host, the command fails with `bind source path does not exist: /run/host-services/ssh-auth.sock` and the container never starts.

This differs from the older `-v` / `--volume` syntax, which silently created an empty directory at the source path. The current `devcontainer.json` comment claims the latter behaviour applies here:

> On native Linux Docker (no Docker Desktop) the source path does not exist; Docker creates an empty directory at that path and bind-mounts it, so the container still starts but SSH forwarding silently no-ops.

The Dev Containers extension *may* be pre-creating the source path on the host before invoking docker (which would make the claim correct), but we have not confirmed that. If it does not, every native-Linux rebuild of this devcontainer will fail at startup with no obvious connection to the SSH-forwarding change.

## Who this affects

- **Local Linux Docker users** (any launcher: VS Code, Zed, devcontainer CLI) rebuilding the devcontainer.
- Currently a small population. We do not actively develop on Linux Docker hosts day-to-day.

Codespaces also runs on a Linux Docker host, but in practice the bind mount has not surfaced an issue there - either the Codespaces runtime creates the path, or it tolerates the missing source. Codespaces signing uses `gh-gpgsign` and does not depend on this socket, so functionality is unaffected even when the mount degrades.

## Symptoms if the risk materialises

- `Dev Containers: Rebuild Container` (VS Code) or `devcontainer up` (CLI) on a native Linux host fails with an error containing `bind source path does not exist: /run/host-services/ssh-auth.sock`.
- The container will not enter a running state. Setup never reaches `configure-signing.sh`.

## Remediation options, in order of preference

1. **Pre-create the source via `initializeCommand`.** Add an `initializeCommand` to `devcontainer.json` that runs on the host before docker:

   ```jsonc
   "initializeCommand": "test -e /run/host-services/ssh-auth.sock || sudo mkdir -p /run/host-services && sudo touch /run/host-services/ssh-auth.sock"
   ```

   Caveats: must be cross-platform-safe (Windows PowerShell needs a different command); creates a regular file at the socket path, not a socket, which `--mount type=bind` will still accept and which our `postStartCommand` chown will tolerate. The result on Linux is the same harmless no-op as the comment claims today, but reliably.

2. **Switch the mount from `type=bind` to `type=volume` with no source.** A named volume always exists. SSH forwarding via the volume would not work, but the container would always start. This is the lowest-risk option for "make the rebuild succeed everywhere" but kills SSH forwarding on Docker Desktop too, which defeats the purpose.

3. **Make the mount conditional.** `devcontainer.json` does not support per-host conditional mounts directly. Workarounds: use `initializeCommand` to write a `docker-compose.local.yml` overlay (we already use this pattern for postgres tuning), or maintain a separate `devcontainer.linux.json`. Both add machinery that is hard to justify for an edge case.

4. **Document the limitation and require Linux users to remove the mount locally.** Cheapest, ugliest. Worth keeping in mind as a fallback if (1) proves brittle.

The intended path when this becomes worth the work is **option 1**.

## Verification trigger

Re-evaluate this note when any of the following happens:
- A developer reports a rebuild failure with the symptom above.
- We start actively supporting Linux Docker hosts as a first-class developer environment.
- We migrate to a different launcher (or VS Code changes its Dev Containers extension behaviour) that no longer pre-creates the mount source.

Review date: 2026-08-12 (~3 months out). If no new evidence by then, refresh or remove.
