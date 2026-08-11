using System.Runtime.InteropServices;
using System.Diagnostics;
using BA.Pointer.Interop;
using BA.Pointer.Models;
using BA.Pointer.Services;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace BA.Pointer;

public sealed partial class MainWindow : Window
{
    private const string AppVersion = "1.1.1";
    private const string ProjectUrl = "https://github.com/Dr-hydra/BA-Pointer";
    private const string BilibiliUrl = "https://space.bilibili.com/441133155";
    private const int HotKeyId = 0xBA01;
    private const uint TrayCallbackMessage = NativeMethods.WM_APP + 43;
    private const uint TrayIconId = 1;
    private const uint MenuOpen = 1001;
    private const uint MenuToggle = 1002;
    private const uint MenuExit = 1003;
    private const nuint SubclassId = 0xBA01;

    private readonly SettingsStore _store = new();
    private readonly CursorInstaller _cursorInstaller;
    private readonly PointerEffectController _controller;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hwnd;
    private readonly NativeMethods.SubclassProc _subclassProc;
    private PointerSettings _settings;
    private NativeMethods.NOTIFYICONDATA _trayData;
    private string? _updateUrl;
    private bool _initializing = true;
    private bool _allowClose;
    private bool _updateCheckStarted;

    public MainWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        ConfigureWindow(windowId);

        _cursorInstaller = new CursorInstaller(_store);
        _controller = new PointerEffectController(_cursorInstaller, DispatcherQueue);
        _controller.StateChanged += OnControllerStateChanged;
        _settings = _store.Load();
        _settings.CursorImagePath = AssetLocator.GetBundledCursorPath();

        _subclassProc = WindowSubclass;
        NativeMethods.SetWindowSubclass(_hwnd, _subclassProc, SubclassId, 0);
        NativeMethods.RegisterHotKey(_hwnd, HotKeyId, NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, 0x50);
        InitializeTray();
        PopulateControls();
        UpdateValueLabels();
        SetRunningUi(false);
        _initializing = false;

