using System.Reflection;
using HarmonyLib;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;

namespace StrmDirect.Interop;

// Keep all private Emby API names and signatures in one place.
internal sealed class EmbyStreaming
{
    private const string Namespace = "Emby.Server.MediaEncoding.Api.";
    private readonly Assembly _assembly = Assembly.Load("Emby.Server.MediaEncoding");
    private readonly Type _requestType;
    private readonly Type _stateType;
    private readonly MethodInfo _getState;
    private readonly MethodInfo _disposeState;
    private readonly MethodInfo _clone = AccessTools.Method(typeof(object), "MemberwiseClone");

    public MethodInfo ProgressiveRequest { get; }
    public MethodInfo StateRequest => _getState;

    public EmbyStreaming()
    {
        _requestType = FindType("StreamRequest");
        _stateType = FindType("StreamState");
        ProgressiveRequest = FindMethod(FindType("Progressive.BaseProgressiveStreamingService"),
            "ProcessRequest", typeof(Task<object>), _requestType, typeof(bool));
        _getState = FindMethod(FindType("BaseStreamingService"), "GetState",
            typeof(Task<>).MakeGenericType(_stateType), _requestType, typeof(bool), typeof(CancellationToken));
        _disposeState = FindMethod(_stateType, "Dispose", typeof(void), typeof(bool), typeof(bool));
        RequireProperty(_requestType, "Static", typeof(bool), writable: true);
        RequireProperty(_stateType, "MediaSource", typeof(MediaSourceInfo));
        RequireProperty(_stateType, "LiveStream");
    }

    public MethodInfo FindPlaybackInfo()
    {
        var dto = FindType("GetPostedPlaybackInfo");
        RequireBoolean(dto, "EnableTranscoding");
        RequireBoolean(dto, "EnableDirectStream");
        RequireProperty(dto, "Id", typeof(string));
        return FindMethod(FindType("MediaInfoService"), "GetPlaybackInfo", typeof(Task<PlaybackInfoResponse>),
            dto, typeof(bool), typeof(string), typeof(CancellationToken));
    }

    public object Copy(object request) => _clone.Invoke(request, null)!;

    public object? OpenState(object service, object request, bool useStaticState, CancellationToken cancellation)
    {
        var task = (Task)_getState.Invoke(service, [request, useStaticState, cancellation])!;
        task.GetAwaiter().GetResult();
        return Read<object>(task, "Result");
    }

    public void ReleaseState(object state) => _disposeState.Invoke(state, [true, true]);

    public static T? Read<T>(object? instance, string name) =>
        instance != null && AccessTools.Property(instance.GetType(), name)?.GetValue(instance) is T value
            ? value : default;

    public static void Write(object instance, string name, object? value) =>
        (AccessTools.Property(instance.GetType(), name) ?? throw new MissingMemberException(name))
            .SetValue(instance, value);

    private Type FindType(string name) => _assembly.GetType(Namespace + name, throwOnError: true)!;

    private static MethodInfo FindMethod(Type type, string name, Type result, params Type[] parameters)
    {
        var method = AccessTools.Method(type, name, parameters);
        return method is { IsStatic: false } && method.ReturnType == result
            ? method : throw new MissingMethodException(type.FullName, name);
    }

    private static PropertyInfo RequireProperty(Type type, string name, Type? expected = null, bool writable = false)
    {
        var property = AccessTools.Property(type, name);
        if (property == null || !property.CanRead || (writable && !property.CanWrite) ||
            (expected != null && property.PropertyType != expected))
            throw new MissingMemberException(type.FullName, name);
        return property;
    }

    private static void RequireBoolean(Type type, string name)
    {
        var property = RequireProperty(type, name, writable: true);
        if (property.PropertyType != typeof(bool) && property.PropertyType != typeof(bool?))
            throw new MissingMemberException(type.FullName, name);
    }
}
