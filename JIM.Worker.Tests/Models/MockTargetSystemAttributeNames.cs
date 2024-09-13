﻿namespace JIM.Worker.Tests.Models;

/// <summary>
/// Mimicking an Active Directory forest.
/// </summary>
public enum MockTargetSystemAttributeNames
{
    ObjectGuid = 1,
    ObjectSid = 2,
    SamAccountName = 3,
    DisplayName = 4,
    Mail = 5,
    Manager = 6,
    JobTitle = 7,
    EmployeeId = 10,
    UserAccountControl = 11,
    UserPrincipalName = 12
}