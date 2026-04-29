using LoalNas.Host.Services;
using Microsoft.Extensions.Hosting;
using System.Drawing;
using System.Windows.Forms;

namespace LoalNas.Host.Forms;

public sealed class HostStatusForm : Form
{
	private readonly FileBrowserProcessManager _fileBrowserManager;
	private readonly IHostApplicationLifetime _applicationLifetime;
	private readonly string[] _boundUrls;
	private readonly System.Windows.Forms.Timer _refreshTimer;

	private readonly Label _hostStatusValue = CreateValueLabel();
	private readonly Label _fileBrowserStatusValue = CreateValueLabel();
	private readonly TextBox _bindingUrlsTextBox = CreateReadOnlyTextBox();
	private readonly TextBox _ipv6UrlsTextBox = CreateReadOnlyTextBox();
	private readonly TextBox _fileBrowserBaseUrlTextBox = CreateReadOnlyTextBox();
	private readonly TextBox _sharedRootTextBox = CreateReadOnlyTextBox();

	private bool _closingFromHost;

	public HostStatusForm(
		FileBrowserProcessManager fileBrowserManager,
		IHostApplicationLifetime applicationLifetime,
		IEnumerable<string> boundUrls)
	{
		_fileBrowserManager = fileBrowserManager;
		_applicationLifetime = applicationLifetime;
		_boundUrls = boundUrls.ToArray();

		Text = "loal_NAS Host";
		StartPosition = FormStartPosition.CenterScreen;
		MinimumSize = new Size(860, 620);
		Size = new Size(860, 620);
		MinimizeBox = true;
		MaximizeBox = false;
		ShowIcon = false;

		_refreshTimer = new System.Windows.Forms.Timer
		{
			Interval = 2000
		};

		_refreshTimer.Tick += (_, _) => RefreshStatus();
		_applicationLifetime.ApplicationStopping.Register(CloseFromHostThread);

		BuildLayout();

		Shown += (_, _) =>
		{
			RefreshStatus();
			_refreshTimer.Start();
		};
		FormClosed += (_, _) => _refreshTimer.Stop();
	}

	private void BuildLayout()
	{
		var root = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			Padding = new Padding(16),
			ColumnCount = 2,
			RowCount = 7,
			AutoSize = true
		};

		root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
		root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
		root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
		root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
		root.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
		root.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));
		root.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
		root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
		root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));

		AddRow(root, 0, "宿主状态", _hostStatusValue);
		AddRow(root, 1, "FileBrowser 状态", _fileBrowserStatusValue);
		AddRow(root, 2, "宿主绑定地址", _bindingUrlsTextBox);
		AddRow(root, 3, "可用 IPv6 地址", _ipv6UrlsTextBox);
		AddRow(root, 4, "FileBrowser 地址", _fileBrowserBaseUrlTextBox);
		AddRow(root, 5, "共享目录", _sharedRootTextBox);

		var buttonPanel = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			FlowDirection = FlowDirection.RightToLeft,
			WrapContents = false
		};

		var closeButton = new Button
		{
			Text = "关闭应用",
			AutoSize = true,
			AutoSizeMode = AutoSizeMode.GrowAndShrink,
			Padding = new Padding(12, 6, 12, 6)
		};

		closeButton.Click += (_, _) => Close();

		var minimizeButton = new Button
		{
			Text = "最小化",
			AutoSize = true,
			AutoSizeMode = AutoSizeMode.GrowAndShrink,
			Padding = new Padding(12, 6, 12, 6)
		};

		minimizeButton.Click += (_, _) => WindowState = FormWindowState.Minimized;

		buttonPanel.Controls.Add(closeButton);
		buttonPanel.Controls.Add(minimizeButton);

		root.Controls.Add(new Label(), 0, 6);
		root.Controls.Add(buttonPanel, 1, 6);

		Controls.Add(root);
	}

	private void RefreshStatus()
	{
		_hostStatusValue.Text = "运行中";
		_fileBrowserStatusValue.Text = _fileBrowserManager.IsRunning ? "运行中" : "已停止";
		_bindingUrlsTextBox.Text = string.Join(Environment.NewLine, _boundUrls);
		_fileBrowserBaseUrlTextBox.Text = _fileBrowserManager.BaseAddress.ToString();
		_sharedRootTextBox.Text = _fileBrowserManager.SharedRootPath;

		var ipv6Lines = Ipv6EndpointReporter.GetAvailableAddresses(_boundUrls)
			.Select(endpoint => $"{endpoint.Category}: {endpoint.Url}")
			.ToArray();

		_ipv6UrlsTextBox.Text = ipv6Lines.Length == 0
			? "未检测到可用的非回环 IPv6 地址"
			: string.Join(Environment.NewLine, ipv6Lines);
	}

	private void CloseFromHostThread()
	{
		_closingFromHost = true;

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

		if (_closingFromHost)
		{
			return;
		}
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			_refreshTimer.Dispose();
		}

		base.Dispose(disposing);
	}

	private static void AddRow(TableLayoutPanel root, int rowIndex, string labelText, Control valueControl)
	{
		var label = new Label
		{
			Text = labelText,
			Dock = DockStyle.Fill,
			TextAlign = ContentAlignment.MiddleLeft,
			AutoSize = true,
			Padding = new Padding(0, 8, 0, 0)
		};

		valueControl.Dock = DockStyle.Fill;
		root.Controls.Add(label, 0, rowIndex);
		root.Controls.Add(valueControl, 1, rowIndex);
	}

	private static Label CreateValueLabel()
	{
		return new Label
		{
			AutoSize = true,
			TextAlign = ContentAlignment.MiddleLeft,
			Padding = new Padding(0, 8, 0, 0)
		};
	}

	private static TextBox CreateReadOnlyTextBox()
	{
		return new TextBox
		{
			ReadOnly = true,
			Multiline = true,
			ScrollBars = ScrollBars.Vertical,
			BorderStyle = BorderStyle.FixedSingle,
			BackColor = SystemColors.Window,
			WordWrap = false
		};
	}
}