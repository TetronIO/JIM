#!/bin/bash
# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.
#
# Configure git SSH commit signing in the devcontainer.
#
# This script detects the current environment and tries to configure git to
# sign commits using the most appropriate mechanism:
#
#   1. If running inside a GitHub Codespace, the system gitconfig already
#      contains gpg.program=/.codespaces/bin/gh-gpgsign which signs via the
#      GitHub API. No further action is required beyond ensuring
#      commit.gpgsign=true and gpg.format=ssh are set.
#
#   2. If running in a local devcontainer with SSH agent forwarding active
#      and a key loaded, configure git to use the forwarded key for signing.
#      This mirrors the developer's local SSH identity without copying the
#      private key into the container.
#
#   3. If neither of those is available, print a loud warning with
#      actionable recovery steps and exit 1. Commits in this state will
#      either be unsigned, or refused by the pre-commit hook in .githooks/.
#
# Designed to be idempotent: running it repeatedly is safe. Intended to be
# invoked by setup.sh during devcontainer creation AND by developers via the
# jim-setup-signing alias whenever they want to reconfigure signing.

set -e

# Allow colour output but degrade gracefully if not a terminal.
if [ -t 1 ]; then
    GREEN='\033[0;32m'
    BLUE='\033[0;34m'
    YELLOW='\033[1;33m'
    RED='\033[0;31m'
    BOLD='\033[1m'
    NC='\033[0m'
else
    GREEN=''
    BLUE=''
    YELLOW=''
    RED=''
    BOLD=''
    NC=''
fi

print_banner() {
    local colour="$1"
    local title="$2"
    shift 2
    echo ""
    echo -e "${colour}${BOLD}╔══════════════════════════════════════════════════════════════════════════╗${NC}"
    printf "${colour}${BOLD}║ %-72s ║${NC}\n" "$title"
    echo -e "${colour}${BOLD}╠══════════════════════════════════════════════════════════════════════════╣${NC}"
    for line in "$@"; do
        printf "${colour}${BOLD}║${NC} %-72s ${colour}${BOLD}║${NC}\n" "$line"
    done
    echo -e "${colour}${BOLD}╚══════════════════════════════════════════════════════════════════════════╝${NC}"
    echo ""
}

print_step() {
    echo -e "${BLUE}▶${NC} $1"
}

print_success() {
    echo -e "${GREEN}✓${NC} $1"
}

print_warning() {
    echo -e "${YELLOW}⚠${NC} $1"
}

print_error() {
    echo -e "${RED}✗${NC} $1"
}

# Detect whether we are running inside a GitHub Codespace. The CODESPACES
# environment variable is the canonical signal; we also check for the
# gh-gpgsign helper as a secondary cue.
is_codespace() {
    if [ -n "$CODESPACES" ] && [ "$CODESPACES" = "true" ]; then
        return 0
    fi
    if [ -x "/.codespaces/bin/gh-gpgsign" ]; then
        return 0
    fi
    return 1
}

# Probe that gh-gpgsign can actually produce a signature. Configuring git is
# necessary but not sufficient in a Codespace: gh-gpgsign signs via the GitHub
# API, which returns "Current user GPG signing disabled" unless the account has
# GPG verification enabled for this repo (github.com/settings/codespaces). We
# sign a throwaway, unreferenced commit object (git commit-tree -S) rather than
# a real commit: it touches no ref and is garbage-collected, but it exercises
# the exact signing path git uses. Returns 0 if signing works (or cannot be
# probed), 1 if it is disabled or otherwise failing.
verify_codespaces_signing_works() {
    local tree probe_out
    # No HEAD yet (unusual setup ordering / bare clone) means nothing to sign
    # against; don't raise a false alarm.
    tree=$(git rev-parse "HEAD^{tree}" 2>/dev/null) || return 0

    if probe_out=$(echo "jim codespaces signing probe" | git commit-tree "$tree" -S 2>&1); then
        return 0
    fi

    if echo "$probe_out" | grep -qi "signing disabled"; then
        print_banner "$RED" "Codespaces commit signing is disabled on your account" \
            "" \
            "git is configured to sign, but a test signature was refused" \
            "by the GitHub API. gh-gpgsign needs GPG verification enabled" \
            "for your account. To fix:" \
            "" \
            "  1. Open github.com/settings/codespaces" \
            "  2. Under 'GPG verification', enable it and allow this" \
            "     repository (or select all repositories)" \
            "  3. Restart this Codespace (Stop then Start, or rebuild) so a" \
            "     fresh token carries the capability; the running token" \
            "     does not pick it up until then" \
            "" \
            "This repository's branch ruleset requires signed commits, so" \
            "the pre-commit hook will refuse commits until this is done." \
            "" \
            "See engineering/DEVELOPER_GUIDE.md 'Commit Signing' for details."
    else
        print_banner "$RED" "Codespaces commit signing test failed" \
            "" \
            "git is configured to sign, but a test signature failed for" \
            "an unexpected reason (see the probe output below)." \
            "" \
            "Run 'jim-setup-signing' to retry once resolved, or see" \
            "engineering/DEVELOPER_GUIDE.md 'Commit Signing' for details."
    fi
    print_warning "Signing probe output: ${probe_out}"
    return 1
}

