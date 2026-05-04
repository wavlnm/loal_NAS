using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net;
using System.Reflection;
using System.Windows.Forms;
using LoalNas.Host.Services;
using Microsoft.Extensions.Hosting;
using QRCoder;

namespace LoalNas.Host.Forms;

public sealed class HostStatusForm : Form
{
	private static readonly Color CBg = Color.FromArgb(246, 248, 252);
	private static readonly Color CCard = Color.White;
	private static readonly Color CBorder = Color.FromArgb(230, 234, 242);
	private static readonly Color CTextPrimary = Color.FromArgb(28, 32, 40);
	private static readonly Color CTextMuted = Color.FromArgb(114, 124, 138);
	private static readonly Color CAccent = Color.FromArgb(49, 107, 255);
	private static readonly Color CAccentSoft = Color.FromArgb(239, 245, 255);
	private static readonly Color CSuccess = Color.FromArgb(26, 162, 104);
	private static readonly Color CSuccessSoft = Color.FromArgb(232, 249, 240);
	private static readonly Color CWarning = Color.FromArgb(201, 127, 39);
	private static readonly Color CWarningSoft = Color.FromArgb(255, 244, 230);
	private static readonly Color CDanger = Color.FromArgb(210, 78, 91);
	private static readonly Color CDangerSoft = Color.FromArgb(253, 239, 242);
	private const int LeftW = 550;
	private const int RightW = 600;

	private readonly FileBrowserProcessManager _fileBrowserManager;
	private readonly IHostApplicationLifetime _applicationLifetime;
	private readonly ConnectedDeviceTracker _deviceTracker;
	private readonly DeviceIdentityService _deviceIdentity;
	private readonly string[] _boundUrls;
	private readonly System.Windows.Forms.Timer _refreshTimer;
	private IPAddress? _stableIpv6;
	private string? _lastReportedIpv6;   // 上次成功上报到云端的地址，用于检测变化时立即同步
	private IReadOnlyList<IPAddress> _lanIpv4;
	private int _refreshInFlight;

	private Label _ipv6StatusLabel = null!;
	private Label _ipv6AddressLabel = null!;
	private Button _ipv6CopyButton = null!;
	private readonly List<Label> _lanIpv4AddressLabels = new();
	private readonly List<Button> _lanIpv4CopyButtons = new();
	private Label _storageUsageLabel = null!;
	private Label _storagePercentLabel = null!;
	private Panel _storageFillPanel = null!;
	private Panel _storageBarBg = null!;
	private Image? _deviceBindingQrImage;
	private FlowLayoutPanel _devicesListPanel = null!;
	private readonly Dictionary<string, (Panel Panel, Label AgoLabel)> _deviceItemViews = new();
	private Panel? _emptyDeviceStatePanel;
	private Label _cloudSyncStatusLabel = null!;
	private int _syncTickCount;

	// ── 注册状态机 ────────────────────────────────────────────────────────────
	private enum RegistrationState { Unregistered, Registering, Registered }
	private RegistrationState _regState = RegistrationState.Unregistered;
	private string? _regUsername;
	private Panel _unregisteredPanel = null!;
	private Panel _registeringPanel = null!;
	private Panel _registeredPanel = null!;
	private Label _regUsernameValueLabel = null!;
	private Label _regDeviceNameValueLabel = null!;
	private System.Windows.Forms.Timer? _registrationPollTimer;
	private DateTime _registrationStartTime;

	private enum ConnectivityState { Testing, Ready, NotReady, NoAddress }

	public HostStatusForm(
		FileBrowserProcessManager fileBrowserManager,
		IHostApplicationLifetime applicationLifetime,
		ConnectedDeviceTracker deviceTracker,
		DeviceIdentityService deviceIdentity,
		IEnumerable<string> boundUrls)
	{
		_fileBrowserManager = fileBrowserManager;
		_applicationLifetime = applicationLifetime;
		_deviceTracker = deviceTracker;
		_deviceIdentity = deviceIdentity;
		_boundUrls = boundUrls.ToArray();

		_stableIpv6 = NetworkInfoService.GetStablePublicIpv6();
		_lanIpv4 = NetworkInfoService.GetLanIpv4Addresses();

		InitializeComponent();

		_refreshTimer = new System.Windows.Forms.Timer { Interval = 4000 };
		_refreshTimer.Tick += async (_, _) => await RefreshDynamicAsync();
		_applicationLifetime.ApplicationStopping.Register(CloseFromHostThread);

		Shown += OnShown;
		FormClosed += (_, _) => _refreshTimer.Stop();
	}

	private async void OnShown(object? sender, EventArgs e)
	{
		await RefreshDynamicAsync(forceCloudSync: true);
		_refreshTimer.Start();
	}

	private void InitializeComponent()
	{
		Text = "千私云";
		StartPosition = FormStartPosition.CenterScreen;
		ClientSize = new Size(1230, 770);
		MinimumSize = new Size(1230, 770);
		FormBorderStyle = FormBorderStyle.FixedSingle;
		MaximizeBox = false;
		MinimizeBox = true;
		ShowIcon = false;
		BackColor = CBg;
		Font = new Font("Segoe UI", 9f);

		var root = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			BackColor = CBg,
			Padding = new Padding(24, 24, 24, 24),
			RowCount = 2,
			ColumnCount = 1,
		};
		root.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
		root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

