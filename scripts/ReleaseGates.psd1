# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

# The release gates: every commit status a commit must carry as `success` before it can be tagged. One row per gate.
#
#   Context             the commit status a test run posts on the SHA it tested.
#   Name                the gate's name, as the release run's warnings and summary give it.
#   Workflow            the workflow that posts the status.
#   EnforcedVariable    the repository variable that switches the gate on, set to `true` once the run has passed for the
#                       first time (gh variable set <name> --body true). Until then release.yml passes the gate with a
#                       warning; from then on there is no override, since the variable is the single switch and
#                       changing it needs repository admin.
#   Remedy              what to do about a refusal; {0} is the commit.
#   NotReportedReason   why a commit can carry no status at all.
#   NotEnforcedMessage  the warning a release carries while the gate is not enforced.
#
# scripts/Test-ReleaseGate.ps1 checks one row; release.yml's release-gates job runs it for every row (a matrix that
# scripts/Tests/Test-ReleaseGate.Tests.ps1 holds to exactly these rows), and the /release skill does the same before it
# tags. Adding a gate is adding a row here and to that matrix.
@{
    Gates = @(
        @{
            Context            = 'jim-ad-lab'
            Name               = 'Active Directory lab gate'
            Workflow           = 'ad-lab.yml'
            EnforcedVariable   = 'JIM_AD_LAB_GATE_ENFORCED'
            Remedy             = 'There is no override: fix the cause, then dispatch ad-lab.yml so it runs on {0} (gh workflow run ad-lab.yml --ref main, while {0} is the head of main), wait for it to finish, and check again.'
            NotReportedReason  = 'the Active Directory lab has not run on this exact commit. The nightly run tests the head of main as it stood then, so a commit made since has none.'
            NotEnforcedMessage = 'The Active Directory lab gate is not enforced yet (JIM_AD_LAB_GATE_ENFORCED is not true), so this release has not been proven against a real domain controller.'
        }
        @{
            Context            = 'jim-ad-lab-adfs'
            Name               = 'Active Directory lab AD FS gate'
            Workflow           = 'ad-lab.yml'
            EnforcedVariable   = 'JIM_AD_LAB_ADFS_GATE_ENFORCED'
            Remedy             = 'There is no override: fix the cause, then dispatch ad-lab.yml so it runs on {0} (gh workflow run ad-lab.yml --ref main, while {0} is the head of main), wait for it to finish, and check again.'
            NotReportedReason  = 'the Active Directory lab''s AD FS sign-in checks have not run on this exact commit. The nightly run tests the head of main as it stood then, so a commit made since has none.'
            NotEnforcedMessage = 'The Active Directory lab AD FS gate is not enforced yet (JIM_AD_LAB_ADFS_GATE_ENFORCED is not true), so this release has not been proven to sign people in through a real AD FS.'
        }
        @{
            Context            = 'jim-sso-entra'
            Name               = 'Entra ID sign-in gate'
            Workflow           = 'ad-lab.yml'
            EnforcedVariable   = 'JIM_SSO_ENTRA_GATE_ENFORCED'
            Remedy             = 'There is no override: fix the cause, then dispatch ad-lab.yml so it runs on {0} (gh workflow run ad-lab.yml --ref main -f scenario=35, while {0} is the head of main), wait for it to finish, and check again. A red run can be an Entra ID or test tenant outage: the run says which.'
            NotReportedReason  = 'the Active Directory lab''s Entra ID sign-in checks have not run on this exact commit. The nightly run tests the head of main as it stood then, so a commit made since has none.'
            NotEnforcedMessage = 'The Entra ID sign-in gate is not enforced yet (JIM_SSO_ENTRA_GATE_ENFORCED is not true), so this release has not been proven to sign people in through Microsoft Entra ID.'
        }
        @{
            Context            = 'jim-pre-release'
            Name               = 'Pre-release integration gate'
            Workflow           = 'pre-release.yml'
            EnforcedVariable   = 'JIM_PRE_RELEASE_GATE_ENFORCED'
            Remedy             = 'There is no override: fix the cause, then run pre-release.yml on {0} (merging the release pull request starts it; otherwise gh workflow run pre-release.yml --ref main, while {0} is the head of main), wait for it to finish (about two hours), and check again.'
            NotReportedReason  = 'the pre-release integration suite has not run on this exact commit. It starts by itself when a change to VERSION reaches main, or on demand, so a commit that was neither has none.'
            NotEnforcedMessage = 'The pre-release integration gate is not enforced yet (JIM_PRE_RELEASE_GATE_ENFORCED is not true), so this release has not been proven by the full integration suite.'
        }
        @{
            Context            = 'jim-deploy-lab'
            Name               = 'Deployment lab gate'
            Workflow           = 'deploy-lab.yml'
            EnforcedVariable   = 'JIM_DEPLOY_LAB_GATE_ENFORCED'
            Remedy             = 'There is no override: fix the cause, then run deploy-lab.yml on {0} (merging the release pull request starts it; otherwise gh workflow run deploy-lab.yml --ref main, while {0} is the head of main), wait for it to finish (about 65 minutes), and check again.'
            NotReportedReason  = 'the deployment lab has not run on this exact commit. It starts by itself nightly on main and when a change to VERSION reaches main, or on demand, so a commit that was none of these has none.'
            NotEnforcedMessage = 'The deployment lab gate is not enforced yet (JIM_DEPLOY_LAB_GATE_ENFORCED is not true), so this release has not been proven by installing and upgrading it on fresh air-gapped hosts.'
        }
    )
}
