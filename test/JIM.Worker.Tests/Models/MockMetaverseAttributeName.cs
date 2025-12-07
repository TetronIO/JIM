namespace JIM.Worker.Tests.Models;

/// <summary>
/// This is used to give a standard set of ids to metaverse attributes in unit-tests.
/// </summary>
public enum MockMetaverseAttributeName
{
    HrId = 1, // guid
    EmployeeId = 2, // string
    EmployeeNumber = 3, // int
    DisplayName = 4, // string
    Email = 5, // text
    EmployeeStartDate = 6, // datetime
    EmployeeEndDate = 7, // datetime
    Manager = 8, // reference
    LocationId = 9, // guid
    Photo = 10, // binary
    AccountName = 11, // text
    Member = 12 // reference
}
