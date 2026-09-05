# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMServiceHealth {
    <#
    .SYNOPSIS
        Reports whether JIM's background services (the Worker and the Scheduler) are alive and what each is doing.

    .DESCRIPTION
        Reads the heartbeat every JIM background service writes to the database every 5 seconds and returns one
        object per service: WorkerSync (the Worker's synchronisation loop, which runs Run Profiles and other
        queued work), WorkerPasswordDelivery (the Worker's password delivery loop) and Scheduler (which starts
        Schedules when they fall due). This is the same report the Service Health strip on the Operations page
        shows, so a script and an administrator always see the same verdict.

        Each service's State is one of:

        - Running: reported within the last 15 seconds. Nothing to do.
        - Stale: more than 15 seconds since the last heartbeat, but not yet long enough to presume the process is
          gone. It may be paused under load or the database may be slow; worth a glance, not yet an alarm.
        - NoProgress: alive, but its current work has not moved forward for more than 10 minutes. The process is
          up; the task it is running may be wedged. Only judged for work that reports progress.
        - NotSeen: no heartbeat for 60 seconds (Worker services) or 120 seconds (Scheduler), or the service has
          never reported at all (Reason is "Never reported"). Queued and scheduled work will not run until it is
          back.

        Reason says why in one sentence. CurrentWork names what the service was doing when it last reported (for
        example "Full Import: Corporate Directory"), and Version is the JIM version that instance runs: compare it
        with the web tier's version from -Summary, because a mismatch means a partial upgrade.

        With -Summary, one object is returned instead: Overall (the worst state present), WebVersion, GeneratedAt
        and the per-service objects under Services. A monitoring script that alerts on anything other than Running
        needs to read nothing but Overall.

        Get-JIMHealth answers a different question: it probes the web tier only, without authentication, and says
        nothing about the Worker or the Scheduler. This cmdlet needs a Connect-JIM session with the Administrator
        role.

    .PARAMETER Summary
        Returns one object carrying Overall, WebVersion, GeneratedAt and Services, instead of one object per
        service.

    .OUTPUTS
        JIM.ServiceHealth
        One object per service with Service, State, Reason, CurrentWork, CurrentWorkStartedAt, LastSeenAt,
        StartedAt, HostName, Version, InstanceId, LastProgressAt and Detail. Fields a never-seen service cannot
        supply are null.

        JIM.ServiceHealthSummary
        With -Summary: one object with Overall, WebVersion, GeneratedAt and Services (the JIM.ServiceHealth
        objects above).

    .EXAMPLE
        Get-JIMServiceHealth

        Lists the three services with their state and reason. The quickest way to find out whether the Worker is
        running and what it is doing.

    .EXAMPLE
        Get-JIMServiceHealth | Format-Table Service, State, CurrentWork, LastSeenAt, Version

        The columns that matter during a change window: what each service is doing, when it last reported, and
        which version it is running.

    .EXAMPLE
        $health = Get-JIMServiceHealth -Summary
        if ($health.Overall -ne 'Running') {
            $health.Services | Where-Object State -ne 'Running' | Format-List Service, State, Reason
            exit 1
        }

        A monitoring check. Exits non-zero when any service is Stale, NoProgress or NotSeen, printing which and
        why, so a scheduler or pipeline can raise an alert on the exit code.

    .EXAMPLE
        Get-JIMServiceHealth | Where-Object State -eq 'NoProgress' | Select-Object Service, CurrentWork, LastProgressAt

        Names the work that has stalled. The process is up; the task is what needs looking at, and the Operations
        page's Queue tab is where to cancel it if it is genuinely wedged.

    .EXAMPLE
        $health = Get-JIMServiceHealth -Summary
        $health.Services | Where-Object { $_.Version -and $_.Version -ne $health.WebVersion } |
            Select-Object Service, HostName, Version, @{ Name = 'WebVersion'; Expression = { $health.WebVersion } }

        Finds services running a different version from the web tier, which after an upgrade means one container
        did not restart.

    .LINK
        Get-JIMHealth
        Get-JIMVersion
    #>
    [CmdletBinding(DefaultParameterSetName = 'Services')]
    [OutputType('JIM.ServiceHealth', ParameterSetName = 'Services')]
    [OutputType('JIM.ServiceHealthSummary', ParameterSetName = 'Summary')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'Summary')]
        [switch]$Summary
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        Write-Verbose "Getting service health"
        $response = Invoke-JIMApi -Endpoint '/api/v1/system/health'
        if ($null -eq $response) {
            return
        }

        # Built explicitly rather than passed through, so the output shape is the documented one whatever the
        # wire's casing, and so the property order reads as an operator wants it: what is it, is it well, why,
        # what is it doing; the identifying detail after.
        $services = foreach ($service in @($response.services)) {
            $item = [PSCustomObject]@{
                Service              = $service.service
                State                = $service.state
                Reason               = $service.reason
                CurrentWork          = $service.currentWork
                CurrentWorkStartedAt = $service.currentWorkStartedAt
                LastSeenAt           = $service.lastSeenAt
                StartedAt            = $service.startedAt
                HostName             = $service.hostName
                Version              = $service.version
                InstanceId           = $service.instanceId
                LastProgressAt       = $service.lastProgressAt
                Detail               = $service.detail
            }
            $item.PSObject.TypeNames.Insert(0, 'JIM.ServiceHealth')
            $item
        }

        if ($PSCmdlet.ParameterSetName -eq 'Summary') {
            $summaryObject = [PSCustomObject]@{
                Overall     = $response.overall
                WebVersion  = $response.webVersion
                GeneratedAt = $response.generatedAt
                Services    = @($services)
            }
            $summaryObject.PSObject.TypeNames.Insert(0, 'JIM.ServiceHealthSummary')
            $summaryObject
            return
        }

        $services
    }
}
