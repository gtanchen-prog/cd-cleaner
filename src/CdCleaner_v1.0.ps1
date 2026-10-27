#requires -Version 5.1
<#
  C盘清理助手 v1.0（绿色单文件版）
  ============================================================
  只清理：临时文件 / 浏览器缓存 / 应用缓存 / 错误报告 / 崩溃与内存转储 /
          预读取缓存 / 更新下载缓存 / 缩略图图标缓存 / 各盘根目录 Temp /
          回收站（单独确认后清空）。
  不触碰：系统文件、页文件、休眠文件、系统还原点、WinSxS、Installer，
          以及个人文档、桌面、下载、图片等非缓存内容。
  适配：Windows 10 / 11（自带 PowerShell 5.1），无需安装，拷贝即可运行。
#>
param(
    [switch]$SelfTest,
    [switch]$Elevated
)

$ErrorActionPreference = 'Continue'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# 高 DPI 适配（失败不影响运行）
try {
    Add-Type -Namespace 'Win32Api' -Name 'Dpi' -MemberDefinition '[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetProcessDPIAware();' -ErrorAction Stop
    [void][Win32Api.Dpi]::SetProcessDPIAware()
} catch { }

# ------------------------------------------------------------
# 全局状态
# ------------------------------------------------------------
$script:AppVersion = '1.0'
$script:AppTitle   = 'C盘清理助手 v1.0'
$script:ScriptDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $script:ScriptDir) { $script:ScriptDir = (Get-Location).Path }
$script:LogFile    = Join-Path $script:ScriptDir '清理日志.txt'
$script:Busy       = $false
$script:StopRequested = $false
$script:IsAdmin    = $false
$script:MainForm   = $null
$script:LogBox     = $null
$script:TargetItems = @{}
$script:DriveInfoList = @()

# 日志目录不可写时回退到临时目录
try {
    Add-Content -LiteralPath $script:LogFile -Value '' -Encoding UTF8 -ErrorAction Stop
} catch {
    $script:LogFile = Join-Path $env:TEMP 'C盘清理助手日志.txt'
}

