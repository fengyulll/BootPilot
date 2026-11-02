using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace BootPilot.Services;

/// <summary>交给提权子进程执行的一次性请求（通过临时 JSON 文件传递）。</summary>
public sealed class ElevatedRequest
{
    public string Action { get; set; } = "Create";

    public TaskSpec? Spec { get; set; }

    public string TaskName { get; set; } = string.Empty;

    public bool Enable { get; set; }
}

/// <summary>
/// 主界面始终保持普通权限（这样才收得到资源管理器的拖放），
/// 真正需要管理员权限的那一下，才启动一个短命的提权子进程去做。
/// </summary>
public static class ElevationBroker
{
    public static (bool Ok, string Message) Run(ElevatedRequest request)
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return (false, "无法定位 BootPilot 自身的可执行文件路径。");

        string dir = Path.Combine(Path.GetTempPath(), "BootPilot");
        string token = Guid.NewGuid().ToString("N");
        string requestPath = Path.Combine(dir, token + ".json");
        string resultPath = Path.Combine(dir, token + ".result");

        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(requestPath, JsonSerializer.Serialize(request), new UTF8Encoding(false));

            var startInfo = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = "--apply \"" + requestPath + "\" \"" + resultPath + "\"",
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            try
            {
                Process.Start(startInfo);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return (false, "已取消管理员授权，操作没有执行。");
            }

            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(resultPath))
                {
                    string text = File.ReadAllText(resultPath, Encoding.UTF8).Trim();
                    if (text == "OK") return (true, string.Empty);
                    return (false, text.StartsWith("ERR:", StringComparison.Ordinal) ? text[4..] : text);
                }
                Thread.Sleep(100);
            }

            return (false, "等待提权操作超时（120 秒）。请重试一次。");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            try { File.Delete(requestPath); } catch { }
            try { File.Delete(resultPath); } catch { }
        }
    }
}

/// <summary>提权子进程的入口：执行一次操作，把结果写进结果文件后退出。</summary>
public static class ApplyRunner
{
    public static int Run(string requestPath, string resultPath)
    {
        try
        {
            string json = File.ReadAllText(requestPath, Encoding.UTF8);
            var request = JsonSerializer.Deserialize<ElevatedRequest>(json)
                          ?? throw new InvalidOperationException("提权请求内容无效。");

            var service = new TaskSchedulerService();
            switch (request.Action)
            {
                case "Create":
                    if (request.Spec is null) throw new InvalidOperationException("提权请求缺少任务定义。");
                    service.CreateOrUpdate(request.Spec);
                    break;

                case "Delete":
                    service.Delete(request.TaskName);
                    break;

                case "Enable":
                case "Disable":
                    service.SetEnabled(request.TaskName, request.Enable);
                    break;

                default:
                    throw new InvalidOperationException("未知的提权操作：" + request.Action);
            }

            File.WriteAllText(resultPath, "OK", new UTF8Encoding(false));
            return 0;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(resultPath, "ERR:" + ex.Message, new UTF8Encoding(false)); } catch { }
            return 1;
        }
    }
}
