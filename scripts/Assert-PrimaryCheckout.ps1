# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Refuses to let a volume-destroying script run from a linked git worktree.

.DESCRIPTION
    The JIM Docker stack is a singleton per host. docker-compose.yml gives every service a
    container_name, and gives the network and all four volumes an explicit name:, so those resources
    belong to the Docker daemon rather than to a compose project. `docker compose down -v` therefore
    removes jim-db-volume whichever directory it is invoked from, and several scripts remove it by
    name as well. `docker compose -p <project>` does not change any of this.

    A second checkout running one of those scripts therefore destroys the JIM instance belonging to
    the primary checkout: its database and its data protection keys, which presents to the developer
    as being unable to sign in. That happened on 2026-08-11, when an agent session ran the integration
    suite from a worktree while the developer's instance was up.

    Making the stack project-scoped is the proper fix. It was analysed and deliberately declined,
    because the only thing it buys is local multi-instance for worktrees and that is not a workflow we
    want to encourage; see engineering/notes/DOCKER_STACK_PROJECT_SCOPING.md for the analysis and for
    what a fix would involve. This guard is the cheap protection that remains.

    A linked worktree is the reliable signal for "second checkout": git reports a per-worktree git
    directory that differs from the shared common directory. The primary checkout reports the same
    path for both.

.PARAMETER RepoRoot
    The repository root the calling script is operating on.

.PARAMETER Allow
    Set when the caller passed its own opt-out switch. Only correct when no other JIM stack is running
    on this host.

.PARAMETER ScriptName
    Name of the calling script, used in the refusal message.
#>
function Assert-PrimaryCheckout {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)][string]$RepoRoot,
        [Parameter(Mandatory=$false)][switch]$Allow,
        [Parameter(Mandatory=$true)][string]$ScriptName
    )

    if ($Allow) {
        Write-Host "  Worktree check bypassed (-AllowWorktree). Ensure no other JIM stack is running." -ForegroundColor Yellow
        return
    }

    Push-Location $RepoRoot
    try {
        $gitDir = (git rev-parse --git-dir 2>$null)
        $gitCommonDir = (git rev-parse --git-common-dir 2>$null)
    } finally {
        Pop-Location
    }

    # Not a git checkout at all, or git unavailable: nothing to assert, so do not block.
    if (-not $gitDir -or -not $gitCommonDir) {
        return
    }

    if ($gitDir -eq $gitCommonDir) {
        return
    }

    Write-Host ""
    Write-Host "Refusing to run $ScriptName from a linked git worktree." -ForegroundColor Red
    Write-Host "  This script removes jim-db-volume, jim-keys-volume and the rest by their global names," -ForegroundColor Yellow
    Write-Host "  so it would destroy the JIM instance belonging to the primary checkout, not just this" -ForegroundColor Yellow
    Write-Host "  one's. The stack is a singleton per host." -ForegroundColor Yellow
    Write-Host "  See engineering/notes/DOCKER_STACK_PROJECT_SCOPING.md." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  Run from the primary checkout, or pass -AllowWorktree if you are certain no other JIM" -ForegroundColor Gray
    Write-Host "  stack is running on this host." -ForegroundColor Gray
    Write-Host ""
    exit 1
}
