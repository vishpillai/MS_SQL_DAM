using System.Diagnostics;
using System.Net.Http.Json;
using System.ServiceProcess;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SqlDam.Desktop;

public sealed class MainForm : Form
{
    private const string DefaultBaseUrl = "http://127.0.0.1:5055";
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(3) };

    private WebView2? _webView;
    private Panel _topBar = null!;
    private Panel _loadingPanel = null!;
    private Label _loadingLabel = null!;
    private Button _btnRetry = null!;
    private Button _btnStartService = null!;
    private Button _btnOpenBrowser = null!;

    private Button _btnBack = null!;
    private Button _btnForward = null!;
    private Button _btnRefresh = null!;
    private Button _btnHome = null!;
    private Button _btnAlerts = null!;
    private Button _btnActivity = null!;
    private Button _btnSources = null!;
    private Button _btnAudit = null!;
    private Label _statusLabel = null!;

    private NotifyIcon _notifyIcon = null!;
    private ContextMenuStrip _trayMenu = null!;
    private System.Windows.Forms.Timer _healthTimer = null!;

    private bool _isServiceOnline = false;
    private bool _webViewInitialized = false;

    public MainForm()
    {
        InitializeUi();
        InitializeTray();
        InitializeHealthTimer();
    }

    private void InitializeUi()
    {
        Text = "SQL DAM - Enterprise Database Activity Monitor";
        Size = new Size(1300, 850);
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(245, 247, 249);

        // 1. Top Navigation Bar
        _topBar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 48,
            BackColor = Color.FromArgb(17, 29, 41),
            Padding = new Padding(8, 6, 8, 6)
        };

        var flowLeft = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent
        };

        _btnBack = CreateNavButton("◀", "Back", (s, e) => { if (_webView?.CanGoBack == true) _webView.GoBack(); });
        _btnForward = CreateNavButton("▶", "Forward", (s, e) => { if (_webView?.CanGoForward == true) _webView.GoForward(); });
        _btnRefresh = CreateNavButton("↻", "Refresh", (s, e) => { _webView?.Reload(); });

        _btnHome = CreateNavButton("◫ Dashboard", "Dashboard", (s, e) => NavigateTo("/"));
        _btnActivity = CreateNavButton("≋ Activity", "Activity", (s, e) => NavigateTo("/activity"));
        _btnAlerts = CreateNavButton("! Alerts", "Alerts", (s, e) => NavigateTo("/alerts"));
        _btnSources = CreateNavButton("▣ Sources", "SQL Sources", (s, e) => NavigateTo("/sources"));
        _btnAudit = CreateNavButton("≡ Audit Trail", "Audit Trail", (s, e) => NavigateTo("/compliance"));

        flowLeft.Controls.AddRange([_btnBack, _btnForward, _btnRefresh, _btnHome, _btnActivity, _btnAlerts, _btnSources, _btnAudit]);

        var flowRight = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent
        };

        _statusLabel = new Label
        {
            Text = "● Checking Service...",
            ForeColor = Color.FromArgb(245, 158, 11),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(8, 10, 12, 0)
        };

        var btnBrowser = CreateNavButton("↗ Open in Browser", "Open in external browser", (s, e) =>
        {
            var url = _webView?.Source?.ToString() ?? DefaultBaseUrl;
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        });

        flowRight.Controls.AddRange([_statusLabel, btnBrowser]);

        _topBar.Controls.Add(flowLeft);
        _topBar.Controls.Add(flowRight);
        Controls.Add(_topBar);

        // 2. Loading / Offline Panel
        _loadingPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(245, 247, 249)
        };

        var centerBox = new Panel
        {
            Size = new Size(500, 260),
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        centerBox.Location = new Point((Width - centerBox.Width) / 2, (Height - centerBox.Height) / 2 - 40);
        centerBox.Anchor = AnchorStyles.None;

        var lblTitle = new Label
        {
            Text = "SQL DAM Enterprise Control Plane",
            Font = new Font("Segoe UI", 13f, FontStyle.Bold),
            ForeColor = Color.FromArgb(22, 34, 45),
            Location = new Point(24, 24),
            AutoSize = true
        };

        _loadingLabel = new Label
        {
            Text = "Connecting to SQL DAM Windows Service on http://127.0.0.1:5055...\nPlease wait while background monitoring initiates.",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = Color.FromArgb(100, 116, 139),
            Location = new Point(24, 65),
            Size = new Size(450, 48)
        };

        _btnRetry = new Button
        {
            Text = "Retry Connection",
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Size = new Size(140, 36),
            Location = new Point(24, 130),
            BackColor = Color.FromArgb(29, 57, 56),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        _btnRetry.FlatAppearance.BorderSize = 0;
        _btnRetry.Click += async (s, e) => await CheckServiceHealthAsync(userInitiated: true);

        _btnStartService = new Button
        {
            Text = "Start Service",
            Font = new Font("Segoe UI", 9f),
            Size = new Size(130, 36),
            Location = new Point(174, 130),
            BackColor = Color.FromArgb(241, 245, 249),
            ForeColor = Color.FromArgb(30, 41, 59),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        _btnStartService.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
        _btnStartService.Click += (s, e) => TryStartSqlDamService();

        _btnOpenBrowser = new Button
        {
            Text = "Open in Browser",
            Font = new Font("Segoe UI", 9f),
            Size = new Size(130, 36),
            Location = new Point(314, 130),
            BackColor = Color.FromArgb(241, 245, 249),
            ForeColor = Color.FromArgb(30, 41, 59),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        _btnOpenBrowser.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
        _btnOpenBrowser.Click += (s, e) => Process.Start(new ProcessStartInfo(DefaultBaseUrl) { UseShellExecute = true });

        var lblFootnote = new Label
        {
            Text = "Service runs locally on port 5055. Embedded database: App_Data\\sqldam.db",
            Font = new Font("Segoe UI", 8f),
            ForeColor = Color.FromArgb(148, 163, 184),
            Location = new Point(24, 195),
            AutoSize = true
        };

        centerBox.Controls.AddRange([lblTitle, _loadingLabel, _btnRetry, _btnStartService, _btnOpenBrowser, lblFootnote]);
        _loadingPanel.Controls.Add(centerBox);
        Controls.Add(_loadingPanel);

        // 3. Initialize WebView2 container
        try
        {
            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
                Visible = false
            };
            Controls.Add(_webView);
            _webView.BringToFront();
        }
        catch (Exception ex)
        {
            _loadingLabel.Text = $"WebView2 initialization notice: {ex.Message}.\nYou can still access the dashboard directly in your web browser.";
        }
    }

    private Button CreateNavButton(string text, string toolTip, EventHandler onClick)
    {
        var btn = new Button
        {
            Text = text,
            Font = new Font("Segoe UI", 9f, FontStyle.Regular),
            ForeColor = Color.FromArgb(194, 204, 213),
            BackColor = Color.Transparent,
            FlatStyle = FlatStyle.Flat,
            AutoSize = true,
            Height = 32,
            Margin = new Padding(2, 2, 2, 2),
            Cursor = Cursors.Hand,
            Padding = new Padding(8, 2, 8, 2)
        };
        btn.FlatAppearance.BorderSize = 0;
        btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(27, 43, 58);
        btn.Click += onClick;
        return btn;
    }

    private void InitializeTray()
    {
        _trayMenu = new ContextMenuStrip();
        _trayMenu.Items.Add("Open SQL DAM Dashboard", null, (s, e) => RestoreFromTray());
        _trayMenu.Items.Add("View Alerts", null, (s, e) => { RestoreFromTray(); NavigateTo("/alerts"); });
        _trayMenu.Items.Add("View Activity", null, (s, e) => { RestoreFromTray(); NavigateTo("/activity"); });
        _trayMenu.Items.Add("-");
        _trayMenu.Items.Add("Open in Web Browser", null, (s, e) => Process.Start(new ProcessStartInfo(DefaultBaseUrl) { UseShellExecute = true }));
        _trayMenu.Items.Add("Check Service Status", null, async (s, e) => await CheckServiceHealthAsync(userInitiated: true));
        _trayMenu.Items.Add("-");
        _trayMenu.Items.Add("Exit", null, (s, e) => { _notifyIcon.Visible = false; Application.Exit(); });

        _notifyIcon = new NotifyIcon
        {
            Text = "SQL DAM - Database Activity Monitor",
            Icon = SystemIcons.Shield,
            ContextMenuStrip = _trayMenu,
            Visible = true
        };

        _notifyIcon.DoubleClick += (s, e) => RestoreFromTray();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            // Minimize to system tray instead of killing the desktop viewer
            e.Cancel = true;
            Hide();
            _notifyIcon.ShowBalloonTip(2000, "SQL DAM Desktop", "Application minimized to system notification area.", ToolTipIcon.Info);
        }
        else
        {
            _notifyIcon.Visible = false;
            base.OnFormClosing(e);
        }
    }

    private void InitializeHealthTimer()
    {
        _healthTimer = new System.Windows.Forms.Timer
        {
            Interval = 3000
        };
        _healthTimer.Tick += async (s, e) => await CheckServiceHealthAsync(userInitiated: false);
        _healthTimer.Start();

        // Run immediate check
        _ = CheckServiceHealthAsync(userInitiated: false);
    }

    private async Task CheckServiceHealthAsync(bool userInitiated)
    {
        try
        {
            var response = await _httpClient.GetAsync($"{DefaultBaseUrl}/health");
            if (response.IsSuccessStatusCode)
            {
                _isServiceOnline = true;
                _statusLabel.Text = "● Service: Connected";
                _statusLabel.ForeColor = Color.FromArgb(52, 211, 153); // Emerald green

                if (!_webViewInitialized)
                {
                    await InitializeWebViewAsync();
                }

                if (_webView != null)
                {
                    _webView.Visible = true;
                    _loadingPanel.Visible = false;
                }
            }
            else
            {
                SetServiceOffline($"Service returned HTTP {(int)response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            SetServiceOffline(ex.Message);
            if (userInitiated)
            {
                MessageBox.Show($"Could not connect to SQL DAM at {DefaultBaseUrl}.\n\nDetails: {ex.Message}\n\nEnsure the 'SqlDam' service is started.",
                    "Connection Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    private void SetServiceOffline(string reason)
    {
        _isServiceOnline = false;
        _statusLabel.Text = "● Service: Offline";
        _statusLabel.ForeColor = Color.FromArgb(239, 68, 68); // Red

        if (!_webViewInitialized)
        {
            if (_webView != null) _webView.Visible = false;
            _loadingPanel.Visible = true;
            _loadingLabel.Text = $"SQL DAM Windows Service is offline ({reason}).\nClick 'Start Service' or ensure the SqlDam background service is running.";
        }
    }

    private async Task InitializeWebViewAsync()
    {
        if (_webView == null || _webViewInitialized) return;

        try
        {
            var userDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SqlDamDesktop", "WebView2Data");
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await _webView.EnsureCoreWebView2Async(env);

            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;

            _webView.Source = new Uri(DefaultBaseUrl);
            _webViewInitialized = true;
        }
        catch (Exception ex)
        {
            _loadingLabel.Text = $"WebView2 engine could not load: {ex.Message}.\nYou can open the dashboard in your default browser:";
            _loadingPanel.Visible = true;
        }
    }

    private void NavigateTo(string relativePath)
    {
        if (_webViewInitialized && _webView?.CoreWebView2 != null)
        {
            _webView.CoreWebView2.Navigate($"{DefaultBaseUrl}{relativePath}");
        }
        else if (_isServiceOnline)
        {
            Process.Start(new ProcessStartInfo($"{DefaultBaseUrl}{relativePath}") { UseShellExecute = true });
        }
    }

    private void TryStartSqlDamService()
    {
        try
        {
            using var sc = new ServiceController("SqlDam");
            if (sc.Status == ServiceControllerStatus.Stopped)
            {
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(8));
                MessageBox.Show("SQL DAM Service started successfully!", "Service Control", MessageBoxButtons.OK, MessageBoxIcon.Information);
                _ = CheckServiceHealthAsync(userInitiated: true);
            }
            else
            {
                MessageBox.Show($"SqlDam Service status is currently: {sc.Status}", "Service Control", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not start service: {ex.Message}\n\nNote: Starting Windows Services may require administrative permissions.",
                "Service Start Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
