using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace BootPilot.Models;

/// <summary>列表里的一条自启项（对应一个 BootPilot_ 前缀的计划任务）。</summary>
public sealed class AutoStartEntry : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _targetPath = string.Empty;
    private string _arguments = string.Empty;
    private string _workingDirectory = string.Empty;
    private bool _enabled = true;
    private bool _isLogonTrigger = true;
    private int _delaySeconds;

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); }
    }

    /// <summary>去掉 BootPilot_ 前缀后展示给用户的名字。</summary>
    public string DisplayName
    {
        get
        {
            const string prefix = "BootPilot_";
            return Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? Name[prefix.Length..]
                : Name;
        }
    }

    public string TargetPath
    {
        get => _targetPath;
        set
        {
            _targetPath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TargetMissing));
            OnPropertyChanged(nameof(TargetTip));
            OnPropertyChanged(nameof(SubLine));
        }
    }

    public string Arguments
    {
        get => _arguments;
        set { _arguments = value; OnPropertyChanged(); OnPropertyChanged(nameof(SubLine)); }
    }

    public string WorkingDirectory
    {
        get => _workingDirectory;
        set { _workingDirectory = value; OnPropertyChanged(); }
    }

    public bool IsLogonTrigger
    {
        get => _isLogonTrigger;
        set { _isLogonTrigger = value; OnPropertyChanged(); OnPropertyChanged(nameof(TriggerText)); }
    }

    public string TriggerText => IsLogonTrigger ? "用户登录时" : "系统启动时";

    public int DelaySeconds
    {
        get => _delaySeconds;
        set { _delaySeconds = value; OnPropertyChanged(); OnPropertyChanged(nameof(DelayText)); }
    }

    public string DelayText => DelaySeconds > 0 ? $"延迟 {DelaySeconds} 秒" : string.Empty;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(StateBrush));
            OnPropertyChanged(nameof(ToggleText));
            OnPropertyChanged(nameof(OpacityOfRow));
        }
    }

    public string StateText => Enabled ? "已启用" : "已禁用";

    public string ToggleText => Enabled ? "禁用" : "启用";

    public double OpacityOfRow => Enabled ? 1.0 : 0.55;

    public Brush StateBrush => Enabled
        ? new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x5B))
        : new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xB2));

    public bool TargetMissing => !string.IsNullOrWhiteSpace(TargetPath)
                                 && !File.Exists(TargetPath)
                                 && !Directory.Exists(TargetPath);

    public string TargetTip => TargetMissing
        ? "目标程序已不存在（路径失效），建议删除后重新添加"
        : TargetPath;

    /// <summary>名单里的第二行说明：目标路径 + 启动参数。</summary>
    public string SubLine
    {
        get
        {
            var text = TargetPath;
            if (!string.IsNullOrWhiteSpace(Arguments)) text += "  " + Arguments;
            return text;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
