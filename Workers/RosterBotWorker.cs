using ClergyRosterBot.Services;
using ClergyRosterBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

namespace ClergyRosterBot.Workers;

public class RosterBotWorker : BackgroundService
{
    private readonly ILogger<RosterBotWorker> _logger;
    private readonly DiscordService _discordService;
    private readonly PlaywrightService _playwrightService;
    private readonly InstructionParser _instructionParser;
    private readonly IServiceProvider _serviceProvider;
    private readonly BotSettings _settings;
    private readonly ConcurrentQueue<IMessage> _messageQueue;
    private readonly SemaphoreSlim _processingSemaphore; // Used as lock
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
        _messageQueue = new ConcurrentQueue<IMessage>();
        _processingSemaphore = new SemaphoreSlim(1, 1); // Initialize semaphore
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RosterBotWorker starting.");

        // Register the event handler for new messages
        _discordService.MessageReceived += HandleIncomingMessage;

        // Wait for Discord service to be ready before fetching history
        _logger.LogInformation("Waiting for Discord service to connect and become ready...");
        try
        {
            // Wait until the Discord client reports it's ready or the service stops
            await _discordService.Ready.WaitAsync(stoppingToken);
            _logger.LogInformation("Discord service is ready.");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Worker stopping before Discord service became ready.");
            return; // Exit if cancellation requested during wait
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error waiting for Discord service readiness.");
            // Optionally handle this error more gracefully, e.g., retry or shutdown
            return; // Exit if we can't confirm readiness
        }

        await FetchAndEnqueueHistoricalMessages(stoppingToken);

        _logger.LogInformation("RosterBotWorker running. Listening for messages.");

