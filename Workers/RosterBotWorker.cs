using ClergyRosterBot.Services;
using ClergyRosterBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Playwright;
using Microsoft.Extensions.Options;
using System.Threading.Channels;

namespace ClergyRosterBot.Workers;

public class RosterBotWorker : BackgroundService
{
    private readonly ILogger<RosterBotWorker> _logger;
    private readonly DiscordService _discordService;
    private readonly PlaywrightService _playwrightService;
    private readonly InstructionParser _instructionParser;
    private readonly IServiceProvider _serviceProvider;
    private readonly BotSettings _settings;
    private readonly Channel<IMessage> _messageChannel;
    private Task? _processingTask;

    // Define Emojis (consider moving to Constants)
    private static readonly Emoji SuccessEmoji = new Emoji("✅");
    private static readonly Emoji ErrorEmoji = new Emoji("❓");
    private static readonly Emoji FatalErrorEmoji = new Emoji("❌");

    public RosterBotWorker(
        ILogger<RosterBotWorker> logger,
        DiscordService discordService,
        PlaywrightService playwrightService,
        InstructionParser instructionParser,
        IServiceProvider serviceProvider,
        IOptions<BotSettings> settings)
    {
        _logger = logger;
        _discordService = discordService;
        _playwrightService = playwrightService;
        _instructionParser = instructionParser;
        _serviceProvider = serviceProvider;
        _settings = settings.Value;
        _messageChannel = Channel.CreateUnbounded<IMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RosterBotWorker starting.");

        _discordService.MessageReceived += HandleIncomingMessage;

        _logger.LogInformation("Waiting for Discord service to connect and become ready...");
        try
        {
            await _discordService.Ready.WaitAsync(stoppingToken);
            _logger.LogInformation("Discord service is ready.");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Worker stopping before Discord service became ready.");
            _discordService.MessageReceived -= HandleIncomingMessage;
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error waiting for Discord service readiness.");
            _discordService.MessageReceived -= HandleIncomingMessage;
            return;
        }

        _processingTask = ProcessChannelAsync(stoppingToken);
        _logger.LogInformation("Message processing loop started.");

        await FetchAndEnqueueHistoricalMessages(stoppingToken);

        _logger.LogInformation("RosterBotWorker running. Listening for messages and processing channel.");

        await _processingTask;

        _logger.LogInformation("RosterBotWorker ExecuteAsync finished.");
    }

    private async Task FetchAndEnqueueHistoricalMessages(CancellationToken stoppingToken)
    {
        if (stoppingToken.IsCancellationRequested) return;

        _logger.LogInformation("Performing initial check for unprocessed messages...");
        try
        {
            var unprocessedMessages = await _discordService.FetchUnprocessedMessagesAsync();
            if (unprocessedMessages.Any())
            {
                _logger.LogInformation("Enqueuing {Count} historical messages for processing (oldest first).", unprocessedMessages.Count());
                foreach (var msg in unprocessedMessages.OrderBy(m => m.Timestamp))
                {
                    if (!stoppingToken.IsCancellationRequested)
                    {
                        if (!_messageChannel.Writer.TryWrite(msg))
                        {
                            _logger.LogWarning("Failed to write historical message {MessageId} to channel (should not happen with unbounded channel).", msg.Id);
                        }
                    }
                    else
                    {
                         _logger.LogInformation("Cancellation requested during historical message enqueueing.");
                         break;
                    }
                }
                _logger.LogInformation("Finished enqueuing historical messages.");
            }
            else
            {
                _logger.LogInformation("No unprocessed historical messages found.");
            }
        }
        catch (Exception ex)
        {
             _logger.LogError(ex, "Error fetching or enqueuing historical messages.");
        }
    }

    private Task HandleIncomingMessage(SocketMessage message)
    {
        _logger.LogDebug("Received new message {MessageId}. Writing to channel.", message.Id);
        if (!_messageChannel.Writer.TryWrite(message))
        {
            _logger.LogWarning("Failed to write incoming message {MessageId} to channel.", message.Id);
        }
        return Task.CompletedTask;
    }

