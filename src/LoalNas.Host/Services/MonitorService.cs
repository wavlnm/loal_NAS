using System.Text.Json;
using LoalNas.Host.Configuration;
using Microsoft.Extensions.Options;

namespace LoalNas.Host.Services;

/// <summary>
/// 监控：摄像头侦测/录像的控制器与状态出口。
/// 抓帧本身由 ScreenCaptureService 的摄像头通道负责，这里只做三件事：
/// 1. 把用户的侦测开关/灵敏度写进 command.json（并持久化，重启后恢复）；
/// 2. 读 worker 写出的 state.json，对外提供状态与今日事件；
/// 3. 每天把非当天的事件片段整体移入回收站（沿用回收站的保留策略）。
/// 另外签发"听现场"的一次性播放票据（播放器插件带不了签名头，只能靠不可猜的 key）。
/// </summary>
public sealed class MonitorService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public sealed record ClipInfo(
        string Name,
        string RelativePath,
        long SizeBytes,
        DateTimeOffset ModifiedAt);

    private readonly ILogger<MonitorService> _logger;
    private readonly MonitorOptions _options;
    private readonly ScreenCaptureService _capture;
    private readonly FileBrowserProcessManager _manager;
    private readonly TrashService _trash;

    private readonly object _stateLock = new();
    private bool _detectEnabled;
    private string _sensitivity = "mid";
    private long _manualToken;

    private readonly ConcurrentTicketBag _listenTickets = new();

    public MonitorService(
        IOptions<MonitorOptions> options,
        ScreenCaptureService capture,
        FileBrowserProcessManager manager,
        TrashService trash,
        ILogger<MonitorService> logger)
    {
        _logger = logger;
        _options = options.Value;
        _capture = capture;
        _manager = manager;
        _trash = trash;
        _detectEnabled = _options.DetectEnabled;
        _sensitivity = NormalizeSensitivity(_options.Sensitivity);
        LoadSettings();
    }

    public string ClipRootPath => Path.Combine(
        Path.GetFullPath(_manager.SharedRootPath), _options.ClipFolderName);

    /// <summary>监控总开关（配置项），关掉后摄像头取图与所有监控接口都不可用。</summary>
    public bool StatusEnabled => _options.Enabled;

    /// <summary>端点需要的听现场参数（码率、采样率、票据 TTL、最长时长）。</summary>
    public MonitorOptions ListenOptions => _options;

    public string ClipRootResourcePath => "/" + _options.ClipFolderName;

    // ── 设置 ────────────────────────────────────────────────────────────────

    public (bool Enabled, string Sensitivity) Detect
    {
        get
        {
            lock (_stateLock)
            {
                return (_detectEnabled && _options.Enabled, _sensitivity);
            }
        }
    }

    public void ConfigureDetect(bool? enabled, string? sensitivity)
    {
        lock (_stateLock)
        {
            if (enabled.HasValue)
            {
                _detectEnabled = enabled.Value;
            }
            if (!string.IsNullOrWhiteSpace(sensitivity))
            {
                _sensitivity = NormalizeSensitivity(sensitivity);
            }
        }

        SaveSettings();
        WriteCommand();
        ApplyKeepAlive();
        _logger.LogInformation("移动侦测：{Enabled}，灵敏度 {Sensitivity}",
            Detect.Enabled, Detect.Sensitivity);
    }

    public void RequestManualRecording()
    {
        lock (_stateLock)
        {
            _manualToken = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
        WriteCommand();
    }

    // ── 状态 ────────────────────────────────────────────────────────────────

    public object Status()
    {
        var worker = ReadState();
        var camera = _capture.CameraWorkerState();
        var todayClips = ListClips(null);
        return new
        {
            enabled = _options.Enabled,
            camera = new
            {
                device = _options.CameraDevice,
                present = File.Exists(_options.CameraDevice),
                running = camera is not null,
                width = camera?.Width ?? 0,
                quality = camera?.Quality ?? 0,
            },
            detect = new
            {
                enabled = Detect.Enabled,
                sensitivity = Detect.Sensitivity,
                recordSeconds = _options.RecordSeconds,
                quietSeconds = _options.QuietSeconds,
                maxSegmentSeconds = _options.MaxSegmentSeconds,
                cooldownSeconds = _options.CooldownSeconds,
                withAudio = _options.ClipWithAudio,
                imageLib = BoolOf(worker, "imageLib"),
            },
            worker = worker is null
                ? null
                : new
                {
                    running = BoolOf(worker, "running"),
                    motionActive = BoolOf(worker, "motionActive"),
                    recording = BoolOf(worker, "recording"),
                    eventActive = BoolOf(worker, "eventActive"),
                    segmentIndex = IntOf(worker, "segmentIndex"),
                    quietRemaining = DoubleOf(worker, "quietRemaining"),
                    segmentStartedAt = DoubleOf(worker, "segmentStartedAt"),
                    lastMotionAt = DoubleOf(worker, "lastMotionAt"),
                    previewFps = DoubleOf(worker, "previewFpsMeasured"),
                    lastTrigger = DoubleOf(worker, "lastTrigger"),
                    lastScore = DoubleOf(worker, "lastScore"),
                    lastMean = DoubleOf(worker, "lastMean"),
                    sourceResolution = SizeOf(worker, "sourceResolution"),
                    explicitCaps = BoolOf(worker, "explicitCaps"),
                },
            clipsToday = todayClips.Count,
            clipFolder = ClipRootResourcePath,
            listen = new { enabled = _options.ListenEnabled },
        };
    }

    // worker 的 state.json 字段随版本变化，读取一律容错，缺字段当默认值。
    private static bool BoolOf(JsonElement? element, string name) =>
        element is { } root
        && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.True;

    private static int IntOf(JsonElement? element, string name)
    {
        if (element is not { } root
            || !root.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number)
        {
            return 0;
        }

        return value.TryGetInt32(out var integer) ? integer : 0;
    }

    private static double DoubleOf(JsonElement? element, string name)
    {
        if (element is not { } root
            || !root.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number)
        {
            return 0d;
        }

        return value.TryGetInt64(out var integer)
            ? integer
            : value.TryGetDouble(out var real) ? real : 0d;
    }

    /// <summary>把 [宽,高] 数组渲染成 "1280x720"，缺字段返回 null。</summary>
    private static string? SizeOf(JsonElement? element, string name)
    {
        if (element is not { } root
            || !root.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Array
            || value.GetArrayLength() != 2)
        {
            return null;
        }

        var items = value.EnumerateArray().ToArray();
        if (!items[0].TryGetInt32(out var width) || !items[1].TryGetInt32(out var height))
        {
            return null;
        }

        return $"{width}x{height}";
    }

    public JsonElement? ReadState()
    {
        try
        {
            if (!File.Exists(MonitorPaths.StateFile))
            {
                return null;
            }

            using var stream = File.OpenRead(MonitorPaths.StateFile);
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.Clone();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "读取监控状态失败。");
            return null;
        }
    }

    /// <summary>今日（或指定日期）的事件片段列表，按时间倒序。</summary>
    public IReadOnlyList<ClipInfo> ListClips(string? date)
    {
        var day = string.IsNullOrWhiteSpace(date) ? DateTime.Now.ToString("yyyy-MM-dd") : date.Trim();
        var folder = Path.Combine(ClipRootPath, day);
        var list = new List<ClipInfo>();
        if (!Directory.Exists(folder))
        {
            return list;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.mp4"))
            {
                var info = new FileInfo(file);
                if (info.Length == 0)
                {
                    continue;
                }

                var name = info.Name;
                list.Add(new ClipInfo(
                    name,
                    $"{ClipRootResourcePath}/{day}/{name}",
                    info.Length,
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "读取事件片段列表失败。");
        }

        return list.OrderByDescending(c => c.ModifiedAt).ToList();
    }

    public IReadOnlyList<string> ClipDates()
    {
        var dates = new List<string>();
        try
        {
            if (!Directory.Exists(ClipRootPath))
            {
                return dates;
            }

            dates.AddRange(Directory.EnumerateDirectories(ClipRootPath)
                .Select(Path.GetFileName)
                .Where(n => n is not null && n.Length == 10)
                .Select(n => n!)
                .OrderByDescending(n => n));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "读取片段日期目录失败。");
        }

        return dates;
    }

    // ── 听现场票据 ───────────────────────────────────────────────────────────

    /// <summary>签发一次性实时流票据（播放器插件带不了签名头，只能靠不可猜的 key）。</summary>
    public string IssueListenTicket()
    {
        var key = _listenTickets.Issue(TimeSpan.FromSeconds(
            Math.Clamp(_options.ListenTicketTtlSeconds, 30, 3600)));
        _logger.LogInformation("已签发监控实时音频票据（一次用）。");
        return key;
    }

    public bool TryConsumeListenTicket(string? key) => _listenTickets.Consume(key);

    // ── 周期任务 ─────────────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 先把设置推给 worker，保证 worker 一起来就是用户上次选的状态。
        WriteCommand();
        ApplyKeepAlive();

        var lastCleanupDay = string.Empty;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                ApplyKeepAlive();

                var today = DateTime.Now.ToString("yyyy-MM-dd");
                if (today != lastCleanupDay || DateTime.Now.Hour < 1)
                {
                    lastCleanupDay = today;
                    MoveExpiredClips(today);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "监控周期任务出错。");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 侦测开着就让摄像头进程常驻；关掉时不硬掐——正在取图的请求会被打断，
    /// 交给 ScreenCaptureService 的空闲停止逻辑在 IdleSeconds 后收尾。
    /// </summary>
    private void ApplyKeepAlive()
    {
        var (enabled, _) = Detect;
        _capture.EnsureCameraChannelAlive(_options.Enabled && enabled);
    }

    private void MoveExpiredClips(string today)
    {
        if (!Directory.Exists(ClipRootPath))
        {
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(ClipRootPath).ToList())
        {
            var name = Path.GetFileName(dir);
            if (name == today || name.Length != 10)
            {
                continue;
            }

            var status = _trash.MoveToTrash($"{ClipRootResourcePath}/{name}", out var entry);
            if (status == TrashService.TrashStatus.Ok)
            {
                _logger.LogInformation("跨天事件片段已移入回收站：{Day} -> {Id}", name, entry?.Id);
            }
            else
            {
                _logger.LogWarning("跨天事件片段移入回收站失败：{Day}（{Status}）", name, status);
            }
        }

        // 散落在根目录的旧片段（早期版本或手工放的）同样处理。
        foreach (var file in Directory.EnumerateFiles(ClipRootPath, "*.mp4").ToList())
        {
            if (new FileInfo(file).LastWriteTimeUtc.AddDays(1) > DateTime.UtcNow)
            {
                continue;
            }

            _trash.MoveToTrash($"{ClipRootResourcePath}/{Path.GetFileName(file)}", out _);
        }
    }

    // ── 本地文件读写 ─────────────────────────────────────────────────────────

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(MonitorPaths.SettingsFile))
            {
                return;
            }

            using var stream = File.OpenRead(MonitorPaths.SettingsFile);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.TryGetProperty("detectEnabled", out var enabled)
                && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                _detectEnabled = enabled.GetBoolean();
            }

            if (root.TryGetProperty("sensitivity", out var sens))
            {
                _sensitivity = NormalizeSensitivity(sens.GetString());
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取监控设置失败，使用默认值。");
        }
    }

    private void SaveSettings()
    {
        try
        {
            MonitorPaths.EnsureDirectory();
            var (enabled, sensitivity) = Detect;
            File.WriteAllText(MonitorPaths.SettingsFile,
                JsonSerializer.Serialize(new { detectEnabled = enabled, sensitivity }, JsonOpts));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存监控设置失败。");
        }
    }

    private void WriteCommand()
    {
        try
        {
            MonitorPaths.EnsureDirectory();
            var (enabled, sensitivity) = Detect;
            long manual;
            lock (_stateLock)
            {
                manual = _manualToken;
            }

            File.WriteAllText(MonitorPaths.CommandFile,
                JsonSerializer.Serialize(new
                {
                    detect = enabled,
                    sensitivity,
                    manual,
                    updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                }, JsonOpts));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写监控指令失败。");
        }
    }

    private static string NormalizeSensitivity(string? value) =>
        value is "low" or "mid" or "high" ? value : "mid";

    /// <summary>一次性票据集合：签发时带 TTL，取用即失效。</summary>
    private sealed class ConcurrentTicketBag
    {
        private readonly Dictionary<string, DateTime> _tickets = new();
        private readonly object _lock = new();

        public string Issue(TimeSpan ttl)
        {
            var key = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16))
                .ToLowerInvariant();
            lock (_lock)
            {
                foreach (var pair in _tickets.Where(p => p.Value < DateTime.UtcNow).ToList())
                {
                    _tickets.Remove(pair.Key);
                }

                _tickets[key] = DateTime.UtcNow.Add(ttl);
            }

            return key;
        }

        public bool Consume(string? key)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length != 32)
            {
                return false;
            }

            lock (_lock)
            {
                if (!_tickets.TryGetValue(key, out var expiry))
                {
                    return false;
                }

                _tickets.Remove(key);
                return expiry > DateTime.UtcNow;
            }
        }
    }
}
