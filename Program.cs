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
    Microsoft.Playwright.Program.Main(new[] { "install" });
    logger.LogInformation("Playwright browser check/install complete.");
}
catch (Exception ex)
{
    logger.LogError(ex, "Failed to run Playwright install command. The bot might not work correctly.");
    // Optionally exit if playwright is critical and install failed
    // return 1;
}


await host.RunAsync(); 