using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

sealed class GuildTagFixture : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Task _server;
    public string Url { get; }
    public bool LoggedIn { get; set; }
    public bool CanView { get; set; } = true;
    public bool CanEdit { get; set; } = true;
    public bool WrongThread { get; set; }
    public bool MissingFirstPost { get; set; }
    public int RateLimitReads { get; set; }
    public int LoginCount { get; private set; }
    public int Writes { get; private set; }
    public int SaveStatus { get; set; } = 200;
    public bool PersistSave { get; set; } = true;
    public string Body { get; set; } = "<p>Original roster</p>";
    public string OtherBody { get; } = "<p>Historical roster; preserve</p>";

    public GuildTagFixture()
    {
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        Url = $"http://127.0.0.1:{port}/forum-thread/fixture/1/";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _server = ServeAsync();
    }

    private async Task ServeAsync()
    {
        try
        {
            while (_listener.IsListening)
            {
                var ctx = await _listener.GetContextAsync();
                var path = ctx.Request.Url!.AbsolutePath;
                object? data = null;
                string? html = null;
                if (path == "/login")
                    html = LoggedIn ? "<a href='/logout'>Logout</a>" : """
                        <button onclick="document.querySelector('form').hidden=false">
                          Login with your Guildtag Account
                        </button>
                        <form hidden method='post' action='/session'>
                        <input type='email' name='email'><input type='password' name='password'><button>Login</button></form>
                        """;
                else if (path == "/session")
                {
                    LoggedIn = true;
                    LoginCount++;
                    ctx.Response.Redirect(Url);
                }
                else if (path == "/api/forum-thread/1/1/")
                {
                    if (RateLimitReads > 0)
                    {
                        RateLimitReads--;
                        ctx.Response.StatusCode = 429;
                        ctx.Response.Headers["Retry-After"] = "2";
                        data = new { message = "rate limited" };
                    }
                    else
                        data = new { canView = LoggedIn && CanView, forum = new { forumId = 5 },
                            thread = new { forumThreadId = WrongThread ? 999 : 1 },
                            posts = new[] {
                                new { postNumber = 2, forumPostId = 20, canEdit = true, body = OtherBody, forumPostVisibilityTypeId = 0 },
                                new { postNumber = MissingFirstPost ? 3 : 1, forumPostId = 10, canEdit = CanEdit,
                                    body = Body, forumPostVisibilityTypeId = 0 }
                            } };
                }
                else if (path == "/api/forum-post/")
                {
                    Writes++;
                    using var input = await JsonDocument.ParseAsync(ctx.Request.InputStream);
                    var p = input.RootElement;
                    if (ctx.Request.HttpMethod != "POST" || !LoggedIn || !CanEdit
                        || p.GetProperty("forumPostId").GetInt64() != 10
                        || p.GetProperty("forumThreadId").GetInt64() != 1
                        || p.GetProperty("forumId").GetInt64() != 5
                        || ctx.Request.Headers["guildtag-csrf-token"] != "synthetic")
                        ctx.Response.StatusCode = 403;
                    else
                    {
                        if (PersistSave) Body = p.GetProperty("body").GetString()!;
                        ctx.Response.StatusCode = SaveStatus;
                    }
                    data = new { };
                }
                else
                    html = "<meta name='guildtag-csrf-token' content='synthetic'>" +
                        (LoggedIn ? "<a href='/logout'>Logout</a>" : "<p>Public shell without permission banner</p>");
                var bytes = Encoding.UTF8.GetBytes(data != null ? JsonSerializer.Serialize(data) : html ?? "");
                ctx.Response.ContentType = data != null ? "application/json" : "text/html";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        }
        catch (HttpListenerException) when (!_listener.IsListening) { }
        catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        await _server;
        _listener.Close();
    }
}
