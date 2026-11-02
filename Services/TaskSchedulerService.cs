using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml;
using BootPilot.Models;
using Microsoft.CSharp.RuntimeBinder;

namespace BootPilot.Services;

public enum TriggerKind
{
    /// <summary>用户登录时（交互式，界面正常显示）。</summary>
    Logon,

    /// <summary>系统启动时（无交互桌面，适合后台程序）。</summary>
    Boot,
}

public sealed class TaskSpec
{
    public string TaskName { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public TriggerKind Trigger { get; set; } = TriggerKind.Logon;
    public int DelaySeconds { get; set; }
    public bool RunAsAdmin { get; set; }
}

public sealed class BootPilotException : Exception
{
    public BootPilotException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// 通过 Windows 任务计划服务（Schedule.Service COM）读写计划任务。
/// 只管理带 BootPilot_ 前缀的任务，绝不触碰系统自带任务。
/// </summary>
public sealed class TaskSchedulerService
{
    public const string Prefix = "BootPilot_";

    private const int TaskCreateOrUpdate = 6;
    private const int ActionExec = 0;
    private const int TriggerBoot = 8;
    private const int TriggerLogon = 9;
    private const int RunLevelLeast = 0;
    private const int RunLevelHighest = 1;
    private const int LogonInteractiveToken = 3;
    private const int LogonS4U = 6;
    private const int EnumHidden = 1;

    public static bool IsElevated { get; } = DetectElevation();

    public static string CurrentUser { get; } = GetCurrentUserName();

    private static string GetCurrentUserName()
    {
        try { return WindowsIdentity.GetCurrent().Name; }
        catch { return Environment.UserDomainName + "\\" + Environment.UserName; }
    }

    private static bool DetectElevation()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    // ---------------- 读取 ----------------

    /// <summary>枚举本工具创建的全部自启项。</summary>
    public List<AutoStartEntry> LoadOwned()
    {
        var list = new List<AutoStartEntry>();
        try
        {
            dynamic service = CreateService();
            dynamic root = service.GetFolder("\\");
            dynamic tasks = root.GetTasks(EnumHidden);

            foreach (dynamic task in tasks)
            {
                string name;
                try { name = (string?)task.Name ?? string.Empty; }
                catch { continue; }

                if (!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) continue;

                var entry = new AutoStartEntry { Name = name };
                try { entry.Enabled = (bool)task.Enabled; } catch { /* 无读取权限时保持默认 */ }

                try { FillFromDefinition(entry, task.Definition); }
                catch { /* 部分任务的定义不可读，忽略即可 */ }

                list.Add(entry);
            }
        }
        catch (Exception ex)
        {
            throw new BootPilotException(Humanize(ex), ex);
        }

        list.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
        return list;
    }

    private static void FillFromDefinition(AutoStartEntry entry, dynamic definition)
    {
        foreach (dynamic action in definition.Actions)
        {
            entry.TargetPath = (string?)action.Path ?? string.Empty;
            entry.Arguments = (string?)action.Arguments ?? string.Empty;
            try { entry.WorkingDirectory = (string?)action.WorkingDirectory ?? string.Empty; } catch { }
            break;
        }

        foreach (dynamic trigger in definition.Triggers)
        {
            int type = (int)trigger.Type;
            entry.IsLogonTrigger = type != TriggerBoot;
            try { entry.DelaySeconds = ParseIsoDelay((string?)trigger.Delay); } catch { }
            break;
        }
    }

    private static int ParseIsoDelay(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return 0;
        try
        {
            var span = XmlConvert.ToTimeSpan(iso);
            return span.TotalSeconds <= 0 ? 0 : (int)Math.Round(span.TotalSeconds);
        }
        catch { return 0; }
    }

    public bool Exists(string taskName)
    {
        try
        {
            dynamic service = CreateService();
            dynamic root = service.GetFolder("\\");
            root.GetTask(taskName);
            return true;
        }
        catch { return false; }
    }

    // ---------------- 写入 ----------------