		var body = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			BackColor = CBg,
			ColumnCount = 2,
			Margin = new Padding(0),
		};
		body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LeftW + 32));
		body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, RightW));
		body.Controls.Add(BuildLeftColumn(), 0, 0);
		body.Controls.Add(BuildRightColumn(), 1, 0);

		root.Controls.Add(BuildHeader(), 0, 0);
		root.Controls.Add(body, 0, 1);
		Controls.Add(root);
	}

	private Panel BuildHeader()
	{
		var panel = new Panel { Dock = DockStyle.Fill, BackColor = CBg, Margin = new Padding(0, 0, 0, 12) };

		var textFlow = new FlowLayoutPanel
		{
			FlowDirection = FlowDirection.TopDown,
			AutoSize = true,
			AutoSizeMode = AutoSizeMode.GrowAndShrink,
			BackColor = CBg,
			WrapContents = false,
			Location = new Point(0, 0),
			Margin = new Padding(0),
			Padding = new Padding(0),
		};

		var title = new Label
		{
			Text = "千私云",
			Font = new Font("Segoe UI", 20f, FontStyle.Bold),
			ForeColor = CTextPrimary,
			AutoSize = true,
			Margin = new Padding(0, 0, 0, 2),
		};

		var subtitle = new Label
		{
			Text = "你的电脑，随时可用的私有云存储中心",
			Font = new Font("Segoe UI", 10f),
			ForeColor = CTextMuted,
			AutoSize = true,
			Margin = new Padding(0),
		};

		textFlow.Controls.AddRange(new Control[] { title, subtitle });

		var openWebButton = CreateOutlineButton("访问 Web 管理", 168, 40);
		openWebButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
		openWebButton.Location = new Point(1022, 8);
		openWebButton.Click += (_, _) => OpenUrl(_fileBrowserManager.BaseAddress.ToString());

		panel.Controls.AddRange(new Control[] { textFlow, openWebButton });
		return panel;
	}

	private Panel BuildLeftColumn()
	{
		var panel = new Panel { Dock = DockStyle.Fill, BackColor = CBg, Margin = new Padding(0) };

		var deviceCard = BuildDeviceCard();
		deviceCard.Location = new Point(0, 0);

		var storageCard = BuildCard(LeftW, 252);
		storageCard.Location = new Point(0, 374);
		storageCard.Controls.Add(MakeSectionTitle("云空间根目录", "该目录与可用空间信息会实时刷新", 20, 20));

		var pathBox = new Panel
		{
			Location = new Point(20, 92),
			Size = new Size(510, 60),
			BackColor = CAccentSoft,
		};
		ApplyRoundedRegion(pathBox, 16);
		pathBox.Paint += (_, e) => DrawRoundedBorder(e, pathBox.ClientRectangle, 16, Color.FromArgb(215, 228, 255));

		var pathLabel = new Label
		{
			Text = _fileBrowserManager.SharedRootPath,
			ForeColor = CTextPrimary,
			Font = new Font("Segoe UI", 9f),
			AutoEllipsis = true,
			Size = new Size(432, 36),
			Location = new Point(16, 13),
			TextAlign = ContentAlignment.MiddleLeft,
		};

		var copyPathButton = CreateIconButton();
		copyPathButton.Location = new Point(462, 14);
		copyPathButton.Click += (_, _) => CopyToClipboard(_fileBrowserManager.SharedRootPath);

		pathBox.Controls.AddRange(new Control[] { pathLabel, copyPathButton });
		storageCard.Controls.Add(pathBox);

		_storageUsageLabel = new Label
		{
			Text = "读取中...",
			ForeColor = CTextPrimary,
			Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
			AutoSize = false,
			Size = new Size(446, 24),
			Location = new Point(20, 170),
		};

		_storagePercentLabel = new Label
		{
			Text = "0%",
			ForeColor = CTextMuted,
			Font = new Font("Segoe UI", 9f),
			AutoSize = true,
			Location = new Point(494, 170),
		};

		_storageBarBg = new Panel
		{
			Location = new Point(20, 202),
			Size = new Size(510, 10),
			BackColor = Color.FromArgb(234, 238, 244),
		};
		ApplyRoundedRegion(_storageBarBg, 5);

		_storageFillPanel = new Panel
		{
			Location = new Point(0, 0),
			Size = new Size(0, 10),
			BackColor = CAccent,
		};
		ApplyRoundedRegion(_storageFillPanel, 5);
		_storageBarBg.Controls.Add(_storageFillPanel);
		storageCard.Controls.AddRange(new Control[] { _storageUsageLabel, _storagePercentLabel, _storageBarBg });

		panel.Controls.AddRange(new Control[] { deviceCard, storageCard });
		return panel;
	}

	private Panel BuildRightColumn()
	{
		var panel = new Panel { Dock = DockStyle.Fill, BackColor = CBg, Margin = new Padding(0) };

		var networkCard = BuildCard(RightW, 350);
		networkCard.Location = new Point(0, 0);
		networkCard.Controls.Add(MakeSectionTitle("设备地址", "设备地址可能发生改变，建议登录客户端以实时同步最新地址。", 20, 20));

		int top = 92;
		var ipv6Row = BuildAddressRow(
			"公网 IPv6 地址",
			_stableIpv6?.ToString() ?? "未检测到稳定公网 IPv6 地址",
			_stableIpv6?.ToString(),
			true,
			(valueLabel, copyButton) =>
			{
				_ipv6AddressLabel = valueLabel;
				_ipv6CopyButton = copyButton;
			});
		ipv6Row.Location = new Point(20, top);
		networkCard.Controls.Add(ipv6Row);
		top += 70;

		for (int index = 0; index < 2; index++)
		{
			var address = _lanIpv4.ElementAtOrDefault(index)?.ToString();
			var row = BuildAddressRow(
				"局域网地址",
				address ?? (index == 0 ? "未检测到局域网 IPv4 地址" : "暂无第二个局域网 IPv4 地址"),
				address,
				false,
				(valueLabel, copyButton) =>
				{
					_lanIpv4AddressLabels.Add(valueLabel);
					_lanIpv4CopyButtons.Add(copyButton);
				});
			row.Location = new Point(20, top);
			networkCard.Controls.Add(row);
			top += 70;
		}

		_cloudSyncStatusLabel = new Label
		{
			Text = "",
			ForeColor = CTextMuted,
			Font = new Font("Segoe UI", 8.5f),
			AutoSize = false,
			Size = new Size(RightW - 40, 20),
			Location = new Point(20, 306),
		};
		networkCard.Controls.Add(_cloudSyncStatusLabel);

		var devicesCard = BuildCard(RightW, 252);
		devicesCard.Location = new Point(0, 374);
		devicesCard.Controls.Add(MakeSectionTitle("最近连接设备", "显示最近 5 分钟内有访问记录的客户端", 20, 20));

		_devicesListPanel = new FlowLayoutPanel
		{
			Location = new Point(20, 88),
			Size = new Size(RightW - 40, 128),
			FlowDirection = FlowDirection.TopDown,
			WrapContents = false,
			AutoScroll = true,
			BackColor = CCard,
			Margin = new Padding(0),
			Padding = new Padding(0),
		};
		EnableDoubleBuffered(_devicesListPanel);
		devicesCard.Controls.Add(_devicesListPanel);

		panel.Controls.AddRange(new Control[] { networkCard, devicesCard });
		return panel;
	}

	private async Task RefreshDynamicAsync(bool forceCloudSync = false)
	{
		if (Interlocked.Exchange(ref _refreshInFlight, 1) == 1)
		{
			return;
		}

		try
		{
			RefreshStorageUsage();
			RefreshDevicesList();

			// 网络刷新单独捕获，失败时仅更新徽章，不阻断后续的 tick 计数和云同步
			try
			{
				await RefreshServiceAndNetworkAsync();
			}
			catch
			{
				SetIpv6Badge(ConnectivityState.NotReady);
			}

			_syncTickCount++;
			if (forceCloudSync || _syncTickCount >= 8)
			{
				_syncTickCount = 0;
				await SyncDeviceToCloudAsync(); // 内部已捕获所有异常，失败后下次仍会在 32s 后重试
			}
		}
		finally
		{
			Interlocked.Exchange(ref _refreshInFlight, 0);
		}
	}

	private void RefreshStorageUsage()
	{
		try
		{
			var root = Path.GetFullPath(_fileBrowserManager.SharedRootPath);
			var driveRoot = Path.GetPathRoot(root);
			if (driveRoot != null)
			{
				var drive = new DriveInfo(driveRoot);
				if (drive.IsReady)
				{
					double total = drive.TotalSize;
					double used = total - drive.TotalFreeSpace;
					int pct = total > 0 ? (int)Math.Round(used / total * 100, MidpointRounding.AwayFromZero) : 0;
					_storageUsageLabel.Text = $"已使用 {FormatBytes(used)} / {FormatBytes(total)}";
					_storagePercentLabel.Text = $"{pct}%";
					_storageFillPanel.Width = Math.Max(0, Math.Min(_storageBarBg.Width, (int)Math.Round(_storageBarBg.Width * pct / 100d)));
					_storageFillPanel.BackColor = pct >= 90 ? CDanger : pct >= 70 ? CWarning : CAccent;
				}
			}
		}
		catch
		{
			_storageUsageLabel.Text = "无法读取磁盘空间信息";
			_storagePercentLabel.Text = "--";
			_storageFillPanel.Width = 0;
		}
	}

	private async Task RefreshServiceAndNetworkAsync()
	{
		if (!_fileBrowserManager.IsRunning)
		{
			try
			{
				await _fileBrowserManager.EnsureRunningAsync(CancellationToken.None);
			}
			catch
			{
				SetIpv6Badge(ConnectivityState.NotReady);
				return;
			}
		}

		RefreshNetworkAddressSnapshot();
		await TestIpv6ConnectivityAsync();
	}

	private async Task SyncDeviceToCloudAsync()
	{
		var ipv6 = _stableIpv6?.ToString();
		if (string.IsNullOrEmpty(ipv6))
		{
			_cloudSyncStatusLabel.Text = "暂无公网 IPv6 地址，跳过同步";
			_cloudSyncStatusLabel.ForeColor = CTextMuted;
			// 未检测到地址时也缩短到 8s 重试，而非 32s
			_syncTickCount = 6;
			return;
		}

		var (success, username, deviceName) = await TrySyncDeviceToCloudAsync();
		if (success)
		{
			_lastReportedIpv6 = _stableIpv6?.ToString();
			_cloudSyncStatusLabel.Text = $"地址已同步至云端 · {DateTime.Now:HH:mm:ss}";
			_cloudSyncStatusLabel.ForeColor = CSuccess;

			// 首次同步成功说明设备已注册，切换到已注册界面
			if (_regState == RegistrationState.Unregistered)
			{
				ApplyRegisteredState(username, deviceName);
			}
		}
		else
		{
			_cloudSyncStatusLabel.Text = $"地址同步失败（设备可能尚未注册）";
			_cloudSyncStatusLabel.ForeColor = CTextMuted;
			// 有地址但上报失败，8s 后重试（而非 32s）
			_syncTickCount = 6;
		}
	}

	/// <summary>向云端同步当前 IPv6 地址，返回是否成功及注册用户信息。</summary>
	private async Task<(bool Success, string? Username, string? DeviceName)> TrySyncDeviceToCloudAsync()
	{
		var ipv6 = _stableIpv6?.ToString();
		if (string.IsNullOrEmpty(ipv6))
			return (false, null, null);

		try
		{
			var payload = System.Text.Json.JsonSerializer.Serialize(new
			{
				deviceId = _deviceIdentity.DeviceId,
				deviceName = _deviceIdentity.DeviceName,
				ipv6Address = ipv6,
			});
			// UseProxy = false：云端同步走直连，不受系统代理影响
			using var handler = new System.Net.Http.HttpClientHandler { UseProxy = false };
			using var client = new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
			using var content = new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8, "application/json");
			var response = await client.PutAsync("https://reportzs.me/nas-api/devices/by-device-id", content);
			var body = await response.Content.ReadAsStringAsync();

			bool success = false;
			string? username = null;
			string? deviceName = null;
			try
			{
				using var doc = System.Text.Json.JsonDocument.Parse(body);
				if (doc.RootElement.TryGetProperty("success", out var s))
					success = s.GetBoolean();

				if (success && doc.RootElement.TryGetProperty("user", out var user))
				{
					if (user.TryGetProperty("username", out var u)) username = u.GetString();
					if (user.TryGetProperty("deviceName", out var dn)) deviceName = dn.GetString();
				}
			}
			catch { }

			return (success, username, deviceName);
		}
		catch
		{
			return (false, null, null);
		}
	}

	private void RefreshNetworkAddressSnapshot()
	{
		var previous = _stableIpv6?.ToString();
		_stableIpv6 = NetworkInfoService.GetStablePublicIpv6();
		var current  = _stableIpv6?.ToString();

		// IPv6 地址发生变化（包括 null→有值），立即触发云端同步
		if (current != previous && !string.IsNullOrEmpty(current))
		{
			_syncTickCount = 8; // 下一个 tick 立即同步
		}

		_lanIpv4 = NetworkInfoService.GetLanIpv4Addresses();

		_ipv6AddressLabel.Text = _stableIpv6?.ToString() ?? "未检测到稳定公网 IPv6 地址";
		_ipv6CopyButton.Enabled = _stableIpv6 is not null;
		_ipv6CopyButton.Tag = _stableIpv6?.ToString();

		for (int index = 0; index < _lanIpv4AddressLabels.Count; index++)
		{
			var address = _lanIpv4.ElementAtOrDefault(index)?.ToString();
			_lanIpv4AddressLabels[index].Text = address ?? (index == 0 ? "未检测到局域网 IPv4 地址" : "暂无第二个局域网 IPv4 地址");
			_lanIpv4CopyButtons[index].Enabled = !string.IsNullOrWhiteSpace(address);
			_lanIpv4CopyButtons[index].Tag = address;
		}
	}

	private void RefreshDevicesList()
	{
		_devicesListPanel.SuspendLayout();

		var devices = _deviceTracker.GetActiveDevices().Take(5).ToList();
		if (devices.Count == 0)
		{
			foreach (var view in _deviceItemViews.Values)
			{
				_devicesListPanel.Controls.Remove(view.Panel);
				view.Panel.Dispose();
			}
			_deviceItemViews.Clear();

			_emptyDeviceStatePanel ??= (Panel)BuildEmptyDeviceState();
			if (_devicesListPanel.Controls.Count != 1 || _devicesListPanel.Controls[0] != _emptyDeviceStatePanel)
			{
				_devicesListPanel.Controls.Clear();
				_devicesListPanel.Controls.Add(_emptyDeviceStatePanel);
			}

			_devicesListPanel.ResumeLayout();
			return;
		}

		if (_emptyDeviceStatePanel is not null)
		{
			_devicesListPanel.Controls.Remove(_emptyDeviceStatePanel);
		}

		var activeIps = new HashSet<string>(devices.Select(d => d.IpAddress));
		var staleIps = _deviceItemViews.Keys.Where(ip => !activeIps.Contains(ip)).ToList();
		foreach (var staleIp in staleIps)
		{
			var view = _deviceItemViews[staleIp];
			_devicesListPanel.Controls.Remove(view.Panel);
			view.Panel.Dispose();
			_deviceItemViews.Remove(staleIp);
		}

		var desiredPanels = new List<Panel>(devices.Count);
		foreach (var device in devices)
		{
			var elapsed = DateTimeOffset.UtcNow - device.LastSeen;
			var agoText = elapsed.TotalSeconds < 60
				? $"{Math.Max(1, (int)elapsed.TotalSeconds)} 秒前"
				: $"{Math.Max(1, (int)elapsed.TotalMinutes)} 分钟前";

			if (!_deviceItemViews.TryGetValue(device.IpAddress, out var view))
			{
				view = BuildDeviceItem(device.IpAddress, agoText);
				_deviceItemViews[device.IpAddress] = view;
			}
			else
			{
				view.AgoLabel.Text = $"最近活动: {agoText}";
			}

			desiredPanels.Add(view.Panel);
		}

		var orderChanged = _devicesListPanel.Controls.Count != desiredPanels.Count;
		if (!orderChanged)
		{
			for (int i = 0; i < desiredPanels.Count; i++)
			{
				if (_devicesListPanel.Controls[i] != desiredPanels[i])
				{
					orderChanged = true;
					break;
				}
			}
		}

		if (orderChanged)
		{
			_devicesListPanel.Controls.Clear();
			foreach (var panel in desiredPanels)
			{
				_devicesListPanel.Controls.Add(panel);
			}
		}

		_devicesListPanel.ResumeLayout();
	}

	private async Task TestIpv6ConnectivityAsync()
	{
		if (!await IsHostReachableLocallyAsync())
		{
			SetIpv6Badge(ConnectivityState.NotReady);
			return;
		}

		if (_stableIpv6 is null)
		{
			SetIpv6Badge(ConnectivityState.NoAddress);
			return;
		}

		SetIpv6Badge(ConnectivityState.Testing);
		try
		{
			var port = GetBoundPort();
			var testUrl = $"http://[{_stableIpv6}]:{port}/api/system/status";
			using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
			var response = await client.GetAsync(testUrl);
			SetIpv6Badge(response.IsSuccessStatusCode ? ConnectivityState.Ready : ConnectivityState.NotReady);
		}
		catch
		{
			SetIpv6Badge(ConnectivityState.NotReady);
		}
	}

	private async Task<bool> IsHostReachableLocallyAsync()
	{
		var port = GetBoundPort();
		foreach (var localUrl in new[]
		{
			$"http://[::1]:{port}/api/system/status",
			$"http://127.0.0.1:{port}/api/system/status"
		})
		{
			try
			{
				using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) };
				using var response = await client.GetAsync(localUrl);
				if (response.IsSuccessStatusCode)
				{
					return true;
				}
			}
			catch
			{
			}
		}

		return false;
	}

	private void SetIpv6Badge(ConnectivityState state)
	{
		if (_ipv6StatusLabel.InvokeRequired)
		{
			_ipv6StatusLabel.BeginInvoke(() => SetIpv6Badge(state));
			return;
		}

		var (text, foreColor, backColor) = state switch
		{
			ConnectivityState.Testing => ("测试中", CTextMuted, Color.FromArgb(243, 245, 248)),
			ConnectivityState.Ready => ("已就绪", CSuccess, CSuccessSoft),
			ConnectivityState.NotReady => ("未就绪", CDanger, CDangerSoft),
			ConnectivityState.NoAddress => ("无地址", CTextMuted, Color.FromArgb(243, 245, 248)),
			_ => (string.Empty, CTextMuted, Color.FromArgb(243, 245, 248)),
		};

		_ipv6StatusLabel.Text = text;
		_ipv6StatusLabel.ForeColor = foreColor;
		_ipv6StatusLabel.BackColor = backColor;
	}

	private int GetBoundPort()
	{
		foreach (var url in _boundUrls)
		{
			if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
			{
				return uri.Port;
			}
		}

		return 5034;
	}

	// ══════════════════════════════════════════════════════════════════════════
	// 设备卡片（状态机驱动：未注册 → 注册中 → 已注册）
	// ══════════════════════════════════════════════════════════════════════════

	private Panel BuildDeviceCard()
	{
		var card = BuildCard(LeftW, 350);

		// ── ① 未注册 panel ─────────────────────────────────────────────────
		_unregisteredPanel = new Panel
		{
			Location = new Point(0, 0),
			Size = new Size(LeftW, 350),
			BackColor = Color.Transparent,
			Visible = true,
		};
		_unregisteredPanel.Controls.Add(MakeSectionTitle("设备注册", "绑定账号后可通过手机客户端随时访问此设备", 20, 20));

		var descLabel = new Label
		{
			Text = "注册后，你的手机客户端可通过扫码与此设备完成绑定。\n\n"
				 + "注册仅记录设备 ID 和网络地址，不会上传任何文件。\n"
				 + "若你已有账号，请直接在手机端登录，无需再次注册。",
			ForeColor = CTextMuted,
			Font = new Font("Segoe UI", 9.5f),
			Location = new Point(24, 92),
			Size = new Size(LeftW - 48, 140),
			AutoSize = false,
		};

		var registerButton = CreateFilledButton("立即注册（显示二维码）", LeftW - 48, 44);
		registerButton.Location = new Point(24, 280);
		registerButton.Click += (_, _) => StartRegistration();

		_unregisteredPanel.Controls.AddRange(new Control[] { descLabel, registerButton });

		// ── ② 注册中 panel ─────────────────────────────────────────────────
		_registeringPanel = new Panel
		{
			Location = new Point(0, 0),
			Size = new Size(LeftW, 350),
			BackColor = Color.Transparent,
			Visible = false,
		};
		_registeringPanel.Controls.Add(MakeSectionTitle("设备绑定", "使用手机客户端扫码注册", 20, 20));

		var qrShell = new Panel
		{
			Location = new Point(24, 92),
			Size = new Size(168, 168),
			BackColor = Color.White,
			Padding = new Padding(12),
		};
		ApplyRoundedRegion(qrShell, 22);
		qrShell.Paint += (_, e) => DrawRoundedBorder(e, qrShell.ClientRectangle, 22, CBorder);

		try
		{
			_deviceBindingQrImage = CreateDeviceBindingQrImage();
			var qrPictureBox = new PictureBox
			{
				Dock = DockStyle.Fill,
				BackColor = Color.White,
				Image = _deviceBindingQrImage,
				SizeMode = PictureBoxSizeMode.Zoom,
				TabStop = false,
			};
			qrShell.Controls.Add(qrPictureBox);
		}
		catch
		{
			var qrFallback = new Label
			{
				Text = "二维码生成失败\n请复制设备 ID 手动绑定",
				ForeColor = CTextMuted,
				Dock = DockStyle.Fill,
				TextAlign = ContentAlignment.MiddleCenter,
				Font = new Font("Segoe UI", 10f, FontStyle.Bold),
			};
			qrShell.Controls.Add(qrFallback);
		}

		_registeringPanel.Controls.Add(qrShell);
		_registeringPanel.Controls.Add(BuildInfoPair("设备名称", _deviceIdentity.DeviceName, 222, 102, 304, 34, 13f, false));
		_registeringPanel.Controls.Add(BuildInfoPair("设备 ID", _deviceIdentity.DeviceId, 222, 174, 260, 56, 10.2f, true));

		var copyDeviceIdButton = CreateIconButton();
		copyDeviceIdButton.Location = new Point(490, 201);
		copyDeviceIdButton.Click += (_, _) => CopyToClipboard(_deviceIdentity.DeviceId);

		var waitLabel = new Label
		{
			Text = "等待手机扫码... (3 分钟后自动取消)",
			ForeColor = CTextMuted,
			Font = new Font("Segoe UI", 8.8f),
			Location = new Point(222, 274),
			AutoSize = true,
		};

		var cancelButton = CreateOutlineButton("取消", 100, 32);
		cancelButton.Location = new Point(LeftW - 120, 10);
		cancelButton.Click += (_, _) => CancelRegistration();

		_registeringPanel.Controls.AddRange(new Control[] { copyDeviceIdButton, waitLabel, cancelButton });

		// ── ③ 已注册 panel ─────────────────────────────────────────────────
		_registeredPanel = new Panel
		{
			Location = new Point(0, 0),
			Size = new Size(LeftW, 350),
			BackColor = Color.Transparent,
			Visible = false,
		};
		_registeredPanel.Controls.Add(MakeSectionTitle("设备绑定", "账号与设备已绑定", 20, 20));

		var successBadge = new Label
		{
			Text = "✓ 已注册",
			ForeColor = CSuccess,
			BackColor = CSuccessSoft,
			Font = new Font("Segoe UI", 9f, FontStyle.Bold),
			Location = new Point(24, 92),
			AutoSize = false,
			Size = new Size(100, 28),
			TextAlign = ContentAlignment.MiddleCenter,
		};
		ApplyRoundedRegion(successBadge, 8);

		_regUsernameValueLabel = new Label
		{
			Text = "—",
			ForeColor = CTextPrimary,
			Font = new Font("Segoe UI", 18f, FontStyle.Bold),
			Location = new Point(24, 136),
			AutoSize = true,
		};

		var regUsernameCaptionLabel = new Label
		{
			Text = "账号",
			ForeColor = CTextMuted,
			Font = new Font("Segoe UI", 8.8f),
			Location = new Point(24, 120),
			AutoSize = true,
		};

		_regDeviceNameValueLabel = new Label
		{
			Text = _deviceIdentity.DeviceName,
			ForeColor = CTextPrimary,
			Font = new Font("Segoe UI", 13f, FontStyle.Bold),
			Location = new Point(24, 210),
			AutoSize = true,
		};

		var regDeviceNameCaptionLabel = new Label
		{
			Text = "设备名称",
			ForeColor = CTextMuted,
			Font = new Font("Segoe UI", 8.8f),
			Location = new Point(24, 194),
			AutoSize = true,
		};

		var openWebMiniButton = CreateFilledButton("打开管理页", LeftW - 48, 38);
		openWebMiniButton.Location = new Point(24, 280);
		openWebMiniButton.Click += (_, _) => OpenUrl(_fileBrowserManager.BaseAddress.ToString());

		_registeredPanel.Controls.AddRange(new Control[] {
			successBadge, regUsernameCaptionLabel, _regUsernameValueLabel,
			regDeviceNameCaptionLabel, _regDeviceNameValueLabel, openWebMiniButton,
		});

		card.Controls.AddRange(new Control[] { _unregisteredPanel, _registeringPanel, _registeredPanel });
		return card;
	}

	private void SwitchRegistrationPanel(RegistrationState state)
	{
		if (InvokeRequired) { BeginInvoke(() => SwitchRegistrationPanel(state)); return; }
		_regState = state;
		_unregisteredPanel.Visible = state == RegistrationState.Unregistered;
		_registeringPanel.Visible  = state == RegistrationState.Registering;
		_registeredPanel.Visible   = state == RegistrationState.Registered;
	}

	private void StartRegistration()
	{
		SwitchRegistrationPanel(RegistrationState.Registering);
		_registrationStartTime = DateTime.UtcNow;

		_registrationPollTimer?.Stop();
		_registrationPollTimer?.Dispose();
		_registrationPollTimer = new System.Windows.Forms.Timer { Interval = 2000 };
		_registrationPollTimer.Tick += async (_, _) => await RegistrationPollTickAsync();
		_registrationPollTimer.Start();
	}

	private void CancelRegistration()
	{
		_registrationPollTimer?.Stop();
		_registrationPollTimer?.Dispose();
		_registrationPollTimer = null;
		SwitchRegistrationPanel(RegistrationState.Unregistered);
	}

	private async Task RegistrationPollTickAsync()
	{
		// 3 分钟超时
		if ((DateTime.UtcNow - _registrationStartTime).TotalMinutes >= 3)
		{
			_registrationPollTimer?.Stop();
			_registrationPollTimer?.Dispose();
			_registrationPollTimer = null;
			SwitchRegistrationPanel(RegistrationState.Unregistered);
			return;
		}

		var (success, username, deviceName) = await TrySyncDeviceToCloudAsync();
		if (success)
		{
			_registrationPollTimer?.Stop();
			_registrationPollTimer?.Dispose();
			_registrationPollTimer = null;
			ApplyRegisteredState(username, deviceName);
		}
	}

	private void ApplyRegisteredState(string? username, string? deviceName)
	{
		if (InvokeRequired) { BeginInvoke(() => ApplyRegisteredState(username, deviceName)); return; }
		_regUsername = username;
		_regUsernameValueLabel.Text = username ?? "—";
		_regDeviceNameValueLabel.Text = deviceName ?? _deviceIdentity.DeviceName;
		SwitchRegistrationPanel(RegistrationState.Registered);
	}

	private static Panel BuildCard(int width, int height)
	{
		var panel = new Panel
		{
			Size = new Size(width, height),
			BackColor = CCard,
			Margin = new Padding(0),
		};
		ApplyRoundedRegion(panel, 24);
		panel.Paint += (_, e) => DrawRoundedBorder(e, panel.ClientRectangle, 24, CBorder);
		return panel;
	}

	private static Control MakeSectionTitle(string title, string subtitle, int x, int y)
	{
		var flow = new FlowLayoutPanel
		{
			Location = new Point(x, y),
			FlowDirection = FlowDirection.TopDown,
			AutoSize = true,
			AutoSizeMode = AutoSizeMode.GrowAndShrink,
			WrapContents = false,
			BackColor = Color.Transparent,
			Margin = new Padding(0),
			Padding = new Padding(0),
		};

		var titleLabel = new Label
		{
			Text = title,
			ForeColor = CTextPrimary,
			Font = new Font("Segoe UI", 15f, FontStyle.Bold),
			AutoSize = true,
			Margin = new Padding(0, 0, 0, 1),
		};

		var subtitleLabel = new Label
		{
			Text = subtitle,
			ForeColor = CTextMuted,
			Font = new Font("Segoe UI", 9f),
			AutoSize = true,
			Margin = new Padding(0),
		};

		flow.Controls.AddRange(new Control[] { titleLabel, subtitleLabel });
		return flow;
	}

	private static Control BuildInfoPair(string caption, string value, int x, int y, int width, int valueHeight, float valueFontSize, bool multiline)
	{
		var wrapper = new Panel
		{
			Location = new Point(x, y),
			Size = new Size(width, valueHeight + 22),
			BackColor = Color.Transparent,
		};

		var captionLabel = new Label
		{
			Text = caption,
			ForeColor = CTextMuted,
			Font = new Font("Segoe UI", 8.8f),
			AutoSize = true,
			Location = new Point(0, 0),
		};

		var valueLabel = new Label
		{
			Text = value,
			ForeColor = CTextPrimary,
			Font = new Font("Segoe UI", valueFontSize, FontStyle.Bold),
			AutoEllipsis = !multiline,
			Size = new Size(width, valueHeight),
			Location = new Point(0, 22),
			TextAlign = ContentAlignment.TopLeft,
		};

		wrapper.Controls.AddRange(new Control[] { captionLabel, valueLabel });
		return wrapper;
	}

	private Panel BuildAddressRow(string caption, string address, string? copyValue, bool withStatusBadge, Action<Label, Button>? bindControls = null)
	{
		var row = new Panel
		{
			Size = new Size(RightW - 40, 58),
			BackColor = Color.FromArgb(250, 251, 253),
		};
		ApplyRoundedRegion(row, 18);
		row.Paint += (_, e) => DrawRoundedBorder(e, row.ClientRectangle, 18, CBorder);

		var captionLabel = new Label
		{
			Text = caption,
			ForeColor = CTextMuted,
			Font = new Font("Segoe UI", 8.5f),
			AutoSize = true,
			Location = new Point(14, 10),
		};

		var copyButton = CreateIconButton();
		copyButton.Location = new Point(row.Width - copyButton.Width - 12, 13);
		copyButton.Enabled = !string.IsNullOrWhiteSpace(copyValue);
		copyButton.Click += (_, _) =>
		{
			var latestValue = copyButton.Tag as string ?? copyValue;
			if (!string.IsNullOrWhiteSpace(latestValue))
			{
				CopyToClipboard(latestValue);
			}
		};
		copyButton.Tag = copyValue;

		var badgeWidth = withStatusBadge ? 72 : 0;
		var statusBadge = CreateStatusBadge();
		statusBadge.Location = new Point(copyButton.Left - badgeWidth - 10, 14);
		statusBadge.Visible = withStatusBadge;

		if (withStatusBadge)
		{
			_ipv6StatusLabel = statusBadge;
		}

		var valueLabel = new Label
		{
			Text = address,
			ForeColor = CTextPrimary,
			Font = new Font("Segoe UI", 10.2f, FontStyle.Bold),
			AutoEllipsis = true,
			Size = new Size(statusBadge.Left - 30, 24),
			Location = new Point(14, 30),
		};

		if (!withStatusBadge)
		{
			valueLabel.Width = copyButton.Left - 28;
		}

		bindControls?.Invoke(valueLabel, copyButton);

		row.Controls.AddRange(new Control[] { captionLabel, valueLabel, statusBadge, copyButton });
		return row;
	}

	private static Label CreateStatusBadge()
	{
		var label = new Label
		{
			Size = new Size(72, 28),
			TextAlign = ContentAlignment.MiddleCenter,
			Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
			ForeColor = CTextMuted,
			BackColor = Color.FromArgb(243, 245, 248),
		};
		ApplyRoundedRegion(label, 14);
		return label;
	}

	private Control BuildEmptyDeviceState()
	{
		var panel = new Panel
		{
			Size = new Size(RightW - 40, 108),
			BackColor = CAccentSoft,
			Margin = new Padding(0),
		};
		ApplyRoundedRegion(panel, 18);
		panel.Paint += (_, e) => DrawRoundedBorder(e, panel.ClientRectangle, 18, Color.FromArgb(220, 230, 252));

		var text = new Label
		{
			Text = "暂无连接记录\n最近 5 分钟内没有来自客户端的新请求",
			ForeColor = CTextMuted,
			Font = new Font("Segoe UI", 9f),
			Dock = DockStyle.Fill,
			TextAlign = ContentAlignment.MiddleCenter,
		};
		panel.Controls.Add(text);
		return panel;
	}

	private (Panel Panel, Label AgoLabel) BuildDeviceItem(string ipAddress, string agoText)
	{
		var panel = new Panel
		{
			Size = new Size(RightW - 40, 60),
			BackColor = Color.FromArgb(250, 251, 253),
			Margin = new Padding(0, 0, 0, 10),
		};
		ApplyRoundedRegion(panel, 18);
		panel.Paint += (_, e) => DrawRoundedBorder(e, panel.ClientRectangle, 18, CBorder);

		var title = new Label
		{
			Text = ipAddress,
			ForeColor = CTextPrimary,
			Font = new Font("Segoe UI", 10f, FontStyle.Bold),
			AutoSize = true,
			Location = new Point(14, 11),
		};

		var subtitle = new Label
		{
			Text = $"最近活动: {agoText}",
			ForeColor = CTextMuted,
			Font = new Font("Segoe UI", 8.5f),
			AutoSize = true,
			Location = new Point(14, 33),
		};

		var copyButton = CreateIconButton();
		copyButton.Location = new Point(panel.Width - 44, 14);
		copyButton.Click += (_, _) => CopyToClipboard(ipAddress);

		panel.Controls.AddRange(new Control[] { title, subtitle, copyButton });
		return (panel, subtitle);
	}

	private static void EnableDoubleBuffered(Control control)
	{
		typeof(Control)
			.GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)
			?.SetValue(control, true);
	}

	private string CreateDeviceBindingPayload()
	{
		return System.Text.Json.JsonSerializer.Serialize(new
		{
			deviceId = _deviceIdentity.DeviceId,
			deviceName = _deviceIdentity.DeviceName,
			secret = _deviceIdentity.DeviceSecret,
		});
	}

	private Image CreateDeviceBindingQrImage()
	{
		using var generator = new QRCodeGenerator();
		using var qrData = generator.CreateQrCode(CreateDeviceBindingPayload(), QRCodeGenerator.ECCLevel.Q);
		using var qrCode = new QRCode(qrData);
		return qrCode.GetGraphic(8, CTextPrimary, Color.White, drawQuietZones: true);
	}

	private static Button CreateOutlineButton(string text, int width, int height)
	{
		var button = new Button
		{
			Text = text,
			Size = new Size(width, height),
			FlatStyle = FlatStyle.Flat,
			BackColor = Color.White,
			ForeColor = CTextPrimary,
			Font = new Font("Segoe UI", 8.8f),
			UseVisualStyleBackColor = false,
		};
		button.FlatAppearance.BorderSize = 0;
		ApplyRoundedRegion(button, 14);
		button.Paint += (_, e) => DrawRoundedBorder(e, button.ClientRectangle, 14, CBorder);
		return button;
	}

	private static Button CreateFilledButton(string text, int width, int height)
	{
		var button = new Button
		{
			Text = text,
			Size = new Size(width, height),
			FlatStyle = FlatStyle.Flat,
			BackColor = CAccent,
			ForeColor = Color.White,
			Font = new Font("Segoe UI", 8.8f),
			UseVisualStyleBackColor = false,
		};
		button.FlatAppearance.BorderSize = 0;
		ApplyRoundedRegion(button, 14);
		return button;
	}

	private static Button CreateIconButton()
	{
		var button = new Button
		{
			Text = "⧉",
			Size = new Size(32, 32),
			FlatStyle = FlatStyle.Flat,
			BackColor = Color.White,
			ForeColor = CTextMuted,
			Font = new Font("Segoe UI Symbol", 10.5f),
			UseVisualStyleBackColor = false,
			Cursor = Cursors.Hand,
		};
		button.FlatAppearance.BorderSize = 0;
		ApplyRoundedRegion(button, 10);
		button.Paint += (_, e) => DrawRoundedBorder(e, button.ClientRectangle, 10, CBorder);
		return button;
	}

	private static void ApplyRoundedRegion(Control control, int radius)
	{
		void UpdateRegion()
		{
			if (control.Width <= 0 || control.Height <= 0)
			{
				return;
			}

			var previousRegion = control.Region;
			using var path = CreateRoundedPath(new Rectangle(0, 0, control.Width - 1, control.Height - 1), radius);
			control.Region = new Region(path);
			previousRegion?.Dispose();
		}

		control.SizeChanged += (_, _) => UpdateRegion();
		UpdateRegion();
	}

	private static void DrawRoundedBorder(PaintEventArgs e, Rectangle bounds, int radius, Color borderColor)
	{
		e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
		using var path = CreateRoundedPath(new Rectangle(bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1), radius);
		using var pen = new Pen(borderColor);
		e.Graphics.DrawPath(pen, path);
	}

	private static GraphicsPath CreateRoundedPath(Rectangle bounds, int radius)
	{
		var diameter = radius * 2;
		var path = new GraphicsPath();

		if (radius <= 0)
		{
			path.AddRectangle(bounds);
			path.CloseFigure();
			return path;
		}

		path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
		path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
		path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
		path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
		path.CloseFigure();
		return path;
	}

	private static void CopyToClipboard(string text)
	{
		if (string.IsNullOrWhiteSpace(text)) return;
		for (int i = 0; i < 3; i++)
		{
			try
			{
				Clipboard.SetText(text);
				return;
			}
			catch (System.Runtime.InteropServices.ExternalException)
			{
				System.Threading.Thread.Sleep(50);
			}
		}
	}

	private static void OpenUrl(string url)
	{
		try
		{
			Process.Start(new ProcessStartInfo
			{
				FileName = url,
				UseShellExecute = true,
			});
		}
		catch
		{
		}
	}

	private static string FormatBytes(double bytes)
	{
		return bytes switch
		{
			>= 1_099_511_627_776 => $"{bytes / 1_099_511_627_776:F1} TB",
			>= 1_073_741_824 => $"{bytes / 1_073_741_824:F1} GB",
			>= 1_048_576 => $"{bytes / 1_048_576:F0} MB",
			_ => $"{bytes / 1024:F0} KB",
		};
	}

	private void CloseFromHostThread()
	{
		if (!IsHandleCreated || IsDisposed)
		{
			return;
		}

		if (InvokeRequired)
		{
			BeginInvoke(new Action(Close));
		}
		else
		{
			Close();
		}
	}

	protected override void OnFormClosing(FormClosingEventArgs e)
	{
		_refreshTimer.Stop();
		base.OnFormClosing(e);
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			_refreshTimer.Dispose();
			_deviceBindingQrImage?.Dispose();
		}

		base.Dispose(disposing);
	}
}