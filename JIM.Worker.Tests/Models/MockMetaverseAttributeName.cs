namespace JIM.Worker.Tests.Models;

/// <summary>
/// This is used to give a standard set of ids to metaverse attributes in unit-tests.
/// </summary>
public enum MockMetaverseAttributeName
{
    EmployeeId = 1, // int
    DisplayName = 2, // string
    Email = 3, // text
    EmployeeStartDate = 4, // datetime
    EmployeeEndDate = 5, // datetime
    Manager = 6, // reference
    LocationId = 7, // guid
    Photo = 8, // binary
    AccountName = 9, // text
    Member = 10 // reference
}
