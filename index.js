#!/usr/bin/env bun

import { writeFile, mkdir } from 'fs/promises';
import path from 'path';
import { spawn } from 'child_process';
import { Client, GatewayIntentBits, Partials, Events } from 'discord.js';
import { chromium } from 'playwright';
import '@dotenvx/dotenvx/config';
// NEVER CHANGE THIS LINE: cheerio's ES module import is broken in upstream package
const cheerio = require('cheerio');

// --- Configuration ---
const {
    DISCORD_BOT_TOKEN,
    DISCORD_CHANNEL_ID,
    GUILDTAG_FORUM_URL,
    GUILDTAG_EMAIL,
    GUILDTAG_PASSWORD,
} = process.env;

// Validate essential configuration
const requiredEnv = [
    'DISCORD_BOT_TOKEN',
    'DISCORD_CHANNEL_ID',
    'GUILDTAG_FORUM_URL',
    'GUILDTAG_EMAIL',
    'GUILDTAG_PASSWORD',
];
for (const key of requiredEnv) {
    if (!process.env[key]) {
        console.error(`Error: Missing required environment variable ${key}. Please check your .env file.`);
        process.exit(1);
    }
}

// --- Constants ---
const DIVINES = [
    "Akatosh", "Arkay", "Dibella", "Julianos",
    "Kynareth", "Mara", "Stendarr", "Zenithar"
];
const KNOWN_RANKS = [
    "Priest", "Priestess", "Curate", "Prior", "Acolyte", "High Priestess", "High Priest"
];
// Ranks expected within the table cells for direct manipulation
const BASE_RANKS = ["Priest", "Curate", "Prior", "Acolyte"];

// --- Utility Functions ---
function levenshtein(a, b) {
  const an = a ? a.length : 0;
  const bn = b ? b.length : 0;
  if (an === 0) return bn;
  if (bn === 0) return an;

  const matrix = [];
  for (let i = 0; i <= bn; i++) {
    matrix[i] = [i];
  }
  for (let j = 0; j <= an; j++) {
    matrix[0][j] = j;
  }

  for (let i = 1; i <= bn; i++) {
    for (let j = 1; j <= an; j++) {
      const cost = a[j - 1] === b[i - 1] ? 0 : 1;
      matrix[i][j] = Math.min(
        matrix[i - 1][j] + 1,
        matrix[i][j - 1] + 1,
        matrix[i - 1][j - 1] + cost
      );
    }
  }

  return matrix[bn][an];
}

function fuzzyMatch(input, candidates) {
  if (!input || !candidates || candidates.length === 0) return null;
  const normInput = input.trim().toLowerCase();
  let bestDist = Infinity;
  let bestMatch = null;

  for (const c of candidates) {
    const normC = c.toLowerCase();
    const dist = levenshtein(normInput, normC);
    if (dist < bestDist) {
      bestDist = dist;
      bestMatch = c; // Return original casing
    }
  }

  // Adjust threshold based on input length? For now, fixed.
  // Consider partial matches? (e.g., "Aka" for "Akatosh") - Levenshtein handles this somewhat.
  return bestDist <= 3 ? bestMatch : null; // Threshold might need tuning
}