# Configure signing for Codespaces. The heavy lifting is already done by the
# Codespaces system gitconfig, which sets gpg.program to the gh-gpgsign helper.
# We just need to ensure commit.gpgsign is true and that we do NOT set
# gpg.format, because Codespaces' signing chain is:
#   commit.gpgsign=true -> (no gpg.format) -> default gpg format -> gpg.program
#   -> /.codespaces/bin/gh-gpgsign -> GitHub API signing
# Setting gpg.format=ssh would break this chain by routing to native SSH
# signing (which requires user.signingkey) instead of gh-gpgsign.
configure_codespaces_signing() {
    print_step "Configuring git signing for GitHub Codespaces..."

    # Ensure commit/tag signing is on.
    git config --global commit.gpgsign true
    git config --global tag.gpgsign true

    # Explicitly unset gpg.format if a previous run (or a stale global config)
    # set it to ssh. In Codespaces the gh-gpgsign helper is a gpg wrapper, not
    # an ssh-format signer, so gpg.format must be absent or "openpgp".
    git config --global --unset gpg.format 2>/dev/null || true

    # Same for user.signingkey: gh-gpgsign figures out the key to use from
    # the authenticated GitHub session, not from a local key reference. A
    # stale user.signingkey from a previous local-devcontainer session would
    # just be ignored, but we clean it up to reduce surprise.
    git config --global --unset user.signingkey 2>/dev/null || true

    print_success "Codespaces signing configured via gh-gpgsign helper"

    # Configuring git is necessary but not sufficient: gh-gpgsign signs via the
    # GitHub API, which refuses with "Current user GPG signing disabled" unless
    # GPG verification is enabled for your account (and this repo) under
    # github.com/settings/codespaces. Probe it now with a throwaway signed
    # object so the gap surfaces during setup rather than on the first commit.
    if ! verify_codespaces_signing_works; then
        return 1
    fi

    print_success "All commits will be signed automatically"
    return 0
}

# Check that the git user identity is set before configuring signing. The
# allowed_signers file built below ties signatures to the committer's email,
# so user.email must resolve to something meaningful. Without this check the
# script used to fall back to a "developer@local" placeholder, which made
# every "git verify-commit" output show that placeholder regardless of who
# actually authored the commit.
#
# VS Code's Dev Containers extension copies the host gitconfig into the
# container automatically; Zed and the devcontainer CLI do not. After a
# rebuild on those launchers, /home/vscode/.gitconfig has no identity, so we
# need to either inherit it (out of scope here) or refuse to proceed and tell
# the developer to set it.
check_git_identity() {
    local git_name git_email
    git_name=$(git config --get user.name 2>/dev/null || echo "")
    git_email=$(git config --get user.email 2>/dev/null || echo "")

    if [ -n "$git_name" ] && [ -n "$git_email" ]; then
        return 0
    fi

    print_banner "$YELLOW" "Git user identity is not set" \
        "" \
        "  user.name:  ${git_name:-<unset>}" \
        "  user.email: ${git_email:-<unset>}" \
        "" \
        "Signing cannot be fully configured until both are set, because" \
        "the allowed_signers file ties signatures to the committer's" \
        "email. Without it, 'git verify-commit' would show a placeholder" \
        "address instead of the real committer." \
        "" \
        "VS Code's Dev Containers extension copies the host gitconfig" \
        "into the container automatically. Zed and the devcontainer CLI" \
        "do not, so identity must be set explicitly inside the container" \
        "(or in a host config that survives rebuilds)." \
        "" \
        "Set both, then re-run signing setup:" \
        "" \
        "  git config --global user.name \"Your Name\"" \
        "  git config --global user.email \"you@example.com\"" \
        "  .devcontainer/configure-signing.sh"
    return 1
}

