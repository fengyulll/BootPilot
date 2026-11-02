using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();

        ItemsHost.ItemsSource = _items;

        _toastTimer.Interval = TimeSpan.FromSeconds(3.2);
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            HideToast();
        };

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdatePermissionBadge();
        LoadList();
        TbTarget.Focus();

        if (TaskSchedulerService.IsElevated)
            ShowToast("以管理员身份运行时 Windows 会阻止拖放文件，请改用「浏览…」按钮；建议平时用普通权限启动");
    }

    private void UpdatePermissionBadge()
    {
        if (TaskSchedulerService.IsElevated)
        {
            AdminBadge.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xE3));
            AdminBadgeText.Text = "管理员模式 · 拖放受限";
            AdminBadgeText.Foreground = (Brush)FindResource("B.Warn");
            AdminBadge.ToolTip =
                "以管理员身份运行时，Windows 的 UIPI 机制会阻止从资源管理器拖入文件。\n" +
                "建议用普通方式启动 BootPilot —— 只有真正需要权限的那一步才会弹 UAC。";
        }
        else
        {
            AdminBadge.Background = (Brush)FindResource("B.OkSoft");
            AdminBadgeText.Text = "按需提权";
            AdminBadgeText.Foreground = (Brush)FindResource("B.Ok");
            AdminBadge.ToolTip =
                "BootPilot 保持普通权限运行，才能接收资源管理器的拖放。\n" +
                "创建「系统启动时」的自启项时，会自动弹一次 UAC 申请权限。";
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BtnAdd.IsEnabled = !busy;
        BtnAdd.Content = busy ? "处理中…" : "添加自启项";
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

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

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

        bool created = false;
        bool elevatedUsed = false;
        string? error = null;

        SetBusy(true);
        try
        {
            if (TaskSchedulerService.IsElevated)
            {
                _service.CreateOrUpdate(spec);
                created = true;
            }
            else
            {
                try
                {
                    _service.CreateOrUpdate(spec);
                    created = true;
                }
                catch (BootPilotException ex) when (ex.NeedsElevation || boot)
                {
                    var result = await Task.Run(() => ElevationBroker.Run(
                        new ElevatedRequest { Action = "Create", Spec = spec }));

                    created = result.Ok;
                    elevatedUsed = result.Ok;
                    error = result.Ok ? null : result.Message;
                }
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            SetBusy(false);
        }

        if (created)
        {
            LoadList();
            ClearForm();
            ShowToast(elevatedUsed
                ? $"已通过管理员权限添加：{name}"
                : $"已添加自启项：{name}");
        }
        else if (!string.IsNullOrEmpty(error))
        {
            MessageBox.Show(this, error + ElevationHint(error), "BootPilot — 添加失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>即使提权也失败时，多半是被安全软件拦了，给一句可操作的建议。</summary>
    private static string ElevationHint(string message)
    {
        if (!message.Contains("拒绝访问") && !message.Contains("权限不足")) return string.Empty;
        return "\n\n如果刚才已经确认过管理员授权，那么很可能是安全软件（杀毒 / 安全卫士 / EDR）" +
               "拦截了「创建计划任务」这个动作。\n" +
               "可以尝试：把 BootPilot.exe 加入安全软件的白名单，或临时退出安全软件后重试。";
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

    private async void ToggleItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AutoStartEntry entry } || _busy) return;

        bool next = !entry.Enabled;
        string action = next ? "启用" : "禁用";

        try
        {
            _service.SetEnabled(entry.Name, next);
            entry.Enabled = next;
            ShowToast($"已{action}「{entry.DisplayName}」");
            return;
        }
        catch (BootPilotException ex) when (ex.NeedsElevation)
        {
            var result = await Task.Run(() => ElevationBroker.Run(new ElevatedRequest
            {
                Action = next ? "Enable" : "Disable",
                TaskName = entry.Name,
                Enable = next,
            }));

            if (result.Ok)
            {
                entry.Enabled = next;
                ShowToast($"已通过管理员权限{action}「{entry.DisplayName}」");
            }
            else
            {
                MessageBox.Show(this, result.Message + ElevationHint(result.Message),
                    $"BootPilot — {action}失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, $"BootPilot — {action}失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void DeleteItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AutoStartEntry entry } || _busy) return;

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
            return;
        }
        catch (BootPilotException ex) when (ex.NeedsElevation)
        {
            var result = await Task.Run(() => ElevationBroker.Run(new ElevatedRequest
            {
                Action = "Delete",
                TaskName = entry.Name,
            }));

            if (result.Ok)
            {
                _items.Remove(entry);
                UpdateListChrome();
                ShowToast($"已通过管理员权限删除「{entry.DisplayName}」");
            }
            else
            {
                MessageBox.Show(this, result.Message + ElevationHint(result.Message),
                    "BootPilot — 删除失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
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

        if (!boot) return;

        ShowToast(TaskSchedulerService.IsElevated
            ? "提示：界面类程序在系统启动时运行是看不见的，建议用「用户登录时」"
            : "添加时会自动请求一次管理员权限");
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
