# clergy-roster-bot

[![License](https://img.shields.io/badge/license-Apache%202.0-blue.svg)](LICENSE)

A Discord bot and automation tool for managing the clergy roster of an Elder Scrolls Online (ESO) guild, using natural-language commands in Discord to update a forum post containing a complex HTML table.  
**Project home:** https://github.com/allquixotic/clergy-roster-bot

---

## Features

- **Discord Integration:** Listens to a designated Discord channel for roster update commands.
- **Natural Language Parsing:** Accepts human-friendly commands for adding, removing, renaming, or updating clergy members and their ranks.
- **Automated Forum Editing:** Uses Playwright to log in and update an HTML forum post (on Guildtag) with the new roster.
- **Robust HTML Table Handling:** Parses and regenerates complex HTML tables, preserving formatting and special markers (e.g., LOTH).
- **Change Backups:** Saves before/after snapshots of the roster for audit and recovery.
- **Error Feedback:** Reacts to Discord messages with status emojis and replies with error details if parsing fails.

---

## Quick Start

### Prerequisites

- [Bun](https://bun.sh/) (for running the bot)
- Node.js-compatible environment (Bun is a drop-in replacement for Node)
- Discord bot token and channel ID
- Guildtag forum credentials (email, password, and post URL)
- A `.env` file with required secrets (see below)

### Installation

```bash
cd clergy-roster-bot
bun install
```

### Configuration

Create a `.env` file in the `clergy-roster-bot/` directory with the following variables:

```env
DISCORD_BOT_TOKEN=your_discord_bot_token
DISCORD_CHANNEL_ID=your_channel_id
GUILDTAG_FORUM_URL=https://your.guildtag.com/forum/thread/12345
GUILDTAG_EMAIL=your_guildtag_email
GUILDTAG_PASSWORD=your_guildtag_password
```

**Note:** Never commit your `.env` file or secrets to version control.

### Running the Bot

```bash
bun run index.js
```

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

See the pinned message in your Discord channel for more syntax examples.

---

## Development

- Main code: [`index.js`](clergy-roster/index.js)
- Dependencies: see [`package.json`](clergy-roster/package.json)
- Backups and logs: `clergy-roster-backups/` (auto-created)
- Ignore files: see [`.gitignore`](clergy-roster/.gitignore)

### Scripts

- `bun run index.js` — Start the bot

---

## License

Copyright 2024-2025 Sean McNamara <smcnam@gmail.com>

Licensed under the [Apache License, Version 2.0](LICENSE).

---

## Contributing

Pull requests and issues are welcome! Please see [CONTRIBUTING.md](CONTRIBUTING.md) if available, or open an issue to discuss your ideas.

---

## Disclaimer

This project is not affiliated with ZeniMax Online Studios, Guildtag, or Discord. Use at your own risk.

---

**Project home:** https://github.com/allquixotic/clergy-roster-bot
