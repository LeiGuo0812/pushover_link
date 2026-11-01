using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace CodexPushover
{
    public static class UiTheme
    {
        public static readonly Color Blue = Color.FromArgb(37, 99, 235);
        public static readonly Color Hover = Color.FromArgb(29, 78, 216);
        public static readonly Color Canvas = Color.FromArgb(243, 246, 252);
        public static readonly Color Pale = Color.FromArgb(237, 243, 255);
        public static readonly Color Border = Color.FromArgb(220, 229, 242);
        public static readonly Color Ink = Color.FromArgb(24, 38, 63);
        public static readonly Color Muted = Color.FromArgb(98, 113, 138);
        public static readonly Color Error = Color.FromArgb(164, 60, 60);
        public static Font TextFont(float points, FontStyle style) { return new Font("Microsoft YaHei UI", points, style); }
        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath(); float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            p.AddArc(r.Left, r.Top, d, d, 180, 90); p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90); p.CloseFigure(); return p;
        }
        public static Label Label(string text, float size, Color color, bool bold)
        {
            return new Label { Text = text, Font = TextFont(size, bold ? FontStyle.Bold : FontStyle.Regular), ForeColor = color,
                AutoSize = false, Dock = DockStyle.Fill, Margin = Padding.Empty, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        }
        public static TableLayoutPanel Table(int columns)
        {
            var table = new BufferedTable { ColumnCount = columns, RowCount = 1, Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Color.Transparent };
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); return table;
        }
    }
    public sealed class BufferedTable : TableLayoutPanel
    {
        public BufferedTable() { DoubleBuffered = true; SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true); }
    }
    public class BufferedPanel : Panel
    {
        public BufferedPanel() { DoubleBuffered = true; SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true); }
    }
    public sealed class SurfacePanel : BufferedPanel
    {
        public SurfacePanel() { BackColor = Color.White; Padding = new Padding(20); }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? UiTheme.Canvas : Parent.BackColor == Color.Transparent ? UiTheme.Canvas : Parent.BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = UiTheme.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), 9 * e.Graphics.DpiX / 96f))
            using (var brush = new SolidBrush(Color.White))
            using (var pen = new Pen(UiTheme.Border)) { e.Graphics.FillPath(brush, path); e.Graphics.DrawPath(pen, path); }
        }
    }
    public sealed class BlueButton : Button
    {
        bool hover, pressed;
        public bool Primary;
        public BlueButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; Cursor = Cursors.Hand; Font = UiTheme.TextFont(10, FontStyle.Regular);
            Height = 40; Width = 112; Margin = new Padding(8, 0, 0, 0); UseVisualStyleBackColor = false;
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? UiTheme.Canvas : Parent.BackColor); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = e.Graphics.DpiX / 96f;
            Color bg = !Enabled ? UiTheme.Border : Primary ? (hover || pressed ? UiTheme.Hover : UiTheme.Blue) : hover || pressed ? UiTheme.Pale : Color.White;
            Color text = !Enabled ? UiTheme.Muted : Primary ? Color.White : UiTheme.Blue;
            using (var path = UiTheme.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), 6 * scale))
            using (var brush = new SolidBrush(bg))
            using (var pen = new Pen(Primary ? bg : UiTheme.Border)) { e.Graphics.FillPath(brush, path); e.Graphics.DrawPath(pen, path); }
            var bounds = ClientRectangle; if (pressed) bounds.Offset(0, 1);
            TextRenderer.DrawText(e.Graphics, Text, Font, bounds, text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -5, -5), text, bg);
        }
    }
    public sealed class BlueSwitch : CheckBox
    {
        public BlueSwitch()
        {
            AutoSize = false; Height = 32; Dock = DockStyle.Fill; Margin = Padding.Empty; FlatStyle = FlatStyle.Flat; Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; float s = e.Graphics.DpiX / 96f;
            var track = new RectangleF(Width - 40 * s, (Height - 22 * s) / 2, 40 * s, 22 * s);
            Color bg = Enabled && Checked ? UiTheme.Blue : UiTheme.Border;
            using (var path = UiTheme.Round(track, 11 * s)) using (var brush = new SolidBrush(bg)) e.Graphics.FillPath(brush, path);
            using (var brush = new SolidBrush(Color.White)) e.Graphics.FillEllipse(brush, track.X + (Checked ? 21 : 3) * s, track.Y + 3 * s, 16 * s, 16 * s);
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(0, 0, Math.Max(0, Width - (int)(52 * s)), Height), Enabled ? UiTheme.Ink : UiTheme.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -1, -1));
        }
    }
    public sealed class InputBox : BufferedPanel
    {
        public readonly TextBox Input;
        bool focused;
        public InputBox(string label)
        {
            BackColor = Color.White; Height = 42; Dock = DockStyle.Fill; Padding = new Padding(12, 10, 12, 8); Margin = Padding.Empty;
            Input = new TextBox { BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, Font = UiTheme.TextFont(10, FontStyle.Regular), BackColor = Color.White,
                ForeColor = UiTheme.Ink, UseSystemPasswordChar = true, MaxLength = 100, AccessibleName = label };
            Input.Enter += delegate { focused = true; Invalidate(); }; Input.Leave += delegate { focused = false; Invalidate(); }; Controls.Add(Input);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = UiTheme.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), 5 * e.Graphics.DpiX / 96f))
            using (var pen = new Pen(focused ? UiTheme.Blue : UiTheme.Border, focused ? 1.5f : 1)) e.Graphics.DrawPath(pen, path);
        }
    }
    public sealed class BlueMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Color.White; } }
        public override Color MenuBorder { get { return UiTheme.Border; } }
        public override Color MenuItemSelected { get { return UiTheme.Pale; } }
        public override Color MenuItemBorder { get { return UiTheme.Pale; } }
        public override Color SeparatorDark { get { return UiTheme.Border; } }
        public override Color SeparatorLight { get { return Color.White; } }
    }
    public sealed class BlueMenuRenderer : ToolStripProfessionalRenderer
    {
        public BlueMenuRenderer() : base(new BlueMenuColors()) { RoundedEdges = true; }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        { e.TextColor = e.Item.Enabled ? e.Item.Selected ? UiTheme.Blue : UiTheme.Ink : UiTheme.Muted; base.OnRenderItemText(e); }
    }
    public sealed class UiInputError : Exception { public UiInputError(string message) : base(message) { } }
    public sealed class DashboardSnapshot
    {
        public bool Enabled, Blocked, Credentials, Guard, Startup, Maintenance, Suggestions, InputRequests;
        public long Accepted, Pending, Errors, Quarantined;
        public static DashboardSnapshot Read()
        {
            var s = Core.Status();
            return new DashboardSnapshot { Enabled = Convert.ToBoolean(s["enabled"]), Blocked = Convert.ToBoolean(s["sender_blocked"]), Credentials = Convert.ToBoolean(s["credentials_ready"]),
                Guard = Host.IsHealthy("guard", 30), Startup = CodexPushover.Startup.IsConfigured(), Maintenance = Convert.ToBoolean(s["maintenance"]), Suggestions = Convert.ToBoolean(s[Core.SuggestionOption]), InputRequests = Convert.ToBoolean(s[Core.InputOption]),
                Accepted = Convert.ToInt64(s["accepted"]), Pending = Convert.ToInt64(s["pending"]), Errors = Convert.ToInt64(s["blocked"]), Quarantined = Convert.ToInt64(s["quarantined"]) };
        }
        public static DashboardSnapshot Preview()
        { return new DashboardSnapshot { Enabled = true, Credentials = true, Guard = true, Startup = true, Suggestions = true, InputRequests = true, Accepted = 37, Pending = 0, Errors = 0 }; }
        public string Key { get { return String.Join("|", new object[] { Enabled, Blocked, Credentials, Guard, Startup, Maintenance, Suggestions, InputRequests, Accepted, Pending, Errors, Quarantined }); } }
    }
    public sealed class SettingsForm : Form
    {
        readonly TrayContext context;
        readonly Label stateTitle, stateDetail, accepted, pending, errors, feedback;
        readonly BlueSwitch notifications, startup, suggestions, inputRequests;
        readonly NumericUpDown limit;
        readonly TextBox token, user;
        readonly BlueButton save, test, resume;
        readonly PictureBox brand;
        readonly ToolTip tips;
        readonly Icon ownedIcon;
        bool updating, dragging, working;
        string lastSnapshot = "";
        DashboardSnapshot deferred;
        public bool CloseForExit;
        public SettingsForm(TrayContext app)
        {
            SuspendLayout();
            context = app; Text = "Codex 手机通知 · 状态与设置"; AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
            ClientSize = new Size(920, 860); MinimumSize = new Size(760, 620); StartPosition = FormStartPosition.CenterScreen;
            Font = UiTheme.TextFont(10, FontStyle.Regular); ForeColor = UiTheme.Ink; BackColor = UiTheme.Canvas;
            DoubleBuffered = true; SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            ownedIcon = UiAssets.WindowIcon(); Icon = ownedIcon; tips = new ToolTip { AutoPopDelay = 10000, InitialDelay = 400 };
            var root = UiTheme.Table(1); root.BackColor = UiTheme.Canvas; root.RowCount = 3; root.RowStyles.Clear(); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 108)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 108)); Controls.Add(root);
            var header = new BufferedPanel { Dock = DockStyle.Fill, BackColor = UiTheme.Blue, Padding = new Padding(24, 20, 24, 20), Margin = Padding.Empty };
            var headerGrid = UiTheme.Table(3); headerGrid.BackColor = UiTheme.Blue; headerGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80)); headerGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); headerGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            brand = new PictureBox { Image = UiAssets.BrandImage(), SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 14, 0), AccessibleName = "蓝色通知铃图标" };
            headerGrid.Controls.Add(brand, 0, 0); var headerText = UiTheme.Table(1); headerText.RowCount = 2; headerText.RowStyles.Clear(); headerText.RowStyles.Add(new RowStyle(SizeType.Percent, 64)); headerText.RowStyles.Add(new RowStyle(SizeType.Percent, 36));
            headerText.Controls.Add(UiTheme.Label("Codex 手机通知", 23, Color.White, true), 0, 0);
            headerText.Controls.Add(UiTheme.Label("项目名称 · 用户输入 · 本轮回复", 10, Color.FromArgb(220, 233, 255), false), 0, 1); headerGrid.Controls.Add(headerText, 1, 0);
            var version = UiTheme.Label("Pushover\nv" + Program.Version, 10, Color.FromArgb(220, 233, 255), false); version.TextAlign = ContentAlignment.MiddleRight; headerGrid.Controls.Add(version, 2, 0); header.Controls.Add(headerGrid); root.Controls.Add(header, 0, 0);
            var scroll = new BufferedPanel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = UiTheme.Canvas, Padding = new Padding(24, 18, 24, 12), Margin = Padding.Empty };
            var body = UiTheme.Table(1); body.Dock = DockStyle.Top; body.AutoSize = true; body.AutoSizeMode = AutoSizeMode.GrowAndShrink; body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); body.RowCount = 3; body.RowStyles.Clear();
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 148)); body.RowStyles.Add(new RowStyle(SizeType.Absolute, 404)); body.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); scroll.Controls.Add(body); root.Controls.Add(scroll, 0, 1);
            var status = new SurfacePanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 14), Padding = new Padding(20, 14, 20, 12) };
            var statusGrid = UiTheme.Table(1); statusGrid.RowCount = 3; statusGrid.RowStyles.Clear(); statusGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); statusGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 22)); statusGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            stateTitle = UiTheme.Label("正在读取通知状态…", 13, UiTheme.Ink, true); stateDetail = UiTheme.Label("后台发送与手机接收状态分别确认", 9.5f, UiTheme.Muted, false);
            statusGrid.Controls.Add(stateTitle, 0, 0); statusGrid.Controls.Add(stateDetail, 0, 1);
            var metrics = UiTheme.Table(3); foreach (int n in new [] { 0, 1, 2 }) metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, n == 0 ? 40 : 30));
            accepted = AddMetric(metrics, 0, "Pushover 已接受", UiTheme.Blue); pending = AddMetric(metrics, 1, "等待发送", UiTheme.Ink); errors = AddMetric(metrics, 2, "需要处理", UiTheme.Ink);
            statusGrid.Controls.Add(metrics, 0, 2); status.Controls.Add(statusGrid); body.Controls.Add(status, 0, 0);
            var settings = UiTheme.Table(2); settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40)); settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60)); settings.Margin = new Padding(0, 0, 0, 12);
            var settingsCard = new SurfacePanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 12, 0), Padding = new Padding(20, 16, 20, 16) };
            var options = UiTheme.Table(1); options.RowCount = 12; options.RowStyles.Clear();
            foreach (int height in new [] { 28, 32, 22, 34, 22, 34, 22, 34, 22, 30, 36, 20 }) options.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            options.Controls.Add(UiTheme.Label("通知设置", 11, UiTheme.Ink, true), 0, 0);
            notifications = new BlueSwitch { Text = "手机通知", BackColor = Color.White, AccessibleName = "手机通知开关" }; options.Controls.Add(notifications, 0, 1);
            options.Controls.Add(UiTheme.Label("开关立即生效，暂停时保留待发", 9, UiTheme.Muted, false), 0, 2);
            startup = new BlueSwitch { Text = "登录后自动运行", BackColor = Color.White, AccessibleName = "登录自动运行开关" }; options.Controls.Add(startup, 0, 3);
            options.Controls.Add(UiTheme.Label("Windows 登录后进入托盘", 9, UiTheme.Muted, false), 0, 4);
            suggestions = new BlueSwitch { Text = "输入框建议提醒", BackColor = Color.White, AccessibleName = "输入框建议提醒开关" }; options.Controls.Add(suggestions, 0, 5);
            options.Controls.Add(UiTheme.Label("推送 Codex 自动生成的下一步建议", 9, UiTheme.Muted, false), 0, 6);
            inputRequests = new BlueSwitch { Text = "需要回答时提醒", BackColor = Color.White, AccessibleName = "需要回答时提醒开关" }; options.Controls.Add(inputRequests, 0, 7);
            options.Controls.Add(UiTheme.Label("提问或选择时提醒，需信任新增 hook", 9, UiTheme.Muted, false), 0, 8);
            tips.SetToolTip(suggestions, "立即保存。关闭后取消尚未发出的建议；已开始发送的请求可能仍会送达。");
            tips.SetToolTip(inputRequests, "立即保存。支持 request_user_input / request_user_input_async 提问，不代你回答。");
            options.Controls.Add(UiTheme.Label("用户输入显示上限", 9.5f, UiTheme.Ink, false), 0, 9);
            var limitRow = UiTheme.Table(2); limitRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 98)); limitRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            limit = new NumericUpDown { AutoScaleMode = AutoScaleMode.None, Minimum = 8, Maximum = 400, Value = 200, Font = UiTheme.TextFont(11, FontStyle.Regular), Dock = DockStyle.Fill, Margin = new Padding(0, 3, 10, 3), ForeColor = UiTheme.Ink, BackColor = Color.White, AccessibleName = "用户输入显示字符上限" };
            limitRow.Controls.Add(limit, 0, 0); limitRow.Controls.Add(UiTheme.Label("字符，调整后保存", 9, UiTheme.Muted, false), 1, 0); options.Controls.Add(limitRow, 0, 10);
            options.Controls.Add(UiTheme.Label("整条通知最多 1024 字符，超长截断", 8.75f, UiTheme.Muted, false), 0, 11); settingsCard.Controls.Add(options); settings.Controls.Add(settingsCard, 0, 0);
            var credentials = new SurfacePanel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(20, 16, 20, 16) };
            var fields = UiTheme.Table(1); fields.RowCount = 8; fields.RowStyles.Clear();
            foreach (int height in new [] { 28, 24, 24, 42, 10, 24, 42, 36 }) fields.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            fields.Controls.Add(UiTheme.Label("Pushover 凭据", 11, UiTheme.Ink, true), 0, 0);
            fields.Controls.Add(UiTheme.Label("仅在更换时填写；留空保留已保存的值。", 9.25f, UiTheme.Muted, false), 0, 1);
            fields.Controls.Add(UiTheme.Label("应用 Token  /  Application Token", 9.5f, UiTheme.Ink, false), 0, 2);
            var tokenBox = new InputBox("Pushover Application Token"); token = tokenBox.Input; fields.Controls.Add(tokenBox, 0, 3);
            fields.Controls.Add(UiTheme.Label("用户 Key  /  User Key", 9.5f, UiTheme.Ink, false), 0, 5);
            var userBox = new InputBox("Pushover User Key"); user = userBox.Input; fields.Controls.Add(userBox, 0, 6);
            fields.Controls.Add(UiTheme.Label("凭据只保存在本机，不显示已保存内容。", 9, UiTheme.Muted, false), 0, 7); credentials.Controls.Add(fields); settings.Controls.Add(credentials, 1, 0); body.Controls.Add(settings, 0, 1);
            var location = UiTheme.Table(2); location.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); location.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            var path = UiTheme.Label("数据目录  " + Host.Root, 8.75f, UiTheme.Muted, false); tips.SetToolTip(path, Host.Root); location.Controls.Add(path, 0, 0);
            var openData = new LinkLabel { Text = "打开数据目录", AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, LinkColor = UiTheme.Blue, ActiveLinkColor = UiTheme.Hover, VisitedLinkColor = UiTheme.Blue, Margin = Padding.Empty };
            openData.LinkClicked += delegate { TrayContext.Open(Host.Root); }; location.Controls.Add(openData, 1, 0); body.Controls.Add(location, 0, 2);
            var footer = new BufferedPanel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(24, 12, 24, 10), Margin = Padding.Empty };
            var footerGrid = UiTheme.Table(1); footerGrid.RowCount = 3; footerGrid.RowStyles.Clear(); footerGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); footerGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); footerGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var actionRow = UiTheme.Table(2); actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 382));
            var help = new LinkLabel { Text = "使用说明", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, LinkColor = UiTheme.Blue, ActiveLinkColor = UiTheme.Hover, VisitedLinkColor = UiTheme.Blue, Margin = Padding.Empty };
            help.LinkClicked += delegate { TrayContext.Open(Path.Combine(Host.Root, "使用说明.md")); }; actionRow.Controls.Add(help, 0, 0);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Color.White };
            save = new BlueButton { Text = "保存更改", Primary = true, Width = 112 }; test = new BlueButton { Text = "发送测试", Width = 112 }; resume = new BlueButton { Text = "恢复发送", Width = 112 };
            actions.Controls.Add(save); actions.Controls.Add(test); actions.Controls.Add(resume); actionRow.Controls.Add(actions, 1, 0); footerGrid.Controls.Add(actionRow, 0, 0);
            feedback = UiTheme.Label("关闭窗口后继续在托盘运行。", 9, UiTheme.Muted, false); footerGrid.Controls.Add(feedback, 0, 1);
            footerGrid.Controls.Add(UiTheme.Label("接口已接受不等于手机已收到。", 8.5f, UiTheme.Muted, false), 0, 2); footer.Controls.Add(footerGrid); root.Controls.Add(footer, 0, 2);
            notifications.CheckedChanged += delegate { if (!updating && context != null) context.SetNotificationEnabled(notifications.Checked); };
            startup.CheckedChanged += delegate { if (!updating && context != null) context.SetStartup(startup.Checked); };
            suggestions.CheckedChanged += delegate { if (!updating && context != null) context.SetInteractionOption(Core.SuggestionOption, suggestions.Checked); };
            inputRequests.CheckedChanged += delegate { if (!updating && context != null) context.SetInteractionOption(Core.InputOption, inputRequests.Checked); };
            save.Click += delegate { Save(); }; test.Click += delegate { if (context != null) context.Test(); }; resume.Click += delegate { if (context != null) { context.Resume(); ShowFeedback("正在解除发送错误暂停…", false); } };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (!CloseForExit) { e.Cancel = true; Hide(); } };
            ResizeBegin += delegate { dragging = true; }; ResizeEnd += delegate { dragging = false; if (deferred != null) ApplySnapshot(deferred); };
            Shown += delegate
            {
                var area = Screen.FromControl(this).WorkingArea;
                MinimumSize = new Size(Math.Min(MinimumSize.Width, area.Width - 24), Math.Min(MinimumSize.Height, area.Height - 24));
                if (Width > area.Width - 24 || Height > area.Height - 24) Size = new Size(Math.Min(Width, area.Width - 24), Math.Min(Height, area.Height - 24));
            };
            if (context == null) ApplySnapshot(DashboardSnapshot.Preview());
            else
            {
                var current = Core.ReadObject(Path.Combine(Host.Root, "notification-options.json")); object value; int n;
                if (current.TryGetValue("user_input_max_chars", out value) && Int32.TryParse(Convert.ToString(value), out n)) limit.Value = Math.Max(8, Math.Min(400, n));
            }
            AutoScaleDimensions = new SizeF(96, 96);
            ResumeLayout(false); PerformAutoScale(); PerformLayout();
        }
        static Label AddMetric(TableLayoutPanel parent, int column, string title, Color color)
        {
            var stack = UiTheme.Table(1); stack.Margin = new Padding(column == 0 ? 0 : 16, 2, 0, 0); stack.RowCount = 2; stack.RowStyles.Clear(); stack.RowStyles.Add(new RowStyle(SizeType.Percent, 65)); stack.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
            var count = UiTheme.Label("—", 22, color, false); count.Font = new Font("Consolas", 22, FontStyle.Regular); stack.Controls.Add(count, 0, 0); stack.Controls.Add(UiTheme.Label(title, 8.75f, UiTheme.Muted, false), 0, 1); parent.Controls.Add(stack, column, 0); return count;
        }
        static void ChangeText(Label label, string text) { if (label.Text != text) label.Text = text; }
        public void RefreshStatus() { if (context != null) context.RequestStatus(); }
        public void ApplySnapshot(DashboardSnapshot s)
        {
            deferred = s; if (dragging || s.Key == lastSnapshot) return; lastSnapshot = s.Key;
            updating = true; if (!working) { notifications.Checked = s.Enabled; startup.Checked = s.Startup; suggestions.Checked = s.Suggestions; inputRequests.Checked = s.InputRequests; } updating = false;
            string title = s.Maintenance ? "通知服务正在维护" : !s.Enabled ? "手机通知已暂停" : !s.Credentials ? "等待填写 Pushover 凭据" : s.Blocked ? "发送已暂停，等待处理" : "通知服务运行中";
            stateTitle.ForeColor = s.Blocked ? UiTheme.Error : UiTheme.Ink; ChangeText(stateTitle, title);
            string detail = (s.Credentials ? "凭据已保存" : "请填写 Pushover 凭据") + "  ·  " + (s.Guard ? "后台守护运行中" : "后台守护正在恢复") + (s.Quarantined > 0 ? "  ·  部分记录已隔离" : "");
            ChangeText(stateDetail, detail); ChangeText(accepted, s.Accepted.ToString("N0", CultureInfo.CurrentCulture)); ChangeText(pending, s.Pending.ToString("N0", CultureInfo.CurrentCulture)); ChangeText(errors, s.Errors.ToString("N0", CultureInfo.CurrentCulture));
            errors.ForeColor = s.Errors > 0 ? UiTheme.Error : UiTheme.Ink; resume.Enabled = !working && s.Blocked; tips.SetToolTip(resume, "凭据或额度问题修正后，解除发送错误暂停。");
        }
        public void ShowFeedback(string text, bool error) { feedback.ForeColor = error ? UiTheme.Error : UiTheme.Muted; ChangeText(feedback, text); }
        public void SetWorking(bool value)
        {
            working = value; save.Enabled = test.Enabled = notifications.Enabled = startup.Enabled = suggestions.Enabled = inputRequests.Enabled = limit.Enabled = token.Enabled = user.Enabled = !value;
            resume.Enabled = !value && deferred != null && deferred.Blocked;
            if (!value && deferred != null) { lastSnapshot = ""; ApplySnapshot(deferred); }
        }
        void Save()
        {
            if (context == null) return; string app = token.Text.Trim(), account = user.Text.Trim(); int count = (int)limit.Value;
            if ((app.Length > 0 && !Regex.IsMatch(app, "^[A-Za-z0-9]{30}$")) || (account.Length > 0 && !Regex.IsMatch(account, "^[A-Za-z0-9]{30}$"))) { ShowFeedback("Token 和 Key 均应为 30 位字母或数字。", true); return; }
            context.RunAction(delegate
            {
                var credentials = Core.ReadObject(Path.Combine(Host.Root, "credentials.json"));
                if (app.Length > 0) credentials["PUSHOVER_APP_TOKEN"] = app; if (account.Length > 0) credentials["PUSHOVER_USER_KEY"] = account;
                foreach (string key in new [] { "PUSHOVER_APP_TOKEN", "PUSHOVER_USER_KEY" })
                    if (!Regex.IsMatch(Installation.Text(credentials, key), "^[A-Za-z0-9]{30}$")) throw new UiInputError("请先填写有效的应用 Token 和用户 Key。");
                if (app.Length > 0 || account.Length > 0) Core.WriteObject(Path.Combine(Host.Root, "credentials.json"), credentials);
                var options = Core.ReadObject(Path.Combine(Host.Root, "notification-options.json")); options["content_mode"] = "user_input"; options["user_input_max_chars"] = count;
                Core.WriteObject(Path.Combine(Host.Root, "notification-options.json"), options); Program.Wake();
            }, "设置已保存。", delegate { token.Clear(); user.Clear(); });
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { if (brand != null && brand.Image != null) brand.Image.Dispose(); if (ownedIcon != null) ownedIcon.Dispose(); if (tips != null) tips.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
