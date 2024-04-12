using JIM.Models.Enums;
namespace JIM.Models.Utility;

public class PagedResultSet<T>
{
    #region accessors
    public List<T> Results { get; set; } = new();
    public virtual int TotalResults { get; set; }
    public int PageSize { get; set; }
    public int CurrentPage { get; set; }
    public virtual int TotalPages
    {
        get
        {
            var pages = (double)TotalResults / (double)PageSize;
            var roundedUpDecimal = Math.Ceiling(pages);
            var roundedUpInt = Convert.ToInt32(roundedUpDecimal);

            // if we have some items but less than a page size then return one page, not zero
            if (TotalResults > 0 && roundedUpInt < 1)
                roundedUpInt = 1;

            return roundedUpInt;
        }
    }
    public int MaximumResults { get; set; }
    public QuerySortBy QuerySortBy { get; set; }
    public QueryRange QueryRange { get; set; }
    #endregion

    #region methods
    public int[] GetNavigationPageNumbers(int pagesToShow)
    {
        var numbers = new int[pagesToShow];

        // if we're halfway or less from the middle of the range in relation to the overall page list then just list from page 1 and up
        var middleOfRangeDouble = (double)pagesToShow / (double)2;
        var middleOfRange = Math.Ceiling(middleOfRangeDouble);
        if (CurrentPage <= middleOfRange)
        {
            // we're near the start, anchor on the start
            for (var i = 0; i < pagesToShow; i++)
                numbers[i] = i + 1;

            return numbers;
        }

        // if we're halfway or less to the end of the range in relation to the overall page list then just list from the last page minus the range
        var pagesFromEnd = TotalPages - CurrentPage;
        if (pagesFromEnd <= middleOfRange)
        {
            // we're near the end of pages, anchor on the last page
            var page = TotalPages - pagesToShow + 1;
            for (var i = 0; i < pagesToShow; i++)
            {
                numbers[i] = page;
                page++;
            }

            return numbers;
        }

        // otherwise anchor the current page on the middle of the range of pages we want to show
        var positionDouble = (double)pagesToShow / (double)2;
        var startNumber = Convert.ToInt32(CurrentPage - positionDouble);
        if (startNumber == 0)
            startNumber = 1;

        for (var i = 0; i < pagesToShow; i++)
        {
            numbers[i] = startNumber;
            startNumber++;
        }

        return numbers;
    }

    public string BuildTagQueryString(QueryRange queryRange)
    {
        var query = "?";

        if (CurrentPage > 1)
            query += "p=" + CurrentPage + "&";

        if (QuerySortBy != QuerySortBy.DateCreated)
            query += "s=" + QuerySortBy.ToString().ToLower() + "&";

        query += "r=" + queryRange.ToString().ToLower();
        return query;
    }

    public string BuildTagQueryString(QuerySortBy querySortBy)
    {
        var query = "?";

        if (CurrentPage > 1)
            query += "p=" + CurrentPage + "&";

        query += "s=" + querySortBy.ToString().ToLower();

        if (QueryRange != QueryRange.Forever)
            query += "&r=" + QueryRange.ToString().ToLower();

        return query;
    }

    public string BuildTagQueryString(int pageNumber, string currentPath)
    {
        var query = "?";
        var paramz = new List<string>();

        if (pageNumber > 1)
            paramz.Add("p=" + pageNumber);

        if (QuerySortBy != QuerySortBy.DateCreated)
            paramz.Add("s=" + QuerySortBy.ToString().ToLower());

        if (QueryRange != QueryRange.Forever)
            paramz.Add("r=" + QueryRange.ToString().ToLower());

        if (paramz.Count == 0)
            return currentPath;

        query += string.Join('&', paramz);
        query = currentPath + query;
        return query;
    }
    #endregion
}