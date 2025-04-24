using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ClergyRosterBot.Models;
using ClergyRosterBot.Utilities;

namespace ClergyRosterBot.Services;

public class InstructionParser
{
    private readonly ILogger<InstructionParser> _logger;

    public InstructionParser(ILogger<InstructionParser> logger)
    {
        _logger = logger;
    }

    public Instruction ParseInstruction(string line)
    {
        string originalLine = line;
        // _logger.LogTrace("Parsing line: {Line}", line);

        if (string.IsNullOrWhiteSpace(line))
            return new Instruction(InstructionType.Ignore, "Empty line", originalLine);

        string candidateLine = line.Trim();
        if (candidateLine.StartsWith("//") || candidateLine.StartsWith('#'))
            return new Instruction(InstructionType.Ignore, "Comment line", originalLine);

        if (IsQuestStart(line))
            return new Instruction(InstructionType.Ignore, "Quest start line", originalLine);

        // Normalize separators, collapse spaces, remove mentions
        candidateLine = Regex.Replace(candidateLine, @"[,:]+| [-–—]+ ", " - "); // Normalize separators
        candidateLine = Regex.Replace(candidateLine, @"\s+", " ").Trim(); // Collapse spaces
        candidateLine = Regex.Replace(candidateLine, @"@\s*\S+", "").Trim(); // Remove mentions

        // 1. Rename: (Old Name) > (New Name)
        var renameMatch = Regex.Match(candidateLine, @"^(.+?)\s+>\s+(.+)$", RegexOptions.IgnoreCase);
        if (renameMatch.Success)
        {
            string oldName = renameMatch.Groups[1].Value.Trim();
            string newName = renameMatch.Groups[2].Value.Trim();
            if (!string.IsNullOrEmpty(oldName) && !string.IsNullOrEmpty(newName) &&
                !string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Parsed RENAME: \"{OldName}\" => \"{NewName}\"", oldName, newName);
                return new Instruction(InstructionType.Rename, null, originalLine) { OldName = oldName, NewName = newName };
            }
            else
            {
                _logger.LogWarning("Invalid rename format or same names: {OriginalLine}", originalLine);
                return new Instruction(InstructionType.Error, "Invalid rename format or names are the same", originalLine);
            }
        }

