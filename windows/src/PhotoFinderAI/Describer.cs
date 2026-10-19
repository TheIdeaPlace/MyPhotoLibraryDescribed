// The only file that calls Windows' AI API. What it decides about results goes through Protocol,
// which is tested; this is the thin part that can only run on a Copilot+ PC.
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Graphics.Imaging;
using Microsoft.Windows.AI;
using Microsoft.Windows.AI.ContentSafety;
using Microsoft.Windows.AI.Imaging;
using Microsoft.Windows.AI.Text;
using Windows.ApplicationModel;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PhotoFinderAI;

public sealed class Describer : IDisposable
{
    private ImageDescriptionGenerator? _generator;
    private LanguageModel? _language;

    /// <summary>Answers a follow-up question from the photo's description (#24), with Phi Silica. Throws
    /// <see cref="ProtocolException"/> with the code to answer with.</summary>
    public async Task<string> AskAsync(Protocol.AskRequest request)
    {
        try
        {
            if (_language is null)
            {
                try
                {
                    _language = await StartLanguageModelAsync(request);
                }
                catch (UnauthorizedAccessException ex) when (ex.Message.Contains("Limited Access Feature", StringComparison.OrdinalIgnoreCase))
                {
                    // Phi Silica is a Limited Access Feature: an app needs Microsoft's token to use it (#24).
                    var refused = Protocol.ForPhiSilicaAccess(PhiSilicaAccess.Value) ?? Protocol.ForPhiSilicaAccess("Unavailable")!.Value;
                    throw new ProtocolException(request.Id, refused.Code, refused.Message);
                }
            }
            using var context = _language.CreateContext(Protocol.AskSystemPrompt);
            var result = await _language.GenerateResponseAsync(context, Protocol.AskPrompt(request), new LanguageModelOptions());
            var status = result.Status.ToString();
            if (Protocol.CodeForAnswerStatus(status) is { } code) throw new ProtocolException(request.Id, code, Protocol.MessageForAnswerStatus(status));
            return result.Text.Trim();
        }
        catch (Exception ex) when (ex is not ProtocolException)
        {
            // Start the text model afresh next time, but keep the image model: a question going wrong says nothing
            // about describing, and reloading it would slow the next description down.
            ResetLanguageModel();
            throw new ProtocolException(request.Id, Codes.InternalError,
                $"Windows couldn't answer" + (string.IsNullOrWhiteSpace(ex.Message) ? "." : $": {ex.Message.Trim()}") + $" (0x{ex.HResult:X8})");
        }
    }

    /// <summary>Unlocks Phi Silica, a Limited Access Feature, with Microsoft's token for this app (#24), once per run.
    /// Windows checks the token against the package family, TheIdeaPlace.MyPhotoLibraryDescribed_mzky7tc8qkasw, so only
    /// a build signed as Kelly Ford gets it. The token isn't in the source: the build takes it from the PhiSilicaToken
    /// property or environment variable (the release workflow's PHI_SILICA_TOKEN secret). A build without one still
    /// asks, with no token, so a Windows that doesn't need one says AvailableWithoutToken; refused, it says NoToken.
    /// Otherwise the status, as LimitedAccessFeatureStatus names it.</summary>
    private static readonly Lazy<string> PhiSilicaAccess = new(() =>
    {
        var token = Protocol.BuiltInToken(typeof(Describer).Assembly);
        var status = StateOf(() =>
            LimitedAccessFeatures.TryUnlockFeature(Protocol.PhiSilicaFeature, token ?? "", Protocol.PhiSilicaAttestation).Status.ToString());
        return Protocol.AccessStatus(status, hasToken: token is not null);
    });

    /// <summary>Windows getting Phi Silica ready (downloading it, the first time), started by a question and left
    /// running, so a long download doesn't run into the app's time limit for an answer.</summary>
    private static Task<AIFeatureReadyResult>? _preparingLanguage;

    private static async Task<LanguageModel> StartLanguageModelAsync(Protocol.AskRequest request)
    {
        if (Protocol.ForPhiSilicaAccess(PhiSilicaAccess.Value) is { } refused) throw new ProtocolException(request.Id, refused.Code, refused.Message);
        var state = LanguageModel.GetReadyState();
        if (Protocol.ForTextReadyState(state.ToString()) is { } unavailable) throw new ProtocolException(request.Id, unavailable.Code, unavailable.Message);
        if (state != AIFeatureReadyState.Ready)
        {
            _preparingLanguage ??= LanguageModel.EnsureReadyAsync().AsTask();
            // A minute's wait covers a model that's already there; a download carries on after this answer.
            await Task.WhenAny(_preparingLanguage, Task.Delay(TimeSpan.FromMinutes(1)));
            if (!_preparingLanguage.IsCompleted)
                throw new ProtocolException(request.Id, Codes.NotReady,
                    "Windows is getting its text model ready, which the first time can take a few minutes. Ask again shortly.");
            var ready = await _preparingLanguage;
            _preparingLanguage = null;
            if (ready.Status != AIFeatureReadyResultState.Success)
                throw new ProtocolException(request.Id, Codes.NotReady,
                    "Windows couldn't get its text model ready" + (ready.ExtendedError is { } e ? $": {e.Message.Trim()}" : ". It may still be downloading; try again in a few minutes."));
        }
        return await LanguageModel.CreateAsync();
    }

