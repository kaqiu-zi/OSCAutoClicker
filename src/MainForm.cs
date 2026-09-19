using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OSCAutoClicker;

internal sealed class MainForm : Form
{
    private const string ClickAddress = "/input/UseAxisRight";
    private const string GithubUrl = "https://github.com/aeongdesu/OSCAutoClicker";

    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 1;
    private const uint ModNoRepeat = 0x4000;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uPeriod);

    private enum StatusKind { Idle, Stopped, Running, ConnectFailed, CaptureHint }

    private readonly TextBox _host = new() { Text = "127.0.0.1" };
    private readonly TextBox _port = new() { Text = "9000", MaxLength = 5 };
    private readonly NumericUpDown _interval = new() { Minimum = 1, Maximum = 60000, Value = 100 };
    private readonly NumericUpDown _hold = new() { Minimum = 10, Maximum = 5000, Value = 20 };
    private readonly NumericUpDown _jitter = new() { Minimum = 0, Maximum = 10000, Value = 0 };
    private readonly Button _hotkeyButton = new();
    private readonly ComboBox _languageCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly LinkLabel _github = new() { Text = "GitHub" };
    private readonly Label _version = new() { AutoSize = true, ForeColor = SystemColors.GrayText };

    private readonly Label _hostLabel = new();
    private readonly Label _portLabel = new();
    private readonly Label _intervalLabel = new();
    private readonly Label _holdLabel = new();
    private readonly Label _jitterLabel = new();
    private readonly Label _hotkeyLabel = new();
    private readonly Label _languageLabel = new();

    private readonly Label _detailsToggle = new();
    private readonly TableLayoutPanel _details = new();

    private readonly Button _toggle = new();
    private readonly Label _status = new() { AutoSize = false, AutoEllipsis = true };

    private readonly Random _rng = new();
    private readonly Settings _settings = Settings.Load();

    private CancellationTokenSource? _cts;
    private OscSender? _sender;
    private bool _starting;
    private bool _detailsOpen;
    private long _clicks;

    private Keys _hotkey = Keys.None;
    private bool _capturing;

    private AppLanguage _language;
    private StatusKind _statusKind = StatusKind.Idle;
    private StatusKind _statusBeforeCapture = StatusKind.Idle;

    private Strings S => _language.Strings;

    public MainForm()
    {
        _language = Localization.Resolve(_settings.Language);
        _hotkey = (Keys)_settings.Hotkey & Keys.KeyCode;
        RestoreSettings();

        Text = "OSC Auto Clicker";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        BuildDetailsPanel();

        var grid = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 10,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12),
            Dock = DockStyle.Fill,
        };
        for (int i = 0; i < grid.RowCount; i++)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        int row = 0;
        void AddRow(Label label, Control control)
        {
            AddLabeledRow(grid, label, control, row);
            row++;
        }

        string version = typeof(MainForm).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        int metadata = version.IndexOf('+');
        if (metadata >= 0) version = version[..metadata];
        _version.Text = version.Length > 0 ? $"v{version}" : "";
        _version.Margin = new Padding(0, 0, 6, 0);

        _github.AutoSize = true;
        _github.Margin = new Padding(0);
        _github.LinkClicked += (_, _) => OpenGithub();

        var header = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0, 0, 3, 8),
        };
        header.Controls.Add(_version);
        header.Controls.Add(_github);
        grid.Controls.Add(header, 0, row);
        grid.SetColumnSpan(header, 2);
        row++;

        foreach (AppLanguage language in Localization.All)
        {
            _languageCombo.Items.Add(language.NativeName);
        }
        _languageCombo.SelectedIndex = Math.Max(0, Array.IndexOf(Localization.All, _language));
        _languageCombo.SelectedIndexChanged += (_, _) =>
        {
            _language = Localization.All[_languageCombo.SelectedIndex];
            ApplyStrings();
        };
        AddRow(_languageLabel, _languageCombo);

        _detailsToggle.AutoSize = true;
        _detailsToggle.TextAlign = ContentAlignment.MiddleLeft;
        _detailsToggle.Cursor = Cursors.Hand;
        _detailsToggle.Anchor = AnchorStyles.Left;
        _detailsToggle.Margin = new Padding(0, 4, 0, 4);
        _detailsToggle.Click += (_, _) => SetDetailsOpen(!_detailsOpen);
        _detailsToggle.DoubleClick += (_, _) => SetDetailsOpen(!_detailsOpen);
        grid.Controls.Add(_detailsToggle, 0, row);
        grid.SetColumnSpan(_detailsToggle, 2);
        row++;

        grid.Controls.Add(_details, 0, row);
        grid.SetColumnSpan(_details, 2);
        row++;

        AddRow(_intervalLabel, _interval);
        AddRow(_holdLabel, _hold);
        AddRow(_jitterLabel, _jitter);

        _hotkeyButton.Click += (_, _) => BeginCapture();
        _hotkeyButton.Leave += (_, _) => CancelCapture();
        AddRow(_hotkeyLabel, _hotkeyButton);

        _toggle.Width = 150;
        _toggle.Anchor = AnchorStyles.Left;
        _toggle.Margin = new Padding(3, 10, 3, 3);
        _toggle.Click += (_, _) => Toggle();
        grid.Controls.Add(_toggle, 1, row++);

        _status.Width = 1;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _status.Margin = new Padding(3, 4, 3, 0);
        grid.Controls.Add(_status, 0, row);
        grid.SetColumnSpan(_status, 2);

        Controls.Add(grid);
        _status.Height = _status.PreferredHeight;

        _host.TextChanged += (_, _) => UpdateDetailsHeader();
        _port.TextChanged += (_, _) => UpdateDetailsHeader();
        _port.KeyPress += (_, e) =>
        {
            if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true;
        };

        SetDetailsOpen(false);
        ApplyStrings();
    }

    private void RestoreSettings()
    {
        _host.Text = _settings.Host;
        _port.Text = (_settings.Port is >= 1 and <= 65535 ? _settings.Port : 9000).ToString();
        _interval.Value = Clamp(_interval, _settings.Interval);
        _hold.Value = Clamp(_hold, _settings.Hold);
        _jitter.Value = Clamp(_jitter, _settings.Jitter);
    }

    private static decimal Clamp(NumericUpDown control, int value) =>
        Math.Clamp(value, (int)control.Minimum, (int)control.Maximum);

    private void StoreSettings()
    {
        _settings.Host = _host.Text.Trim();
        if (TryGetPort(out int port)) _settings.Port = port;
        _settings.Interval = (int)_interval.Value;
        _settings.Hold = (int)_hold.Value;
        _settings.Jitter = (int)_jitter.Value;
        _settings.Hotkey = (int)_hotkey;
        _settings.Language = _language.Code;
        _settings.Save();
    }

    private static void AddLabeledRow(TableLayoutPanel grid, Label label, Control control, int row)
    {
        control.Width = 150;
        control.Anchor = AnchorStyles.Left;
        label.AutoSize = true;
        label.Anchor = AnchorStyles.Left;
        label.Margin = new Padding(0, 6, 12, 6);
        grid.Controls.Add(label, 0, row);
        grid.Controls.Add(control, 1, row);
    }

    private void BuildDetailsPanel()
    {
        _details.ColumnCount = 2;
        _details.RowCount = 2;
        _details.AutoSize = true;
        _details.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _details.Margin = new Padding(14, 0, 0, 6);
        for (int i = 0; i < _details.RowCount; i++)
        {
            _details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        AddLabeledRow(_details, _hostLabel, _host, 0);
        AddLabeledRow(_details, _portLabel, _port, 1);
    }

    private void ApplyStrings()
    {
        _hostLabel.Text = S.Host;
        _portLabel.Text = S.Port;
        _intervalLabel.Text = S.Interval;
        _holdLabel.Text = S.Hold;
        _jitterLabel.Text = S.Jitter;
        _hotkeyLabel.Text = S.Hotkey;
        _languageLabel.Text = S.Language;

        _hotkeyButton.Text = _capturing ? S.HotkeyPressKey : KeyName(_hotkey);

        UpdateDetailsHeader();
        UpdateToggleText();
        RenderStatus();
    }

    private void OpenGithub()
    {
        try
        {
            Process.Start(new ProcessStartInfo(GithubUrl) { UseShellExecute = true });
            _github.LinkVisited = true;
        }
        catch
        {
        }
    }

    private void SetDetailsOpen(bool open)
    {
        _detailsOpen = open;
        _details.Visible = open;
        UpdateDetailsHeader();
    }

    private void UpdateDetailsHeader()
    {
        string port = TryGetPort(out int parsed) ? parsed.ToString() : "?";

        string host = _host.Text.Trim();
        if (host.Length > 24) host = host[..23] + "…";

        _detailsToggle.Text = _detailsOpen
            ? $"▼ {S.Connection}"
            : $"▶ {S.Connection}  ({host}:{port})";
    }

    private bool TryGetPort(out int port) =>
        int.TryParse(_port.Text.Trim(), out port) && port >= 1 && port <= 65535;

    private void UpdateToggleText() => _toggle.Text = _cts is null ? S.Start : S.Stop;

    private void SetStatus(StatusKind kind)
    {
        _statusKind = kind;
        RenderStatus();
    }

    private void RenderStatus() => _status.Text = _statusKind switch
    {
        StatusKind.Stopped => string.Format(S.StatusStopped, _clicks),
        StatusKind.Running => string.Format(S.StatusRunning, _clicks),
        StatusKind.ConnectFailed => S.StatusConnectFailed,
        StatusKind.CaptureHint => S.HotkeyCaptureHint,
        _ => S.StatusIdle,
    };

    private string KeyName(Keys key) => key switch
    {
        Keys.None => S.HotkeyNone,
        >= Keys.D0 and <= Keys.D9 => ((char)('0' + (key - Keys.D0))).ToString(),
        _ => key.ToString(),
    };

    private void BeginCapture()
    {
        if (_capturing) return;

        _capturing = true;
        UnregisterHotKey(Handle, HotkeyId);
        _hotkeyButton.Text = S.HotkeyPressKey;
        _statusBeforeCapture = _statusKind;
        SetStatus(StatusKind.CaptureHint);
    }

    private void CancelCapture()
    {
        if (_capturing) EndCapture(_hotkey, notifyOnFailure: false);
    }

    private void EndCapture(Keys key, bool notifyOnFailure = true)
    {
        _capturing = false;
        SetStatus(_statusBeforeCapture);
        ApplyHotkey(key, notifyOnFailure);
    }

    private void ApplyHotkey(Keys key, bool notifyOnFailure = true)
    {
        UnregisterHotKey(Handle, HotkeyId);
        _hotkey = Keys.None;

        if (key != Keys.None)
        {
            if (RegisterHotKey(Handle, HotkeyId, ModNoRepeat, (uint)key))
            {
                _hotkey = key;
            }
            else if (notifyOnFailure)
            {
                MessageBox.Show(
                    string.Format(S.HotkeyFailedBody, KeyName(key)),
                    S.HotkeyFailedTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        _hotkeyButton.Text = KeyName(_hotkey);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!_capturing) return base.ProcessCmdKey(ref msg, keyData);

        Keys key = keyData & Keys.KeyCode;
        Keys modifiers = keyData & Keys.Modifiers;

        if (key is Keys.ShiftKey or Keys.ControlKey or Keys.Menu or Keys.LWin or Keys.RWin)
        {
            return true;
        }

        if (key == Keys.F4 && modifiers == Keys.Alt)
        {
            CancelCapture();
            return base.ProcessCmdKey(ref msg, keyData);
        }

        if (key == Keys.Escape)
        {
            EndCapture(_hotkey, notifyOnFailure: false);
            return true;
        }

        if (key is Keys.Back or Keys.Delete)
        {
            EndCapture(Keys.None);
            return true;
        }

        if (modifiers != Keys.None) return true;

        EndCapture(key);
        return true;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyHotkey(_hotkey, notifyOnFailure: false);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        UnregisterHotKey(Handle, HotkeyId);
        _cts?.Cancel();
        ReleaseClick();
        StoreSettings();
        base.OnFormClosing(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && (int)m.WParam == HotkeyId)
        {
            Toggle();
        }
        base.WndProc(ref m);
    }

    private void Toggle()
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            return;
        }

        if (_starting) return;

        _starting = true;
        try
        {
            Start();
        }
        finally
        {
            _starting = false;
        }
    }

    private void Start()
    {
        if (!TryGetPort(out int port))
        {
            SetDetailsOpen(true);
            MessageBox.Show(
                S.PortErrorBody, S.PortErrorTitle,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        OscSender sender;
        try
        {
            sender = new OscSender(_host.Text.Trim(), port);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                string.Format(S.OpenTargetFailed, ex.Message),
                S.ConnectFailedTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            SetStatus(StatusKind.ConnectFailed);
            return;
        }

        if (IPAddress.IsLoopback(sender.Target.Address) && OscSender.NothingListeningOn(sender.Target))
        {
            DialogResult answer = MessageBox.Show(
                string.Format(S.NothingListening, sender.Target),
                S.ConnectFailedTitle,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.Yes)
            {
                sender.Dispose();
                SetStatus(StatusKind.ConnectFailed);
                return;
            }
        }

        _clicks = 0;
        _sender = sender;
        _cts = new CancellationTokenSource();
        _details.Enabled = false;
        UpdateToggleText();
        _ = RunAsync(sender, _cts.Token);
    }

    private void ReleaseClick()
    {
        try
        {
            _sender?.SendInt(ClickAddress, 0);
        }
        catch
        {
        }
    }

    private async Task RunAsync(OscSender sender, CancellationToken ct)
    {
        timeBeginPeriod(1);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int hold = (int)_hold.Value;
                int interval = (int)_interval.Value;
                int jitter = (int)_jitter.Value;

                sender.SendInt(ClickAddress, 1);
                await Task.Delay(hold, ct);
                sender.SendInt(ClickAddress, 0);

                _clicks++;
                if (!_capturing) SetStatus(StatusKind.Running);

                int offset = jitter > 0 ? _rng.Next(-jitter, jitter + 1) : 0;
                int wait = interval + offset - hold;
                if (wait > 0) await Task.Delay(wait, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException ex)
        {
            MessageBox.Show(
                string.Format(S.SendFailed, ex.SocketErrorCode),
                S.ConnectFailedTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message, S.SendErrorTitle,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            timeEndPeriod(1);

            ReleaseClick();
            _sender = null;
            sender.Dispose();

            _cts?.Dispose();
            _cts = null;

            if (!IsDisposed)
            {
                _details.Enabled = true;
                UpdateToggleText();
                _statusBeforeCapture = StatusKind.Stopped;
                if (!_capturing) SetStatus(StatusKind.Stopped);
            }
        }
    }
}