        // 2. LOTH Status Update
        var lothRemoveMatch = Regex.Match(candidateLine, @"^(.+?)\s+no\s+longer\s+LOTH\s*$", RegexOptions.IgnoreCase);
        if (lothRemoveMatch.Success)
        {
            string name = lothRemoveMatch.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(name))
            {
                _logger.LogDebug("Parsed LOTH REMOVE: \"{Name}\"", name);
                return new Instruction(InstructionType.UpdateLOTH, null, originalLine) { CharacterName = name, MakeLOTH = false };
            }
        }

        var lothAddMatch = Regex.Match(candidateLine, @"^(.+?)\s+(?:is\s+)?now\s+LOTH\s*$", RegexOptions.IgnoreCase);
        if (lothAddMatch.Success)
        {
            string name = lothAddMatch.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(name))
            {
                _logger.LogDebug("Parsed LOTH ADD: \"{Name}\"", name);
                return new Instruction(InstructionType.UpdateLOTH, null, originalLine) { CharacterName = name, MakeLOTH = true };
            }
        }

        // 3. Remove Command
        if (IsRemoveCommand(candidateLine, out string? nameToRemove))
        {
            if (!string.IsNullOrEmpty(nameToRemove))
            {
                _logger.LogDebug("Parsed REMOVE: \"{NameToRemove}\"", nameToRemove);
                return new Instruction(InstructionType.Remove, null, originalLine) { CharacterName = nameToRemove };
            }
            else
            {
                _logger.LogWarning("Remove command found but no name specified: \"{OriginalLine}\"", originalLine);
                return new Instruction(InstructionType.Error, "Remove command without name", originalLine);
            }
        }

        // --- Extract LOTH Status and Remove Marker ---
        bool isLOTH = false;
        var lothSuffixMatch = Constants.LothSuffixRegex.Match(candidateLine);
        if (lothSuffixMatch.Success)
        {
            isLOTH = true;
            candidateLine = candidateLine.Substring(0, lothSuffixMatch.Index).Trim();
            _logger.LogDebug("Detected LOTH suffix, remaining line: \"{CandidateLine}\"", candidateLine);
        }

        // 4. High Priest(ess) Assignment
        var highPriestMatch = Constants.HighPriestAssignmentRegex.Match(candidateLine);
        if (highPriestMatch.Success)
        {
            string name = highPriestMatch.Groups[1].Value.Trim();
            string title = candidateLine.Contains("priestess", StringComparison.OrdinalIgnoreCase)
                           ? Constants.HighPriestessTitle : Constants.HighPriestTitle;
            if (!string.IsNullOrEmpty(name))
            {
                _logger.LogDebug("Parsed SET HIGH PRIEST: \"{Name}\" ({Title}), LOTH: {IsLOTH}", name, title, isLOTH);
                // Use TargetRank to store the title for SetHighPriest type
                return new Instruction(InstructionType.SetHighPriest, null, originalLine)
                    { CharacterName = name, TargetRank = title, IsLOTH = isLOTH };
            }
        }

        // 5. Add/Move Character
        bool finishingQuest = IsCurateQuestFinish(line);
        var parseResult = ParseAddMove(candidateLine, finishingQuest);

        if (parseResult.IsValid)
        {
            if (!Constants.BaseRanks.Contains(parseResult.Rank!))
            {
                 _logger.LogWarning("Instruction targets rank \"{Rank}\" which is not directly editable in tables. Cannot use ADD/MOVE for HP.", parseResult.Rank);
                 return new Instruction(InstructionType.Error, $"Cannot add/move to non-base rank {parseResult.Rank}", originalLine);
            }
             _logger.LogDebug("Parsed ADD/MOVE: Name=\"{Name}\", Rank=\"{Rank}\", Divine=\"{Divine}\", LOTH={IsLOTH}",
                              parseResult.Name, parseResult.Rank, parseResult.Divine, isLOTH);
            return new Instruction(InstructionType.Add, null, originalLine)
            {
                CharacterName = parseResult.Name,
                TargetRank = parseResult.Rank,
                TargetDivine = parseResult.Divine,
                IsLOTH = isLOTH
            };
        }
        else if (!string.IsNullOrEmpty(parseResult.Name))
        {
             if (!string.IsNullOrEmpty(parseResult.Rank) || !string.IsNullOrEmpty(parseResult.Divine))
             {
                _logger.LogWarning("Partially parsed ADD/MOVE line: Name=\"{Name}\", Rank={Rank}, Divine={Divine}. Missing info.",
                                 parseResult.Name, parseResult.Rank ?? "N/A", parseResult.Divine ?? "N/A");
                return new Instruction(InstructionType.Error, "Missing rank or divine for add/move", originalLine);
             }
             else
             {
                _logger.LogWarning("Line parsed down to only a name: \"{Name}\". Could be invalid, comment, or ambiguous removal.", parseResult.Name);
                return new Instruction(InstructionType.Error, "Only name found after parsing, ambiguous instruction", originalLine);
             }
        }

        // If none of the patterns matched successfully
        _logger.LogWarning("Could not parse line into a known instruction format: \"{OriginalLine}\"", originalLine);
        return new Instruction(InstructionType.Error, "Unrecognized format", originalLine);
    }

    private bool IsQuestStart(string line) => Constants.QuestStartRegex.IsMatch(line);
    private bool IsCurateQuestFinish(string line) => Constants.CurateQuestFinishRegex.IsMatch(line);

    private bool IsRemoveCommand(string line, out string? nameToRemove)
    {
        nameToRemove = null;
        var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0 && Constants.RemoveSynonyms.Contains(parts[0], StringComparer.OrdinalIgnoreCase))
        {
            nameToRemove = string.Join(" ", parts.Skip(1)).Trim();
            return true;
        }
        return false;
    }

    private (bool IsValid, string? Name, string? Rank, string? Divine) ParseAddMove(string line, bool finishingQuest)
    {
        string? targetDivine = null;
        string? targetRank = null;
        string remainingLine = line;

        // a. Explicit "Rank of Divine"
        var rodMatch = Constants.RankOfDivineRegex.Match(remainingLine);
        if (rodMatch.Success)
        {
            targetRank = StringExtensions.FuzzyMatch(rodMatch.Groups[1].Value, Constants.KnownRanks);
            targetDivine = StringExtensions.FuzzyMatch(rodMatch.Groups[2].Value, Constants.Divines);
            remainingLine = remainingLine.Replace(rodMatch.Value, "").Trim();
            _logger.LogTrace("Pattern A matched: Rank={Rank}, Divine={Divine}, Remaining: \"{Remaining}\"", targetRank, targetDivine, remainingLine);
        }
        else
        {
            // b. Look for "of Divine"
            var divineMatch = Constants.DivineSuffixRegex.Match(remainingLine);
            if (divineMatch.Success)
            {
                targetDivine = StringExtensions.FuzzyMatch(divineMatch.Groups[1].Value, Constants.Divines);
                remainingLine = remainingLine.Replace(divineMatch.Value, "").Trim();
                 _logger.LogTrace("Pattern B matched: Divine={Divine}, Remaining: \"{Remaining}\"", targetDivine, remainingLine);

                // c. Look for Rank in remaining words (fuzzy)
                var words = remainingLine.Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries);
                string? bestRankMatch = null;
                int bestRankDist = 3;
                string rankWord = string.Empty;
                foreach (var word in words)
                {
                    var potentialRank = StringExtensions.FuzzyMatch(word, Constants.KnownRanks);
                    if (potentialRank != null)
                    {
                        int dist = StringExtensions.LevenshteinDistance(word.ToLowerInvariant(), potentialRank.ToLowerInvariant());
                        if (dist < bestRankDist)
                        {
                            bestRankDist = dist;
                            bestRankMatch = potentialRank;
                            rankWord = word;
                        }
                    }
                }
                if (bestRankMatch != null)
                {
                    targetRank = bestRankMatch;
                    // Remove the matched rank word carefully
                    remainingLine = Regex.Replace(remainingLine, $@"\b{Regex.Escape(rankWord)}\b", "", RegexOptions.IgnoreCase).Trim();
                    _logger.LogTrace("Pattern C matched: Rank={Rank} (from word '{Word}'), Remaining: \"{Remaining}\"", targetRank, rankWord, remainingLine);
                }
            }
        }

        // Handle "Promoted to Curate of Divine"
        var promotedMatch = Constants.PromotedCurateRegex.Match(line); // Match original line
        if (promotedMatch.Success)
        {
            string? forcedDivine = StringExtensions.FuzzyMatch(promotedMatch.Groups[1].Value, Constants.Divines);
            if (forcedDivine != null)
            {
                _logger.LogDebug("Overriding with 'Promoted to Curate' pattern: Curate of {Divine}", forcedDivine);
                targetDivine = forcedDivine;
                targetRank = Constants.CurateRank;
                // Name is everything before "Promoted..."
                int promoIndex = line.IndexOf(promotedMatch.Value, StringComparison.OrdinalIgnoreCase);
                if (promoIndex > 0)
                {
                    remainingLine = line.Substring(0, promoIndex).Trim();
                }
                else
                {
                    remainingLine = string.Empty; // Should not happen if name exists
                }
            }
        }

        // Apply context: Curate Quest Finish
        if (finishingQuest && targetDivine != null && targetRank == null)
        {
             _logger.LogDebug("Applying Curate rank due to quest completion context for Divine {Divine}", targetDivine);
             targetRank = Constants.CurateRank;
        }

        // Normalize Priestess -> Priest unless High Priestess
        if (targetRank?.Equals(Constants.PriestessRank, StringComparison.OrdinalIgnoreCase) == true &&
            targetRank != Constants.HighPriestessTitle)
        {
            _logger.LogDebug("Normalizing 'Priestess' to 'Priest'");
            targetRank = Constants.PriestRank;
        }

        // e. Remaining part is the character name
        string characterName = remainingLine.Trim('-', ' ');

        bool isValid = !string.IsNullOrEmpty(characterName) &&
                       !string.IsNullOrEmpty(targetRank) &&
                       !string.IsNullOrEmpty(targetDivine);

        return (isValid, characterName, targetRank, targetDivine);
    }
} 