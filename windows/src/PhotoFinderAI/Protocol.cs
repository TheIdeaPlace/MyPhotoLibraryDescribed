// The helper's protocol with My Photo Library Described. Plain C#, no Windows Runtime, so it is unit tested on any
// machine (windows/tests/PhotoFinderAI.Tests), including CI runners with no NPU.
//
// Serve mode reads one JSON request per line on stdin and writes one JSON response per line on
// stdout, in order. Requests:
//   {"id": 7, "kind": "accessible", "mime": "image/jpeg", "image": "<base64>"}
// A follow-up question about a photo (#24), answered by Phi Silica from its description, not the picture:
//   {"id": 8, "op": "ask", "description": "...", "text": "text read from it", "question": "...",
//    "history": [{"question": "...", "answer": "..."}]}
// Responses:
//   {"id": 7, "ok": true,  "kind": "accessible", "text": "...", "seconds": 2.41}
//   {"id": 8, "ok": true,  "kind": "answer", "text": "...", "seconds": 1.10}
//   {"id": 7, "ok": false, "code": "content_filtered", "message": "..."}
// The codes are the contract; My Photo Library Described (Services/WindowsAI.cs) decides what each one means
// for the person. Bump Version if a field's meaning changes.
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PhotoFinderAI;

public static class Protocol
{
    public const int Version = 2;

    /// <summary>The description kinds, in order: the first is the default.</summary>
    public static readonly string[] Kinds = ["accessible", "detailed", "brief", "diagram"];

    /// <summary>What the image decoder is asked to read. My Photo Library Described converts everything to JPEG
    /// before it gets here.</summary>
    public static readonly string[] SupportedMimeTypes = ["image/jpeg", "image/png", "image/bmp", "image/gif", "image/tiff"];

    /// <summary>Far larger than any picture My Photo Library Described sends (a JPEG at most 2048 pixels on a side), and small enough
    /// that the copies a request passes through on the way in stay a few hundred MB at most.</summary>
    public const int MaxImageBytes = 20 * 1024 * 1024;

    /// <summary>The longest base64 a picture within the limit can take.</summary>
    public const int MaxImageBase64Chars = (MaxImageBytes + 2) / 3 * 4;

    /// <summary>The longest request line read: the picture plus room for the other fields. A longer
    /// line is refused as it is read, so it is never held in memory whole.</summary>
    public const int MaxLineChars = MaxImageBase64Chars + 4096;

    /// <summary>The longest side a picture is decoded at. The model works on far less; this keeps
    /// a huge photo from costing gigabytes of memory.</summary>
    public const uint MaxSide = 4096;

    /// <summary>Apostrophes and accents written as themselves, not as escapes: the output is
    /// valid JSON either way, and people read --check's.</summary>
    private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public sealed record DescribeRequest(int Id, string Kind, string Mime, byte[] Image);

    /// <summary>A follow-up question about a photo, with what's known about it and the questions asked so far.</summary>
    public sealed record AskRequest(int Id, string Description, string Text, string Question, IReadOnlyList<(string Question, string Answer)> History);

    /// <summary>Limits on a question's parts, far beyond a real one, so a request can't be made huge.</summary>
    public const int MaxDescriptionChars = 20_000;
    public const int MaxQuestionChars = 2_000;
    public const int MaxHistory = 20;

    /// <summary>A request line: a description (<see cref="DescribeRequest"/>) or, with "op": "ask", a follow-up question
    /// (<see cref="AskRequest"/>).</summary>
    public static object ParseAnyRequest(string line)
    {
        var (request, id) = ParseJson(line);
        if (request["op"] is { } op && op.GetValueKind() == JsonValueKind.String && op.GetValue<string>() == "ask")
            return AskFrom(request, id);
        return DescribeFrom(request, id);
    }

    /// <summary>Reads one request line. Throws <see cref="ProtocolException"/> with the code to
    /// answer with, and the request's id when it could be read.</summary>
    public static DescribeRequest ParseRequest(string line)
    {
        var (request, id) = ParseJson(line);
        return DescribeFrom(request, id);
    }

    private static (JsonObject Request, int Id) ParseJson(string line)
    {
        JsonObject request;
        try
        {
            request = JsonNode.Parse(line) as JsonObject ?? throw new JsonException("it isn't an object");
            // A repeated key surfaces only when the object is first read, as ArgumentException.
            _ = request.Count;
            _ = request["id"];
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new ProtocolException(0, Codes.BadRequest, $"The request isn't valid JSON: {ex.Message}");
        }

        var id = 0;
        if (request["id"] is { } idNode)
        {
            if (idNode is not JsonValue idValue || !idValue.TryGetValue(out int parsedId))
                throw new ProtocolException(0, Codes.BadRequest, "The request's id must be a whole number.");
            id = parsedId;
        }
        return (request, id);
    }

