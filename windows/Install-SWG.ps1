# Star Wars Galaxies (SWGEmu client) installer for Windows.
# Gets the client files listed in SWGEmu's required2.txt (offline "client-files" folder next to the installer first,
# then the LAN mirror if set, then SWGEmu's own patch server), checks every MD5, points the game at your server and makes Desktop/Start menu shortcuts.
# Nothing is bundled here. Re-running it on an existing install only fetches missing or damaged files ("repair").
# Start it with "Install Star Wars Galaxies.bat" (double-click).
param([string]$Dest = "", [string]$Server = "", [string]$Mirror = "", [switch]$Quiet)
$StatusHost = ""

$ErrorActionPreference = "Stop"
$SwgEmu = "http://www.launchpad2.net/SWGEmu"
$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
foreach ($conf in @("$Here\swg-installer.conf", "$Here\..\swg-installer.conf")) {
    if (Test-Path $conf) {
        foreach ($line in Get-Content $conf) {
            if ($line -match '^\s*SERVER=(.*)$' -and -not $Server) { $Server = $Matches[1].Trim('"', ' ') }
            if ($line -match '^\s*MIRROR=(.*)$' -and -not $Mirror) { $Mirror = $Matches[1].Trim('"', ' ') }
            if ($line -match '^\s*STATUS=(.*)$') { $StatusHost = $Matches[1].Trim('"', ' ') }
        }
        break
    }
}
if (-not $Dest) { $Dest = "C:\Games\SWG" }

Add-Type -AssemblyName System.Windows.Forms, System.Drawing, System.Net.Http
[System.Windows.Forms.Application]::EnableVisualStyles()
$http = New-Object System.Net.Http.HttpClient
$http.Timeout = [TimeSpan]::FromHours(1)

# ---------- the period look (banner + colours) and the game launcher, compiled on this PC from launcher\*.cs ----------
# (C# that ships with Windows; the launcher reads its pictures and icon out of the game's own files - none are shipped)
$LauncherDir = @("$Here\launcher", "$Here\..\windows\launcher") | Where-Object { Test-Path "$_\RetroArt.cs" } | Select-Object -First 1
$LauncherSrc = @(); $Retro = $false
if ($LauncherDir) {
    $LauncherSrc = @(Get-ChildItem $LauncherDir -Filter *.cs | ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) })
    try {
        Add-Type -TypeDefinition ([System.IO.File]::ReadAllText("$LauncherDir\RetroArt.cs")) -ReferencedAssemblies System.Windows.Forms, System.Drawing
        $Retro = $true
    } catch { }
}
function Set-Look($banner, $bannerH, $bg, $fg, $accent, $box) {
    foreach ($c in @($form.Controls)) {
        $c.Top += $bannerH
        if ($c -is [System.Windows.Forms.TextBox]) { $c.BackColor = $box; $c.ForeColor = $fg; $c.BorderStyle = "FixedSingle" }
        elseif ($c -is [System.Windows.Forms.Button]) {
            $c.FlatStyle = "Flat"; $c.BackColor = $box; $c.ForeColor = $accent; $c.FlatAppearance.BorderColor = $accent
        }
        elseif ($c -is [System.Windows.Forms.ProgressBar]) { }
        else { $c.ForeColor = $fg; $c.BackColor = $bg }
    }
    $form.BackColor = $bg; $form.Height += $bannerH
    $banner.SetBounds(0, 0, $form.ClientSize.Width, $bannerH); $form.Controls.Add($banner)
}
# Builds "<dest>\<exeName>" and lets it read its art; returns the exe, or $null (shortcuts then start the game directly)
function Build-Launcher($dest, $exeName, $conf) {
    if (-not $LauncherSrc.Count) { return $null }
    $exe = Join-Path $dest $exeName
    try {
        $cp = New-Object System.CodeDom.Compiler.CompilerParameters
        $cp.GenerateExecutable = $true; $cp.OutputAssembly = $exe; $cp.CompilerOptions = "/target:winexe /optimize"
        [void]$cp.ReferencedAssemblies.AddRange([string[]]@("System.dll", "System.Drawing.dll", "System.Windows.Forms.dll"))
        $r = (New-Object Microsoft.CSharp.CSharpCodeProvider).CompileAssemblyFromSource($cp, [string[]]$LauncherSrc)
        if ($r.Errors.HasErrors) { return $null }
        [System.IO.File]::WriteAllText((Join-Path $dest "launcher.conf"), $conf)
        $p = Start-Process -FilePath $exe -ArgumentList "--extract" -WorkingDirectory $dest -PassThru
        while (-not $p.HasExited) { Start-Sleep -Milliseconds 200; [System.Windows.Forms.Application]::DoEvents() }
    } catch { return $null }
    if (Test-Path (Join-Path $dest "launcher-art\icon.ico")) { return $exe }
    return $null
}

