# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

# The deployment lab's matrix (#1876): every operating system, container runtime and network a customer installs JIM
# on, one cell each, each on a fresh VM. The images are built on the lab host by TetronIO/ci-runners
# (scripts/deploy-lab/build-lab-images.sh): the distribution's cloud image with only the container runtime added, as a
# customer's host has. AlmaLinux stands in for Red Hat Enterprise Linux 9, with SELinux enforcing and firewalld on.
#
# A cell's MemoryGiB is what its VM is given, and an ExternalDatabase cell's DatabaseMemoryGiB its database server's;
# the runner starts as many cells at once as the host's memory holds.
@{
    Images = @{
        'u24-docker-ctrd' = @{ Image = 'jimlab/u24-docker-ctrd'; Os = 'Ubuntu 24.04' }
        'u24-docker-classic' = @{ Image = 'jimlab/u24-docker-classic'; Os = 'Ubuntu 24.04' }
        'u24-podman' = @{ Image = 'jimlab/u24-podman'; Os = 'Ubuntu 24.04' }
        'alma9-podman' = @{ Image = 'jimlab/alma9-podman'; Os = 'AlmaLinux 9' }
    }

    Cells = @(
        # Docker, with each of its two image stores: containerd (the default for a new installation) and the classic
        # overlay2 store an existing host may still use.
        @{ Name = 'u24-docker-ctrd-ag'; Image = 'u24-docker-ctrd'; Runtime = 'docker'; Network = 'airgap'; Scenario = 'Install'; MemoryGiB = 6; Tags = @('install', 'airgap', 'docker', 'ubuntu') }
        @{ Name = 'u24-docker-classic-ag'; Image = 'u24-docker-classic'; Runtime = 'docker'; Network = 'airgap'; Scenario = 'Install'; MemoryGiB = 6; Tags = @('install', 'airgap', 'docker', 'ubuntu') }

        # Podman, rootful and rootless, on Ubuntu (AppArmor) and on AlmaLinux (SELinux, firewalld, NetworkManager).
        @{ Name = 'u24-podrf-ag'; Image = 'u24-podman'; Runtime = 'podman'; Rootless = $false; Network = 'airgap'; Scenario = 'Install'; MemoryGiB = 6; Tags = @('install', 'airgap', 'podman', 'ubuntu') }
        @{ Name = 'u24-podrl-ag'; Image = 'u24-podman'; Runtime = 'podman'; Rootless = $true; Network = 'airgap'; Scenario = 'Install'; MemoryGiB = 6; Tags = @('install', 'airgap', 'podman', 'ubuntu') }
        @{ Name = 'alma9-podrf-ag'; Image = 'alma9-podman'; Runtime = 'podman'; Rootless = $false; Network = 'airgap'; Scenario = 'Install'; MemoryGiB = 6; Tags = @('install', 'airgap', 'podman', 'rhel') }
        @{ Name = 'alma9-podrl-ag'; Image = 'alma9-podman'; Runtime = 'podman'; Rootless = $true; Network = 'airgap'; Scenario = 'Install'; MemoryGiB = 6; Tags = @('install', 'airgap', 'podman', 'rhel') }

        # Upgrades from the previous release, with its data, by the Upgrading guide, then a rollback by it. 12 GiB, as
        # v0.16.0's database does not start below about 9 GB (#1943); 6 will do once the previous release is later.
        @{ Name = 'u24-docker-ctrd-up'; Image = 'u24-docker-ctrd'; Runtime = 'docker'; Network = 'airgap'; Scenario = 'Upgrade'; MemoryGiB = 12; Tags = @('upgrade', 'airgap', 'docker', 'ubuntu') }
        @{ Name = 'u24-docker-classic-up'; Image = 'u24-docker-classic'; Runtime = 'docker'; Network = 'airgap'; Scenario = 'Upgrade'; MemoryGiB = 12; Tags = @('upgrade', 'airgap', 'docker', 'ubuntu') }
        @{ Name = 'u24-podrf-up'; Image = 'u24-podman'; Runtime = 'podman'; Rootless = $false; Network = 'airgap'; Scenario = 'Upgrade'; MemoryGiB = 12; Tags = @('upgrade', 'airgap', 'podman', 'ubuntu') }
        @{ Name = 'u24-podrl-up'; Image = 'u24-podman'; Runtime = 'podman'; Rootless = $true; Network = 'airgap'; Scenario = 'Upgrade'; MemoryGiB = 12; Tags = @('upgrade', 'airgap', 'podman', 'ubuntu') }
        @{ Name = 'alma9-podrf-up'; Image = 'alma9-podman'; Runtime = 'podman'; Rootless = $false; Network = 'airgap'; Scenario = 'Upgrade'; MemoryGiB = 12; Tags = @('upgrade', 'airgap', 'podman', 'rhel') }
        @{ Name = 'alma9-podrl-up'; Image = 'alma9-podman'; Runtime = 'podman'; Rootless = $true; Network = 'airgap'; Scenario = 'Upgrade'; MemoryGiB = 12; Tags = @('upgrade', 'airgap', 'podman', 'rhel') }

        # The installer's refusals, a run stopped with Ctrl+C, and runs over an installation, on each runtime and both
        # kinds of host.
        @{ Name = 'u24-docker-ctrd-cases'; Image = 'u24-docker-ctrd'; Runtime = 'docker'; Network = 'airgap'; Scenario = 'InstallerCases'; MemoryGiB = 6; Tags = @('cases', 'airgap', 'docker', 'ubuntu') }
        @{ Name = 'u24-podrf-cases'; Image = 'u24-podman'; Runtime = 'podman'; Rootless = $false; Network = 'airgap'; Scenario = 'InstallerCases'; MemoryGiB = 6; Tags = @('cases', 'airgap', 'podman', 'ubuntu') }
        @{ Name = 'alma9-podrf-cases'; Image = 'alma9-podman'; Runtime = 'podman'; Rootless = $false; Network = 'airgap'; Scenario = 'InstallerCases'; MemoryGiB = 6; Tags = @('cases', 'airgap', 'podman', 'rhel') }
        @{ Name = 'alma9-podrl-cases'; Image = 'alma9-podman'; Runtime = 'podman'; Rootless = $true; Network = 'airgap'; Scenario = 'InstallerCases'; MemoryGiB = 6; Tags = @('cases', 'airgap', 'podman', 'rhel') }

        # A PostgreSQL server of the customer's own, on a VM of its own beside JIM's.
        @{ Name = 'u24-docker-ctrd-extdb'; Image = 'u24-docker-ctrd'; Runtime = 'docker'; Network = 'airgap'; Scenario = 'ExternalDatabase'; MemoryGiB = 6; DatabaseMemoryGiB = 2; Tags = @('extdb', 'airgap', 'docker', 'ubuntu') }
        @{ Name = 'alma9-podrf-extdb'; Image = 'alma9-podman'; Runtime = 'podman'; Rootless = $false; Network = 'airgap'; Scenario = 'ExternalDatabase'; MemoryGiB = 6; DatabaseMemoryGiB = 2; Tags = @('extdb', 'airgap', 'podman', 'rhel') }
        @{ Name = 'alma9-podrf-soak'; Image = 'alma9-podman'; Runtime = 'podman'; Rootless = $false; Network = 'airgap'; Scenario = 'Soak'; MemoryGiB = 6; Tags = @('soak', 'airgap', 'podman', 'rhel') }

        # Online, as a customer with internet access installs and upgrades: the installation guide's one-line install
        # of the latest published release, and an upgrade to it from the release before it by the guide's connected
        # steps, downloading from GitHub and ghcr.io. They test what is published, not the commit, so a run takes them
        # only when asked for with the online tag: release.yml does, once a release is published.
        @{ Name = 'u24-docker-ctrd-on'; Image = 'u24-docker-ctrd'; Runtime = 'docker'; Network = 'online'; Scenario = 'Install'; MemoryGiB = 6; Tags = @('install', 'online', 'docker', 'ubuntu') }
        @{ Name = 'u24-docker-classic-on'; Image = 'u24-docker-classic'; Runtime = 'docker'; Network = 'online'; Scenario = 'Install'; MemoryGiB = 6; Tags = @('install', 'online', 'docker', 'ubuntu') }
        @{ Name = 'alma9-podrf-on'; Image = 'alma9-podman'; Runtime = 'podman'; Rootless = $false; Network = 'online'; Scenario = 'Install'; MemoryGiB = 6; Tags = @('install', 'online', 'podman', 'rhel') }
        @{ Name = 'u24-podrl-on'; Image = 'u24-podman'; Runtime = 'podman'; Rootless = $true; Network = 'online'; Scenario = 'Install'; MemoryGiB = 6; Tags = @('install', 'online', 'podman', 'ubuntu') }
        @{ Name = 'u24-docker-ctrd-on-up'; Image = 'u24-docker-ctrd'; Runtime = 'docker'; Network = 'online'; Scenario = 'Upgrade'; MemoryGiB = 12; Tags = @('upgrade', 'online', 'docker', 'ubuntu') }
        @{ Name = 'u24-docker-classic-on-up'; Image = 'u24-docker-classic'; Runtime = 'docker'; Network = 'online'; Scenario = 'Upgrade'; MemoryGiB = 12; Tags = @('upgrade', 'online', 'docker', 'ubuntu') }
        @{ Name = 'alma9-podrf-on-up'; Image = 'alma9-podman'; Runtime = 'podman'; Rootless = $false; Network = 'online'; Scenario = 'Upgrade'; MemoryGiB = 12; Tags = @('upgrade', 'online', 'podman', 'rhel') }
        @{ Name = 'u24-podrl-on-up'; Image = 'u24-podman'; Runtime = 'podman'; Rootless = $true; Network = 'online'; Scenario = 'Upgrade'; MemoryGiB = 12; Tags = @('upgrade', 'online', 'podman', 'ubuntu') }
    )
}
