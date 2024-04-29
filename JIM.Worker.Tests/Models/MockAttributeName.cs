namespace JIM.Worker.Tests.Models;

public enum MockAttributeName
{
    EMPLOYEE_ID = 1, // int
    DISPLAY_NAME = 2, // string
    EMAIL_ADDRESS = 3, // string
    ROLE = 4, // string
    MANAGER = 5, // reference
    QUALIFICATIONS = 6, // text, multivalued
    START_DATE = 7, // datetime
    HR_ID = 8, // guid
    PROFILE_PICTURE_BYTES = 9 // binary
}