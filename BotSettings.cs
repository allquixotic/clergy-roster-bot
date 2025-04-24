namespace ClergyRosterBot;

public class BotSettings
{
    public required string DiscordBotToken { get; set; }
    public required ulong DiscordChannelId { get; set; }
    public required string GuildtagForumUrl { get; set; }
    public required string GuildtagEmail { get; set; }
    public required string GuildtagPassword { get; set; }

    public string BackupsDirectory { get; set; } = "clergy-roster-backups";

    /// <summary>
    /// If true, prevents the bot from replying or reacting with errors (e.g., ❓, ❌) on failures.
    /// Useful for reducing noise during testing or known failure states.
    /// Controlled by the SUPPRESS_ERROR_FEEDBACK environment variable (set to "1" to enable).
    /// </summary>
    public bool SuppressErrorFeedback { get; set; } = false; // Default to false
} 