using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClergyRosterBot.Services;

/// <summary>Native account login, then same-origin GuildTag source reads and verified edits.</summary>
public class PlaywrightService : IAsyncDisposable
{
    private readonly ILogger<PlaywrightService> _logger;
    private readonly BotSettings _settings;
    private readonly Uri _threadUrl;
    private readonly string _threadId;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IPage? _page;
    private bool _authenticated;
    private JsonElement? _originalThread;
    private JsonElement? _originalPost;
    private DateTimeOffset _nextRequestAt;

    public PlaywrightService(ILogger<PlaywrightService> logger, IOptions<BotSettings> settings)
    {
        _logger = logger;
        _settings = settings.Value;
        _threadUrl = new Uri(_settings.GuildtagForumUrl, UriKind.Absolute);
        if ((_threadUrl.Scheme != "https" && !(_threadUrl.Scheme == "http" && _threadUrl.IsLoopback))
            || !string.IsNullOrEmpty(_threadUrl.UserInfo))
            throw new InvalidOperationException("GUILDTAG_FORUM_URL must use HTTPS (HTTP is allowed only on loopback for tests).");
        var match = Regex.Match(_threadUrl.AbsolutePath, @"^/forum-thread/[^/]+/(\d+)/?$");
        if (!match.Success)
            throw new InvalidOperationException("GUILDTAG_FORUM_URL must contain /forum-thread/<slug>/<threadId>/.");
        _threadId = match.Groups[1].Value;
    }

