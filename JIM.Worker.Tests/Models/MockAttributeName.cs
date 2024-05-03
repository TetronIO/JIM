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
    PROFILE_PICTURE_BYTES = 9, // binary
    CONTRACTED_WEEKLY_HOURS = 10, // int
    LOCATION_ID = 11, // guid,
    END_DATE = 12, // datetime,
    LEAVER = 13, // boolean
    COURSE_COUNT = 14, // int
    COURSE_END_DATE = 15, // datetime
    CURRENT_COURSE_NAME = 16, // string
    CURRENT_COURSE_ID = 17, // guid
    CURRENT_COURSE_ACTIVE = 18, // boolean
    CURRENT_COURSE_TUTOR = 19 // reference
}