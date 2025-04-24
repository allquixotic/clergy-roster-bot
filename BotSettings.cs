namespace ClergyRosterBot;

public class BotSettings
{
    public required string DiscordBotToken { get; init; }
    public required ulong DiscordChannelId { get; init; }
    public required string GuildtagForumUrl { get; init; }
    public required string GuildtagEmail { get; init; }
    public required string GuildtagPassword { get; init; }

    public string BackupsDirectory { get; set; } = "clergy-roster-backups";
} 