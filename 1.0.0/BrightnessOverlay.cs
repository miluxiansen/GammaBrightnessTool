namespace GammaBrightnessTool;

/// <summary>
/// A temporary overlay window that shows the current brightness level.
/// Features: interactive slider, mouse hover detection to prevent auto-hide.
/// </summary>
public sealed class BrightnessOverlay : Form
{
    private readonly Label _label;
    private readonly System.Windows.Forms.Timer _hideTimer;
    private readonly Panel _progressPanel;
    private readonly Panel _progressFill;
    private readonly Panel _sliderHitArea;  // Invisible hit area for slider interaction
    private bool _isMouseOver;
    private bool _isDragging;
    private int _currentPercentage = 100;

    public event EventHandler<float>? OnBrightnessChanged;

    public BrightnessOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        // DPI-aware sizing: base design for 96 DPI, scale up for higher DPI
        float dpiScale = DeviceDpi / 96.0f;
        int baseWidth = 120;   // Compact width
        int baseHeight = 38;   // Compact height
        Size = new Size((int)(baseWidth * dpiScale), (int)(baseHeight * dpiScale));
        BackColor = Color.FromArgb(32, 32, 32);
        ForeColor = Color.White;
        Opacity = 0.7;  // 70% opacity for the form
        TopMost = true;

        // Apply rounded corners
        ApplyRoundedCorners(8);

        // Calculate scaled dimensions - compact layout
        int margin = (int)(6 * dpiScale);
        int topPadding = (int)(1 * dpiScale);  // Minimal top padding
        int labelHeight = (int)(20 * dpiScale);  // Reduced label height
        int gap = (int)(2 * dpiScale);  // Minimal gap
        int barHeight = Math.Max(3, (int)(4 * dpiScale));
        int clientWidth = ClientSize.Width;
        int contentWidth = clientWidth - margin * 2;

        // Percentage label
        int fontSize = Math.Max(7, (int)(7 * dpiScale));  // Even smaller font
        _label = new Label
        {
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", fontSize, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "100%",
            AutoSize = false,
            Location = new Point(margin, topPadding),
            Size = new Size(contentWidth, labelHeight)
        };

        // Progress bar background
        int barY = topPadding + labelHeight + gap;
        _progressPanel = new Panel
        {
            BackColor = Color.FromArgb(80, 255, 255, 255),
            Location = new Point(margin, barY),
            Size = new Size(contentWidth, barHeight)
        };

        _progressFill = new Panel
        {
            Dock = DockStyle.Left,
            Width = (int)(100 * dpiScale),
            BackColor = Color.White
        };

        _progressPanel.Controls.Add(_progressFill);

        // Slider hit area (invisible, captures mouse events for dragging)
        _sliderHitArea = new Panel
        {
            BackColor = Color.Transparent,
            Location = new Point(margin, barY - (int)(4 * dpiScale)),  // Slightly larger than visual bar
            Size = new Size(contentWidth, barHeight + (int)(8 * dpiScale)),
            Cursor = Cursors.Hand
        };

        // Mouse event handlers for slider interaction
        _sliderHitArea.MouseDown += SliderHitArea_MouseDown;
        _sliderHitArea.MouseMove += SliderHitArea_MouseMove;
        _sliderHitArea.MouseUp += SliderHitArea_MouseUp;
        _sliderHitArea.MouseWheel += SliderHitArea_MouseWheel;

        // Also add mouse events to progress panel (visual bar)
        _progressPanel.MouseDown += SliderHitArea_MouseDown;
        _progressPanel.MouseMove += SliderHitArea_MouseMove;
        _progressPanel.MouseUp += SliderHitArea_MouseUp;
        _progressPanel.MouseWheel += SliderHitArea_MouseWheel;
        _progressFill.MouseDown += SliderHitArea_MouseDown;
        _progressFill.MouseMove += SliderHitArea_MouseMove;
        _progressFill.MouseUp += SliderHitArea_MouseUp;
        _progressFill.MouseWheel += SliderHitArea_MouseWheel;

        Controls.Add(_label);
        Controls.Add(_progressPanel);
        Controls.Add(_sliderHitArea);

        // Mouse hover detection for all controls
        SubscribeMouseEvents(this);