    private static string? StringField(JsonObject request, int id, string name)
    {
        var node = request[name];
        if (node is null) return null;
        if (node.GetValueKind() != JsonValueKind.String)
            throw new ProtocolException(id, Codes.BadRequest, $"The request's {name} must be text.");
        return node.GetValue<string>();
    }

    private static DescribeRequest DescribeFrom(JsonObject request, int id)
    {
        string? StringField(string name) => Protocol.StringField(request, id, name);
        var kind = StringField("kind");
        var mime = StringField("mime");
        var image = StringField("image") ?? throw new ProtocolException(id, Codes.BadRequest, "The request has no picture.");
        if (image.Length > MaxImageBase64Chars)
            throw TooLarge(id, (long)image.Length * 3 / 4);
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(image);
        }
        catch (FormatException)
        {
            throw new ProtocolException(id, Codes.BadRequest, "The picture isn't valid base64.");
        }
        return Validate(id, kind, mime, bytes);
    }

    private static AskRequest AskFrom(JsonObject request, int id)
    {
        var description = (StringField(request, id, "description") ?? "").Trim();
        var text = (StringField(request, id, "text") ?? "").Trim();
        var question = (StringField(request, id, "question") ?? "").Trim();
        if (description.Length == 0) throw new ProtocolException(id, Codes.BadRequest, "There's no description to answer from.");
        if (question.Length == 0) throw new ProtocolException(id, Codes.BadRequest, "There's no question.");
        if (description.Length > MaxDescriptionChars || text.Length > MaxDescriptionChars || question.Length > MaxQuestionChars)
            throw new ProtocolException(id, Codes.TooLarge, "The question is too long.");
        var history = new List<(string, string)>();
        if (request["history"] is { } node)
        {
            if (node is not JsonArray items) throw new ProtocolException(id, Codes.BadRequest, "The request's history must be a list.");
            foreach (var item in items.TakeLast(MaxHistory))
            {
                if (item is not JsonObject o) throw new ProtocolException(id, Codes.BadRequest, "Each history item must be a question and answer.");
                var q = (StringField(o, id, "question") ?? "").Trim();
                var a = (StringField(o, id, "answer") ?? "").Trim();
                if (q.Length > MaxQuestionChars || a.Length > MaxDescriptionChars) throw new ProtocolException(id, Codes.TooLarge, "The question is too long.");
                if (q.Length > 0 && a.Length > 0) history.Add((q, a));
            }
        }
        return new AskRequest(id, description, text, question, history);
    }

    /// <summary>What Phi Silica is told: it can't see the photo, only what's known about it, and must say so rather than
    /// guess. Kept here, in plain C#, so it's tested.</summary>
    public const string AskSystemPrompt =
        "You answer questions about a photo for someone who can't see it. You can't see the photo either: you have only " +
        "its description and any text read from it. Answer from those alone, briefly and plainly. If they don't say, " +
        "say that the description doesn't say. Never invent details.";

    public static string AskPrompt(AskRequest request)
    {
        var prompt = new System.Text.StringBuilder();
        prompt.Append("Description of the photo:\n").Append(request.Description).Append("\n\n");
        if (request.Text.Length > 0) prompt.Append("Text read from the photo:\n").Append(request.Text).Append("\n\n");
        foreach (var (q, a) in request.History) prompt.Append("Question: ").Append(q).Append("\nAnswer: ").Append(a).Append("\n\n");
        prompt.Append("Question: ").Append(request.Question).Append("\nAnswer:");
        return prompt.ToString();
    }

    /// <summary>The code for a finished answer's status, by the status's name in
    /// Microsoft.Windows.AI.Text.LanguageModelResponseStatus. Null means it succeeded.</summary>
    public static string? CodeForAnswerStatus(string status) => status switch
    {
        "Complete" => null,
        "PromptBlockedByContentModeration" or "ResponseBlockedByContentModeration" => Codes.ContentFiltered,
        "PromptLargerThanContext" => Codes.TooLarge,
        "BlockedByPolicy" => Codes.BlockedByPolicy,
        _ => Codes.InternalError,
    };

    public static string MessageForAnswerStatus(string status) => status switch
    {
        "PromptBlockedByContentModeration" => "Windows' content filter declined the question.",
        "ResponseBlockedByContentModeration" => "Windows' content filter declined the answer it wrote.",
        "PromptLargerThanContext" => "The description and questions are too long for Windows' text model. Close Get Info and ask again.",
        "BlockedByPolicy" => "A policy on this PC blocks Windows' text model.",
        _ => $"Windows couldn't answer ({status}).",
    };

    /// <summary>The code and message for Phi Silica not being usable, by its ready state's name.</summary>
    public static (string Code, string Message)? ForTextReadyState(string state) => state switch
    {
        "Ready" or "NotReady" => null,
        "NotSupportedOnCurrentSystem" => (Codes.NotSupported,
            "Follow-up questions need Windows' text model (Phi Silica), on a Copilot+ PC with a recent version of Windows 11."),
        "DisabledByUser" => (Codes.DisabledByUser,
            "Windows' AI features are turned off on this PC. They can be turned on in Settings, Privacy & security."),
        _ => (Codes.NotSupported, $"Windows' text model isn't available on this PC ({state})."),
    };

    /// <summary>Phi Silica's Limited Access Feature, and the attestation Microsoft gave with this app's token (#24). Neither
    /// is secret; the token is, and comes from the build (<see cref="BuiltInToken"/>).</summary>
    public const string PhiSilicaFeature = "com.microsoft.windows.ai.languagemodel";
    public const string PhiSilicaAttestation =
        "mzky7tc8qkasw has registered their use of com.microsoft.windows.ai.languagemodel with Microsoft and agrees to the terms of use.";

    /// <summary>The access status reported for a build made without the token.</summary>
    public const string NoToken = "NoToken";

    /// <summary>The Phi Silica token the build put in the assembly's metadata, or null if it was built without one.</summary>
    public static string? BuiltInToken(System.Reflection.Assembly assembly) =>
        assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "PhiSilicaToken")?.Value is { Length: > 0 } token ? token.Trim() : null;

    /// <summary>Whether Windows granted Phi Silica, by the LimitedAccessFeatureStatus name. AvailableWithoutToken is a
    /// Windows that no longer asks.</summary>
    public static bool PhiSilicaGranted(string access) => access is "Available" or "AvailableWithoutToken";

    /// <summary>The code and message for Phi Silica not being granted, or null if it was.</summary>
    public static (string Code, string Message)? ForPhiSilicaAccess(string access) => PhiSilicaGranted(access) ? null : access switch
    {
        NoToken => (Codes.NotSupported,
            "This copy was built without Microsoft's permission to use Windows' text model, so it can't answer follow-up questions."),
        _ => (Codes.NotSupported, $"Windows didn't let this app use its text model ({access})."),
    };

    /// <summary>Checks a request's parts, wherever they came from, and normalises the kind and type.</summary>
    public static DescribeRequest Validate(int id, string? kind, string? mime, byte[] image)
    {
        var k = (kind ?? Kinds[0]).Trim().ToLowerInvariant();
        if (!Kinds.Contains(k))
            throw new ProtocolException(id, Codes.BadRequest, $"There's no description kind '{kind}'. The kinds are {string.Join(", ", Kinds)}.");

        var m = (mime ?? "").Trim().ToLowerInvariant();
        if (m == "image/jpg") m = "image/jpeg";
        if (!SupportedMimeTypes.Contains(m))
            throw new ProtocolException(id, Codes.UnsupportedFormat,
                m.Length == 0 ? "The request didn't say what type of picture it is." : $"Pictures of type {m} can't be described. Use JPEG, PNG, BMP, GIF or TIFF.");

        if (image.Length == 0) throw new ProtocolException(id, Codes.BadRequest, "The picture is empty.");
        if (image.Length > MaxImageBytes) throw TooLarge(id, image.Length);
        return new DescribeRequest(id, k, m, image);
    }

    private static ProtocolException TooLarge(int id, long bytes) =>
        new(id, Codes.TooLarge, $"The picture is more than {MaxImageBytes / (1024 * 1024)} MB, the most the helper takes.");

    /// <summary>The size a picture is decoded at: as it is, or scaled down to fit
    /// <see cref="MaxSide"/> on its longer side, keeping its shape.</summary>
    public static (uint Width, uint Height) ScaledSize(uint width, uint height)
    {
        var longer = Math.Max(width, height);
        if (longer <= MaxSide || longer == 0) return (Math.Max(width, 1u), Math.Max(height, 1u));
        var scale = (double)MaxSide / longer;
        return ((uint)Math.Max(1, Math.Round(width * scale)), (uint)Math.Max(1, Math.Round(height * scale)));
    }

    /// <summary>The answer line for a description.</summary>
    public static string Success(int id, string kind, string text, double seconds) =>
        new JsonObject { ["id"] = id, ["ok"] = true, ["kind"] = kind, ["text"] = text, ["seconds"] = Math.Round(seconds, 2) }.ToJsonString(Readable);

    /// <summary>The answer line for a follow-up question.</summary>
    public static string Answer(int id, string text, double seconds) =>
        new JsonObject { ["id"] = id, ["ok"] = true, ["kind"] = "answer", ["text"] = text, ["seconds"] = Math.Round(seconds, 2) }.ToJsonString(Readable);

    public static string Failure(int id, string code, string message) =>
        new JsonObject { ["id"] = id, ["ok"] = false, ["code"] = code, ["message"] = message }.ToJsonString(Readable);

    /// <summary>The readiness report for --check and --prepare.</summary>
    /// <param name="components">The ready state of Windows' other AI features ("textRecognition", "languageModel"), for
    /// My Photo Library Described's Check System.</param>
    public static string Readiness(bool identity, string state, string? message = null, IReadOnlyDictionary<string, string>? components = null)
    {
        var o = new JsonObject
        {
            ["protocol"] = Version,
            ["version"] = typeof(Protocol).Assembly.GetName().Version?.ToString(3) ?? "",
            ["identity"] = identity,
            ["state"] = state,
            ["kinds"] = new JsonArray(Kinds.Select(k => (JsonNode)k).ToArray()),
        };
        if (message is not null) o["message"] = message;
        if (components is { Count: > 0 })
        {
            var parts = new JsonObject();
            foreach (var (name, value) in components) parts[name] = value;
            o["components"] = parts;
        }
        return o.ToJsonString(Readable);
    }

    /// <summary>The code for a finished description's status, by the status's name in
    /// Microsoft.Windows.AI.Imaging.ImageDescriptionResultStatus. Null means it succeeded.</summary>
    public static string? CodeForStatus(string status) => status switch
    {
        "Complete" => null,
        "ImageBlockedByContentModeration" or "TextInImageBlockedByContentModeration"
            or "DescriptionTextBlockedByContentModeration" => Codes.ContentFiltered,
        "ImageHasTooMuchText" => Codes.TooMuchText,
        "BlockedByPolicy" => Codes.BlockedByPolicy,
        _ => Codes.InternalError, // InternalError, and InProgress, which a finished call shouldn't report
    };

    /// <summary>What to tell the user for a status that isn't Complete.</summary>
    public static string MessageForStatus(string status) => status switch
    {
        "ImageBlockedByContentModeration" => "Windows' content filter declined to describe this picture.",
        "TextInImageBlockedByContentModeration" => "Windows' content filter declined the text in this picture.",
        "DescriptionTextBlockedByContentModeration" => "Windows' content filter declined the description it wrote for this picture.",
        "ImageHasTooMuchText" => "This picture has too much text for Windows to describe. Try OCR instead.",
        "BlockedByPolicy" => "A policy on this PC blocks Windows' image description.",
        _ => $"Windows couldn't describe this picture ({status}).",
    };

    /// <summary>The code and message for a model that isn't ready to use, by the state's name in
    /// Microsoft.Windows.AI.AIFeatureReadyState. Null when it is Ready or can be made ready.</summary>
    public static (string Code, string Message)? ForReadyState(string state) => state switch
    {
        "Ready" or "NotReady" => null,
        "NotSupportedOnCurrentSystem" => (Codes.NotSupported,
            "Windows' image description needs a Copilot+ PC, with an NPU, and a recent version of Windows 11."),
        "DisabledByUser" => (Codes.DisabledByUser,
            "Windows' AI features are turned off on this PC. They can be turned on in Settings, Privacy & security."),
        _ => (Codes.NotSupported, $"Windows' image description isn't available on this PC ({state})."),
    };
}

/// <summary>The failure codes. My Photo Library Described (Services/WindowsAI.cs) decides what each means for the person.</summary>
public static class Codes
{
    public const string ContentFiltered = "content_filtered";
    public const string TooMuchText = "too_much_text";
    public const string BlockedByPolicy = "blocked_by_policy";
    public const string NotSupported = "not_supported";
    public const string DisabledByUser = "disabled_by_user";
    public const string NotReady = "not_ready";
    public const string InternalError = "internal_error";
    public const string UnsupportedFormat = "unsupported_format";
    public const string TooLarge = "too_large";
    public const string DecodeFailed = "decode_failed";
    public const string BadRequest = "bad_request";
}

public sealed class ProtocolException(int id, string code, string message) : Exception(message)
{
    public int Id { get; } = id;
    public string Code { get; } = code;
}
