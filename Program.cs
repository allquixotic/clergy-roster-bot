using ClergyRosterBot;
using ClergyRosterBot.Services;
using DotNetEnv;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Load .env file
Env.Load();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// Configure logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// Configure settings
builder.Services.AddOptions<BotSettings>()
    .Bind(builder.Configuration.GetSection("BotSettings"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Load configuration from environment variables (after .env is loaded)
// Environment variables override .env file values.
// Prefix needs to match how variables are named, e.g., BotSettings__DiscordBotToken
builder.Configuration.AddEnvironmentVariables(prefix: "BotSettings__");

// Register services
builder.Services.AddSingleton<DiscordService>();
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