    private void ResetLanguageModel()
    {
        try { _language?.Dispose(); } catch { /* it may already be gone */ }
        _language = null;
    }

    public static string ReadyState() => ImageDescriptionGenerator.GetReadyState().ToString();

    /// <summary>The ready state of Windows' other AI features, for My Photo Library Described's Check System: text recognition, and
    /// Phi Silica (the language model, which Windows lets an app use only with Microsoft's permission). A feature that
    /// can't be asked says why, briefly.</summary>
    public static Dictionary<string, string> OtherStates() => new()
    {
        ["textRecognition"] = StateOf(() => TextRecognizer.GetReadyState().ToString()),
        ["languageModelAccess"] = PhiSilicaAccess.Value,
        ["languageModel"] = StateOf(() => LanguageModel.GetReadyState().ToString()),
    };

    private static string StateOf(Func<string> read)
    {
        try { return read(); }
        catch (Exception ex) { return $"Unavailable ({ex.GetType().Name}, 0x{ex.HResult:X8})"; }
    }

    /// <summary>Makes the model ready, downloading it the first time if Windows needs to.
    /// Returns null when ready, or the failure code and message.</summary>
    public static async Task<(string Code, string Message)?> PrepareAsync()
    {
        var state = ImageDescriptionGenerator.GetReadyState();
        if (Protocol.ForReadyState(state.ToString()) is { } unavailable) return unavailable;
        if (state == AIFeatureReadyState.Ready) return null;
        var result = await ImageDescriptionGenerator.EnsureReadyAsync();
        if (result.Status == AIFeatureReadyResultState.Success) return null;
        return (Codes.NotReady,
            "Windows couldn't get its image description model ready" +
            (result.ExtendedError is { } e ? $": {e.Message.Trim()} (0x{e.HResult:X8})." : ". It may still be downloading; try again in a few minutes."));
    }

    /// <summary>Describes one picture. Returns the description, or throws
    /// <see cref="ProtocolException"/> with the code to answer with. Anything else it throws is
    /// unexpected; the caller should then <see cref="Reset"/>.</summary>
    public async Task<string> DescribeAsync(Protocol.DescribeRequest request)
    {
        if (_generator is null)
        {
            if (await PrepareAsync() is { } notReady) throw new ProtocolException(request.Id, notReady.Code, notReady.Message);
            _generator = await ImageDescriptionGenerator.CreateAsync();
        }

        SoftwareBitmap bitmap;
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(request.Image.AsBuffer());
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var (width, height) = Protocol.ScaledSize(decoder.PixelWidth, decoder.PixelHeight);
            var transform = new BitmapTransform
            {
                ScaledWidth = width,
                ScaledHeight = height,
                InterpolationMode = BitmapInterpolationMode.Fant,
            };
            bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        }
        catch (Exception ex)
        {
            // The decoder's own messages are often empty; say what failed.
            throw new ProtocolException(request.Id, Codes.DecodeFailed,
                $"The picture couldn't be read as {request.Mime}" + (string.IsNullOrWhiteSpace(ex.Message) ? "." : $": {ex.Message.Trim()}"));
        }

        using (bitmap)
        using (var image = ImageBuffer.CreateForSoftwareBitmap(bitmap))
        {
            var kind = request.Kind switch
            {
                "detailed" => ImageDescriptionKind.DetailedDescription,
                "brief" => ImageDescriptionKind.BriefDescription,
                "diagram" => ImageDescriptionKind.DiagramDescription,
                _ => ImageDescriptionKind.AccessibleDescription,
            };
            var result = await _generator.DescribeAsync(image, kind, new ContentFilterOptions());
            var status = result.Status.ToString();
            if (Protocol.CodeForStatus(status) is { } code) throw new ProtocolException(request.Id, code, Protocol.MessageForStatus(status));
            return result.Description;
        }
    }

    /// <summary>Drops the model after an unexpected failure, so the next request starts it
    /// afresh rather than reusing one that may be broken (the NPU reset, say).</summary>
    public void Reset()
    {
        try { _generator?.Dispose(); } catch { /* it may already be gone */ }
        _generator = null;
    }

    public void Dispose()
    {
        Reset();
        ResetLanguageModel();
    }
}
