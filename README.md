# BootPilot

把任意程序或文件变成开机自启项 —— 拖进去，点一下，就完事了。

背后用的是 Windows 计划任务（Task Scheduler），不是注册表 `Run` 键，所以支持延迟启动、指定工作目录、最高权限运行，也随时可以在系统里看到和管理。

![icon](app.ico)

## 它解决什么问题

想让某个软件开机自启，Windows 自带的做法要么是翻注册表，要么是打开任务计划程序一步步新建任务 —— 路径、参数、工作目录、触发器、权限，五六步点下来。BootPilot 把它压成一次拖拽。

## 功能

- **拖拽即填**：exe / bat / lnk / 任意文件拖进窗口，路径、任务名、工作目录自动填好
- **两种触发方式**
  - 用户登录时（默认）—— 推荐，界面正常可见，不需要管理员权限
  - 系统启动时 —— 不登录也运行，但没有桌面，只适合后台程序
- **延迟启动**：0–3600 秒，错开开机时的资源争抢
- **逐项可设**：启动参数、工作目录、以最高权限运行
- **列表管理**：试运行（验证配置）、启用/禁用、删除，目标文件失效会标红
- **安全边界**：所有任务统一 `BootPilot_` 前缀，只读写这一类，绝不触碰系统自带任务

## 使用

1. 双击 `BootPilot.exe`
2. 把要自启的程序拖进窗口
3. 点「添加自启项」

创建「系统启动时」的任务需要管理员权限。顶栏有权限徽章，点一下即可提权重启，已填写的内容会自动带过去。

## 构建

需要 .NET 8 SDK（Windows）。

```bash
dotnet build -c Release
dotnet publish -c Release -o publish
```

产物是单文件 `publish/BootPilot.exe`（framework-dependent，需要 .NET 8 Desktop Runtime）。

重新生成图标（纯 Python，无需 Pillow）：

```bash
python tools/make_icon.py
```

## 实现要点

任务读写走 `Schedule.Service` COM 接口（`dynamic` 调用），因为需要精确控制触发器类型、登录方式、运行级别和工作目录，这些用 `schtasks` 命令行很难干净地表达。

| 场景 | 登录方式 | 说明 |
|---|---|---|
| 用户登录时 | `TASK_LOGON_INTERACTIVE_TOKEN` | 有交互桌面，普通权限即可 |
| 系统启动时 | `TASK_LOGON_S4U` | 无需存密码，需要管理员权限 |

删除、启停优先走 COM，`schtasks.exe` 作为兜底。错误码会翻译成中文提示（比如 `0x80070005` 会直接引导你提权）。

## 兼容性

- Windows 10 / 11，x64
- .NET 8 Desktop Runtime