    public void CreateOrUpdate(TaskSpec spec)
    {
        try
        {
            bool boot = spec.Trigger == TriggerKind.Boot;

            dynamic service = CreateService();
            dynamic root = service.GetFolder("\\");
            dynamic task = service.NewTask(0);

            dynamic info = task.RegistrationInfo;
            info.Description = "由 BootPilot 创建的自启任务。如需移除，请打开 BootPilot 删除。";
            info.Author = CurrentUser;

            dynamic principal = task.Principal;
            principal.UserId = CurrentUser;
            principal.RunLevel = spec.RunAsAdmin ? RunLevelHighest : RunLevelLeast;
            principal.LogonType = boot ? LogonS4U : LogonInteractiveToken;

            dynamic trigger = task.Triggers.Create(boot ? TriggerBoot : TriggerLogon);
            trigger.Enabled = true;
            if (spec.DelaySeconds > 0)
                trigger.Delay = "PT" + spec.DelaySeconds.ToString() + "S";

            dynamic action = task.Actions.Create(ActionExec);
            action.Path = spec.TargetPath;
            action.Arguments = spec.Arguments ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(spec.WorkingDirectory))
                action.WorkingDirectory = spec.WorkingDirectory;

            dynamic settings = task.Settings;
            settings.Enabled = true;
            settings.AllowDemandStart = true;
            settings.StartWhenAvailable = true;
            settings.DisallowStartIfOnBatteries = false;
            settings.StopIfGoingOnBatteries = false;
            settings.RunOnlyIfIdle = false;
            settings.RunOnlyIfNetworkAvailable = false;
            settings.ExecutionTimeLimit = "PT0S";
            settings.MultipleInstances = 3;
            settings.Hidden = false;

            object? password = null;
            root.RegisterTaskDefinition(
                spec.TaskName,
                task,
                TaskCreateOrUpdate,
                CurrentUser,
                password,
                boot ? LogonS4U : LogonInteractiveToken);
        }
        catch (Exception ex)
        {
            throw new BootPilotException(Humanize(ex), ex);
        }
    }

    public void Delete(string taskName)
    {
        try
        {
            dynamic service = CreateService();
            dynamic root = service.GetFolder("\\");
            root.DeleteTask(taskName, 0);
            return;
        }
        catch (Exception ex)
        {
            if (RunSchtasks("/delete", "/tn", taskName, "/f"))
                return;
            throw new BootPilotException("删除计划任务失败：" + Humanize(ex), ex);
        }
    }

    public void SetEnabled(string taskName, bool enabled)
    {
        try
        {
            dynamic service = CreateService();
            dynamic root = service.GetFolder("\\");
            dynamic task = root.GetTask(taskName);
            task.Enabled = enabled;
            return;
        }
        catch (Exception ex)
        {
            bool ok = RunSchtasks("/change", "/tn", taskName, enabled ? "/enable" : "/disable");
            if (ok) return;
            throw new BootPilotException(
                (enabled ? "启用" : "禁用") + "计划任务失败：" + Humanize(ex), ex);
        }
    }

    /// <summary>立即执行一次，用于验证配置是否正确。</summary>
    public void RunNow(string taskName)
    {
        try
        {
            dynamic service = CreateService();
            dynamic root = service.GetFolder("\\");
            dynamic task = root.GetTask(taskName);
            task.Run(Type.Missing);
            return;
        }
        catch (Exception ex)
        {
            if (RunSchtasks("/run", "/tn", taskName)) return;
            throw new BootPilotException(
                "启动失败。可能原因：目标程序已被移动或删除，或当前账户对该任务没有运行权限。", ex);
        }
    }

    // ---------------- 内部工具 ----------------

    private static dynamic CreateService()
    {
        var comType = Type.GetTypeFromProgID("Schedule.Service");
        if (comType is null)
            throw new BootPilotException("当前系统未提供任务计划服务（Schedule.Service），无法管理自启项。");

        dynamic service = Activator.CreateInstance(comType)!;
        service.Connect();
        return service;
    }

    private static bool RunSchtasks(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return false;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(15000))
            {
                try { p.Kill(true); } catch { }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string Humanize(Exception ex)
    {
        if (ex is BootPilotException bpe) return bpe.Message;

        if (ex is COMException com)
        {
            return (uint)com.HResult switch
            {
                0x80070005 => "权限不足（拒绝访问）。创建「系统启动时」的任务，或以管理员权限运行的程序，需要先以管理员身份启动 BootPilot。",
                0x80070002 => "找不到目标程序或文件，请确认路径没有被移动或删除。",
                0x8007007B => "路径格式不正确，请检查是否含有非法字符。",
                0x80070057 => "参数无效，目标路径或参数可能包含计划任务不接受的字符。",
                0x8004131F => "任务计划服务当前不可用。请确认 Windows 服务 Task Scheduler 正在运行。",
                0x80041318 => "这个任务名已经被占用，请换一个名字。",
                _ => "系统返回错误 0x" + com.HResult.ToString("X8") + "：" + com.Message,
            };
        }

        if (ex is RuntimeBinderException || ex is InvalidCastException)
            return "与任务计划服务的通信失败（COM 调用异常）。请重启 BootPilot 后再试一次。";

        return ex.Message;
    }
}
