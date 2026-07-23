using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using System.Windows.Threading;

namespace AirPodsController
{
    public enum NoiseMode { Off, ANC, Transparency, Adaptive }

    public partial class MainWindow : Window
    {
        // === USB / Device ===
        private IntPtr hDevice;
        private readonly object usbLock = new();
        private bool deviceDisconnected = false;
        private NoiseMode currentNoiseMode = NoiseMode.ANC;
        private int lastKnownMode = -1;
        private int lastKnownCaseBattery = -1;
        private bool lowBatteryWarningShown = false;

        // === Window / Hotkeys ===
        private IntPtr _windowHandle;
        private readonly List<int> registeredHotkeyIds = new();
        private int nextHotkeyId = 100;
        private readonly Dictionary<int, string> hotkeyActions = new();

        // === Config ===
        private AppSettings? config;
        private string configPath = "";

        // === Tray ===
        private System.Windows.Forms.NotifyIcon? trayIcon;

        // === Timers ===
        private DispatcherTimer? _autoConnectTimer;
        private DispatcherTimer? _batteryTimer;
        private bool _isConnecting = false;
        private bool _isModeBusy = false;

        // === Sequence counter for AP2 (instance, resets on reconnect) ===
        private byte _sequenceCounter = 0;

        // === Constants (exact from driver) ===
        private static readonly Guid AirPodsGuid = new("fc71b33d-d528-4763-a86c-78777c7bcd7b");
        // init connection: { 00,00,04,00,01,00,02,00,00,00,00,00,00,00,00,00 }
        private static readonly byte[] InitCmd = { 0x00, 0x00, 0x04, 0x00, 0x01, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        // request battery status: { 04, 00, 04, 00, 0x0F, 00, 0xFF, 0xFF, 0xFF, 0xFF }
        private static readonly byte[] BatteryCmd = { 0x04, 0x00, 0x04, 0x00, 0x0F, 0x00, 0xFF, 0xFF, 0xFF, 0xFF };
        // request set mode: { 04, 00, 04, 00, 0x9, 00, 0xD, mode, 00, 00, 00 }
        private static readonly byte[] ModeCmdBase = { 0x04, 0x00, 0x04, 0x00, 0x09, 0x00, 0x0D, 0xFF, 0x00, 0x00, 0x00 };
        private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

        // === WinAPI ===
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
        private const int WM_DEVICECHANGE = 0x0219;
        private const int DBT_DEVICEARRIVAL = 0x8000;
        private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

        public MainWindow()
        {
            InitializeComponent();
            this.ContentRendered += MainWindow_ContentRendered;
            configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirPodsController", "config.json");
            hDevice = INVALID_HANDLE_VALUE;
            LoadConfig();
            LoadImages();
            InitTray();
            InitAutoConnectTimer();
            InitBatteryTimer();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _windowHandle = new WindowInteropHelper(this).Handle;
            var source = HwndSource.FromHwnd(_windowHandle);
            source?.AddHook(WndProc);
            RegisterAllHotkeys();
        }

        private void MainWindow_ContentRendered(object? sender, EventArgs e)
        {
            UpdateModeIcons();
            TryAutoConnect();
        }

        // ==================== TIMERS ====================

        private void InitAutoConnectTimer()
        {
            _autoConnectTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _autoConnectTimer.Tick += (s, e) => TryAutoConnect();
            _autoConnectTimer.Start();
        }

        private void InitBatteryTimer()
        {
            _batteryTimer = new DispatcherTimer();
            UpdateBatteryTimerInterval();
            _batteryTimer.Tick += async (s, e) =>
            {
                if (hDevice != INVALID_HANDLE_VALUE && !deviceDisconnected)
                {
                    await Task.Run(() => CheckBatteryInternal());
                }
            };
            _batteryTimer.Start();
        }

        private void UpdateBatteryTimerInterval()
        {
            int seconds = config?.CheckInterval ?? 30;
            if (seconds < 10) seconds = 10;
            if (_batteryTimer != null)
                _batteryTimer.Interval = TimeSpan.FromSeconds(seconds);
        }

        // ==================== AUTO CONNECT ====================

        private void TryAutoConnect()
        {
            if (_isConnecting || _isModeBusy) return;

            if (hDevice != INVALID_HANDLE_VALUE && !deviceDisconnected)
            {
                if (!IsDeviceAvailable())
                {
                    lock (usbLock)
                    {
                        if (hDevice != INVALID_HANDLE_VALUE)
                        {
                            CloseHandle(hDevice);
                            hDevice = INVALID_HANDLE_VALUE;
                        }
                    }
                    deviceDisconnected = true;
                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = "Устройство отключено";
                        ResetBatteryDisplay();
                    });
                }
                return;
            }