        _appWindow.Closing += OnAppWindowClosing;
        Activated += OnWindowActivated;
        Closed += OnClosed;
        if (!_settings.SilentStart) DispatcherQueue.TryEnqueue(StartUpdateCheck);
        if (_settings.Enabled) DispatcherQueue.TryEnqueue(StartEffects);
    }

    private void ConfigureWindow(WindowId windowId)
    {
        _appWindow.Resize(new SizeInt32(920, 720));
        _appWindow.Title = "BA Pointer";
        var display = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        _appWindow.Move(new PointInt32(work.X + Math.Max(0, (work.Width - 920) / 2), work.Y + Math.Max(0, (work.Height - 720) / 2)));
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        _appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        _appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        var icon = LoadApplicationIcon();
        if (icon != IntPtr.Zero)
        {
            NativeMethods.SendMessage(_hwnd, NativeMethods.WM_SETICON, new IntPtr(NativeMethods.ICON_BIG), icon);
            NativeMethods.SendMessage(_hwnd, NativeMethods.WM_SETICON, new IntPtr(NativeMethods.ICON_SMALL), icon);
        }
    }

    private void PopulateControls()
    {
        AboutVersionText.Text = $"版本 {AppVersion}";
        EffectScaleSlider.Value = _settings.EffectScale;
        EffectOpacitySlider.Value = _settings.EffectOpacity;
        EffectDurationSlider.Value = _settings.EffectDurationScale;
        FragmentScaleSlider.Value = _settings.FragmentScale;
        FragmentTransitionSlider.Value = _settings.FragmentTransitionScale;
        TrailWidthSlider.Value = _settings.TrailWidthScale;
        TrailDurationSlider.Value = _settings.TrailDurationMs;
        PersistentTrailToggle.IsOn = _settings.PersistentTrail;
        DistanceEmissionSlider.Value = _settings.DistanceEmissionScale;
        BloomRadiusSlider.Value = _settings.BloomRadius;
        BloomStrengthSlider.Value = _settings.BloomStrength;
        TargetCombo.SelectedIndex = _settings.Target == TargetScope.AllDesktop ? 0 : 1;
        FpsCombo.SelectedIndex = _settings.FrameRate switch { 60 => 0, 144 => 2, _ => 1 };
        SystemCursorToggle.IsOn = _settings.UseSystemCursor;
        StartupToggle.IsOn = _settings.StartWithWindows;
        SilentStartToggle.IsOn = _settings.SilentStart;
        RunAsAdministratorToggle.IsOn = _settings.RunAsAdministrator;
        EnabledToggle.IsOn = _settings.Enabled;
    }

    private void ReadControls()
    {
        _settings.EffectScale = EffectScaleSlider.Value;
        _settings.EffectOpacity = EffectOpacitySlider.Value;
        _settings.EffectDurationScale = EffectDurationSlider.Value;
        _settings.FragmentScale = FragmentScaleSlider.Value;
        _settings.FragmentTransitionScale = FragmentTransitionSlider.Value;
        _settings.TrailWidthScale = TrailWidthSlider.Value;
        _settings.TrailDurationMs = TrailDurationSlider.Value;
        _settings.PersistentTrail = PersistentTrailToggle.IsOn;
        _settings.DistanceEmissionScale = DistanceEmissionSlider.Value;
        _settings.BloomRadius = BloomRadiusSlider.Value;
        _settings.BloomStrength = BloomStrengthSlider.Value;
        _settings.Target = TargetCombo.SelectedIndex == 1 ? TargetScope.PauseWhenFullscreen : TargetScope.AllDesktop;
        _settings.FrameRate = FpsCombo.SelectedIndex switch { 0 => 60, 2 => 144, _ => 120 };
        _settings.UseSystemCursor = SystemCursorToggle.IsOn;
        _settings.StartWithWindows = StartupToggle.IsOn;
        _settings.SilentStart = SilentStartToggle.IsOn;
        _settings.RunAsAdministrator = RunAsAdministratorToggle.IsOn;
        _settings.Enabled = EnabledToggle.IsOn;
        _settings.CursorImagePath = AssetLocator.GetBundledCursorPath();
    }

    private void SaveAndApply()
    {
        var administratorSettingChanged = _settings.RunAsAdministrator != RunAsAdministratorToggle.IsOn;
        ReadControls();
        StartupManager.SetEnabled(_settings.StartWithWindows);
        if (_settings.Enabled) _controller.Start(_settings, _settings.CursorImagePath);
        else _controller.Stop();
        _store.Save(_settings);
        if (administratorSettingChanged)
            SetStatus("设置已保存，管理员身份将在下次启动时生效", InfoBarSeverity.Informational);
        else
            SetStatus(_controller.IsRunning ? "效果正在运行" : "设置已保存", _controller.IsRunning ? InfoBarSeverity.Success : InfoBarSeverity.Informational);
    }

    private void StartEffects()
    {
        try
        {
            ReadControls();
            _settings.Enabled = true;
            _controller.Start(_settings, _settings.CursorImagePath);
            _store.Save(_settings);
            SetRunningUi(true);
        }
        catch (Exception exception)
        {
            ErrorLog.Write(exception);
            EnabledToggle.IsOn = false;
            _settings.Enabled = false;
            SetStatus(exception.Message, InfoBarSeverity.Error);
        }
    }

    private void StopEffects()
    {
        _controller.Stop();
        _settings.Enabled = false;
        _store.Save(_settings);
        SetRunningUi(false);
    }

    private void OnEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        if (EnabledToggle.IsOn) StartEffects(); else StopEffects();
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        try { SaveAndApply(); }
        catch (Exception exception) { SetStatus(exception.Message, InfoBarSeverity.Error); }
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        var enabled = EnabledToggle.IsOn;
        _settings = new PointerSettings { Enabled = enabled, CursorImagePath = AssetLocator.GetBundledCursorPath() };
        _initializing = true;
        PopulateControls();
        _initializing = false;
        UpdateValueLabels();
        SetStatus("参数已恢复默认值，点击应用并保存", InfoBarSeverity.Informational);
    }

    private void OnRestoreCursorClick(object sender, RoutedEventArgs e)
    {
        _controller.Stop();
        _cursorInstaller.Restore();
        EnabledToggle.IsOn = false;
        _settings.Enabled = false;
        _store.Save(_settings);
        SetRunningUi(false);
        SetStatus("已恢复原系统光标", InfoBarSeverity.Success);
    }

    private void OnSliderValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_initializing) UpdateValueLabels();
    }

    private void UpdateValueLabels()
    {
        EffectScaleValue.Text = $"{EffectScaleSlider.Value:0.00}x";
        EffectOpacityValue.Text = $"{EffectOpacitySlider.Value:P0}";
        EffectDurationValue.Text = $"{EffectDurationSlider.Value:0.00}x";
        FragmentScaleValue.Text = $"{FragmentScaleSlider.Value:0.00}x";
        FragmentTransitionValue.Text = $"{FragmentTransitionSlider.Value:0.00}x";
        TrailWidthValue.Text = $"{TrailWidthSlider.Value:0.00}x";
        TrailDurationValue.Text = $"{TrailDurationSlider.Value:0} ms";
        DistanceEmissionValue.Text = $"{DistanceEmissionSlider.Value:0.00}x";
        BloomRadiusValue.Text = $"{BloomRadiusSlider.Value:0.0} px";
        BloomStrengthValue.Text = $"{BloomStrengthSlider.Value:0.00}x";
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = args.SelectedItemContainer?.Tag as string;
        var effects = tag == "effects";
        var system = tag == "system";
        EffectsPage.Visibility = effects ? Visibility.Visible : Visibility.Collapsed;
        SystemPage.Visibility = system ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = !effects && !system ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = effects ? "效果参数" : system ? "资源与系统" : "关于";
        PageSubtitle.Text = effects ? "点击、圆弧 Bloom、碎片和拖尾" : system ? "生效范围、刷新率、光标与自动启动" : "版本、声明与项目主页";
    }

    private void OnOpenProjectClick(object sender, RoutedEventArgs e)
    {
        OpenExternalUrl(ProjectUrl);
    }

    private void OnOpenBilibiliClick(object sender, RoutedEventArgs e)
    {
        OpenExternalUrl(BilibiliUrl);
    }

    private void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_updateUrl)) OpenExternalUrl(_updateUrl);
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated) return;
        StartUpdateCheck();
    }

    private void StartUpdateCheck()
    {
        if (_updateCheckStarted) return;
        _updateCheckStarted = true;
        ErrorLog.WriteInfo("Update", "Starting GitHub release check.");
        _ = CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        var update = await new UpdateService().CheckAsync(Version.Parse(AppVersion));
        if (update is null)
        {
            ErrorLog.WriteInfo("Update", "No newer stable release found.");
            return;
        }

        _updateUrl = update.ReleasePageUrl;
        UpdateButton.Content = $"更新 {update.TagName}";
        UpdateButton.Visibility = Visibility.Visible;
        SetStatus($"发现新版本 {update.TagName}", InfoBarSeverity.Informational);
    }

    private void OpenExternalUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, InfoBarSeverity.Error);
        }
    }

    private void SetRunningUi(bool running)
    {
        if (EnabledToggle.IsOn != running)
        {
            _initializing = true;
            EnabledToggle.IsOn = running;
            _initializing = false;
        }
        SetStatus(running ? "效果正在运行" : "效果已停止", running ? InfoBarSeverity.Success : InfoBarSeverity.Informational);
    }

    private void SetStatus(string message, InfoBarSeverity severity)
    {
        StatusBar.Title = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }

    private void OnControllerStateChanged(bool running) => DispatcherQueue.TryEnqueue(() => SetRunningUi(running));

    private void InitializeTray()
    {
        _trayData = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = TrayIconId,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            uCallbackMessage = TrayCallbackMessage,
            hIcon = LoadApplicationIcon(),
            szTip = "BA Pointer",
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref _trayData);
    }

    private static IntPtr LoadApplicationIcon()
    {
        var icon = NativeMethods.LoadIcon(NativeMethods.GetModuleHandle(null), NativeMethods.IDI_APPLICATION);
        return icon != IntPtr.Zero ? icon : NativeMethods.LoadIcon(IntPtr.Zero, NativeMethods.IDI_APPLICATION);
    }

    private IntPtr WindowSubclass(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint subclassId, nuint referenceData)
    {
        if (message == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotKeyId) { ToggleEffects(); return IntPtr.Zero; }
        if (message == TrayCallbackMessage)
        {
            var mouseMessage = unchecked((uint)lParam.ToInt64());
            if (mouseMessage == NativeMethods.WM_LBUTTONDBLCLK) ShowSettings();
            else if (mouseMessage == NativeMethods.WM_RBUTTONUP) ShowTrayMenu();
            return IntPtr.Zero;
        }
        if (message == NativeMethods.WM_COMMAND)
        {
            switch ((uint)(wParam.ToInt64() & 0xffff))
            {
                case MenuOpen: ShowSettings(); return IntPtr.Zero;
                case MenuToggle: ToggleEffects(); return IntPtr.Zero;
                case MenuExit: ExitApplication(); return IntPtr.Zero;
            }
        }
        return NativeMethods.DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private void ShowTrayMenu()
    {
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, MenuOpen, "打开设置");
        NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, MenuToggle, _controller.IsRunning ? "停止效果" : "启动效果");
        NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, 0, null);
        NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, MenuExit, "退出");
        NativeMethods.GetCursorPos(out var point);
        NativeMethods.SetForegroundWindow(_hwnd);
        NativeMethods.TrackPopupMenu(menu, NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_BOTTOMALIGN, point.X, point.Y, 0, _hwnd, IntPtr.Zero);
        NativeMethods.DestroyMenu(menu);
    }

    private void ToggleEffects()
    {
        if (_controller.IsRunning) StopEffects(); else StartEffects();
    }

    private void ShowSettings()
    {
        _appWindow.Show();
        Activate();
        StartUpdateCheck();
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        _appWindow.Hide();
    }

    private void ExitApplication()
    {
        _allowClose = true;
        _settings.Enabled = _controller.IsRunning;
        _store.Save(_settings);
        Close();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        NativeMethods.UnregisterHotKey(_hwnd, HotKeyId);
        NativeMethods.RemoveWindowSubclass(_hwnd, _subclassProc, SubclassId);
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref _trayData);
        _controller.StateChanged -= OnControllerStateChanged;
        Activated -= OnWindowActivated;
        _controller.Dispose();
        ((App)Application.Current).Shutdown();
    }
}
