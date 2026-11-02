# 刷新 Windows 图标 / 缩略图缓存。
#
# 场景：替换了 exe 的图标，但资源管理器仍显示旧图标。
#   - 小图标（16/32/48）走 iconcache_*.db
#   - 大图标（256）走 thumbcache_*.db  <- ie4uinit -show 够不到这里
#
# 注意：本机 Winlogon\AutoRestartShell = 1，explorer 被强杀后约 0.4 秒就会
# 自动重启，缓存文件立刻被重新锁住，所以删除必须"抢窗口"——边删边重试。
#
# 用法：  powershell -ExecutionPolicy Bypass -File .\refresh_icon_cache.ps1

$ErrorActionPreference = 'SilentlyContinue'

$dir = Join-Path $env:LOCALAPPDATA 'Microsoft\Windows\Explorer'
if (-not (Test-Path -LiteralPath $dir)) {
    Write-Output "缓存目录不存在：$dir"
    exit 1
}

# 优先删和图标相关的几个，索引文件最后删（它们最容易一直被占用）
$targets = @(
    'iconcache_16.db', 'iconcache_32.db', 'iconcache_48.db', 'iconcache_256.db',
    'thumbcache_16.db', 'thumbcache_32.db', 'thumbcache_48.db',
    'thumbcache_256.db', 'thumbcache_1280.db',
    'iconcache_idx.db', 'thumbcache_idx.db'
)

Write-Output "停止资源管理器..."
Stop-Process -Name explorer -Force
Start-Sleep -Milliseconds 400

$removed = 0
$locked = @()
foreach ($t in $targets) {
    $p = Join-Path $dir $t
    if (-not (Test-Path -LiteralPath $p)) { continue }

    $done = $false
    for ($k = 0; $k -lt 8; $k++) {
        try {
            Remove-Item -LiteralPath $p -Force -ErrorAction Stop
            $done = $true
            break
        } catch {
            Start-Sleep -Milliseconds 120
        }
    }
    if ($done) { $removed++ } else { $locked += $t }
}

Write-Output "启动资源管理器..."
Start-Process explorer.exe
Start-Sleep -Seconds 2

Write-Output "已删除 $removed 个缓存文件。"
if ($locked.Count -gt 0) {
    Write-Output ("仍有占用（下次重启后会自动失效）：" + ($locked -join ', '))
}

$inst = (Get-Process explorer).Count
Write-Output "explorer 实例数：$inst"
