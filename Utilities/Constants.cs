using System.Text.RegularExpressions;

namespace ClergyRosterBot.Utilities;

public static partial class Constants
{
    public static readonly string[] Divines = {
        "Akatosh", "Arkay", "Dibella", "Julianos",
        "Kynareth", "Mara", "Stendarr", "Zenithar"
    };

    public static readonly string[] KnownRanks = {
        "Priest", "Priestess", "Curate", "Prior", "Acolyte", "High Priestess", "High Priest"
    };

    // Ranks expected within the table cells for direct manipulation
    public static readonly string[] BaseRanks = { "Priest", "Curate", "Prior", "Acolyte" };

    public const string DefaultHighPriestTitle = "High Priest";
    public const string HighPriestTitle = "High Priest";
    public const string HighPriestessTitle = "High Priestess";
    public const string PriestRank = "Priest";
    public const string PriestessRank = "Priestess"; // For normalization checks
    public const string CurateRank = "Curate";

    public static readonly string[] VacantPlaceholders = { "—", "vacant", "-" };

    public static readonly string[] RemoveSynonyms = { "remove", "delete", "purge", "clear", "expunge", "rm", "rem" };

    // Regex for parsing
    public static readonly Regex HighPriestRegex = GenerateHighPriestRegex();
    public static readonly Regex LothSuffixRegex = GenerateLothSuffixRegex();
    public static readonly Regex HighPriestAssignmentRegex = GenerateHighPriestAssignmentRegex();
    public static readonly Regex RankOfDivineRegex = new Regex($@"\b({string.Join('|', KnownRanks)})\s+of\s+({string.Join('|', Divines)})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public static readonly Regex DivineSuffixRegex = new Regex($@"\bof\s+({string.Join('|', Divines)})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public static readonly Regex PromotedCurateRegex = GeneratePromotedCurateRegex();
    public static readonly Regex QuestStartRegex = GenerateQuestStartRegex();
    public static readonly Regex CurateQuestFinishRegex = GenerateCurateQuestFinishRegex();


    // Pre-compiled Regex definitions using source generators
    [GeneratedRegex(@"^#?\s*Current\s+(High\s+Priest(?:ess)?)\s*[:\\s-]+\s*(.*)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex GenerateHighPriestRegex();

    [GeneratedRegex(@"(?:\s-)?\s*(?:\(\s*LOTH\s*\)|LOTH)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex GenerateLothSuffixRegex();

    [GeneratedRegex(@"^(?:(?:High\s+Priest(?:ess)?)|HP)\s*[:\\s-]+\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex GenerateHighPriestAssignmentRegex();

    [GeneratedRegex(@"\bPromoted\s+to\s+Curate\s+of\s+(\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex GeneratePromotedCurateRegex();

    [GeneratedRegex(@"^\s*(?:Beginning|Starting)\s+Curate\s+Quest(?:\s+for\s+\S+)?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex GenerateQuestStartRegex();

    [GeneratedRegex(@"^\s*(?:Completing|Finishing|Finished|Completed)\s+Curate\s+Quest(?:\s+for\s+\S+)?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex GenerateCurateQuestFinishRegex();
} 