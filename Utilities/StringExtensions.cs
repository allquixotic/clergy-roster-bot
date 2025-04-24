using System.Text.RegularExpressions;

namespace ClergyRosterBot.Utilities;

public static partial class StringExtensions
{
    private static readonly Regex LothMarkerRegex = GenerateLothMarkerRegex();
    private const string LothHtmlEntity = "&#42;"; // Note the missing semicolon in original JS; assuming it should be there.
    private const string LothHtmlEntityWithSpace = " &#42;";

    /// <summary>
    /// Normalizes a name by removing HTML tags, LOTH markers, and trimming.
    /// </summary>
    public static string NormalizeNameForComparison(this string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        // Remove HTML tags (simple regex, might not cover all edge cases)
        string cleaned = Regex.Replace(name, "<[^>]*>", string.Empty);
        // Remove LOTH markers (regex defined below)
        cleaned = LothMarkerRegex.Replace(cleaned, string.Empty);

        return cleaned.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Ensures LOTH marker is the HTML entity with a preceding space.
    /// </summary>
    public static string NormalizeLothSuffix(this string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        // Replace space+asterisk or just asterisk at end with space+entity
        string cleaned = Regex.Replace(name.TrimEnd(), @"\s?\*$", LothHtmlEntityWithSpace);
        // Ensure existing entity has space
        if (cleaned.EndsWith(LothHtmlEntity) && !cleaned.EndsWith(LothHtmlEntityWithSpace))
        {
            cleaned = cleaned.Substring(0, cleaned.Length - LothHtmlEntity.Length).TrimEnd() + LothHtmlEntityWithSpace;
        }
        return cleaned;
    }

    /// <summary>
    /// Checks if two names match using normalized comparison.
    /// </summary>
    public static bool MatchesName(this string? existingName, string? newName)
    {
        return NormalizeNameForComparison(existingName) == NormalizeNameForComparison(newName);
    }

    /// <summary>
    /// Computes the Levenshtein distance between two strings.
    /// </summary>
    public static int LevenshteinDistance(this string? a, string? b)
    {
        // Handle nulls or empties
        int n = a?.Length ?? 0;
        int m = b?.Length ?? 0;
        if (n == 0) return m;
        if (m == 0) return n;

        // Create matrix
        int[,] d = new int[n + 1, m + 1];

        // Initialize borders
        for (int i = 0; i <= n; d[i, 0] = i++) {}
        for (int j = 0; j <= m; d[0, j] = j++) {}

        // Fill matrix
        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                int cost = (b![j - 1] == a![i - 1]) ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }
        return d[n, m];
    }

    /// <summary>
    /// Finds the best fuzzy match for an input string from a list of candidates using Levenshtein distance.
    /// </summary>
    /// <param name="input">The string to match.</param>
    /// <param name="candidates">The list of potential matches.</param>
    /// <param name="maxDistanceThreshold">The maximum allowed Levenshtein distance (inclusive).</param>
    /// <returns>The best matching candidate (original casing) or null if no match is within the threshold.</returns>
    public static string? FuzzyMatch(this string? input, IEnumerable<string> candidates, int maxDistanceThreshold = 3)
    {
        if (string.IsNullOrWhiteSpace(input) || candidates?.Any() != true) return null;

        string normInput = input.Trim().ToLowerInvariant();
        int bestDist = int.MaxValue;
        string? bestMatch = null;

        foreach (string candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            string normC = candidate.ToLowerInvariant();
            int dist = LevenshteinDistance(normInput, normC);

            if (dist < bestDist)
            {
                bestDist = dist;
                bestMatch = candidate; // Return original casing
            }
        }

        return bestDist <= maxDistanceThreshold ? bestMatch : null;
    }

    [GeneratedRegex(@"\s*(\(LOTH\)|\*|&#42;)\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex GenerateLothMarkerRegex();
} 