using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace AirPodsController
{
    public enum NoiseMode { ANC, Transparency }

    public partial class MainWindow : Window
    {
        private IntPtr hDevice;
        private NoiseMode currentNoiseMode = NoiseMode.ANC;
#pragma warning disable CS0414
        private int lastKnownMode = -1;
        private int lastKnownCaseBattery = -1;
        private bool lowBatteryWarningShown = false;
#pragma warning restore CS0414

        private AppSettings? config;
        private string configPath = "";
        private System.Windows.Forms.NotifyIcon? trayIcon;

        private readonly List<int> registeredHotkeyIds = new List<int>();
        private int nextHotkeyId = 100;
        private readonly Dictionary<int, string> hotkeyActions = new Dictionary<int, string>();

        private readonly object usbLock = new object();
        private bool deviceDisconnected = false;

        private static readonly Guid AirPodsGuid = new Guid("fc71b33d-d528-4763-a86c-78777c7bcd7b");
        private static readonly byte[] InitCmd = { 0x00, 0x00, 0x04, 0x00, 0x01, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        private static readonly byte[] BatteryCmd = { 0x04, 0x00, 0x04, 0x00, 0x0F, 0x00, 0xFF, 0xFF, 0xFF, 0xFF };
        private static readonly byte[] ModeCmdBase = { 0x04, 0x00, 0x04, 0x00, 0x09, 0x00, 0x0D, 0xFF, 0x00, 0x00, 0x00 };
        private static readonly byte[] StatusCmd = { 0x04, 0x00, 0x04, 0x00, 0x09, 0x00, 0x0C, 0x01, 0x00, 0x00, 0x00 };
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Get_Device_Interface_List_Size(ref uint pulLen, ref Guid InterfaceClassGuid, IntPtr pDeviceID, uint ulFlags);
        [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Get_Device_Interface_List(ref Guid InterfaceClassGuid, IntPtr pDeviceID, char[] Buffer, uint BufferLen, uint ulFlags);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_SHIFT = 0x0004;
        private const int WM_HOTKEY = 0x0312;

        public MainWindow()
        {
            InitializeComponent();
            this.ContentRendered += MainWindow_ContentRendered;
            configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirPodsController", "config.json");
            hDevice = INVALID_HANDLE_VALUE;
            LoadConfig();
            LoadImages();
            InitTray();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source?.AddHook(WndProc);
            RegisterAllHotkeys();
        }

        private void MainWindow_ContentRendered(object? sender, EventArgs e)
        {
            UpdateModeIcons();
        }

        private void LoadImages()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string resDir = Path.Combine(baseDir, "Resources");
                LoadImageFromFile(LogoImage, resDir, "new_logo.png");
                LoadImageFromFile(EarbudsImage, resDir, "airpods_earbuds.png");
                LoadImageFromFile(CaseImage, resDir, "airpods_case.png");
            }
            catch (Exception ex)
            {
                StatusText.Text = "Ошибка: " + ex.Message;
            }
        }

        private void LoadImageFromFile(System.Windows.Controls.Image imageControl, string folder, string fileName)
        {
            string filePath = Path.Combine(folder, fileName);
            if (File.Exists(filePath))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(filePath);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                imageControl.Source = bitmap;
            }
        }

        private void UpdateModeIcons()
        {
            // Анимация "езды" синего фона
            if (AncButton.ActualWidth > 0)
            {
                double targetX = currentNoiseMode == NoiseMode.ANC
                    ? 0
                    : AncButton.ActualWidth + 6; // ширина кнопки + margin (3+3)

                var animation = new DoubleAnimation
                {
                    From = HighlightTransform.X,
                    To = targetX,
                    Duration = TimeSpan.FromMilliseconds(300),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
                };

                HighlightTransform.BeginAnimation(TranslateTransform.XProperty, animation);
            }

            // Меняем цвет текста
            AncText.Foreground = currentNoiseMode == NoiseMode.ANC
                ? Brushes.White
                : new SolidColorBrush(Color.FromRgb(28, 28, 30));

            TransText.Foreground = currentNoiseMode == NoiseMode.Transparency
                ? Brushes.White
                : new SolidColorBrush(Color.FromRgb(28, 28, 30));

            lastKnownMode = currentNoiseMode == NoiseMode.ANC ? 2 : 3;
        }

        private void InitTray()
        {
            trayIcon = new System.Windows.Forms.NotifyIcon();
            trayIcon.Icon = System.Drawing.SystemIcons.Application;
            trayIcon.Text = "AirPods Controller";
            trayIcon.Visible = true;

            var menu = new System.Windows.Forms.ContextMenuStrip();
            var openItem = new System.Windows.Forms.ToolStripMenuItem("Открыть");
            openItem.Click += (s, e) => { this.Show(); this.Activate(); };
            menu.Items.Add(openItem);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            var settingsItem = new System.Windows.Forms.ToolStripMenuItem("Настройки");
            settingsItem.Click += (s, e) => Settings_Click(this, new RoutedEventArgs());
            menu.Items.Add(settingsItem);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            var exitItem = new System.Windows.Forms.ToolStripMenuItem("Выход");
            exitItem.Click += (s, e) => { UnregisterAllHotkeys(); trayIcon?.Dispose(); Application.Current.Shutdown(); };
            menu.Items.Add(exitItem);

            trayIcon.ContextMenuStrip = menu;
            trayIcon.DoubleClick += (s, e) => { this.Show(); this.Activate(); };
        }

        private void DrainBuffer()
        {
            if (hDevice == INVALID_HANDLE_VALUE) return;
            byte[] drain = new byte[512];
            for (int i = 0; i < 3; i++)
            {
                ReadFile(hDevice, drain, 512, out uint r, IntPtr.Zero);
                if (r == 0) break;
                Thread.Sleep(10);
            }
        }

        private byte[]? SendCommand(byte[] cmd, int expectedOpcode, int minBytes, int timeoutMs = 500)
        {
            if (hDevice == INVALID_HANDLE_VALUE) return null;
            DrainBuffer();
            WriteFile(hDevice, cmd, (uint)cmd.Length, out uint written, IntPtr.Zero);
            byte[] buf = new byte[512];
            int elapsed = 0;
            while (elapsed < timeoutMs)
            {
                bool ok = ReadFile(hDevice, buf, 512, out uint bytesRead, IntPtr.Zero);
                if (ok && bytesRead >= (uint)minBytes && buf[0] == 0x04 && buf[4] == (byte)expectedOpcode)
                {
                    byte[] result = new byte[bytesRead];
                    Array.Copy(buf, result, bytesRead);
                    return result;
                }
                Thread.Sleep(30);
                elapsed += 30;
            }
            return null;
        }

        private async void Connect_Click(object sender, RoutedEventArgs e)
        {
            await Task.Run(() =>
            {
                lock (usbLock)
                {
                    if (hDevice != INVALID_HANDLE_VALUE)
                    {
                        byte[]? response = SendCommand(StatusCmd, 0x09, 11, 500);
                        if (response != null)
                        {
                            Dispatcher.Invoke(() => StatusText.Text = "Уже подключено");
                            deviceDisconnected = false;
                            return;
                        }
                        else
                        {
                            CloseHandle(hDevice);
                            hDevice = INVALID_HANDLE_VALUE;
                            deviceDisconnected = true;
                            Dispatcher.Invoke(() => StatusText.Text = "Переподключение...");
                        }
                    }

                    uint size = 0;
                    Guid guid = AirPodsGuid;
                    if (CM_Get_Device_Interface_List_Size(ref size, ref guid, IntPtr.Zero, 0) != 0)
                    {
                        Dispatcher.Invoke(() => StatusText.Text = "Драйвер не найден");
                        return;
                    }
                    char[] buffer = new char[size];
                    if (CM_Get_Device_Interface_List(ref guid, IntPtr.Zero, buffer, size, 0) != 0)
                    {
                        Dispatcher.Invoke(() => StatusText.Text = "Ошибка пути");
                        return;
                    }
                    string path = new string(buffer).TrimEnd('\0');
                    if (string.IsNullOrEmpty(path))
                    {
                        Dispatcher.Invoke(() => StatusText.Text = "Устройство не найдено");
                        return;
                    }

                    hDevice = CreateFile(path, 0xC0000000, 0x3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (hDevice == INVALID_HANDLE_VALUE)
                    {
                        Dispatcher.Invoke(() => StatusText.Text = "Не удалось открыть");
                        return;
                    }

                    deviceDisconnected = false;
                    WriteFile(hDevice, InitCmd, (uint)InitCmd.Length, out uint w, IntPtr.Zero);
                    string name = GetRealDeviceName();
                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = "Подключено";
                        DeviceNameText.Text = name;
                    });
                    ShowNotification("AirPods подключены", "Устройство готово к работе");
                }
            });
        }

        private async void ANC_Click(object sender, RoutedEventArgs e)
        {
            if (hDevice == INVALID_HANDLE_VALUE || deviceDisconnected)
            {
                StatusText.Text = "Сначала подключите AirPods";
                return;
            }

            currentNoiseMode = NoiseMode.ANC;

            await Task.Run(() =>
            {
                lock (usbLock)
                {
                    if (hDevice == INVALID_HANDLE_VALUE) return;
                    byte[] cmd = (byte[])ModeCmdBase.Clone();
                    cmd[7] = 2;
                    var response = SendCommand(cmd, 0x09, 11, 500);

                    if (response != null)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            StatusText.Text = "Шумоподавление";
                            UpdateModeIcons();
                            if (config?.NotifyModeChange == true) ShowNotification("Режим", "Шумоподавление");
                        });
                    }
                    else
                    {
                        Dispatcher.Invoke(() => StatusText.Text = "Не удалось переключить");
                    }
                }
            });
        }

        private async void Transparency_Click(object sender, RoutedEventArgs e)
        {
            if (hDevice == INVALID_HANDLE_VALUE || deviceDisconnected)
            {
                StatusText.Text = "Сначала подключите AirPods";
                return;
            }

            currentNoiseMode = NoiseMode.Transparency;

            await Task.Run(() =>
            {
                lock (usbLock)
                {
                    if (hDevice == INVALID_HANDLE_VALUE) return;
                    byte[] cmd = (byte[])ModeCmdBase.Clone();
                    cmd[7] = 3;
                    var response = SendCommand(cmd, 0x09, 11, 500);

                    if (response != null)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            StatusText.Text = "Прозрачность";
                            UpdateModeIcons();
                            if (config?.NotifyModeChange == true) ShowNotification("Режим", "Прозрачность");
                        });
                    }
                    else
                    {
                        Dispatcher.Invoke(() => StatusText.Text = "Не удалось переключить");
                    }
                }
            });
        }

        private async void CheckBattery_Click(object sender, RoutedEventArgs e)
        {
            await Task.Run(() =>
            {
                lock (usbLock)
                {
                    if (hDevice == INVALID_HANDLE_VALUE) return;
                    byte[]? r = SendCommand(BatteryCmd, 0x0F, 22, 800);
                    if (r == null || r.Length < 22) return;

                    int left = r[9], right = r[14], caseBat = r[19];
                    if (left > 100 || right > 100 || caseBat > 100) return;

                    Dispatcher.Invoke(() =>
                    {
                        LeftBatteryText.Text = left + "%";
                        RightBatteryText.Text = right + "%";
                        CaseBatteryText.Text = caseBat + "%";
                        LeftBatteryText.Foreground = Brushes.ForestGreen;
                        RightBatteryText.Foreground = Brushes.ForestGreen;
                        CaseBatteryText.Foreground = Brushes.ForestGreen;
                        StatusText.Text = "Заряд обновлён";
                    });

                    if (config != null && config.NotifyLowBattery && !lowBatteryWarningShown)
                    {
                        int minBat = Math.Min(left, Math.Min(right, caseBat));
                        if (minBat <= config.LowBatteryThreshold)
                        {
                            ShowNotification("Низкий заряд", $"Л:{left}% П:{right}% Кейс:{caseBat}%");
                            lowBatteryWarningShown = true;
                        }
                    }
                    if (caseBat > lastKnownCaseBattery && lastKnownCaseBattery != -1)
                        lowBatteryWarningShown = false;
                    lastKnownCaseBattery = caseBat;

                    if (config != null)
                    {
                        config.BatteryHistory ??= new List<BatteryHistory>();
                        config.BatteryHistory.Add(new BatteryHistory
                        {
                            Timestamp = DateTime.Now,
                            Left = left,
                            Right = right,
                            Case = caseBat
                        });
                        if (config.BatteryHistory.Count > 100)
                            config.BatteryHistory.RemoveRange(0, config.BatteryHistory.Count - 100);
                    }
                }
            });
        }

        private void ShowNotification(string title, string message)
        {
            trayIcon?.ShowBalloonTip(3000, title, message, System.Windows.Forms.ToolTipIcon.Info);
        }

        private void LoadConfig()
        {
            if (File.Exists(configPath))
            {
                try { config = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(configPath)); }
                catch { config = new AppSettings(); }
            }
            else { config = new AppSettings(); }
            if (config == null) config = new AppSettings();
        }

        private string GetRealDeviceName()
        {
            try
            {
                var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%AirPods%'");
                foreach (ManagementObject obj in searcher.Get())
                {
                    string? name = obj["Name"]?.ToString();
                    if (!string.IsNullOrEmpty(name)) return name!;
                }
            }
            catch { }
            return "AirPods Pro";
        }

        private void Settings_Click(object? sender, RoutedEventArgs e)
        {
            if (config == null) return;
            var w = new SettingsWindow(config, configPath);
            w.Owner = this;
            if (w.ShowDialog() == true)
            {
                LoadConfig();
                RegisterAllHotkeys();
            }
        }

        private void Notifications_Click(object sender, RoutedEventArgs e) => Settings_Click(sender, e);

        private void About_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.MessageBox.Show("AirPods Controller\n\nВерсия: 2.0 (WPF)\nУправление AirPods Pro на Windows\n\n© 2024", "О программе", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }

        private void RegisterAllHotkeys()
        {
            if (config == null) return;

            // ВСЕГДА используем IntPtr.Zero для глобальных hotkey
            IntPtr hWnd = IntPtr.Zero;

            // Удаляем старые (тоже с hWnd=0)
            foreach (var id in registeredHotkeyIds)
                UnregisterHotKey(hWnd, id);
            registeredHotkeyIds.Clear();
            hotkeyActions.Clear();
            nextHotkeyId = 100;

            if (!string.IsNullOrWhiteSpace(config.ANCHotkey))
                RegHotkey(hWnd, config.ANCHotkey, config.ANCCtrl, config.ANCAlt, config.ANCShift, "ANC");
            if (!string.IsNullOrWhiteSpace(config.TransparencyHotkey))
                RegHotkey(hWnd, config.TransparencyHotkey, config.TransCtrl, config.TransAlt, config.TransShift, "Trans");
            if (!string.IsNullOrWhiteSpace(config.BatteryHotkey))
                RegHotkey(hWnd, config.BatteryHotkey, config.BatCtrl, config.BatAlt, config.BatShift, "Bat");
        }

        private bool RegHotkey(IntPtr hWnd, string keyName, bool ctrl, bool alt, bool shift, string action)
        {
            try
            {
                uint mods = 0;
                if (ctrl) mods |= MOD_CONTROL;
                if (alt) mods |= MOD_ALT;
                if (shift) mods |= MOD_SHIFT;

                var key = (Key)Enum.Parse(typeof(Key), keyName);
                uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                int id = nextHotkeyId++;

                bool result = RegisterHotKey(hWnd, id, mods, vk);
                if (result)
                {
                    registeredHotkeyIds.Add(id);
                    hotkeyActions[id] = action;
                }
                return result;
            }
            catch { return false; }
        }

        private void UnregisterAllHotkeys()
        {
            IntPtr hWnd = IntPtr.Zero;
            foreach (var id in registeredHotkeyIds)
                UnregisterHotKey(hWnd, id);
            registeredHotkeyIds.Clear();
            hotkeyActions.Clear();
            nextHotkeyId = 100;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (hotkeyActions.TryGetValue(id, out string? action))
                {
                    if (action == "ANC") ANC_Click(this, new RoutedEventArgs());
                    else if (action == "Trans") Transparency_Click(this, new RoutedEventArgs());
                    else if (action == "Bat") CheckBattery_Click(this, new RoutedEventArgs());
                }
                handled = true;
            }
            return IntPtr.Zero;
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            this.Hide();
        }
    }

    public class AppSettings
    {
        public bool NotifyLowBattery { get; set; } = true;
        public bool NotifyConnect { get; set; } = true;
        public bool NotifyModeChange { get; set; } = true;
        public int LowBatteryThreshold { get; set; } = 20;
        public int CheckInterval { get; set; } = 30;
        public bool SoundNotification { get; set; } = false;
        public string ANCHotkey { get; set; } = "";
        public string TransparencyHotkey { get; set; } = "";
        public string BatteryHotkey { get; set; } = "";
        public bool ANCCtrl { get; set; } = false;
        public bool ANCAlt { get; set; } = false;
        public bool ANCShift { get; set; } = false;
        public bool TransCtrl { get; set; } = false;
        public bool TransAlt { get; set; } = false;
        public bool TransShift { get; set; } = false;
        public bool BatCtrl { get; set; } = false;
        public bool BatAlt { get; set; } = false;
        public bool BatShift { get; set; } = false;
        public bool AutoStart { get; set; } = false;
        public bool DarkTheme { get; set; } = false;
        public List<BatteryHistory>? BatteryHistory { get; set; } = new List<BatteryHistory>();
    }

    public class BatteryHistory
    {
        public DateTime Timestamp { get; set; }
        public int Left { get; set; }
        public int Right { get; set; }
        public int Case { get; set; }
    }
}