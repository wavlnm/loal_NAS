namespace LoalNas.Host.Configuration;

/// <summary>
/// 监控（摄像头源 + 移动侦测 + 事件录像 + 听现场）配置。
/// 事件片段写在共享目录内的独立文件夹里，手机既能从监控页看，也能当普通文件下载。
/// </summary>
public sealed class MonitorOptions
{
    public const string SectionName = "LoalNas:Monitor";

    /// <summary>监控功能总开关；关闭时摄像头接口一律返回未启用。</summary>
    public bool Enabled { get; set; } = true;

    public string CameraDevice { get; set; } = "/dev/video0";

    /// <summary>摄像头抓取脚本相对发布目录的路径。</summary>
    public string WorkerScript { get; set; } = "tools/monitor/camera_worker.py";

    // ── 预览流 ────────────────────────────────────────────────────────────
    public int MinWidth { get; set; } = 320;
    public int MaxWidth { get; set; } = 1280;
    public int DefaultWidth { get; set; } = 640;
    public int DefaultQuality { get; set; } = 60;
    public int PreviewFps { get; set; } = 8;

    /// <summary>没有取图请求且侦测关闭时，空闲多久停掉抓取进程。</summary>
    public int IdleSeconds { get; set; } = 60;

    /// <summary>缓存帧新鲜度上限，超过则视为抓取停摆。</summary>
    public int MaxFrameAgeSeconds { get; set; } = 5;

    // ── 移动侦测与事件录像 ────────────────────────────────────────────────
    /// <summary>首次启动时的侦测开关默认值（用户改过之后以持久化状态为准）。</summary>
    public bool DetectEnabled { get; set; }

    /// <summary>灵敏度：low / mid / high。</summary>
    public string Sensitivity { get; set; } = "mid";

    public int RingFps { get; set; } = 10;

    /// <summary>摄像头源（也是事件录像）的分辨率；预览流在此基础上缩小。</summary>
    public int ClipWidth { get; set; } = 1280;

    public int ClipHeight { get; set; } = 720;
    public int DetectFps { get; set; } = 3;
    public int ConfirmHits { get; set; } = 3;
    public int RecordSeconds { get; set; } = 10;
    public int CooldownSeconds { get; set; } = 30;
    public int ClipCrf { get; set; } = 23;

    /// <summary>事件片段是否同时录麦克风音轨。</summary>
    public bool ClipWithAudio { get; set; } = true;

    /// <summary>共享目录内放片段的文件夹名。</summary>
    public string ClipFolderName { get; set; } = "监控录像";

    // ── 听现场（麦克风实时流） ────────────────────────────────────────────
    public bool ListenEnabled { get; set; } = true;
    public int ListenBitrateKbps { get; set; } = 48;
    public int ListenSampleRate { get; set; } = 16000;

    /// <summary>播放票据有效期；到点后必须重新申请。</summary>
    public int ListenTicketTtlSeconds { get; set; } = 300;

    /// <summary>单条实时流的最长时长，防止客户端异常断线后进程常驻。</summary>
    public int ListenMaxSeconds { get; set; } = 300;
}
