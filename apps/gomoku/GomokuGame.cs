using System.Collections.Concurrent;

namespace LoalNas.Gomoku;

/// <summary>一局五子棋房间（15×15，无禁手，连成 5 子或以上获胜）。</summary>
public sealed class GomokuRoom
{
    public const int Size = 15;
    public const int MinMoveTimeoutSeconds = 15;
    public const int MaxMoveTimeoutSeconds = 600;

    public object Sync { get; } = new();
    public string Token { get; }
    public string HostKey { get; }
    public int MoveTimeoutSeconds { get; }
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;

    public int[] Board { get; } = new int[Size * Size];
    public string Status { get; private set; } = "waiting";
    public int Turn { get; private set; } = 1;
    public string? BlackClientId { get; private set; }
    public string? WhiteClientId { get; private set; }
    public int Winner { get; private set; }
    public string? EndReason { get; private set; }
    public long DeadlineUnixMs { get; private set; }
    public long Version { get; private set; }
    public int LastMoveX { get; private set; } = -1;
    public int LastMoveY { get; private set; } = -1;
    public int RematchRequestedBy { get; private set; }
    public bool Removed { get; private set; }
    public List<MoveRecord> Moves { get; } = new();

    private readonly HashSet<TaskCompletionSource<bool>> _waiters = new();

    public GomokuRoom(string token, string hostKey, int moveTimeoutSeconds)
    {
        Token = token;
        HostKey = hostKey;
        MoveTimeoutSeconds = Math.Clamp(
            moveTimeoutSeconds, MinMoveTimeoutSeconds, MaxMoveTimeoutSeconds);
    }

    /// <summary>加入房间；返回 1=黑、2=白、0=观战。</summary>
    public int Join(string clientId, string? hostKey, out bool started)
    {
        started = false;
        lock (Sync)
        {
            if (Removed)
            {
                return 0;
            }

            var color = ColorOfLocked(clientId);

            if (!string.IsNullOrEmpty(hostKey) && FixedTimeEquals(hostKey, HostKey))
            {
                if (WhiteClientId == clientId)
                {
                    WhiteClientId = null;
                }

                if (BlackClientId != clientId)
                {
                    BlackClientId = clientId;
                    Version++;
                }

                color = 1;
            }
            else if (color == 0)
            {
                if (BlackClientId is null)
                {
                    BlackClientId = clientId;
                    color = 1;
                    Version++;
                }
                else if (WhiteClientId is null)
                {
                    WhiteClientId = clientId;
                    color = 2;
                    Version++;
                }
            }

            if (Status == "waiting" && BlackClientId is not null && WhiteClientId is not null)
            {
                StartGameLocked();
                started = true;
            }
            else if (color != 0 && Version > 0)
            {
                NotifyLocked();
            }

            return color;
        }
    }

    public string? Move(string clientId, int x, int y, out int color)
    {
        lock (Sync)
        {
            color = ColorOfLocked(clientId);
            if (Removed)
            {
                return "removed";
            }

            ApplyTimeoutLocked();
            if (Status != "playing")
            {
                return "not_playing";
            }

            if (color == 0)
            {
                return "not_seated";
            }

            if (color != Turn)
            {
                return "not_your_turn";
            }

            if (x < 0 || x >= Size || y < 0 || y >= Size)
            {
                return "out_of_range";
            }

            var index = y * Size + x;
            if (Board[index] != 0)
            {
                return "occupied";
            }

            Board[index] = Turn;
            LastMoveX = x;
            LastMoveY = y;
            Moves.Add(new MoveRecord(x, y, Turn));

            if (HasFiveLocked(x, y, Turn))
            {
                FinishLocked(Turn, "five");
            }
            else if (Moves.Count >= Size * Size)
            {
                FinishLocked(3, "draw");
            }
            else
            {
                Turn = 3 - Turn;
                DeadlineUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() +
                    MoveTimeoutSeconds * 1000L;
                Version++;
                NotifyLocked();
            }

            return null;
        }
    }

    public string? Resign(string clientId)
    {
        lock (Sync)
        {
            if (Removed)
            {
                return "removed";
            }

            var color = ColorOfLocked(clientId);
            if (color == 0)
            {
                return "not_seated";
            }

            ApplyTimeoutLocked();
            if (Status != "playing")
            {
                return "not_playing";
            }

            FinishLocked(3 - color, "resign");
            return null;
        }
    }