function normalizeNameForComparison(n) {
    if (!n || typeof n !== 'string') return '';
    return n.replace(/<span[^>]*>.*?<\/span>/gi, '') // remove potential markup (e.g., color spans)
      .replace(/\(LOTH\)/i, '') // remove literal (LOTH)
      .replace(/&#42;/g, '')   // remove HTML entity for asterisk
      .replace(/\*/g, '')       // remove literal asterisk
      .trim().toLowerCase();
}

function matchesName(existingName, newName) {
    // Use normalized comparison
    return normalizeNameForComparison(existingName) === normalizeNameForComparison(newName);
}

// --- Roster Processing Logic ---

// Represents the state of the roster parsed from HTML
class RosterState {
    constructor() {
        this.allDivineData = DIVINES.map(() => ({ Priest: [], Curate: [], Prior: [], Acolyte: [] }));
        this.highPriestName = null;
        this.highPriestTitle = "High Priest";
        this.$ = null; // Cheerio instance for DOM manipulation
    }

    // Parses the initial HTML content to populate the state
    parseFromHtml(htmlContent) {
        this.$ = cheerio.load(htmlContent, { decodeEntities: false }); // Keep HTML entities like &#42;
        const $ = this.$;

        // Find the two main tables based on structure (adjust if structure changes)
        const tables = $('table').filter((i, el) => {
            const $el = $(el);
            // Heuristic: has multiple rows, at least 4 data cells, maybe specific class/ID if available
            return $el.find('tr').length > 1 && $el.find('tr[valign=top]').eq(1).find('td').length >= 4;
        });

        if (tables.length < 2) {
            console.error("[ERROR] Could not find the expected 2 roster tables in the HTML. Found:", tables.length);
            // Provide more context if possible:
            // console.error("[DEBUG] First few chars of HTML:", htmlContent.slice(0, 200));
            throw new Error("Could not find the expected 2 roster tables in the HTML.");
        }

        const firstTable = tables.eq(0);
        const secondTable = tables.eq(1);
        // Target the row containing the actual clergy names (often the second `tr` with valign=top)
        const firstTableDataRow = firstTable.find('tr[valign=top]').eq(1);
        const secondTableDataRow = secondTable.find('tr[valign=top]').eq(1);

        if (!firstTableDataRow.length || !secondTableDataRow.length) {
             throw new Error("Could not find data rows (tr valign=top) in roster tables.");
        }

        const firstTableTds = firstTableDataRow.find('td');
        const secondTableTds = secondTableDataRow.find('td');

        if (firstTableTds.length < 4 || secondTableTds.length < 4) {
             throw new Error(`Insufficient columns found in table data rows (need 4, found ${firstTableTds.length}, ${secondTableTds.length})`);
        }


        const divineTds = [
            firstTableTds.eq(0), firstTableTds.eq(1), firstTableTds.eq(2), firstTableTds.eq(3),
            secondTableTds.eq(0), secondTableTds.eq(1), secondTableTds.eq(2), secondTableTds.eq(3)
        ];

        divineTds.forEach((td, index) => {
            if (index < DIVINES.length) {
                this.allDivineData[index] = this._parseDivineCell($(td));
            }
        });

        console.log("[DEBUG] Initial parsed table data:", JSON.stringify(this.allDivineData, null, 2));

        // Extract High Priest - Look for a specific pattern, often outside the tables
        // Making the pattern more robust: allows optional '#' and flexible spacing
        const highPriestRegex = /#?\s*Current\s+High\s+Priest(ess)?\s*[:-\s]+\s*(.*)/i;
        let foundHpLine = false;
        // Search within common tags where this info might be (p, div, potentially just body text nodes)
        $('body').find('p, div, h1, h2, h3, h4').addBack('body').contents().each((i, node) => {
             if (node.type === 'text') {
                 const txt = $(node).text().trim();
                 const m = txt.match(highPriestRegex);
                 if (m) {
                     this.highPriestName = m[2].trim() || null;
                     // Handle names with asterisks correctly here if needed
                     this.highPriestName = this.highPriestName?.replace(/\s+\*$/, ' &#42;'); // Normalize to entity if present

                     this.highPriestTitle = (m[1] && m[1].toLowerCase() === 'ess') ? "High Priestess" : "High Priest";
                     console.log(`[INFO] Found High Priest: ${this.highPriestName || 'None'} (${this.highPriestTitle})`);
                     foundHpLine = true;
                     return false; // Stop searching once found
                 }
             }
        });
        if (!foundHpLine) {
            console.log("[WARN] High Priest line not found using regex:", highPriestRegex);
        }
    }

    // Internal helper to parse one table cell (representing one Divine)
    _parseDivineCell($cell) {
        const $ = this.$;
        const ranks = { Priest: [], Curate: [], Prior: [], Acolyte: [] }; // Initialize for the cell

        // Find rank headers: <span><u>RankName</u></span>
        const rankSpans = $cell.find('span > u').parent('span');

        rankSpans.each((i, spanEl) => {
            const $span = $(spanEl);
            const rankName = $span.find('u').first().text().trim();

            // Only process ranks we expect in the table structure
            if (!BASE_RANKS.includes(rankName)) {
                // console.log(`[DEBUG] Skipping unexpected rank header in cell: ${rankName}`);
                return;
            }

            // Collect all nodes (text, <br>, other elements like colored spans)
            // between this rank span and the next one (or the end of the cell)
            let currentNode = spanEl.nextSibling;
            let collectedNodesHtml = []; // Store raw HTML pieces

            // Find the *next* rank span element to delimit the section
            const nextRankSpanEl = rankSpans.eq(i + 1)[0]; // Get the DOM element

            while (currentNode) {
                 // Stop if we hit the next rank span *element*
                 if (nextRankSpanEl && currentNode === nextRankSpanEl) {
                     break;
                 }

                 if (currentNode.type === 'text') {
                     collectedNodesHtml.push(currentNode.data); // Keep raw text, including whitespace for now
                 } else if (currentNode.type === 'tag') {
                     if (currentNode.name === 'br') {
                         collectedNodesHtml.push('\n'); // Treat <br> as newline
                     } else {
                         // Keep other tags (like colored spans for LOTH) as HTML
                         collectedNodesHtml.push($.html(currentNode));
                     }
                 }
                 currentNode = currentNode.nextSibling;
            }

            // Process the collected HTML for this rank section
            const sectionHtml = collectedNodesHtml.join('');
            let names = sectionHtml.split('\n') // Split by newlines (from text or <br>)
                .map(line => line.trim())     // Trim whitespace
                .filter(Boolean)              // Remove empty lines
                .filter(line => !['—', 'vacant', '-'].includes(line.toLowerCase())); // Remove placeholders

             // Normalize LOTH representation: ensure ' &#42;' for consistency if present
             names = names.map(name => name.replace(/\s+\*$/, ' &#42;'));


            if (ranks.hasOwnProperty(rankName)) {
                ranks[rankName].push(...names);
            }
        });
        return ranks;
    }

    // Applies a list of parsed instructions to the current state
    applyInstructions(instructions) {
        for (const instruction of instructions) {
            this._applySingleInstruction(instruction);
        }
        this._alphabetizeRanks(); // Alphabetize after all changes are made
    }

    // Dispatches a single instruction to the appropriate handler
    _applySingleInstruction(instruction) {
        console.log("[DEBUG] Applying instruction:", instruction);
        try {
            switch (instruction.type) {
                case 'remove':
                    this._removeByName(instruction.characterName);
                    break;
                case 'add':
                    // Ensure character isn't listed elsewhere before adding
                    this._removeCharacterFromAll(instruction.characterName);
                    this._addCharacter(instruction.characterName, instruction.targetDivine, instruction.targetRank, instruction.isLOTH);
                    break;
                case 'rename':
                    this._renameCharacter(instruction.oldName, instruction.newName);
                    break;
                case 'setHighPriest':
                    this._setHighPriest(instruction.highPriest, instruction.highPriestTitle, instruction.isLOTH); // Pass LOTH status
                    break;
                case 'updateLOTH':
                    this._updateLOTHStatus(instruction.characterName, instruction.makeLOTH);
                    break;
                case 'error':
                case 'ignore':
                    console.log(`[DEBUG] Skipping instruction of type ${instruction.type}: ${instruction.originalLine}`);
                    break;
                default:
                    console.log(`[WARN] Unknown instruction type encountered: ${instruction.type}`);
            }
        } catch (error) {
             console.error(`[ERROR] Failed to apply instruction: ${JSON.stringify(instruction)}`, error);
             // Decide if processing should halt or continue
        }
    }

    // --- Data Modification Methods ---

    _setHighPriest(name, title, isLOTH = false) {
      const finalName = name.trim() + (isLOTH ? ' &#42;' : ''); // Apply LOTH if needed
      console.log(`[INFO] Setting High Priest to: ${finalName} (${title})`);

      // Check if different from current
      if (this.highPriestName !== finalName || this.highPriestTitle !== title) {
          if (this.highPriestName && !matchesName(this.highPriestName, finalName)) {
              console.log(`[INFO] Replacing previous High Priest ${this.highPriestName} (${this.highPriestTitle})`);
              // Ensure the old HP (if different) is removed from any base rank they might *also* hold
              this._removeCharacterFromAll(this.highPriestName);
          } else if (!this.highPriestName) {
              console.log(`[INFO] Assigning initial High Priest.`);
          } else {
              console.log(`[INFO] Updating High Priest details (possibly just LOTH status or title).`);
          }
          this.highPriestName = finalName;
          this.highPriestTitle = title;
          // Also ensure the *new* HP is not in any base ranks
           this._removeCharacterFromAll(finalName);
      } else {
          console.log(`[DEBUG] High Priest details unchanged: ${finalName} (${title})`);
      }
    }

    // Removes a character from all BASE_RANKS lists. Does NOT affect High Priest state here.
    // Returns true if any removal occurred.
    _removeCharacterFromAll(name) {
      console.log(`[DEBUG] Ensuring ${name} is removed from all base ranks.`);
      let removed = false;
      const normNameToRemove = normalizeNameForComparison(name);
      if (!normNameToRemove) return false; // Don't remove empty names

      for (let d = 0; d < this.allDivineData.length; d++) {
        for (const rank of BASE_RANKS) {
          const initialLength = this.allDivineData[d][rank].length;
          this.allDivineData[d][rank] = this.allDivineData[d][rank].filter(existingName => {
              const shouldKeep = normalizeNameForComparison(existingName) !== normNameToRemove;
              if (!shouldKeep) {
                  const plainName = existingName.replace(/<[^>]+>/g, ''); // For logging
                  console.log(`[INFO] Removed ${plainName} from ${rank} of ${DIVINES[d]}`);
                  removed = true;
              }
              return shouldKeep;
          });
        }
      }
      return removed;
    }

     _addCharacter(name, divineName, rank, loth = false) {
        const divineIndex = DIVINES.indexOf(divineName);
        if (divineIndex === -1) {
            console.log(`[WARN] Divine not found for add: ${divineName}`);
            return;
        }
        // Ensure rank is a valid base rank where characters are listed
        if (!BASE_RANKS.includes(rank)) {
            console.log(`[WARN] Invalid target rank for add/move: ${rank}. Must be one of ${BASE_RANKS.join(', ')}.`);
            return;
        }

        const ranks = this.allDivineData[divineIndex];
        let displayName = name.trim();
        if (!displayName) {
            console.log(`[WARN] Attempted to add empty name to ${rank} of ${divineName}`);
            return;
        }

        if (loth) displayName += ' &#42;'; // Use HTML entity for asterisk consistency

        // Check if already exists (case-insensitive, ignoring LOTH)
        const existingIndex = ranks[rank].findIndex(existingName => matchesName(existingName, displayName));

        if (existingIndex === -1) {
            // Doesn't exist, add it
            ranks[rank].push(displayName);
            console.log(`[INFO] Added ${displayName} to ${rank} of ${divineName}`);
        } else {
            // Exists, check if LOTH status needs update
            const currentEntry = ranks[rank][existingIndex];
            if (currentEntry !== displayName) {
                 console.log(`[INFO] Updating LOTH status for existing ${name} in ${rank} of ${divineName}: ${currentEntry} => ${displayName}`);
                 ranks[rank][existingIndex] = displayName;
            } else {
                console.log(`[DEBUG] ${name} already exists in ${rank} of ${divineName} with correct LOTH status, skipping duplicate add.`);
            }
        }
    }

    // Finds all occurrences (case-insensitive, ignoring LOTH) across High Priest and base ranks.
    // Includes fuzzy matching as a fallback.
    _findCharacterOccurrences(name) {
        let occurrences = [];
        const queryNorm = normalizeNameForComparison(name);
        if (!queryNorm) return []; // Cannot find empty name

        // 1. Exact (normalized) match check
        // Check High Priest
        if (this.highPriestName && normalizeNameForComparison(this.highPriestName) === queryNorm) {
            occurrences.push({ name: this.highPriestName, special: 'HighPriest', title: this.highPriestTitle });
        }
        // Check regular ranks
        for (let d = 0; d < this.allDivineData.length; d++) {
            for (const rank of BASE_RANKS) {
                for (const existingName of this.allDivineData[d][rank]) {
                    if (normalizeNameForComparison(existingName) === queryNorm) {
                         // Avoid adding duplicates if someone holds multiple roles (unlikely but possible)
                         if (!occurrences.some(o => o.name === existingName && o.rank === rank && o.divine === DIVINES[d])) {
                             occurrences.push({ name: existingName, divine: DIVINES[d], rank: rank });
                         }
                    }
                }
            }
        }

        // 2. Fuzzy match if no exact match found
        if (occurrences.length === 0) {
             console.log(`[DEBUG] No exact match for "${name}". Attempting fuzzy search...`);
             let candidates = [];
             if (this.highPriestName) candidates.push({ name: this.highPriestName, special: 'HighPriest', title: this.highPriestTitle });
             this.allDivineData.forEach((divineData, d) => {
                BASE_RANKS.forEach(rank => {
                    divineData[rank].forEach(n => candidates.push({ name: n, divine: DIVINES[d], rank: rank }));
                });
             });

             let bestDist = Infinity;
             let fuzzyMatches = [];
             for (const c of candidates) {
                const dist = levenshtein(queryNorm, normalizeNameForComparison(c.name));
                const similarityThreshold = Math.max(1, Math.floor(queryNorm.length / 4)); // Allow more deviation for longer names? Needs tuning. Default 2-3.
                 // console.log(`[FUZZY] Comparing "${queryNorm}" with "${normalizeNameForComparison(c.name)}", dist: ${dist}, threshold: ${similarityThreshold}`);
                if (dist <= 2) { // Use fixed threshold for simplicity for now
                    if (dist < bestDist) {
                        bestDist = dist;
                        fuzzyMatches = [c]; // Start new list with better match
                    } else if (dist === bestDist && !fuzzyMatches.some(fm => fm.name === c.name && fm.rank === c.rank && fm.divine === c.divine)) {
                         fuzzyMatches.push(c); // Add equally good match if not duplicate
                    }
                }
             }
             if (fuzzyMatches.length > 0) {
                 console.log(`[INFO] Fuzzy matched "${name}" (dist ${bestDist}) to:`, fuzzyMatches.map(o => `${o.name} (${o.rank || o.special})`));
                 occurrences = fuzzyMatches;
             } else {
                 console.log(`[DEBUG] No fuzzy matches found for "${name}".`);
             }
        }

        return occurrences;
    }


    _removeByName(name) {
        console.log(`[INFO] Processing removal request for: "${name}"`);
        const matches = this._findCharacterOccurrences(name);

        if (matches.length === 0) {
            console.log(`[WARN] No character found matching "${name}" for removal.`);
            return;
        }

        // Handle ambiguity - current strategy: remove all matches found
        if (matches.length > 1) {
             console.log(`[WARN] Ambiguous removal: "${name}" matched ${matches.length} entries. Removing all found:`, matches.map(m => `${m.name} (${m.rank || m.special})`));
        }

        let actuallyRemoved = false;
        for (const m of matches) {
            // Use normalized comparison for safety, comparing against the original match `m.name`
            const normMatchName = normalizeNameForComparison(m.name);

            if (m.special === 'HighPriest') {
                // Check if current HP matches the one found
                if (this.highPriestName && normalizeNameForComparison(this.highPriestName) === normMatchName) {
                    console.log(`[INFO] Removing High Priest: ${this.highPriestName}`);
                    this.highPriestName = null;
                    actuallyRemoved = true;
                }
            } else if (m.divine && m.rank) {
                const dIndex = DIVINES.indexOf(m.divine);
                if (dIndex !== -1 && BASE_RANKS.includes(m.rank)) {
                    const rankData = this.allDivineData[dIndex][m.rank];
                    const initialLength = rankData.length;
                    // Filter out based on normalized comparison with the specific name found in this match `m.name`
                    this.allDivineData[dIndex][m.rank] = rankData.filter(n => normalizeNameForComparison(n) !== normMatchName);
                    if (this.allDivineData[dIndex][m.rank].length < initialLength) {
                         const plainName = m.name.replace(/<[^>]+>/g, ''); // Clean name for logging
                         console.log(`[INFO] Removed ${plainName} from ${m.rank} of ${m.divine}`);
                         actuallyRemoved = true;
                    }
                }
            }
        }

        if (!actuallyRemoved && matches.length > 0) {
            // This might happen if the match was found but then state changed before removal, or fuzzy match issue
            console.log(`[DEBUG] Found matches for "${name}" but no state change occurred during removal.`);
        }
    }

    _renameCharacter(oldName, newName) {
        console.log(`[INFO] Processing rename: "${oldName}" => "${newName}"`);
        if (normalizeNameForComparison(oldName) === normalizeNameForComparison(newName)) {
            console.log("[WARN] Rename skipped: Old and new names are effectively the same.");
            return;
        }

        const matches = this._findCharacterOccurrences(oldName); // Find based on old name

        if (matches.length === 0) {
            console.log(`[WARN] Cannot rename: Old name "${oldName}" not found.`);
            return;
        }
        if (matches.length > 1) {
            console.log(`[WARN] Ambiguous rename: Old name "${oldName}" matched ${matches.length} entries. Applying rename to all found.`);
        }

        let renamed = false;
        for (const m of matches) {
            const normMatchName = normalizeNameForComparison(m.name);
            // Preserve LOTH status from the matched entry `m.name`
            const isLOTH = m.name.includes('&#42;');
            const finalNewName = newName.trim() + (isLOTH ? ' &#42;' : '');

            if (m.special === 'HighPriest') {
                // Check if current HP matches the one found
                if (this.highPriestName && normalizeNameForComparison(this.highPriestName) === normMatchName) {
                    console.log(`[INFO] Renaming High Priest: ${this.highPriestName} => ${finalNewName}`);
                    this.highPriestName = finalNewName; // Update internal state
                    renamed = true;
                }
            } else if (m.divine && m.rank) {
                const dIndex = DIVINES.indexOf(m.divine);
                if (dIndex !== -1 && BASE_RANKS.includes(m.rank)) {
                    const rankData = this.allDivineData[dIndex][m.rank];
                    let foundInRank = false;
                    for (let i = 0; i < rankData.length; i++) {
                        // Rename the specific entry that was matched
                        if (normalizeNameForComparison(rankData[i]) === normMatchName) {
                           console.log(`[INFO] Renaming ${rankData[i].replace(/<[^>]+>/g, '')} to ${finalNewName.replace(/<[^>]+>/g, '')} in ${m.rank} of ${m.divine}`);
                           rankData[i] = finalNewName;
                           renamed = true;
                           foundInRank = true;
                           // break; // Remove break if one person could theoretically hold same rank twice (unlikely)
                        }
                    }
                    if (!foundInRank) console.log(`[DEBUG] Match for ${oldName} found in ${m.rank}/${m.divine}, but couldn't find exact entry to rename in current state.`);
                }
            }
        }

        if (!renamed && matches.length > 0) {
             console.log(`[DEBUG] Found matches for rename "${oldName}" but no state change occurred.`);
        }
    }

    _updateLOTHStatus(name, makeLOTH) {
        console.log(`[INFO] Processing LOTH update for "${name}": set to ${makeLOTH}`);
        const matches = this._findCharacterOccurrences(name);

         if (matches.length === 0) {
            console.log(`[WARN] Cannot update LOTH: Name "${name}" not found.`);
            return;
        }
        if (matches.length > 1) {
            console.log(`[WARN] Ambiguous LOTH update: Name "${name}" matched ${matches.length} entries. Applying update to all found.`);
        }

        let updated = false;
        for (const m of matches) {
            const normMatchName = normalizeNameForComparison(m.name);
            const baseName = m.name.replace(/&#42;/g, '').replace(/\*/g, '').trim(); // Get name without LOTH marker
            const finalName = baseName + (makeLOTH ? ' &#42;' : ''); // Construct new name with correct LOTH status

            // Only proceed if the final name is different from the matched name `m.name`
            if (m.name === finalName) {
                // console.log(`[DEBUG] LOTH status for ${m.name} in ${m.rank || m.special} is already ${makeLOTH}. Skipping.`);
                continue; // No change needed for this entry
            }


            if (m.special === 'HighPriest') {
                 // Check if current HP matches the one found
                 if (this.highPriestName && normalizeNameForComparison(this.highPriestName) === normMatchName) {
                     console.log(`[INFO] Updating LOTH for High Priest: ${this.highPriestName} => ${finalName}`);
                     this.highPriestName = finalName;
                     updated = true;
                 }
            } else if (m.divine && m.rank) {
                const dIndex = DIVINES.indexOf(m.divine);
                if (dIndex !== -1 && BASE_RANKS.includes(m.rank)) {
                    const rankData = this.allDivineData[dIndex][m.rank];
                    let foundInRank = false;
                    for (let i = 0; i < rankData.length; i++) {
                        // Update the specific entry that was matched
                        if (normalizeNameForComparison(rankData[i]) === normMatchName) {
                            console.log(`[INFO] Updating LOTH for ${rankData[i].replace(/<[^>]+>/g, '')} to ${finalName.replace(/<[^>]+>/g, '')} in ${m.rank} of ${m.divine}`);
                            rankData[i] = finalName;
                            updated = true;
                            foundInRank = true;
                            // break; // Assume only one instance per rank/divine pair
                        }
                    }
                     if (!foundInRank) console.log(`[DEBUG] Match for LOTH update ${name} found in ${m.rank}/${m.divine}, but couldn't find exact entry in current state.`);
                }
            }
        }

         if (!updated && matches.length > 0) {
             console.log(`[DEBUG] Found matches for LOTH update "${name}" but no state change occurred (already had status?).`);
        }
    }


    _alphabetizeRanks() {
        console.log("[DEBUG] Alphabetizing ranks...");
        for (let d = 0; d < this.allDivineData.length; d++) {
            for (const rank of BASE_RANKS) {
                this.allDivineData[d][rank].sort((a, b) => {
                    const normA = normalizeNameForComparison(a);
                    const normB = normalizeNameForComparison(b);
                    // Basic locale comparison, ignoring case and accents
                    return normA.localeCompare(normB, 'en', { sensitivity: 'base' });
                });
            }
        }
    }

    // Regenerates the HTML content based on the current state using the Cheerio object
    regenerateHtml() {
        if (!this.$) throw new Error("RosterState not initialized with HTML ($ is null).");
        const $ = this.$;

        // 1. Update High Priest text line
        const highPriestRegex = /^(#?\s*Current\s+High\s+Priest(?:ess)?\s*[:-\s]+)\s*(.*)/i;
        let hpLineUpdated = false;
         $('body').find('p, div, h1, h2, h3, h4').addBack('body').contents().each((i, node) => {
             if (node.type === 'text') {
                 const currentText = $(node).text(); // Get text content of the node
                 const match = currentText.match(highPriestRegex);
                 if (match) {
                     const prefix = match[1].replace(/High Priest(?:ess)?/i, this.highPriestTitle); // Adjust title in prefix
                     const newText = prefix + (this.highPriestName || ''); // Use updated name, or empty if null
                     if (currentText.trim() !== newText.trim()) {
                        console.log(`[DEBUG] Updating High Priest line in HTML: "${currentText.trim()}" => "${newText.trim()}"`);
                        $(node).replaceWith(newText); // Replace the text node content
                        hpLineUpdated = true;
                     } else {
                        // console.log("[DEBUG] High Priest line text already up-to-date.");
                        hpLineUpdated = true; // Mark as found even if no change needed
                     }
                     return false; // Stop searching
                 }
             }
         });
         if (!hpLineUpdated) {
             console.log("[WARN] Could not find or update the High Priest line in the HTML DOM.");
             // Consider adding the line if it's missing entirely? Requires knowing where to put it.
         }


        // 2. Update table cells
        const tables = $('table').filter((i, el) => $(el).find('tr').length > 1 && $(el).find('tr[valign=top]').eq(1).find('td').length >= 4);
        const firstTableDataRow = tables.eq(0).find('tr[valign=top]').eq(1);
        const secondTableDataRow = tables.eq(1).find('tr[valign=top]').eq(1);
        const firstTableTds = firstTableDataRow.find('td');
        const secondTableTds = secondTableDataRow.find('td');

        const divineTds = [
            firstTableTds.eq(0), firstTableTds.eq(1), firstTableTds.eq(2), firstTableTds.eq(3),
            secondTableTds.eq(0), secondTableTds.eq(1), secondTableTds.eq(2), secondTableTds.eq(3)
        ];

        divineTds.forEach(($td, index) => {
            if (index < DIVINES.length) {
                this._updateDivineCellInDOM($td, this.allDivineData[index], DIVINES[index]);
            }
        });

        // 3. Return the modified HTML content of the body
        // Use options to avoid adding extra newlines if possible, keep entities
        return $('body').html();
    }

     // Internal helper to update one table cell in the Cheerio DOM based on data
     _updateDivineCellInDOM($cell, data, divineNameForDebug) {
        const $ = this.$;
        console.log(`[DEBUG] Updating DOM cell for Divine ${divineNameForDebug}`); // Assuming data maps back easily

        const rankSpans = $cell.find('span > u').parent('span');

        rankSpans.each((i, spanEl) => {
            const $span = $(spanEl);
            const rankName = $span.find('u').first().text().trim();

            if (!BASE_RANKS.includes(rankName)) return; // Skip unexpected ranks

            if (!data || !data.hasOwnProperty(rankName)) {
                console.log(`[WARN] Rank ${rankName} found in HTML cell but not in processed data for ${divineNameForDebug}.`);
                return;
            }

            // --- Remove old nodes ---
            let currentNode = spanEl.nextSibling;
            let nodesToRemove = [];
            const nextRankSpanEl = rankSpans.eq(i + 1)[0];

            while (currentNode) {
                 if (nextRankSpanEl && currentNode === nextRankSpanEl) break; // Stop before next header
                 nodesToRemove.push(currentNode);
                 currentNode = currentNode.nextSibling;
            }
            // Remove collected nodes (old names, text, BRs between ranks)
            nodesToRemove.forEach(node => $(node).remove());

            // --- Insert new nodes ---
            const names = data[rankName] || []; // Ensure it's an array
            let insertNodes = [];

            if (names.length > 0) {
                // Create ['\n', name1, '\n', name2, '\n', ...] structure
                 insertNodes.push('\n'); // Leading newline
                 names.forEach((name, idx) => {
                     insertNodes.push(name); // Add the name (potentially with HTML markup)
                     if (idx < names.length - 1) {
                         insertNodes.push('\n'); // Add newline between names
                     }
                 });
                 insertNodes.push('\n'); // Trailing newline
                 console.log(`[DEBUG] Inserting ${names.length} names for ${rankName} of ${divineNameForDebug}`);
            } else if (rankName === 'Priest') { // Special vacant case for Priest
                 insertNodes.push('\n');
                 insertNodes.push('Vacant');
                 insertNodes.push('\n');
                 console.log(`[DEBUG] Inserting 'Vacant' for ${rankName} of ${divineNameForDebug}`);
            } else {
                 // Other ranks just get a newline after header if empty
                 insertNodes.push('\n');
                  console.log(`[DEBUG] Rank ${rankName} of ${divineNameForDebug} is empty.`);
            }

            // Insert the new nodes after the rank span
            // Use .after() with multiple arguments or build HTML string carefully
             $span.after(...insertNodes); // Spread syntax inserts elements/text nodes in order
        });
    }
}


// --- Instruction Parsing ---

function isQuestStart(line) {
    // More specific to avoid matching random mentions
    return /^\s*(?:Beginning|Starting)\s+Curate\s+Quest(?:\s+for\s+\S+)?\s*$/i.test(line);
}

function isCurateQuestFinish(line) {
    // More specific
    return /^\s*(?:Completing|Finishing|Finished|Completed)\s+Curate\s+Quest(?:\s+for\s+\S+)?\s*$/i.test(line);
}

const removeSynonyms = ["remove", "delete", "purge", "clear", "expunge", "rm", "rem"];
function isRemoveCommand(line) {
    const firstWord = line.trim().split(/\s+/)[0].toLowerCase();
    return removeSynonyms.includes(firstWord);
}

// Takes a raw line of text and tries to parse it into a structured instruction object
// Possible types: 'add', 'remove', 'rename', 'setHighPriest', 'updateLOTH', 'ignore', 'error'
function parseInstruction(line) {
    const originalLine = line;
    // console.log("[DEBUG] Parsing line:", line);

    // --- Basic Filters & Normalization ---
    if (!line || typeof line !== 'string') return { type: 'ignore', reason: 'Invalid input line', originalLine };
    let candidateLine = line.trim();
    if (candidateLine.length === 0 || candidateLine.startsWith('//') || candidateLine.startsWith('#')) {
        return { type: 'ignore', reason: 'Empty or comment line', originalLine };
    }
    if (isQuestStart(line)) { // Use original line for exact match
        return { type: 'ignore', reason: 'Quest start line', originalLine };
    }

    // Normalize separators, collapse spaces, remove mentions
    candidateLine = candidateLine.replace(/[,:]+| [-–—]+ /g, ' - '); // Normalize separators to ' - '
    candidateLine = candidateLine.replace(/\s+/g, ' ').trim();
    candidateLine = candidateLine.replace(/@\s*\S+/g, '').trim(); // Remove @mentions


    // --- Check Specific Instruction Patterns ---

    // 1. Rename: (Old Name) > (New Name)
    // Be strict about the '>' separator
    let renameMatch = candidateLine.match(/^(.+?)\s+>\s+(.+)$/);
    if (renameMatch) {
        const oldName = renameMatch[1].trim();
        const newName = renameMatch[2].trim();
         if (oldName && newName && oldName.toLowerCase() !== newName.toLowerCase()) { // Ensure names are different
            console.log(`[DEBUG] Parsed RENAME: "${oldName}" => "${newName}"`);
            return { type: 'rename', oldName, newName, originalLine };
         } else {
             console.log(`[WARN] Invalid rename format or same names: ${originalLine}`);
             return { type: 'error', reason: 'Invalid rename format or names are the same', originalLine };
         }
    }

    // 2. LOTH Status Update: (name) no longer LOTH | (name) (is)? now LOTH
    let lothRemoveMatch = candidateLine.match(/^(.+?)\s+no\s+longer\s+LOTH\s*$/i);
    if (lothRemoveMatch) {
        const name = lothRemoveMatch[1].trim();
        if (name) {
            console.log(`[DEBUG] Parsed LOTH REMOVE: "${name}"`);
            return { type: 'updateLOTH', characterName: name, makeLOTH: false, originalLine };
        }
    }
    // Allow optional "is"
    let lothAddMatch = candidateLine.match(/^(.+?)\s+(?:is\s+)?now\s+LOTH\s*$/i);
    if (lothAddMatch) {
        const name = lothAddMatch[1].trim();
         if (name) {
            console.log(`[DEBUG] Parsed LOTH ADD: "${name}"`);
            return { type: 'updateLOTH', characterName: name, makeLOTH: true, originalLine };
         }
    }

    // 3. Remove Command: remove <name>
    if (isRemoveCommand(candidateLine)) {
        const parts = candidateLine.split(/\s+/);
        const nameToRemove = parts.slice(1).join(' ').trim(); // Get everything after the command word
        if (nameToRemove) {
             console.log(`[DEBUG] Parsed REMOVE: "${nameToRemove}"`);
            return { type: 'remove', characterName: nameToRemove, originalLine };
        } else {
            console.log(`[WARN] Remove command found but no name specified: "${originalLine}"`);
             return { type: 'error', reason: 'Remove command without name', originalLine };
        }
    }

    // --- Extract LOTH Status and Remove Marker ---
    // Check for "(LOTH)" or just "LOTH" at the end, possibly preceded by space or hyphen
    let isLOTH = false;
    const lothSuffixRegex = /(?:\s-)?\s*(?:\(\s*LOTH\s*\)|LOTH)\s*$/i;
    if (lothSuffixRegex.test(candidateLine)) {
        isLOTH = true;
        candidateLine = candidateLine.replace(lothSuffixRegex, '').trim();
        console.log(`[DEBUG] Detected LOTH suffix, remaining line: "${candidateLine}"`);
    }

    // 4. High Priest(ess) Assignment: High Priest(ess) - Name | HP Name
    // Allow "HP" abbreviation, flexible separators
    let highPriestMatch = candidateLine.match(/^(?:(?:High\s+Priest(?:ess)?)|HP)\s*[:-\s]+\s*(.+)$/i);
    if (highPriestMatch) {
        const name = highPriestMatch[1].trim();
         // Determine title based on full name or default to Priest if HP used
         let title = "High Priest";
         if (/High\s+Priestess/i.test(candidateLine)) {
             title = "High Priestess";
         }
        if (name) {
            console.log(`[DEBUG] Parsed SET HIGH PRIEST: "${name}" (${title}), LOTH: ${isLOTH}`);
            return { type: 'setHighPriest', highPriest: name, highPriestTitle: title, isLOTH, originalLine };
        }
    }

    // 5. Add/Move Character (Most complex case)
    // Try to identify Name, Rank, Divine from the remaining candidateLine
    let targetDivine = null;
    let targetRank = null;
    let characterName = null;
    let remainingLine = candidateLine; // Start with potentially LOTH-stripped line

    const finishingQuest = isCurateQuestFinish(line); // Check original line context

     // Strategy:
     // a. Look for explicit "Rank of Divine" patterns.
     // b. If not found, look for "of Divine" pattern.
     // c. If divine found, look for Rank among remaining words.
     // d. If only Rank found, infer Divine? (Risky, avoid for now).
     // e. Remaining part is the Name.

    // a. Explicit "Rank of Divine" (using known lists)
    const rankOfDivineRegex = new RegExp(`\\b(${KNOWN_RANKS.join('|')})\\s+of\\s+(${DIVINES.join('|')})\\b`, 'i');
    let rodMatch = remainingLine.match(rankOfDivineRegex);
    if (rodMatch) {
        targetRank = fuzzyMatch(rodMatch[1], KNOWN_RANKS); // Normalize found rank
        targetDivine = fuzzyMatch(rodMatch[2], DIVINES);   // Normalize found divine
        remainingLine = remainingLine.replace(rodMatch[0], '').trim();
        console.log(`[DEBUG] Pattern A matched: Rank=${targetRank}, Divine=${targetDivine}, Remaining: "${remainingLine}"`);
    } else {
        // b. Look for "of Divine"
         const divineRegex = new RegExp(`\\bof\\s+(${DIVINES.join('|')})\\b`, 'i');
         let divineMatch = remainingLine.match(divineRegex);
         if (divineMatch) {
             targetDivine = fuzzyMatch(divineMatch[1], DIVINES);
             remainingLine = remainingLine.replace(divineMatch[0], '').trim();
             console.log(`[DEBUG] Pattern B matched: Divine=${targetDivine}, Remaining: "${remainingLine}"`);

             // c. Look for Rank in remaining words (fuzzy)
             const words = remainingLine.split(/[\s-]+/);
             let bestRankMatch = null;
             let bestRankDist = 3; // Max distance for rank match
             let rankWord = '';
             for (const word of words) {
                 const potentialRank = fuzzyMatch(word, KNOWN_RANKS);
                 if (potentialRank) {
                     const dist = levenshtein(word.toLowerCase(), potentialRank.toLowerCase());
                     if (dist < bestRankDist) {
                         bestRankDist = dist;
                         bestRankMatch = potentialRank;
                         rankWord = word;
                     }
                 }
             }
             if (bestRankMatch) {
                 targetRank = bestRankMatch;
                 // Remove the matched rank word carefully
                 const rankWordRegex = new RegExp(`\\b${rankWord}\\b`, 'i'); // Match the specific word found
                 remainingLine = remainingLine.replace(rankWordRegex, '').trim();
                 console.log(`[DEBUG] Pattern C matched: Rank=${targetRank} (from word '${rankWord}'), Remaining: "${remainingLine}"`);
             }
         }
    }

     // Handle "Promoted to Curate of Divine" variation - might override previous finds if more specific
    let promotedMatch = candidateLine.match(/\bPromoted\s+to\s+Curate\s+of\s+(\S+)/i);
    if (promotedMatch) {
         const forcedDivine = fuzzyMatch(promotedMatch[1], DIVINES);
         if (forcedDivine) {
             console.log(`[DEBUG] Overriding with 'Promoted to Curate' pattern: Curate of ${forcedDivine}`);
             targetDivine = forcedDivine;
             targetRank = 'Curate';
             // Assume the name is everything before the "Promoted..." part
             remainingLine = candidateLine.substring(0, candidateLine.indexOf(promotedMatch[0])).trim();
         }
    }

    // Handle quest completion context -> Curate if Rank is missing but Divine is known
     if (finishingQuest && targetDivine && !targetRank) {
        console.log(`[DEBUG] Applying Curate rank due to quest completion context for Divine ${targetDivine}`);
        targetRank = 'Curate';
    }


    // Normalize Priestess -> Priest unless High Priestess
    if (targetRank && targetRank.toLowerCase() === 'priestess' && targetRank !== 'High Priestess') {
        console.log("[DEBUG] Normalizing 'Priestess' to 'Priest'");
        targetRank = 'Priest';
    }

    // e. The remaining part is the character name
    characterName = remainingLine.replace(/^-+|-+$/g, '').trim(); // Clean leading/trailing hyphens

    // --- Validation and Return ---
    if (characterName && targetRank && targetDivine) {
        // Check if the targetRank is one of the base ranks allowed for add/move
        if (!BASE_RANKS.includes(targetRank)) {
             console.log(`[WARN] Instruction targets rank "${targetRank}" which is not directly editable in tables (High Priest?). Cannot use ADD/MOVE for HP.`);
             // If the name *only* matches HP, LOTH updates handled earlier are sufficient.
             // If intended as ADD/MOVE, it's an error for HP via this path.
             return { type: 'error', reason: `Cannot add/move to non-base rank ${targetRank}`, originalLine };
        }
        console.log(`[DEBUG] Parsed ADD/MOVE: Name="${characterName}", Rank="${targetRank}", Divine="${targetDivine}", LOTH=${isLOTH}`);
        return { type: 'add', characterName, targetRank, targetDivine, isLOTH, originalLine };
    } else if (characterName && (targetRank || targetDivine)) {
        // Partial match - required info missing
        console.log(`[WARN] Partially parsed ADD/MOVE line: Name="${characterName}", Rank=${targetRank || 'N/A'}, Divine=${targetDivine || 'N/A'}. Missing info.`);
        return { type: 'error', reason: 'Missing rank or divine for add/move', originalLine };
    } else if (characterName && !targetRank && !targetDivine) {
        // Only a name was left - could be intended removal (missed earlier?), comment, or invalid line
         console.log(`[WARN] Line parsed down to only a name: "${characterName}". Could be invalid, comment, or ambiguous removal.`);
         // Let's treat this as an error unless it matches 'remove' command explicitly.
         return { type: 'error', reason: 'Only name found after parsing, ambiguous instruction', originalLine };
    }

    // If none of the patterns matched successfully
    console.log(`[WARN] Could not parse line into a known instruction format: "${originalLine}"`);
    return { type: 'error', reason: 'Unrecognized format', originalLine };
}


// --- Playwright Service ---
class PlaywrightService {
    constructor(config) {
        this.config = config;
        this.browser = null;
        this.page = null;
        this.isEditorOpen = false;
    }

    async launch() {
        if (this.browser) {
            console.log("[DEBUG] Playwright browser already launched.");
            // Ensure page exists if browser does
            if (!this.page || this.page.isClosed()) {
                this.page = await this.browser.newPage();
            }
            return;
        }
        console.log("[INFO] Launching Playwright browser (Chromium)...");
        try {
            this.browser = await chromium.launch({ headless: true }); // Use true for server environments
            this.page = await this.browser.newPage();
            this.page.setDefaultTimeout(30000); // Increase default timeout for network/selectors
            console.log("[INFO] Browser launched successfully.");
        } catch (error) {
             console.error("[FATAL] Failed to launch Playwright browser:", error);
             throw error; // Re-throw to halt execution if browser fails
        }
    }

    async close() {
        this.isEditorOpen = false; // Reset editor state on close
        if (this.browser) {
            console.log("[INFO] Closing Playwright browser...");
            try {
                await this.browser.close();
                 console.log("[INFO] Browser closed.");
            } catch (error) {
                 console.error("[ERROR] Error closing Playwright browser:", error);
            } finally {
                this.browser = null;
                this.page = null;
            }
        }
    }

    async _ensureLoggedInAndNavigated(targetUrl) {
        if (!this.page || !this.browser) throw new Error("Playwright browser not launched.");

        const maxRetries = 2;
        for (let attempt = 1; attempt <= maxRetries; attempt++) {
            console.log(`[INFO] Navigating to forum thread: ${targetUrl} (Attempt ${attempt})`);
            try {
                await this.page.goto(targetUrl, { waitUntil: 'domcontentloaded' });

                // Check for permission error / need to login *after* navigation
                const logoutIndicatorLocator = this.page.locator(".alert.alert-danger").filter({
                      hasText: "Error loading data: You do not have permission to view this",
                });

                try {
                    // Attempt to wait for the permission error element
                    console.log("[DEBUG] Checking for permission error element visibility...");
                    await logoutIndicatorLocator.waitFor({ state: 'visible', timeout: 10000 });

                    // --- If waitFor SUCCEEDS ---
                    // This means the permission error *is* visible. User is not logged in or lacks permissions.
                    console.log("[INFO] Permission error detected. Attempting login...");
                    await this._performLogin();

                    // After login, re-navigate to the target URL
                    console.log(`[INFO] Re-navigating to target URL after login: ${targetUrl}`);
                    await this.page.goto(targetUrl, { waitUntil: 'domcontentloaded' });

                    // Verify login by checking for permission error again (quick check)
                    // Use isVisible for a quick check now, assuming login fixed it.
                    const permissionErrorAfterLogin = await logoutIndicatorLocator.isVisible({ timeout: 5000 });
                    if (permissionErrorAfterLogin) {
                        throw new Error("Login appeared successful, but still lack permission to view the thread.");
                    }
                    console.log("[INFO] Navigation successful after login.");
                    return; // Logged in and navigated successfully

                } catch (error) {
                    // --- If waitFor FAILS ---
                    // Check if it's the expected timeout error from Playwright
                    if (error.name === 'TimeoutError') {
                        // This means the permission error element did NOT appear within the timeout.
                        // Assume the user is logged in and has permission.
                        console.log("[INFO] Permission error element not found within timeout. Assuming logged in and permitted.");
                        console.log("[INFO] Forum thread loaded successfully.");
                        return; // Already logged in or public thread
                    } else {
                        // Unexpected error during waitFor (not a timeout)
                        console.error("[ERROR] Unexpected error while checking for permission element:", error);
                        throw error; // Re-throw unexpected errors
                    }
                }
            } catch (error) {
                 console.error(`[ERROR] Navigation/Login attempt ${attempt} failed:`, error);
                 if (attempt === maxRetries) {
                     throw new Error(`Failed to navigate and ensure login after ${maxRetries} attempts: ${error.message}`);
                 }
                 await this.page.waitForTimeout(1000); // Wait before retry
            }
        }
    }

    async _performLogin() {
        // Derive login page URL from forum URL (common pattern)
        const forumUrl = new URL(this.config.forumUrl);
        const loginUrl = new URL('/login/', forumUrl.origin).toString();

        console.log(`[INFO] Navigating to login page: ${loginUrl}`);
        await this.page.goto(loginUrl, { waitUntil: 'domcontentloaded' });

        console.log("[INFO] Clicking 'Login with your Guildtag Account' button (if exists)...");
        // Use a specific button selector with a filter for the text
        const guildtagLoginButton = this.page.locator('button').filter({ hasText: "Login with your Guildtag Account" }).first();
        try {
            await guildtagLoginButton.waitFor({ state: 'visible', timeout: 5000 });
            await guildtagLoginButton.click();
            await this.page.waitForLoadState('domcontentloaded'); // Wait for potential page change
        } catch (error) {
            if (error.name === 'TimeoutError') {
                console.log("[INFO] 'Login with Guildtag Account' button not found, assuming direct login form.");
            } else {
                console.error("[ERROR] Unexpected error while waiting for Guildtag login button:", error);
                throw error;
            }
        }


        console.log("[INFO] Filling login credentials...");
        // Robust selectors using associated labels
        await this.page.locator('label:has-text("Email")').locator(' + input, input').first().fill(this.config.email);
        await this.page.locator('label:has-text("Password")').locator(' + input, input').first().fill(this.config.password);

        console.log("[INFO] Clicking Login button...");
         // Common login button texts
         await this.page.locator('button:has-text("Login"), button:has-text("Sign In"), input[type="submit"][value="Login"], input[type="submit"][value="Sign In"]').first().click();

        console.log("[INFO] Waiting for navigation/confirmation after login...");
        try {
             // Wait until URL changes OR a known element on the logged-in page appears
             await this.page.waitForURL(url => !url.pathname.includes('/login'), { timeout: 15000 });
             console.log(`[INFO] Login likely successful. Current URL: ${this.page.url()}`);
        } catch (e) {
            // Check if login failed explicitly
            const loginError = this.page.locator('.error:visible, .message.error:visible, [data-message-type="error"]:visible');
            if (await loginError.count() > 0) {
                 const errorText = await loginError.first().textContent();
                 console.error(`[ERROR] Login failed with error: ${errorText}`);
                 throw new Error(`Login failed: ${errorText}`);
            } else {
                 console.warn("[WARN] Timeout waiting for URL change after login, but no explicit error found. Proceeding cautiously.");
                 // Potentially add check for expected element on successful login page
            }
        }
    }

    async getForumPostContentAndOpenEditor() {
        if (this.isEditorOpen) {
            console.log("[DEBUG] Editor already open, retrieving content...");
             const textarea = this.page.locator('textarea').first(); // Assume editor state persists
             if (!(await textarea.isVisible())) {
                 console.warn("[WARN] Editor was marked open, but textarea not found. Re-opening.");
                 this.isEditorOpen = false; // Force reopen
             } else {
                 return await textarea.inputValue();
             }
        }

        await this._ensureLoggedInAndNavigated(this.config.forumUrl);

        // Find and click the Edit link/button
        console.log("[INFO] Clicking 'Edit' link/button...");
        // Use a broader selector for edit links/buttons
        const editLink = this.page.locator('a:has-text("Edit"), button:has-text("Edit"), a[data-action="edit"]').first();
        try {
            await editLink.waitFor({ state: 'visible', timeout: 10000 });
            await editLink.click();
        } catch (error) {
             console.error("[ERROR] Could not find or click the 'Edit' link/button.", error);
             const pageContent = await this.page.content();
             console.error("[DEBUG] Page source (first 500 chars):", pageContent.slice(0,500));
             throw new Error("Could not find the 'Edit' link/button. Check selector and permissions.");
        }


        // Wait for the editor textarea to appear
        console.log("[INFO] Waiting for editor textarea...");
        // Use a common selector for rich text editors or plain textareas
        const textarea = this.page.locator('textarea, .wysiwyg-editor textarea').first();
        try {
            await textarea.waitFor({ state: 'visible', timeout: 15000 });
        } catch (error) {
             console.error("[ERROR] Editor textarea did not become visible after clicking Edit.", error);
              const pageContent = await this.page.content();
             console.error("[DEBUG] Page source after clicking edit (first 500 chars):", pageContent.slice(0,500));
             throw new Error("Editor textarea did not appear. Check editor loading behavior.");
        }


        console.log("[INFO] Extracting content from textarea...");
        const content = await textarea.inputValue();
        this.isEditorOpen = true; // Mark editor as open
        console.log(`[DEBUG] Extracted ${content?.length || 0} characters from textarea.`);
        return content;
    }

    async updateForumPost(newContent) {
        if (!this.isEditorOpen || !this.page) {
            throw new Error("Editor is not open. Call getForumPostContentAndOpenEditor first.");
        }

        console.log("[INFO] Updating textarea content...");
        const textarea = this.page.locator('textarea, .wysiwyg-editor textarea').first();
         if (!(await textarea.isVisible())) {
             throw new Error("Textarea element disappeared before update.");
         }
        await textarea.fill(newContent); // Fill replaces existing content
        console.log(`[DEBUG] Filled textarea with ${newContent.length} characters.`);

        // Find and click the Save button
        console.log("[INFO] Clicking 'Save Edit' button...");
        // Common save button selectors
        const saveButton = this.page.locator('button:has-text("Save"), button:has-text("Save Edit"), input[type="submit"][value="Save"], input[type="submit"][value="Save Changes"]').first();
        try {
            await saveButton.waitFor({ state: 'visible', timeout: 5000 });
            await saveButton.click();
        } catch (error) {
             console.error("[ERROR] Could not find or click the 'Save Edit' button.", error);
             throw new Error("Could not find or click the 'Save Edit' button. Check selector.");
        }

        // Wait for save confirmation (e.g., URL change, success message)
        console.log("[INFO] Waiting for save confirmation...");
        try {
             // Wait for URL to change away from edit mode OR for a success message
             await this.page.waitForURL(url => !url.search.includes('do=editpost') && !url.search.includes('action=edit'), { timeout: 20000 });
             // Add check for success message if URL doesn't change reliably?
             console.log(`[INFO] Edit saved successfully. Current URL: ${this.page.url()}`);
             this.isEditorOpen = false; // Editor is closed after save
        } catch(error) {
             console.warn("[WARN] Timeout waiting for URL change after save. Edit might have saved, but confirmation unclear.", error);
              // Check for error messages on page?
             const saveError = this.page.locator('.error:visible, .message.error:visible, [data-message-type="error"]:visible');
              if (await saveError.count() > 0) {
                  const errorText = await saveError.first().textContent();
                  console.error(`[ERROR] Save failed with error message: ${errorText}`);
                  // Should we keep editor state open? Maybe.
                  throw new Error(`Save failed: ${errorText}`);
              }
             // If no specific error, assume saved but redirect timed out.
             this.isEditorOpen = false;
        }
    }

    // Utility to save content locally to a dedicated folder
    async saveContentToFile(content, suffix = '') {
        const backupsDir = path.join(process.cwd(), 'clergy-roster-backups');
        try {
            await mkdir(backupsDir, { recursive: true }); // Ensure directory exists

            const timestamp = new Date().toISOString().replace(/[:.]/g, '-');
            const filename = `roster-${timestamp}${suffix}.html`;
            const savePath = path.join(backupsDir, filename);

            await writeFile(savePath, content || '', 'utf8'); // Write empty string if content is null/undefined
            console.log(`[INFO] Saved content to ${savePath}`);
            return savePath; // Return path for potential diffing
        } catch (error) {
            console.error(`[ERROR] Failed to save content to file in ${backupsDir}:`, error);
            return null;
        }
    }
}


// --- Discord Service ---
class DiscordService {
    constructor(token, channelId) {
        if (!token || !channelId) {
            throw new Error("Discord token and channel ID are required.");
        }
        this.token = token;
        this.channelId = channelId;
        this.client = new Client({
            intents: [
                GatewayIntentBits.Guilds,
                GatewayIntentBits.GuildMessages,
                GatewayIntentBits.MessageContent, // NEEDED to read message content
                GatewayIntentBits.GuildMessageReactions // NEEDED to check reactions
            ],
            partials: [Partials.Message, Partials.Channel, Partials.Reaction], // Recommended for reliability
        });
        this.targetChannel = null;
    }

    async connect(onReadyCallback, onMessageCallback) {
        console.log("[INFO] Connecting Discord bot...");

        this.client.once(Events.ClientReady, async readyClient => {
            console.log(`[INFO] Discord bot logged in as ${readyClient.user.tag}!`);
            try {
                 this.targetChannel = await this.client.channels.fetch(this.channelId);
                 if (!this.targetChannel || !this.targetChannel.isTextBased()) {
                     console.error(`[FATAL] Could not find channel with ID ${this.channelId} or it's not a text channel.`);
                     process.exit(1); // Bot cannot function without target channel
                 }
                 console.log(`[INFO] Successfully fetched and monitoring channel: #${this.targetChannel.name} (${this.channelId})`);
                 if (onReadyCallback) {
                     await onReadyCallback(); // Signal readiness and trigger initial processing
                 }
            } catch (error) {
                console.error(`[FATAL] Failed to fetch channel ${this.channelId}:`, error);
                process.exit(1); // Exit if channel fetch fails
            }
        });

        this.client.on(Events.MessageCreate, async message => {
            // Basic filtering
            if (message.author.bot || message.channelId !== this.channelId || !message.content) {
                return;
            }
             console.log(`[INFO] Received message [${message.id}] from ${message.author.tag} in #${this.targetChannel.name}: "${message.content.slice(0, 50)}..."`);
             if (onMessageCallback) {
                 try {
                    await onMessageCallback(message); // Pass the full message object
                 } catch (error) {
                      console.error(`[ERROR] Error processing incoming message ${message.id}:`, error);
                      // Maybe notify user of failure?
                      await this.replyToMessage(message.id, "Sorry, an internal error occurred while processing your command.").catch(e => console.error("Failed to send error reply:", e));
                 }
             }
        });

        this.client.on(Events.Error, error => {
            console.error('[ERROR] Discord client encountered an error:', error);
        });
        this.client.on(Events.Warn, warning => {
            console.warn('[WARN] Discord client warning:', warning);
        });

        try {
            await this.client.login(this.token);
        } catch (error) {
             console.error("[FATAL] Failed to login to Discord:", error);
             process.exit(1); // Cannot proceed without login
        }
    }

     // Fetches messages back in time until one with *any* reaction is found.
     // Returns messages *newer* than the first reacted message found, ordered newest to oldest.
     async fetchUnprocessedMessages() {
        if (!this.targetChannel) throw new Error("Discord client not ready or channel not found.");

        console.log(`[INFO] Fetching message history in #${this.targetChannel.name} to find unprocessed commands (newest first)...`);
        let unprocessedMessages = []; // Will store messages newest to oldest
        let lastMessageId = null;
        let foundReactedMessage = false;
        const fetchLimit = 100;
        let fetchedCount = 0;
        const maxTotalFetch = 1000; // Safety limit

        try {
             while (!foundReactedMessage && fetchedCount < maxTotalFetch) {
                 const options = { limit: fetchLimit };
                 if (lastMessageId) {
                     options.before = lastMessageId; // Fetch messages older than the oldest in the previous batch
                 }

                 const messages = await this.targetChannel.messages.fetch(options);
                 fetchedCount += messages.size;

                 if (messages.size === 0) {
                     console.log("[DEBUG] Reached end of message history or beginning of channel.");
                     break; // No more messages
                 }

                 console.log(`[DEBUG] Fetched batch of ${messages.size} messages (total fetched: ${fetchedCount}). Processing newest to oldest in batch.`);
                 lastMessageId = messages.lastKey(); // Oldest message ID in this batch, used for next fetch if needed

                 // Messages are fetched newest to oldest by default. Iterate in that order.
                 for (const message of messages.values()) {
                     if (message.author.bot) continue; // Skip bot messages

                     // Check cache for reactions. Stop *searching* history if a reacted message is found.
                     if (message.reactions.cache.size > 0) {
                         console.log(`[DEBUG] Found reacted message [${message.id}]. Stopping history scan.`);
                         foundReactedMessage = true;
                         break; // Stop processing this batch and stop fetching older messages
                     } else {
                         // If no reaction found AND we haven't already found the boundary reacted message,
                         // this message is considered unprocessed.
                         console.log(`[DEBUG] Adding unreacted message [${message.id}] to processing batch.`);
                         unprocessedMessages.push(message); // Add to end for now
                     }
                 } // End for loop iterating through batch
             } // End while loop fetching batches

             if (fetchedCount >= maxTotalFetch) {
                 console.warn(`[WARN] Reached fetch limit (${maxTotalFetch}) without finding a reacted message. Processing the ${unprocessedMessages.length} found so far.`);
             }

        } catch (error) {
            console.error("[ERROR] Failed during message history fetch:", error);
            // Return whatever was collected, or handle error appropriately
        }

         // Reverse the collected messages so the newest is first
         unprocessedMessages.reverse();

         console.log(`[INFO] Found ${unprocessedMessages.length} potentially unprocessed messages (newer than the first found reacted message), ordered newest to oldest.`);
         return unprocessedMessages; // Return newest to oldest
     }


    async reactToMessage(messageId, emoji) {
        if (!this.targetChannel) return;
        try {
            // Fetch the message fresh to ensure it exists and is accessible
            const message = await this.targetChannel.messages.fetch(messageId);
            // Check if already reacted with this emoji by the bot
            const existingReaction = message.reactions.cache.get(emoji);
            if (existingReaction && existingReaction.me) {
                 // console.log(`[DEBUG] Already reacted with ${emoji} to message ${messageId}`);
                return;
            }
            await message.react(emoji);
             // console.log(`[DEBUG] Reacted with ${emoji} to message ${messageId}`);
        } catch (error) {
            // Common errors: Unknown Message, Missing Permissions
            if (error.code === 10008) { // Unknown Message
                 console.warn(`[WARN] Failed to react to message ${messageId}: Message not found (possibly deleted).`);
            } else if (error.code === 50013) { // Missing Permissions
                 console.error(`[ERROR] Failed to react to message ${messageId}: Missing 'Add Reactions' permission in #${this.targetChannel.name}.`);
            } else {
                console.error(`[ERROR] Failed to react to message ${messageId} with ${emoji}:`, error);
            }
        }
    }

    async replyToMessage(messageId, content) {
         if (!this.targetChannel) return;
         try {
            const message = await this.targetChannel.messages.fetch(messageId);
            await message.reply({ content: content, allowedMentions: { repliedUser: false } }); // Avoid pinging user by default
            // console.log(`[DEBUG] Replied to message ${messageId}`);
         } catch (error) {
            if (error.code === 10008) { // Unknown Message
                 console.warn(`[WARN] Failed to reply to message ${messageId}: Message not found (possibly deleted).`);
            } else if (error.code === 50013) { // Missing Permissions
                 console.error(`[ERROR] Failed to reply to message ${messageId}: Missing 'Send Messages' or 'Read Message History' permission in #${this.targetChannel.name}.`);
            } else {
                console.error(`[ERROR] Failed to reply to message ${messageId}:`, error);
            }
         }
    }
}


// --- Main Application Logic ---

class RosterBot {
    constructor() {
        this.discordService = new DiscordService(DISCORD_BOT_TOKEN, DISCORD_CHANNEL_ID);
        this.playwrightService = new PlaywrightService({
            forumUrl: GUILDTAG_FORUM_URL,
            email: GUILDTAG_EMAIL,
            password: GUILDTAG_PASSWORD,
        });
        // State lock to prevent concurrent Playwright operations
        this.processingLock = false;
        // Queue for messages arriving while processing
        this.messageQueue = [];
        this.isCurrentlyProcessing = false;
    }

    async run() {
        // Connect Discord and set up handlers
        await this.discordService.connect(
            this.handleDiscordReady.bind(this),    // Called when bot is ready
            this.handleIncomingMessage.bind(this) // Called for each new message
        );
    }

    // Called once the Discord bot is logged in and the target channel is ready
    async handleDiscordReady() {
        console.log("[INFO] Discord ready. Performing initial check for unprocessed messages...");
        // Fetch historical messages and trigger processing if needed
        const unprocessedMessages = await this.discordService.fetchUnprocessedMessages();
        if (unprocessedMessages.length > 0) {
            console.log(`[INFO] Enqueuing ${unprocessedMessages.length} historical messages for processing (newest first).`);
            // Add them to the FRONT of the queue, preserving newest-first order
            this.messageQueue.unshift(...unprocessedMessages);
            this._triggerProcessing(); // Start processing the queue
        } else {
            console.log("[INFO] No unprocessed historical messages found.");
        }
         console.log("[INFO] Bot is ready and listening for new messages.");
    }

    // Called whenever a new message arrives in the monitored channel
    handleIncomingMessage(message) {
        console.log(`[INFO] Queuing new message ${message.id} for processing (adding to front).`);
        this.messageQueue.unshift(message); // Add new messages to the FRONT
        this._triggerProcessing(); // Attempt to process the queue
    }

    // Internal method to start processing the queue if not already running
    _triggerProcessing() {
        if (this.isCurrentlyProcessing) {
            console.log("[DEBUG] Processor already running. New message added to queue.");
            return; // Don't start another loop if one is active
        }
        if (this.messageQueue.length === 0) {
            // console.log("[DEBUG] Processing triggered, but queue is empty.");
            return; // Nothing to process
        }

        console.log("[INFO] Starting processing loop.");
        this.isCurrentlyProcessing = true;
        // Use setImmediate to avoid blocking the event loop and allow handleIncomingMessage to return quickly
        setImmediate(async () => {
            try {
                await this._processQueue();
            } catch (error) {
                 console.error("[FATAL] Unhandled error in processing loop:", error);
                 // Consider adding more robust error handling/recovery here
            } finally {
                this.isCurrentlyProcessing = false;
                console.log("[INFO] Processing loop finished.");
                 // Check if new messages arrived while processing was ongoing
                 if (this.messageQueue.length > 0) {
                     console.log("[INFO] New messages arrived during processing. Restarting loop.");
                     this._triggerProcessing(); // Re-trigger if queue is not empty
                 }
            }
        });
    }


    // Processes the message queue one batch at a time
    async _processQueue() {
        // Decide batching strategy: process all available messages in one go? Or one by one?
        // Processing all in one batch is more efficient (one login/edit cycle).
        if (this.messageQueue.length === 0) {
             console.log("[DEBUG] Queue is empty. Nothing to process.");
             return;
        }

        const messagesToProcess = [...this.messageQueue]; // Copy queue for this batch
        this.messageQueue = []; // Clear the queue immediately

        console.log(`\n--- Processing batch of ${messagesToProcess.length} message(s) ---`);
        // Add IDs for logging
        console.log(`[DEBUG] Message IDs in batch: ${messagesToProcess.map(m => m.id).join(', ')}`);

        // Acquire lock (though _triggerProcessing should prevent concurrent runs)
        if (this.processingLock) {
            console.error("[ERROR] Processing lock was already acquired! This should not happen.");
            // Re-queue the messages?
            this.messageQueue.unshift(...messagesToProcess);
            return;
        }
        this.processingLock = true;

        let originalHtml = null;
        let originalFile = null;
        let updatedFile = null;
        let changesMade = false; // Track if the HTML actually changed

        try {
            await this.playwrightService.launch(); // Ensure browser is running

            // Get current content from the forum *before* processing instructions
            originalHtml = await this.playwrightService.getForumPostContentAndOpenEditor();
            originalFile = await this.playwrightService.saveContentToFile(originalHtml, '-original');

            const rosterState = new RosterState();
            rosterState.parseFromHtml(originalHtml); // Parse the initial state

            const instructionsToApply = [];
            const results = { success: [], failure: [] }; // Track individual message outcomes

            // Parse all messages in the batch
            for (const message of messagesToProcess) {
                const parsed = parseInstruction(message.content);
                 if (parsed.type === 'error') {
                     console.log(`[WARN] Failed parsing message ${message.id}: ${parsed.reason} - "${parsed.originalLine}"`);
                     results.failure.push({ message: message, reason: parsed.reason });
                 } else if (parsed.type !== 'ignore') {
                     instructionsToApply.push(parsed);
                     // Mark as success for now, reaction depends on whether changes are made
                     results.success.push(message);
                 } else {
                      // Ignored messages don't need reactions (comments, quest starts)
                      console.log(`[DEBUG] Ignored message ${message.id}: ${parsed.reason}`);
                 }
            }

            // Apply valid instructions if any exist
            if (instructionsToApply.length > 0) {
                 console.log(`[INFO] Applying ${instructionsToApply.length} valid instructions to roster state...`);
                 rosterState.applyInstructions(instructionsToApply); // Apply all valid instructions to internal state
                 const updatedHtml = rosterState.regenerateHtml(); // Generate the new HTML based on state

                 // Compare generated HTML with original HTML (simple string compare)
                 if (updatedHtml !== originalHtml) {
                    console.log("[INFO] Changes detected after applying instructions. Updating forum post...");
                    await this.playwrightService.updateForumPost(updatedHtml);
                    updatedFile = await this.playwrightService.saveContentToFile(updatedHtml, '-updated');
                    changesMade = true; // Mark that the forum was actually updated
                    console.log("[INFO] Forum post updated successfully.");
                 } else {
                    console.log("[INFO] Instructions were valid, but resulted in no change to the roster HTML. Skipping forum update.");
                    // No need to save updated file as it's same as original
                    updatedFile = originalFile; // For diffing purposes, point to original
                 }
            } else {
                console.log("[INFO] No valid instructions found in the batch to apply. No changes made.");
                 updatedFile = originalFile; // No changes, for diffing
            }

            // --- React to messages based on outcome ---
            // Success = parsed correctly. React ✅ only if changes were made OR no changes needed.
            // Failure = parsing error. React ❓ and reply.
            for (const message of results.success) {
                // React success if changes were made OR if no instructions needed applying (but parsing was ok)
                // Or if valid instructions caused no net change
                await this.discordService.reactToMessage(message.id, '✅');
            }
             for (const { message, reason } of results.failure) {
                 await this.discordService.reactToMessage(message.id, '❓');
                 await this.discordService.replyToMessage(message.id, `Failed to parse command (Reason: ${reason || 'Unknown'}). Check pinned message for syntax.`);
             }

        } catch (error) {
            console.error("[FATAL] Unrecoverable error during batch processing:", error);
            // Attempt to notify Discord about the failure for this batch
             const firstMessageId = messagesToProcess[0]?.id;
             if (firstMessageId) {
                 await this.discordService.replyToMessage(firstMessageId, `An unexpected error occurred while processing the batch: ${error.message}. Some commands may not have been applied.`).catch(e => console.error("Failed to send batch error reply:", e));
                 // React with error to all messages in the failed batch?
                 for(const msg of messagesToProcess) {
                     await this.discordService.reactToMessage(msg.id, '❌').catch(()=>{}); // Fire and forget reaction
                 }
             }
        } finally {
             if (changesMade && originalFile && updatedFile && originalFile !== updatedFile) {
                 console.log(`[INFO] Original and updated content saved to:\n  - ${originalFile}\n  - ${updatedFile}`);
             } else if (!changesMade) {
                 console.log("[INFO] Skipping file saving diff indication as no changes were made to the forum post.");
             }
            // Ensure browser is closed after processing the batch
            await this.playwrightService.close();
            this.processingLock = false; // Release lock
            console.log("--- Finished processing batch ---");
        }
    } // End _processQueue
}


// --- Entry Point ---
async function main() {
    console.log("Starting Clergy Roster Bot...");
    const bot = new RosterBot();
    await bot.run(); // Start the bot (connects Discord, sets up listeners)
    console.log("Bot run() method finished. Process should stay alive listening for Discord events.");
     // Keep process alive (though Discord client should do this)
     // process.stdin.resume(); // Keep Node.js process alive
}

// Graceful shutdown handling
process.on('SIGINT', async () => {
  console.log('\n[INFO] SIGINT received. Shutting down...');
  // Add cleanup here if needed (e.g., close Playwright if open, logout Discord client)
  process.exit(0);
});
process.on('SIGTERM', async () => {
  console.log('[INFO] SIGTERM received. Shutting down...');
  process.exit(0);
});

main().catch(err => {
    console.error("[FATAL] Unhandled error in main execution:", err);
    process.exit(1); // Exit with error code
});