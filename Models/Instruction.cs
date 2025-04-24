namespace ClergyRosterBot.Models;

public enum InstructionType
{
    Add,
    Remove,
    Rename,
    SetHighPriest,
    UpdateLOTH,
    Ignore,
    Error
}

public record Instruction(
    InstructionType Type,
    string? Reason, // For Ignore/Error types
    string OriginalLine)
{
    // Properties for specific types
    public string? CharacterName { get; init; }
    public string? TargetRank { get; init; } // Also used for HighPriest title
    public string? TargetDivine { get; init; }
    public bool IsLOTH { get; init; } // Default is false
    public string? OldName { get; init; }
    public string? NewName { get; init; }
    public bool? MakeLOTH { get; init; } // For explicit UpdateLOTH

    // Constructor for simpler Ignore/Error cases
    // public Instruction(InstructionType type, string reason, string originalLine)
    //     : this(type, reason, originalLine, null, null, null, false, null, null, null) { }

     // Private primary constructor for the record
    // private Instruction(InstructionType Type,
    //                   string? Reason,
    //                   string OriginalLine,
    //                   string? CharacterName,
    //                   string? TargetRank,
    //                   string? TargetDivine,
    //                   bool IsLOTH,
    //                   string? OldName,
    //                   string? NewName,
    //                   bool? MakeLOTH)
    // {
    //     this.Type = Type;
    //     this.Reason = Reason;
    //     this.OriginalLine = OriginalLine;
    //     this.CharacterName = CharacterName;
    //     this.TargetRank = TargetRank;
    //     this.TargetDivine = TargetDivine;
    //     this.IsLOTH = IsLOTH;
    //     this.OldName = OldName;
    //     this.NewName = NewName;
    //     this.MakeLOTH = MakeLOTH;
    // }
} 