# Devcontainer SSH agent forwarding: bind mount on hosts without `/run/host-services/ssh-auth.sock`

## Status: Remediated 2026-05-15.

Originally recorded 2026-05-12 as a known risk for native Linux Docker hosts. The risk materialised earlier than expected: a developer rebuilding the devcontainer on Docker Desktop for Windows hit the predicted failure mode (`A mount config is invalid. Make sure it has the right format and a source folder that exists on the machine where the Docker daemon is running.`), which contradicted the assumption baked into PR [#716](https://github.com/TetronIO/JIM/pull/716) that the bind mount worked on Docker Desktop on macOS *and* Windows. In practice only the macOS Docker Desktop case populates `/run/host-services/ssh-auth.sock`; on Windows and on native Linux Docker the path is absent and the strict `type=bind` mount aborts container startup.

This note is preserved as a postmortem so the next time a launcher-specific assumption sneaks into a mount config we can recognise the pattern.

## Background

The SSH agent forwarding added to `.devcontainer/devcontainer.json` in commit `27285950` (PR [#716](https://github.com/TetronIO/JIM/pull/716), branch `feature/devcontainer-ssh-forwarding`) was introduced to make git commit signing work for developers using Zed and the official devcontainer CLI, which (unlike VS Code's Dev Containers extension) do not implicitly forward `SSH_AUTH_SOCK`. The fix bind-mounted Docker Desktop's host-services socket into the container:

```jsonc
"mounts": [
  "source=/run/host-services/ssh-auth.sock,target=/ssh-agent,type=bind"
]
```

This was verified end-to-end on Docker Desktop for macOS only. The original PR description and the inline comment in `devcontainer.json` asserted that it also worked on Docker Desktop for Windows, on the (untested) assumption that Docker Desktop populated the same host-services socket path on both operating systems. It does not.

## The failure mode

On any Docker daemon where `/run/host-services/ssh-auth.sock` does not exist (Docker Desktop for Windows; native Linux Docker), `--mount type=bind,source=/run/host-services/ssh-auth.sock,...` fails strictly. Modern `--mount type=bind` does not auto-create missing sources; it errors with `bind source path does not exist: /run/host-services/ssh-auth.sock` and the container never starts. This differs from the older `-v` / `--volume` syntax, which silently created an empty directory at the source.

The previous inline comment in `devcontainer.json` claimed Docker silently created an empty directory at the path on hosts without the socket. That was wishful thinking; the strict `--mount` form does not do this.

## Affected hosts

- Docker Desktop for Windows (confirmed 2026-05-15).
- Native Linux Docker (predicted at PR #716 time; remains true).
- Codespaces (also Linux Docker) has not surfaced the issue, presumably because the Codespaces runtime pre-creates `/run/host-services` or tolerates the missing source. Codespaces signing uses `gh-gpgsign` and does not depend on this mount, so functionality is unaffected even where the mount degrades.

## Remediation

Implemented option 1 from the original list of remediation options ("Pre-create the source via `initializeCommand`"), adapted to be cross-platform safe. The original note's example was a host-side `sudo touch`, which is bash-specific and assumes the path lives on the host filesystem. On Docker Desktop the `/run/host-services/` path lives inside the daemon's Linux VM, so a Windows PowerShell `touch` against the Windows host filesystem would not help.

The fix instead delegates the touch to a one-shot busybox container that bind-mounts the daemon's `/run` into itself, so the same `initializeCommand` works on every host that has Docker available (which is a precondition for using the devcontainer):

```jsonc
"initializeCommand": [
  "docker", "run", "--rm",
  "-v", "/run:/run",
  "busybox", "sh", "-c",
  "mkdir -p /run/host-services && [ -e /run/host-services/ssh-auth.sock ] || touch /run/host-services/ssh-auth.sock"
]
```

The `[ -e ]` guard preserves the real socket on macOS Docker Desktop (sockets satisfy the existence test). On Windows / Linux it leaves an empty regular file, which `--mount type=bind` accepts, after which the subsequent `postStartCommand` chown to `vscode` is harmless. SSH forwarding only actually works where Docker Desktop populates the socket for real (macOS); elsewhere the mount is a silent no-op, exactly as the inline comment has always promised but only now reliably delivers.

The array (argv) form is used in `initializeCommand` rather than a single string, so PowerShell on Windows and bash on macOS/Linux invoke `docker` identically without per-platform shell escaping pitfalls.

## Why not the alternatives

- **`type=volume` instead of `type=bind`** (option 2 in the original note): always succeeds, but kills the real SSH forwarding on macOS too, defeating the purpose of PR #716.
- **Per-host conditional mount** (option 3): `devcontainer.json` does not support host-conditional mounts directly. A `docker-compose.local.yml` overlay or a separate `devcontainer.windows.json` would solve it, but each adds machinery that is hard to justify against a one-line `initializeCommand`.
- **Document and require manual removal** (option 4): cheap but ugly, and the symptom error message is opaque enough that affected developers will lose time before finding the note.

## Lessons for next time

- Mount source paths that depend on a host-side runtime (Docker Desktop's macOS-only host-services proxy, in this case) must be either pre-created by an `initializeCommand` or made tolerant via volume mounts. Asserting "this also works on Windows" without testing on Windows is the same class of error as asserting "this also works on Linux" without testing on Linux. PR #716 made the latter explicit in this very note while making the former implicit in the PR description.
- When introducing a launcher-specific or platform-specific workaround into shared configuration, document the verification matrix in the PR description (which OS / which launcher / what was tested), not just the intent.

## Review trigger

Re-evaluate this note when any of the following happens:
- A developer reports a rebuild failure that bypasses the `initializeCommand` (e.g., because their Docker setup blocks the one-shot container).
- VS Code's Dev Containers extension or Zed changes its SSH forwarding behaviour in a way that makes the host-services mount unnecessary.
- We move to a docker-compose-based devcontainer where per-host overlays become the natural fit.
