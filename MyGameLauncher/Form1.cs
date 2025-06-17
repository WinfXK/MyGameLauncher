using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace MyGameLauncher
{
    public partial class Form1 : Form
    {
        #region Win32 API
        private static string GetShortcutTarget(string file)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(shellType);
                object shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { file });
                string targetPath = (string)shortcut.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, shortcut, null);
                return string.IsNullOrEmpty(targetPath) ? file : targetPath;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[日志] 解析快捷方式失败: {file} - {ex.Message}");
                return file;
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern uint ExtractIconEx(string szFileName, int nIconIndex, IntPtr[] phiconLarge, IntPtr[] phiconSmall, uint nIcons);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("Gdi32.dll", EntryPoint = "CreateRoundRectRgn")]
        private static extern IntPtr CreateRoundRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect, int nWidthEllipse, int nHeightEllipse);

        #endregion

        #region Animation Engine
        private class AnimationState
        {
            public Color StartColor { get; set; }
            public Color TargetColor { get; set; }
            public DateTime StartTime { get; set; }
            public TimeSpan Duration { get; } = TimeSpan.FromMilliseconds(150);
        }
        private readonly Dictionary<Control, AnimationState> _animatedControls = new Dictionary<Control, AnimationState>();
        private readonly Timer _animationTimer;
        private readonly Timer _fadeInTimer;
        private Timer _closeTimer;
        private bool _isClosing = false;
        #endregion

        #region Configuration Settings
        public class AppSettings
        {
            public string WindowTitle { get; set; } = "应用中心";
            public List<string> SupportedExtensions { get; set; } = new List<string> { ".exe", ".lnk", ".bat", ".jar" };

            // --- 新增 ---
            // 用于保存“保持开启”状态的配置项
            public bool KeepLauncherOpen { get; set; } = false;
        }
        private AppSettings _settings;
        #endregion

        #region Form Draggable Fields
        private bool _isDragging = false;
        private Point _dragStartPoint;
        #endregion

        private const string AppsFolderName = "Apps";
        private const string ConfigFileName = "config.json";

        // --- 新增 ---
        // 复选框控件
        private CheckBox _keepOpenCheckBox;

        public Form1()
        {
            InitializeComponent();

            this.Opacity = 0;

            _animationTimer = new Timer { Interval = 15 };
            _animationTimer.Tick += AnimationTimer_Tick;
            _animationTimer.Start();

            _fadeInTimer = new Timer { Interval = 20 };
            _fadeInTimer.Tick += FadeInTimer_Tick;
            _closeTimer = new Timer { Interval = 15 };
            _closeTimer.Tick += CloseTimer_Tick;
            this.FormClosing += Form1_FormClosing;

            // 注意：先设置UI，再加载配置，这样配置才能应用到UI上
            SetupModernUI();
            LoadSettings();
            LoadApplications();
        }

        // --- 新增 ---
        // 将保存配置的逻辑提取成一个独立方法
        private void SaveSettings()
        {
            try
            {
                string configPath = Path.Combine(Application.StartupPath, ConfigFileName);
                var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                string jsonString = JsonSerializer.Serialize(_settings, options);
                File.WriteAllText(configPath, jsonString, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[日志] 保存配置文件失败: {ex.Message}");
            }
        }

        private void LoadSettings()
        {
            string configPath = Path.Combine(Application.StartupPath, ConfigFileName);
            if (File.Exists(configPath))
            {
                try
                {
                    string jsonString = File.ReadAllText(configPath, Encoding.UTF8);
                    _settings = JsonSerializer.Deserialize<AppSettings>(jsonString);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[日志] 读取配置文件失败: {ex.Message}");
                    _settings = new AppSettings();
                }
            }
            else
            {
                _settings = new AppSettings();
                SaveSettings(); // 调用保存方法，而不是重复代码
            }

            // --- 修改 ---
            // 加载配置后，更新复选框的选中状态
            if (_keepOpenCheckBox != null)
            {
                _keepOpenCheckBox.Checked = _settings.KeepLauncherOpen;
            }
        }

        private void SetupModernUI()
        {
            this.StartPosition = FormStartPosition.CenterScreen;

            Rectangle screen = Screen.PrimaryScreen.WorkingArea;
            this.Size = new Size(Convert.ToInt32(screen.Width * 0.7), Convert.ToInt32(screen.Height * 0.8));
            this.MinimumSize = new Size(600, 400);
            this.MaximumSize = screen.Size;
            this.DoubleBuffered = true;

            this.FormBorderStyle = FormBorderStyle.None;
            this.Padding = new Padding(0);

            this.BackColor = Color.White;
            this.flowLayoutPanel1.BackColor = this.BackColor;

            this.Load += Form1_Load;
            this.Resize += (s, e) => AdjustLayout();

            SetupCustomTitleBar();

            // --- 新增 ---
            // 设置复选框
            SetupKeepOpenCheckBox();
        }

        // --- 新增 ---
        // 创建并配置复选框的方法
        private void SetupKeepOpenCheckBox()
        {
            _keepOpenCheckBox = new CheckBox
            {
                Text = "启动后不关闭",
                AutoSize = true,
                Font = new Font("Segoe UI", 9F),
                ForeColor = Color.DimGray,
                BackColor = Color.Transparent, // 背景透明
                // 使用 Anchor 属性使其自动停靠在右下角
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            };

            // 计算其在右下角的位置（带一点边距）
            int margin = 15;
            _keepOpenCheckBox.Location = new Point(
                this.ClientSize.Width - _keepOpenCheckBox.Width - margin,
                this.ClientSize.Height - _keepOpenCheckBox.Height - margin
            );

            // 添加事件处理器，当勾选状态改变时保存设置
            _keepOpenCheckBox.CheckedChanged += (s, e) =>
            {
                _settings.KeepLauncherOpen = _keepOpenCheckBox.Checked;
                SaveSettings();
            };

            this.Controls.Add(_keepOpenCheckBox);
            _keepOpenCheckBox.BringToFront(); // 确保它在最上层，不会被其他控件遮挡
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            this.Text = _settings.WindowTitle;
            // 更新标题栏的文本
            var titleBar = this.Controls.OfType<Panel>().FirstOrDefault(p => p.Dock == DockStyle.Top);
            if (titleBar != null)
            {
                var titleLabel = titleBar.Controls.OfType<Label>().FirstOrDefault();
                if (titleLabel != null)
                {
                    titleLabel.Text = this.Text;
                }
            }

            ApplyRoundCorners();
            AdjustLayout();
            _fadeInTimer.Start();
        }

        private void ApplyRoundCorners()
        {
            if (this.WindowState == FormWindowState.Maximized)
            {
                this.Region = null;
            }
            else
            {
                this.Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, this.Width, this.Height, 20, 20));
            }
        }

        private void SetupCustomTitleBar()
        {
            Panel titleBar = new Panel
            {
                Height = 32,
                Dock = DockStyle.Top,
                BackColor = Color.FromArgb(248, 248, 248)
            };

            Label titleLabel = new Label
            {
                Text = this.Text, // 初始文本，将在Load事件中更新
                ForeColor = Color.Black,
                Font = new Font("Segoe UI Semibold", 10F),
                Location = new Point(15, 7),
                AutoSize = true
            };

            Button closeButton = new Button
            {
                Text = "✕",
                ForeColor = Color.Black,
                Font = new Font("Segoe UI", 12F),
                FlatStyle = FlatStyle.Flat,
                Size = new Size(50, 32),
                Dock = DockStyle.Right
            };
            closeButton.FlatAppearance.BorderSize = 0;
            closeButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(232, 17, 35);
            closeButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(200, 17, 35);

            closeButton.Click += (s, e) => {
                if (!_isClosing)
                {
                    _isClosing = true;
                    _closeTimer.Start();
                }
            };
            titleBar.MouseDown += TitleBar_MouseDown;
            titleBar.MouseUp += TitleBar_MouseUp;
            titleBar.MouseMove += TitleBar_MouseMove;
            titleLabel.MouseDown += TitleBar_MouseDown;
            titleLabel.MouseUp += TitleBar_MouseUp;
            titleLabel.MouseMove += TitleBar_MouseMove;

            titleBar.Controls.Add(titleLabel);
            titleBar.Controls.Add(closeButton);
            this.Controls.Add(titleBar);
            titleBar.BringToFront();
        }

        private void LoadApplications()
        {
            string appsFolderPath = Path.Combine(Application.StartupPath, AppsFolderName);
            if (!Directory.Exists(appsFolderPath))
            {
                Directory.CreateDirectory(appsFolderPath);
            }

            List<string> allFiles = new List<string>();
            foreach (var ext in _settings.SupportedExtensions)
            {
                allFiles.AddRange(Directory.GetFiles(appsFolderPath, $"*{ext}"));
            }

            if (allFiles.Count == 0)
            {
                Label emptyLabel = new Label
                {
                    Text = "这里空空如也，快把应用放进来吧！",
                    Font = new Font("Segoe UI", 16F),
                    ForeColor = Color.DarkGray,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Size = this.flowLayoutPanel1.ClientSize
                };
                this.flowLayoutPanel1.Controls.Add(emptyLabel);
                return;
            }
            this.flowLayoutPanel1.SuspendLayout();
            foreach (string filePath in allFiles)
            {
                CreateApplicationTile(filePath);
            }
            this.flowLayoutPanel1.ResumeLayout(true);
        }

        private void CreateApplicationTile(string filePath)
        {
            string targetPath = filePath;
            if (Path.GetExtension(filePath).ToLower() == ".lnk")
            {
                targetPath = GetShortcutTarget(filePath);
            }

            using (Bitmap sourceBitmap = HighResIconExtractor.GetIconFromFile(targetPath))
            {
                if (sourceBitmap == null) return;
                var tileSize = new Size(90, 111);
                var pictureBoxSize = new Size(55, 55);
                var pictureBoxLocation = new Point((90 - 55) / 2, 14);
                var labelHeight = 31;
                Panel tilePanel = new Panel
                {
                    Size = tileSize,
                    Margin = new Padding(15),
                    BackColor = this.flowLayoutPanel1.BackColor,
                    Tag = filePath
                };

                PictureBox pictureBox = new PictureBox
                {
                    Size = pictureBoxSize,
                    Location = pictureBoxLocation,
                    Tag = filePath,
                    BackColor = Color.Transparent,
                    SizeMode = PictureBoxSizeMode.Zoom
                };

                pictureBox.Image = ResizeImage(sourceBitmap, pictureBoxSize);

                Label nameLabel = new Label
                {
                    Text = Path.GetFileNameWithoutExtension(filePath),
                    ForeColor = Color.Black,
                    Font = new Font("Segoe UI Semibold", 9F),
                    Dock = DockStyle.Bottom,
                    TextAlign = ContentAlignment.TopCenter,
                    Height = labelHeight,
                    Padding = new Padding(5, 10, 5, 5),
                    Tag = filePath,
                    BackColor = Color.Transparent
                };

                var hoverColor = Color.FromArgb(240, 240, 240);
                var normalColor = this.flowLayoutPanel1.BackColor;
                EventHandler enterHandler = (s, e) => StartAnimation(tilePanel, hoverColor);
                EventHandler leaveHandler = (s, e) => StartAnimation(tilePanel, normalColor);
                tilePanel.MouseEnter += enterHandler;
                pictureBox.MouseEnter += enterHandler;
                nameLabel.MouseEnter += enterHandler;
                tilePanel.MouseLeave += leaveHandler;
                pictureBox.MouseLeave += leaveHandler;
                nameLabel.MouseLeave += leaveHandler;
                tilePanel.Click += Tile_Click;
                pictureBox.Click += Tile_Click;
                nameLabel.Click += Tile_Click;
                tilePanel.Paint += (s, e) =>
                {
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var brush = new SolidBrush(tilePanel.BackColor))
                    {
                        Rectangle rect = new Rectangle(0, 0, tilePanel.Width - 1, tilePanel.Height - 1);
                        int radius = 12;
                        GraphicsPath path = new GraphicsPath();
                        path.AddArc(rect.X, rect.Y, radius, radius, 180, 90);
                        path.AddArc(rect.Right - radius, rect.Y, radius, radius, 270, 90);
                        path.AddArc(rect.Right - radius, rect.Bottom - radius, radius, radius, 0, 90);
                        path.AddArc(rect.X, rect.Bottom - radius, radius, radius, 90, 90);
                        path.CloseFigure();
                        e.Graphics.FillPath(brush, path);
                        using (var pen = new Pen(Color.FromArgb(230, 230, 230), 1))
                        {
                            e.Graphics.DrawPath(pen, path);
                        }
                    }
                };

                tilePanel.Controls.Add(pictureBox);
                tilePanel.Controls.Add(nameLabel);
                this.flowLayoutPanel1.Controls.Add(tilePanel);
            }
        }

        #region Animation and Layout
        private void AdjustLayout()
        {
            if (flowLayoutPanel1.Controls.Count == 0 || !flowLayoutPanel1.IsHandleCreated) return;

            var firstTile = flowLayoutPanel1.Controls[0];
            int tileFullWidth = firstTile.Width + firstTile.Margin.Left + firstTile.Margin.Right;
            int panelWidth = flowLayoutPanel1.ClientSize.Width;
            if (tileFullWidth <= 0) return;
            int tilesPerRow = Math.Max(1, panelWidth / tileFullWidth);
            int totalContentWidth = tilesPerRow * tileFullWidth;
            int emptySpace = panelWidth - totalContentWidth;
            int sidePadding = Math.Max(25, emptySpace / 2);
            flowLayoutPanel1.Padding = new Padding(sidePadding, 25, sidePadding, 25);
        }

        private void StartAnimation(Control control, Color targetColor)
        {
            if (_animatedControls.TryGetValue(control, out var existingState) && existingState.TargetColor == targetColor)
            {
                return;
            }

            var state = new AnimationState
            {
                StartColor = control.BackColor,
                TargetColor = targetColor,
                StartTime = DateTime.Now
            };
            _animatedControls[control] = state;
        }

        private void AnimationTimer_Tick(object sender, EventArgs e)
        {
            var finishedAnimations = new List<Control>();
            foreach (var kvp in _animatedControls.ToList())
            {
                var control = kvp.Key;
                var state = kvp.Value;

                double elapsed = (DateTime.Now - state.StartTime).TotalMilliseconds;
                double progress = Math.Min(elapsed / state.Duration.TotalMilliseconds, 1.0);
                int r = (int)(state.StartColor.R + (state.TargetColor.R - state.StartColor.R) * progress);
                int g = (int)(state.StartColor.G + (state.TargetColor.G - state.StartColor.G) * progress);
                int b = (int)(state.StartColor.B + (state.TargetColor.B - state.StartColor.B) * progress);

                control.BackColor = Color.FromArgb(r, g, b);
                control.Invalidate();

                if (progress >= 1.0)
                {
                    finishedAnimations.Add(control);
                }
            }
            foreach (var control in finishedAnimations)
            {
                _animatedControls.Remove(control);
            }
        }

        private void FadeInTimer_Tick(object sender, EventArgs e)
        {
            if (this.Opacity < 1.0)
            {
                this.Opacity += 0.3;
            }
            else
            {
                this.Opacity = 1.0;
                _fadeInTimer.Stop();
                _fadeInTimer.Dispose();
            }
        }

        private void CloseTimer_Tick(object sender, EventArgs e)
        {
            if (this.Opacity > 0.05)
            {
                this.Opacity -= 0.05;
            }
            else
            {
                _closeTimer.Stop();
                Application.Exit();
            }
        }
        #endregion

        private void Tile_Click(object sender, EventArgs e)
        {
            Control clickedControl = sender as Control;
            string filePath = "";
            if (clickedControl is Panel)
            {
                filePath = clickedControl.Tag as string;
            }
            else if (clickedControl?.Parent is Panel)
            {
                filePath = clickedControl.Parent.Tag as string;
            }

            if (!string.IsNullOrEmpty(filePath))
            {
                try
                {
                    Process.Start(filePath);

                    // --- 修改 ---
                    // 根据复选框的选中状态决定是否关闭
                    if (!_settings.KeepLauncherOpen)
                    {
                        if (!_isClosing)
                        {
                            _isClosing = true;
                            _closeTimer.Start();
                        }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"无法启动: {Path.GetFileName(filePath)}\n\n错误: {ex.Message}", "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_isClosing)
            {
                e.Cancel = true;
                _isClosing = true;
                _closeTimer.Start();
            }
        }
        #region Window Dragging Handlers
        private void TitleBar_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { _isDragging = true; _dragStartPoint = new Point(e.X, e.Y); }
        }
        private void TitleBar_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { _isDragging = false; }
        }
        private void TitleBar_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDragging) { Point p = PointToScreen(e.Location); Location = new Point(p.X - _dragStartPoint.X, p.Y - _dragStartPoint.Y); }
        }
        #endregion

        private static Bitmap ResizeImage(Image image, Size newSize)
        {
            if (image == null) return null;
            var destRect = new Rectangle(0, 0, newSize.Width, newSize.Height);
            var destImage = new Bitmap(newSize.Width, newSize.Height);

            destImage.SetResolution(image.HorizontalResolution, image.VerticalResolution);

            using (var graphics = Graphics.FromImage(destImage))
            {
                graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
                graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

                using (var wrapMode = new System.Drawing.Imaging.ImageAttributes())
                {
                    wrapMode.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY);
                    graphics.DrawImage(image, destRect, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, wrapMode);
                }
            }

            return destImage;
        }
    }
}
