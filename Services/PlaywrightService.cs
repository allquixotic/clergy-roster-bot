using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using System.Threading.Tasks;

namespace ClergyRosterBot.Services;

public class PlaywrightService : IAsyncDisposable
{
    private readonly ILogger<PlaywrightService> _logger;
    private readonly BotSettings _settings;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IPage? _page;
    private bool _isEditorOpen = false;

    // Constants for selectors (replace with actual C# constants later)
    private const string LOCATOR_ALERT_DANGER = ".alert.alert-danger";
    private const string LOCATOR_GUILDTAG_LOGIN_BUTTON = "button";
    private const string LOCATOR_GUILDTAG_LOGIN_BUTTON_TEXT = "Login with your Guildtag Account";
    private const string LOCATOR_LABEL_EMAIL = "label:has-text(\"Email\")";
    private const string LOCATOR_LABEL_PASSWORD = "label:has-text(\"Password\")";
    private const string LOCATOR_LOGIN_BUTTON = ".widget-login .card-footer button.btn-primary";
    private const string LOCATOR_LOGIN_BUTTON_TEXT = "/^Login\\s*$/"; // Regex needs C# handling
    private const string LOCATOR_LOGIN_FORM = "form[action*=\"login\"]";
    private const string LOCATOR_ERROR_VISIBLE = ".error:visible, .message.error:visible, [data-message-type=\"error\"]:visible";
    private const string LOCATOR_EDIT_LINK = "a:has-text(\"Edit\"):not(:has-text(\"Edit Thread\")), button:has-text(\"Edit\"):not(:has-text(\"Edit Thread\")), a[data-action=\"edit\"]:not(:has-text(\"Edit Thread\"))";
    private const string LOCATOR_EDITOR_TEXTAREA = "#compose-container > div:nth-child(2) > div.row > div > div > div.form-group > textarea"; // May need adjustment
    private const string LOCATOR_FORM_TEXTAREA = "textarea.form-text.form-control";
    private const string LOCATOR_SAVE_BUTTON = "button.btn-primary.bk:has-text(\"Save Edit\")";
    private const string LOCATOR_INPUT_AFTER_LABEL = " + input, input"; // Complex, review

    public PlaywrightService(ILogger<PlaywrightService> logger, IOptions<BotSettings> settings)
    {
        _logger = logger;
        _settings = settings.Value;
    }