        // Keep the worker alive
        await Task.Delay(Timeout.Infinite, stoppingToken);
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
                // Enqueue them in the correct order (oldest first)
                foreach (var msg in unprocessedMessages.OrderBy(m => m.Timestamp))
                {
                    _messageQueue.Enqueue(msg);
                }
                TriggerProcessing(stoppingToken); // Start processing the queue
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
        _logger.LogInformation("Queueing new message {MessageId} for processing.", message.Id);
        _messageQueue.Enqueue(message);
        TriggerProcessing(CancellationToken.None); // Use CancellationToken.None for new messages
        return Task.CompletedTask;
    }

    private void TriggerProcessing(CancellationToken stoppingToken)
    {
        _logger.LogDebug("TriggerProcessing called. Checking conditions...");

        // Use the semaphore to ensure only one processing loop runs at a time.
        if (_processingSemaphore.CurrentCount == 0) // Check if semaphore is already taken (i.e., processing is active)
        {
             _logger.LogDebug("TriggerProcessing exiting: Semaphore is unavailable (CurrentCount = 0).");
             return;
        }

        if (_messageQueue.IsEmpty)
        {
            _logger.LogDebug("TriggerProcessing exiting: Message queue is empty.");
            return;
        }

        _logger.LogDebug("TriggerProcessing: Queue has {Count} items. Checking processing task state...", _messageQueue.Count);

        // If there's no active processing task or the previous one completed,
        // start a new one.
        bool shouldStart = _processingTask == null || _processingTask.IsCompleted;
        _logger.LogDebug("TriggerProcessing: _processingTask is null? {IsNull}. IsCompleted? {IsCompleted}. ShouldStart? {ShouldStart}",
                         _processingTask == null, _processingTask?.IsCompleted, shouldStart);

        if (shouldStart)
        {
             _logger.LogInformation("TriggerProcessing: Starting background processing task...");
             _processingTask = Task.Run(() => ProcessQueueAsync(stoppingToken), stoppingToken);
             // Add continuation to log task completion/fault status (optional but good practice)
             _processingTask.ContinueWith(t => {
                 if (t.IsFaulted)
                     _logger.LogError(t.Exception, "Background processing task faulted.");
                 else if (t.IsCanceled)
                     _logger.LogWarning("Background processing task was canceled.");
                 else
                     _logger.LogDebug("Background processing task completed successfully.");
             }, TaskScheduler.Default);
        }
        else
        {    _logger.LogDebug("TriggerProcessing exiting: Processing task already exists and is not completed (Status: {Status}).", _processingTask?.Status);
        }
    }

    private async Task ProcessQueueAsync(CancellationToken stoppingToken)
    {
        // Acquire the semaphore. If acquired, proceed. If not, another thread is processing.
        if (!await _processingSemaphore.WaitAsync(0, stoppingToken)) // Use 0 timeout for non-blocking check
        {
            _logger.LogDebug("Could not acquire processing lock; another task is active.");
            return;
        }

        _logger.LogInformation("Processing lock acquired. Starting batch processing.");

        try
        {
            while (!_messageQueue.IsEmpty && !stoppingToken.IsCancellationRequested)
            {
                var messagesToProcess = new List<IMessage>();
                while (_messageQueue.TryDequeue(out var msg))
                {
                    messagesToProcess.Add(msg);
                }

                if (messagesToProcess.Count == 0) continue;

                _logger.LogInformation("--- Processing batch of {Count} message(s) ---", messagesToProcess.Count);
                _logger.LogDebug("Message IDs in batch: {Ids}", string.Join(", ", messagesToProcess.Select(m => m.Id)));

                string? originalHtml = null;
                string? originalFile = null;
                string? updatedFile = null;
                bool changesMade = false;
                var results = new RosterChangeResult(); // Track outcomes
                bool isFatalPlaywrightError = false;

                try
                {
                    // --- FIRST PASS: Parse all messages, collect actionable instructions, and track errors/ignores ---
                    var instructionsToApply = new List<Models.Instruction>();
                    var messagesWithError = new HashSet<IMessage>();
                    var messagesIgnored = new HashSet<IMessage>();

                    foreach (var message in messagesToProcess)
                    {
                        var lines = (message.Content ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        if (lines.Length == 0) { messagesIgnored.Add(message); continue; }

                        // Check if first line starts with 'ignore'
                        if (lines[0].Trim().StartsWith("ignore", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogDebug("Skipping message {MessageId} because first line starts with 'ignore'.", message.Id);
                            messagesIgnored.Add(message);
                            continue; // Skip this entire message
                        }

                        bool messageHadError = false;
                        bool messageHadAction = false;
                        for (int i = 0; i < lines.Length; i++)
                        {
                            string line = lines[i].Trim();
                            if (string.IsNullOrEmpty(line)) continue;

                            var parsed = _instructionParser.ParseInstruction(line);
                            if (parsed.Type == Models.InstructionType.Error)
                            {
                                _logger.LogWarning("Failed parsing message {MessageId} (line {LineNum}): {Reason} - \"{OriginalLine}\"",
                                    message.Id, i + 1, parsed.Reason, parsed.OriginalLine);
                                results.Failures.Add(new MessageFailure(message, parsed.Reason ?? "Unknown parsing error"));
                                messageHadError = true;
                                break; // Stop processing further lines in this message
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

                    // --- ONLY invoke Playwright if there are actionable instructions ---
                    if (instructionsToApply.Any())
                    {
                        // Get current content from the forum *before* processing instructions
                        originalHtml = await _playwrightService.GetForumPostContentAndOpenEditorAsync();
                        originalFile = await _playwrightService.SaveContentToFileAsync(originalHtml, "-original");

                        // Create a new RosterState instance for this batch using DI
                        using var scope = _serviceProvider.CreateScope();
                        var rosterState = scope.ServiceProvider.GetRequiredService<RosterState>();
                        rosterState.ParseFromHtml(originalHtml);

                        _logger.LogInformation("Applying {Count} valid instructions to roster state...", instructionsToApply.Count);
                        rosterState.ApplyInstructions(instructionsToApply);
                        string updatedHtml = rosterState.RegenerateHtml();

                        // Compare generated HTML with original HTML
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
                        // No Playwright actions, so no files to save
                        originalFile = null;
                        updatedFile = null;
                    }

                    // React to messages based on outcome
                    foreach (var message in results.Successes)
                    {
                        await _discordService.ReactToMessageAsync(message.Id, SuccessEmoji);
                    }
                    foreach (var failure in results.Failures)
                    {
                        if (!_settings.SuppressErrorFeedback)
                        {
                            await _discordService.ReactToMessageAsync(failure.Message.Id, ErrorEmoji);
                            await _discordService.ReplyToMessageAsync(failure.Message.Id, $"Failed to parse command (Reason: {failure.Reason}). Check format.");
                        }
                    }
                    foreach (var ignored in messagesIgnored)
                    {
                        // Optionally react to ignored messages, or just log
                        _logger.LogDebug("Message {MessageId} was ignored (no actionable instructions).", ignored.Id);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unrecoverable error during batch processing.");

                    // Check if it looks like a Playwright error
                    if (_playwrightService.IsPageAvailable || ex is PlaywrightException || (ex.Message?.Contains("Playwright", StringComparison.OrdinalIgnoreCase) ?? false))
                    {
                        isFatalPlaywrightError = true;
                        await _playwrightService.TakeScreenshotAsync(); // Attempt screenshot
                    }

                    // Attempt to notify Discord about the failure for this batch
                    var firstMessage = messagesToProcess.FirstOrDefault();
                    if (firstMessage != null && !_settings.SuppressErrorFeedback) // Check suppression setting
                    {
                        await _discordService.ReplyToMessageAsync(firstMessage.Id, $"An unexpected error occurred: {ex.Message}. Commands might not have been applied.");
                        // React with ❌ only if NOT a fatal Playwright error AND feedback not suppressed
                        if (!isFatalPlaywrightError)
                        {
                            foreach (var msg in messagesToProcess)
                            {
                                await _discordService.ReactToMessageAsync(msg.Id, FatalErrorEmoji);
                            }
                        }
                        else
                        {
                             _logger.LogError("Skipping ❌ emoji reactions due to fatal Playwright automation error.");
                        }
                    }
                    else if (firstMessage != null)
                    {
                        _logger.LogWarning("Suppressing error feedback for batch processing error as requested by settings.");
                    }
                     // Rethrow or handle -> Rethrowing stops the worker potentially
                     // throw;
                }
                finally
                {
                    if (changesMade && originalFile != null && updatedFile != null && originalFile != updatedFile)
                    {
                        _logger.LogInformation("Original and updated content saved to:\n  - {OriginalFile}\n  - {UpdatedFile}", originalFile, updatedFile);
                    }
                    else if (!changesMade)
                    {
                        _logger.LogInformation("Skipping file saving diff indication as no changes were made to the forum post.");
                    }
                    _logger.LogInformation("--- Finished processing batch ---");
                    _processingSemaphore.Release(); // Release the lock
                    _logger.LogInformation("Processing lock released.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Processing queue cancelled due to application stopping.");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Unhandled exception in processing queue loop.");
        }

        // Check if new messages arrived while processing and trigger again if needed
        if (!_messageQueue.IsEmpty && !stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Messages arrived during processing. Triggering again.");
            // Use Task.Run to avoid making this method async void or blocking
            // CS4014 Fix: Explicitly discard the task
            _ = Task.Run(() => TriggerProcessing(stoppingToken), stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("RosterBotWorker stopping.");

        // Unsubscribe from events
        _discordService.MessageReceived -= HandleIncomingMessage;

        // Signal the processing loop to stop and wait for it
        // (This requires ProcessQueueAsync to respect the cancellation token)
        if (_processingTask != null)
        {
            try
            {
                await _processingTask; // Wait for the current batch to finish (or cancel)
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Processing task cancelled successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during processing task shutdown.");
            }
        }

        await base.StopAsync(cancellationToken);
    }
} 