            if (!IsDeviceAvailable()) return;

            _isConnecting = true;
            try
            {
                lock (usbLock)
                {
                    if (hDevice != INVALID_HANDLE_VALUE) return;

                    uint size = 0;
                    Guid guid = AirPodsGuid;
                    if (CM_Get_Device_Interface_List_Size(ref size, ref guid, IntPtr.Zero, 0) != 0) return;
                    char[] buffer = new char[size];
                    if (CM_Get_Device_Interface_List(ref guid, IntPtr.Zero, buffer, size, 0) != 0) return;
                    string path = new string(buffer).TrimEnd('\0');
                    if (string.IsNullOrEmpty(path)) return;

                    hDevice = CreateFile(path, 0xC0000000, 0x3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (hDevice == INVALID_HANDLE_VALUE) return;

                    deviceDisconnected = false;
                    _sequenceCounter = 0;

                    // Send init command (exact from driver)
                    WriteFile(hDevice, InitCmd, (uint)InitCmd.Length, out uint _, IntPtr.Zero);

                    // Small delay to let device initialize
                    Thread.Sleep(100);

                    string name = GetRealDeviceName();

                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = "Подключено (авто)";
                        DeviceNameText.Text = name;
                    });

                    if (config?.NotifyConnect == true)
                        ShowNotification("AirPods подключены", "Устройство готово к работе");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"TryAutoConnect ERROR: {ex.Message}");
            }
            finally
            {
                _isConnecting = false;
            }
        }

        private bool IsDeviceAvailable()
        {
            try
            {
                uint size = 0;
                Guid guid = AirPodsGuid;
                if (CM_Get_Device_Interface_List_Size(ref size, ref guid, IntPtr.Zero, 0) != 0) return false;
                if (size <= 1) return false;
                return true;
            }
            catch { return false; }
        }

        // ==================== UI & IMAGES ====================

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
                Debug.WriteLine($"LoadImages ERROR: {ex.Message}");
                Dispatcher.Invoke(() => StatusText.Text = "Ошибка загрузки ресурсов");
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
                imageControl.Source = bitmap;
            }
        }

        private void UpdateModeIcons()
        {
            double targetX = 0;
            if (currentNoiseMode == NoiseMode.Off)
                targetX = 0;
            else if (currentNoiseMode == NoiseMode.ANC)
                targetX = OffButton.ActualWidth + 6;
            else if (currentNoiseMode == NoiseMode.Transparency)
                targetX = OffButton.ActualWidth + AncButton.ActualWidth + 12;
            else
                targetX = OffButton.ActualWidth + AncButton.ActualWidth + TransButton.ActualWidth + 18;

            var animation = new DoubleAnimation
            {
                From = HighlightTransform.X,
                To = targetX,
                Duration = TimeSpan.FromMilliseconds(300),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            HighlightTransform.BeginAnimation(TranslateTransform.XProperty, animation);

            OffText.Foreground = currentNoiseMode == NoiseMode.Off ? Brushes.White : new SolidColorBrush(Color.FromRgb(28, 28, 30));
            AncText.Foreground = currentNoiseMode == NoiseMode.ANC ? Brushes.White : new SolidColorBrush(Color.FromRgb(28, 28, 30));
            TransText.Foreground = currentNoiseMode == NoiseMode.Transparency ? Brushes.White : new SolidColorBrush(Color.FromRgb(28, 28, 30));
            AdaptiveText.Foreground = currentNoiseMode == NoiseMode.Adaptive ? Brushes.White : new SolidColorBrush(Color.FromRgb(28, 28, 30));

            lastKnownMode = currentNoiseMode switch
            {
                NoiseMode.Off => 1,
                NoiseMode.ANC => 2,
                NoiseMode.Transparency => 3,
                NoiseMode.Adaptive => 4,
                _ => 1
            };
        }

        // ==================== TRAY ====================

        private void InitTray()
        {
            System.Drawing.Icon? customIcon = null;
            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "logo.ico");
                if (File.Exists(iconPath))
                    customIcon = new System.Drawing.Icon(iconPath);
            }
            catch { }

            trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = customIcon ?? System.Drawing.SystemIcons.Application,
                Text = "AirPods Controller",
                Visible = true
            };

            var menu = new System.Windows.Forms.ContextMenuStrip();
            var openItem = new System.Windows.Forms.ToolStripMenuItem("Открыть");
            openItem.Click += (s, e) => { this.Show(); this.Activate(); };
            menu.Items.Add(openItem);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

            var offItem = new System.Windows.Forms.ToolStripMenuItem("Выкл (Normal)");
            offItem.Click += (s, e) => Dispatcher.Invoke(() => Off_Click(this, new RoutedEventArgs()));
            menu.Items.Add(offItem);

            var ancItem = new System.Windows.Forms.ToolStripMenuItem("ANC");
            ancItem.Click += (s, e) => Dispatcher.Invoke(() => ANC_Click(this, new RoutedEventArgs()));
            menu.Items.Add(ancItem);

            var transItem = new System.Windows.Forms.ToolStripMenuItem("Прозрачность");
            transItem.Click += (s, e) => Dispatcher.Invoke(() => Transparency_Click(this, new RoutedEventArgs()));
            menu.Items.Add(transItem);

            var adaptiveItem = new System.Windows.Forms.ToolStripMenuItem("Адаптивное аудио");
            adaptiveItem.Click += (s, e) => Dispatcher.Invoke(() => Adaptive_Click(this, new RoutedEventArgs()));
            menu.Items.Add(adaptiveItem);

            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

            var batItem = new System.Windows.Forms.ToolStripMenuItem("Проверить заряд");
            batItem.Click += (s, e) => Dispatcher.Invoke(() => CheckBattery_Click(this, new RoutedEventArgs()));
            menu.Items.Add(batItem);

            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

            var settingsItem = new System.Windows.Forms.ToolStripMenuItem("Настройки");
            settingsItem.Click += (s, e) => Settings_Click(this, new RoutedEventArgs());
            menu.Items.Add(settingsItem);

            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            var exitItem = new System.Windows.Forms.ToolStripMenuItem("Выход");
            exitItem.Click += (s, e) => ShutdownApp();
            menu.Items.Add(exitItem);

            trayIcon.ContextMenuStrip = menu;
            trayIcon.DoubleClick += (s, e) => { this.Show(); this.Activate(); };
        }

        private void ShowNotification(string title, string message)
        {
            trayIcon?.ShowBalloonTip(3000, title, message, System.Windows.Forms.ToolTipIcon.Info);
        }

        private void ShutdownApp()
        {
            _autoConnectTimer?.Stop();
            _batteryTimer?.Stop();
            UnregisterAllHotkeys();
            trayIcon?.Dispose();
            lock (usbLock)
            {
                if (hDevice != INVALID_HANDLE_VALUE)
                {
                    CloseHandle(hDevice);
                    hDevice = INVALID_HANDLE_VALUE;
                }
            }
            Application.Current.Shutdown();
        }

        // ==================== USB COMMANDS ====================

        /// <summary>
        /// Reads and discards any pending data in the buffer.
        /// Exact from driver logic.
        /// </summary>
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

        /// <summary>
        /// Sends command and reads response. Exact logic from driver demo.
        /// </summary>
        private byte[]? SendCommandReadResponse(byte[] cmd, int expectedLen, int timeoutMs = 1000)
        {
            if (hDevice == INVALID_HANDLE_VALUE || deviceDisconnected) return null;

            WriteFile(hDevice, cmd, (uint)cmd.Length, out uint _, IntPtr.Zero);

            byte[] buf = new byte[512];
            int elapsed = 0;
            while (elapsed < timeoutMs)
            {
                bool ok = ReadFile(hDevice, buf, 512, out uint bytesRead, IntPtr.Zero);
                if (!ok) return null;
                if (bytesRead == expectedLen)
                {
                    byte[] result = new byte[bytesRead];
                    Array.Copy(buf, result, bytesRead);
                    return result;
                }
                if (bytesRead > 0)
                {
                    // Got some data but wrong length, continue reading
                    Thread.Sleep(20);
                    elapsed += 20;
                    continue;
                }
                Thread.Sleep(20);
                elapsed += 20;
            }
            return null;
        }

        // ==================== BUTTON HANDLERS ====================

        private async void Connect_Click(object sender, RoutedEventArgs e)
        {
            await Task.Run(() =>
            {
                lock (usbLock)
                {
                    if (hDevice != INVALID_HANDLE_VALUE)
                    {
                        // Already connected, just update UI
                        Dispatcher.Invoke(() => StatusText.Text = "Уже подключено");
                        deviceDisconnected = false;
                        return;
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
                    _sequenceCounter = 0;

                    // Send init command
                    WriteFile(hDevice, InitCmd, (uint)InitCmd.Length, out uint _, IntPtr.Zero);
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
            UpdateModeIcons();

            await Task.Run(() =>
            {
                lock (usbLock)
                {
                    if (hDevice == INVALID_HANDLE_VALUE) return;

                    _isModeBusy = true;
                    try
                    {
                        // Send Init first (required before mode change)
                        WriteFile(hDevice, InitCmd, (uint)InitCmd.Length, out uint initWritten, IntPtr.Zero);
                        if (initWritten == 0)
                        {
                            Debug.WriteLine("[ANC] InitCmd failed — device not ready");
                            Dispatcher.Invoke(() => StatusText.Text = "Устройство не готово");
                            return;
                        }
                        Thread.Sleep(80);

                        byte[] cmd = (byte[])ModeCmdBase.Clone();
                        cmd[7] = 2; // ANC ON
                        cmd[8] = _sequenceCounter++;

                        Debug.WriteLine($"[ANC] Sending: {BitConverter.ToString(cmd)}");
                        bool ok = WriteFile(hDevice, cmd, (uint)cmd.Length, out uint written, IntPtr.Zero);
                        Debug.WriteLine($"[ANC] WriteFile result: {ok}, Written: {written} bytes");

                        Thread.Sleep(150);

                        Dispatcher.Invoke(() =>
                        {
                            StatusText.Text = "Шумоподавление";
                            if (config?.NotifyModeChange == true) ShowNotification("Режим", "Шумоподавление");
                        });
                    }
                    finally
                    {
                        _isModeBusy = false;
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
            UpdateModeIcons();

            await Task.Run(() =>
            {
                lock (usbLock)
                {
                    if (hDevice == INVALID_HANDLE_VALUE) return;

                    _isModeBusy = true;
                    try
                    {
                        WriteFile(hDevice, InitCmd, (uint)InitCmd.Length, out uint initWritten, IntPtr.Zero);
                        if (initWritten == 0)
                        {
                            Debug.WriteLine("[TRANS] InitCmd failed — device not ready");
                            Dispatcher.Invoke(() => StatusText.Text = "Устройство не готово");
                            return;
                        }
                        Thread.Sleep(80);

                        byte[] cmd = (byte[])ModeCmdBase.Clone();
                        cmd[7] = 3; // Transparency
                        cmd[8] = _sequenceCounter++;

                        Debug.WriteLine($"[TRANS] Sending: {BitConverter.ToString(cmd)}");
                        bool ok = WriteFile(hDevice, cmd, (uint)cmd.Length, out uint written, IntPtr.Zero);
                        Debug.WriteLine($"[TRANS] WriteFile result: {ok}, Written: {written} bytes");

                        Thread.Sleep(150);

                        Dispatcher.Invoke(() =>
                        {
                            StatusText.Text = "Прозрачность";
                            if (config?.NotifyModeChange == true) ShowNotification("Режим", "Прозрачность");
                        });
                    }
                    finally
                    {
                        _isModeBusy = false;
                    }
                }
            });
        }

        private async void Adaptive_Click(object sender, RoutedEventArgs e)
        {
            if (hDevice == INVALID_HANDLE_VALUE || deviceDisconnected)
            {
                StatusText.Text = "Сначала подключите AirPods";
                return;
            }

            currentNoiseMode = NoiseMode.Adaptive;
            UpdateModeIcons();

            await Task.Run(() =>
            {
                lock (usbLock)
                {
                    if (hDevice == INVALID_HANDLE_VALUE) return;

                    _isModeBusy = true;
                    try
                    {
                        WriteFile(hDevice, InitCmd, (uint)InitCmd.Length, out uint initWritten, IntPtr.Zero);
                        if (initWritten == 0)
                        {
                            Debug.WriteLine("[ADAPTIVE] InitCmd failed — device not ready");
                            Dispatcher.Invoke(() => StatusText.Text = "Устройство не готово");
                            return;
                        }
                        Thread.Sleep(80);

                        byte[] cmd = (byte[])ModeCmdBase.Clone();
                        cmd[7] = 4; // Adaptive Audio
                        cmd[8] = _sequenceCounter++;

                        Debug.WriteLine($"[ADAPTIVE] Sending: {BitConverter.ToString(cmd)}");
                        bool ok = WriteFile(hDevice, cmd, (uint)cmd.Length, out uint written, IntPtr.Zero);
                        Debug.WriteLine($"[ADAPTIVE] WriteFile result: {ok}, Written: {written} bytes");

                        Thread.Sleep(150);

                        Dispatcher.Invoke(() =>
                        {
                            StatusText.Text = "Адаптивное аудио";
                            if (config?.NotifyModeChange == true) ShowNotification("Режим", "Адаптивное аудио");
                        });
                    }
                    finally
                    {
                        _isModeBusy = false;
                    }
                }
            });
        }

        private async void Off_Click(object sender, RoutedEventArgs e)
        {
            if (hDevice == INVALID_HANDLE_VALUE || deviceDisconnected)
            {
                StatusText.Text = "Сначала подключите AirPods";
                return;
            }

            currentNoiseMode = NoiseMode.Off;
            UpdateModeIcons();

            await Task.Run(() =>
            {
                lock (usbLock)
                {
                    if (hDevice == INVALID_HANDLE_VALUE) return;

                    _isModeBusy = true;
                    try
                    {
                        WriteFile(hDevice, InitCmd, (uint)InitCmd.Length, out uint initWritten, IntPtr.Zero);
                        if (initWritten == 0)
                        {
                            Debug.WriteLine("[OFF] InitCmd failed — device not ready");
                            Dispatcher.Invoke(() => StatusText.Text = "Устройство не готово");
                            return;
                        }
                        Thread.Sleep(80);

                        byte[] cmd = (byte[])ModeCmdBase.Clone();
                        cmd[7] = 1; // OFF / Normal mode (ANC OFF)
                        cmd[8] = _sequenceCounter++;

                        Debug.WriteLine($"[OFF] Sending: {BitConverter.ToString(cmd)}");
                        bool ok = WriteFile(hDevice, cmd, (uint)cmd.Length, out uint written, IntPtr.Zero);
                        Debug.WriteLine($"[OFF] WriteFile result: {ok}, Written: {written} bytes");

                        Thread.Sleep(150);

                        Dispatcher.Invoke(() =>
                        {
                            StatusText.Text = "Режим выкл (Normal)";
                            if (config?.NotifyModeChange == true) ShowNotification("Режим", "Выключено");
                        });
                    }
                    finally
                    {
                        _isModeBusy = false;
                    }
                }
            });
        }

        // ==================== BATTERY (EXACT FROM DRIVER) ====================

        /// <summary>
        /// UI entry point for battery check button.
        /// </summary>
        private async void CheckBattery_Click(object sender, RoutedEventArgs e)
        {
            await Task.Run(() => CheckBatteryInternal());
        }

        /// <summary>
        /// Exact battery parsing from driver demo (main.cpp):
        /// if (cbRead == 22) {
        ///     printf("L : %d , R : %d, Case : %d\n", recvBuffer[9], recvBuffer[14], recvBuffer[19]);
        ///     printf("Left unit in case : %s\n", recvBuffer[10] == 0x1 ? "true" : "false");
        ///     printf("Right unit in case : %s\n", recvBuffer[15] == 0x1 ? "true" : "false");
        /// }
        /// </summary>
        private void CheckBatteryInternal()
        {
            if (hDevice == INVALID_HANDLE_VALUE) return;

            try
            {
                lock (usbLock)
                {
                    if (hDevice == INVALID_HANDLE_VALUE) return;

                    // Send battery request (exact from driver)
                    byte[]? response = SendCommandReadResponse(BatteryCmd, expectedLen: 22, timeoutMs: 1500);

                    if (response == null)
                    {
                        Dispatcher.Invoke(() => StatusText.Text = "Нет ответа от наушников");
                        return;
                    }

                    // Log for debugging
                    Debug.WriteLine($"[BATTERY] Response length: {response.Length}");
                    Debug.WriteLine($"[BATTERY] Bytes: {BitConverter.ToString(response)}");

                    // Exact parsing from driver
                    int leftBattery = response[9];
                    int leftInCase = response[10];
                    int rightBattery = response[14];
                    int rightInCase = response[15];
                    int caseBattery = response[19];

                    Debug.WriteLine($"[BATTERY] L={leftBattery} (inCase={leftInCase}), R={rightBattery} (inCase={rightInCase}), Case={caseBattery}");

                    // Validate values
                    if (leftBattery > 100 || rightBattery > 100 || caseBattery > 100)
                    {
                        Dispatcher.Invoke(() => StatusText.Text = "Некорректные данные заряда");
                        return;
                    }

                    // Build display strings
                    string leftStr = leftBattery + "%";
                    string rightStr = rightBattery + "%";
                    string caseStr = caseBattery + "%";

                    // Determine colors: gray if in case, otherwise by level
                    Brush leftBrush = leftInCase == 0x1 ? Brushes.Gray : GetBatteryBrush(leftBattery);
                    Brush rightBrush = rightInCase == 0x1 ? Brushes.Gray : GetBatteryBrush(rightBattery);
                    Brush caseBrush = GetBatteryBrush(caseBattery);

                    Dispatcher.Invoke(() =>
                    {
                        LeftBatteryText.Text = leftStr;
                        RightBatteryText.Text = rightStr;
                        CaseBatteryText.Text = caseStr;
                        LeftBatteryText.Foreground = leftBrush;
                        RightBatteryText.Foreground = rightBrush;
                        CaseBatteryText.Foreground = caseBrush;
                        StatusText.Text = "Заряд обновлён";
                    });

                    // Low battery notification
                    int minBat = Math.Min(leftBattery, Math.Min(rightBattery, caseBattery));
                    if (config != null && config.NotifyLowBattery && !lowBatteryWarningShown)
                    {
                        if (minBat <= config.LowBatteryThreshold)
                        {
                            ShowNotification("Низкий заряд", $"Л:{leftStr} П:{rightStr} Кейс:{caseStr}");
                            lowBatteryWarningShown = true;
                        }
                    }
                    if (caseBattery > lastKnownCaseBattery && lastKnownCaseBattery != -1)
                        lowBatteryWarningShown = false;
                    lastKnownCaseBattery = caseBattery;

                    // Save to history
                    if (config != null)
                    {
                        config.BatteryHistory ??= new List<BatteryHistory>();
                        config.BatteryHistory.Add(new BatteryHistory
                        {
                            Timestamp = DateTime.Now,
                            Left = leftBattery,
                            Right = rightBattery,
                            Case = caseBattery
                        });
                        if (config.BatteryHistory.Count > 100)
                            config.BatteryHistory.RemoveRange(0, config.BatteryHistory.Count - 100);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BATTERY] ERROR: {ex.Message}");
                Dispatcher.Invoke(() => StatusText.Text = "Ошибка получения заряда");
            }
        }

        private Brush GetBatteryBrush(int level)
        {
            if (level <= 20) return Brushes.Crimson;
            if (level <= 40) return Brushes.Orange;
            return Brushes.ForestGreen;
        }

        private void ResetBatteryDisplay()
        {
            LeftBatteryText.Text = "--%";
            RightBatteryText.Text = "--%";
            CaseBatteryText.Text = "--%";
            LeftBatteryText.Foreground = Brushes.Gray;
            RightBatteryText.Foreground = Brushes.Gray;
            CaseBatteryText.Foreground = Brushes.Gray;
        }

        // ==================== CONFIG ====================

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
                    if (!string.IsNullOrEmpty(name)) return name;
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
                UpdateBatteryTimerInterval();
            }
        }

        private void Notifications_Click(object sender, RoutedEventArgs e) => Settings_Click(sender, e);

        private void About_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("AirPods Controller\n\nВерсия: 2.2 (WPF)\nУправление AirPods Pro на Windows\n\nFStudio ©2026", "О программе", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ==================== HOTKEYS ====================

        private void RegisterAllHotkeys()
        {
            if (_windowHandle == IntPtr.Zero)
            {
                Debug.WriteLine("RegisterAllHotkeys: window handle is zero, skipping");
                return;
            }
            if (config == null) return;

            IntPtr hWnd = _windowHandle;

            foreach (var id in registeredHotkeyIds)
                UnregisterHotKey(hWnd, id);

            registeredHotkeyIds.Clear();
            hotkeyActions.Clear();
            nextHotkeyId = 100;

            if (!string.IsNullOrWhiteSpace(config.OffHotkey))
                RegHotkey(hWnd, config.OffHotkey, config.OffCtrl, config.OffAlt, config.OffShift, "Off");

            if (!string.IsNullOrWhiteSpace(config.ANCHotkey))
                RegHotkey(hWnd, config.ANCHotkey, config.ANCCtrl, config.ANCAlt, config.ANCShift, "ANC");

            if (!string.IsNullOrWhiteSpace(config.TransparencyHotkey))
                RegHotkey(hWnd, config.TransparencyHotkey, config.TransCtrl, config.TransAlt, config.TransShift, "Trans");

            if (!string.IsNullOrWhiteSpace(config.AdaptiveHotkey))
                RegHotkey(hWnd, config.AdaptiveHotkey, config.AdaptiveCtrl, config.AdaptiveAlt, config.AdaptiveShift, "Adaptive");

            if (!string.IsNullOrWhiteSpace(config.BatteryHotkey))
                RegHotkey(hWnd, config.BatteryHotkey, config.BatCtrl, config.BatAlt, config.BatShift, "Bat");
        }

        private void UnregisterAllHotkeys()
        {
            if (_windowHandle == IntPtr.Zero) return;

            IntPtr hWnd = _windowHandle;
            foreach (var id in registeredHotkeyIds)
                UnregisterHotKey(hWnd, id);

            registeredHotkeyIds.Clear();
            hotkeyActions.Clear();
            nextHotkeyId = 100;
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
                    Debug.WriteLine($"Hotkey registered: {action} -> {keyName} (ID={id})");
                }
                else
                {
                    Debug.WriteLine($"RegisterHotKey FAILED for {action}: Error={Marshal.GetLastWin32Error()}");
                }
                return result;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RegHotkey EXCEPTION: {ex.Message}");
                return false;
            }
        }

        // ==================== WNDPROC ====================

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (hotkeyActions.TryGetValue(id, out string? action))
                {
                    if (action == "Off") Dispatcher.Invoke(() => Off_Click(this, new RoutedEventArgs()));
                    else if (action == "ANC") Dispatcher.Invoke(() => ANC_Click(this, new RoutedEventArgs()));
                    else if (action == "Trans") Dispatcher.Invoke(() => Transparency_Click(this, new RoutedEventArgs()));
                    else if (action == "Adaptive") Dispatcher.Invoke(() => Adaptive_Click(this, new RoutedEventArgs()));
                    else if (action == "Bat") Dispatcher.Invoke(() => CheckBattery_Click(this, new RoutedEventArgs()));
                }
                handled = true;
            }
            else if (msg == WM_DEVICECHANGE)
            {
                int wp = wParam.ToInt32();
                if (wp == DBT_DEVICEARRIVAL || wp == DBT_DEVICEREMOVECOMPLETE)
                {
                    Task.Delay(1000).ContinueWith(_ => TryAutoConnect());
                }
            }
            return IntPtr.Zero;
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            this.Hide();
        }
    }

    // ==================== MODELS ====================

    public class AppSettings
    {
        public bool NotifyLowBattery { get; set; } = true;
        public bool NotifyConnect { get; set; } = true;
        public bool NotifyModeChange { get; set; } = true;
        public int LowBatteryThreshold { get; set; } = 20;
        public int CheckInterval { get; set; } = 30;
        public bool SoundNotification { get; set; } = false;
        public string OffHotkey { get; set; } = "";
        public string ANCHotkey { get; set; } = "";
        public string TransparencyHotkey { get; set; } = "";
        public string AdaptiveHotkey { get; set; } = "";
        public string BatteryHotkey { get; set; } = "";
        public bool OffCtrl { get; set; } = false;
        public bool OffAlt { get; set; } = false;
        public bool OffShift { get; set; } = false;
        public bool ANCCtrl { get; set; } = false;
        public bool ANCAlt { get; set; } = false;
        public bool ANCShift { get; set; } = false;
        public bool TransCtrl { get; set; } = false;
        public bool TransAlt { get; set; } = false;
        public bool TransShift { get; set; } = false;
        public bool AdaptiveCtrl { get; set; } = false;
        public bool AdaptiveAlt { get; set; } = false;
        public bool AdaptiveShift { get; set; } = false;
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