# Configure signing for a local devcontainer. This requires the host's SSH
# agent to be forwarded into the container with at least one key loaded, and
# the git user identity to be set (see check_git_identity).
configure_local_signing() {
    print_step "Configuring git SSH signing for local devcontainer..."

    # Probe the forwarded SSH agent.
    if ! ssh-add -l >/dev/null 2>&1; then
        print_banner "$YELLOW" "SSH agent not available or has no keys" \
            "" \
            "Git commit signing cannot be configured in this devcontainer" \
            "because the host's SSH agent is not forwarded or has no keys" \
            "loaded. Without signing, commits made in this environment will" \
            "be refused by the pre-commit hook." \
            "" \
            "To fix this, on your HOST machine (not inside the container):" \
            "" \
            "  macOS:" \
            "    1. ssh-add --apple-use-keychain ~/.ssh/id_ed25519" \
            "    2. Verify: ssh-add -l" \
            "    3. Rebuild the devcontainer (Command Palette:" \
            "       'Dev Containers: Rebuild Container')" \
            "" \
            "  Linux:" \
            "    1. eval \"\$(ssh-agent -s)\"" \
            "    2. ssh-add ~/.ssh/id_ed25519" \
            "    3. Rebuild the devcontainer" \
            "" \
            "  Windows 11:" \
            "    1. Open Services (services.msc)" \
            "    2. Set 'OpenSSH Authentication Agent' to Automatic and Start" \
            "    3. In PowerShell (admin): ssh-add \$env:USERPROFILE\\.ssh\\id_ed25519" \
            "    4. Verify: ssh-add -l" \
            "    5. Rebuild the devcontainer" \
            "" \
            "After rebuilding, run: jim-setup-signing" \
            "" \
            "See engineering/DEVELOPER_GUIDE.md 'Commit Signing' for details."
        return 1
    fi

    # Grab the first public key from the agent.
    local ssh_key
    ssh_key=$(ssh-add -L 2>/dev/null | head -1)

    if [ -z "$ssh_key" ]; then
        print_error "ssh-add -L returned no keys; agent is forwarded but empty"
        return 1
    fi

    # Configure git to sign commits with the SSH key. The "key::" prefix is
    # the git-recognised syntax for supplying a public key value directly
    # rather than a path to a key file. The private key stays on the host;
    # only the public key is referenced here, and actual signing is delegated
    # to the forwarded agent.
    git config --global gpg.format ssh
    git config --global commit.gpgsign true
    git config --global tag.gpgsign true
    git config --global user.signingkey "key::$ssh_key"

    # Build an allowed_signers file so that "git verify-commit" works locally.
    # check_git_identity has already guaranteed user.email is set, so we read
    # it from the resolved git config (any scope, not just --global) to match
    # what git will actually use for the commit author.
    local git_email
    git_email=$(git config --get user.email)
    mkdir -p ~/.ssh
    echo "$git_email $ssh_key" > ~/.ssh/allowed_signers
    git config --global gpg.ssh.allowedSignersFile ~/.ssh/allowed_signers

    print_success "Git SSH signing configured using forwarded agent key"
    print_success "Key: $(echo "$ssh_key" | awk '{print $1, $NF}')"
    return 0
}

# Print the current signing configuration and whether the next commit would
# be signed. Safe to run at any time.
print_status() {
    echo ""
    echo -e "${BOLD}Git commit signing status${NC}"
    echo ""

    local format signingkey gpgsign
    format=$(git config --get gpg.format 2>/dev/null || echo "<unset>")
    signingkey=$(git config --get user.signingkey 2>/dev/null || echo "<unset>")
    gpgsign=$(git config --get commit.gpgsign 2>/dev/null || echo "<unset>")

    echo "  gpg.format:         $format"
    echo "  user.signingkey:    ${signingkey:0:60}$([ ${#signingkey} -gt 60 ] && echo ...)"
    echo "  commit.gpgsign:     $gpgsign"

    if is_codespace; then
        echo "  environment:        GitHub Codespace"
        echo "  signing mechanism:  gh-gpgsign (via GitHub API)"
    else
        echo "  environment:        local devcontainer (or other)"
        if ssh-add -l >/dev/null 2>&1; then
            local key_count
            key_count=$(ssh-add -l | wc -l)
            echo "  ssh agent:          forwarded, $key_count key(s) loaded"
        else
            echo "  ssh agent:          ${RED}NOT forwarded or no keys${NC}"
        fi
    fi

    echo ""
    if is_codespace; then
        # Actively probe rather than assume. gh-gpgsign can be configured yet
        # still be refused by the GitHub API when GPG verification is disabled
        # for the account, so "commit.gpgsign=true" alone does not mean signing
        # works. verify_codespaces_signing_works prints an actionable banner on
        # failure; on success we add the one residual caveat it cannot detect.
        if verify_codespaces_signing_works; then
            print_success "Signing works; your commits will be marked verified on GitHub"
            print_warning "If you enabled GPG verification AFTER starting this codespace, stop and restart it, otherwise commits sign with a key GitHub will not verify"
        fi
    elif [ "$gpgsign" = "true" ]; then
        if ssh-add -l >/dev/null 2>&1; then
            print_success "Signing is configured and should work on the next commit"
        else
            print_warning "Signing is requested but may fail (no signing source available)"
            print_warning "Run 'jim-setup-signing' to reconfigure"
        fi
    else
        print_warning "Signing is not enabled"
        print_warning "Run 'jim-setup-signing' to configure"
    fi
    echo ""
}

# Main entry point. Supports a --status subcommand for the jim-signing-status
# alias, otherwise runs the full configure flow.
main() {
    if [ "${1:-}" = "--status" ]; then
        print_status
        return 0
    fi

    if is_codespace; then
        configure_codespaces_signing
        return $?
    else
        # Identity must be set before signing config so allowed_signers
        # references the real committer rather than a placeholder.
        if ! check_git_identity; then
            return 1
        fi
        configure_local_signing
        return $?
    fi
}

main "$@"
