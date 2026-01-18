using Microsoft.JSInterop;

namespace JIM.Web.Services;

/// <summary>
/// Service for managing user preferences stored in browser localStorage.
/// </summary>
public interface IUserPreferenceService
{
    /// <summary>
    /// Gets the user's preferred rows per page setting for pagination controls.
    /// </summary>
    /// <returns>The preferred page size, or the default (10) if not set or invalid.</returns>
    Task<int> GetRowsPerPageAsync();

    /// <summary>
    /// Sets the user's preferred rows per page setting for pagination controls.
    /// </summary>
    /// <param name="rowsPerPage">The page size to store. Must be a valid option (10, 25, 50, or 100).</param>
    Task SetRowsPerPageAsync(int rowsPerPage);
}

/// <summary>
/// Implementation of <see cref="IUserPreferenceService"/> using browser localStorage via JavaScript interop.
/// </summary>
public class UserPreferenceService : IUserPreferenceService
{
    private readonly IJSRuntime _jsRuntime;
    private const string RowsPerPageKey = "rowsPerPage";
    private const int DefaultRowsPerPage = 10;

    /// <summary>
    /// Valid page sizes that match MudBlazor's MudTablePager default options.
    /// </summary>
    private static readonly int[] ValidPageSizes = [10, 25, 50, 100];

    public UserPreferenceService(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime ?? throw new ArgumentNullException(nameof(jsRuntime));
    }

    /// <inheritdoc />
    public async Task<int> GetRowsPerPageAsync()
    {
        try
        {
            var value = await _jsRuntime.InvokeAsync<string?>("jimPreferences.get", RowsPerPageKey);
            if (int.TryParse(value, out var rowsPerPage) && ValidPageSizes.Contains(rowsPerPage))
            {
                return rowsPerPage;
            }
        }
        catch (JSDisconnectedException)
        {
            // Circuit disconnected, return default
        }
        catch (InvalidOperationException)
        {
            // JS interop not available (e.g., during prerendering), return default
        }

        return DefaultRowsPerPage;
    }

    /// <inheritdoc />
    public async Task SetRowsPerPageAsync(int rowsPerPage)
    {
        if (!ValidPageSizes.Contains(rowsPerPage))
            return; // Ignore invalid values

        try
        {
            await _jsRuntime.InvokeVoidAsync("jimPreferences.set", RowsPerPageKey, rowsPerPage.ToString());
        }
        catch (JSDisconnectedException)
        {
            // Circuit disconnected, ignore
        }
        catch (InvalidOperationException)
        {
            // JS interop not available (e.g., during prerendering), ignore
        }
    }
}
