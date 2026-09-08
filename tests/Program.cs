using ClergyRosterBot.Models;
using ClergyRosterBot.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ClergyRosterBot;
using HtmlAgilityPack;

var parser = new InstructionParser(NullLogger<InstructionParser>.Instance);
foreach (var line in new[] {
    "Beginning Curate Quest", "Starting Curate Quest for Example",
    "Example-Name - Started Curate Quest (8/11/2026)",
    "Example-Name - Beginning Curate Quest on 8/11" })
{
    if (parser.ParseInstruction(line).Type != InstructionType.Ignore)
        throw new Exception($"V3: Quest announcement must be ignored: {line}");
}
if (parser.ParseInstruction("Example-Name - Curate of Zenithar").Type != InstructionType.Add)
    throw new Exception("V3: Actual promotion must remain actionable");
Console.WriteLine("PASS V3 quest announcements and actual promotions");

string Cell(int i) => $"<td style='color:red'><span><u>Priest</u></span>Leader {i}<br>" +
    $"<span><u>Curate</u></span>Member {i} &#42;<br><span><u>Prior</u></span><br>" +
    "<span><u>Acolyte</u></span><br></td>";
string Table(int start) => "<table><tr valign='top'>" +
    string.Concat(Enumerable.Range(start, 4).Select(i => $"<td><img src='https://example.com/divine-{i}.png'></td>")) +
    "</tr><tr valign='top'>" + string.Concat(Enumerable.Range(start, 4).Select(Cell)) + "</tr></table>";
var fixture = "# Current High Priest: Example Leader\n" + Table(0) + Table(4) + "<p>Keep footer</p>";
var roster = new RosterState(NullLogger<RosterState>.Instance);
roster.ParseFromHtml(fixture);
roster.ApplyInstructions([
    parser.ParseInstruction("New-One - Acolyte of Kynareth"),
    parser.ParseInstruction("New-Two - Prior of Arkay"),
    parser.ParseInstruction("New-Three - Prior of Stendarr")
]);
var output = roster.RegenerateHtml();
var roundTrip = new RosterState(NullLogger<RosterState>.Instance);
roundTrip.ParseFromHtml(output);
for (var i = 0; i < 8; i++)
{
    Assert(roundTrip.AllDivineData[i]["Priest"].SequenceEqual(new[] { $"Leader {i}" }), "V5 unrelated priest");
    Assert(roundTrip.AllDivineData[i]["Curate"].SequenceEqual(new[] { $"Member {i} &#42;" }), "V5 unrelated LOTH");
}
Assert(roundTrip.AllDivineData[4]["Acolyte"].Contains("New-One"), "V5 acolyte addition");
Assert(roundTrip.AllDivineData[1]["Prior"].Contains("New-Two"), "V5 prior addition");
Assert(roundTrip.AllDivineData[6]["Prior"].Contains("New-Three"), "V5 prior addition");
var dom = new HtmlDocument(); dom.LoadHtml(output);
Assert(dom.DocumentNode.SelectNodes("//img").Count == 8 && output.Contains("Keep footer")
    && output.StartsWith("# Current High Priest: Example Leader"), "V5 formatting and heading");
Console.WriteLine("PASS V5 roster round-trip, unrelated members, LOTH and surrounding content");
var emptyPriest = new RosterState(NullLogger<RosterState>.Instance);
emptyPriest.ParseFromHtml(fixture.Replace("Leader 0", ""));
emptyPriest.ApplyInstructions([parser.ParseInstruction("New-One - Acolyte of Kynareth")]);
Assert(!emptyPriest.RegenerateHtml().Contains("Vacant"), "V5 untouched empty priest cell must stay empty");

await using var site = new GuildTagFixture();
var backup = Path.Combine(Path.GetTempPath(), "clergy-regression-" + Guid.NewGuid());
var settings = new BotSettings {
    DiscordBotToken = "synthetic", DiscordChannelId = 1, GuildtagForumUrl = site.Url,
    GuildtagEmail = "example@example.com", GuildtagPassword = "synthetic", BackupsDirectory = backup
};
await using var service = new PlaywrightService(NullLogger<PlaywrightService>.Instance, Options.Create(settings));
try
{
    var body = await service.ReadForumPostAsync();
    Assert(body == site.Body && site.LoginCount == 1, "V1 authenticate without permission banner; select first post by number");
    Console.WriteLine("PASS V1 native login and first-post identity");
    site.RateLimitReads = 1;
    await service.UpdateForumPostAsync("<p>Updated source</p>");
    Assert(site.Body == "<p>Updated source</p>" && site.Writes == 1 && Directory.GetFiles(backup).Length > 0,
        "V2/V4 paced read retry, backup and verified persistence");
    await service.UpdateForumPostAsync(site.Body);
    Assert(site.Writes == 1, "V2 already-current must not write twice");
    Console.WriteLine("PASS V2/V4 rate limit recovery, backup, verified save and idempotent repeat");

    site.Body = "<p>Concurrent edit</p>";
    await Reject(() => service.UpdateForumPostAsync("<p>Stale overwrite</p>"), "roster changed");
    Assert(site.Writes == 1, "V2 concurrent change prevented write");
    Console.WriteLine("PASS V2 intervening edit rejected");

    await service.ReadForumPostAsync();
    site.PersistSave = false;
    await Reject(() => service.UpdateForumPostAsync("<p>Unpersisted</p>"), "not verified");
    Assert(site.Writes == 2 && site.Body == "<p>Concurrent edit</p>", "V2 HTTP 200 is insufficient");
    Console.WriteLine("PASS V2 unpersisted HTTP 200 rejected");

    site.PersistSave = true;
    site.SaveStatus = 500;
    await service.UpdateForumPostAsync("<p>Landed despite uncertain response</p>");
    Assert(site.Writes == 3, "V2 uncertain landed write verified without duplicate");
    Console.WriteLine("PASS V2 uncertain save checked before any retry");

    site.LoggedIn = false;
    await service.ReadForumPostAsync();
    Assert(site.LoginCount == 2, "V1 expired session reauthenticates");
    Console.WriteLine("PASS V1 expired session recovery");
    site.CanEdit = false;
    await Reject(async () => { await service.ReadForumPostAsync(); }, "cannot edit");
    site.CanEdit = true;
    site.WrongThread = true;
    await Reject(async () => { await service.ReadForumPostAsync(); }, "different thread");
    site.WrongThread = false;
    site.MissingFirstPost = true;
    await Reject(async () => { await service.ReadForumPostAsync(); }, "first roster post");
    Assert(site.Writes == 3, "V1 permission/identity failures never write");
    Console.WriteLine("PASS V1 edit permission, thread identity and missing first-post failures");
}
finally { if (Directory.Exists(backup)) Directory.Delete(backup, true); }

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static async Task Reject(Func<Task> action, string expected)
{
    try { await action(); }
    catch (Exception ex) when (ex.Message.Contains(expected, StringComparison.OrdinalIgnoreCase)) { return; }
    throw new Exception($"Expected rejection containing: {expected}");
}
