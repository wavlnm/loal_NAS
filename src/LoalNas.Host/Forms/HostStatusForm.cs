using LoalNas.Host.Services;
using Microsoft.Extensions.Hosting;
using System.Drawing;
using System.Net;
using System.Windows.Forms;

namespace LoalNas.Host.Forms;

public sealed class HostStatusForm : Form
{
	// ── 配色 ────────────────────────────────────────────────────────────────
	private static readonly Color CBg          = Color.FromArgb(248, 249, 250);
	private static readonly Color CCard        = Color.White;
	private static readonly Color CBorder      = Color.FromArgb(222, 226, 230);
	private static readonly Color CTextPrimary = Color.FromArgb(33,  37,  41);
	private static readonly Color CTextMuted   = Color.FromArgb(108, 117, 125);
	private static readonly Color CAccent      = Color.FromArgb(13,  110, 253);
	private static readonly Color CSuccess     = Color.FromArgb(25,  135, 84);
	private static readonly Color CDanger      = Color.FromArgb(220, 53,  69);
	private static readonly Color CWarning     = Color.FromArgb(180, 130, 0);

	// ── 布局常量 ─────────────────────────────────────────────────────────────
	private const int LeftW  = 240;   // 左栏控件宽度
	private const int RightW = 540;   // 右栏控件宽度（保守值，实际可用 ~552px）

	// ── 依赖 ─────────────────────────────────────────────────────────────────
	private readonly FileBrowserProcessManager _fileBrowserManager;
	private readonly IHostApplicationLifetime  _applicationLifetime;
	private readonly ConnectedDeviceTracker    _deviceTracker;
	private readonly string[]                  _boundUrls;
	private readonly System.Windows.Forms.Timer _refreshTimer;

	// ── 网络信息（启动时读取一次） ───────────────────────────────────────────
	private readonly IPAddress?            _stableIpv6;
	private readonly IReadOnlyList<IPAddress> _lanIpv4;
	private ConnectivityState              _ipv6State;

	// ── 需要动态刷新的控件 ───────────────────────────────────────────────────
	private Label  _ipv6StatusLabel  = null!;
	private Label  _storageTextLabel = null!;
	private Panel  _storageFillPanel = null!;
	private Panel  _storageBarBg     = null!;
	private Label  _devicesLabel     = null!;

	private enum ConnectivityState { Testing, Ready, NotReady, NoAddress }

	public HostStatusForm(
		FileBrowserProcessManager fileBrowserManager,
		IHostApplicationLifetime  applicationLifetime,
		ConnectedDeviceTracker    deviceTracker,
		IEnumerable<string>       boundUrls)
	{
		_fileBrowserManager = fileBrowserManager;
		_applicationLifetime = applicationLifetime;
		_deviceTracker       = deviceTracker;
		_boundUrls           = boundUrls.ToArray();

		_stableIpv6 = NetworkInfoService.GetStablePublicIpv6();
		_lanIpv4    = NetworkInfoService.GetLanIpv4Addresses();
		_ipv6State  = _stableIpv6 is null ? ConnectivityState.NoAddress : ConnectivityState.Testing;

		InitializeComponent();

		_refreshTimer = new System.Windows.Forms.Timer { Interval = 4000 };
		_refreshTimer.Tick += (_, _) => RefreshDynamic();
		_applicationLifetime.ApplicationStopping.Register(CloseFromHostThread);

		Shown     += OnShown;
		FormClosed += (_, _) => _refreshTimer.Stop();
	}

	private async void OnShown(object? sender, EventArgs e)
	{
		RefreshDynamic();
		_refreshTimer.Start();
		await TestIpv6ConnectivityAsync();
	}

