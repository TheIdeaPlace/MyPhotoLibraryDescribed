using System.Text.Json.Nodes;
using PhotoFinderAI;
using Xunit;

namespace PhotoFinderAI.Tests;

/// <summary>Follow-up questions (#24): the "ask" request, the prompt Phi Silica gets, and how its results are read.</summary>
public class AskTests
{
    private static string Ask(int id, string description = "A dog on a beach.", string question = "What colour is the dog?",
        string? text = null, JsonArray? history = null)
    {
        var o = new JsonObject { ["id"] = id, ["op"] = "ask", ["description"] = description, ["question"] = question };
        if (text is not null) o["text"] = text;
        if (history is not null) o["history"] = history;
        return o.ToJsonString();
    }

    [Fact]
    public void AnAskRequest_IsRead_WithItsHistory()
    {
        var history = new JsonArray(new JsonObject { ["question"] = "Is it sunny?", ["answer"] = "The description doesn't say." });
        var request = Assert.IsType<Protocol.AskRequest>(Protocol.ParseAnyRequest(Ask(5, text: "BEACH CLOSED", history: history)));
        Assert.Equal(5, request.Id);
        Assert.Equal("A dog on a beach.", request.Description);
        Assert.Equal("BEACH CLOSED", request.Text);
        Assert.Equal("What colour is the dog?", request.Question);
        Assert.Equal([("Is it sunny?", "The description doesn't say.")], request.History);
        // Without "op", it's a description request as before.
        Assert.IsType<Protocol.DescribeRequest>(Protocol.ParseAnyRequest(
            new JsonObject { ["id"] = 1, ["mime"] = "image/jpeg", ["image"] = Convert.ToBase64String([0xFF, 0xD8, 0xFF]) }.ToJsonString()));
    }

    [Theory]
    [InlineData("", "Why?", "bad_request")]
    [InlineData("A dog.", "  ", "bad_request")]
    public void AnAskWithoutADescriptionOrQuestion_IsRefused(string description, string question, string code)
    {
        var ex = Assert.Throws<ProtocolException>(() => Protocol.ParseAnyRequest(Ask(9, description, question)));
        Assert.Equal((9, code), (ex.Id, ex.Code));
    }

    [Fact]
    public void AHugeQuestion_IsRefused_AndOnlyTheLastTwentyExchangesAreKept()
    {
        var ex = Assert.Throws<ProtocolException>(() => Protocol.ParseAnyRequest(Ask(3, question: new string('x', Protocol.MaxQuestionChars + 1))));
        Assert.Equal("too_large", ex.Code);
        var history = new JsonArray(Enumerable.Range(0, 30).Select(i => (JsonNode)new JsonObject { ["question"] = $"q{i}", ["answer"] = $"a{i}" }).ToArray());
        var request = (Protocol.AskRequest)Protocol.ParseAnyRequest(Ask(4, history: history));
        Assert.Equal(Protocol.MaxHistory, request.History.Count);
        Assert.Equal("q29", request.History[^1].Question);
    }

    [Fact]
    public void ThePrompt_HasTheDescriptionTextHistoryAndQuestion_AndTheModelIsToldItCantSeeThePhoto()
    {
        var request = new Protocol.AskRequest(1, "A dog on a beach.", "BEACH CLOSED", "What does the sign say?", [("Is it sunny?", "It doesn't say.")]);
        var prompt = Protocol.AskPrompt(request);
        Assert.Contains("A dog on a beach.", prompt);
        Assert.Contains("Text read from the photo:\nBEACH CLOSED", prompt);
        Assert.Contains("Question: Is it sunny?\nAnswer: It doesn't say.", prompt);
        Assert.EndsWith("Question: What does the sign say?\nAnswer:", prompt);
        Assert.Contains("can't see the photo", Protocol.AskSystemPrompt);
        Assert.Contains("Never invent", Protocol.AskSystemPrompt);
        Assert.DoesNotContain("Text read", Protocol.AskPrompt(request with { Text = "" }));
    }

    [Theory]
    [InlineData("Complete", null)]
    [InlineData("PromptBlockedByContentModeration", "content_filtered")]
    [InlineData("ResponseBlockedByContentModeration", "content_filtered")]
    [InlineData("PromptLargerThanContext", "too_large")]
    [InlineData("BlockedByPolicy", "blocked_by_policy")]
    [InlineData("Error", "internal_error")]
    public void AnAnswersStatus_HasItsCode(string status, string? code) => Assert.Equal(code, Protocol.CodeForAnswerStatus(status));

    [Fact]
    public async Task TheServer_AnswersQuestions_AndDescriptions_InOrder()
    {
        var describe = new JsonObject { ["id"] = 1, ["mime"] = "image/jpeg", ["image"] = Convert.ToBase64String([0xFF, 0xD8, 0xFF]) }.ToJsonString();
        var output = new StringWriter();
        await Server.RunAsync(new StringReader($"{describe}\n{Ask(2)}\n"), output,
            r => Task.FromResult("described"), null, q => Task.FromResult($"answer to {q.Question}"));
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
        Assert.Equal(("described", "accessible"), ((string)lines[0]["text"]!, (string)lines[0]["kind"]!));
        Assert.Equal(("answer to What colour is the dog?", "answer"), ((string)lines[1]["text"]!, (string)lines[1]["kind"]!));

        // A helper without answering (an older caller's setup) says so rather than guessing.
        var old = new StringWriter();
        await Server.RunAsync(new StringReader($"{Ask(3)}\n"), old, r => Task.FromResult("x"));
        Assert.Equal("not_supported", (string)JsonNode.Parse(old.ToString().Trim())!["code"]!);
    }
}