    private async Task ProcessChannelAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting channel processing loop.");
        try
        {
            while (await _messageChannel.Reader.WaitToReadAsync(stoppingToken))
            {
                var messagesToProcess = new List<IMessage>();

                if (_messageChannel.Reader.TryRead(out var firstMessage))
                {
                    messagesToProcess.Add(firstMessage);

                    int maxBatchSize = 50;
                    while (messagesToProcess.Count < maxBatchSize && _messageChannel.Reader.TryRead(out var subsequentMessage))
                    {
                        messagesToProcess.Add(subsequentMessage);
                    }
                }

                if (messagesToProcess.Any())
                {
                    _logger.LogInformation("Dequeued batch of {Count} message(s) from channel.", messagesToProcess.Count);
                    try
                    {
                        await ProcessBatchAsync(messagesToProcess, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        _logger.LogInformation("Processing batch cancelled.");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing message batch. Message IDs: {Ids}", string.Join(", ", messagesToProcess.Select(m => m.Id)));
                    }
                }
                else
                {
                     _logger.LogWarning("WaitToReadAsync returned true, but TryRead failed. This might indicate a race condition or unexpected channel state.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Channel processing loop cancelled.");
        }
        catch (ChannelClosedException)
        {
            _logger.LogInformation("Channel processing loop ended because the channel was closed.");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Unhandled exception in channel processing loop.");
        }
        finally
        {
            _logger.LogInformation("Exiting channel processing loop.");
        }
    }

    private async Task ProcessBatchAsync(List<IMessage> messagesToProcess, CancellationToken stoppingToken)
    {
        _logger.LogInformation("--- Processing batch of {Count} message(s) ---", messagesToProcess.Count);
        _logger.LogDebug("Message IDs in batch: {Ids}", string.Join(", ", messagesToProcess.Select(m => m.Id)));

        string? originalHtml = null;
        string? originalFile = null;
        string? updatedFile = null;
        bool changesMade = false;
        var results = new RosterChangeResult();
        bool isFatalPlaywrightError = false;

        if (stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning("Processing cancelled before starting work on batch.");
            return;
        }

        try
        {
            var instructionsToApply = new List<Models.Instruction>();
            var messagesWithError = new HashSet<IMessage>();
            var messagesIgnored = new HashSet<IMessage>();

            foreach (var message in messagesToProcess)
            {
                 if (stoppingToken.IsCancellationRequested) break;

                var lines = (message.Content ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0) { messagesIgnored.Add(message); continue; }

                if (lines[0].Trim().StartsWith("ignore", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("Skipping message {MessageId} because first line starts with 'ignore'.", message.Id);
                    messagesIgnored.Add(message);
                    continue;
                }

                bool messageHadError = false;
                bool messageHadAction = false;
                for (int i = 0; i < lines.Length; i++)
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    string line = lines[i].Trim();
                    if (string.IsNullOrEmpty(line)) continue;

                    var parsed = _instructionParser.ParseInstruction(line);
                    if (parsed.Type == Models.InstructionType.Error)
                    {
                        _logger.LogWarning("Failed parsing message {MessageId} (line {LineNum}): {Reason} - \"{OriginalLine}\"",
                            message.Id, i + 1, parsed.Reason, parsed.OriginalLine);
                        results.Failures.Add(new MessageFailure(message, parsed.Reason ?? "Unknown parsing error"));
                        messageHadError = true;
                        break;
                    }
                    else if (parsed.Type != Models.InstructionType.Ignore)
                    {
                        instructionsToApply.Add(parsed);
                        messageHadAction = true;
                    }
                    else
                    {
                        _logger.LogDebug("Ignored line in message {MessageId}: {Reason}", message.Id, parsed.Reason);
                    }
                }
                 if (stoppingToken.IsCancellationRequested) break;

                if (messageHadError)
                {
                    messagesWithError.Add(message);
                }
                else if (!messageHadAction)
                {
                    messagesIgnored.Add(message);
                }
                else
                {
                    results.Successes.Add(message);
                }
            }

            if (stoppingToken.IsCancellationRequested)
            {
                 _logger.LogWarning("Processing cancelled during message parsing phase.");
                 return;
            }

            if (instructionsToApply.Any())
            {
                originalHtml = await _playwrightService.GetForumPostContentAndOpenEditorAsync();
                originalFile = await _playwrightService.SaveContentToFileAsync(originalHtml, "-original");

                using var scope = _serviceProvider.CreateScope();
                var rosterState = scope.ServiceProvider.GetRequiredService<RosterState>();
                rosterState.ParseFromHtml(originalHtml);

                _logger.LogInformation("Applying {Count} valid instructions to roster state...", instructionsToApply.Count);
                rosterState.ApplyInstructions(instructionsToApply);
                string updatedHtml = rosterState.RegenerateHtml();

                 if (stoppingToken.IsCancellationRequested) { _logger.LogWarning("Processing cancelled before comparing/updating HTML."); return; }

                if (updatedHtml != originalHtml)
                {
                    _logger.LogInformation("Changes detected after applying instructions. Updating forum post...");
                    await _playwrightService.UpdateForumPostAsync(updatedHtml);
                    updatedFile = await _playwrightService.SaveContentToFileAsync(updatedHtml, "-updated");
                    changesMade = true;
                    _logger.LogInformation("Forum post updated successfully.");
                }
                else
                {
                    _logger.LogInformation("Instructions were valid, but resulted in no change to the roster HTML. Skipping forum update.");
                    updatedFile = originalFile;
                }
            }
            else
            {
                _logger.LogInformation("No valid instructions found in the batch to apply. No changes made. Playwright will not be invoked.");
                originalFile = null;
                updatedFile = null;
            }

             if (stoppingToken.IsCancellationRequested) { _logger.LogWarning("Processing cancelled before reacting to messages."); return; }

            foreach (var message in results.Successes.Where(m => !messagesWithError.Contains(m)))
            {
                 if (stoppingToken.IsCancellationRequested) break;
                await _discordService.ReactToMessageAsync(message.Id, SuccessEmoji);
            }
            foreach (var failure in results.Failures)
            {
                 if (stoppingToken.IsCancellationRequested) break;
                if (!_settings.SuppressErrorFeedback)
                {
                    await _discordService.ReactToMessageAsync(failure.Message.Id, ErrorEmoji);
                    await _discordService.ReplyToMessageAsync(failure.Message.Id, $"Failed to parse command (Reason: {failure.Reason}). Check format.");
                }
            }
            foreach (var ignored in messagesIgnored)
            {
                 if (stoppingToken.IsCancellationRequested) break;
                _logger.LogDebug("Message {MessageId} was ignored (no actionable instructions or started with 'ignore').", ignored.Id);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
             _logger.LogWarning("Operation cancelled during batch processing (Playwright/Discord interaction).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unrecoverable error during batch processing.");

            if (_playwrightService.IsPageAvailable || ex is PlaywrightException || (ex.Message?.Contains("Playwright", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                isFatalPlaywrightError = true;
                await _playwrightService.TakeScreenshotAsync("-error");
            }

            var firstMessage = messagesToProcess.FirstOrDefault();
            if (firstMessage != null && !_settings.SuppressErrorFeedback)
            {
                try {
                    await _discordService.ReplyToMessageAsync(firstMessage.Id, $"An unexpected error occurred while processing this batch: {ex.Message}. Some commands might not have been applied.");
                    if (!isFatalPlaywrightError)
                    {
                        foreach (var msg in messagesToProcess)
                        {
                            if (!results.Successes.Contains(msg) && !results.Failures.Any(f => f.Message.Id == msg.Id))
                            {
                                try { await _discordService.ReactToMessageAsync(msg.Id, FatalErrorEmoji); } catch { /* Ignore reaction error */ }
                            }
                        }
                    }
                    else
                    {
                        _logger.LogError("Skipping ❌ emoji reactions due to fatal Playwright automation error.");
                    }
                } catch (Exception discordEx) {
                     _logger.LogError(discordEx, "Failed to send error feedback to Discord for batch failure.");
                }
            }
            else if (firstMessage != null)
            {
                _logger.LogWarning("Suppressing error feedback for batch processing error as requested by settings.");
            }
        }
        finally
        {
            if (changesMade && originalFile != null && updatedFile != null && originalFile != updatedFile)
            {
                _logger.LogInformation("Original and updated content saved to:\n  - {OriginalFile}\n  - {UpdatedFile}", originalFile, updatedFile);
            }
            else if (!changesMade && originalFile != null)
            {
                 _logger.LogInformation("No changes were made to the forum post. Original content saved to: {OriginalFile}", originalFile);
            }
            else if (!changesMade)
            {
                _logger.LogInformation("Skipping file saving as no changes were made and no Playwright interaction occurred.");
            }
             _logger.LogInformation("--- Finished processing batch ---");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("RosterBotWorker stopping.");

        _discordService.MessageReceived -= HandleIncomingMessage;

        _logger.LogInformation("Completing message channel writer.");
        _messageChannel.Writer.Complete();

        _logger.LogInformation("Waiting for processing task to finish...");
        if (_processingTask != null)
        {
            try
            {
                 await _processingTask.WaitAsync(cancellationToken);
                 _logger.LogInformation("Processing task completed.");
            }
            catch (OperationCanceledException)
            {
                 _logger.LogInformation("Processing task wait cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while waiting for processing task shutdown.");
            }
        } else {
             _logger.LogWarning("Processing task was null during StopAsync.");
        }

        _logger.LogInformation("RosterBotWorker stopped.");
        await base.StopAsync(cancellationToken);
    }
} 