using System.Text.Json;
using ZdrowieInfo.Server.Reports;
using ZdrowieInfo.Server.Sources;

namespace ZdrowieInfo.Server.Tests.Reports;

public class ReportComposerTests
{
    private static PendingItem P(long id, string source, string url, string title, string? content, int updates = 0, SourceItemKind kind = SourceItemKind.Entry) =>
        new(new SourceItem { Id = id, Key = url, Url = url, Title = title, Content = content, UpdateCount = updates, Kind = kind, LastChangedAt = DateTimeOffset.UnixEpoch.AddMinutes(id) }, source);

    [Fact]
    public void Same_article_from_two_sources_is_one_input_item()
    {
        var inputs = ReportComposer.BuildInputs([
            P(5, "NFZ – Aktualności", "https://www.nfz.gov.pl/a,9057.html", "Wyroby medyczne", "Treść A"),
            P(7, "NFZ – Komunikaty DI", "https://nfz.gov.pl/a,9057.html", "Wyroby medyczne", null),
            P(9, "CSIRT", "https://csirt.cez.gov.pl/x", "Podatność", "Treść B", updates: 1),
        ]);

        Assert.Equal(2, inputs.Count);
        var merged = inputs.Single(i => i.SourceItemIds.Count == 2);
        Assert.Equal("5", merged.Id);
        Assert.Equal(["NFZ – Aktualności", "NFZ – Komunikaty DI"], merged.SourceNames);
        Assert.Equal("Treść A", merged.Content);
        Assert.Equal("aktualizacja", inputs.Single(i => i.Id == "9").Kind);
    }

    [Fact]
    public void Long_content_is_truncated_explicitly()
    {
        var input = Assert.Single(ReportComposer.BuildInputs([P(1, "S", "https://x.pl/1", "T", new string('a', ReportComposer.MaxContentChars + 10))]));
        Assert.True(input.ContentTruncated);
        var message = ReportComposer.BuildUserMessage(new DateOnly(2026, 11, 18), new ClinicProfile(), [input]);
        Assert.Contains("Treść skrócona", message);
    }

    [Fact]
    public void User_message_has_profile_items_and_previous_errors()
    {
        var inputs = ReportComposer.BuildInputs([P(3, "MZ – Komunikaty", "https://gov.pl/k", "Komunikat <ważny>", "Treść")]);
        var profile = new ClinicProfile { Systems = "Fortinet FortiGate 7.4\nmMedica" };
        var message = ReportComposer.BuildUserMessage(new DateOnly(2026, 11, 18), profile, inputs, ["Cytat 1 nie jest dosłowny."]);

        Assert.Contains("Data raportu: 18.11.2026 (środa)", message);
        Assert.Contains("Fortinet FortiGate 7.4", message);
        Assert.Contains("Podmiot kluczowy w KSC: tak", message);
        Assert.Contains("<wpis id=\"3\" typ=\"nowy wpis\" zrodlo=\"MZ – Komunikaty\"", message.Replace("&#x2013;", "–"));
        Assert.Contains("Cytat 1 nie jest dosłowny.", message);
    }

    [Fact]
    public void Schema_lists_polish_importance_and_categories()
    {
        var json = JsonSerializer.Serialize(ReportJson.Schema());
        Assert.Contains("krytyczna", JsonSerializer.Deserialize<string>(JsonSerializer.Serialize("krytyczna")) is var k && json.Contains("krytyczna") ? json : "");
        Assert.Contains("\"additionalProperties\":false", json);
        Assert.Contains("item_id", json);
    }

    [Fact]
    public void Report_json_round_trips_snake_case()
    {
        var doc = new ReportDocument
        {
            Title = "Raport", Summary = "S",
            Findings = [new FindingDto { Importance = "średnia", Category = "CeR", Headline = "H", Body = "B", Citations = [new CitationDto { ItemId = "1", Quote = "Q" }] }],
            Ignored = [new IgnoredDto { ItemId = "2", Reason = "dla aptek" }],
        };
        var json = ReportJson.Serialize(doc);
        Assert.Contains("\"item_id\":\"1\"", json);
        Assert.Contains("średnia", json);
        Assert.Equal("dla aptek", ReportJson.Parse(json).Ignored[0].Reason);
    }
}
