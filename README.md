# Clergy Roster Bot (.NET)

[![License](https://img.shields.io/badge/license-Apache%202.0-blue.svg)](LICENSE) <!-- Assuming same license -->

A Discord bot and automation tool for managing the clergy roster of an Elder Scrolls Online (ESO) guild, using natural-language commands in Discord to update a forum post containing a complex HTML table. This is a C#/.NET 9 port of the original JavaScript/Bun version.

**Original Project home:** https://github.com/allquixotic/clergy-roster-bot

---

## Features

- **Discord Integration:** Listens to a designated Discord channel for roster update commands (using Discord.Net).
- **Natural Language Parsing:** Accepts human-friendly commands for adding, removing, renaming, or updating clergy members and their ranks.
- **Automated Forum Editing:** Uses Playwright for .NET to log in and update an HTML forum post (on Guildtag) with the new roster.
- **Robust HTML Table Handling:** Parses and regenerates complex HTML tables using AngleSharp, preserving formatting and special markers (e.g., LOTH).
- **Change Backups:** Saves before/after snapshots of the roster for audit and recovery.
- **Error Feedback:** Reacts to Discord messages with status emojis and replies with error details if parsing fails.
- **Modern .NET:** Built with .NET 9, C#, and uses `Microsoft.Extensions.Hosting` for configuration, logging, and dependency injection.

---

## Quick Start

### Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Discord bot token and channel ID
- Guildtag forum credentials (email, password, and post URL)
- A `.env` file with required secrets (see below) or environment variables set.

### Configuration

Create a `.env` file in the project's output directory (e.g., `clergy-roster-csharp/bin/Debug/net9.0/`) or set environment variables with the following names (using double underscore `__` for nesting if setting environment variables directly):

```env
# .env file format
BotSettings__DiscordBotToken=your_discord_bot_token
BotSettings__DiscordChannelId=your_channel_id
BotSettings__GuildtagForumUrl=https://your.guildtag.com/forum/thread/12345
BotSettings__GuildtagEmail=your_guildtag_email
BotSettings__GuildtagPassword=your_guildtag_password
```

**Note:** Environment variables will override values in the `.env` file.

### Building and Running the Bot

```bash
cd clergy-roster-csharp
dotnet build
dotnet run
```

On the first run, Playwright will attempt to download the necessary browser binaries.
The bot will connect to Discord, monitor the specified channel, and process new and historical messages for roster updates.

---

## Usage

Send commands in the designated Discord channel. Supported command types include:

- **Add/Move:**
  `John Doe - Curate of Mara`
  `Jane Smith - Priest of Arkay`
- **Remove:**
  `remove John Doe`
- **Rename:**
  `Jane Smith > Jane the Wise`
- **Set High Priest(ess):**
  `High Priest - John Doe`
  `High Priestess - Jane Smith`
- **LOTH Status:**
  `Jane Smith is now LOTH`
  `John Doe no longer LOTH`

The bot will react with:
- ✅ for successful/valid commands
- ❓ for parsing errors (with a reply explaining the issue)
- ❌ for fatal errors (e.g., automation or login failures)

---

## Development

- **Main Code:** [`Program.cs`](Program.cs), [`Workers/RosterBotWorker.cs`](Workers/RosterBotWorker.cs)
- **Services:** [`Services/`](Services/)
- **Models:** [`Models/`](Models/)
- **Utilities:** [`Utilities/`](Utilities/)
- **Dependencies:** see [`ClergyRosterBot.csproj`](ClergyRosterBot.csproj)
- **Backups:** `clergy-roster-backups/` (auto-created in runtime directory)

---

## License

Copyright 2024-2025 Sean McNamara <smcnam@gmail.com>

Licensed under the [Apache License, Version 2.0](LICENSE). <!-- Add LICENSE file -->

---

## Disclaimer

This project is not affiliated with ZeniMax Online Studios, Guildtag, or Discord. Use at your own risk.

---

**Project home:** (Link to the new C# repo once created) 