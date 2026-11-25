using MediaBrowser.Common;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Logging;
using StrmDirect.Configuration;
using StrmDirect.Playback;

namespace StrmDirect;

public sealed class Plugin : BasePluginSimpleUI<PluginOptions>, IDisposable
{
    private readonly PlaybackInterceptor _playback;
    private readonly object _sync = new();
    private bool _disposed;

    public override Guid Id => new("ff9f1516-9380-47c6-95c5-3a7ad36a0637");
    public override string Name => "StrmDirect";
    public override string Description => "Direct STRM playback for Emby.";

    public Plugin(IApplicationHost host, ILibraryManager library, IHttpClient http, ILogManager log)
        : base(host)
    {
        _playback = new PlaybackInterceptor(library, http, log.GetLogger(Name));
        try { _playback.Apply(GetOptions()); }
        catch (Exception error)
        {
            log.GetLogger(Name).Error("StrmDirect could not load configuration ({0}).", error.GetType().Name);
        }
    }

    protected override bool OnOptionsSaving(PluginOptions options)
    {
        lock (_sync)
            return !_disposed && _playback.Prepare(options);
    }

    protected override void OnOptionsSaved(PluginOptions options)
    {
        lock (_sync)
        {
            if (!_disposed)
                _playback.Apply(options);
        }
    }

    protected override PluginOptions OnBeforeShowUI(PluginOptions options) => new()
    {
        Enabled = _playback.Enabled,
        FollowRedirects = options.FollowRedirects,
        DisableTranscoding = _playback.Enabled ? _playback.TranscodingDisabled : options.DisableTranscoding
    };

    public override void OnUninstalling()
    {
        Dispose();
        base.OnUninstalling();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            _playback.Dispose();
        }
    }
}
