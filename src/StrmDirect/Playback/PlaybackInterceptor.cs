using System.Net;
using System.Reflection;
using HarmonyLib;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.Services;
using StrmDirect.Configuration;
using StrmDirect.Interop;
using StrmDirect.Networking;

namespace StrmDirect.Playback;

internal sealed class PlaybackInterceptor(ILibraryManager library, IHttpClient http, ILogger logger) : IDisposable
{
    private const string PatchId = "StrmDirect.ff9f1516.playback";
    private static volatile PlaybackInterceptor? _current;
    private readonly Harmony _harmony = new(PatchId);
    private readonly RedirectResolver _resolver = new(http);
    private EmbyStreaming? _api;
    private MethodInfo? _negotiation;
    private volatile Policy? _policy;

    private sealed record Policy(bool FollowRedirects, bool DisableTranscoding);

    public bool Enabled => _policy != null;
    public bool TranscodingDisabled => _policy?.DisableTranscoding == true;

    // Prepare hooks before persisting an enabled configuration; publish the policy only after saving.
    public bool Prepare(PluginOptions options)
    {
        if (!options.Enabled)
            return true;

        try
        {
            InstallRedirect();
            if (options.DisableTranscoding)
                InstallTranscodingPolicy();
            return true;
        }
        catch (Exception error)
        {
            if (_policy == null)
                Dispose();
            logger.Error("StrmDirect configuration not applied ({0}).", error.GetType().Name);
            return false;
        }
    }

    public void Apply(PluginOptions options)
    {
        if (!options.Enabled)
            Dispose();
        else if (Prepare(options))
            _policy = new Policy(options.FollowRedirects, options.DisableTranscoding);
    }

    private void InstallRedirect()
    {
        if (_api != null)
            return;

        var api = new EmbyStreaming();
        try { AddPrefix(api.ProgressiveRequest, nameof(InterceptProgressive)); }
        catch
        {
            RemovePrefix(api.ProgressiveRequest);
            throw;
        }
        _api = api;
        _current = this;
    }

    private void InstallTranscodingPolicy()
    {
        if (_negotiation != null)
            return;

        var api = _api!;
        var negotiation = api.FindPlaybackInfo();
        try
        {
            AddPrefix(negotiation, nameof(ConfigurePlaybackRequest));
            AddPrefix(api.StateRequest, nameof(RejectConversion));
            _negotiation = negotiation;
        }
        catch
        {
            RemovePrefix(negotiation);
            RemovePrefix(api.StateRequest);
            throw;
        }
    }

    private void AddPrefix(MethodInfo target, string name)
    {
        if (Harmony.GetPatchInfo(target)?.Prefixes.Any(patch => patch.owner == PatchId && patch.PatchMethod.Name == name) != true)
            _harmony.Patch(target, prefix: new HarmonyMethod(typeof(PlaybackInterceptor), name));
    }

    private void RemovePrefix(MethodInfo? target)
    {
        if (target == null)
            return;
        try
        {
            _harmony.Unpatch(target, HarmonyPatchType.Prefix, PatchId);
        }
        catch (Exception error)
        {
            logger.Warn("StrmDirect could not remove a patch ({0}).", error.GetType().Name);
        }
    }

    public void Dispose()
    {
        _policy = null;
        if (ReferenceEquals(_current, this))
            _current = null;

        RemovePrefix(_negotiation);
        RemovePrefix(_api?.StateRequest);
        RemovePrefix(_api?.ProgressiveRequest);
        _negotiation = null;
        _api = null;
    }