	// ── 界面构建 ─────────────────────────────────────────────────────────────
	private void InitializeComponent()
	{
		Text            = "loal_NAS";
		StartPosition   = FormStartPosition.CenterScreen;
		ClientSize      = new Size(840, 500);
		FormBorderStyle = FormBorderStyle.FixedSingle;
		MaximizeBox     = false;
		MinimizeBox     = true;
		ShowIcon        = false;
		BackColor       = CBg;
		Font            = new Font("Segoe UI", 9f);

		var table = new TableLayoutPanel
		{
			Dock        = DockStyle.Fill,
			ColumnCount = 2,
			RowCount    = 1,
			Padding     = new Padding(24, 20, 24, 20),
			BackColor   = CBg,
		};
		table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LeftW));
		table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
		table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
		table.Controls.Add(BuildLeftPanel(),  0, 0);
		table.Controls.Add(BuildRightPanel(), 1, 0);
		Controls.Add(table);
	}

	// ── 左栏：二维码占位 + 共享目录 ─────────────────────────────────────────
	private Panel BuildLeftPanel()
	{
		var panel = new Panel { Dock = DockStyle.Fill, BackColor = CBg };

		// 标题
		var title = new Label
		{
			Text      = "loal_NAS",
			Font      = new Font("Segoe UI", 16f, FontStyle.Bold),
			ForeColor = CTextPrimary,
			AutoSize  = true,
			Location  = new Point(0, 0),
		};

		// 二维码占位卡片
		var qrCard = new Panel
		{
			Width     = LeftW,
			Height    = LeftW,
			BackColor = CCard,
			Location  = new Point(0, 44),
		};
		qrCard.Paint += (_, e) =>
		{
			using var pen = new Pen(CBorder);
			e.Graphics.DrawRectangle(pen, 0, 0, qrCard.Width - 1, qrCard.Height - 1);
		};
		var qrPlaceholder = new Label
		{
			Text      = "二维码\n（云端接口就绪后\n自动生成）",
			ForeColor = CTextMuted,
			TextAlign = ContentAlignment.MiddleCenter,
			Dock      = DockStyle.Fill,
		};
		qrCard.Controls.Add(qrPlaceholder);

		var qrHint = new Label
		{
			Text      = "手机端扫码快速连接",
			ForeColor = CTextMuted,
			Font      = new Font("Segoe UI", 8f),
			AutoSize  = true,
			Location  = new Point(0, 44 + LeftW + 6),
		};

		// 共享目录卡片
		var dirCard = BuildCard();
		dirCard.Location = new Point(0, 44 + LeftW + 30);
		dirCard.Height   = 90;
		dirCard.Width    = LeftW;

		var dirCaption = MakeCaption("云空间根目录");
		dirCaption.Location = new Point(12, 10);

		var dirPath = new Label
		{
			Text      = _fileBrowserManager.SharedRootPath,
			ForeColor = CTextPrimary,
			Font      = new Font("Segoe UI", 8.5f),
			AutoSize  = false,
			Width     = LeftW - 24,
			Height    = 48,
			Location  = new Point(12, 32),
		};

		var copyBtn = new Button
		{
			Text      = "复制",
			FlatStyle = FlatStyle.Flat,
			Font      = new Font("Segoe UI", 8f),
			Width     = 40,
			Height    = 22,
			Location  = new Point(LeftW - 52, 8),
			ForeColor = CAccent,
			BackColor = CCard,
		};
		copyBtn.FlatAppearance.BorderSize = 0;
		copyBtn.Click += (_, _) => Clipboard.SetText(_fileBrowserManager.SharedRootPath);

		dirCard.Controls.AddRange(new Control[] { dirCaption, dirPath, copyBtn });

		// 关闭按钮
		var closeBtn = new Button
		{
			Text      = "关闭应用",
			Width     = LeftW,
			Height    = 34,
			Location  = new Point(0, 44 + LeftW + 136),
			BackColor = CDanger,
			ForeColor = Color.White,
			FlatStyle = FlatStyle.Flat,
			Font      = new Font("Segoe UI", 9f),
		};
		closeBtn.FlatAppearance.BorderSize = 0;
		closeBtn.Click += (_, _) => Close();

		panel.Controls.AddRange(new Control[] { title, qrCard, qrHint, dirCard, closeBtn });
		return panel;
	}

	// ── 右栏：网络地址 + 存储 + 已连接设备 ──────────────────────────────────
	private Panel BuildRightPanel()
	{
		var panel = new Panel { Dock = DockStyle.Fill, BackColor = CBg, Padding = new Padding(20, 0, 0, 0) };

		int x = 20;

		// ── 网络地址卡片 ────────────────────────────────────────────────────
		var netCard = BuildCard();
		netCard.Location = new Point(x, 0);
		netCard.Width    = RightW;
		netCard.Height   = 164;

		netCard.Controls.Add(MakeCaption("网络地址", 12, 10));

		int ny = 36;
		// 公网 IPv6
		var ipv6Row = BuildAddressRow(
			"公网 IPv6",
			_stableIpv6?.ToString() ?? "未检测到稳定公网 IPv6 地址",
			12, ny);
		_ipv6StatusLabel = (Label)ipv6Row.Controls[2];
		netCard.Controls.Add(ipv6Row);
		ny += 44;

		// 局域网 IPv4（可能多个）
		if (_lanIpv4.Count == 0)
		{
			var row = BuildAddressRow("局域网 IPv4", "未检测到局域网地址", 12, ny);
			netCard.Controls.Add(row);
		}
		else
		{
			foreach (var ip in _lanIpv4.Take(2))
			{
				var row = BuildAddressRow("局域网 IPv4", ip.ToString(), 12, ny);
				netCard.Controls.Add(row);
				ny += 44;
			}
		}

		// ── 存储卡片 ────────────────────────────────────────────────────────
		var stgCard = BuildCard();
		stgCard.Location = new Point(x, 180);
		stgCard.Width    = RightW;
		stgCard.Height   = 100;
		stgCard.Controls.Add(MakeCaption("存储空间", 12, 10));

		_storageTextLabel = new Label
		{
			Text      = "读取中…",
			ForeColor = CTextMuted,
			Font      = new Font("Segoe UI", 8.5f),
			AutoSize  = true,
			Location  = new Point(12, 34),
		};

		_storageBarBg = new Panel
		{
			BackColor = CBorder,
			Height    = 6,
			Width     = RightW - 24,
			Location  = new Point(12, 60),
		};
		_storageFillPanel = new Panel
		{
			BackColor = CAccent,
			Height    = 6,
			Width     = 0,
			Location  = new Point(0, 0),
		};
		_storageBarBg.Controls.Add(_storageFillPanel);

		stgCard.Controls.AddRange(new Control[] { _storageTextLabel, _storageBarBg });

		// ── 已连接设备卡片 ───────────────────────────────────────────────────
		var devCard = BuildCard();
		devCard.Location = new Point(x, 296);
		devCard.Width    = RightW;
		devCard.Height   = 164;
		devCard.Controls.Add(MakeCaption("最近连接设备", 12, 10));

		_devicesLabel = new Label
		{
			Text      = "暂无连接记录",
			ForeColor = CTextMuted,
			Font      = new Font("Segoe UI", 8.5f),
			AutoSize  = false,
			Width     = RightW - 24,
			Height    = 120,
			Location  = new Point(12, 34),
		};
		devCard.Controls.Add(_devicesLabel);

		panel.Controls.AddRange(new Control[] { netCard, stgCard, devCard });
		return panel;
	}

	// ── 动态刷新 ─────────────────────────────────────────────────────────────
	private void RefreshDynamic()
	{
		// 存储
		try
		{
			var root      = Path.GetFullPath(_fileBrowserManager.SharedRootPath);
			var driveRoot = Path.GetPathRoot(root);
			if (driveRoot != null)
			{
				var drive = new DriveInfo(driveRoot);
				if (drive.IsReady)
				{
					double total = drive.TotalSize;
					double free  = drive.TotalFreeSpace;
					double used  = total - free;
					int pct      = (int)(used / total * 100);
					_storageTextLabel.Text      = $"{FormatBytes(used)} / {FormatBytes(total)} 已使用  ({pct}%)";
					_storageFillPanel.Width     = (int)(_storageBarBg.Width * pct / 100.0);
					_storageFillPanel.BackColor = pct > 90 ? CDanger : pct > 70 ? CWarning : CAccent;
				}
			}
		}
		catch { /* 忽略 */ }

		// 已连接设备
		var devices = _deviceTracker.GetActiveDevices();
		if (devices.Count == 0)
		{
			_devicesLabel.Text      = "暂无连接记录（最近 5 分钟内无请求）";
			_devicesLabel.ForeColor = CTextMuted;
		}
		else
		{
			var lines = devices.Select(d =>
			{
				var ago = DateTimeOffset.UtcNow - d.LastSeen;
				var agoText = ago.TotalSeconds < 60
					? $"{(int)ago.TotalSeconds} 秒前"
					: $"{(int)ago.TotalMinutes} 分钟前";
				return $"{d.IpAddress}  （{agoText}）";
			});
			_devicesLabel.Text      = string.Join(Environment.NewLine, lines);
			_devicesLabel.ForeColor = CTextPrimary;
		}
	}

	// ── 公网 IPv6 连通性测试 ──────────────────────────────────────────────────
	private async Task TestIpv6ConnectivityAsync()
	{
		if (_stableIpv6 is null)
		{
			SetIpv6Badge(ConnectivityState.NoAddress);
			return;
		}

		SetIpv6Badge(ConnectivityState.Testing);
		try
		{
			// 用宿主绑定的端口对本机 IPv6 发一次 HTTP GET
			var port    = GetBoundPort();
			var testUrl = $"http://[{_stableIpv6}]:{port}/api/system/status";
			using var client = new System.Net.Http.HttpClient
			{
				Timeout = TimeSpan.FromSeconds(6)
			};
			var resp = await client.GetAsync(testUrl);
			SetIpv6Badge(resp.IsSuccessStatusCode
				? ConnectivityState.Ready
				: ConnectivityState.NotReady);
		}
		catch
		{
			SetIpv6Badge(ConnectivityState.NotReady);
		}
	}

	private void SetIpv6Badge(ConnectivityState state)
	{
		_ipv6State = state;
		if (_ipv6StatusLabel.InvokeRequired)
		{
			_ipv6StatusLabel.BeginInvoke(() => SetIpv6Badge(state));
			return;
		}
		(_ipv6StatusLabel.Text, _ipv6StatusLabel.ForeColor) = state switch
		{
			ConnectivityState.Testing   => ("测试中…",  CTextMuted),
			ConnectivityState.Ready     => ("已就绪",   CSuccess),
			ConnectivityState.NotReady  => ("未就绪",   CDanger),
			ConnectivityState.NoAddress => ("无地址",   CTextMuted),
			_                           => ("",          CTextMuted),
		};
	}

	private int GetBoundPort()
	{
		foreach (var url in _boundUrls)
		{
			if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
				return uri.Port;
		}
		return 5034;
	}

	// ── 卡片和控件工厂 ────────────────────────────────────────────────────────
	private static Panel BuildCard()
	{
		var p = new Panel { BackColor = CCard };
		p.Paint += (_, e) =>
		{
			using var pen = new Pen(CBorder);
			e.Graphics.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1);
		};
		return p;
	}

	private static Label MakeCaption(string text, int x = 0, int y = 0)
	{
		return new Label
		{
			Text      = text,
			Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
			ForeColor = CTextPrimary,
			AutoSize  = true,
			Location  = new Point(x, y),
		};
	}

	/// <summary>
	/// 一行地址：左侧灰色标签 + 中间地址 + 右侧状态徽章（仅 IPv6 行使用）。
	/// </summary>
	private static Panel BuildAddressRow(string caption, string address, int x, int y)
	{
		var row = new Panel
		{
			Width     = RightW - 24,
			Height    = 40,
			Location  = new Point(x, y),
			BackColor = CCard,
		};

		var cap = new Label
		{
			Text      = caption,
			ForeColor = CTextMuted,
			Font      = new Font("Segoe UI", 8f),
			AutoSize  = true,
			Location  = new Point(0, 2),
		};

		var addr = new Label
		{
			Text      = address,
			ForeColor = CTextPrimary,
			Font      = new Font("Segoe UI", 9f),
			AutoSize  = true,
			Location  = new Point(0, 20),
		};

		// 右侧徽章（用于 IPv6 就绪状态；非 IPv6 行留空 Label 占位）
		var badge = new Label
		{
			Text      = "",
			ForeColor = CTextMuted,
			Font      = new Font("Segoe UI", 8f),
			AutoSize  = true,
			Location  = new Point(RightW - 100, 2),
		};

		row.Controls.AddRange(new Control[] { cap, addr, badge });
		return row;
	}

	private static string FormatBytes(double bytes)
	{
		return bytes switch
		{
			>= 1_099_511_627_776 => $"{bytes / 1_099_511_627_776:F1} TB",
			>= 1_073_741_824     => $"{bytes / 1_073_741_824:F1} GB",
			>= 1_048_576         => $"{bytes / 1_048_576:F0} MB",
			_                    => $"{bytes / 1024:F0} KB",
		};
	}

	// ── 生命周期 ──────────────────────────────────────────────────────────────
	private void CloseFromHostThread()
	{
		if (!IsHandleCreated || IsDisposed) return;
		if (InvokeRequired) BeginInvoke(new Action(Close));
		else Close();
	}

	protected override void OnFormClosing(FormClosingEventArgs e)
	{
		_refreshTimer.Stop();
		base.OnFormClosing(e);
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing) _refreshTimer.Dispose();
		base.Dispose(disposing);
	}
}