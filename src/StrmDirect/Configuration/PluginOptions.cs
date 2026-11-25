using System.ComponentModel;
using Emby.Web.GenericEdit;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.GenericEdit;

namespace StrmDirect.Configuration;

public sealed class PluginOptions : EditableOptionsBase
{
    public override string EditorTitle => "设置";
    public override string EditorDescription => string.Empty;

    [DisplayName("启用插件")]
    public bool Enabled { get; set; }

    [DisplayName("跟随重定向")]
    [Description("获取最终地址；关闭时直接返回 STRM 内原始地址。")]
    [VisibleCondition(nameof(Enabled), SimpleCondition.IsTrue)]
    public bool FollowRedirects { get; set; } = true;

    [DisplayName("禁用转码")]
    [Description("禁止 STRM 播放转码及重封装；不支持直放的客户端可能无法播放。")]
    [VisibleCondition(nameof(Enabled), SimpleCondition.IsTrue)]
    public bool DisableTranscoding { get; set; }
}