# ---------- window ----------
$form = New-Object System.Windows.Forms.Form
$form.Text = "Star Wars Galaxies installer"; $form.Size = New-Object System.Drawing.Size(560, 360)
$form.StartPosition = "CenterScreen"; $form.FormBorderStyle = "FixedDialog"; $form.MaximizeBox = $false
function Add-Label($text, $y) {
    $l = New-Object System.Windows.Forms.Label; $l.Text = $text; $l.Location = New-Object System.Drawing.Point(16, $y)
    $l.AutoSize = $true; $form.Controls.Add($l)
}
function Add-Box($text, $y, $w) {
    $b = New-Object System.Windows.Forms.TextBox; $b.Text = $text; $b.Location = New-Object System.Drawing.Point(16, $y)
    $b.Width = $w; $form.Controls.Add($b); $b
}
Add-Label "Install folder (about 4 GB):" 14
$destBox = Add-Box $Dest 34 420
$browse = New-Object System.Windows.Forms.Button; $browse.Text = "Browse..."; $browse.Location = New-Object System.Drawing.Point(444, 32)
$form.Controls.Add($browse)
$browse.Add_Click({
    $d = New-Object System.Windows.Forms.FolderBrowserDialog; $d.Description = "Choose a folder - SWG goes into an 'SWG' folder inside it"
    if ($d.ShowDialog() -eq "OK") { $destBox.Text = Join-Path $d.SelectedPath "SWG" }
})
Add-Label "Server address:" 66
$serverBox = Add-Box $Server 86 510
Add-Label "LAN mirror (optional, faster at home):" 118
$mirrorBox = Add-Box $Mirror 138 510
$desk = New-Object System.Windows.Forms.CheckBox; $desk.Text = "Desktop shortcut"; $desk.Checked = $true
$desk.Location = New-Object System.Drawing.Point(16, 170); $desk.AutoSize = $true; $form.Controls.Add($desk)
$bar = New-Object System.Windows.Forms.ProgressBar; $bar.Location = New-Object System.Drawing.Point(16, 204); $bar.Width = 510
$form.Controls.Add($bar)
$status = New-Object System.Windows.Forms.Label; $status.Location = New-Object System.Drawing.Point(16, 234)
$status.Size = New-Object System.Drawing.Size(510, 40); $form.Controls.Add($status)
$go = New-Object System.Windows.Forms.Button; $go.Text = "Install"; $go.Location = New-Object System.Drawing.Point(370, 282)
$go.Size = New-Object System.Drawing.Size(75, 28); $form.Controls.Add($go)
$quit = New-Object System.Windows.Forms.Button; $quit.Text = "Close"; $quit.Location = New-Object System.Drawing.Point(451, 282)
$quit.Size = New-Object System.Drawing.Size(75, 28); $form.Controls.Add($quit); $quit.Add_Click({ $form.Close() })
if ($Retro) {   # starfield + the SWG logo from the offline client files (when they're next to the installer)
    $banner = New-Object Retro.Starfield; $banner.Title = "STAR WARS GALAXIES"; $banner.Sub = "A long time ago in a galaxy far, far away..."
    foreach ($d in @("$Here\client-files", "$Here\..\client-files")) {
        if (-not (Test-Path $d)) { continue }
        try {
            $idx = New-Object 'System.Collections.Generic.Dictionary[string,Retro.Art+TreEntry]'
            foreach ($t in Get-ChildItem "$d\*.tre") { [Retro.Art]::TreIndex($t.FullName, $idx) }
            $k = if ($idx.ContainsKey("texture/ui_starwars_logo_jtl.dds")) { "texture/ui_starwars_logo_jtl.dds" } else { "texture/ui_starwars_logo.dds" }
            $banner.Logo = [Retro.Art]::Dds([Retro.Art]::TreRead($idx[$k]))
        } catch { }
        break
    }
    Set-Look $banner 180 ([System.Drawing.Color]::FromArgb(10, 22, 34)) ([System.Drawing.Color]::FromArgb(204, 221, 238)) `
        ([System.Drawing.Color]::FromArgb(240, 184, 72)) ([System.Drawing.Color]::FromArgb(19, 40, 58))
}

function Set-Status($text) { $status.Text = $text; [System.Windows.Forms.Application]::DoEvents() }

# Get $rel into $file: offline "client-files" folder first (USB stick), then the LAN mirror, then SWGEmu.
# With $sum, a source only counts if its MD5 matches. Downloads stream in chunks so the window stays responsive.
$LocalDirs = @("$Here\client-files", "$Here\..\client-files")
function Get-File($rel, $file, $mirror, $sum = "") {
    foreach ($d in $LocalDirs) {
        $src = Join-Path $d ($rel -replace '/', '\')
        if (Test-Path $src) {
            Copy-Item -Force $src $file
            if (-not $sum -or (Test-Md5 $file $sum)) { return $true }
        }
    }
    $urls = @(); if ($mirror) { $urls += "$($mirror.TrimEnd('/'))/$rel" }; $urls += "$SwgEmu/$rel"
    foreach ($u in $urls) {
        try {
            $resp = $http.GetAsync($u, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
            if (-not $resp.IsSuccessStatusCode) { continue }
            $in = $resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            $out = [System.IO.File]::Create("$file.part")
            try {
                $buf = New-Object byte[] 1048576
                while (($n = $in.Read($buf, 0, $buf.Length)) -gt 0) { $out.Write($buf, 0, $n); [System.Windows.Forms.Application]::DoEvents() }
            } finally { $out.Close(); $in.Close() }
            Move-Item -Force "$file.part" $file
            if (-not $sum -or (Test-Md5 $file $sum)) { return $true }
        } catch { Remove-Item -Force -ErrorAction SilentlyContinue "$file.part" }
    }
    return $false
}
function Test-Md5($file, $sum) {
    (Test-Path $file) -and ((Get-FileHash -Algorithm MD5 $file).Hash -eq $sum.ToUpper())
}

function Install-Swg {
    $dest = $destBox.Text.Trim(); $server = $serverBox.Text.Trim(); $mirror = $mirrorBox.Text.Trim()
    if (-not $server) { throw "Please enter the server address." }
    New-Item -ItemType Directory -Force $dest | Out-Null
    $drive = (Get-Item $dest).PSDrive
    if ($drive.Free -lt 5GB) { throw "Not enough space on $($drive.Name): need 5 GB free." }

    Set-Status "Getting the file list..."
    $list = Join-Path $dest "required2.txt"
    if (-not (Get-File "required2.txt" $list $mirror)) { throw "Can't download the SWGEmu file list (no internet, and no LAN mirror reachable)." }
    $entries = Get-Content $list | Where-Object { $_ -match ';' }
    $bad = @(); $i = 0
    foreach ($e in $entries) {
        $i++; $f, $size, $sum = $e.Split(';')
        $bar.Value = [int](100 * $i / $entries.Count)
        Set-Status "($i/$($entries.Count)) $f"
        $file = Join-Path $dest ($f -replace '/', '\')
        New-Item -ItemType Directory -Force (Split-Path -Parent $file) | Out-Null
        if ((Test-Path $file) -and (Get-Item $file).Length -eq [long]$size -and (Test-Md5 $file $sum)) { continue }
        if (-not (Get-File $f $file $mirror $sum)) { $bad += $f }
    }
    if ($bad.Count) { throw "These files failed to download or verify:`n$($bad -join ', ')`n`nClick Install again to retry - good files are kept." }

    Set-Status "Writing game settings..."
    foreach ($c in @("swgemu.cfg", "swgemu_live.cfg", "swgemu_preload.cfg")) {
        if (-not (Get-File $c (Join-Path $dest $c) $mirror)) { throw "Can't download $c." }
    }
    foreach ($c in @("options.cfg", "user.cfg")) {
        if (-not (Test-Path (Join-Path $dest $c))) { Get-File $c (Join-Path $dest $c) $mirror | Out-Null }
    }
    $login = "[ClientGame]`r`n`tloginServerPort0=44453`r`n`tloginServerAddress0=$server`r`n[Station]`r`n`tsubscriptionFeatures=1`r`n`tgameFeatures=65535`r`n"
    [System.IO.File]::WriteAllText((Join-Path $dest "swgemu_login.cfg"), $login)
    $ctl = Join-Path $Here "..\controller"
    if (Test-Path $ctl) { Copy-Item -Recurse -Force $ctl (Join-Path $dest "controller") }

    Set-Status "Building the launcher (reads the logo and planets from the game files)..."
    $sh_status = if ($StatusHost) { $StatusHost } else { $server }
    $launcher = Build-Launcher $dest "SWG Launcher.exe" "SERVER=$server`r`nSTATUS=$sh_status`r`n"
    $play = if ($launcher) { "SWG Launcher.exe" } else { "SWGEmu.exe" }

    Set-Status "Making shortcuts..."
    $sh = New-Object -ComObject WScript.Shell
    $menu = Join-Path ([Environment]::GetFolderPath("Programs")) "Star Wars Galaxies"
    New-Item -ItemType Directory -Force $menu | Out-Null
    $links = @(@("$menu\Star Wars Galaxies.lnk", $play), @("$menu\Star Wars Galaxies Settings.lnk", "SWGEmu_Setup.exe"))
    if ($desk.Checked) { $links += , @((Join-Path ([Environment]::GetFolderPath("Desktop")) "Star Wars Galaxies.lnk"), $play) }
    foreach ($l in $links) {
        $s = $sh.CreateShortcut($l[0]); $s.TargetPath = Join-Path $dest $l[1]; $s.WorkingDirectory = $dest
        if ($launcher -and $l[1] -eq $play) { $s.IconLocation = Join-Path $dest "launcher-art\icon.ico" }
        $s.Save()
    }

    # SWG uses the old DirectX 9 helper DLLs (d3dx9), which Windows 10/11 don't ship. Offer Microsoft's runtime installer.
    $sys = if ([Environment]::Is64BitOperatingSystem) { "$env:WINDIR\SysWOW64" } else { "$env:WINDIR\System32" }
    if (-not (Get-ChildItem "$sys\d3dx9_*.dll" -ErrorAction SilentlyContinue)) {
        $a = [System.Windows.Forms.MessageBox]::Show("SWG needs the DirectX 9 runtime, which isn't installed yet. Open Microsoft's DirectX End-User Runtime download page now?", "Star Wars Galaxies installer", "YesNo")
        if ($a -eq "Yes") { Start-Process "https://www.microsoft.com/en-us/download/details.aspx?id=35" }
    }
    $bar.Value = 100
    Set-Status "Done."
    [System.Windows.Forms.MessageBox]::Show("Star Wars Galaxies is installed in $dest.`n`nStart it with the 'Star Wars Galaxies' shortcut. Make up a username and password on the first login (accounts are created automatically).`n`nController: see controller\README in the install folder.", "Star Wars Galaxies installer") | Out-Null
}

$go.Add_Click({
    $go.Enabled = $false
    try { Install-Swg } catch { Set-Status "Failed."; [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, "Star Wars Galaxies installer", "OK", "Error") | Out-Null }
    $go.Enabled = $true
})
if ($Quiet) { Install-Swg } else { [void]$form.ShowDialog() }
