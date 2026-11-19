// Auto Clicker - a small left-click auto clicker for Windows.
//
// Runs on the .NET Framework 4.x that ships with Windows 10 and 11, so there
// is nothing to install. Written in C# 5 so it also builds with the compiler
// that comes with Windows (see build.bat).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("Auto Clicker")]
[assembly: AssemblyDescription("Left-click auto clicker for Windows")]
[assembly: AssemblyProduct("Auto Clicker")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace AutoClicker
{
    // ------------------------------------------------------------------
    // Win32 interop
    // ------------------------------------------------------------------
    internal static class NativeMethods
    {
        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_NOREPEAT = 0x4000;
        public const uint INPUT_MOUSE = 0;
        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;
        public const uint VK_F1 = 0x70;

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        // MOUSEINPUT is the largest member of the INPUT union, so this layout
        // has the correct size on both 32-bit and 64-bit Windows.
        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public MOUSEINPUT mi;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetProcessDPIAware();

        [DllImport("winmm.dll")]
        public static extern uint timeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll")]
        public static extern uint timeEndPeriod(uint uPeriod);
    }

    // ------------------------------------------------------------------
    // Clicking engine (runs on a background thread)
    // ------------------------------------------------------------------
    internal sealed class ClickJob
    {
        public int IntervalMs;
        public bool DoubleClick;
        public int RepeatCount;          // 0 = repeat until stopped
        public bool UseFixedPosition;
        public int X;
        public int Y;
        public int StartDelayMs;
    }

    internal sealed class Clicker
    {
        private static readonly int InputSize = Marshal.SizeOf(typeof(NativeMethods.INPUT));

        private readonly ManualResetEvent stopSignal = new ManualResetEvent(false);
        private Thread thread;
        private int clicks;
        private long clickingStartsAtTicks;

        public event EventHandler Finished;

        public int Clicks
        {
            get { return Thread.VolatileRead(ref clicks); }
        }

        public DateTime ClickingStartsAtUtc
        {
            get { return new DateTime(Interlocked.Read(ref clickingStartsAtTicks), DateTimeKind.Utc); }
        }

        public void Start(ClickJob job)
        {
            // A previous run may still be in its last few instructions.
            Thread old = thread;
            if (old != null && old.IsAlive)
                old.Join(2000);

            stopSignal.Reset();
            Interlocked.Exchange(ref clicks, 0);
            Interlocked.Exchange(ref clickingStartsAtTicks, DateTime.UtcNow.AddMilliseconds(job.StartDelayMs).Ticks);

            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Name = "Clicker";
            thread.Start(job);
        }

        public void Stop()
        {
            stopSignal.Set();
        }

        public void Join(int timeoutMs)
        {
            Thread t = thread;
            if (t != null)
                t.Join(timeoutMs);
        }

        private void Run(object state)
        {
            ClickJob job = (ClickJob)state;
            bool highResTimer = false;
            try
            {
                // 1 ms timer resolution so short intervals are accurate.
                try { NativeMethods.timeBeginPeriod(1); highResTimer = true; }
                catch (Exception) { }

                if (job.StartDelayMs > 0 && stopSignal.WaitOne(job.StartDelayMs))
                    return;

                Stopwatch clock = Stopwatch.StartNew();
                long dueMs = 0;
                while (true)
                {
                    if (job.UseFixedPosition)
                        NativeMethods.SetCursorPos(job.X, job.Y);

                    SendLeftClick(job.DoubleClick);
                    int done = Interlocked.Increment(ref clicks);

                    if (job.RepeatCount > 0 && done >= job.RepeatCount)
                        break;

                    // Schedule against a steady clock so the rate doesn't drift.
                    dueMs += job.IntervalMs;
                    long waitMs = dueMs - clock.ElapsedMilliseconds;
                    if (waitMs < 0)
                    {
                        // Fell behind (e.g. system busy): don't burst to catch up.
                        dueMs = clock.ElapsedMilliseconds;
                        waitMs = 0;
                    }

                    if (stopSignal.WaitOne((int)waitMs))
                        break;
                }
            }
            catch (Exception)
            {
                // Never let the worker crash the app; Finished still fires below.
            }
            finally
            {
                if (highResTimer)
                {
                    try { NativeMethods.timeEndPeriod(1); }
                    catch (Exception) { }
                }

                EventHandler handler = Finished;
                if (handler != null)
                    handler(this, EventArgs.Empty);
            }
        }

        private static void SendLeftClick(bool doubleClick)
        {
            int count = doubleClick ? 4 : 2;
            NativeMethods.INPUT[] inputs = new NativeMethods.INPUT[count];
            for (int i = 0; i < count; i++)
            {
                inputs[i].type = NativeMethods.INPUT_MOUSE;
                inputs[i].mi.dwFlags = (i % 2 == 0)
                    ? NativeMethods.MOUSEEVENTF_LEFTDOWN
                    : NativeMethods.MOUSEEVENTF_LEFTUP;
            }
            NativeMethods.SendInput((uint)count, inputs, InputSize);
        }
    }

    // ------------------------------------------------------------------
    // Full-screen overlay used to pick a fixed click position
    // ------------------------------------------------------------------
    internal sealed class PickOverlay : Form
    {
        private readonly Font hintFont = new Font("Segoe UI", 16f, FontStyle.Bold);
        private readonly Font coordFont = new Font("Segoe UI", 11f, FontStyle.Bold);
        private Point mouse = new Point(int.MinValue, int.MinValue);

        public Point PickedPoint { get; private set; }

        public PickOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.Black;
            Opacity = 0.5;
            Cursor = Cursors.Cross;
            KeyPreview = true;
            DoubleBuffered = true;
            Bounds = SystemInformation.VirtualScreen;   // covers every monitor
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Bounds = SystemInformation.VirtualScreen;
            Activate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left)
            {
                PickedPoint = Cursor.Position;
                DialogResult = DialogResult.OK;
            }
            else if (e.Button == MouseButtons.Right)
            {
                DialogResult = DialogResult.Cancel;
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape)
                DialogResult = DialogResult.Cancel;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Invalidate(CoordRect(mouse));
            mouse = e.Location;
            Invalidate(CoordRect(mouse));
        }

        private static Rectangle CoordRect(Point p)
        {
            if (p.X == int.MinValue)
                return Rectangle.Empty;
            return new Rectangle(p.X + 12, p.Y + 14, 180, 32);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            Rectangle primary = Screen.PrimaryScreen.Bounds;
            primary.Offset(-Left, -Top);
            Rectangle hintRect = new Rectangle(primary.X, primary.Y + primary.Height / 3, primary.Width, 120);
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Near;
                g.DrawString("Click where you want the auto clicker to click\nEsc or right-click to cancel",
                    hintFont, Brushes.White, hintRect, sf);
            }

            if (mouse.X != int.MinValue)
            {
                Point screenPoint = PointToScreen(mouse);
                g.DrawString(screenPoint.X + ", " + screenPoint.Y, coordFont, Brushes.White, mouse.X + 14, mouse.Y + 16);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                hintFont.Dispose();
                coordFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // ------------------------------------------------------------------
    // Main window
    // ------------------------------------------------------------------
    internal sealed class MainForm : Form
    {
        private const int HotkeyId = 0x4143;   // any id in 0x0000-0xBFFF

        private NumericUpDown nudHours, nudMins, nudSecs, nudMs, nudDelay, nudRepeat, nudX, nudY;
        private ComboBox cboClickType, cboHotkey;
        private RadioButton rbRepeatTimes, rbRepeatForever, rbCurrentPos, rbFixedPos;
        private Button btnStart, btnStop, btnPick;
        private CheckBox chkTopMost;
        private GroupBox grpInterval, grpOptions, grpRepeat, grpPosition;
        private Label lblRate, lblStatus;
        private readonly System.Windows.Forms.Timer uiTimer = new System.Windows.Forms.Timer();

        private readonly Clicker clicker = new Clicker();
        private readonly string settingsPath;
        private bool running;
        private bool userStopped;
        private bool hotkeyRegistered;
        private IntPtr hotkeyWindow = IntPtr.Zero;
        private bool picking;
        private bool closing;

        public MainForm()
        {
            settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AutoClicker", "settings.ini");

            BuildUi();
            LoadSettings();

            clicker.Finished += OnClickerFinishedFromWorker;
            uiTimer.Interval = 100;
            uiTimer.Tick += delegate { UpdateRunningStatus(); };

            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch (Exception) { }

            UpdateRateLabel();
            ApplyRunningState();
        }

        // ---------------- UI construction ----------------

        private void BuildUi()
        {
            SuspendLayout();

            Text = "Auto Clicker";
            Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(396, 480);

            // Click interval
            grpInterval = Group("Click interval", 8, 78);
            nudHours = Num(12, 22, 50, 0, 24, 0);
            nudMins = Num(102, 22, 50, 0, 59, 0);
            nudSecs = Num(192, 22, 50, 0, 59, 0);
            nudMs = Num(282, 22, 50, 0, 999, 100);
            grpInterval.Controls.AddRange(new Control[] {
                nudHours, Lbl("hours", 65, 25),
                nudMins, Lbl("mins", 155, 25),
                nudSecs, Lbl("secs", 245, 25),
                nudMs, Lbl("ms", 335, 25) });
            lblRate = Lbl("", 12, 52);
            lblRate.ForeColor = SystemColors.GrayText;
            grpInterval.Controls.Add(lblRate);
            foreach (NumericUpDown n in new[] { nudHours, nudMins, nudSecs, nudMs })
                n.ValueChanged += delegate { UpdateRateLabel(); };

            // Click options
            grpOptions = Group("Click options", 94, 84);
            cboClickType = Combo(88, 22, 96, new object[] { "Single", "Double" });
            cboHotkey = Combo(260, 22, 70, new object[] {
                "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12" });
            cboHotkey.SelectedIndex = 5; // F6
            nudDelay = Num(88, 52, 50, 0, 60, 0);
            grpOptions.Controls.AddRange(new Control[] {
                Lbl("Click type:", 12, 25), cboClickType,
                Lbl("Hotkey:", 206, 25), cboHotkey,
                Lbl("Start delay:", 12, 55), nudDelay, Lbl("seconds", 142, 55) });
            cboClickType.SelectedIndexChanged += delegate { UpdateRateLabel(); };
            cboHotkey.SelectedIndexChanged += delegate { RegisterCurrentHotkey(); };

            // Click repeat
            grpRepeat = Group("Click repeat", 186, 78);
            rbRepeatTimes = Radio("Repeat", 12, 23);
            rbRepeatTimes.AutoSize = false;
            rbRepeatTimes.Size = new Size(72, 20);
            nudRepeat = Num(88, 21, 90, 1, 10000000, 100);
            nudRepeat.ThousandsSeparator = true;
            rbRepeatForever = Radio("Repeat until stopped", 12, 50);
            rbRepeatForever.Checked = true;
            grpRepeat.Controls.AddRange(new Control[] {
                rbRepeatTimes, nudRepeat, Lbl("times", 182, 24), rbRepeatForever });
            rbRepeatTimes.CheckedChanged += delegate { UpdateEnabledFields(); };

            // Cursor position
            grpPosition = Group("Cursor position", 272, 86);
            rbCurrentPos = Radio("Current mouse location", 12, 23);
            rbCurrentPos.Checked = true;
            rbFixedPos = Radio("Fixed", 12, 53);
            rbFixedPos.AutoSize = false;
            rbFixedPos.Size = new Size(70, 20);
            nudX = Num(102, 51, 64, -100000, 100000, 0);
            nudY = Num(188, 51, 64, -100000, 100000, 0);
            btnPick = new Button();
            btnPick.Text = "Pick location";
            btnPick.Location = new Point(262, 50);
            btnPick.Size = new Size(98, 26);
            btnPick.Click += delegate { PickLocation(); };
            grpPosition.Controls.AddRange(new Control[] {
                rbCurrentPos, rbFixedPos,
                Lbl("X", 88, 54), nudX, Lbl("Y", 174, 54), nudY, btnPick });
            rbFixedPos.CheckedChanged += delegate { UpdateEnabledFields(); };

            // Start / Stop
            btnStart = new Button();
            btnStart.Location = new Point(12, 368);
            btnStart.Size = new Size(180, 40);
            btnStart.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            btnStart.Click += delegate { StartClicking(); };

            btnStop = new Button();
            btnStop.Location = new Point(204, 368);
            btnStop.Size = new Size(180, 40);
            btnStop.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            btnStop.Click += delegate { StopClicking(); };

            chkTopMost = new CheckBox();
            chkTopMost.Text = "Always on top";
            chkTopMost.AutoSize = true;
            chkTopMost.Location = new Point(14, 416);
            chkTopMost.CheckedChanged += delegate { TopMost = chkTopMost.Checked; };

            lblStatus = new Label();
            lblStatus.Location = new Point(12, 440);
            lblStatus.Size = new Size(372, 36);

            Controls.AddRange(new Control[] {
                grpInterval, grpOptions, grpRepeat, grpPosition,
                btnStart, btnStop, chkTopMost, lblStatus });

            UpdateHotkeyTexts();

            ResumeLayout(false);
            PerformLayout();
        }

        private GroupBox Group(string text, int y, int height)
        {
            GroupBox g = new GroupBox();
            g.Text = text;
            g.Location = new Point(12, y);
            g.Size = new Size(372, height);
            return g;
        }

        private static NumericUpDown Num(int x, int y, int width, int min, int max, int value)
        {
            NumericUpDown n = new NumericUpDown();
            n.Location = new Point(x, y);
            n.Width = width;
            n.Minimum = min;
            n.Maximum = max;
            n.Value = value;
            return n;
        }

        private static Label Lbl(string text, int x, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Location = new Point(x, y);
            return l;
        }

        private static ComboBox Combo(int x, int y, int width, object[] items)
        {
            ComboBox c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.Location = new Point(x, y);
            c.Width = width;
            c.Items.AddRange(items);
            c.SelectedIndex = 0;
            return c;
        }

        private static RadioButton Radio(string text, int x, int y)
        {
            RadioButton r = new RadioButton();
            r.Text = text;
            r.AutoSize = true;
            r.Location = new Point(x, y);
            return r;
        }

        // ---------------- Hotkey ----------------

        private string HotkeyName
        {
            get { return cboHotkey.SelectedItem == null ? "F6" : cboHotkey.SelectedItem.ToString(); }
        }

        private string StopHint
        {
            get { return hotkeyRegistered ? "press " + HotkeyName + " to stop" : "click Stop to stop"; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RegisterCurrentHotkey();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            UnregisterCurrentHotkey();
            base.OnHandleDestroyed(e);
        }

        private void RegisterCurrentHotkey()
        {
            if (!IsHandleCreated)
                return;

            UnregisterCurrentHotkey();
            uint vk = NativeMethods.VK_F1 + (uint)Math.Max(0, cboHotkey.SelectedIndex);
            try
            {
                hotkeyWindow = Handle;
                hotkeyRegistered =
                    NativeMethods.RegisterHotKey(hotkeyWindow, HotkeyId, NativeMethods.MOD_NOREPEAT, vk) ||
                    NativeMethods.RegisterHotKey(hotkeyWindow, HotkeyId, 0, vk);
            }
            catch (Exception)
            {
                hotkeyRegistered = false;
            }

            UpdateHotkeyTexts();
            if (!running)
            {
                if (hotkeyRegistered)
                    SetStatus("Ready. Press " + HotkeyName + " anywhere (or click Start) to begin clicking.");
                else
                    SetStatus(HotkeyName + " is already used by another app. Pick a different hotkey above.");
            }
        }

        private void UnregisterCurrentHotkey()
        {
            if (!hotkeyRegistered)
                return;
            try { NativeMethods.UnregisterHotKey(hotkeyWindow, HotkeyId); }
            catch (Exception) { }
            hotkeyRegistered = false;
        }

        private void UpdateHotkeyTexts()
        {
            btnStart.Text = "Start (" + HotkeyName + ")";
            btnStop.Text = "Stop (" + HotkeyName + ")";
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_HOTKEY && m.WParam.ToInt64() == HotkeyId)
            {
                if (!picking)
                {
                    if (running) StopClicking();
                    else StartClicking();
                }
                return;
            }
            base.WndProc(ref m);
        }

        // ---------------- Start / stop ----------------

        private long IntervalMs()
        {
            return (long)nudHours.Value * 3600000L
                 + (long)nudMins.Value * 60000L
                 + (long)nudSecs.Value * 1000L
                 + (long)nudMs.Value;
        }

        private void StartClicking()
        {
            if (running || picking)
                return;

            long interval = IntervalMs();
            if (interval < 1)
            {
                SetStatus("Set an interval of at least 1 ms first.");
                return;
            }

            ClickJob job = new ClickJob();
            job.IntervalMs = (int)Math.Min(interval, int.MaxValue);
            job.DoubleClick = cboClickType.SelectedIndex == 1;
            job.RepeatCount = rbRepeatTimes.Checked ? (int)nudRepeat.Value : 0;
            job.UseFixedPosition = rbFixedPos.Checked;
            job.X = (int)nudX.Value;
            job.Y = (int)nudY.Value;
            job.StartDelayMs = (int)nudDelay.Value * 1000;

            running = true;
            userStopped = false;
            ApplyRunningState();
            clicker.Start(job);
            uiTimer.Start();
            UpdateRunningStatus();
        }

        private void StopClicking()
        {
            if (!running)
                return;
            userStopped = true;
            clicker.Stop();
        }

        private void OnClickerFinishedFromWorker(object sender, EventArgs e)
        {
            if (closing)
                return;
            try
            {
                if (IsHandleCreated && !IsDisposed)
                    BeginInvoke((MethodInvoker)OnClickerFinished);
            }
            catch (InvalidOperationException) { }   // also covers ObjectDisposedException
        }

        private void OnClickerFinished()
        {
            if (closing)
                return;
            running = false;
            uiTimer.Stop();
            ApplyRunningState();

            int clicks = clicker.Clicks;
            string what = clicks == 1 ? "1 click" : clicks.ToString("N0") + " clicks";
            if (userStopped)
                SetStatus("Stopped after " + what + ". Press " + HotkeyName + " to start again.");
            else
                SetStatus("Done: " + what + ". Press " + HotkeyName + " to start again.");
        }

        private void UpdateRunningStatus()
        {
            if (!running)
                return;

            TimeSpan left = clicker.ClickingStartsAtUtc - DateTime.UtcNow;
            if (left.TotalMilliseconds > 0)
            {
                SetStatus("Starting in " + Math.Ceiling(left.TotalSeconds) + "…  (" + StopHint.Replace("to stop", "to cancel") + ")");
                return;
            }

            int clicks = clicker.Clicks;
            string progress = rbRepeatTimes.Checked
                ? clicks.ToString("N0") + " / " + ((int)nudRepeat.Value).ToString("N0") + " clicks"
                : clicks.ToString("N0") + " clicks";
            SetStatus("Clicking…  " + progress + "  ·  " + StopHint);
        }

        private void ApplyRunningState()
        {
            bool idle = !running;
            grpInterval.Enabled = idle;
            grpOptions.Enabled = idle;
            grpRepeat.Enabled = idle;
            grpPosition.Enabled = idle;
            btnStart.Enabled = idle;
            btnStop.Enabled = running;
            UpdateEnabledFields();
        }

        private void UpdateEnabledFields()
        {
            nudRepeat.Enabled = rbRepeatTimes.Checked;
            nudX.Enabled = rbFixedPos.Checked;
            nudY.Enabled = rbFixedPos.Checked;
        }

        private void UpdateRateLabel()
        {
            long ms = IntervalMs();
            string suffix = cboClickType.SelectedIndex == 1 ? " (double-clicks)" : "";
            if (ms < 1)
                lblRate.Text = "Interval must be at least 1 ms";
            else if (ms < 1000)
                lblRate.Text = "≈ " + (1000.0 / ms).ToString("0.#", CultureInfo.CurrentCulture) + " clicks per second" + suffix;
            else
                lblRate.Text = "1 click every " + FormatDuration(ms) + suffix;
        }

        private static string FormatDuration(long ms)
        {
            TimeSpan t = TimeSpan.FromMilliseconds(ms);
            List<string> parts = new List<string>();
            int hours = (int)t.TotalHours;
            if (hours > 0) parts.Add(hours + " h");
            if (t.Minutes > 0) parts.Add(t.Minutes + " min");
            double secs = t.Seconds + t.Milliseconds / 1000.0;
            if (secs > 0) parts.Add(secs.ToString("0.###", CultureInfo.CurrentCulture) + " s");
            return string.Join(" ", parts.ToArray());
        }

        private void SetStatus(string text)
        {
            if (lblStatus.Text != text)
                lblStatus.Text = text;
        }

        // ---------------- Pick location ----------------

        private void PickLocation()
        {
            if (running)
                return;
            picking = true;
            try
            {
                using (PickOverlay overlay = new PickOverlay())
                {
                    if (overlay.ShowDialog(this) == DialogResult.OK)
                    {
                        Point p = overlay.PickedPoint;
                        nudX.Value = Math.Max(nudX.Minimum, Math.Min(nudX.Maximum, p.X));
                        nudY.Value = Math.Max(nudY.Minimum, Math.Min(nudY.Maximum, p.Y));
                        rbFixedPos.Checked = true;
                        SetStatus("Click position set to " + p.X + ", " + p.Y + ".");
                    }
                }
            }
            finally
            {
                picking = false;
                Activate();
            }
        }

        // ---------------- Settings ----------------

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(settingsPath))
                    return;

                Dictionary<string, string> s = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in File.ReadAllLines(settingsPath))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0)
                        s[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }

                SetNum(nudHours, s, "hours");
                SetNum(nudMins, s, "mins");
                SetNum(nudSecs, s, "secs");
                SetNum(nudMs, s, "ms");
                SetNum(nudDelay, s, "startDelay");
                SetNum(nudRepeat, s, "repeatCount");
                SetNum(nudX, s, "x");
                SetNum(nudY, s, "y");
                SetIndex(cboClickType, s, "clickType");
                SetIndex(cboHotkey, s, "hotkey");
                if (GetInt(s, "repeatMode", 0) == 1) rbRepeatTimes.Checked = true; else rbRepeatForever.Checked = true;
                if (GetInt(s, "positionMode", 0) == 1) rbFixedPos.Checked = true; else rbCurrentPos.Checked = true;
                chkTopMost.Checked = GetInt(s, "alwaysOnTop", 0) == 1;
            }
            catch (Exception)
            {
                // Bad or unreadable settings file: just use defaults.
            }
        }

        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
                File.WriteAllLines(settingsPath, new string[] {
                    "hours=" + Inv(nudHours.Value),
                    "mins=" + Inv(nudMins.Value),
                    "secs=" + Inv(nudSecs.Value),
                    "ms=" + Inv(nudMs.Value),
                    "clickType=" + cboClickType.SelectedIndex,
                    "hotkey=" + cboHotkey.SelectedIndex,
                    "startDelay=" + Inv(nudDelay.Value),
                    "repeatMode=" + (rbRepeatTimes.Checked ? 1 : 0),
                    "repeatCount=" + Inv(nudRepeat.Value),
                    "positionMode=" + (rbFixedPos.Checked ? 1 : 0),
                    "x=" + Inv(nudX.Value),
                    "y=" + Inv(nudY.Value),
                    "alwaysOnTop=" + (chkTopMost.Checked ? 1 : 0) });
            }
            catch (Exception)
            {
                // Settings are a convenience; ignore failures.
            }
        }

        private static string Inv(decimal d)
        {
            return ((long)d).ToString(CultureInfo.InvariantCulture);
        }

        private static int GetInt(Dictionary<string, string> s, string key, int fallback)
        {
            string raw;
            int value;
            if (s.TryGetValue(key, out raw) && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                return value;
            return fallback;
        }

        private static void SetNum(NumericUpDown n, Dictionary<string, string> s, string key)
        {
            int v = GetInt(s, key, int.MinValue);
            if (v != int.MinValue)
                n.Value = Math.Max(n.Minimum, Math.Min(n.Maximum, v));
        }

        private static void SetIndex(ComboBox c, Dictionary<string, string> s, string key)
        {
            int v = GetInt(s, key, -1);
            if (v >= 0 && v < c.Items.Count)
                c.SelectedIndex = v;
        }

        // ---------------- Shutdown ----------------

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            closing = true;
            clicker.Stop();
            clicker.Join(1000);
            uiTimer.Stop();
            SaveSettings();
            base.OnFormClosing(e);
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            // Use real pixel coordinates on high-DPI screens so the picked
            // position and the clicked position always match.
            try { NativeMethods.SetProcessDPIAware(); }
            catch (Exception) { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