# ------------------------------------------------------------
# 基础工具
# ------------------------------------------------------------
function Test-AdminRight {
    try {
        $id = [Security.Principal.WindowsIdentity]::GetCurrent()
        $p  = New-Object Security.Principal.WindowsPrincipal($id)
        return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch { return $false }
}

function Format-Size {
    param([long]$Bytes)
    if ($Bytes -ge 1GB) { return ('{0:N2} GB' -f ($Bytes / 1GB)) }
    if ($Bytes -ge 1MB) { return ('{0:N1} MB' -f ($Bytes / 1MB)) }
    if ($Bytes -ge 1KB) { return ('{0:N0} KB' -f ($Bytes / 1KB)) }
    return ('{0} B' -f $Bytes)
}

function Write-UiLog {
    param([string]$Message, [string]$Level = 'INFO')
    $line = ('[{0}] {1}' -f (Get-Date -Format 'HH:mm:ss'), $Message)
    $color = [System.Drawing.Color]::FromArgb(70, 70, 70)
    if ($Level -eq 'OK')   { $color = [System.Drawing.Color]::FromArgb(20, 115, 60) }
    if ($Level -eq 'WARN') { $color = [System.Drawing.Color]::FromArgb(170, 105, 0) }
    if ($Level -eq 'ERR')  { $color = [System.Drawing.Color]::FromArgb(175, 30, 30) }
    if ($script:LogBox -and -not $script:LogBox.IsDisposed) {
        $script:LogBox.SelectionStart = $script:LogBox.TextLength
        $script:LogBox.SelectionLength = 0
        $script:LogBox.SelectionColor = $color
        $script:LogBox.AppendText($line + "`r`n")
        $script:LogBox.ScrollToCaret()
    }
    try { Add-Content -LiteralPath $script:LogFile -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue } catch { }
}

function Update-Ui {
    [System.Windows.Forms.Application]::DoEvents()
}

function Set-StatusText {
    param([string]$Text)
    if ($script:LblStatus -and -not $script:LblStatus.IsDisposed) { $script:LblStatus.Text = $Text }
}

function Set-BusyUi {
    if ($script:MainForm -and $script:MainForm.IsDisposed) { return }
    if ($script:BtnStart)    { $script:BtnStart.Enabled   = (-not $script:Busy) }
    if ($script:BtnScan)     { $script:BtnScan.Enabled    = (-not $script:Busy) }
    if ($script:BtnBin)      { $script:BtnBin.Enabled     = (-not $script:Busy) }
    if ($script:BtnStop)     { $script:BtnStop.Enabled    = $script:Busy }
    if ($script:MainForm) {
        if ($script:Busy) { $script:MainForm.Cursor = [System.Windows.Forms.Cursors]::WaitCursor }
        else              { $script:MainForm.Cursor = [System.Windows.Forms.Cursors]::Default }
    }
    Update-Ui
}

# ------------------------------------------------------------
# 安全校验 / 体积 / 删除
# ------------------------------------------------------------
function Test-SafeTargetPath {
    param([string]$Full)
    if (-not $Full) { return $false }
    $Full = ([string]$Full).TrimEnd('\')
    if ($Full -match '^[A-Za-z]:$') { return $false }
    # 个人资料目录一律拒绝
    if ($Full -match '\\(Documents|Desktop|Downloads|Pictures|Videos|Music|OneDrive)(\\|$)') { return $false }
    $lower = $Full.ToLowerInvariant()
    if ($env:SystemRoot   -and $lower -eq $env:SystemRoot.ToLowerInvariant())   { return $false }
    if ($env:USERPROFILE  -and $lower -eq $env:USERPROFILE.ToLowerInvariant())  { return $false }
    if ($env:LOCALAPPDATA -and $lower -eq $env:LOCALAPPDATA.ToLowerInvariant()) { return $false }
    if ($env:ProgramData  -and $lower -eq $env:ProgramData.ToLowerInvariant())  { return $false }
    if ($script:ScriptDir -and $lower.StartsWith($script:ScriptDir.ToLowerInvariant() + '\')) { return $false }
    foreach ($frag in @('\windows\system32', '\windows\syswow64', '\windows\winsxs', '\windows\installer', '\windows\servicing', '\system volume information')) {
        if ($lower.Contains($frag)) { return $false }
    }
    return $true
}

function Get-PathSizeBytes {
    param([string]$Path)
    if (-not $Path) { return [long]0 }
    if (-not (Test-Path -LiteralPath $Path)) { return [long]0 }
    $sum = [long]0
    try {
        foreach ($item in (Get-ChildItem -LiteralPath $Path -Force -Recurse -ErrorAction SilentlyContinue)) {
            if ($item.PSIsContainer) { continue }
            try { $sum += [long]$item.Length } catch { }
        }
    } catch { }
    return $sum
}

function Clear-PathContent {
    param([string]$Path)
    if (-not $Path) { return }
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $full = [System.IO.Path]::GetFullPath($Path)
    $full = $full.TrimEnd('\')
    if (-not (Test-SafeTargetPath $full)) { return }

    if (Test-Path -LiteralPath $full -PathType Leaf) {
        try { Remove-Item -LiteralPath $full -Force -ErrorAction Stop } catch { }
        return
    }

    try { & cmd.exe /c "rd /s /q `"$full`"" 2>$null } catch { }
    if (-not (Test-Path -LiteralPath $full)) {
        try { New-Item -ItemType Directory -Force -Path $full -ErrorAction Stop | Out-Null } catch { }
    }
}

# ------------------------------------------------------------
# 清理目标解析
# ------------------------------------------------------------
function Get-UserLocalDirs {
    $dirs = New-Object System.Collections.Generic.List[string]
    if ($env:LOCALAPPDATA) { $dirs.Add($env:LOCALAPPDATA) }
    if ($script:IsAdmin) {
        $usersRoot = Split-Path -Path $env:USERPROFILE -Parent
        foreach ($u in (Get-ChildItem -LiteralPath $usersRoot -Directory -Force -ErrorAction SilentlyContinue)) {
            $p = Join-Path $u.FullName 'AppData\Local'
            if ((Test-Path -LiteralPath $p) -and (-not $dirs.Contains($p))) { $dirs.Add($p) }
        }
    }
    return $dirs.ToArray()
}

function Get-BrowserCachePaths {
    param($LocalDirs)
    $list = New-Object System.Collections.Generic.List[string]
    $chromiumRoots = @(
        'Google\Chrome\User Data',
        'Microsoft\Edge\User Data',
        'BraveSoftware\Brave-Browser\User Data',
        'Chromium\User Data',
        'Vivaldi\User Data'
    )
    $operaRoots = @('Opera Software\Opera Stable', 'Opera Software\Opera GX Stable')
    foreach ($ld in $LocalDirs) {
        foreach ($r in $chromiumRoots) {
            $ud = Join-Path $ld $r
            if (-not (Test-Path -LiteralPath $ud)) { continue }
            foreach ($n in @('Cache', 'Code Cache', 'GPUCache', 'ShaderCache', 'GrShaderCache', 'DawnGraphiteCache', 'DawnWebGPUCache')) {
                $p = Join-Path $ud $n
                if (Test-Path -LiteralPath $p) { $list.Add($p) }
            }
            foreach ($prof in (Get-ChildItem -LiteralPath $ud -Directory -Force -ErrorAction SilentlyContinue)) {
                foreach ($n in @('Cache', 'Code Cache', 'GPUCache')) {
                    $p = Join-Path $prof.FullName $n
                    if (Test-Path -LiteralPath $p) { $list.Add($p) }
                }
                $sw = Join-Path $prof.FullName 'Service Worker\CacheStorage'
                if (Test-Path -LiteralPath $sw) { $list.Add($sw) }
            }
        }
        foreach ($r in $operaRoots) {
            $ud = Join-Path $ld $r
            if (-not (Test-Path -LiteralPath $ud)) { continue }
            foreach ($n in @('Cache', 'Code Cache', 'GPUCache')) {
                $p = Join-Path $ud $n
                if (Test-Path -LiteralPath $p) { $list.Add($p) }
            }
        }
        $ff = Join-Path $ld 'Mozilla\Firefox\Profiles'
        if (Test-Path -LiteralPath $ff) {
            foreach ($prof in (Get-ChildItem -LiteralPath $ff -Directory -Force -ErrorAction SilentlyContinue)) {
                foreach ($n in @('cache2', 'startupCache')) {
                    $p = Join-Path $prof.FullName $n
                    if (Test-Path -LiteralPath $p) { $list.Add($p) }
                }
            }
        }
    }
    return $list.ToArray()
}

function Get-ThumbnailCacheFiles {
    param($LocalDirs)
    $list = New-Object System.Collections.Generic.List[string]
    foreach ($ld in $LocalDirs) {
        $exp = Join-Path $ld 'Microsoft\Windows\Explorer'
        if (-not (Test-Path -LiteralPath $exp)) { continue }
        foreach ($f in (Get-ChildItem -LiteralPath $exp -Force -File -ErrorAction SilentlyContinue)) {
            if ($f.Name -like 'thumbcache_*.db' -or $f.Name -like 'iconcache_*.db') { $list.Add($f.FullName) }
        }
    }
    return $list.ToArray()
}

function Get-AppCachePaths {
    param($LocalDirs)
    $list = New-Object System.Collections.Generic.List[string]
    foreach ($ld in $LocalDirs) {
        $pk = Join-Path $ld 'Packages'
        if (-not (Test-Path -LiteralPath $pk)) { continue }
        foreach ($d in (Get-ChildItem -LiteralPath $pk -Directory -Force -ErrorAction SilentlyContinue)) {
            $lc = Join-Path $d.FullName 'LocalCache'
            if (Test-Path -LiteralPath $lc) { $list.Add($lc) }
        }
    }
    return $list.ToArray()
}

# 清理目标定义：Id / 名称 / 类别 / 作用域 / 解析器
# 作用域 sys = 需要管理员；user = 当前（管理员时含所有用户）；drive = 所选驱动器
$script:TargetDefs = @(
    [pscustomobject]@{ Id='sys_temp';   Name='系统临时文件';            Cat='系统缓存'; Scope='sys';   Resolve={ param($c) @("$env:SystemRoot\Temp") } }
    [pscustomobject]@{ Id='win_update'; Name='Windows 更新下载缓存';    Cat='系统缓存'; Scope='sys';   Resolve={ param($c) @("$env:SystemRoot\SoftwareDistribution\Download", "$env:SystemRoot\SoftwareDistribution\DeliveryOptimization") } }
    [pscustomobject]@{ Id='prefetch';   Name='预读取缓存 (Prefetch)';   Cat='系统缓存'; Scope='sys';   Resolve={ param($c) @("$env:SystemRoot\Prefetch") } }
    [pscustomobject]@{ Id='wer_sys';    Name='系统错误报告 (WER)';      Cat='系统缓存'; Scope='sys';   Resolve={ param($c) @("$env:ProgramData\Microsoft\Windows\WER") } }
    [pscustomobject]@{ Id='dumps';      Name='内存转储 (dmp/Minidump)'; Cat='系统缓存'; Scope='sys';   Resolve={ param($c) @("$env:SystemRoot\memory.dmp", "$env:SystemRoot\Minidump", "$env:SystemRoot\LiveKernelReports") } }
    [pscustomobject]@{ Id='user_temp';  Name='用户临时文件';            Cat='用户缓存'; Scope='user';  Resolve={ param($c) $c.LocalDirs | ForEach-Object { Join-Path $_ 'Temp' } } }
    [pscustomobject]@{ Id='browser';    Name='浏览器缓存';              Cat='用户缓存'; Scope='user';  Resolve={ param($c) Get-BrowserCachePaths $c.LocalDirs } }
    [pscustomobject]@{ Id='thumb';      Name='缩略图/图标缓存';         Cat='用户缓存'; Scope='user';  Resolve={ param($c) Get-ThumbnailCacheFiles $c.LocalDirs } }
    [pscustomobject]@{ Id='d3d';        Name='DirectX 着色器缓存';      Cat='用户缓存'; Scope='user';  Resolve={ param($c) $c.LocalDirs | ForEach-Object { Join-Path $_ 'D3DSCache' } } }
    [pscustomobject]@{ Id='crashdump';  Name='应用崩溃转储 (CrashDumps)'; Cat='用户缓存'; Scope='user'; Resolve={ param($c) $c.LocalDirs | ForEach-Object { Join-Path $_ 'CrashDumps' } } }
    [pscustomobject]@{ Id='wer_user';   Name='用户错误报告 (WER)';      Cat='用户缓存'; Scope='user';  Resolve={ param($c) $c.LocalDirs | ForEach-Object { Join-Path $_ 'Microsoft\Windows\WER' } } }
    [pscustomobject]@{ Id='appcache';   Name='应用缓存 (LocalCache)';   Cat='用户缓存'; Scope='user';  Resolve={ param($c) Get-AppCachePaths $c.LocalDirs } }
    [pscustomobject]@{ Id='drive_temp'; Name='各盘根目录 Temp';         Cat='驱动器';   Scope='drive'; Resolve={ param($c) $c.Drives | ForEach-Object { "$($_):\Temp" } } }
)

function New-CleanContext {
    $drives = Get-CheckedDrives
    return [pscustomobject]@{
        Drives    = $drives
        LocalDirs = Get-UserLocalDirs
    }
}

function Get-TargetPaths {
    param($Target, $Context)
    $raw = & $Target.Resolve $Context
    $res = New-Object System.Collections.Generic.List[string]
    foreach ($p in $raw) {
        if (-not $p) { continue }
        if (-not (Test-Path -LiteralPath $p)) { continue }
        $full = [System.IO.Path]::GetFullPath($p).TrimEnd('\')
        if (-not (Test-SafeTargetPath $full)) { continue }
        if (-not $res.Contains($full)) { $res.Add($full) }
    }
    return $res.ToArray()
}

function Get-CheckedDrives {
    $out = @()
    for ($i = 0; $i -lt $script:DriveListBox.Items.Count; $i++) {
        if ($script:DriveListBox.GetItemChecked($i)) {
            $name = $script:DriveInfoList[$i].Name
            $out += $name.Substring(0, 1)
        }
    }
    return $out
}

function Get-CheckedTargetItems {
    $out = New-Object System.Collections.Generic.List[object]
    foreach ($t in $script:TargetDefs) {
        $it = $script:TargetItems[$t.Id]
        if ($it -and $it.Checked) { $out.Add($t) }
    }
    return $out.ToArray()
}

# ------------------------------------------------------------
# 界面
# ------------------------------------------------------------
function Build-Ui {
    $uifont = $null
    try { $uifont = New-Object System.Drawing.Font('Microsoft YaHei UI', 9) } catch { $uifont = New-Object System.Drawing.Font('Segoe UI', 9) }

    $form = New-Object System.Windows.Forms.Form
    $form.Text = $script:AppTitle
    $form.ClientSize = New-Object System.Drawing.Size(920, 660)
    $form.StartPosition = 'CenterScreen'
    $form.FormBorderStyle = 'FixedSingle'
    $form.MaximizeBox = $false
    $form.Font = $uifont

    $lblTitle = New-Object System.Windows.Forms.Label
    $lblTitle.Text = $script:AppTitle
    $lblTitle.Location = New-Object System.Drawing.Point(16, 8)
    $lblTitle.Size = New-Object System.Drawing.Size(420, 30)
    try { $lblTitle.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 13, [System.Drawing.FontStyle]::Bold) } catch { }
    $lblTitle.ForeColor = [System.Drawing.Color]::FromArgb(30, 45, 70)

    $lblSub = New-Object System.Windows.Forms.Label
    $lblSub.Text = '只清理缓存与临时文件，不触碰系统文件和个人资料'
    $lblSub.Location = New-Object System.Drawing.Point(18, 38)
    $lblSub.Size = New-Object System.Drawing.Size(460, 18)
    $lblSub.ForeColor = [System.Drawing.Color]::FromArgb(105, 105, 105)

    $lblAdmin = New-Object System.Windows.Forms.Label
    $lblAdmin.Location = New-Object System.Drawing.Point(600, 14)
    $lblAdmin.Size = New-Object System.Drawing.Size(230, 22)
    $lblAdmin.TextAlign = 'MiddleRight'
    $lblAdmin.Text = '检测权限中…'
    $lblAdmin.ForeColor = [System.Drawing.Color]::FromArgb(105, 105, 105)

    $btnExit = New-Object System.Windows.Forms.Button
    $btnExit.Text = '退出'
    $btnExit.Location = New-Object System.Drawing.Point(852, 10)
    $btnExit.Size = New-Object System.Drawing.Size(56, 28)
    $btnExit.Add_Click({ $script:MainForm.Close() })

    # ---------- 驱动器 ----------
    $grpDrives = New-Object System.Windows.Forms.GroupBox
    $grpDrives.Text = '驱动器（影响回收站与各盘 Temp 的清理范围）'
    $grpDrives.Location = New-Object System.Drawing.Point(12, 66)
    $grpDrives.Size = New-Object System.Drawing.Size(588, 140)

    $lstDrives = New-Object System.Windows.Forms.CheckedListBox
    $lstDrives.Location = New-Object System.Drawing.Point(12, 22)
    $lstDrives.Size = New-Object System.Drawing.Size(564, 104)
    $lstDrives.CheckOnClick = $true
    $lstDrives.IntegralHeight = $false

    $script:DriveInfoList = @()
    $script:DriveListBox = $lstDrives
    foreach ($d in [System.IO.DriveInfo]::GetDrives()) {
        if (-not $d.IsReady) { continue }
        if ($d.DriveType -ne 'Fixed' -and $d.DriveType -ne 'Removable') { continue }
        $script:DriveInfoList += $d
        $freeGB = [math]::Round($d.AvailableFreeSpace / 1GB, 1)
        $totGB  = [math]::Round($d.TotalSize / 1GB, 1)
        $tag = ''
        if ($d.Name.ToUpper().StartsWith($env:SystemDrive.ToUpper())) { $tag = '  [系统盘]' }
        $text = ('{0}  剩余 {1} GB / 共 {2} GB{3}' -f $d.Name, $freeGB, $totGB, $tag)
        $checked = ($d.DriveType -eq 'Fixed')
        [void]$lstDrives.Items.Add($text, $checked)
    }
    $grpDrives.Controls.Add($lstDrives)

    # ---------- 清理项目 ----------
    $grpTargets = New-Object System.Windows.Forms.GroupBox
    $grpTargets.Text = '清理项目（勾选后点击右侧“开始清理”）'
    $grpTargets.Location = New-Object System.Drawing.Point(12, 214)
    $grpTargets.Size = New-Object System.Drawing.Size(588, 268)

    $lv = New-Object System.Windows.Forms.ListView
    $lv.Location = New-Object System.Drawing.Point(12, 22)
    $lv.Size = New-Object System.Drawing.Size(564, 202)
    $lv.View = 'Details'
    $lv.CheckBoxes = $true
    $lv.FullRowSelect = $true
    $lv.HeaderStyle = 'Nonclickable'
    $lv.GridLines = $false
    [void]$lv.Columns.Add('清理项目', 310)
    [void]$lv.Columns.Add('类别', 70)
    [void]$lv.Columns.Add('预计大小', 100)

    $gSys   = New-Object System.Windows.Forms.ListViewGroup('系统缓存')
    $gUser  = New-Object System.Windows.Forms.ListViewGroup('用户缓存')
    $gDrive = New-Object System.Windows.Forms.ListViewGroup('驱动器')
    [void]$lv.Groups.Add($gSys)
    [void]$lv.Groups.Add($gUser)
    [void]$lv.Groups.Add($gDrive)

    $script:TargetItems = @{}
    foreach ($t in $script:TargetDefs) {
        $it = New-Object System.Windows.Forms.ListViewItem($t.Name)
        [void]$it.SubItems.Add($t.Cat)
        [void]$it.SubItems.Add('未扫描')
        $it.Tag = $t.Id
        switch ($t.Cat) {
            '系统缓存' { $it.Group = $gSys }
            '用户缓存' { $it.Group = $gUser }
            '驱动器'   { $it.Group = $gDrive }
        }
        [void]$lv.Items.Add($it)
        $it.Checked = $true
        $script:TargetItems[$t.Id] = $it
    }

    $btnScan = New-Object System.Windows.Forms.Button
    $btnScan.Text = '扫描大小'
    $btnScan.Location = New-Object System.Drawing.Point(12, 232)
    $btnScan.Size = New-Object System.Drawing.Size(110, 28)

    $btnAll = New-Object System.Windows.Forms.Button
    $btnAll.Text = '全选'
    $btnAll.Location = New-Object System.Drawing.Point(128, 232)
    $btnAll.Size = New-Object System.Drawing.Size(86, 28)

    $btnNone = New-Object System.Windows.Forms.Button
    $btnNone.Text = '全不选'
    $btnNone.Location = New-Object System.Drawing.Point(220, 232)
    $btnNone.Size = New-Object System.Drawing.Size(86, 28)

    $grpTargets.Controls.Add($lv)
    $grpTargets.Controls.Add($btnScan)
    $grpTargets.Controls.Add($btnAll)
    $grpTargets.Controls.Add($btnNone)

    # ---------- 右侧：操作 ----------
    $grpOps = New-Object System.Windows.Forms.GroupBox
    $grpOps.Text = '操作'
    $grpOps.Location = New-Object System.Drawing.Point(612, 66)
    $grpOps.Size = New-Object System.Drawing.Size(296, 124)

    $btnStart = New-Object System.Windows.Forms.Button
    $btnStart.Text = '开始清理'
    $btnStart.Location = New-Object System.Drawing.Point(14, 20)
    $btnStart.Size = New-Object System.Drawing.Size(268, 38)
    $btnStart.BackColor = [System.Drawing.Color]::FromArgb(46, 125, 80)
    $btnStart.ForeColor = [System.Drawing.Color]::White
    $btnStart.FlatStyle = 'Flat'
    try { $btnStart.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 10, [System.Drawing.FontStyle]::Bold) } catch { }

    $btnStop = New-Object System.Windows.Forms.Button
    $btnStop.Text = '停止'
    $btnStop.Location = New-Object System.Drawing.Point(14, 66)
    $btnStop.Size = New-Object System.Drawing.Size(128, 28)
    $btnStop.Enabled = $false

    $btnOpenLog = New-Object System.Windows.Forms.Button
    $btnOpenLog.Text = '打开日志'
    $btnOpenLog.Location = New-Object System.Drawing.Point(152, 66)
    $btnOpenLog.Size = New-Object System.Drawing.Size(130, 28)

    $grpOps.Controls.Add($btnStart)
    $grpOps.Controls.Add($btnStop)
    $grpOps.Controls.Add($btnOpenLog)

    # ---------- 右侧：危险操作 ----------
    $grpDanger = New-Object System.Windows.Forms.GroupBox
    $grpDanger.Text = '危险操作（单独确认）'
    $grpDanger.Location = New-Object System.Drawing.Point(612, 198)
    $grpDanger.Size = New-Object System.Drawing.Size(296, 124)

    $btnBin = New-Object System.Windows.Forms.Button
    $btnBin.Text = '清空回收站（不可恢复）'
    $btnBin.Location = New-Object System.Drawing.Point(14, 22)
    $btnBin.Size = New-Object System.Drawing.Size(268, 34)
    $btnBin.BackColor = [System.Drawing.Color]::FromArgb(190, 55, 55)
    $btnBin.ForeColor = [System.Drawing.Color]::White
    $btnBin.FlatStyle = 'Flat'

    $lblBinHint = New-Object System.Windows.Forms.Label
    $lblBinHint.Text = '仅清空所选驱动器的回收站内容，删除后无法恢复。'
    $lblBinHint.Location = New-Object System.Drawing.Point(14, 62)
    $lblBinHint.Size = New-Object System.Drawing.Size(268, 40)
    $lblBinHint.ForeColor = [System.Drawing.Color]::FromArgb(130, 130, 130)

    $grpDanger.Controls.Add($btnBin)
    $grpDanger.Controls.Add($lblBinHint)

    # ---------- 右侧：状态 ----------
    $grpStatus = New-Object System.Windows.Forms.GroupBox
    $grpStatus.Text = '状态'
    $grpStatus.Location = New-Object System.Drawing.Point(612, 330)
    $grpStatus.Size = New-Object System.Drawing.Size(296, 64)

    $prg = New-Object System.Windows.Forms.ProgressBar
    $prg.Location = New-Object System.Drawing.Point(14, 22)
    $prg.Size = New-Object System.Drawing.Size(268, 16)
    $prg.Minimum = 0
    $prg.Maximum = 100

    $lblStatus = New-Object System.Windows.Forms.Label
    $lblStatus.Text = '就绪'
    $lblStatus.Location = New-Object System.Drawing.Point(14, 42)
    $lblStatus.Size = New-Object System.Drawing.Size(268, 18)
    $lblStatus.ForeColor = [System.Drawing.Color]::FromArgb(80, 80, 80)

    $grpStatus.Controls.Add($prg)
    $grpStatus.Controls.Add($lblStatus)

    # ---------- 右侧：提示 ----------
    $lblTips = New-Object System.Windows.Forms.Label
    $lblTips.Location = New-Object System.Drawing.Point(612, 404)
    $lblTips.Size = New-Object System.Drawing.Size(296, 78)
    $lblTips.ForeColor = [System.Drawing.Color]::FromArgb(105, 105, 105)
    $lblTips.Text = "提示：`r`n· 清理前请关闭浏览器，否则部分缓存会被占用`r`n· 回收站需勾选确认后才能清空`r`n· 日志自动保存到程序目录"

    # ---------- 日志 ----------
    $grpLog = New-Object System.Windows.Forms.GroupBox
    $grpLog.Text = '运行日志（同步写入程序目录 清理日志.txt）'
    $grpLog.Location = New-Object System.Drawing.Point(12, 494)
    $grpLog.Size = New-Object System.Drawing.Size(896, 154)

    $rtb = New-Object System.Windows.Forms.RichTextBox
    $rtb.Location = New-Object System.Drawing.Point(12, 20)
    $rtb.Size = New-Object System.Drawing.Size(872, 124)
    $rtb.ReadOnly = $true
    $rtb.BackColor = [System.Drawing.Color]::FromArgb(250, 250, 250)
    $rtb.BorderStyle = 'FixedSingle'
    $rtb.WordWrap = $false
    $rtb.ScrollBars = 'Both'
    try { $rtb.Font = New-Object System.Drawing.Font('Consolas', 9) } catch { }
    $grpLog.Controls.Add($rtb)

    # ---------- 组装 ----------
    foreach ($c in @($lblTitle, $lblSub, $lblAdmin, $btnExit, $grpDrives, $grpTargets, $grpOps, $grpDanger, $grpStatus, $lblTips, $grpLog)) {
        $form.Controls.Add($c)
    }

    # 逻辑占位控件（按钮事件）
    $script:BtnStart   = $btnStart
    $script:BtnStop    = $btnStop
    $script:BtnScan    = $btnScan
    $script:BtnBin     = $btnBin
    $script:BtnAll     = $btnAll
    $script:BtnNone    = $btnNone
    $script:BtnOpenLog = $btnOpenLog
    $script:PrgBar     = $prg
    $script:LblStatus  = $lblStatus
    $script:LblAdmin   = $lblAdmin
    $script:LogBox     = $rtb
    $script:MainForm   = $form

    # ---------- 事件 ----------
    $btnScan.Add_Click({ if (-not $script:Busy) { Invoke-ScanUi } })
    $btnStart.Add_Click({ if (-not $script:Busy) { Invoke-CleanUi } })
    $btnStop.Add_Click({ if ($script:Busy) { $script:StopRequested = $true; Write-UiLog '收到停止请求，等待当前小步骤完成…' 'WARN' } })
    $btnAll.Add_Click({ foreach ($t in $script:TargetDefs) { $it = $script:TargetItems[$t.Id]; if ($it) { $it.Checked = $true } } })
    $btnNone.Add_Click({ foreach ($t in $script:TargetDefs) { $it = $script:TargetItems[$t.Id]; if ($it) { $it.Checked = $false } } })
    $btnBin.Add_Click({ if (-not $script:Busy) { Invoke-ClearRecycleBinUi } })
    $btnOpenLog.Add_Click({
        try { Start-Process -FilePath 'notepad.exe' -ArgumentList ('"' + $script:LogFile + '"') | Out-Null } catch { }
    })
    $form.Add_FormClosing({
        param($sender, $e)
        if ($script:Busy) {
            $e.Cancel = $true
            [void][System.Windows.Forms.MessageBox]::Show('正在执行清理，请先点击“停止”并等待完成后关闭。', '提示', 'OK', 'Warning')
        }
    })

    return $form
}

# ------------------------------------------------------------
# 扫描
# ------------------------------------------------------------
function Invoke-ScanUi {
    $script:Busy = $true
    $script:StopRequested = $false
    Set-BusyUi
    try {
        $ctx = New-CleanContext
        $items = Get-CheckedTargetItems
        if ($items.Count -eq 0) { Write-UiLog '未勾选任何清理项目' 'WARN'; return }
        $n = 0
        $script:PrgBar.Value = 0
        foreach ($t in $items) {
            if ($script:StopRequested) { break }
            $n++
            Set-StatusText ('扫描中 {0}/{1}：{2}' -f $n, $items.Count, $t.Name)
            Update-Ui
            $paths = Get-TargetPaths $t $ctx
            $bytes = [long]0
            foreach ($p in $paths) {
                $bytes += Get-PathSizeBytes $p
                Update-Ui
            }
            $it = $script:TargetItems[$t.Id]
            if ($it) { $it.SubItems[2].Text = Format-Size $bytes }
            Write-UiLog ('扫描 {0}：{1}（{2} 个位置）' -f $t.Name, (Format-Size $bytes), $paths.Count)
            $script:PrgBar.Value = [int](100 * $n / $items.Count)
            Update-Ui
        }
        Set-StatusText '扫描完成'
    } catch {
        Write-UiLog ('扫描出错：' + $_.Exception.Message) 'ERR'
    } finally {
        $script:Busy = $false
        Set-BusyUi
    }
}

# ------------------------------------------------------------
# 清理
# ------------------------------------------------------------
function Invoke-CleanUi {
    $items = Get-CheckedTargetItems
    if ($items.Count -eq 0) { Write-UiLog '请先勾选要清理的项目' 'WARN'; return }
    if (-not $script:IsAdmin) {
        Write-UiLog '当前为受限模式：系统缓存等项目将被跳过，建议以管理员身份重新运行。' 'WARN'
    }
    $script:Busy = $true
    $script:StopRequested = $false
    Set-BusyUi
    try {
        $ctx = New-CleanContext
        if ($ctx.Drives.Count -eq 0) { Write-UiLog '提示：未勾选任何驱动器，各盘 Temp 项目将跳过' 'WARN' }
        $total = [long]0
        $done = 0
        $script:PrgBar.Value = 0
        Write-UiLog ('开始清理，共 {0} 个项目' -f $items.Count)
        foreach ($t in $items) {
            if ($script:StopRequested) { Write-UiLog '已停止清理' 'WARN'; break }
            if ($t.Scope -eq 'sys' -and -not $script:IsAdmin) {
                Write-UiLog ('跳过 {0}：需要管理员权限' -f $t.Name) 'WARN'
                $done++; $script:PrgBar.Value = [int](100 * $done / $items.Count)
                continue
            }
            $paths = Get-TargetPaths $t $ctx
            if ($paths.Count -eq 0) {
                Write-UiLog ('{0}：无内容，跳过' -f $t.Name)
                $done++; $script:PrgBar.Value = [int](100 * $done / $items.Count)
                Update-Ui
                continue
            }
            $before = [long]0
            foreach ($p in $paths) { $before += Get-PathSizeBytes $p }
            Write-UiLog ('开始清理 {0}（{1}，{2} 个位置）' -f $t.Name, (Format-Size $before), $paths.Count)
            Set-StatusText ('正在清理：' + $t.Name)
            Update-Ui
            foreach ($p in $paths) {
                if ($script:StopRequested) { break }
                Clear-PathContent $p
                Update-Ui
            }
            $after = [long]0
            foreach ($p in $paths) { $after += Get-PathSizeBytes $p }
            $freed = $before - $after
            if ($freed -lt 0) { $freed = 0 }
            $total += $freed
            $it = $script:TargetItems[$t.Id]
            if ($it) { $it.SubItems[2].Text = ('已清 ' + (Format-Size $freed)) }
            Write-UiLog ('完成 {0}：释放 {1}' -f $t.Name, (Format-Size $freed)) 'OK'
            $done++
            $script:PrgBar.Value = [int](100 * $done / $items.Count)
            Update-Ui
        }
        Write-UiLog ('清理结束，本次共释放 {0}' -f (Format-Size $total)) 'OK'
        Set-StatusText ('完成，共释放 ' + (Format-Size $total))
        Refresh-DriveTexts
    } catch {
        Write-UiLog ('清理出错：' + $_.Exception.Message) 'ERR'
    } finally {
        $script:Busy = $false
        Set-BusyUi
    }
}

function Refresh-DriveTexts {
    for ($i = 0; $i -lt $script:DriveListBox.Items.Count; $i++) {
        try { $script:DriveInfoList[$i].Refresh() } catch { }
        $d = $script:DriveInfoList[$i]
        $checked = $script:DriveListBox.GetItemChecked($i)
        $freeGB = [math]::Round($d.AvailableFreeSpace / 1GB, 1)
        $totGB  = [math]::Round($d.TotalSize / 1GB, 1)
        $tag = ''
        if ($d.Name.ToUpper().StartsWith($env:SystemDrive.ToUpper())) { $tag = '  [系统盘]' }
        $script:DriveListBox.Items[$i] = ('{0}  剩余 {1} GB / 共 {2} GB{3}' -f $d.Name, $freeGB, $totGB, $tag)
        $script:DriveListBox.SetItemChecked($i, $checked)
    }
}

# ------------------------------------------------------------
# 回收站（独立确认）
# ------------------------------------------------------------
function Get-RecycleBinStats {
    param([string[]]$Drives)
    $stats = @()
    foreach ($d in $Drives) {
        $root = ('{0}:\$Recycle.Bin' -f $d)
        $cnt = 0
        $sz = [long]0
        if (Test-Path -LiteralPath $root) {
            foreach ($f in (Get-ChildItem -LiteralPath $root -Force -Recurse -ErrorAction SilentlyContinue)) {
                if ($f.PSIsContainer) { continue }
                $cnt++
                try { $sz += [long]$f.Length } catch { }
            }
        }
        $stats += [pscustomobject]@{ Drive = $d; Count = $cnt; Bytes = $sz }
    }
    return $stats
}

function Show-RecycleConfirm {
    param($Stats)
    $dlg = New-Object System.Windows.Forms.Form
    $dlg.Text = '确认清空回收站'
    $dlg.ClientSize = New-Object System.Drawing.Size(500, 340)
    $dlg.StartPosition = 'CenterParent'
    $dlg.FormBorderStyle = 'FixedDialog'
    $dlg.MaximizeBox = $false
    $dlg.MinimizeBox = $false
    try { $dlg.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 9) } catch { }

    $lbl = New-Object System.Windows.Forms.Label
    $lbl.Text = '以下驱动器的回收站将被清空：'
    $lbl.Location = New-Object System.Drawing.Point(16, 14)
    $lbl.Size = New-Object System.Drawing.Size(460, 20)

    $txt = New-Object System.Windows.Forms.TextBox
    $txt.Multiline = $true
    $txt.ReadOnly = $true
    $txt.ScrollBars = 'Vertical'
    $txt.Location = New-Object System.Drawing.Point(16, 40)
    $txt.Size = New-Object System.Drawing.Size(468, 140)
    $lines = @()
    foreach ($s in $Stats) {
        $lines += ('{0}    约 {1:N0} 个文件，{2}' -f $s.Drive, $s.Count, (Format-Size $s.Bytes))
    }
    $txt.Text = ($lines -join "`r`n")

    $lblWarn = New-Object System.Windows.Forms.Label
    $lblWarn.Text = '注意：删除后无法通过回收站恢复。管理员模式下会清除所选驱动器上所有用户的回收站内容。'
    $lblWarn.ForeColor = [System.Drawing.Color]::FromArgb(170, 40, 40)
    $lblWarn.Location = New-Object System.Drawing.Point(16, 190)
    $lblWarn.Size = New-Object System.Drawing.Size(468, 44)

    $chk = New-Object System.Windows.Forms.CheckBox
    $chk.Text = '我明白此操作不可恢复'
    $chk.Location = New-Object System.Drawing.Point(16, 242)
    $chk.Size = New-Object System.Drawing.Size(300, 24)

    $btnOk = New-Object System.Windows.Forms.Button
    $btnOk.Text = '清空回收站'
    $btnOk.Location = New-Object System.Drawing.Point(250, 288)
    $btnOk.Size = New-Object System.Drawing.Size(120, 32)
    $btnOk.Enabled = $false
    $btnOk.BackColor = [System.Drawing.Color]::FromArgb(190, 55, 55)
    $btnOk.ForeColor = [System.Drawing.Color]::White
    $btnOk.FlatStyle = 'Flat'

    $btnCancel = New-Object System.Windows.Forms.Button
    $btnCancel.Text = '取消'
    $btnCancel.Location = New-Object System.Drawing.Point(380, 288)
    $btnCancel.Size = New-Object System.Drawing.Size(100, 32)

    $chk.Add_CheckedChanged({ $btnOk.Enabled = $chk.Checked })
    $btnOk.DialogResult = 'OK'
    $btnCancel.DialogResult = 'Cancel'
    $dlg.AcceptButton = $btnCancel
    $dlg.CancelButton = $btnCancel

    foreach ($c in @($lbl, $txt, $lblWarn, $chk, $btnOk, $btnCancel)) { $dlg.Controls.Add($c) }

    $r = $dlg.ShowDialog($script:MainForm)
    $dlg.Dispose()
    return ($r -eq [System.Windows.Forms.DialogResult]::OK)
}

function Invoke-ClearRecycleBinUi {
    $drives = Get-CheckedDrives
    if ($drives.Count -eq 0) { Write-UiLog '请先在驱动器列表中勾选要清空的回收站' 'WARN'; return }
    $script:Busy = $true
    Set-BusyUi
    try {
        Set-StatusText '正在统计回收站内容…'
        Update-Ui
        $stats = Get-RecycleBinStats $drives
        $script:Busy = $false
        Set-BusyUi

        $totalCount = 0
        foreach ($s in $stats) { $totalCount += $s.Count }
        if ($totalCount -eq 0) {
            Write-UiLog ('所选驱动器回收站为空：' + ($drives -join ', '))
            return
        }

        if (-not (Show-RecycleConfirm $stats)) {
            Write-UiLog '已取消清空回收站'
            return
        }

        $script:Busy = $true
        Set-BusyUi
        Write-UiLog ('开始清空回收站：' + ($drives -join ', '))
        foreach ($s in $stats) {
            $freed = $s.Bytes
            if (-not (Get-Command Clear-RecycleBin -ErrorAction SilentlyContinue)) {
                if ($script:IsAdmin) {
                    Get-ChildItem -LiteralPath ('{0}:\$Recycle.Bin' -f $s.Drive) -Force -ErrorAction SilentlyContinue | ForEach-Object {
                        Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
                    }
                }
            } else {
                try { Clear-RecycleBin -DriveLetter $s.Drive -Force -ErrorAction Stop } catch { }
            }
            Write-UiLog ('已清空 {0}: 回收站（约释放 {1}）' -f $s.Drive, (Format-Size $freed)) 'OK'
            Update-Ui
        }
        Set-StatusText '回收站已清空'
        Refresh-DriveTexts
    } catch {
        Write-UiLog ('清空回收站出错：' + $_.Exception.Message) 'ERR'
    } finally {
        $script:Busy = $false
        Set-BusyUi
    }
}

# ------------------------------------------------------------
# 自检（用于开发验证，不弹窗）
# ------------------------------------------------------------
function Invoke-SelfTest {
    $lines = New-Object System.Collections.Generic.List[string]
    $add = { param($s) $lines.Add($s) }
    $script:STPass = 0
    $script:STFail = 0
    $check = {
        param([string]$name, [bool]$ok, [string]$detail)
        if ($ok) { $script:STPass++ } else { $script:STFail++ }
        $msg = '[FAIL] ' + $name
        if ($ok) { $msg = '[PASS] ' + $name }
        if ($detail) { $msg = $msg + '  ' + $detail }
        & $add $msg
    }

    & $add ('PSVersion = ' + $PSVersionTable.PSVersion.ToString())
    & $add ('IsAdmin   = ' + $script:IsAdmin)

    # 1. 目标定义完整
    & $check 'target-count' ($script:TargetDefs.Count -eq 13) ('actual=' + $script:TargetDefs.Count)

    # 2. 解析器可用
    $ctx = [pscustomobject]@{ Drives = @('C'); LocalDirs = @($env:LOCALAPPDATA) }
    $okAll = $true
    $i = 0
    foreach ($t in $script:TargetDefs) {
        $i++
        try { $null = Get-TargetPaths $t $ctx } catch { $okAll = $false; & $add ('  resolver#' + $i + ' fail: ' + $_.Exception.Message) }
    }
    & $check 'resolvers' $okAll

    # 3. UI 构建（不显示）
    $formOk = $false
    try {
        $f = Build-Ui
        if ($f -ne $null) { $formOk = ($f.Controls.Count -gt 5) }
        try { $f.Dispose() } catch { }
    } catch { & $add ('  Build-Ui error: ' + $_.Exception.Message) }
    & $check 'ui-build' $formOk ('items=' + $script:TargetItems.Count)

    # 4. 体积统计与删除（沙箱，位于临时目录）
    $sb = Join-Path $env:TEMP 'cdcleaner_selftest'
    try {
        Remove-Item -LiteralPath $sb -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force -Path (Join-Path $sb 'a\cache') | Out-Null
        New-Item -ItemType Directory -Force -Path (Join-Path $sb 'b') | Out-Null
        New-Item -ItemType Directory -Force -Path (Join-Path $sb 'Documents') | Out-Null
        [System.IO.File]::WriteAllBytes((Join-Path $sb 'a\cache\f1.bin'), (New-Object byte[] 1048576))
        [System.IO.File]::WriteAllBytes((Join-Path $sb 'b\f2.bin'), (New-Object byte[] 524288))
    } catch { & $add ('  sandbox error: ' + $_.Exception.Message) }

    $sz1 = Get-PathSizeBytes (Join-Path $sb 'a\cache')
    & $check 'size-1MB' ($sz1 -eq 1048576) ('actual=' + $sz1)

    Clear-PathContent (Join-Path $sb 'a\cache')
    $sz2 = Get-PathSizeBytes (Join-Path $sb 'a\cache')
    & $check 'clear-dir' ($sz2 -eq 0) ('actual=' + $sz2)
    & $check 'dir-recreated' (Test-Path -LiteralPath (Join-Path $sb 'a\cache') -PathType Container)

    Clear-PathContent (Join-Path $sb 'b\f2.bin')
    & $check 'clear-file' (-not (Test-Path -LiteralPath (Join-Path $sb 'b\f2.bin')))

    # 5. 安全护栏
    $docPath = Join-Path $sb 'Documents'
    Clear-PathContent $docPath
    & $check 'guard-documents' (Test-Path -LiteralPath $docPath -PathType Container)

    & $check 'guard-drive-root' (-not (Test-SafeTargetPath 'E:\'))

    # 6. 回收站命令可用
    & $check 'clear-recyclebin-cmd' (([bool](Get-Command Clear-RecycleBin -ErrorAction SilentlyContinue)) -or $script:IsAdmin)

    & $add ''
    & $add ('SELFTEST_RESULT: ' + $script:STPass + ' passed, ' + $script:STFail + ' failed')

    try {
        $outFile = Join-Path $env:TEMP 'cdcleaner_selftest_result.txt'
        $text = ($lines -join "`r`n") + "`r`n"
        [System.IO.File]::WriteAllText($outFile, $text, [System.Text.Encoding]::UTF8)
    } catch { }
    $lines | ForEach-Object { Write-Host $_ }
}

# ------------------------------------------------------------
# 入口
# ------------------------------------------------------------
function Main {
    $script:IsAdmin = Test-AdminRight

    # 自提权：非管理员时自动请求管理员权限
    if (-not $script:IsAdmin -and -not $Elevated -and -not $SelfTest) {
        try {
            $argLine = '-NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $PSCommandPath + '" -Elevated'
            Start-Process -FilePath 'powershell.exe' -ArgumentList $argLine -Verb RunAs -ErrorAction Stop | Out-Null
            exit
        } catch {
            # 用户取消 UAC：继续以受限模式运行
        }
    }
    $script:IsAdmin = Test-AdminRight

    $script:MainForm = Build-Ui
    $script:MainForm.Add_Shown({
        $script:LblAdmin.Text = '✔ 管理员模式'
        $script:LblAdmin.ForeColor = [System.Drawing.Color]::FromArgb(20, 115, 60)
        if (-not $script:IsAdmin) {
            $script:LblAdmin.Text = '受限模式（部分项目需管理员）'
            $script:LblAdmin.ForeColor = [System.Drawing.Color]::FromArgb(170, 105, 0)
        }
        Write-UiLog ('=== C盘清理助手 v' + $script:AppVersion + ' ===')
        Write-UiLog ('PowerShell ' + $PSVersionTable.PSVersion.ToString())
        Write-UiLog ('日志文件：' + $script:LogFile)
        if ($script:IsAdmin) { Write-UiLog '已获得管理员权限，系统缓存与回收站可完整清理。' 'OK' }
        else { Write-UiLog '未获得管理员权限：系统临时文件、更新缓存、错误报告等项目会被跳过。' 'WARN' }
        Write-UiLog '点击“扫描大小”可预览体积；点击“开始清理”执行。'
    })
    [void]$script:MainForm.ShowDialog()
}

# 启动
if ($SelfTest) {
    $script:IsAdmin = Test-AdminRight
    Invoke-SelfTest
    exit 0
}
try {
    Main
} catch {
    try {
        [void][System.Windows.Forms.MessageBox]::Show(('启动失败：' + $_.Exception.Message), 'C盘清理助手', 'OK', 'Error')
    } catch { }
    try { Add-Content -LiteralPath $script:LogFile -Value ('[FATAL] ' + $_.Exception.Message) -Encoding UTF8 -ErrorAction SilentlyContinue } catch { }
    exit 1
}