        // Auto-hide timer
        _hideTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        _hideTimer.Tick += (s, e) =>
        {
            if (_isMouseOver || _isDragging)
            {
                // Reset timer if mouse is still over or dragging
                _hideTimer.Stop();
                _hideTimer.Start();
                return;
            }
            Hide();
            _hideTimer.Stop();
        };
    }

    private void SubscribeMouseEvents(Control control)
    {
        control.MouseEnter += (s, e) => _isMouseOver = true;
        control.MouseLeave += (s, e) => CheckMouseLeave();

        // Recursively subscribe for child controls
        foreach (Control child in control.Controls)
        {
            SubscribeMouseEvents(child);
        }
    }

    private void CheckMouseLeave()
    {
        // Check if mouse is still within the form bounds
        var clientPos = PointToClient(Cursor.Position);
        _isMouseOver = ClientRectangle.Contains(clientPos);
    }

    private void SliderHitArea_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _isDragging = true;
            UpdateBrightnessFromMouse(e.X);
        }
    }

    private void SliderHitArea_MouseMove(object? sender, MouseEventArgs e)
    {
        if (_isDragging)
        {
            // Calculate relative X position within the slider hit area
            var sliderPos = _sliderHitArea.PointToClient(Cursor.Position);
            UpdateBrightnessFromMouse(sliderPos.X);
        }
    }

    private void SliderHitArea_MouseUp(object? sender, MouseEventArgs e)
    {
        _isDragging = false;
    }

    private void SliderHitArea_MouseWheel(object? sender, MouseEventArgs e)
    {
        // Scroll wheel on slider: adjust brightness by steps
        int delta = Math.Sign(e.Delta) * 5;  // 5% per wheel step
        int newPercentage = Math.Max(10, Math.Min(100, _currentPercentage + delta));
        UpdateBrightness(newPercentage);
    }

    private void UpdateBrightnessFromMouse(int mouseX)
    {
        float ratio = Math.Max(0f, Math.Min(1f, (float)mouseX / _sliderHitArea.Width));
        int percentage = (int)(ratio * 100);
        percentage = Math.Max(10, Math.Min(100, percentage));  // Clamp to 10-100%
        UpdateBrightness(percentage);
    }

    private void UpdateBrightness(int percentage)
    {
        if (percentage == _currentPercentage) return;

        _currentPercentage = percentage;
        _label.Text = $"{percentage}%";

        // Update progress bar visual
        int fillWidth = (int)(_progressPanel.Width * (percentage / 100.0));
        _progressFill.Width = Math.Max(1, fillWidth);

        // Notify brightness change
        OnBrightnessChanged?.Invoke(this, percentage / 100f);
    }

    public void Show(float brightness)
    {
        if (InvokeRequired)
        {
            Invoke(new Action<float>(Show), brightness);
            return;
        }

        _currentPercentage = (int)(brightness * 100);
        _label.Text = $"{_currentPercentage}%";

        // Recalculate layout if DPI changed
        float dpiScale = DeviceDpi / 96.0f;
        int margin = (int)(6 * dpiScale);
        int topPadding = (int)(1 * dpiScale);  // Minimal top padding
        int labelHeight = (int)(20 * dpiScale);  // Reduced label height
        int gap = (int)(2 * dpiScale);  // Minimal gap
        int barHeight = Math.Max(3, (int)(4 * dpiScale));
        int contentWidth = ClientSize.Width - margin * 2;
        int barY = topPadding + labelHeight + gap;

        _label.Location = new Point(margin, topPadding);
        _label.Size = new Size(contentWidth, labelHeight);
        _label.Font = new Font("Segoe UI", Math.Max(7, (int)(7 * dpiScale)), FontStyle.Bold);

        _progressPanel.Location = new Point(margin, barY);
        _progressPanel.Size = new Size(contentWidth, barHeight);

        _sliderHitArea.Location = new Point(margin, barY - (int)(4 * dpiScale));
        _sliderHitArea.Size = new Size(contentWidth, barHeight + (int)(8 * dpiScale));

        int fillWidth = (int)(_progressPanel.Width * (_currentPercentage / 100.0));
        _progressFill.Width = Math.Max(1, fillWidth);

        // Position: centered above taskbar
        var cursorPos = Cursor.Position;
        var screen = Screen.FromPoint(cursorPos);
        var workingArea = screen.WorkingArea;

        int osdX = workingArea.Left + (workingArea.Width - Width) / 2;
        int osdY = workingArea.Bottom - Height - (int)(10 * dpiScale);

        osdX = Math.Max(workingArea.Left, Math.Min(osdX, workingArea.Right - Width));
        osdY = Math.Max(workingArea.Top, Math.Min(osdY, workingArea.Bottom - Height));

        Location = new Point(osdX, osdY);

        if (!Visible)
        {
            base.Show();
        }

        _hideTimer.Stop();
        _hideTimer.Start();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var exStyle = NativeMethods.GetWindowLong(Handle, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(Handle, NativeMethods.GWL_EXSTYLE, exStyle | NativeMethods.WS_EX_NOACTIVATE);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    private void ApplyRoundedCorners(int radius)
    {
        // Create a GraphicsPath for rounded rectangle
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        int diameter = radius * 2;
        var rect = new Rectangle(0, 0, Width, Height);

        // Top-left arc
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        // Top-right arc
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        // Bottom-right arc
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        // Bottom-left arc
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();

        Region = new Region(path);
    }
}

