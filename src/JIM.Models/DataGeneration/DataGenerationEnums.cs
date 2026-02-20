namespace JIM.Models.DataGeneration;

/// <summary>
/// Defines the type of comparison to make.
/// Used with DataGenerationTemplateDependency objects.
/// </summary>
public enum ComparisonType
{
    Equals = 0,
    NotEquals = 1,
    LessThan = 2,
    GreaterThan = 3,
    GreaterThanOrEqual = 4,
    LessThanOrEqual = 5,
    Like = 6
}