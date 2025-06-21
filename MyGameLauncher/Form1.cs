using System;
using System.Collections.Generic;
using System.ComponentModel;
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

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct SHELLEXECUTEINFO
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            [MarshalAs(UnmanagedType.LPTStr)]
            public string lpVerb;
            [MarshalAs(UnmanagedType.LPTStr)]
            public string lpFile;
            [MarshalAs(UnmanagedType.LPTStr)]
            public string lpParameters;
            [MarshalAs(UnmanagedType.LPTStr)]
            public string lpDirectory;
            public int nShow;
            public IntPtr hInstApp;
            public IntPtr lpIDList;
            [MarshalAs(UnmanagedType.LPTStr)]
            public string lpClass;
            public IntPtr hkeyClass;
            public uint dwHotKey;
            public IntPtr hIcon;
            public IntPtr hProcess;
        }

        private const uint SEE_MASK_INVOKEIDLIST = 12;

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

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

        private CheckBox _keepOpenCheckBox;
        private ContextMenuStrip _tileContextMenu;

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

            SetupModernUI();
            SetupContextMenu();

            LoadSettings();
            LoadApplications();
        }

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
                SaveSettings();
            }

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

            this.AllowDrop = true;
            this.flowLayoutPanel1.AllowDrop = true;
            this.DragEnter += Form1_DragEnter;
            this.DragDrop += Form1_DragDrop;
            this.flowLayoutPanel1.DragEnter += Form1_DragEnter;
            this.flowLayoutPanel1.DragDrop += Form1_DragDrop;

            this.BackColor = Color.White;
            this.flowLayoutPanel1.BackColor = this.BackColor;

            this.Load += Form1_Load;
            this.Resize += (s, e) => AdjustLayout();

            SetupCustomTitleBar();
            SetupKeepOpenCheckBox();
        }

        private void SetupKeepOpenCheckBox()
        {
            _keepOpenCheckBox = new CheckBox
            {
                Text = "启动后不关闭",
                AutoSize = true,
                Font = new Font("Segoe UI", 9F),
                ForeColor = Color.DimGray,
                BackColor = Color.Transparent,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            };

            int margin = 15;
            _keepOpenCheckBox.Location = new Point(
                this.ClientSize.Width - _keepOpenCheckBox.Width - margin,
                this.ClientSize.Height - _keepOpenCheckBox.Height - margin
            );

            _keepOpenCheckBox.CheckedChanged += (s, e) =>
            {
                _settings.KeepLauncherOpen = _keepOpenCheckBox.Checked;
                SaveSettings();
            };

            this.Controls.Add(_keepOpenCheckBox);
            _keepOpenCheckBox.BringToFront();
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            this.Text = _settings.WindowTitle;
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
                Text = this.Text,
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
                    Size = this.flowLayoutPanel1.ClientSize,
                    Anchor = AnchorStyles.None
                };
                this.flowLayoutPanel1.Controls.Add(emptyLabel);
                emptyLabel.Location = new Point(
                    (flowLayoutPanel1.ClientSize.Width - emptyLabel.Width) / 2,
                    (flowLayoutPanel1.ClientSize.Height - emptyLabel.Height) / 2
                );
                return;
            }
            this.flowLayoutPanel1.SuspendLayout();
            foreach (string filePath in allFiles)
            {
                CreateApplicationTile(filePath, false);
            }
            this.flowLayoutPanel1.ResumeLayout(true);
        }

        private void CreateApplicationTile(string filePath, bool useAnimation)
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
                    Tag = filePath,
                    ContextMenuStrip = _tileContextMenu
                };

                PictureBox pictureBox = new PictureBox
                {
                    Size = pictureBoxSize,
                    Location = pictureBoxLocation,
                    Tag = filePath,
                    BackColor = Color.Transparent,
                    SizeMode = PictureBoxSizeMode.Zoom,
                    ContextMenuStrip = _tileContextMenu
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
                    BackColor = Color.Transparent,
                    ContextMenuStrip = _tileContextMenu
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

                if (useAnimation)
                {
                    tilePanel.BackColor = Color.FromArgb(220, 235, 255);
                    StartAnimation(tilePanel, this.flowLayoutPanel1.BackColor);
                }
            }
        }

        #region Animation and Layout
        private void AdjustLayout()
        {
            if (flowLayoutPanel1.Controls.Count == 0 || !flowLayoutPanel1.IsHandleCreated) return;

            if (flowLayoutPanel1.Controls.Count == 1 && flowLayoutPanel1.Controls[0] is Label emptyLabel)
            {
                emptyLabel.Size = flowLayoutPanel1.ClientSize;
                emptyLabel.Location = new Point(0, 0);
                return;
            }

            var firstTile = flowLayoutPanel1.Controls.OfType<Panel>().FirstOrDefault();
            if (firstTile == null) return;

            int tileFullWidth = firstTile.Width + firstTile.Margin.Left + firstTile.Margin.Right;
            int panelWidth = flowLayoutPanel1.ClientSize.Width;
            if (tileFullWidth <= 0) return;
            int tilesPerRow = Math.Max(1, panelWidth / tileFullWidth);
            int totalContentWidth = tilesPerRow * tileFullWidth;
            int emptySpace = panelWidth - totalContentWidth;
            int sidePadding = Math.Max(40, emptySpace / 2);
            flowLayoutPanel1.Padding = new Padding(sidePadding, 40, sidePadding, 40);
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
                if (control.IsDisposed)
                {
                    finishedAnimations.Add(control);
                    continue;
                }
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

        #region Event Handlers (Click, Closing, Dragging)
        private void Tile_Click(object sender, EventArgs e)
        {
            if (e is MouseEventArgs me && me.Button == MouseButtons.Right) return;

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

        #region Drag and Drop
        private void Form1_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files.Any(file => _settings.SupportedExtensions.Contains(Path.GetExtension(file).ToLowerInvariant())))
                {
                    e.Effect = DragDropEffects.Copy;
                }
                else
                {
                    e.Effect = DragDropEffects.None;
                }
            }
            else
            {
                e.Effect = DragDropEffects.None;
            }
        }

        private void Form1_DragDrop(object sender, DragEventArgs e)
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            string appsFolderPath = Path.Combine(Application.StartupPath, AppsFolderName);

            var emptyLabel = flowLayoutPanel1.Controls.OfType<Label>().FirstOrDefault(l => l.Text.Contains("空空如也"));
            if (emptyLabel != null)
            {
                flowLayoutPanel1.Controls.Remove(emptyLabel);
                emptyLabel.Dispose();
            }

            this.flowLayoutPanel1.SuspendLayout();

            foreach (var filePath in files)
            {
                if (!_settings.SupportedExtensions.Contains(Path.GetExtension(filePath).ToLowerInvariant()))
                {
                    continue;
                }

                string destFileName = Path.GetFileName(filePath);
                string destPath = Path.Combine(appsFolderPath, destFileName);

                if (File.Exists(destPath))
                {
                    MessageBox.Show($"文件 '{destFileName}' 已存在于 Apps 文件夹中。\n添加操作已中止。", "文件冲突", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    continue;
                }

                try
                {
                    File.Copy(filePath, destPath);
                    CreateApplicationTile(destPath, true);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"无法添加文件 '{destFileName}'.\n\n错误: {ex.Message}", "添加失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            this.flowLayoutPanel1.ResumeLayout(true);
            AdjustLayout();
        }
        #endregion

        #region Context Menu
        private void SetupContextMenu()
        {
            _tileContextMenu = new ContextMenuStrip();
            _tileContextMenu.Renderer = new PinkMenuRenderer();
            _tileContextMenu.Padding = new Padding(2, 2, 2, 2);
            _tileContextMenu.Opening += OnContextMenuOpening;

            var itemRunAsAdmin = new ToolStripMenuItem("以管理员身份运行", null, OnRunAsAdminClick) { Height = 30 };
            var itemOpenLocation = new ToolStripMenuItem("打开文件位置", null, OnOpenLocationClick) { Height = 30 };
            var itemProperties = new ToolStripMenuItem("属性", null, OnPropertiesClick) { Height = 30 };
            var itemRemove = new ToolStripMenuItem("从列表中移除", null, OnRemoveClick) { Height = 30 };

            _tileContextMenu.Items.Add(itemRunAsAdmin);
            _tileContextMenu.Items.Add(itemOpenLocation);
            _tileContextMenu.Items.Add(itemProperties);
            _tileContextMenu.Items.Add(new ToolStripSeparator());
            _tileContextMenu.Items.Add(itemRemove);
        }

        private void OnContextMenuOpening(object sender, CancelEventArgs e)
        {
            var menu = sender as ContextMenuStrip;
            if (menu == null) return;

            var sourceControl = menu.SourceControl;
            if (sourceControl == null)
            {
                e.Cancel = true;
                return;
            }

            Panel tilePanel = sourceControl as Panel ?? sourceControl.Parent as Panel;
            if (tilePanel != null)
            {
                menu.Tag = tilePanel;
            }
            else
            {
                e.Cancel = true;
            }
        }

        private void OnRunAsAdminClick(object sender, EventArgs e)
        {
            if (!GetTileAndPathFromSender(sender, out _, out string filePath)) return;

            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo(filePath)
                {
                    Verb = "runas",
                    UseShellExecute = true
                };
                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法以管理员身份启动: {Path.GetFileName(filePath)}\n\n错误: {ex.Message}", "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnOpenLocationClick(object sender, EventArgs e)
        {
            if (!GetTileAndPathFromSender(sender, out _, out string filePath)) return;

            try
            {
                Process.Start("explorer.exe", $"/select, \"{filePath}\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法打开文件位置: {ex.Message}", "操作失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnPropertiesClick(object sender, EventArgs e)
        {
            if (!GetTileAndPathFromSender(sender, out _, out string filePath)) return;

            string targetPath = filePath;
            if (Path.GetExtension(filePath).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                targetPath = GetShortcutTarget(filePath);
            }

            ShowFileProperties(targetPath);
        }

        private void OnRemoveClick(object sender, EventArgs e)
        {
            if (!GetTileAndPathFromSender(sender, out Panel tilePanel, out string filePath)) return;

            var result = MessageBox.Show($"您确定要从列表中移除 '{Path.GetFileNameWithoutExtension(filePath)}' 吗？\n\n这将从 Apps 文件夹中永久删除该文件。",
                "确认移除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (result == DialogResult.Yes)
            {
                try
                {
                    flowLayoutPanel1.Controls.Remove(tilePanel);
                    tilePanel.Dispose();
                    AdjustLayout();

                    File.Delete(filePath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"移除文件时出错: {ex.Message}", "移除失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private bool GetTileAndPathFromSender(object sender, out Panel tilePanel, out string filePath)
        {
            tilePanel = null;
            filePath = null;

            var menuItem = sender as ToolStripItem;
            if (menuItem == null) return false;

            var menu = menuItem.Owner as ContextMenuStrip;
            if (menu == null) return false;

            tilePanel = menu.Tag as Panel;
            if (tilePanel == null) return false;

            filePath = tilePanel.Tag as string;
            return !string.IsNullOrEmpty(filePath);
        }

        public bool ShowFileProperties(string Filename)
        {
            SHELLEXECUTEINFO info = new SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf(typeof(SHELLEXECUTEINFO)),
                hwnd = this.Handle,
                lpVerb = "properties",
                lpFile = Filename,
                nShow = 1,
                fMask = SEE_MASK_INVOKEIDLIST
            };
            return ShellExecuteEx(ref info);
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
                graphics.CompositingMode = CompositingMode.SourceOver;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

                using (var wrapMode = new System.Drawing.Imaging.ImageAttributes())
                {
                    wrapMode.SetWrapMode(WrapMode.TileFlipXY);
                    graphics.DrawImage(image, destRect, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, wrapMode);
                }
            }

            return destImage;
        }

        #region Custom Menu Renderer
        private class PinkMenuRenderer : ToolStripProfessionalRenderer
        {
            // --- 修复：将颜色字段声明为 static readonly ---
            private static readonly Color _backColor = Color.FromArgb(255, 250, 252);
            private static readonly Color _hoverColor = Color.FromArgb(255, 230, 240);
            private static readonly Color _borderColor = Color.FromArgb(255, 220, 230);
            private static readonly Color _separatorColor = Color.FromArgb(255, 220, 230);
            private const int MenuRadius = 8;

            public PinkMenuRenderer() : base(new PinkColorTable())
            {
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = e.AffectedBounds;
                rect.Width--;
                rect.Height--;

                using (var path = GetRoundedRect(rect, MenuRadius))
                {
                    // --- 修复：引用静态字段 ---
                    using (var pen = new Pen(_borderColor, 1))
                    {
                        g.DrawPath(pen, path);
                    }
                    if (e.ToolStrip.Region == null || e.ToolStrip.Region.GetBounds(g) != rect)
                    {
                        e.ToolStrip.Region = new Region(path);
                    }
                }
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                if (!e.Item.Enabled) return;

                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);

                if (e.Item.Selected)
                {
                    // --- 修复：引用静态字段 ---
                    using (var brush = new SolidBrush(_hoverColor))
                    using (var path = GetRoundedRect(rect, MenuRadius - 2))
                    {
                        g.FillPath(brush, path);
                    }
                }
                else
                {
                    base.OnRenderMenuItemBackground(e);
                }
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.Item.ForeColor = e.Item.Selected ? Color.Black : Color.Black;
                var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis;
                var textRect = e.TextRectangle;
                textRect.X += 8;
                textRect.Width -= 8;
                TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, textRect, e.TextColor, flags);
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                var rect = e.Item.ContentRectangle;
                // --- 修复：引用静态字段 ---
                using (var pen = new Pen(_separatorColor, 1))
                {
                    e.Graphics.DrawLine(pen, rect.Left + 25, rect.Y + rect.Height / 2, rect.Right - 5, rect.Y + rect.Height / 2);
                }
            }

            private static GraphicsPath GetRoundedRect(Rectangle baseRect, int radius)
            {
                GraphicsPath path = new GraphicsPath();
                if (radius <= 0) { path.AddRectangle(baseRect); return path; }
                int diameter = radius * 2;
                Rectangle arc = new Rectangle(baseRect.Location, new Size(diameter, diameter));
                path.AddArc(arc, 180, 90);
                arc.X = baseRect.Right - diameter;
                path.AddArc(arc, 270, 90);
                arc.Y = baseRect.Bottom - diameter;
                path.AddArc(arc, 0, 90);
                arc.X = baseRect.Left;
                path.AddArc(arc, 90, 90);
                path.CloseFigure();
                return path;
            }

            // --- 修复：此类现在直接引用外部类的静态颜色字段 ---
            private class PinkColorTable : ProfessionalColorTable
            {
                public override Color ToolStripDropDownBackground => _backColor;
                public override Color MenuBorder => _borderColor;
                public override Color MenuItemSelected => _hoverColor;
                public override Color ImageMarginGradientBegin => _backColor;
                public override Color ImageMarginGradientMiddle => _backColor;
                public override Color ImageMarginGradientEnd => _backColor;
            }
        }
        #endregion
    }
}