    public string? Rematch(string clientId, string? action)
    {
        lock (Sync)
        {
            if (Removed)
            {
                return "removed";
            }

            var color = ColorOfLocked(clientId);
            if (color == 0)
            {
                return "not_seated";
            }

            if (Status != "finished")
            {
                return "not_finished";
            }

            if (BlackClientId is null || WhiteClientId is null)
            {
                return "opponent_missing";
            }

            switch ((action ?? "").Trim().ToLowerInvariant())
            {
                case "request":
                    if (RematchRequestedBy == color)
                    {
                        return null;
                    }

                    if (RematchRequestedBy != 0)
                    {
                        StartRematchLocked();
                        return null;
                    }

                    RematchRequestedBy = color;
                    Version++;
                    NotifyLocked();
                    return null;

                case "accept":
                    if (RematchRequestedBy == 0)
                    {
                        return "no_request";
                    }

                    if (RematchRequestedBy == color)
                    {
                        return "waiting_opponent";
                    }

                    StartRematchLocked();
                    return null;

                case "decline":
                    if (RematchRequestedBy == 0 || RematchRequestedBy == color)
                    {
                        return "no_request";
                    }

                    RematchRequestedBy = 0;
                    Version++;
                    NotifyLocked();
                    return null;

                default:
                    return "invalid_action";
            }
        }
    }

    public void Sweep()
    {
        lock (Sync)
        {
            if (Removed)
            {
                return;
            }

            ApplyTimeoutLocked();
        }
    }

    public void MarkRemoved()
    {
        lock (Sync)
        {
            if (Removed)
            {
                return;
            }

            Removed = true;
            Version++;
            NotifyLocked();
        }
    }

    public async Task<RoomSnapshot> WaitForChangeAsync(
        string? clientId, long expectedVersion, TimeSpan timeout, CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool> waiter;
        lock (Sync)
        {
            if (Removed || Version != expectedVersion)
            {
                return Snapshot(clientId);
            }

            waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add(waiter);
        }

        try
        {
            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delay = Task.Delay(timeout, delayCts.Token);
            await Task.WhenAny(waiter.Task, delay);
            delayCts.Cancel();
        }
        catch (OperationCanceledException)
        {
            // 请求已断开
        }
        finally
        {
            lock (Sync)
            {
                _waiters.Remove(waiter);
            }
        }

        return Snapshot(clientId);
    }

    public RoomSnapshot Snapshot(string? clientId)
    {
        lock (Sync)
        {
            ApplyTimeoutLocked();
            var now = DateTimeOffset.UtcNow;
            var color = string.IsNullOrEmpty(clientId) ? 0 : ColorOfLocked(clientId);

            var board = new int[Size][];
            for (var y = 0; y < Size; y++)
            {
                var row = new int[Size];
                Array.Copy(Board, y * Size, row, 0, Size);
                board[y] = row;
            }

            var remaining = Status == "playing" && DeadlineUnixMs > 0
                ? Math.Max(0, (int)Math.Ceiling((DeadlineUnixMs - now.ToUnixTimeMilliseconds()) / 1000.0))
                : 0;

            return new RoomSnapshot(
                Token,
                Status,
                Turn,
                color,
                BlackClientId is not null,
                WhiteClientId is not null,
                Winner,
                EndReason,
                board,
                LastMoveX >= 0 ? new MovePoint(LastMoveX, LastMoveY) : null,
                Status == "playing" ? DeadlineUnixMs : 0,
                now.ToUnixTimeMilliseconds(),
                remaining,
                Version,
                MoveTimeoutSeconds,
                RematchRequestedBy,
                Removed);
        }
    }

    private void StartGameLocked()
    {
        Status = "playing";
        Turn = 1;
        Winner = 0;
        EndReason = null;
        RematchRequestedBy = 0;
        DeadlineUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() +
            MoveTimeoutSeconds * 1000L;
        Version++;
        NotifyLocked();
    }

    private void StartRematchLocked()
    {
        // 交换黑白，重新开局
        (BlackClientId, WhiteClientId) = (WhiteClientId, BlackClientId);
        Array.Clear(Board);
        Moves.Clear();
        LastMoveX = -1;
        LastMoveY = -1;
        Winner = 0;
        EndReason = null;
        RematchRequestedBy = 0;
        Status = "playing";
        Turn = 1;
        DeadlineUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() +
            MoveTimeoutSeconds * 1000L;
        Version++;
        NotifyLocked();
    }

