using Discord;
using Discord.WebSocket;
using System.Collections.Generic;

namespace ClergyRosterBot.Models;

/// <summary>
/// Holds the results of processing a batch of messages.
/// </summary>
public class RosterChangeResult
{
    public List<IMessage> Successes { get; } = new List<IMessage>();
    public List<MessageFailure> Failures { get; } = new List<MessageFailure>();
}

/// <summary>
/// Represents a failed message processing attempt.
/// </summary>
/// <param name="Message">The Discord message that failed.</param>
/// <param name="Reason">The reason for the failure.</param>
public record MessageFailure(IMessage Message, string Reason); 