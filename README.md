# Clergy Roster Bot (.NET)

[![License](https://img.shields.io/badge/license-Apache%202.0-blue.svg)](LICENSE) <!-- Assuming same license -->

A Discord bot and automation tool for managing the clergy roster of an Elder Scrolls Online (ESO) guild, using natural-language commands in Discord to update a forum post containing a complex HTML table. Built with C#/.NET 10.

---

## Features

- **Discord Integration:** Listens to a designated Discord channel for roster update commands (using Discord.Net).
- **Natural Language Parsing:** Accepts human-friendly commands for adding, removing, renaming, or updating clergy members and their ranks.
- **Automated Forum Editing:** Uses Playwright for native GuildTag account login, then the authenticated same-origin forum API to edit the first roster post. Rereads the exact source to verify every save.
- **Robust HTML Table Handling:** Parses and regenerates complex HTML tables using HtmlAgilityPack, preserving formatting and special markers (e.g., LOTH).
- **Change Backups:** Saves before/after snapshots of the roster for audit and recovery.
- **Error Feedback:** Reacts to Discord messages with status emojis and replies with error details if parsing fails.
- **Modern .NET:** Built with .NET 10, C#, and uses `Microsoft.Extensions.Hosting` for configuration, logging, and dependency injection.

---

## Quick Start

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Discord bot token and channel ID
- Guildtag forum credentials (email, password, and post URL)
- A `.env` file with required secrets (see below) or environment variables set.

### Configuration

Set these flat environment variables, or put them in a private dotenv file and load it with `dotenvx run -f <file> -- dotnet run -c Release`. The application does not load dotenv files itself.

```env
# .env file format
DISCORD_BOT_TOKEN=your_discord_bot_token
DISCORD_CHANNEL_ID=your_channel_id
GUILDTAG_FORUM_URL=https://your-guild.guildtag.com/forum-thread/roster/12345/
GUILDTAG_EMAIL=your_guildtag_email
GUILDTAG_PASSWORD=your_guildtag_password
# Optional:
SUPPRESS_ERROR_FEEDBACK=1   # Set to 1 to suppress error feedback in Discord
BACKUPS_DIRECTORY=clergy-roster-backups
```

**Note:** Use the current website host in `GUILDTAG_FORUM_URL`; thread IDs can survive a subsite migration while the old host stops serving them. The account must have permission to edit the first post. Later posts are left untouched.

---

### Using dotenvx for Encrypted Environment Variables

You can use [dotenvx](https://dotenvx.com/) to encrypt your `.env` files for safer sharing and storage. Keep real environment files and private keys outside version control, including encrypted deployment files.

#### Install dotenvx

- macOS: `brew install dotenvx/brew/dotenvx`
- Linux: `curl -sfS https://dotenvx.sh | sh`
- Windows: `winget install dotenvx`

#### Encrypt your .env file

```bash
# In your project directory
dotenvx encrypt
```
This will:
- Encrypt all secrets in `.env`
- Add a `DOTENV_PUBLIC_KEY` to your `.env`
- Create a `.env.keys` file with your private decryption key (do NOT commit `.env.keys` to source control)

#### Running the Bot with dotenvx (Development)

```bash
dotenvx run -- dotnet run
```
This will decrypt your `.env` at runtime using the key in `.env.keys`.

#### Running the Bot with dotenvx (Production)

In production, do **not** include `.env.keys`. Instead, set the private key as an environment variable:

```bash
DOTENV_PRIVATE_KEY="your_private_key" dotenvx run -- dotnet run
```

---

## Building and Running the Bot

```bash
cd clergy-roster-csharp
# (Recommended) Use dotenvx for secrets:
dotenvx run -- dotnet run -c Release
# Or, if not using dotenvx:
dotnet build
dotnet run -c Release
```

On the first run, Playwright will attempt to download the necessary browser binaries. The bot will connect to Discord, monitor the specified channel, and process new and historical messages for roster updates.

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

**Project home:** https://github.com/allquixotic/clergy-roster-bot

### Verification and recovery

```sh
dotnet restore
dotnet build -c Release --no-restore
# Install the matching browser for the regression harness (PowerShell):
pwsh bin/Release/net10.0/playwright.ps1 install chromium
dotnet run --project tests/ClergyRosterBot.RegressionTests.csproj -c Release
# Live read-only check using the deployment's private environment:
dotenvx run -f .env.prod -- dotnet run -c Release -- --check-forum
```

The regression harness uses a local synthetic GuildTag server and headless
Chromium; it never connects to Discord or a real forum. It covers native login,
expired sessions, permissions, first-post identity, rate limits, concurrent
edits, unpersisted saves, uncertain responses, roster preservation, and quest
announcements such as `Example-Name - Started Curate Quest (8/11/2026)`.

`--check-forum` verifies account access, edit permission, source retrieval, and
roster structure without starting Discord, replaying commands, or writing a
post. It reports a source hash instead of the roster body. It cannot prove an
actual save works; use a reviewed real pending command for that.

Each update requires a local backup and a fresh pre-save source comparison.
A changed source aborts the update; the API does not provide an atomic
compare-and-swap, so avoid simultaneous manual edits during a bot batch.
Success requires the exact saved source to appear in a subsequent API read.
Failed or uncertain writes are not automatically resent.

Before restarting, review pending Discord history and the current roster.
Startup stops its backward scan at the first non-ignored user message with
**any reaction**, including human reactions and parse-error reactions. Older
failed commands may need separate reconciliation. Never clear reactions or
replay all history blindly; a failed acknowledgement may follow a saved edit.
