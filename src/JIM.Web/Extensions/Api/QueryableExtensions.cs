using System.Linq.Expressions;
using System.Reflection;
using JIM.Web.Models.Api;
using Microsoft.EntityFrameworkCore;

namespace JIM.Web.Extensions.Api;

/// <summary>
/// Extension methods for IQueryable to support pagination, sorting, and filtering.
/// </summary>
public static class QueryableExtensions
{
    /// <summary>
    /// Applies pagination to a query and returns a paginated response.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="query">The queryable to paginate.</param>
    /// <param name="request">The pagination parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A paginated response containing the items and metadata.</returns>
    public static async Task<PaginatedResponse<T>> ToPaginatedResponseAsync<T>(
        this IQueryable<T> query,
        PaginationRequest request,
        CancellationToken cancellationToken = default)
    {
        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .Skip(request.Skip)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);

        return PaginatedResponse<T>.Create(items, totalCount, request.Page, request.PageSize);
    }

    /// <summary>
    /// Applies sorting to a query based on a property name.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="query">The queryable to sort.</param>
    /// <param name="propertyName">The property name to sort by.</param>
    /// <param name="descending">Whether to sort descending.</param>
    /// <returns>The sorted queryable.</returns>
    public static IQueryable<T> ApplySort<T>(
        this IQueryable<T> query,
        string? propertyName,
        bool descending = false)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
            return query;

        var property = typeof(T).GetProperty(
            propertyName,
            BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);

        if (property == null)
            return query; // Property not found, return unsorted

        var parameter = Expression.Parameter(typeof(T), "x");
        var propertyAccess = Expression.MakeMemberAccess(parameter, property);
        var orderByExpression = Expression.Lambda(propertyAccess, parameter);

        var methodName = descending ? "OrderByDescending" : "OrderBy";

        var resultExpression = Expression.Call(
            typeof(Queryable),
            methodName,
            [typeof(T), property.PropertyType],
            query.Expression,
            Expression.Quote(orderByExpression));

        return query.Provider.CreateQuery<T>(resultExpression);
    }

    /// <summary>
    /// Applies a filter to a query based on a filter string.
    /// Filter format: "property:operator:value"
    /// Supported operators: eq, ne, contains, startswith, endswith
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="query">The queryable to filter.</param>
    /// <param name="filter">The filter string.</param>
    /// <returns>The filtered queryable.</returns>
    public static IQueryable<T> ApplyFilter<T>(this IQueryable<T> query, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return query;

        var parts = filter.Split(':', 3);
        if (parts.Length < 3)
            return query; // Invalid filter format

        var propertyName = parts[0];
        var operatorName = parts[1].ToLowerInvariant();
        var value = parts[2];

        var property = typeof(T).GetProperty(
            propertyName,
            BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);

        if (property == null)
            return query; // Property not found

        var parameter = Expression.Parameter(typeof(T), "x");
        var propertyAccess = Expression.MakeMemberAccess(parameter, property);

        Expression? comparison = null;

        if (property.PropertyType == typeof(string))
        {
            var constant = Expression.Constant(value);

            comparison = operatorName switch
            {
                "eq" => Expression.Equal(propertyAccess, constant),
                "ne" => Expression.NotEqual(propertyAccess, constant),
                "contains" => Expression.Call(propertyAccess,
                    typeof(string).GetMethod("Contains", new[] { typeof(string) })!,
                    constant),
                "startswith" => Expression.Call(propertyAccess,
                    typeof(string).GetMethod("StartsWith", new[] { typeof(string) })!,
                    constant),
                "endswith" => Expression.Call(propertyAccess,
                    typeof(string).GetMethod("EndsWith", new[] { typeof(string) })!,
                    constant),
                _ => null
            };
        }
        else if (property.PropertyType == typeof(int) || property.PropertyType == typeof(int?))
        {
            if (int.TryParse(value, out var intValue))
            {
                var constant = Expression.Constant(intValue, property.PropertyType);
                comparison = operatorName switch
                {
                    "eq" => Expression.Equal(propertyAccess, constant),
                    "ne" => Expression.NotEqual(propertyAccess, constant),
                    "gt" => Expression.GreaterThan(propertyAccess, constant),
                    "gte" => Expression.GreaterThanOrEqual(propertyAccess, constant),
                    "lt" => Expression.LessThan(propertyAccess, constant),
                    "lte" => Expression.LessThanOrEqual(propertyAccess, constant),
                    _ => null
                };
            }
        }
        else if (property.PropertyType == typeof(bool) || property.PropertyType == typeof(bool?))
        {
            if (bool.TryParse(value, out var boolValue))
            {
                var constant = Expression.Constant(boolValue, property.PropertyType);
                comparison = operatorName switch
                {
                    "eq" => Expression.Equal(propertyAccess, constant),
                    "ne" => Expression.NotEqual(propertyAccess, constant),
                    _ => null
                };
            }
        }
        else if (property.PropertyType == typeof(Guid) || property.PropertyType == typeof(Guid?))
        {
            if (Guid.TryParse(value, out var guidValue))
            {
                var constant = Expression.Constant(guidValue, property.PropertyType);
                comparison = operatorName switch
                {
                    "eq" => Expression.Equal(propertyAccess, constant),
                    "ne" => Expression.NotEqual(propertyAccess, constant),
                    _ => null
                };
            }
        }

        if (comparison == null)
            return query; // Unsupported operator or type

        var lambda = Expression.Lambda<Func<T, bool>>(comparison, parameter);
        return query.Where(lambda);
    }

    /// <summary>
    /// Applies sorting and filtering from a pagination request.
    /// </summary>
    public static IQueryable<T> ApplySortAndFilter<T>(
        this IQueryable<T> query,
        PaginationRequest request)
    {
        return query
            .ApplyFilter(request.Filter)
            .ApplySort(request.SortBy, request.IsDescending);
    }

    /// <summary>
    /// Creates a paginated response from an in-memory queryable (synchronous version).
    /// Use this for collections that are already loaded in memory.
    /// </summary>
    public static PaginatedResponse<T> ToPaginatedResponse<T>(
        this IQueryable<T> query,
        PaginationRequest request)
    {
        var totalCount = query.Count();

        var items = query
            .Skip(request.Skip)
            .Take(request.PageSize)
            .ToList();

        return PaginatedResponse<T>.Create(items, totalCount, request.Page, request.PageSize);
    }
}