    private void ApplyTimeoutLocked()
    {
        if (Status != "playing" || DeadlineUnixMs <= 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now < DeadlineUnixMs)
        {
            return;
        }

        FinishLocked(3 - Turn, "timeout");
    }

    private void FinishLocked(int winner, string reason)
    {
        Status = "finished";
        Winner = winner;
        EndReason = reason;
        DeadlineUnixMs = 0;
        Version++;
        NotifyLocked();
    }

    private bool HasFiveLocked(int x, int y, int color)
    {
        ReadOnlySpan<(int dx, int dy)> directions =
        [
            (1, 0), (0, 1), (1, 1), (1, -1),
        ];

        foreach (var (dx, dy) in directions)
        {
            var count = 1;
            count += CountDirectionLocked(x, y, dx, dy, color);
            count += CountDirectionLocked(x, y, -dx, -dy, color);
            if (count >= 5)
            {
                return true;
            }
        }

        return false;
    }

    private int CountDirectionLocked(int x, int y, int dx, int dy, int color)
    {
        var count = 0;
        var cx = x + dx;
        var cy = y + dy;
        while (cx >= 0 && cx < Size && cy >= 0 && cy < Size &&
               Board[cy * Size + cx] == color)
        {
            count++;
            cx += dx;
            cy += dy;
        }

        return count;
    }

    private int ColorOfLocked(string? clientId)
    {
        if (string.IsNullOrEmpty(clientId))
        {
            return 0;
        }

        if (string.Equals(clientId, BlackClientId, StringComparison.Ordinal))
        {
            return 1;
        }

        if (string.Equals(clientId, WhiteClientId, StringComparison.Ordinal))
        {
            return 2;
        }

        return 0;
    }

    private void NotifyLocked()
    {
        foreach (var waiter in _waiters)
        {
            waiter.TrySetResult(true);
        }

        _waiters.Clear();
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(left),
            System.Text.Encoding.UTF8.GetBytes(right));
    }
}

public sealed record MoveRecord(int X, int Y, int Color);

public sealed record MovePoint(int X, int Y);

public sealed record RoomSnapshot(
    string Token,
    string Status,
    int Turn,
    int YourColor,
    bool BlackSeated,
    bool WhiteSeated,
    int Winner,
    string? EndReason,
    int[][] Board,
    MovePoint? LastMove,
    long Deadline,
    long ServerTime,
    int RemainingSeconds,
    long Version,
    int MoveTimeoutSeconds,
    int RematchRequestedBy,
    bool Removed);

public sealed class RoomStore
{
    private readonly ConcurrentDictionary<string, GomokuRoom> _rooms =
        new(StringComparer.Ordinal);

    public int Count => _rooms.Count;

    public GomokuRoom? Find(string? token) =>
        token is not null && _rooms.TryGetValue(token, out var room) ? room : null;

    public bool TryCreate(
        string? token,
        string? hostKey,
        int moveTimeoutSeconds,
        out GomokuRoom? room,
        out string? error)
    {
        room = null;
        error = null;

        if (!IsValidToken(token))
        {
            error = "invalid_token";
            return false;
        }

        if (!IsValidToken(hostKey))
        {
            error = "invalid_host_key";
            return false;
        }

        if (moveTimeoutSeconds is < GomokuRoom.MinMoveTimeoutSeconds or > GomokuRoom.MaxMoveTimeoutSeconds)
        {
            error = "invalid_move_timeout";
            return false;
        }

        var created = new GomokuRoom(token!, hostKey!, moveTimeoutSeconds);
        if (!_rooms.TryAdd(token!, created))
        {
            error = "room_exists";
            return false;
        }

        room = created;
        return true;
    }

    public bool Remove(string? token)
    {
        if (token is null || !_rooms.TryRemove(token, out var room))
        {
            return false;
        }

        room.MarkRemoved();
        return true;
    }

    public void SweepAll()
    {
        foreach (var room in _rooms.Values)
        {
            room.Sweep();
        }
    }

    public static bool IsValidToken(string? token) =>
        token is { Length: 22 } &&
        token.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');
}