    private async Task EnsureInitializedAsync()
    {
        if (_page is { IsClosed: false }) return;
        if (_browser != null) await _browser.CloseAsync();
        _playwright?.Dispose();
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true });
        // NewPage creates an isolated context; never persist or export browser credentials.
        _page = await _browser.NewPageAsync();
        _page.SetDefaultTimeout(20000);
        _authenticated = false;
    }

    private async Task PaceAsync()
    {
        var delay = _nextRequestAt - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero) await Task.Delay(delay);
    }

    private void PaceNextRequest() => _nextRequestAt = DateTimeOffset.UtcNow.AddSeconds(2);

    private async Task NavigateAsync(Uri url)
    {
        await PaceAsync();
        try
        {
            var response = await _page!.GotoAsync(url.ToString(), new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            if (response is { Status: >= 400 })
                throw new InvalidOperationException($"GuildTag page returned HTTP {response.Status}. Check the configured site and retry later.");
            EnsureSameOrigin();
        }
        finally { PaceNextRequest(); }
    }

    private void EnsureSameOrigin()
    {
        if (new Uri(_page!.Url).GetLeftPart(UriPartial.Authority) != _threadUrl.GetLeftPart(UriPartial.Authority))
            throw new InvalidOperationException("GuildTag redirected to another website. Check GUILDTAG_FORUM_URL before logging in or editing.");
    }

    private async Task LoginAsync()
    {
        await NavigateAsync(new Uri(_threadUrl, "/login"));
        var email = _page!.Locator("input[type=email]:visible, input[name=email]:visible").First;
        var password = _page.Locator("input[type=password]:visible").First;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        var nextToggle = DateTimeOffset.MinValue;
        while (!await email.IsVisibleAsync() || !await password.IsVisibleAsync())
        {
            // An already signed-in account sees Logout instead of the account form.
            if (await _page.Locator("a[href*='/logout']:visible").CountAsync() > 0
                && await _page.Locator("input[type=password]").CountAsync() == 0)
            {
                _authenticated = true;
                return;
            }
            if (DateTimeOffset.UtcNow >= deadline)
                throw new InvalidOperationException("GuildTag account login controls did not appear. Check site availability and the configured domain.");
            var toggle = _page.Locator("button:visible, a:visible").Filter(new() {
                HasTextRegex = new Regex(@"^\s*(?:Login with (?:your )?)?Guildtag Account\s*$", RegexOptions.IgnoreCase)
            }).First;
            if (DateTimeOffset.UtcNow >= nextToggle && await toggle.IsVisibleAsync())
            {
                await toggle.ClickAsync();
                nextToggle = DateTimeOffset.UtcNow.AddSeconds(1.5);
            }
            await Task.Delay(100);
        }
        EnsureSameOrigin();
        // Playwright exceptions can include Fill values. Do not retain those exceptions.
        try
        {
            await email.FillAsync(_settings.GuildtagEmail);
            await password.FillAsync(_settings.GuildtagPassword);
        }
        catch { throw new InvalidOperationException("Could not fill GuildTag account login controls."); }
        await PaceAsync();
        await _page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex(@"^(?:Login|Log in|Sign in)$", RegexOptions.IgnoreCase) }).ClickAsync();
        await _page.WaitForFunctionAsync("() => !location.pathname.startsWith('/login') && !document.querySelector('input[type=password]')");
        PaceNextRequest();
        EnsureSameOrigin();
        _authenticated = true;
        _logger.LogInformation("GuildTag account login completed.");
    }

    private async Task<JsonElement> FetchThreadAsync()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await PaceAsync();
            EnsureSameOrigin();
            JsonElement result;
            try
            {
                result = await _page!.EvaluateAsync<JsonElement>("""
                    async path => {
                        try {
                            const r = await fetch(path, {headers:{accept:'application/json'}, credentials:'same-origin',
                                cache:'no-store', signal:AbortSignal.timeout(20000)});
                            return {status:r.status, retryAfter:r.headers.get('retry-after'), body:await r.json().catch(()=>null)};
                        } catch { return {status:0, body:null}; }
                    }
                    """, $"/api/forum-thread/{_threadId}/1/");
            }
            finally { PaceNextRequest(); }
            var status = result.GetProperty("status").GetInt32();
            if (status == 429)
            {
                var retry = result.GetProperty("retryAfter").GetString();
                var seconds = double.TryParse(retry, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed)
                    ? Math.Max(2, parsed)
                    : DateTimeOffset.TryParse(retry, out var date) ? Math.Max(2, (date - DateTimeOffset.UtcNow).TotalSeconds) : 5;
                _nextRequestAt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(seconds, 3600));
                if (seconds <= 30 && attempt < 2)
                {
                    _logger.LogWarning("GuildTag rate limited the read; waiting {Seconds}s before retrying.", seconds);
                    continue;
                }
            }
            if (status == 400)
                throw new InvalidOperationException("GuildTag rejected the thread for this website. Check GUILDTAG_FORUM_URL after a site migration.");
            if (status is 401 or 403) throw new UnauthorizedAccessException("GuildTag requires an authorized account for this thread.");
            if (status is < 200 or >= 300)
                throw new InvalidOperationException($"GuildTag thread read failed (HTTP {status}). No edit was confirmed.");
            var body = result.GetProperty("body");
            if (body.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("GuildTag returned no thread data.");
            if (!body.TryGetProperty("canView", out var canView) || canView.ValueKind != JsonValueKind.True)
                throw new UnauthorizedAccessException("GuildTag account cannot view this thread.");
            if (body.GetProperty("thread").GetProperty("forumThreadId").ToString() != _threadId)
                throw new InvalidOperationException("GuildTag returned a different thread.");
            return body;
        }
        throw new InvalidOperationException("GuildTag read retry limit reached.");
    }

    private static JsonElement RosterPost(JsonElement thread)
    {
        var posts = thread.GetProperty("posts").EnumerateArray()
            .Where(p => p.GetProperty("postNumber").GetInt32() == 1).ToArray();
        if (posts.Length != 1) throw new InvalidOperationException("GuildTag did not return exactly one first roster post.");
        var post = posts[0];
        if (!post.TryGetProperty("canEdit", out var canEdit) || canEdit.ValueKind != JsonValueKind.True)
            throw new UnauthorizedAccessException("GuildTag account cannot edit the first roster post.");
        if (string.IsNullOrWhiteSpace(post.GetProperty("body").GetString()))
            throw new InvalidOperationException("GuildTag roster source is empty.");
        return post;
    }

    public async Task<string> ReadForumPostAsync()
    {
        _originalPost = null;
        _originalThread = null;
        await EnsureInitializedAsync();
        if (!_authenticated) await LoginAsync();
        await NavigateAsync(_threadUrl);
        JsonElement thread;
        try { thread = await FetchThreadAsync(); }
        catch (UnauthorizedAccessException)
        {
            _authenticated = false;
            await LoginAsync();
            await NavigateAsync(_threadUrl);
            thread = await FetchThreadAsync();
        }
        var post = RosterPost(thread);
        _originalThread = thread;
        _originalPost = post;
        _logger.LogInformation("Read editable GuildTag roster post {PostId}.", post.GetProperty("forumPostId").ToString());
        return post.GetProperty("body").GetString()!;
    }

    public async Task UpdateForumPostAsync(string newContent)
    {
        if (_originalPost is not { } original || _originalThread is not { } thread)
            throw new InvalidOperationException("Read the roster before updating it.");
        if (string.IsNullOrWhiteSpace(newContent)) throw new InvalidOperationException("Cannot save an empty roster.");
        var before = RosterPost(await FetchThreadAsync());
        if (before.GetProperty("forumPostId").ToString() != original.GetProperty("forumPostId").ToString())
            throw new InvalidOperationException("GuildTag roster post identity changed; read it again.");
        var current = before.GetProperty("body").GetString();
        if (current == newContent) return;
        if (current != original.GetProperty("body").GetString())
            throw new InvalidOperationException("The roster changed since it was read. No update sent; reconcile the newer edit first.");
        if (await SaveContentToFileAsync(current, "-before-save") == null)
            throw new IOException("Cannot back up the current roster. No update sent.");
        var payload = new {
            body = newContent,
            forumPostVisibilityTypeId = before.GetProperty("forumPostVisibilityTypeId").GetInt32(),
            forumId = thread.GetProperty("forum").GetProperty("forumId").GetInt64(),
            forumThreadId = long.Parse(_threadId),
            forumPostId = before.GetProperty("forumPostId").GetInt64(),
            quotedUsers = Array.Empty<string>()
        };
        await PaceAsync();
        EnsureSameOrigin();
        int status;
        try
        {
            // Use the page's native anti-forgery fields in-page only. Never export them.
            status = await _page!.EvaluateAsync<int>("""
                async payload => {
                    const meta = name => document.querySelector('meta[name="'+name+'"]')?.content ?? '';
                    try {
                        const r = await fetch('/api/forum-post/', {
                            method:'POST', credentials:'same-origin', signal:AbortSignal.timeout(20000),
                            headers:{'content-type':'application/json', accept:'application/json',
                                'guildtag-csrf-token':meta('guildtag-csrf-token'),
                                'guildtag-api-key':meta('guildtag-api-key'),
                                'guildtag-correlation-id':meta('guildtag-correlation-id')},
                            body:JSON.stringify(payload)
                        });
                        return r.status;
                    } catch { return 0; }
                }
                """, payload);
        }
        finally { PaceNextRequest(); }
        // Never blindly repeat a POST, including a timeout or rate limit response.
        // An uncertain request may already have landed. Source read-back decides success.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var saved = RosterPost(await FetchThreadAsync());
            if (saved.GetProperty("forumPostId").ToString() == original.GetProperty("forumPostId").ToString()
                && saved.GetProperty("body").GetString() == newContent)
            {
                _originalPost = saved;
                _logger.LogInformation("Verified saved GuildTag roster source for post {PostId}.", saved.GetProperty("forumPostId").ToString());
                return;
            }
            if (status is >= 400) break;
        }
        throw new InvalidOperationException($"GuildTag save was not verified (HTTP {status}). Re-read the roster before retrying.");
    }

    public async Task<string?> SaveContentToFileAsync(string? content, string suffix = "")
    {
        if (string.IsNullOrEmpty(content)) return null;

        string backupsDir = Path.Combine(Directory.GetCurrentDirectory(), _settings.BackupsDirectory);
        try
        {
            Directory.CreateDirectory(backupsDir); // Ensure directory exists
            string timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
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
        if (_page != null && !_page.IsClosed && !_page.Url.Contains("/login"))
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
        _originalPost = null;
        _authenticated = false;
        if (_browser != null)
        {
            await _browser.CloseAsync();
            _logger.LogInformation("Browser closed.");
        }
        _playwright?.Dispose();
         _logger.LogInformation("Playwright disposed.");
    }

}
