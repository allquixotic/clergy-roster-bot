using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace ClergyRosterBot.Models
{
    public class RosterState
    {
        private static readonly string[] DIVINES = {
            "Akatosh", "Arkay", "Dibella", "Julianos",
            "Kynareth", "Mara", "Stendarr", "Zenithar"
        };
        // Ranks expected within the table cells for direct manipulation
        private static readonly string[] BASE_RANKS = { "Priest", "Curate", "Prior", "Acolyte" };
        private const string LOTH_MARKER_ENTITY = " &#42;"; // Consistent LOTH marker
        private const string LOTH_MARKER_LITERAL = " *";

        // Internal data representation - remains the same
        public List<Dictionary<string, List<string>>> AllDivineData { get; private set; }
        public string? HighPriestName { get; private set; }
        public string HighPriestTitle { get; private set; }

        // Stores the parsed HTML document for manipulation
        private HtmlDocument? _htmlDoc;
        private readonly ILogger<RosterState> _logger; // Assuming logger injection if needed

        // Constructor (logger optional, depends on DI setup)
        public RosterState(ILogger<RosterState> logger) // Add ILogger if you use it here
        {
             _logger = logger; // Store logger
            AllDivineData = DIVINES.Select(_ => new Dictionary<string, List<string>> {
                { "Priest", new List<string>() },
                { "Curate", new List<string>() },
                { "Prior", new List<string>() },
                { "Acolyte", new List<string>() }
            }).ToList();
            HighPriestName = null;
            HighPriestTitle = "High Priest"; // Default title
        }


        // Parses the initial HTML content to populate the state AND store the DOM
        public void ParseFromHtml(string htmlContent)
        {
             _htmlDoc = new HtmlDocument();
             // --- Configuration for HtmlAgilityPack ---
             // Preserve original whitespace as much as possible
             _htmlDoc.OptionPreserveXmlNamespaces = true;
             _htmlDoc.OptionWriteEmptyNodes = true; // Write tags like <br />
             _htmlDoc.OptionOutputOriginalCase = true; // Maintain original tag/attribute casing
             // Don't add missing tags, keep it close to original
             _htmlDoc.OptionAutoCloseOnEnd = false;
             _htmlDoc.OptionFixNestedTags = false;

             _htmlDoc.LoadHtml(htmlContent);

             if (_htmlDoc.ParseErrors != null && _htmlDoc.ParseErrors.Any())
             {
                 _logger?.LogWarning("HTML parsing errors detected:");
                 foreach (var error in _htmlDoc.ParseErrors)
                 {
                     _logger?.LogWarning("- {ErrorCode} at Line {Line}, Pos {Pos}: {Reason}", error.Code, error.Line, error.LinePosition, error.Reason);
                 }
             }
             if (_htmlDoc.DocumentNode == null)
             {
                 _logger?.LogError("Failed to load HTML document. DocumentNode is null.");
                 throw new InvalidOperationException("Failed to load HTML document for parsing.");
             }

            // --- Find the roster tables using XPath ---
            // Adjust XPath if table structure is different or more specific selectors are needed
            // This XPath looks for tables that have a <tr> with valign=top, and that tr has at least 4 <td> children
            var tableNodes = _htmlDoc.DocumentNode.SelectNodes("//table[.//tr[@valign='top'] and count(.//tr[@valign='top'][1]/td) >= 4]");

            if (tableNodes == null || tableNodes.Count < 2)
            {
                var count = tableNodes?.Count ?? 0;
                _logger?.LogError("Could not find the expected 2 roster tables using XPath. Found: {Count}", count);
                throw new FormatException($"Could not find the expected 2 roster tables in the HTML. Found: {count}");
            }

            var firstTable = tableNodes[0];
            var secondTable = tableNodes[1];

            // Find the specific data row (assuming second tr with valign=top) - Adjust index [1] if needed
            var firstTableDataRow = firstTable.SelectSingleNode(".//tr[@valign='top'][2]"); // XPath index is 1-based, LINQ index was 0-based
            var secondTableDataRow = secondTable.SelectSingleNode(".//tr[@valign='top'][2]");

            if (firstTableDataRow == null || secondTableDataRow == null)
            {
                 _logger?.LogError("Could not find data rows (tr valign=top, second instance) in roster tables.");
                 throw new FormatException("Could not find data rows (tr valign=top, second instance) in roster tables.");
            }

            var firstTableTds = firstTableDataRow.SelectNodes("./td");
            var secondTableTds = secondTableDataRow.SelectNodes("./td");

             if (firstTableTds == null || secondTableTds == null || firstTableTds.Count < 4 || secondTableTds.Count < 4)
             {
                  _logger?.LogError("Insufficient columns found in table data rows (need 4, found {Count1}, {Count2})", firstTableTds?.Count ?? 0, secondTableTds?.Count ?? 0);
                  throw new FormatException($"Insufficient columns found in table data rows (need 4, found {firstTableTds?.Count ?? 0}, {secondTableTds?.Count ?? 0})");
             }

             var divineTds = firstTableTds.Take(4).Concat(secondTableTds.Take(4)).ToList();

             for (int i = 0; i < divineTds.Count && i < DIVINES.Length; i++)
             {
                 // Parse data *from* the HtmlNode into our internal dictionary
                 AllDivineData[i] = ParseDivineCellData(divineTds[i]);
             }

              _logger?.LogDebug("Initial parsed table data: {Data}", System.Text.Json.JsonSerializer.Serialize(AllDivineData)); // Use appropriate serializer


             // --- Extract High Priest ---
             // Regex matching the JavaScript version
             var highPriestRegex = new Regex(@"#?\s*Current\s+High\s+Priest(ess)?\s*[:|-]?\s+(.*)", RegexOptions.IgnoreCase);
             bool foundHpLine = false;

             // Select potential text nodes within common containing elements (p, div, h1-h4, or body itself)
             // XPath //text() selects all text nodes in the document. We filter common parents.
             var textNodes = _htmlDoc?.DocumentNode.SelectNodes("//p/text() | //div/text() | //h1/text() | //h2/text() | //h3/text() | //h4/text() | //body/text()[normalize-space()]");

             if (textNodes != null)
             {
                foreach (var node in textNodes)
                {
                    // CS8602 Fix: Assign InnerText to variable after null check
                    var innerText = node?.InnerText;
                    var txt = !string.IsNullOrEmpty(innerText) ? HtmlEntity.DeEntitize(innerText)?.Trim() : string.Empty;
                    var match = highPriestRegex.Match(txt!);
                    if (match.Success)
                    {
                        var rawName = match.Groups[2].Value.Trim();
                        HighPriestName = NormalizeLothMarker(rawName); // Store with consistent entity marker
                        HighPriestTitle = match.Groups[1].Value.Contains("ess", StringComparison.OrdinalIgnoreCase) ? "High Priestess" : "High Priest";
                        _logger?.LogInformation("Found High Priest: {Name} ({Title})", HighPriestName ?? "None", HighPriestTitle);
                        foundHpLine = true;
                        break; // Stop searching
                    }
                }
             }

             if (!foundHpLine)
             {
                 _logger?.LogWarning("High Priest line not found using regex.");
                 // HighPriestName remains null, HighPriestTitle remains default
             }
        }

         // Parses the data for one Divine cell FROM the HtmlNode into a dictionary
         private Dictionary<string, List<string>> ParseDivineCellData(HtmlNode cellNode)
         {
             var ranks = new Dictionary<string, List<string>> {
                 { "Priest", new List<string>() },
                 { "Curate", new List<string>() },
                 { "Prior", new List<string>() },
                 { "Acolyte", new List<string>() }
             };

             // Find rank headers: <span><u>RankName</u></span> using XPath relative to the cell
             var rankHeaderNodes = cellNode.SelectNodes(".//span/u/.."); // Select the parent span

             if (rankHeaderNodes == null)
             {
                _logger?.LogWarning("No rank headers (span > u) found in a divine cell.");
                return ranks; // Return empty ranks
             }

             for (int i = 0; i < rankHeaderNodes.Count; i++)
             {
                 var spanNode = rankHeaderNodes[i];
                 // CS8602 Fix: Check if spanNode itself is null
                 if (spanNode == null) continue;
                 var uNode = spanNode.SelectSingleNode("./u");
                 if (uNode == null) continue;

                 var rankName = HtmlEntity.DeEntitize(uNode.InnerText)?.Trim();
                 if (rankName == null) continue; // Skip if rankName is null to avoid null key usage
                 if (!BASE_RANKS.Contains(rankName)) continue; // Skip unexpected ranks

                 // --- Collect nodes between this header and the next ---
                 var currentRankNames = new List<string>();
                 // CS8602 Fix: spanNode is checked above, so NextSibling access is safer
                 var currentNode = spanNode.NextSibling;
                 HtmlNode? nextHeaderNode = (i + 1 < rankHeaderNodes.Count) ? rankHeaderNodes[i + 1] : null;

                 while (currentNode != null)
                 {
                     // Stop if we hit the next rank header
                     if (nextHeaderNode != null && currentNode == nextHeaderNode)
                     {
                         break;
                     }

                     // Process text nodes and <br> tags to extract names
                     if (currentNode.NodeType == HtmlNodeType.Text)
                     {
                         // Split text by newline chars, trim, filter empty, check for placeholders
                         // CS8602 Fix: Assign InnerText to variable after null check
                         var nodeText = currentNode?.InnerText;
                         var lines = !string.IsNullOrEmpty(nodeText) ? nodeText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
                         foreach (var line in lines)
                         {
                             var trimmedLine = HtmlEntity.DeEntitize(line)?.Trim();
                             if (!string.IsNullOrEmpty(trimmedLine) && !IsPlaceholderName(trimmedLine))
                             {
                                 // CS8604 Fix: Check if normalized name is non-null before adding
                                 var normalizedName = NormalizeLothMarker(trimmedLine);
                                 if (normalizedName != null)
                                 {
                                     currentRankNames.Add(normalizedName); // Store with consistent marker
                                 }
                             }
                         }
                     }
                     else if (currentNode.NodeType == HtmlNodeType.Element)
                     {
                         // Could also check for names wrapped in other spans (e.g., color spans) if needed
                         // Example: if (currentNode.Name == "span") { ... extract text ... }
                         // For now, primarily rely on text nodes and BRs as separators
                         if (currentNode.Name == "br")
                         {
                             // Handled by splitting text nodes by newline above
                         }
                         else
                         {
                              // Check if the element itself contains a name (e.g., <span style="color:red">Name *</span>)
                               // CS8602 Fix: Assign InnerText to variable after null check
                               var elementNodeText = currentNode?.InnerText;
                               var elementText = !string.IsNullOrEmpty(elementNodeText) ? HtmlEntity.DeEntitize(elementNodeText)?.Trim() : string.Empty;
                              if (!string.IsNullOrEmpty(elementText) && !IsPlaceholderName(elementText))
                              {
                                  // CS8604 Fix: Check if normalized name is non-null before adding
                                  var normalizedName = NormalizeLothMarker(elementText);
                                  if (normalizedName != null)
                                  {
                                      currentRankNames.Add(normalizedName); // Includes marker if present in InnerText
                                  }
                              }
                         }
                     }

                     currentNode = currentNode?.NextSibling;
                 }

                 if (rankName != null && ranks.ContainsKey(rankName))
                 {
                     ranks[rankName].AddRange(currentRankNames);
                 }
             }

             return ranks;
         }

        // Helper to check for common placeholder names
        private bool IsPlaceholderName(string name)
        {
            string lowerName = name.ToLowerInvariant();
            return lowerName == "—" || lowerName == "vacant" || lowerName == "-";
        }

        // Helper to normalize LOTH representation to use HTML entity consistently
        private string? NormalizeLothMarker(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return name;

            // Check for literal asterisk (potentially with space) and replace with entity
            if (name.EndsWith(LOTH_MARKER_LITERAL))
            {
                return name.Substring(0, name.Length - LOTH_MARKER_LITERAL.Length).TrimEnd() + LOTH_MARKER_ENTITY;
            }
            // Check for already existing entity (potentially with space)
            if (name.EndsWith(LOTH_MARKER_ENTITY))
            {
                 // Ensure no extra space before entity
                 return name.Substring(0, name.Length - LOTH_MARKER_ENTITY.Length).TrimEnd() + LOTH_MARKER_ENTITY;
            }
            // Also handle case where entity is used but no space before it
            if (name.EndsWith("&#42;"))
            {
                return name.Substring(0, name.Length - 5).TrimEnd() + LOTH_MARKER_ENTITY;
            }

            // If marker not found, return original name trimmed
            return name.Trim();
        }

        // Helper to normalize names for comparison (ignore case, LOTH marker, spans)
        private string NormalizeNameForComparison(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;

             // Basic HTML tag removal (could be more robust if complex HTML is inside names)
             name = Regex.Replace(name, "<.*?>", string.Empty);

             // Remove LOTH markers (entity or literal)
             name = name.Replace(LOTH_MARKER_ENTITY, string.Empty)
                        .Replace(LOTH_MARKER_LITERAL, string.Empty)
                        .Replace("&#42;", string.Empty) // Handle entity without preceding space if needed
                        .Replace("*", string.Empty);    // Handle literal without preceding space

             return name.Trim().ToLowerInvariant();
        }

        // Applies a list of parsed instructions to the current state
        // This part modifies the *internal data*, not the DOM directly yet
        public void ApplyInstructions(List<Instruction> instructions)
        {
            _logger?.LogInformation("Applying {Count} instructions to internal state...", instructions.Count);
            foreach (var instruction in instructions)
            {
                ApplySingleInstruction(instruction);
            }
            AlphabetizeRanks(); // Alphabetize internal data after all changes
            _logger?.LogDebug("Internal state after applying instructions: {Data}", System.Text.Json.JsonSerializer.Serialize(AllDivineData));
        }

        // Dispatches a single instruction to the appropriate handler
        // Modifies internal data (AllDivineData, HighPriestName)
         private void ApplySingleInstruction(Instruction instruction)
         {
             _logger?.LogDebug("Applying instruction: {Type} - {Details}", instruction.Type, instruction.OriginalLine);
             try
             {
                 switch (instruction.Type)
                 {
                     case InstructionType.Remove:
                         RemoveByName(instruction.CharacterName);
                         break;
                     case InstructionType.Add:
                         // Ensure character isn't listed elsewhere before adding
                         RemoveCharacterFromAll(instruction.CharacterName);
                         // CS8604 Fix: Check if CharacterName is null before passing
                         if (instruction.CharacterName != null)
                         {
                             AddCharacter(instruction.CharacterName, instruction.TargetDivine, instruction.TargetRank, instruction.IsLOTH);
                         }
                         break;
                     case InstructionType.Rename:
                         RenameCharacter(instruction.OldName, instruction.NewName);
                         break;
                     case InstructionType.SetHighPriest:
                         // CS1061 Fix: Use CharacterName for the name and TargetRank for the title
                         SetHighPriest(instruction.CharacterName, instruction.TargetRank ?? "High Priest", instruction.IsLOTH); // Provide default title if null
                         break;
                     case InstructionType.UpdateLOTH:
                         // CS1503 Fix: Handle nullable bool? MakeLOTH
                         if (instruction.MakeLOTH.HasValue)
                         {
                             UpdateLOTHStatus(instruction.CharacterName, instruction.MakeLOTH.Value);
                         }
                         break;
                     case InstructionType.Error:
                     case InstructionType.Ignore:
                         _logger?.LogDebug("Skipping instruction of type {Type}: {OriginalLine}", instruction.Type, instruction.OriginalLine);
                         break;
                     default:
                         _logger?.LogWarning("Unknown instruction type encountered: {Type}", instruction.Type);
                         break;
                 }
             }
             catch (Exception ex)
             {
                  _logger?.LogError(ex, "Failed to apply instruction: {InstructionJson}", System.Text.Json.JsonSerializer.Serialize(instruction));
                  // Decide if processing should halt or continue
             }
         }


         // --- Data Modification Methods (Operating on internal AllDivineData, HighPriestName) ---
         // These methods are largely the same logic as before, but operate on the internal collections.

        private void SetHighPriest(string? name, string title, bool isLOTH = false)
        {
            var finalName = (name ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(finalName) && isLOTH)
            {
                finalName += LOTH_MARKER_ENTITY; // Ensure entity format
            }
            finalName = string.IsNullOrEmpty(finalName) ? null : NormalizeLothMarker(finalName); // Ensure consistent LOTH if present

            _logger?.LogInformation("Setting High Priest to: {Name} ({Title})", finalName ?? "None", title);

            var normalizedNewName = NormalizeNameForComparison(finalName);
            var normalizedOldName = NormalizeNameForComparison(HighPriestName);

            if (normalizedOldName != normalizedNewName || HighPriestTitle != title)
            {
                if (!string.IsNullOrEmpty(HighPriestName) && normalizedOldName != normalizedNewName)
                {
                     _logger?.LogInformation("Replacing previous High Priest {OldName} ({OldTitle})", HighPriestName, HighPriestTitle);
                     // Remove the old HP from base ranks if they were also listed (unlikely but possible)
                     RemoveCharacterFromAll(HighPriestName);
                }
                else if (string.IsNullOrEmpty(HighPriestName))
                {
                    _logger?.LogInformation("Assigning initial High Priest.");
                }
                else
                {
                     _logger?.LogInformation("Updating High Priest details (possibly just LOTH status or title).");
                }

                HighPriestName = finalName;
                HighPriestTitle = title;
                // Also ensure the *new* HP is not in any base ranks
                if (HighPriestName != null)
                {
                    RemoveCharacterFromAll(HighPriestName);
                }
            }
            else
            {
                _logger?.LogDebug("High Priest details effectively unchanged: {Name} ({Title})", finalName ?? "None", title);
            }
        }

         // Removes a character from all BASE_RANKS lists in internal data.
         private bool RemoveCharacterFromAll(string? name)
         {
             if (string.IsNullOrWhiteSpace(name)) return false;
             _logger?.LogDebug("Ensuring '{Name}' is removed from all internal base ranks.", name);
             bool removed = false;
             var normNameToRemove = NormalizeNameForComparison(name);
             if (string.IsNullOrEmpty(normNameToRemove)) return false;

             foreach (var divineData in AllDivineData)
             {
                 foreach (var rank in BASE_RANKS)
                 {
                     var initialCount = divineData[rank].Count;
                     divineData[rank].RemoveAll(existingName =>
                     {
                         bool shouldRemove = NormalizeNameForComparison(existingName) == normNameToRemove;
                         if (shouldRemove)
                         {
                             removed = true;
                             _logger?.LogInformation("Removed {ExistingName} from internal {Rank} list.", existingName, rank);
                         }
                         return shouldRemove;
                     });
                 }
             }
             return removed;
         }

        private void AddCharacter(string name, string? divineName, string? rank, bool loth = false)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(divineName) || string.IsNullOrWhiteSpace(rank))
            {
                _logger?.LogWarning("AddCharacter called with missing information: Name='{Name}', Divine='{Divine}', Rank='{Rank}'", name, divineName, rank);
                return;
            }

            var divineIndex = Array.IndexOf(DIVINES, divineName);
            if (divineIndex == -1)
            {
                _logger?.LogWarning("Divine not found for add: {DivineName}", divineName);
                return;
            }
             if (!BASE_RANKS.Contains(rank))
            {
                 _logger?.LogWarning("Invalid target rank for add/move: {Rank}. Must be one of {BaseRanks}", rank, string.Join(", ", BASE_RANKS));
                 return;
            }

            var ranks = AllDivineData[divineIndex];
            string displayName = name.Trim();
            if (loth) displayName += LOTH_MARKER_ENTITY; // Use HTML entity
            displayName = NormalizeLothMarker(displayName)!; // Ensure format consistency


            var normDisplayName = NormalizeNameForComparison(displayName);
            var existingIndex = ranks[rank].FindIndex(existingName => NormalizeNameForComparison(existingName) == normDisplayName);

            if (existingIndex == -1)
            {
                // Doesn't exist, add it
                ranks[rank].Add(displayName);
                _logger?.LogInformation("Added '{DisplayName}' to internal {Rank} list for {DivineName}", displayName, rank, divineName);
            }
            else
            {
                 // Exists, check if LOTH status (full string including marker) needs update
                 var currentEntry = ranks[rank][existingIndex];
                 if (currentEntry != displayName)
                 {
                     _logger?.LogInformation("Updating LOTH status for existing '{Name}' in internal {Rank} list for {DivineName}: '{CurrentEntry}' => '{DisplayName}'", name, rank, divineName, currentEntry, displayName);
                     ranks[rank][existingIndex] = displayName; // Update the entry
                 }
                 else
                 {
                     _logger?.LogDebug("'{Name}' already exists in internal {Rank} list for {DivineName} with correct LOTH status, skipping duplicate add.", name, rank, divineName);
                 }
            }
        }


         // Finds character occurrences in the internal data structure.
         private List<(string name, string? divine, string? rank, string? special, string? title)> FindCharacterOccurrences(string? name)
         {
             var occurrences = new List<(string name, string? divine, string? rank, string? special, string? title)>();
             if (string.IsNullOrWhiteSpace(name)) return occurrences;

             var queryNorm = NormalizeNameForComparison(name);
             if (string.IsNullOrEmpty(queryNorm)) return occurrences;

             // Check High Priest
             if (HighPriestName != null && NormalizeNameForComparison(HighPriestName) == queryNorm)
             {
                 occurrences.Add((HighPriestName, null, null, "HighPriest", HighPriestTitle));
             }

             // Check regular ranks
             for (int d = 0; d < AllDivineData.Count; d++)
             {
                 foreach (var rank in BASE_RANKS)
                 {
                    var rankData = AllDivineData[d][rank];
                    foreach (var existingName in rankData)
                    {
                        if (NormalizeNameForComparison(existingName) == queryNorm)
                        {
                             // Avoid adding duplicates if somehow listed twice (shouldn't happen with RemoveAll logic)
                             if (!occurrences.Any(o => o.name == existingName && o.rank == rank && o.divine == DIVINES[d]))
                             {
                                occurrences.Add((existingName, DIVINES[d], rank, null, null));
                             }
                        }
                    }
                 }
             }

             return occurrences;
         }


         private void RemoveByName(string? name)
         {
             if (string.IsNullOrWhiteSpace(name)) return;
             _logger?.LogInformation("Processing internal removal request for: '{Name}'", name);
             var matches = FindCharacterOccurrences(name);

             if (matches.Count == 0)
             {
                 _logger?.LogWarning("No character found matching '{Name}' in internal data for removal.", name);
                 return;
             }

             if (matches.Count > 1)
             {
                  _logger?.LogWarning("Ambiguous internal removal: '{Name}' matched {Count} entries. Removing all found.", name, matches.Count);
             }

             foreach (var m in matches)
             {
                 var normMatchName = NormalizeNameForComparison(m.name);

                 if (m.special == "HighPriest")
                 {
                     // Check current HP matches the one found during the search
                     if (HighPriestName != null && NormalizeNameForComparison(HighPriestName) == normMatchName)
                     {
                         _logger?.LogInformation("Removing High Priest from internal state: {Name}", HighPriestName);
                         HighPriestName = null;
                         // Keep default title or reset? Keep default for now.
                     }
                 }
                 else if (m.divine != null && m.rank != null)
                 {
                     var dIndex = Array.IndexOf(DIVINES, m.divine);
                     if (dIndex != -1 && BASE_RANKS.Contains(m.rank))
                     {
                         var rankData = AllDivineData[dIndex][m.rank];
                         rankData.RemoveAll(n => NormalizeNameForComparison(n) == normMatchName);
                         // Logged within RemoveAll call in RemoveCharacterFromAll if used, or log here
                     }
                 }
             }
              // Ensure consistency by calling RemoveCharacterFromAll even if HP was removed
              RemoveCharacterFromAll(name);
         }

         private void RenameCharacter(string? oldName, string? newName)
         {
             if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName))
             {
                 _logger?.LogWarning("Rename requested with empty old or new name.");
                 return;
             }

              _logger?.LogInformation("Processing internal rename: '{OldName}' => '{NewName}'", oldName, newName);
             if (NormalizeNameForComparison(oldName) == NormalizeNameForComparison(newName))
             {
                 _logger?.LogWarning("Rename skipped: Old and new names are effectively the same.");
                 return;
             }

             var matches = FindCharacterOccurrences(oldName);

             if (matches.Count == 0)
             {
                 _logger?.LogWarning("Cannot rename internal state: Old name '{OldName}' not found.", oldName);
                 return;
             }
             if (matches.Count > 1)
             {
                 _logger?.LogWarning("Ambiguous internal rename: Old name '{OldName}' matched {Count} entries. Applying rename to all found.", oldName, matches.Count);
             }

             foreach (var m in matches)
             {
                 var normMatchName = NormalizeNameForComparison(m.name);
                 // Preserve LOTH status from the matched entry `m.name`
                 bool isLOTH = m.name.Contains(LOTH_MARKER_ENTITY) || m.name.Contains(LOTH_MARKER_LITERAL);
                 string finalNewName = newName.Trim();
                 if (isLOTH) finalNewName += LOTH_MARKER_ENTITY;
                 finalNewName = NormalizeLothMarker(finalNewName)!; // Ensure LOTH format

                 if (m.special == "HighPriest")
                 {
                    if (HighPriestName != null && NormalizeNameForComparison(HighPriestName) == normMatchName)
                    {
                         _logger?.LogInformation("Renaming internal High Priest: {OldHP} => {NewHP}", HighPriestName, finalNewName);
                         HighPriestName = finalNewName;
                    }
                 }
                 else if (m.divine != null && m.rank != null)
                 {
                     var dIndex = Array.IndexOf(DIVINES, m.divine);
                     if (dIndex != -1 && BASE_RANKS.Contains(m.rank))
                     {
                         var rankData = AllDivineData[dIndex][m.rank];
                         for (int i = 0; i < rankData.Count; i++)
                         {
                             if (NormalizeNameForComparison(rankData[i]) == normMatchName)
                             {
                                 _logger?.LogInformation("Renaming '{OldEntry}' to '{NewEntry}' in internal {Rank} list for {Divine}", rankData[i], finalNewName, m.rank, m.divine);
                                 rankData[i] = finalNewName;
                                 // break; // Assume only one match per rank/divine needed
                             }
                         }
                     }
                 }
             }
         }

         private void UpdateLOTHStatus(string? name, bool makeLOTH)
         {
              if (string.IsNullOrWhiteSpace(name)) return;
              _logger?.LogInformation("Processing internal LOTH update for '{Name}': set to {MakeLOTH}", name, makeLOTH);
              var matches = FindCharacterOccurrences(name);

              if (matches.Count == 0)
              {
                  _logger?.LogWarning("Cannot update internal LOTH: Name '{Name}' not found.", name);
                  return;
              }
               if (matches.Count > 1)
              {
                  _logger?.LogWarning("Ambiguous internal LOTH update: Name '{Name}' matched {Count} entries. Applying update to all found.", name, matches.Count);
              }

              foreach (var m in matches)
              {
                  var normMatchName = NormalizeNameForComparison(m.name);
                  var baseName = m.name.Replace(LOTH_MARKER_ENTITY, string.Empty)
                                        .Replace(LOTH_MARKER_LITERAL, string.Empty)
                                        .Replace("&#42;", string.Empty)
                                        .Replace("*", string.Empty).Trim();
                  string finalName = baseName;
                  if (makeLOTH) finalName += LOTH_MARKER_ENTITY;
                  finalName = NormalizeLothMarker(finalName)!; // Ensure LOTH format

                   // Only proceed if the final name is different from the matched name
                   if (m.name == finalName)
                   {
                       _logger?.LogDebug("Internal LOTH status for {Name} ({Loc}) is already {MakeLOTH}. Skipping.", m.name, m.rank ?? m.special, makeLOTH);
                       continue; // No change needed for this entry
                   }

                  if (m.special == "HighPriest")
                  {
                      if (HighPriestName != null && NormalizeNameForComparison(HighPriestName) == normMatchName)
                      {
                           _logger?.LogInformation("Updating internal LOTH for High Priest: {OldHP} => {NewHP}", HighPriestName, finalName);
                           HighPriestName = finalName;
                      }
                  }
                  else if (m.divine != null && m.rank != null)
                  {
                      var dIndex = Array.IndexOf(DIVINES, m.divine);
                      if (dIndex != -1 && BASE_RANKS.Contains(m.rank))
                      {
                          var rankData = AllDivineData[dIndex][m.rank];
                          for (int i = 0; i < rankData.Count; i++)
                          {
                              if (NormalizeNameForComparison(rankData[i]) == normMatchName)
                              {
                                   _logger?.LogInformation("Updating internal LOTH for {OldEntry} to {NewEntry} in {Rank} list for {Divine}", rankData[i], finalName, m.rank, m.divine);
                                   rankData[i] = finalName;
                                   // break;
                              }
                          }
                      }
                  }
              }
         }

        private void AlphabetizeRanks()
        {
            _logger?.LogDebug("Alphabetizing internal ranks...");
            foreach (var divineData in AllDivineData)
            {
                foreach (var rank in BASE_RANKS)
                {
                    divineData[rank].Sort((a, b) =>
                    {
                        var normA = NormalizeNameForComparison(a);
                        var normB = NormalizeNameForComparison(b);
                        return string.Compare(normA, normB, StringComparison.OrdinalIgnoreCase);
                    });
                }
            }
        }


        // Regenerates the HTML by modifying the stored _htmlDoc based on the current internal state
        public string RegenerateHtml()
        {
            _logger?.LogInformation("Regenerating HTML by modifying the loaded DOM...");
            if (_htmlDoc == null || _htmlDoc.DocumentNode == null)
            {
                _logger?.LogError("Cannot regenerate HTML: HTML document is not loaded.");
                throw new InvalidOperationException("RosterState not initialized with HTML (_htmlDoc is null).");
            }

            // 1. Update High Priest text node
            UpdateHighPriestInDOM();

            // 2. Update table cells
            UpdateTableCellsInDOM();

            // 3. Return the modified HTML content
            // Use OuterHtml of the DocumentNode to get the whole structure including <html> tags if present
            // Or use _htmlDoc.DocumentNode.SelectSingleNode("//body")?.InnerHtml if only body content is needed/expected
            using (var sw = new System.IO.StringWriter())
            {
                 // Configure writer to maintain formatting as much as possible
                 _htmlDoc.OptionOutputOriginalCase = true;
                 _htmlDoc.OptionWriteEmptyNodes = true; // e.g. <br />
                 _htmlDoc.Save(sw);
                 return sw.ToString();
            }
        }

         // Updates the High Priest line in the stored _htmlDoc
        private void UpdateHighPriestInDOM()
        {
            var highPriestRegex = new Regex(@"^(#?\s*Current\s+High\s+Priest(?:ess)?\s*[:|-]?\s+)(.*)", RegexOptions.IgnoreCase);
            bool hpLineUpdated = false;

            // Find potential text nodes again
            var textNodes = _htmlDoc!.DocumentNode.SelectNodes("//p/text() | //div/text() | //h1/text() | //h2/text() | //h3/text() | //h4/text() | //body/text()[normalize-space()]");

            if (textNodes != null)
            {
                foreach (var node in textNodes)
                {
                    // CS8602 Fix: Assign InnerText to variable after null check
                    var currentText = node.InnerText;
                    var match = highPriestRegex.Match(currentText);

                    if (match.Success)
                    {
                        // Construct the new text based on internal state (HighPriestTitle, HighPriestName)
                        // Ensure title in prefix matches internal state
                        var prefix = Regex.Replace(match.Groups[1].Value, @"High\s+Priest(?:ess)?", HighPriestTitle, RegexOptions.IgnoreCase);
                        var newText = prefix + (HighPriestName ?? string.Empty); // Use stored name (already has LOTH marker if needed)

                        // Check if actual change is needed before modifying DOM node
                        if (HtmlEntity.DeEntitize(currentText)?.Trim() != HtmlEntity.DeEntitize(newText)?.Trim()) // Compare decoded values
                        {
                            _logger?.LogDebug("Updating High Priest line in DOM: '{CurrentText}' => '{NewText}'", currentText.Trim(), newText.Trim());
                            // Replace the text node content. Create a new text node.
                            var newTextNode = _htmlDoc.CreateTextNode(newText);
                            // CS8602 Fix: Add null check for ParentNode
                            if (node.ParentNode != null)
                            {
                                node.ParentNode.ReplaceChild(newTextNode, node);
                                hpLineUpdated = true;
                            }
                            else
                            {
                                _logger?.LogWarning("Could not replace High Priest text node as its ParentNode was null.");
                            }
                        }
                        else
                        {
                            // _logger?.LogDebug("High Priest line text already up-to-date in DOM.");
                            hpLineUpdated = true; // Mark as found
                        }
                        break; // Assume only one HP line
                    }
                }
            }

            if (!hpLineUpdated)
            {
                 _logger?.LogWarning("Could not find or update the High Priest line in the HTML DOM during regeneration.");
                 // Future: Consider adding the line if it's missing entirely? Requires knowing where to put it.
            }
        }

         // Updates the table cell contents in the stored _htmlDoc
         private void UpdateTableCellsInDOM()
         {
              // Re-select the tables and cells to ensure we have the nodes
              var tableNodes = _htmlDoc!.DocumentNode.SelectNodes("//table[.//tr[@valign='top'] and count(.//tr[@valign='top'][1]/td) >= 4]");
              if (tableNodes == null || tableNodes.Count < 2) return; // Should have been caught in parse, but check again

              var firstTableDataRow = tableNodes[0].SelectSingleNode(".//tr[@valign='top'][2]");
              var secondTableDataRow = tableNodes[1].SelectSingleNode(".//tr[@valign='top'][2]");
              if (firstTableDataRow == null || secondTableDataRow == null) return;

              var firstTableTds = firstTableDataRow.SelectNodes("./td");
              var secondTableTds = secondTableDataRow.SelectNodes("./td");
              if (firstTableTds == null || secondTableTds == null) return;

              var divineTds = firstTableTds.Take(4).Concat(secondTableTds.Take(4)).ToList();

              for (int i = 0; i < divineTds.Count && i < DIVINES.Length; i++)
              {
                  UpdateDivineCellInDOM(divineTds[i], AllDivineData[i], DIVINES[i]);
              }
         }

         // Updates a single Divine cell in the HtmlAgilityPack DOM
         private void UpdateDivineCellInDOM(HtmlNode cellNode, Dictionary<string, List<string>> data, string divineNameForDebug)
         {
             // CS8602 Fix: Check if the passed cellNode is null
             if (cellNode == null) {
                 _logger?.LogWarning("UpdateDivineCellInDOM called with a null cellNode for Divine {DivineName}. Skipping update.", divineNameForDebug);
                 return;
             }
             _logger?.LogDebug("Updating DOM cell for Divine {DivineName}", divineNameForDebug);

             // Find rank headers: <span><u>RankName</u></span>
             var rankHeaderNodes = cellNode.SelectNodes(".//span/u/.."); // Select the parent span
             if (rankHeaderNodes == null) {
                _logger?.LogWarning("No rank headers found in DOM cell for {DivineName} during update.", divineNameForDebug);
                return;
             }

             for (int i = 0; i < rankHeaderNodes.Count; i++)
             {
                 var spanNode = rankHeaderNodes[i];
                 // CS8602 Fix: Check if spanNode itself is null
                 if (spanNode == null) continue;
                 var uNode = spanNode?.SelectSingleNode("./u");
                 if (uNode == null) continue;

                 var rankName = HtmlEntity.DeEntitize(uNode.InnerText)?.Trim();
                 if (rankName == null) continue; // Skip if rankName is null to avoid null key usage
                 if (!BASE_RANKS.Contains(rankName)) continue; // Skip unexpected ranks

                 if (!data.TryGetValue(rankName, out var names) || names == null)
                 {
                     _logger?.LogWarning("Rank {RankName} found in HTML cell but not in processed data for {DivineName}.", rankName, divineNameForDebug);
                     names = new List<string>(); // Assume empty list if data missing
                 }

                 // --- Remove old content nodes between this header and next ---
                 var nodesToRemove = new List<HtmlNode>();
                 // CS8602 Fix: spanNode is checked above, so NextSibling access is safer
                 var currentNode = spanNode?.NextSibling;
                 HtmlNode? nextHeaderNode = (i + 1 < rankHeaderNodes.Count) ? rankHeaderNodes[i + 1] : null;

                 while (currentNode != null)
                 {
                     // Stop if we hit the next rank header
                     if (nextHeaderNode != null && currentNode == nextHeaderNode) break;
                     nodesToRemove.Add(currentNode);
                     currentNode = currentNode.NextSibling;
                 }

                 // Remove the collected nodes (old names, text, BRs, etc.)
                 foreach (var nodeToRemove in nodesToRemove)
                 {
                     // CS8602 Fix: cellNode is checked at the start of the method
                     cellNode?.RemoveChild(nodeToRemove);
                 }
                 // _logger?.LogDebug("Removed {Count} old nodes after {RankName} header for {DivineName}.", nodesToRemove.Count, rankName, divineNameForDebug);

                 // --- Insert new content nodes ---
                 var nodesToInsert = new List<HtmlNode>();
                 // Always add a leading newline text node after the header for spacing
                 if (_htmlDoc != null) {
                     nodesToInsert.Add(_htmlDoc.CreateTextNode("\n"));
                     // Remove br after the header
                 }

                 if (names.Count > 0)
                 {
                     for(int j = 0; j < names.Count; j++)
                     {
                         // Create text node for the name (includes LOTH marker if present)
                         // Important: Do NOT encode the name here, HtmlAgilityPack handles encoding on save if needed
                         if (_htmlDoc != null) {
                             nodesToInsert.Add(_htmlDoc.CreateTextNode(names[j]));
                         }
                         
                         // Add a newline text node for readability in raw HTML
                         if (_htmlDoc != null && j < names.Count - 1) { // Only add newlines between names, not after the last one
                             nodesToInsert.Add(_htmlDoc.CreateTextNode("\n"));
                         }
                     }
                     
                     // Add a br tag after the last name in the list
                     if (_htmlDoc != null) {
                         nodesToInsert.Add(_htmlDoc.CreateElement("br"));
                         nodesToInsert.Add(_htmlDoc.CreateTextNode("\n"));
                     }
                     
                     _logger?.LogDebug("Inserting {Count} names for {RankName} of {DivineName}.", names.Count, rankName, divineNameForDebug);
                 }
                 else if (rankName == "Priest") // Special vacant case for Priest
                 {
                      if (_htmlDoc != null) {
                          nodesToInsert.Add(_htmlDoc.CreateTextNode("Vacant"));
                          // Add a br tag after "Vacant"
                          nodesToInsert.Add(_htmlDoc.CreateElement("br"));
                          nodesToInsert.Add(_htmlDoc.CreateTextNode("\n"));
                      }
                      _logger?.LogDebug("Inserting 'Vacant' for {RankName} of {DivineName}.", rankName, divineNameForDebug);
                 }
                 else
                 {
                      // Other ranks just have the leading/trailing newline if empty
                      // Add a br tag for spacing even if the list is empty
                      if (_htmlDoc != null) {
                          nodesToInsert.Add(_htmlDoc.CreateElement("br"));
                          nodesToInsert.Add(_htmlDoc.CreateTextNode("\n"));
                      }
                      _logger?.LogDebug("Rank {RankName} of {DivineName} is empty.", rankName, divineNameForDebug);
                 }

                 // Insert the new nodes *after* the current rank header span
                 HtmlNode insertAfterNode = spanNode!;
                 foreach(var nodeToInsert in nodesToInsert)
                 {
                     // CS8602 Fix: Check cellNode is not null (shouldn't be here, but safer)
                     cellNode?.InsertAfter(nodeToInsert, insertAfterNode);
                     insertAfterNode = nodeToInsert; // Insert next node after the one just inserted
                 }
             }
         }

    }
} 