    private bool IsStrm(object request)
    {
        try
        {
            var id = EmbyStreaming.Read<string>(request, "Id");
            if (string.IsNullOrEmpty(id))
                return false;

            var item = library.GetItemById(id);
            return (item?.Path ?? item?.FileName)?.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (Exception error)
        {
            logger.Warn("StrmDirect media lookup failed ({0}).", error.GetType().Name);
            return false;
        }
    }

    private static bool InterceptProgressive(object __instance, object __0, ref Task<object> __result)
    {
        var instance = _current;
        var policy = instance?._policy;
        var api = instance?._api;
        if (instance == null || policy == null || api == null)
            return true;

        var redirect = instance.CreateRedirect(__instance, __0, api, policy);
        if (redirect == null)
            return true;

        __result = Task.FromResult(redirect);
        return false;
    }

    private object? CreateRedirect(object service, object request, EmbyStreaming api, Policy policy)
    {
        object? state = null;
        try
        {
            if (!IsStrm(request))
                return null;

            var filename = EmbyStreaming.Read<string>(request, "StreamFileName");
            var original = string.Equals(Path.GetFileNameWithoutExtension(filename), "original", StringComparison.OrdinalIgnoreCase);
            if (!original && !EmbyStreaming.Read<bool>(request, "Static"))
                return null;

            var context = EmbyStreaming.Read<IRequest>(service, "Request");
            var factory = EmbyStreaming.Read<IHttpResultFactory>(service, "ResultFactory");
            if (context == null || factory == null)
                return null;
            context.CancellationToken.ThrowIfCancellationRequested();

            // Leave the real request untouched if native playback is needed.
            var probe = api.Copy(request);
            EmbyStreaming.Write(probe, "Static", true);
            var extension = Path.GetExtension(filename)?.TrimStart('.');
            if (string.IsNullOrEmpty(EmbyStreaming.Read<string>(probe, "Container")) && !string.IsNullOrEmpty(extension))
                EmbyStreaming.Write(probe, "Container", extension.ToLowerInvariant());

            // Request the original static streaming state.
            state = api.OpenState(service, probe, true, context.CancellationToken);
            if (state == null || EmbyStreaming.Read<object>(state, "LiveStream") != null)
                return null;

            var source = EmbyStreaming.Read<MediaSourceInfo>(state, "MediaSource");
            if (!IsDirectSource(source) || !RedirectResolver.TryNormalize(source!.Path, out var url))
                return null;

            var location = _resolver.ResolveAsync(url, context, policy.FollowRedirects).GetAwaiter().GetResult();
            if (!ReferenceEquals(policy, _policy) || context.CancellationToken.IsCancellationRequested)
                return null;

            return factory.GetRedirectResult(location);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception error)
        {
            // Exception messages may include signed URLs. Log only the type.
            logger.Warn("StrmDirect using native playback ({0}).", error.GetType().Name);
            return null;
        }
        finally
        {
            if (state != null)
            {
                try { api.ReleaseState(state); }
                catch (Exception error) { logger.Warn("StrmDirect state cleanup failed ({0}).", error.GetType().Name); }
            }
        }
    }

    private static bool IsDirectSource(MediaSourceInfo? source) => source is
    {
        IsRemote: true,
        Protocol: MediaProtocol.Http,
        SupportsDirectPlay: true,
        IsInfiniteStream: false,
        RequiresOpening: false,
        RequiresClosing: false,
        RequiredHttpHeaders: null or { Count: 0 }
    };

    private static void ConfigurePlaybackRequest(object[] __args)
    {
        var instance = _current;
        var api = instance?._api;
        if (instance?.TranscodingDisabled != true || api == null || !instance.IsStrm(__args[0]))
            return;

        try
        {
            var request = api.Copy(__args[0]);
            EmbyStreaming.Write(request, "EnableTranscoding", false);
            EmbyStreaming.Write(request, "EnableDirectStream", false);
            __args[0] = request;
        }
        catch (Exception error)
        {
            instance.LogPolicyError(error);
            throw new HttpException("STRM playback policy could not be applied.") { StatusCode = HttpStatusCode.ServiceUnavailable };
        }
    }

    private void LogPolicyError(Exception error) => logger.Warn("StrmDirect playback policy failed ({0}).", error.GetType().Name);

    private static void RejectConversion(object __0)
    {
        var instance = _current;
        if (instance?.TranscodingDisabled != true || EmbyStreaming.Read<bool>(__0, "Static") || !instance.IsStrm(__0))
            return;

        throw new HttpException("STRM transcoding and remuxing are disabled.") { StatusCode = HttpStatusCode.Forbidden };
    }
}
