using ClergyRosterBot;
using ClergyRosterBot.Models;
using ClergyRosterBot.Services;
using ClergyRosterBot.Workers;

HostApplicationBuilder builder = Host.CreateApplicationBuilder();

// Configure logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace); // Log everything to console
builder.Logging.SetMinimumLevel(LogLevel.Debug); // Set overall minimum level

// Configure settings
builder.Services.AddOptions<BotSettings>()
    .Configure(settings =>
    {
        settings.DiscordBotToken = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN") ?? throw new InvalidOperationException("DISCORD_BOT_TOKEN is not set.");
        settings.DiscordChannelId = ulong.Parse(Environment.GetEnvironmentVariable("DISCORD_CHANNEL_ID") ?? throw new InvalidOperationException("DISCORD_CHANNEL_ID is not set."));
        settings.GuildtagForumUrl = Environment.GetEnvironmentVariable("GUILDTAG_FORUM_URL") ?? throw new InvalidOperationException("GUILDTAG_FORUM_URL is not set.");
        settings.GuildtagEmail = Environment.GetEnvironmentVariable("GUILDTAG_EMAIL") ?? throw new InvalidOperationException("GUILDTAG_EMAIL is not set.");
        settings.GuildtagPassword = Environment.GetEnvironmentVariable("GUILDTAG_PASSWORD") ?? throw new InvalidOperationException("GUILDTAG_PASSWORD is not set.");
        settings.BackupsDirectory = Environment.GetEnvironmentVariable("BACKUPS_DIRECTORY") ?? "clergy-roster-backups";
        // Optional: Suppress error feedback (replies/reactions) if env var is "1"
        settings.SuppressErrorFeedback = Environment.GetEnvironmentVariable("SUPPRESS_ERROR_FEEDBACK") == "1";
    })
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Register services
builder.Services.AddSingleton<DiscordService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<DiscordService>());
builder.Services.AddSingleton<PlaywrightService>();
builder.Services.AddTransient<RosterState>(); // Use Transient for RosterState
builder.Services.AddSingleton<InstructionParser>();
builder.Services.AddHostedService<RosterBotWorker>();

IHost host = builder.Build();

// Run Playwright install command before starting host
var logger = host.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("Ensuring Playwright browsers are installed...");
try
{
    // This command installs the default browser (Chromium) if not present.
    // It needs to be run once, typically after deployment or first run.
    var exitCode = Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
    if (exitCode != 0) throw new InvalidOperationException($"Playwright Chromium installation failed (exit {exitCode}).");
    logger.LogInformation("Playwright browser check/install complete.");
}
catch (Exception ex)
{
    logger.LogError(ex, "Failed to install Playwright Chromium. Startup stopped.");
    throw;
}

// Read-only diagnostic: no hosted services, Discord connection, replay, or forum writes.
if (args.Contains("--check-forum", StringComparer.Ordinal))
{
    await using var forum = host.Services.GetRequiredService<PlaywrightService>();
    var source = await forum.ReadForumPostAsync();
    var roster = new RosterState(Microsoft.Extensions.Logging.Abstractions.NullLogger<RosterState>.Instance);
    roster.ParseFromHtml(source);
    var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source)));
    logger.LogInformation("Forum check passed: editable roster, {Length} source characters, SHA256 {Digest}. No writes performed.", source.Length, digest);
    return;
}

await host.RunAsync();
