using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using LoalNas.Host.Configuration;
using Microsoft.Extensions.Options;

namespace LoalNas.Host.Services;

/// <summary>平台侧的一条房间记录。平台只知道令牌、有效期和访问地址，不关心对局内容。</summary>
public sealed class AppRoom
{
    public string AppId { get; init; } = "";
    public string Token { get; init; } = "";
    public string HostKey { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public string Url { get; set; } = "";
    public string OwnerUrl { get; set; } = "";

    public bool IsActive(DateTimeOffset now) => now < ExpiresAt;

    public int RemainingSeconds(DateTimeOffset now) =>
        Math.Max(0, (int)Math.Ceiling((ExpiresAt - now).TotalSeconds));
}

public sealed class AppRoomException : Exception
{
    public AppRoomException(int statusCode, string code, string message) : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    public int StatusCode { get; }
    public string Code { get; }
}

/// <summary>
/// 内存房间注册表。只有手机端（带签名）能创建/关闭房间；公开链接必须先命中这里的令牌。
/// 房间全部关闭（或过期）后，平台停止对应应用进程。
/// </summary>
public sealed class AppRoomService : IHostedService, IDisposable
{
    private readonly AppRegistry _registry;
    private readonly AppProcessManager _processManager;
    private readonly IOptions<AppsOptions> _options;
    private readonly IOptions<PublicHostOptions> _host;
    private readonly ILogger<AppRoomService> _logger;

    private readonly ConcurrentDictionary<string, AppRoom> _rooms = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _appGates =
        new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _cleanupCts;

    public AppRoomService(
        AppRegistry registry,
        AppProcessManager processManager,
        IOptions<AppsOptions> options,
        IOptions<PublicHostOptions> host,
        ILogger<AppRoomService> logger)
    {
        _registry = registry;
        _processManager = processManager;
        _options = options;
        _host = host;
        _logger = logger;
        _processManager.ProcessExited += OnProcessExited;
    }

    private AppsOptions Options => _options.Value;

    public int CountForApp(string appId) =>
        _rooms.Values.Count(r => string.Equals(r.AppId, appId, StringComparison.OrdinalIgnoreCase));

    public AppRoom? Find(string appId, string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || !_rooms.TryGetValue(token, out var room))
        {
            return null;
        }

        if (!string.Equals(room.AppId, appId, StringComparison.OrdinalIgnoreCase) ||
            !room.IsActive(DateTimeOffset.UtcNow))
        {
            return null;
        }

        return room;
    }

