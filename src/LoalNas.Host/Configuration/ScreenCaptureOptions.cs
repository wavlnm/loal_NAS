namespace LoalNas.Host.Configuration;

/// <summary>看屏幕抓取配置（X11 ximagesrc 通道）。</summary>
public sealed class ScreenCaptureOptions
{
    public const string SectionName = "LoalNas:ScreenCapture";

    /// <summary>允许的最宽分辨率，超过则按此值裁剪。</summary>
    public int MaxWidth { get; set; } = 1920;

    /// <summary>允许的最宽分辨率下限。</summary>
    public int MinWidth { get; set; } = 640;

    /// <summary>请求未带 w/q 时的默认值。</summary>
    public int DefaultWidth { get; set; } = 1280;

    public int DefaultQuality { get; set; } = 60;

    /// <summary>抓取进程的出帧频率；公网带宽小可以调低省 CPU 和流量。</summary>
    public int Framerate { get; set; } = 10;

    /// <summary>帧新鲜度上限：缓存帧比这个更旧就视为抓取已停摆，丢弃并重新触发。</summary>
    public int MaxFrameAgeSeconds { get; set; } = 5;

    /// <summary>多久没有取图就停掉抓取进程。</summary>
    public int IdleSeconds { get; set; } = 60;

    /// <summary>抓取脚本相对发布目录的路径。</summary>
    public string WorkerScript { get; set; } = "tools/screen-capture/screen_worker.py";

    /// <summary>帧输出目录；留空则用 ~/.cache/loalnas/screen。</summary>
    public string OutputDirectory { get; set; } = "";
}
