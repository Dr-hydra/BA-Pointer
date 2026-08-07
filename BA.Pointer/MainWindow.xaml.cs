using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BA.Pointer.Interop;
using BA.Pointer.Models;
using BA.Pointer.Services;
using Forms = System.Windows.Forms;
using Color = System.Windows.Media.Color;

namespace BA.Pointer;

public partial class MainWindow : Window
{
    private const int HotKeyId = 0xBA01;
    private readonly SettingsStore _store = new();
    private readonly CursorInstaller _cursorInstaller;
    private readonly PointerEffectController _controller;
    private PointerSettings _settings;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ToolStripMenuItem? _trayToggleItem;
    private IntPtr _hwnd;
    private bool _initializing = true;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        _cursorInstaller = new CursorInstaller(_store);
        _controller = new PointerEffectController(_cursorInstaller);
        _controller.StateChanged += OnControllerStateChanged;
        _settings = _store.Load();
        _settings.CursorImagePath = AssetLocator.GetBundledCursorPath();
        PopulateControls();
        InitializeTray();
        SourceInitialized += OnSourceInitialized;
        _initializing = false;
        UpdateValueLabels();
        SetCursorPreview(_settings.CursorImagePath);
        SetRunningUi(false);
        if (_settings.Enabled)
        {
            Dispatcher.BeginInvoke(StartEffects);
        }
    }

    private void PopulateControls()
    {
        EffectScaleSlider.Value = _settings.EffectScale;
        EffectOpacitySlider.Value = _settings.EffectOpacity;
        EffectDurationSlider.Value = _settings.EffectDurationScale;
        TrailWidthSlider.Value = _settings.TrailWidthScale;
        TrailDurationSlider.Value = _settings.TrailDurationMs;
        DistanceEmissionSlider.Value = _settings.DistanceEmissionScale;
        TargetCombo.SelectedIndex = _settings.Target == TargetScope.AllDesktop ? 0 : 1;
        FpsCombo.SelectedIndex = _settings.FrameRate switch { 60 => 0, 144 => 2, _ => 1 };
        SystemCursorCheck.IsChecked = _settings.UseSystemCursor;
        StartupCheck.IsChecked = _settings.StartWithWindows;
    }

    private void ReadControls()
    {
        _settings.EffectScale = EffectScaleSlider.Value;
        _settings.EffectOpacity = EffectOpacitySlider.Value;
        _settings.EffectDurationScale = EffectDurationSlider.Value;
        _settings.TrailWidthScale = TrailWidthSlider.Value;
        _settings.TrailDurationMs = TrailDurationSlider.Value;
        _settings.DistanceEmissionScale = DistanceEmissionSlider.Value;
        _settings.Target = TargetCombo.SelectedIndex == 1 ? TargetScope.BlueArchiveOnly : TargetScope.AllDesktop;
        _settings.FrameRate = FpsCombo.SelectedIndex switch { 0 => 60, 2 => 144, _ => 120 };
        _settings.UseSystemCursor = SystemCursorCheck.IsChecked == true;
        _settings.StartWithWindows = StartupCheck.IsChecked == true;
    }

    private void SaveAndApply()
    {
        ReadControls();
        _store.Save(_settings);
        StartupManager.SetEnabled(_settings.StartWithWindows);
        if (_controller.IsRunning) _controller.ApplySettings(_settings, _settings.CursorImagePath);
        SetStatus("参数已保存", false);
    }

    private void StartEffects()
    {
        try
        {
            ReadControls();
            StartupManager.SetEnabled(_settings.StartWithWindows);
            _controller.Start(_settings, _settings.CursorImagePath);
            _settings.Enabled = true;
            _store.Save(_settings);
            SetStatus("效果运行中", true);
        }
        catch (Exception ex)
        {
            _controller.Stop();
            _settings.Enabled = false;
            _store.Save(_settings);
            SetStatus("启动失败", false);
            System.Windows.MessageBox.Show(ex.Message, "BA Pointer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StopEffects()
    {
        _controller.Stop();
        _settings.Enabled = false;
        _store.Save(_settings);
        SetStatus("效果已停止", false);
    }

    private void OnStartClick(object sender, RoutedEventArgs e) => StartEffects();
    private void OnStopClick(object sender, RoutedEventArgs e) => StopEffects();

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        try { SaveAndApply(); }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "BA Pointer", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void OnRestoreCursorClick(object sender, RoutedEventArgs e)
    {
        _controller.Stop();
        _cursorInstaller.Restore();
        _settings.Enabled = false;
        _store.Save(_settings);
        SetStatus("已恢复原系统光标", false);
    }

    private void OnSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        UpdateValueLabels();
    }

    private void OnComboSettingChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) { }
    private void OnCheckSettingChanged(object sender, RoutedEventArgs e) { }

    private void UpdateValueLabels()
    {
        EffectScaleValue.Text = $"{EffectScaleSlider.Value:0.00}x";
        EffectOpacityValue.Text = $"{EffectOpacitySlider.Value:P0}";
        EffectDurationValue.Text = $"{EffectDurationSlider.Value:0.00}x";
        TrailWidthValue.Text = $"{TrailWidthSlider.Value:0.00}x";
        TrailDurationValue.Text = $"{TrailDurationSlider.Value:0} ms";
        DistanceEmissionValue.Text = $"{DistanceEmissionSlider.Value:0.00}x";
    }

    private void SetCursorPreview(string path)
    {
        if (!File.Exists(path)) return;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        CursorPreview.Source = image;
        HeaderCursorPreview.Source = image;
    }

    private void SetStatus(string message, bool running)
    {
        StatusText.Text = message;
        StatusIndicator.Fill = new SolidColorBrush(running ? Color.FromRgb(83, 225, 174) : Color.FromRgb(104, 119, 139));
    }

    private void SetRunningUi(bool running)
    {
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        if (_trayToggleItem is not null) _trayToggleItem.Text = running ? "停止效果" : "启动效果";
    }

    private void OnControllerStateChanged(bool running) => Dispatcher.Invoke(() => SetRunningUi(running));

    private void InitializeTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开设置", null, (_, _) => ShowSettings());
        _trayToggleItem = new Forms.ToolStripMenuItem("启动效果", null, (_, _) => ToggleEffects());
        menu.Items.Add(_trayToggleItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "BA Pointer",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowSettings);
    }

    private void ShowSettings()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ToggleEffects()
    {
        if (_controller.IsRunning) StopEffects(); else StartEffects();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd)?.AddHook(WindowHook);
        NativeMethods.RegisterHotKey(_hwnd, HotKeyId, NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, 0x50);
    }

    private IntPtr WindowHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotKeyId)
        {
            ToggleEffects();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        Hide();
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => ExitApplication();

    private void ExitApplication()
    {
        _allowClose = true;
        var resumeOnNextLaunch = _controller.IsRunning;
        _controller.Dispose();
        _settings.Enabled = resumeOnNextLaunch;
        _store.Save(_settings);
        _trayIcon?.Dispose();
        Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_hwnd != IntPtr.Zero) NativeMethods.UnregisterHotKey(_hwnd, HotKeyId);
        _controller.Dispose();
        _trayIcon?.Dispose();
    }
}