    public IReadOnlyList<AppRoom> ListForApp(string appId) =>
        _rooms.Values
            .Where(r => string.Equals(r.AppId, appId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.CreatedAt)
            .ToList();

    public async Task<AppRoom> CreateAsync(
        AppManifest manifest,
        int? expiresInSeconds,
        Dictionary<string, JsonElement>? options,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (!manifest.Enabled)
        {
            throw new AppRoomException(404, "app_disabled", "应用未启用。");
        }

        var maxSeconds = Math.Max(1, manifest.MaxRoomLifetimeHours) * 3600L;
        var ttlSeconds = expiresInSeconds ?? Math.Max(1, manifest.DefaultRoomLifetimeHours) * 3600L;
        if (ttlSeconds < 60 || ttlSeconds > maxSeconds)
        {
            throw new AppRoomException(
                400, "invalid_expiry", $"房间有效期必须在 60 秒到 {maxSeconds} 秒之间。");
        }

        var gate = _appGates.GetOrAdd(manifest.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var activeCount = CountForApp(manifest.Id);
            if (activeCount >= manifest.MaxRooms)
            {
                throw new AppRoomException(
                    409, "too_many_rooms", $"该应用最多同时保留 {manifest.MaxRooms} 个房间。");
            }

            var token = NewUniqueToken();
            var hostKey = ShareTokens.New();

            (HttpStatusCode Status, string Body) result;
            try
            {
                result = await _processManager.ControlAsync(
                    manifest,
                    HttpMethod.Post,
                    "rooms",
                    new
                    {
                        token,
                        hostKey,
                        options = options ?? new Dictionary<string, JsonElement>(),
                    },
                    cancellationToken);
            }
            catch (AppStartException ex)
            {
                throw new AppRoomException(503, "app_start_failed", ex.Message);
            }

            if ((int)result.Status is < 200 or >= 300)
            {
                try
                {
                    if (CountForApp(manifest.Id) == 0 &&
                        _processManager.IsRunning(manifest))
                    {
                        await _processManager.StopAppAsync(
                            manifest, graceful: true, CancellationToken.None);
                    }
                }
                catch
                {
                    // 启动失败后的清理失败不覆盖原始错误
                }

                throw new AppRoomException(
                    502, "app_room_create_failed",
                    $"应用拒绝建房（{(int)result.Status}）：{Truncate(result.Body)}");
            }

            var now = DateTimeOffset.UtcNow;
            var room = new AppRoom
            {
                AppId = manifest.Id,
                Token = token,
                HostKey = hostKey,
                CreatedAt = now,
                ExpiresAt = now.AddSeconds(ttlSeconds),
            };
            room.Url = BuildUrl(manifest, token, request);
            room.OwnerUrl = room.Url + "?key=" + Uri.EscapeDataString(hostKey);

            if (!_rooms.TryAdd(token, room))
            {
                throw new AppRoomException(500, "token_collision", "房间令牌冲突，请重试。");
            }

            _logger.LogInformation(
                "已创建应用房间：{AppId}/{Token}，有效期至 {ExpiresAt:u}",
                manifest.Id, token, room.ExpiresAt);
            return room;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> CloseAsync(
        string appId, string token, string reason, CancellationToken cancellationToken)
    {
        if (!_rooms.TryGetValue(token, out var room) ||
            !string.Equals(room.AppId, appId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var manifest = _registry.Find(appId);
        var gate = _appGates.GetOrAdd(appId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!_rooms.TryRemove(token, out room))
            {
                return false;
            }

            if (manifest is not null)
            {
                try
                {
                    await _processManager.TryControlIfRunningAsync(
                        manifest,
                        HttpMethod.Delete,
                        $"rooms/{token}",
                        null,
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "通知应用删除房间失败：{AppId}/{Token}", appId, token);
                }
            }

            _logger.LogInformation(
                "已关闭应用房间：{AppId}/{Token}（{Reason}）", appId, token, reason);

            if (manifest is not null && CountForApp(appId) == 0)
            {
                await StopAppIfIdleAsync(manifest, reason, cancellationToken);
            }

            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task CloseAllAsync(
        string appId, string reason, CancellationToken cancellationToken)
    {
        var manifest = _registry.Find(appId);
        var gate = _appGates.GetOrAdd(appId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var removed = new List<AppRoom>();
            foreach (var room in _rooms.Values)
            {
                if (string.Equals(room.AppId, appId, StringComparison.OrdinalIgnoreCase) &&
                    _rooms.TryRemove(room.Token, out var actual))
                {
                    removed.Add(actual);
                }
            }

            if (manifest is not null)
            {
                await StopAppIfIdleAsync(manifest, reason, cancellationToken);
            }

            if (removed.Count > 0)
            {
                _logger.LogInformation(
                    "已关闭应用 {AppId} 的 {Count} 个房间（{Reason}）。",
                    appId, removed.Count, reason);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task StopAppAsync(
        string appId, CancellationToken cancellationToken)
    {
        await CloseAllAsync(appId, "manual_stop", cancellationToken);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cleanupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = Task.Run(() => CleanupLoopAsync(_cleanupCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cleanupCts?.Cancel();
        _rooms.Clear();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _processManager.ProcessExited -= OnProcessExited;
        _cleanupCts?.Cancel();
        _cleanupCts?.Dispose();
    }

    private async Task CleanupLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var now = DateTimeOffset.UtcNow;
                var expired = _rooms.Values.Where(r => !r.IsActive(now)).ToList();
                foreach (var room in expired)
                {
                    try
                    {
                        await CloseAsync(room.AppId, room.Token, "expired", cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex, "清理过期房间失败：{AppId}/{Token}", room.AppId, room.Token);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "应用房间清理循环异常退出。");
        }
    }

    private async Task StopAppIfIdleAsync(
        AppManifest manifest, string reason, CancellationToken cancellationToken)
    {
        if (CountForApp(manifest.Id) > 0 || !_processManager.IsRunning(manifest))
        {
            return;
        }

        _logger.LogInformation(
            "应用 {AppId} 已无房间（{Reason}），停止进程。", manifest.Id, reason);
        try
        {
            await _processManager.StopAppAsync(manifest, graceful: true, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "停止空闲应用 {AppId} 失败。", manifest.Id);
        }
    }

    private void OnProcessExited(string appId)
    {
        var removed = new List<string>();
        foreach (var room in _rooms.Values)
        {
            if (string.Equals(room.AppId, appId, StringComparison.OrdinalIgnoreCase) &&
                _rooms.TryRemove(room.Token, out _))
            {
                removed.Add(room.Token);
            }
        }

        if (removed.Count > 0)
        {
            _logger.LogWarning(
                "应用 {AppId} 进程退出，已失效 {Count} 个房间。", appId, removed.Count);
        }
    }

    private string NewUniqueToken()
    {
        string token;
        do
        {
            token = ShareTokens.New();
        }
        while (_rooms.ContainsKey(token));

        return token;
    }

    private string BuildUrl(AppManifest manifest, string token, HttpRequest request)
    {
        var baseUrl = _host.Value.PublicBaseUrl?.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = $"{request.Scheme}://{request.Host}";
        }

        return baseUrl.TrimEnd('/') + manifest.PublicPrefix + "/" + token;
    }

    private static string Truncate(string value, int max = 300) =>
        string.IsNullOrEmpty(value) ? "" : value.Length <= max ? value : value[..max];
}
