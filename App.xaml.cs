using System;
using System.Windows;
using System.Windows.Threading;
using BootPilot.Services;

namespace BootPilot;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 提权子进程模式：只执行一次任务操作，然后立刻退出，不创建任何窗口。
        if (e.Args.Length >= 3 &&
            string.Equals(e.Args[0], "--apply", StringComparison.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int exitCode = ApplyRunner.Run(e.Args[1], e.Args[2]);
            Environment.Exit(exitCode);
            return;
        }

        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        ShowFatal(e.Exception);
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex) ShowFatal(ex);
    }

    private static void ShowFatal(Exception ex)
    {
        MessageBox.Show(
            "BootPilot 遇到了未预期的错误：\n\n" +
            ex.Message +
            "\n\n详细信息：\n" + ex.GetType().Name +
            "\n\n你可以关闭本窗口后重试。若问题持续，请把这段信息反馈给开发者。",
            "BootPilot — 出错了",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
