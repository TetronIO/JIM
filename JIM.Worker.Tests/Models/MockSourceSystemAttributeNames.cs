namespace JIM.Worker.Tests.Models;

/// <summary>
/// Mimicking a HR system that contains staff and students.
/// </summary>
public enum MockSourceSystemAttributeNames
{
    EMPLOYEE_ID = 1, // string
    EMPLOYEE_NUMBER = 2, // int
    DISPLAY_NAME = 3, // string
    EMAIL_ADDRESS = 4, // string -- TODO: move this to the AD target system mock
    ROLE = 5, // string
    MANAGER = 6, // reference
    QUALIFICATIONS = 7, // text, multivalued
    START_DATE = 8, // datetime
    HR_ID = 9, // guid
    PROFILE_PICTURE_BYTES = 10, // binary
    CONTRACTED_WEEKLY_HOURS = 11, // int
    LOCATION_ID = 12, // guid,
    END_DATE = 13, // datetime,
    LEAVER = 14, // boolean
    COURSE_COUNT = 15, // int
    COURSE_END_DATE = 16, // datetime
    CURRENT_COURSE_NAME = 17, // string
    CURRENT_COURSE_ID = 18, // guid
    CURRENT_COURSE_ACTIVE = 19, // boolean
    CURRENT_COURSE_TUTOR = 20, // reference
    PROXY_ADDRESSES = 21, // mva string -- TODO: move this to the AD target system mock
    COMPLETED_COURSE_IDS = 22, // mva int
    PREVIOUS_LOCATION_IDS = 23, // mva guids
    CERTIFICATES = 24, // mva byte -- TODO: move this to the AD target system mock
    GROUP_UID = 25, // sva guid
    MEMBER = 26, // mva reference
    EMPLOYEE_TYPE = 27 // string
}