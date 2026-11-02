# 刷新 Windows 图标 / 缩略图缓存。
#
# 场景：替换了 exe 的图标，但资源管理器仍显示旧图标。
#
# 为什么「重启资源管理器」没用：
#   图标缓存是磁盘文件（iconcache_*.db / thumbcache_*.db），explorer 重启后会
#   直接把这些文件读回来，旧图标仍在里面。重启只是换个进程读同一份缓存。
#   必须删文件，而不是重启进程。
#
# 为什么手动删不掉：
#   本机 Winlogon\AutoRestartShell = 1，explorer 被结束后 0.3~0.5 秒内
#   Winlogon 就会自动拉起新实例，它一起来立刻占用这些 .db。等你手速去删时
#   文件已经上锁（提示"正在使用中"）。所以要抢那个零点几秒的窗口，边删边重试。
#
# 为什么小图标好了大图标还是旧的：
#   小图标（16/32/48）走 iconcache_*.db；大图标（256+）走 thumbcache_*.db。
#   ie4uinit -show 只够得到 iconcache，够不到 thumbcache。
#
# 用法：  powershell -ExecutionPolicy Bypass -File .\refresh_icon_cache.ps1
#
# 说明：删除后 explorer 会自动重建空库，这是正常现象——重建的文件大小是
#       1048576（预分配），时间戳是新时间，里面没有旧图标数据。

$ErrorActionPreference = 'SilentlyContinue'

$dir = Join-Path $env:LOCALAPPDATA 'Microsoft\Windows\Explorer'
if (-not (Test-Path -LiteralPath $dir)) {
    Write-Output "缓存目录不存在：$dir"
    exit 1
}

# 先记录要删的清单。必须全量拿 *.db —— 只删固定几个会漏掉
# iconcache_96/768/1920/2560、thumbcache_768 等，那些也可能存着旧图标。
$targets = @(Get-ChildItem -LiteralPath $dir -Filter '*.db' -Force)
Write-Output "待清理 $($targets.Count) 个缓存文件。"

Write-Output "停止资源管理器..."
Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300

$removed = 0
$failed = @()
foreach ($f in $targets) {
    $ok = $false
    # 重试抢占：自动重启的 explorer 会边起来边锁文件，多试几轮能挤进去
    for ($k = 0; $k -lt 12; $k++) {
        try {
            Remove-Item -LiteralPath $f.FullName -Force -ErrorAction Stop
            $ok = $true
            break
        } catch {
            Start-Sleep -Milliseconds 100
        }
    }
    # 用删除是否成功来判断，不要用 Test-Path —— 删成功后 explorer 会立刻重建
    # 同名文件，Test-Path 仍为 true，会被误报成"删除失败"。
    if ($ok) { $removed++ } else { $failed += $f.Name }
}

if ((Get-Process explorer -ErrorAction SilentlyContinue).Count -eq 0) {
    Write-Output "启动资源管理器..."
    Start-Process explorer.exe
}
Start-Sleep -Seconds 2

Write-Output "已删除 $removed / $($targets.Count) 个缓存文件。"
if ($failed.Count -gt 0) {
    Write-Output ("真正没删掉的：" + ($failed -join ', '))
}

# 通知 shell 重建图标缓存索引（够不到 thumbcache，但那部分上面已经删了）
$ie4uinit = Join-Path $env:SystemRoot 'System32\ie4uinit.exe'
if (Test-Path -LiteralPath $ie4uinit) {
    & $ie4uinit -show
}

$inst = (Get-Process explorer -ErrorAction SilentlyContinue).Count
Write-Output "explorer 实例数：$inst"
Write-Output "提示：已打开的文件夹窗口需重新打开才会刷新图标。"
