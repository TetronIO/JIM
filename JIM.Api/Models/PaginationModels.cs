using System.ComponentModel.DataAnnotations;

namespace JIM.Api.Models;

/// <summary>
/// Standard pagination parameters for list endpoints.
/// </summary>
public class PaginationRequest
{
    private int _page = 1;
    private int _pageSize = 25;

    /// <summary>
    /// The page number (1-based). Defaults to 1.
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "Page must be at least 1.")]
    public int Page
    {
        get => _page;
        set => _page = value < 1 ? 1 : value;
    }

    /// <summary>
    /// The number of items per page. Defaults to 25, max 100.
    /// </summary>
    [Range(1, 100, ErrorMessage = "Page size must be between 1 and 100.")]
    public int PageSize
    {
        get => _pageSize;
        set => _pageSize = value < 1 ? 25 : (value > 100 ? 100 : value);
    }

    /// <summary>
    /// The property name to sort by.
    /// </summary>
    [StringLength(100, ErrorMessage = "Sort property name must not exceed 100 characters.")]
    public string? SortBy { get; set; }

    /// <summary>
    /// Sort direction: "asc" or "desc". Defaults to "asc".
    /// </summary>
    [RegularExpression("^(asc|desc)$", ErrorMessage = "Sort direction must be 'asc' or 'desc'.")]
    public string SortDirection { get; set; } = "asc";

    /// <summary>
    /// Optional filter string in format "property:operator:value".
    /// Supported operators: eq, ne, contains, startswith, endswith.
    /// Example: "name:contains:test" or "status:eq:active"
    /// </summary>
    [StringLength(500, ErrorMessage = "Filter string must not exceed 500 characters.")]
    public string? Filter { get; set; }

    /// <summary>
    /// Calculates the number of items to skip based on page and page size.
    /// </summary>
    public int Skip => (Page - 1) * PageSize;

    /// <summary>
    /// Whether sorting is descending.
    /// </summary>
    public bool IsDescending => SortDirection.Equals("desc", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Standard paginated response wrapper for list endpoints.
/// </summary>
/// <typeparam name="T">The type of items in the response.</typeparam>
public class PaginatedResponse<T>
{
    /// <summary>
    /// The items for the current page.
    /// </summary>
    public IEnumerable<T> Items { get; set; } = Enumerable.Empty<T>();

    /// <summary>
    /// The total number of items across all pages.
    /// </summary>
    public int TotalCount { get; set; }

    /// <summary>
    /// The current page number (1-based).
    /// </summary>
    public int Page { get; set; }

    /// <summary>
    /// The number of items per page.
    /// </summary>
    public int PageSize { get; set; }

    /// <summary>
    /// The total number of pages.
    /// </summary>
    public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);

    /// <summary>
    /// Whether there are more pages after the current one.
    /// </summary>
    public bool HasNextPage => Page < TotalPages;

    /// <summary>
    /// Whether there are pages before the current one.
    /// </summary>
    public bool HasPreviousPage => Page > 1;

    /// <summary>
    /// Creates a paginated response from a collection.
    /// </summary>
    public static PaginatedResponse<T> Create(IEnumerable<T> items, int totalCount, int page, int pageSize)
    {
        return new PaginatedResponse<T>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }
}
