using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace AirPodsController
{
    public partial class SettingsWindow : Window
    {
        private AppSettings config;
        private string configPath;
        private bool isCapturing = false;
        private string captureTarget = "";

        public SettingsWindow(AppSettings settings, string path)
        {
            InitializeComponent();
            config = settings;
            configPath = path;
            LoadSettings();
        }

        private void LoadSettings()
        {
            if (config == null) return;
            ChkOffCtrl.IsChecked = config.OffCtrl;
            ChkOffAlt.IsChecked = config.OffAlt;
            ChkOffShift.IsChecked = config.OffShift;
            TxtOff.Text = string.IsNullOrEmpty(config.OffHotkey) ? "Не назначено" : config.OffHotkey;
            ChkANCCtrl.IsChecked = config.ANCCtrl;
            ChkANCAlt.IsChecked = config.ANCAlt;
            ChkANCShift.IsChecked = config.ANCShift;
            TxtANC.Text = string.IsNullOrEmpty(config.ANCHotkey) ? "Не назначено" : config.ANCHotkey;
            ChkTransCtrl.IsChecked = config.TransCtrl;
            ChkTransAlt.IsChecked = config.TransAlt;
            ChkTransShift.IsChecked = config.TransShift;
            TxtTrans.Text = string.IsNullOrEmpty(config.TransparencyHotkey) ? "Не назначено" : config.TransparencyHotkey;
            ChkAdaptiveCtrl.IsChecked = config.AdaptiveCtrl;
            ChkAdaptiveAlt.IsChecked = config.AdaptiveAlt;
            ChkAdaptiveShift.IsChecked = config.AdaptiveShift;
            TxtAdaptive.Text = string.IsNullOrEmpty(config.AdaptiveHotkey) ? "Не назначено" : config.AdaptiveHotkey;
            ChkBatCtrl.IsChecked = config.BatCtrl;
            ChkBatAlt.IsChecked = config.BatAlt;
            ChkBatShift.IsChecked = config.BatShift;
            TxtBat.Text = string.IsNullOrEmpty(config.BatteryHotkey) ? "Не назначено" : config.BatteryHotkey;
            ChkNotifyLow.IsChecked = config.NotifyLowBattery;
            ChkNotifyConnect.IsChecked = config.NotifyConnect;
            ChkNotifyMode.IsChecked = config.NotifyModeChange;
            ChkSound.IsChecked = config.SoundNotification;
            TxtThreshold.Text = config.LowBatteryThreshold.ToString();
            TxtCheckInterval.Text = config.CheckInterval.ToString();
            ChkAutoStart.IsChecked = config.AutoStart;
            ChkDarkTheme.IsChecked = config.DarkTheme;
        }

        private void SetOff_Click(object sender, RoutedEventArgs e) => StartCapture("Off", TxtOff);
        private void SetANC_Click(object sender, RoutedEventArgs e) => StartCapture("ANC", TxtANC);
        private void SetTrans_Click(object sender, RoutedEventArgs e) => StartCapture("Trans", TxtTrans);
        private void SetAdaptive_Click(object sender, RoutedEventArgs e) => StartCapture("Adaptive", TxtAdaptive);
        private void SetBat_Click(object sender, RoutedEventArgs e) => StartCapture("Bat", TxtBat);

        private void StartCapture(string target, System.Windows.Controls.TextBlock tb)
        {
            isCapturing = true;
            captureTarget = target;
            tb.Text = "Нажмите клавишу...";
            this.PreviewKeyDown += OnKeyDown;
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (!isCapturing) return;
            e.Handled = true;
            if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl || e.Key == Key.LeftAlt || e.Key == Key.RightAlt || e.Key == Key.LeftShift || e.Key == Key.RightShift || e.Key == Key.LWin || e.Key == Key.RWin) return;
            if (e.Key == Key.Escape) { StopCapture(); return; }
            string k = e.Key.ToString();
            if (captureTarget == "Off") { config.OffHotkey = k; TxtOff.Text = k; }
            else if (captureTarget == "ANC") { config.ANCHotkey = k; TxtANC.Text = k; }
            else if (captureTarget == "Trans") { config.TransparencyHotkey = k; TxtTrans.Text = k; }
            else if (captureTarget == "Adaptive") { config.AdaptiveHotkey = k; TxtAdaptive.Text = k; }
            else if (captureTarget == "Bat") { config.BatteryHotkey = k; TxtBat.Text = k; }
            StopCapture();
        }

        private void StopCapture()
        {
            isCapturing = false;
            this.PreviewKeyDown -= OnKeyDown;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            config.OffCtrl = ChkOffCtrl.IsChecked == true;
            config.OffAlt = ChkOffAlt.IsChecked == true;
            config.OffShift = ChkOffShift.IsChecked == true;
            config.ANCCtrl = ChkANCCtrl.IsChecked == true;
            config.ANCAlt = ChkANCAlt.IsChecked == true;
            config.ANCShift = ChkANCShift.IsChecked == true;
            config.TransCtrl = ChkTransCtrl.IsChecked == true;
            config.TransAlt = ChkTransAlt.IsChecked == true;
            config.TransShift = ChkTransShift.IsChecked == true;
            config.AdaptiveCtrl = ChkAdaptiveCtrl.IsChecked == true;
            config.AdaptiveAlt = ChkAdaptiveAlt.IsChecked == true;
            config.AdaptiveShift = ChkAdaptiveShift.IsChecked == true;
            config.BatCtrl = ChkBatCtrl.IsChecked == true;
            config.BatAlt = ChkBatAlt.IsChecked == true;
            config.BatShift = ChkBatShift.IsChecked == true;
            config.NotifyLowBattery = ChkNotifyLow.IsChecked == true;
            config.NotifyConnect = ChkNotifyConnect.IsChecked == true;
            config.NotifyModeChange = ChkNotifyMode.IsChecked == true;
            config.SoundNotification = ChkSound.IsChecked == true;
            if (int.TryParse(TxtThreshold.Text, out int t)) config.LowBatteryThreshold = t;
            if (int.TryParse(TxtCheckInterval.Text, out int ci)) config.CheckInterval = ci;
            config.AutoStart = ChkAutoStart.IsChecked == true;
            config.DarkTheme = ChkDarkTheme.IsChecked == true;
            try
            {
                string? dir = Path.GetDirectoryName(configPath);
                if (dir != null) Directory.CreateDirectory(dir);
                File.WriteAllText(configPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
                var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                if (config.AutoStart) key?.SetValue("AirPodsController", System.Reflection.Assembly.GetExecutingAssembly().Location);
                else key?.DeleteValue("AirPodsController", false);
                System.Windows.MessageBox.Show("Сохранено!", "Успех");
                DialogResult = true;
                Close();
            }
            catch (Exception ex) { System.Windows.MessageBox.Show("Ошибка: " + ex.Message); }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
    }
}