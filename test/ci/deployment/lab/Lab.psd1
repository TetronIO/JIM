# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

# The lab host's fixed parts, as TetronIO/ci-runners sets them up (scripts/deploy-lab/, docs/deploy-lab.md): the LXD
# profiles, the two networks' gateways (where the lab's identity provider listens), and the identity provider's realm
# and test administrator, which are JIM's development realm and its well-known development credentials.
@{
    # Every VM gets the base profile (CPU, disk, the lab's storage pool) and its network's profile.
    BaseProfile = 'jimlab'
    Networks = @{
        airgap = @{ Profile = 'jimlab-airgap'; Gateway = '10.78.0.1' }
        online = @{ Profile = 'jimlab-online'; Gateway = '10.77.0.1' }
    }

    # The name JIM is installed under, which the lab realm accepts as a redirect, and which the lab host resolves to
    # each VM's address for itself.
    HostName = 'jim.jimlab.test'

    IdentityProvider = @{
        Port = 8180
        Realm = 'jim'
        # The development realm's administrator: Subject Identifier 00000000-0000-0000-0000-000000000001, which the
        # boot check installs JIM with as its initial administrator.
        User = 'admin'
        Password = 'admin'
    }

    # The lab's test directory (OpenLDAP, on each network's gateway), which upgrade cells connect a Connected System to,
    # with a stored bind password that the upgraded JIM must still decrypt. Lab-only values, as ci-runners sets them.
    Directory = @{
        Port = 1389
        Suffix = 'dc=jimlab,dc=test'
        BindDn = 'cn=admin,dc=jimlab,dc=test'
        Password = 'jimlab-directory'
    }

    # An ExternalDatabase cell's own PostgreSQL server: a plain Ubuntu VM from the lab's images, given PostgreSQL from
    # the PostgreSQL project's packages while on the online network, then moved onto the cell's.
    DatabaseServer = @{
        Image = 'jimlab/u24-os'
        PostgresVersion = 18
    }

    # Memory kept back for the host itself (its identity provider, directory and the runner) when working out how many
    # VMs run at once.
    ReserveGiB = 8

    # Where the runner keeps what it fetches once (PowerShell for the VMs).
    CachePath = '~/.cache/jim-deployment-lab'
}
