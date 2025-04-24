using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace ClergyRosterBot.Services;

public class DiscordService : IHostedService
{
    private readonly ILogger<DiscordService> _logger;
    private readonly BotSettings _settings;
    private readonly DiscordSocketClient _client;
    private readonly IServiceProvider _serviceProvider; // To create worker scope later if needed
    private readonly TaskCompletionSource _readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public event Func<SocketMessage, Task>? MessageReceived;

    // Expose a Task that completes when the client is ready
    public Task Ready => _readyTcs.Task;

    public DiscordService(
        ILogger<DiscordService> logger,
        IOptions<BotSettings> settings,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _settings = settings.Value;
        _serviceProvider = serviceProvider;

        var config = new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds |
                             GatewayIntents.GuildMessages |
                             GatewayIntents.MessageContent |
                             GatewayIntents.GuildMessageReactions,
            MessageCacheSize = 100 // Adjust as needed
        };
        _client = new DiscordSocketClient(config);

        _client.Log += LogAsync;
        _client.Ready += ReadyAsync;
        _client.MessageReceived += HandleMessageReceivedAsync;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Discord Service...");
        try
        {
            _logger.LogInformation("Attempting to log in to Discord...");
            await _client.LoginAsync(TokenType.Bot, _settings.DiscordBotToken);
            _logger.LogInformation("Discord login successful. Attempting to start connection...");
            await _client.StartAsync();
            // ReadyAsync will log the successful connection
            _logger.LogInformation("Discord connection process initiated."); // Note: Doesn't mean it's fully 'Ready' yet, ReadyAsync confirms that.
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Failed during Discord service startup (Login or Start).");
            // Optionally trigger application shutdown
            var lifetime = _serviceProvider.GetRequiredService<IHostApplicationLifetime>();
            lifetime.StopApplication();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Discord Service...");
        await _client.StopAsync();
        await _client.LogoutAsync();
        _logger.LogInformation("Discord Service stopped.");
    }

