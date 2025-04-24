using System.Text.RegularExpressions;

namespace ClergyRosterBot.Utilities;

public static partial class Selectors
{
    // General
    public const string AlertDanger = ".alert.alert-danger";
    public const string ErrorVisible = ".error:visible, .message.error:visible, [data-message-type=\"error\"]:visible";

    // Login Page
    public const string GuildtagLoginButton = "button";
    public const string GuildtagLoginButtonText = "Login with your Guildtag Account"; // Exact text match
    public const string LabelEmail = "label:has-text(\"Email\")";
    public const string LabelPassword = "label:has-text(\"Password\")";
    // Input field relative to a label (following sibling OR descendant)
    public static string InputNearLabel(string labelSelector) => $"{labelSelector} + input, {labelSelector} input"; // Combine adjacent sibling and descendant
    public const string LoginForm = "form[action*=\"login\"]";
    public const string LoginButtonWidget = ".widget-login .card-footer button.btn-primary";
    // Regex for Login button text (case-insensitive)
    public static readonly Regex LoginButtonRegex = GenerateLoginButtonRegex();

    // Forum Post / Editor
    public const string EditLink = "a:has-text(\"Edit\"):not(:has-text(\"Edit Thread\")), button:has-text(\"Edit\"):not(:has-text(\"Edit Thread\")), a[data-action=\"edit\"]:not(:has-text(\"Edit Thread\"))";
    // These editor selectors might be fragile and depend heavily on the specific forum software's structure.
    // Consider making them configurable if possible.
    public const string EditorTextarea = "#compose-container > div:nth-child(2) > div.row > div > div > div.form-group > textarea";
    public const string FormTextarea = "textarea.form-text.form-control"; // Often same as EditorTextarea, but check
    public const string SaveButton = "button.btn-primary.bk:has-text(\"Save Edit\")";

    // Pre-compiled Regex
    [GeneratedRegex("^Login\\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex GenerateLoginButtonRegex();
} 