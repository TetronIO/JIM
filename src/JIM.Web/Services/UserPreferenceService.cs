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

    /// <summary>
    /// Gets the expanded state for a navigation menu group.
    /// </summary>
    /// <param name="groupName">The name of the nav group (e.g., "users", "groups", "admin").</param>
    /// <returns>True if the group was previously expanded, false otherwise.</returns>
    Task<bool> GetNavGroupExpandedAsync(string groupName);

    /// <summary>
    /// Sets the expanded state for a navigation menu group.
    /// </summary>
    /// <param name="groupName">The name of the nav group (e.g., "users", "groups", "admin").</param>
    /// <param name="expanded">Whether the group is expanded.</param>
    Task SetNavGroupExpandedAsync(string groupName, bool expanded);

    /// <summary>
    /// Gets the user's dark mode preference.
    /// </summary>
    /// <returns>True if the user chose dark mode, false if light mode, null if no preference saved (use system default).</returns>
    Task<bool?> GetDarkModeAsync();

    /// <summary>
    /// Sets the user's dark mode preference.
    /// </summary>
    /// <param name="isDarkMode">Whether dark mode is enabled.</param>
    Task SetDarkModeAsync(bool isDarkMode);
}

/// <summary>
/// Implementation of <see cref="IUserPreferenceService"/> using browser localStorage via JavaScript interop.
/// </summary>
public class UserPreferenceService : IUserPreferenceService
{
    private readonly IJSRuntime _jsRuntime;
    private const string RowsPerPageKey = "rowsPerPage";
    private const string DarkModeKey = "darkMode";
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

    /// <inheritdoc />
    public async Task<bool> GetNavGroupExpandedAsync(string groupName)
    {
        if (string.IsNullOrEmpty(groupName))
            return false;

        try
        {
            var key = $"navGroup_{groupName}";
            var value = await _jsRuntime.InvokeAsync<string?>("jimPreferences.get", key);
            return value == "true";
        }
        catch (JSDisconnectedException)
        {
            // Circuit disconnected, return default
        }
        catch (InvalidOperationException)
        {
            // JS interop not available (e.g., during prerendering), return default
        }

        return false;
    }

    /// <inheritdoc />
    public async Task SetNavGroupExpandedAsync(string groupName, bool expanded)
    {
        if (string.IsNullOrEmpty(groupName))
            return;

        try
        {
            var key = $"navGroup_{groupName}";
            await _jsRuntime.InvokeVoidAsync("jimPreferences.set", key, expanded ? "true" : "false");
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

    /// <inheritdoc />
    public async Task<bool?> GetDarkModeAsync()
    {
        try
        {
            var value = await _jsRuntime.InvokeAsync<string?>("jimPreferences.get", DarkModeKey);
            return value switch
            {
                "true" => true,
                "false" => false,
                _ => null // No preference saved - use system default
            };
        }
        catch (JSDisconnectedException)
        {
            // Circuit disconnected, return default
        }
        catch (InvalidOperationException)
        {
            // JS interop not available (e.g., during prerendering), return default
        }

        return null;
    }

    /// <inheritdoc />
    public async Task SetDarkModeAsync(bool isDarkMode)
    {
        try
        {
            await _jsRuntime.InvokeVoidAsync("jimPreferences.set", DarkModeKey, isDarkMode ? "true" : "false");
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
