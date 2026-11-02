using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BootPilot.Models;
using BootPilot.Services;
using Microsoft.Win32;

namespace BootPilot;

public partial class MainWindow : Window
{
    private readonly TaskSchedulerService _service = new();
    private readonly ObservableCollection<AutoStartEntry> _items = new();
    private readonly DispatcherTimer _toastTimer = new();

    private string _lastAutoWorkDir = string.Empty;
    private bool _suppressTriggerPrompt;

    public MainWindow()
    {
        InitializeComponent();

        ItemsHost.ItemsSource = _items;

        _toastTimer.Interval = TimeSpan.FromSeconds(2.8);
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            HideToast();
        };

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateAdminBadge();
        _suppressTriggerPrompt = true;
        ApplyStartupArguments();
        _suppressTriggerPrompt = false;
        LoadList();
        TbTarget.Focus();
    }

    private void UpdateAdminBadge()
    {
        bool elevated = TaskSchedulerService.IsElevated;

        AdminBadge.Background = elevated
            ? (Brush)FindResource("B.OkSoft")
            : new SolidColorBrush(Color.FromRgb(0xF2, 0xF4, 0xF8));
        AdminBadgeText.Text = elevated ? "管理员模式" : "普通权限 · 点此提权";
        AdminBadgeText.Foreground = elevated
            ? (Brush)FindResource("B.Ok")
            : (Brush)FindResource("B.TextSub");
        AdminBadge.Cursor = elevated ? Cursors.Arrow : Cursors.Hand;
        AdminBadge.ToolTip = elevated
            ? "BootPilot 已以管理员身份运行，可创建任意类型的自启项"
            : "点此以管理员身份重新启动。创建「系统启动时」或「最高权限运行」的自启项需要它";
    }

    private void AdminBadge_Click(object sender, MouseButtonEventArgs e)
    {
        if (TaskSchedulerService.IsElevated) return;
        RelaunchElevated(null, null, 0, false, false);
    }

    // ==================== 启动参数（提权重启时带回填写内容） ====================

    private void ApplyStartupArguments()
    {
        var args = Environment.GetCommandLineArgs();
        string? target = null, name = null, extraArgs = null;
        int delay = 0;
        bool admin = false, boot = false;

        for (int i = 1; i < args.Length; i++)
        {
            string value = i + 1 < args.Length ? args[i + 1] : string.Empty;
            switch (args[i].ToLowerInvariant())
            {
                case "--target": target = value; i++; break;
                case "--name": name = value; i++; break;
                case "--args": extraArgs = value; i++; break;
                case "--delay": int.TryParse(value, out delay); i++; break;
                case "--admin": admin = true; break;
                case "--boot": boot = true; break;
            }
        }

        if (boot) RbBoot.IsChecked = true;
        if (admin) ChkAdmin.IsChecked = true;
        if (delay > 0) TbDelay.Text = Math.Clamp(delay, 0, 3600).ToString();

        if (!string.IsNullOrWhiteSpace(target))
        {
            ApplyTarget(target);
            if (!string.IsNullOrWhiteSpace(extraArgs)) TbArgs.Text = extraArgs;
            if (!string.IsNullOrWhiteSpace(name)) TbName.Text = SanitizeName(name);
        }
    }

    // ==================== 拖拽 ====================

    private void DropZone_DragEnter(object sender, DragEventArgs e) => HandleDragOver(e);

    private void DropZone_DragOver(object sender, DragEventArgs e) => HandleDragOver(e);

    private void HandleDragOver(DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        SetDropActive(ok);
        e.Handled = true;
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e) => SetDropActive(false);

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        SetDropActive(false);
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;

        ApplyTarget(files[0]);
        if (files.Length > 1)
            ShowToast($"一次只能添加一个，已取用第一个：{Path.GetFileName(files[0])}");

        e.Handled = true;
    }

    private void SetDropActive(bool active)
    {
        DropZone.Background = active
            ? new SolidColorBrush(Color.FromRgb(0xEF, 0xF4, 0xFE))
            : Brushes.White;
        DropZone.BorderBrush = active
            ? (Brush)FindResource("B.Accent")
            : (Brush)FindResource("B.Border");
        DropDash.Stroke = active
            ? (Brush)FindResource("B.Accent")
            : new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE5));
        DropTitle.Text = active ? "松手即添加" : "把程序或文件拖到这里";
    }

    private void DropZone_Click(object sender, MouseButtonEventArgs e) => PickFile();

    // ==================== 选择目标 ====================

    private void Browse_Click(object sender, RoutedEventArgs e) => PickFile();

    private void PickFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要开机自启的程序或文件",
            Filter = "程序与快捷方式|*.exe;*.bat;*.cmd;*.lnk;*.com|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) == true)
            ApplyTarget(dialog.FileName);
    }

    private void ApplyTarget(string path)
    {
        TbTarget.Text = path;

        string dir = Directory.Exists(path)
            ? path
            : Path.GetDirectoryName(path) ?? string.Empty;

        if (string.IsNullOrWhiteSpace(TbWorkDir.Text) || TbWorkDir.Text == _lastAutoWorkDir)
            TbWorkDir.Text = dir;

        _lastAutoWorkDir = dir;
        TbName.Text = SuggestName(path);
    }

    private void AutoWorkDir_Click(object sender, RoutedEventArgs e)
    {
        string target = TbTarget.Text.Trim();
        if (target.Length == 0)
        {
            ShowToast("请先选择目标程序");
            return;
        }

        string dir = Directory.Exists(target)
            ? target
            : Path.GetDirectoryName(target) ?? string.Empty;

        TbWorkDir.Text = dir;
        _lastAutoWorkDir = dir;
        ShowToast("工作目录已设为目标所在目录");
    }

    private static string SuggestName(string path)
    {
        string raw;
        try
        {
            raw = Directory.Exists(path)
                ? new DirectoryInfo(path).Name
                : Path.GetFileNameWithoutExtension(path);
        }
        catch { raw = "自启项"; }

        if (string.IsNullOrWhiteSpace(raw)) raw = "自启项";
        return SanitizeName(raw);
    }

    private static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        return name.Length > 60 ? name[..60] : name;
    }

    // ==================== 添加 ====================

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        string target = TbTarget.Text.Trim().Trim('"');

        if (target.Length == 0)
        {
            ShowToast("请先选择要自启的程序或文件");
            return;
        }

        if (!File.Exists(target) && !Directory.Exists(target))
        {
            MessageBox.Show(this,
                "找不到这个路径：\n\n" + target + "\n\n请确认文件没有被移动、重命名或删除。",
                "BootPilot — 目标不存在", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string name = SanitizeName(TbName.Text);
        if (name.Length == 0)
        {
            name = SuggestName(target);
            TbName.Text = name;
        }

        bool boot = RbBoot.IsChecked == true;
        bool admin = ChkAdmin.IsChecked == true;
        int delay = ParseDelay();

        if (boot && !TaskSchedulerService.IsElevated)
        {
            var answer = MessageBox.Show(this,
                "「系统启动时」触发需要管理员权限。\n\n是否立即以管理员身份重新启动 BootPilot？\n（当前填写的内容会自动带过去，不用重填）",
                "BootPilot — 需要管理员权限", MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (answer == MessageBoxResult.Yes)
                RelaunchElevated(target, name, delay, admin, boot);
            return;
        }

        var spec = new TaskSpec
        {
            TaskName = TaskSchedulerService.Prefix + name,
            TargetPath = target,
            Arguments = TbArgs.Text.Trim(),
            WorkingDirectory = TbWorkDir.Text.Trim(),
            Trigger = boot ? TriggerKind.Boot : TriggerKind.Logon,
            DelaySeconds = delay,
            RunAsAdmin = admin,
        };

        if (_service.Exists(spec.TaskName))
        {
            var answer = MessageBox.Show(this,
                $"已经有一个叫「{name}」的自启项了。\n\n要用现在的设置覆盖它吗？",
                "BootPilot — 同名项已存在", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes) return;
        }

        try
        {
            _service.CreateOrUpdate(spec);
            LoadList();
            ClearForm();
            ShowToast("已添加自启项：" + name);

            if (admin && !TaskSchedulerService.IsElevated)
                ShowToast("已添加，但「最高权限运行」通常需要管理员账户，运行失败请改回普通权限");
        }
        catch (BootPilotException ex) when (ex.Message.Contains("权限不足"))
        {
            var answer = MessageBox.Show(this,
                ex.Message + "\n\n是否立即以管理员身份重新启动 BootPilot？\n（当前填写的内容会自动带过去，不用重填）",
                "BootPilot — 需要管理员权限", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (answer == MessageBoxResult.Yes)
                RelaunchElevated(target, name, delay, admin, boot);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "BootPilot — 添加失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private int ParseDelay()
    {
        string text = TbDelay.Text.Trim();
        if (int.TryParse(text, out int value) && value >= 0 && value <= 3600)
            return value;

        TbDelay.Text = "0";
        ShowToast("延迟秒数需为 0–3600 之间的整数，已重置为 0");
        return 0;
    }

    private void RelaunchElevated(string? target, string? name, int delay, bool admin, bool boot)
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
                throw new InvalidOperationException("无法定位 BootPilot 自身的可执行文件路径。");

            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(target))
            {
                sb.Append("--target \"").Append(target.Replace("\"", string.Empty)).Append('"');
                if (!string.IsNullOrWhiteSpace(name))
                    sb.Append(" --name \"").Append(name.Replace("\"", string.Empty)).Append('"');
                sb.Append(" --delay ").Append(delay);
                if (admin) sb.Append(" --admin");
                if (boot) sb.Append(" --boot");
            }

            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = sb.ToString(),
            });

            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "提权启动失败：" + ex.Message + "\n\n你可以关闭 BootPilot，右键选择「以管理员身份运行」后再操作一次。",
                "BootPilot — 提权失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ClearForm()
    {
        TbName.Clear();
        TbTarget.Clear();
        TbArgs.Clear();
        TbWorkDir.Clear();
        TbDelay.Text = "0";
        _lastAutoWorkDir = string.Empty;
        TbTarget.Focus();
    }

    // ==================== 列表操作 ====================

    private void LoadList()
    {
        try
        {
            var loaded = _service.LoadOwned();
            _items.Clear();
            foreach (var item in loaded) _items.Add(item);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "BootPilot — 读取自启项失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        UpdateListChrome();
    }

    private void UpdateListChrome()
    {
        CountText.Text = _items.Count.ToString();
        EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        LoadList();
        ShowToast("列表已刷新");
    }

    private void RunItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string taskName }) return;

        try
        {
            _service.RunNow(taskName);
            ShowToast("已触发运行一次");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "BootPilot — 试运行失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ToggleItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AutoStartEntry entry }) return;

        bool next = !entry.Enabled;
        try
        {
            _service.SetEnabled(entry.Name, next);
            entry.Enabled = next;
            ShowToast(next ? $"已启用「{entry.DisplayName}」" : $"已禁用「{entry.DisplayName}」");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "BootPilot — 操作失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DeleteItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AutoStartEntry entry }) return;

        var answer = MessageBox.Show(this,
            $"确定要删除自启项「{entry.DisplayName}」吗？\n\n删除后它就不会再自动启动，但程序本身不会被卸载或删除。",
            "BootPilot — 删除自启项", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes) return;

        try
        {
            _service.Delete(entry.Name);
            _items.Remove(entry);
            UpdateListChrome();
            ShowToast($"已删除「{entry.DisplayName}」");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "BootPilot — 删除失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenTaskScheduler_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("taskschd.msc") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "打开任务计划程序失败：" + ex.Message,
                "BootPilot", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ==================== 触发方式 ====================

    private void Trigger_Checked(object sender, RoutedEventArgs e)
    {
        if (DelayLabel is null) return;

        bool boot = RbBoot.IsChecked == true;
        DelayLabel.Text = boot ? "启动后延迟" : "登录后延迟";

        if (!boot || _suppressTriggerPrompt) return;

        if (!TaskSchedulerService.IsElevated)
            ShowToast("「系统启动时」需要管理员权限，添加时会提示你提权");
        else
            ShowToast("提示：界面类程序在系统启动时运行是看不见的，建议用「用户登录时」");
    }

    private void TbDelay_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
    }

    // ==================== 浮动提示 ====================

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        Toast.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));

        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void HideToast()
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
        fade.Completed += (_, _) => Toast.Visibility = Visibility.Collapsed;
        Toast.BeginAnimation(OpacityProperty, fade);
    }
}