    private Task LogAsync(LogMessage log)
    {
        _logger.Log(log.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Trace,
            LogSeverity.Debug => LogLevel.Debug,
            _ => LogLevel.Information
        }, $"[Discord] {log.Message}", log.Exception);
        return Task.CompletedTask;
    }

    private Task ReadyAsync()
    {
        _logger.LogInformation($"Discord bot connected as {_client.CurrentUser.Username}#{_client.CurrentUser.Discriminator}");
        // Signal that the client is ready
        _readyTcs.TrySetResult();
        // Trigger initial fetch or other ready actions here if needed
        return Task.CompletedTask;
    }

    private async Task HandleMessageReceivedAsync(SocketMessage messageParam)
    {
        // Ignore system messages, self, and other bots
        if (messageParam is not SocketUserMessage message || message.Source != MessageSource.User || message.Author.IsBot)
        {
            return;
        }

        // Check if the message is in the target channel
        if (message.Channel.Id != _settings.DiscordChannelId)
        {
            return;
        }

        _logger.LogInformation("Received relevant message [Id: {MessageId}] from {Author} in #{ChannelName}",
            message.Id, message.Author.Username, message.Channel.Name);

        // Invoke the event handler if subscribed
        if (MessageReceived != null)
        {
            try
            {
                await MessageReceived.Invoke(message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling MessageReceived event for message {MessageId}", message.Id);
                // Consider replying with an error message
                await ReplyToMessageAsync(message.Id, "An internal error occurred while processing your command.");
            }
        }
        else
        {
            _logger.LogWarning("MessageReceived event handler is null, message {MessageId} not processed further by worker.", message.Id);
        }
    }

    public async Task<IEnumerable<IMessage>> FetchUnprocessedMessagesAsync()
    {
        _logger.LogInformation("Fetching unprocessed messages...");
        // Get the channel first to provide better diagnostics
        var rawChannel = _client.GetChannel(_settings.DiscordChannelId);

        if (rawChannel == null)
        {
            _logger.LogError("Target channel {ChannelId} not found. Ensure the bot is in the guild and the ID is correct.", _settings.DiscordChannelId);
            return Enumerable.Empty<IMessage>();
        }

        // Now check if it's the correct type
        if (rawChannel is not IMessageChannel channel)
        {
            _logger.LogError("Target channel {ChannelId} was found, but it is not a message channel. It is a '{ChannelType}'. Please provide the ID of a text channel.", _settings.DiscordChannelId, rawChannel.GetType().Name);
            return Enumerable.Empty<IMessage>();
        }

        var unprocessedMessages = new List<IMessage>();
        ulong? lastMessageId = null;
        bool foundReactedMessage = false;
        const int fetchLimit = 100;
        int fetchedCount = 0;
        const int maxTotalFetch = 1000; // Safety limit

        try
        {
            while (!foundReactedMessage && fetchedCount < maxTotalFetch)
            {
                var messagesBatch = lastMessageId.HasValue
                    ? await channel.GetMessagesAsync(lastMessageId.Value, Direction.Before, fetchLimit, CacheMode.AllowDownload).FlattenAsync()
                    : await channel.GetMessagesAsync(fetchLimit, CacheMode.AllowDownload).FlattenAsync();

                var currentBatch = messagesBatch.ToList(); // Consume the async enumerable

                fetchedCount += currentBatch.Count;

                if (currentBatch.Count == 0)
                {
                    _logger.LogDebug("Reached end of message history or beginning of channel.");
                    break; // No more messages
                }

                _logger.LogDebug("Fetched batch of {Count} messages (total fetched: {Total}). Processing newest to oldest in batch.", currentBatch.Count, fetchedCount);
                lastMessageId = currentBatch.LastOrDefault()?.Id; // Oldest message ID in this batch
                if (lastMessageId == null) break; // Should not happen if count > 0

                foreach (var message in currentBatch.OrderByDescending(m => m.Timestamp)) // Process newest first to find boundary
                {
                    if (message.Author.IsBot) continue;

                    // Skip messages starting with 'ignore' (case-insensitive, ignoring leading whitespace)
                    if (message.Content?.TrimStart().StartsWith("ignore", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        _logger.LogDebug("Skipping message [{MessageId}] because it starts with 'ignore'.", message.Id);
                        continue;
                    }

                    // Check reactions. Stop *searching* history if a reacted message is found.
                    if (message.Reactions.Any())
                    {
                        _logger.LogDebug("Found reacted message [{MessageId}]. Stopping history scan.", message.Id);
                        foundReactedMessage = true;
                        break; // Stop processing this batch and stop fetching older messages
                    }
                    else
                    {
                        _logger.LogDebug("Adding unreacted message [{MessageId}] to processing batch.", message.Id);
                        unprocessedMessages.Add(message); // Add to end for now
                    }
                }
            }

            if (fetchedCount >= maxTotalFetch)
            {
                _logger.LogWarning("Reached fetch limit ({MaxTotalFetch}) without finding a reacted message. Processing the {Count} found so far.", maxTotalFetch, unprocessedMessages.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed during message history fetch.");
        }

        // Reverse the collected messages so the oldest is first
        unprocessedMessages.Reverse();

        _logger.LogInformation("Found {Count} potentially unprocessed messages (newer than the first found reacted message), ordered oldest to newest.", unprocessedMessages.Count);
        return unprocessedMessages;
    }

    public async Task ReactToMessageAsync(ulong messageId, IEmote emoji)
    {
        if (_client.GetChannel(_settings.DiscordChannelId) is not ITextChannel channel)
        {
            _logger.LogWarning("Cannot react, channel {ChannelId} not found.", _settings.DiscordChannelId);
            return;
        }

        try
        {
            var message = await channel.GetMessageAsync(messageId);
            if (message == null)
            {
                _logger.LogWarning("Cannot react, message {MessageId} not found.", messageId);
                return;
            }

            // Check if already reacted by the bot
            if (message.Reactions.TryGetValue(emoji, out var reactionInfo) && reactionInfo.IsMe)
            {
                // _logger.LogDebug("Already reacted with {Emoji} to message {MessageId}", emoji, messageId);
                return;
            }

            await message.AddReactionAsync(emoji);
            // _logger.LogDebug("Reacted with {Emoji} to message {MessageId}", emoji, messageId);
        }
        catch (Discord.Net.HttpException httpEx) when (httpEx.HttpCode == System.Net.HttpStatusCode.NotFound) // 10008 Unknown Message
        {
            _logger.LogWarning("Failed to react to message {MessageId}: Message not found (possibly deleted).", messageId);
        }
        catch (Discord.Net.HttpException httpEx) when (httpEx.DiscordCode == DiscordErrorCode.MissingPermissions) // 50013
        {
            _logger.LogError("Failed to react to message {MessageId}: Missing 'Add Reactions' permission in channel {ChannelId}.", messageId, _settings.DiscordChannelId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to react to message {MessageId} with {Emoji}.", messageId, emoji);
        }
    }

    public async Task ReplyToMessageAsync(ulong messageId, string content)
    {
        if (_client.GetChannel(_settings.DiscordChannelId) is not ITextChannel channel)
        {
            _logger.LogWarning("Cannot reply, channel {ChannelId} not found.", _settings.DiscordChannelId);
            return;
        }

        try
        {
            var message = await channel.GetMessageAsync(messageId);
            if (message == null)
            {
                _logger.LogWarning("Cannot reply, message {MessageId} not found.", messageId);
                return;
            }
            if (message is IUserMessage userMessage)
            {
                await userMessage.ReplyAsync(content, allowedMentions: AllowedMentions.None); // Don't ping
                // _logger.LogDebug("Replied to message {MessageId}", messageId);
            }
            else
            {
                _logger.LogWarning("Cannot reply to message {MessageId} as it is not a user message.", messageId);
            }
        }
        catch (Discord.Net.HttpException httpEx) when (httpEx.HttpCode == System.Net.HttpStatusCode.NotFound) // 10008 Unknown Message
        {
            _logger.LogWarning("Failed to reply to message {MessageId}: Message not found (possibly deleted).", messageId);
        }
        catch (Discord.Net.HttpException httpEx) when (httpEx.DiscordCode == DiscordErrorCode.MissingPermissions) // 50013
        {
            _logger.LogError("Failed to reply to message {MessageId}: Missing 'Send Messages' or 'Read Message History' permission in channel {ChannelId}.", messageId, _settings.DiscordChannelId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reply to message {MessageId}.", messageId);
        }
    }
} 