    private async Task EnsureInitializedAsync()
    {
        if (_browser != null && _page != null && !_page.IsClosed) return; // Already initialized

        _logger.LogInformation("Initializing Playwright...");
        _playwright = await Playwright.CreateAsync();
        _logger.LogInformation("Launching Chromium browser...");
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true // Use true for server environments
        });
        _page = await _browser.NewPageAsync();
        _page.SetDefaultTimeout(30000); // Set default timeout
        _logger.LogInformation("Playwright initialized successfully.");
    }

    public async Task<string> GetForumPostContentAndOpenEditorAsync()
    {
        await EnsureInitializedAsync();
        if (_page == null) throw new InvalidOperationException("Playwright page is not initialized.");

        await EnsureLoggedInAndNavigatedAsync(_settings.GuildtagForumUrl);

        _logger.LogInformation("Clicking 'Edit' link/button...");
        var editLink = _page.Locator(LOCATOR_EDIT_LINK).First;
        await AwaitVisibleAndClickAsync(editLink, 10000,
            "Could not find or click the 'Edit' link/button. Check selector and permissions.");

        _logger.LogInformation("Waiting for editor textarea...");
        var textarea = _page.Locator(LOCATOR_EDITOR_TEXTAREA).First; // Might need refinement
        await AwaitVisibleWithErrorAsync(textarea, 15000,
            "Editor textarea did not appear. Check editor loading behavior.");

        _logger.LogInformation("Extracting content from textarea...");
        await Task.Delay(100); // Small delay
        string content = await textarea.InputValueAsync(new LocatorInputValueOptions { Timeout = 10000 });

        _isEditorOpen = true;
        _logger.LogDebug("Extracted {Length} characters from textarea.", content?.Length ?? 0);
        return content ?? string.Empty;
    }

    public async Task UpdateForumPostAsync(string newContent)
    {
        if (!_isEditorOpen || _page == null)
        {
            throw new InvalidOperationException("Editor is not open. Call GetForumPostContentAndOpenEditorAsync first.");
        }

        _logger.LogInformation("Updating textarea content...");
        var textarea = _page.Locator(LOCATOR_FORM_TEXTAREA).First;
        await AwaitVisibleAndFillAsync(textarea, newContent, 10000,
            "Textarea element disappeared before update.");
        _logger.LogDebug("Filled textarea with {Length} characters.", newContent.Length);

        _logger.LogInformation("Clicking 'Save Edit' button...");
        var saveButton = _page.Locator(LOCATOR_SAVE_BUTTON).First;
        await AwaitVisibleAndClickAsync(saveButton, 5000,
            "Could not find or click the 'Save Edit' button. Check selector.");

        _logger.LogInformation("Waiting for save confirmation...");
        try
        {
            // Wait for URL to change away from edit mode
            await _page.WaitForURLAsync(url => !url.Contains("do=editpost") && !url.Contains("action=edit"),
                                        new PageWaitForURLOptions { Timeout = 20000 });
            _logger.LogInformation("Edit saved successfully. Current URL: {Url}", _page.Url);
            _isEditorOpen = false;
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "Timeout waiting for URL change after save. Edit might have saved, but confirmation unclear.");
            // Check for error messages
            var saveError = _page.Locator(LOCATOR_ERROR_VISIBLE);
            if (await saveError.CountAsync() > 0)
            {
                string errorText = await saveError.First.TextContentAsync() ?? "Unknown error";
                _logger.LogError("Save failed with error message: {ErrorText}", errorText);
                // Keep editor state open?
                throw new Exception($"Save failed: {errorText}");
            }
            _isEditorOpen = false; // Assume saved if no explicit error
        }
    }

    private async Task EnsureLoggedInAndNavigatedAsync(string targetUrl)
    {
        await EnsureInitializedAsync();
        if (_page == null) throw new InvalidOperationException("Playwright page is not initialized.");

        const int maxRetries = 2;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            _logger.LogInformation("Navigating to forum thread: {TargetUrl} (Attempt {Attempt})", targetUrl, attempt);
            try
            {
                await _page.GotoAsync(targetUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

                var permissionErrorLocator = _page.Locator(LOCATOR_ALERT_DANGER)
                    .Filter(new LocatorFilterOptions { HasTextString = "Error loading data: You do not have permission to view this" });

                _logger.LogDebug("Checking for permission error element visibility...");
                bool needsLogin = await WaitForVisibleAsync(permissionErrorLocator, timeout: 10000, shouldThrow: false);

                if (needsLogin)
                {
                    _logger.LogInformation("Permission error detected or timeout waiting for content. Attempting login...");
                    await PerformLoginAsync();

                    _logger.LogInformation("Re-navigating to target URL after login: {TargetUrl}", targetUrl);
                    await _page.GotoAsync(targetUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

                    // Verify login by checking for permission error again (quick check)
                    bool permissionErrorAfterLogin = await WaitForVisibleAsync(permissionErrorLocator, timeout: 5000, shouldThrow: false);
                    if (permissionErrorAfterLogin)
                    {
                        throw new Exception("Login appeared successful, but still lack permission to view the thread.");
                    }
                    _logger.LogInformation("Navigation successful after login.");
                    return; // Logged in and navigated successfully
                }
                else
                {
                    _logger.LogInformation("Permission error element not found within timeout. Assuming logged in and permitted.");
                    _logger.LogInformation("Forum thread loaded successfully.");
                    return; // Already logged in or public thread
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Navigation/Login attempt {Attempt} failed.", attempt);
                if (attempt == maxRetries)
                {
                    throw new Exception($"Failed to navigate and ensure login after {maxRetries} attempts: {ex.Message}", ex);
                }
                await Task.Delay(1000); // Wait before retry
            }
        }
    }

    private async Task PerformLoginAsync()
    {
        if (_page == null) throw new InvalidOperationException("Playwright page is not initialized.");

        var forumUrl = new Uri(_settings.GuildtagForumUrl);
        var loginUrl = new Uri(forumUrl, "/login/").ToString();

        _logger.LogInformation("Navigating to login page: {LoginUrl}", loginUrl);
        await _page.GotoAsync(loginUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        _logger.LogInformation("Clicking 'Login with your Guildtag Account' button (if exists)...");
        var guildtagLoginButton = _page.Locator(LOCATOR_GUILDTAG_LOGIN_BUTTON)
            .Filter(new LocatorFilterOptions { HasTextString = LOCATOR_GUILDTAG_LOGIN_BUTTON_TEXT }).First;
        await AwaitVisibleAndClickAsync(guildtagLoginButton, 5000,
            "Guildtag login button not found or failed to click.");

        await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        _logger.LogInformation("Filling login credentials...");
        // Find the input either immediately following the label or nested within it.
        await _page.Locator(LOCATOR_LABEL_EMAIL).Locator(" + input, input").First.FillAsync(_settings.GuildtagEmail);
        await _page.Locator(LOCATOR_LABEL_PASSWORD).Locator(" + input, input").First.FillAsync(_settings.GuildtagPassword);

        _logger.LogInformation("Clicking Login button...");
        var loginButton = _page.Locator(LOCATOR_LOGIN_BUTTON, new PageLocatorOptions { HasTextRegex = new System.Text.RegularExpressions.Regex("^Login\\s*$") });
        await AwaitVisibleAndClickAsync(loginButton, 5000,
            "Login button not found or not clickable. Fatal error during login process.");

        _logger.LogInformation("Waiting for navigation/confirmation after login...");
        try
        {
            await _page.WaitForURLAsync(url => !url.Contains("/login"), new PageWaitForURLOptions { Timeout = 15000 });

            // Optional: Check for absence of login form
            bool stillOnLogin = await WaitForVisibleAsync(_page.Locator(LOCATOR_LOGIN_FORM), 5000, false);
            if (stillOnLogin)
            {
                 _logger.LogError("Still on login form after attempting login. Login failed.");
                 throw new Exception("Login failed: Still on login form after submitting credentials.");
            }

            // Optional: Check for permission error right after login
            var permissionErrorLocator = _page.Locator(LOCATOR_ALERT_DANGER)
                    .Filter(new LocatorFilterOptions { HasTextString = "Error loading data: You do not have permission to view this" });
            if(await WaitForVisibleAsync(permissionErrorLocator, 2000, false))
            {
                _logger.LogError("Permission error still present after login. Login failed.");
                throw new Exception("Login failed: Permission error still present after login.");
            }

            _logger.LogInformation("Login successful. Current URL: {Url}", _page.Url);
        }
        catch (Exception e)
        {
            // Check for explicit login error messages
            var loginError = _page.Locator(LOCATOR_ERROR_VISIBLE);
            if (await loginError.CountAsync() > 0)
            {
                string errorText = await loginError.First.TextContentAsync() ?? "Unknown error";
                _logger.LogError("Login failed with error: {ErrorText}", errorText);
                throw new Exception($"Login failed: {errorText}", e);
            }
            else
            {
                _logger.LogError(e, "Timeout or unknown error after attempting login. Automation will stop.");
                throw new Exception("Login failed: Timeout or unknown error after attempting login.", e);
            }
        }
    }

    // --- Helper Methods for Locators (similar to JS) ---
    private async Task<bool> WaitForVisibleAsync(ILocator locator, int timeout = 5000, bool shouldThrow = true, string? errorMessage = null)
    {
        try
        {
            await locator.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = timeout });
            return true;
        }
        catch (TimeoutException ex)
        {
            if (shouldThrow)
            {
                string msg = errorMessage ?? $"Element did not become visible within {timeout}ms.";
                _logger.LogError(ex, msg + " Selector: " + locator.ToString()); // Log selector
                throw new TimeoutException(msg, ex);
            }
            else
            {
                return false;
            }
        }
        catch (Exception ex)
        {
             _logger.LogError(ex, errorMessage ?? "Unexpected error waiting for element visibility." + " Selector: " + locator.ToString());
             throw; // Re-throw unexpected errors
        }
    }

    private async Task AwaitVisibleAndClickAsync(ILocator locator, int timeout = 5000, string? errorMessage = "Failed to click element")
    {
        await WaitForVisibleAsync(locator, timeout, true, errorMessage);
        try
        {
             await locator.ClickAsync(new LocatorClickOptions { Timeout = timeout }); // Use timeout for click too
        }
         catch (Exception ex)
        {
            _logger.LogError(ex, errorMessage + " Selector: " + locator.ToString());
            throw new Exception(errorMessage, ex);
        }
    }

     private async Task AwaitVisibleAndFillAsync(ILocator locator, string value, int timeout = 5000, string? errorMessage = "Failed to fill element")
    {
        await WaitForVisibleAsync(locator, timeout, true, errorMessage);
         try
        {
            await locator.FillAsync(value, new LocatorFillOptions { Timeout = timeout });
        }
         catch (Exception ex)
        {
            _logger.LogError(ex, errorMessage + " Selector: " + locator.ToString());
            throw new Exception(errorMessage, ex);
        }
    }

     private async Task AwaitVisibleWithErrorAsync(ILocator locator, int timeout = 5000, string? errorMessage = "Element did not become visible")
    {
        await WaitForVisibleAsync(locator, timeout, true, errorMessage);
    }

    public async Task<string?> SaveContentToFileAsync(string? content, string suffix = "")
    {
        if (string.IsNullOrEmpty(content)) return null;

        string backupsDir = Path.Combine(Directory.GetCurrentDirectory(), _settings.BackupsDirectory);
        try
        {
            Directory.CreateDirectory(backupsDir); // Ensure directory exists
            string timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
            string filename = $"roster-{timestamp}{suffix}.html";
            string savePath = Path.Combine(backupsDir, filename);

            await File.WriteAllTextAsync(savePath, content);
            _logger.LogInformation("Saved content to {SavePath}", savePath);
            return savePath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save content to file in {BackupsDir}", backupsDir);
            return null;
        }
    }

    public async Task TakeScreenshotAsync(string fileName = "debug.png")
    {
        if (_page != null && !_page.IsClosed)
        {
            try
            {
                await _page.ScreenshotAsync(new PageScreenshotOptions { Path = fileName });
                _logger.LogInformation("Screenshot saved to {FileName}", fileName);
            }
            catch(Exception ex)
            {
                _logger.LogError(ex, "Failed to take screenshot to {FileName}", fileName);
            }
        }
    }

    public bool IsPageAvailable => _page != null && !_page.IsClosed;

    public async ValueTask DisposeAsync()
    {
        _logger.LogInformation("Disposing Playwright Service...");
        _isEditorOpen = false;
        if (_browser != null)
        {
            await _browser.CloseAsync();
            _logger.LogInformation("Browser closed.");
        }
        _playwright?.Dispose();
         _logger.LogInformation("Playwright disposed.");
    }

    /// <summary>
    /// Creates a Playwright Locator for an element containing the specified text, case-insensitively.
    /// </summary>
    /// <param name="text">The text to search for.</param>
    /// <returns>A Playwright Locator.</returns>
    private ILocator CreateLocatorForText(string text)
    {
        return _page?.Locator($"text={text}") ?? throw new Exception("Page is not initialized.");
    }
} 