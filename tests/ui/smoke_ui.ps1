# AuraAudio v2.0.2 - End-to-End Pure-Monochrome UIA Smoke Test
# Validates every v2.0.2 shell surface, DWM neutral attributes, rail/right-pane/FullScreen/MiniPlayer
# modes, SearchHints + 5 scopes, Settings BreadcrumbBar + categories, Inspector FFT canvas,
# 4-tab Get Info sheet, and clean zero-crash shutdown via language-independent AutomationIds (Part D.9).

param(
    [string]$ExePath = ""
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class AuraSmokeWin32 {
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")]
    public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);
}
"@

$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$exePath = if ([string]::IsNullOrWhiteSpace($ExePath)) {
    Join-Path $repoRoot "bin\Release\net9.0-windows10.0.19041.0\AuraAudio.exe"
} else {
    $ExePath
}
$dirPath = Split-Path $exePath
$crashLog = Join-Path $dirPath "aura_crash.log"
$startupLog = Join-Path $dirPath "aura_startup_error.log"

if (-not (Test-Path $exePath)) {
    Write-Error "Target binary not found at: $exePath. Build or publish Release first."
    exit 1
}
Write-Output "Target Exe: $exePath"

Get-Process -Name "AuraAudio" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

if (Test-Path $crashLog) { Remove-Item $crashLog -Force }
if (Test-Path $startupLog) { Remove-Item $startupLog -Force }

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exePath
$psi.WorkingDirectory = $dirPath
$psi.UseShellExecute = $false
$psi.EnvironmentVariables["AURA_SMOKE_MODE"] = "1"
$psi.EnvironmentVariables["AURA_AUDIO_NULL_OUTPUT"] = "1"
$proc = [System.Diagnostics.Process]::Start($psi)
$root = $null
$pidCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
    $proc.Id
)

try {
    for ($i = 0; $i -lt 50; $i++) {
        $proc.Refresh()
        if ($proc.HasExited) {
            throw "App exited prematurely with code $($proc.ExitCode)"
        }
        $root = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children,
            $pidCond
        )
        if ($root -ne $null) { break }
        Start-Sleep -Milliseconds 200
    }

    if ($root -eq $null) {
        throw "Could not locate MainWindow UIA root for PID $($proc.Id)."
    }

    $hwnd = [IntPtr]$root.Current.NativeWindowHandle
    if ($hwnd -ne [IntPtr]::Zero) {
        [AuraSmokeWin32]::SetForegroundWindow($hwnd) | Out-Null
        [AuraSmokeWin32]::BringWindowToTop($hwnd) | Out-Null
    }
    Start-Sleep -Milliseconds 900

    $rect = $root.Current.BoundingRectangle
    Write-Output ("Window: Title='{0}', Rect={1}x{2}" -f $root.Current.Name, [int]$rect.Width, [int]$rect.Height)
    if ($root.Current.Name -notlike "*v2.0.2*") {
        throw "Expected MainWindow title to include 'v2.0.2', got '$($root.Current.Name)'"
    }
    Write-Output "PASS [Version]: MainWindow title displays v2.0.2"

    # 1. Verify Windows 11 DWM pure-neutral chrome attributes (DarkMode == 1, SystemBackdrop == 1 None)
    $darkAttr = 0
    $backdropAttr = 0
    $hrDark = [AuraSmokeWin32]::DwmGetWindowAttribute($hwnd, 20, [ref]$darkAttr, 4)
    $hrBackdrop = [AuraSmokeWin32]::DwmGetWindowAttribute($hwnd, 38, [ref]$backdropAttr, 4)
    Write-Output ("PASS [DWM]: DarkMode={0} (hr={1}), SystemBackdrop={2} (hr={3})" -f $darkAttr, $hrDark, $backdropAttr, $hrBackdrop)
    if ($hrDark -eq 0 -and $darkAttr -ne 1) { throw "Expected DWMWA_USE_IMMERSIVE_DARK_MODE == 1, got $darkAttr" }
    if ($hrBackdrop -eq 0 -and $backdropAttr -ne 1) { throw "Expected DWMWA_SYSTEMBACKDROP_TYPE == 1 (None), got $backdropAttr" }

    function Find-ById([string]$id, [int]$retries = 8, [bool]$allProcessWindows = $false) {
        $idCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
            $id
        )
        for ($i = 0; $i -lt $retries; $i++) {
            if ($allProcessWindows) {
                $wins = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
                    [System.Windows.Automation.TreeScope]::Children,
                    $pidCond
                )
                foreach ($w in $wins) {
                    $el = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $idCond)
                    if ($el -ne $null) { return $el }
                }
            } else {
                $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $idCond)
                if ($el -ne $null) { return $el }
            }
            Start-Sleep -Milliseconds 150
        }
        return $null
    }

    function Invoke-El($el, [string]$label) {
        if ($el -eq $null) { throw "Missing UIA element: $label" }
        $pat = $null
        if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pat)) {
            $pat.Invoke()
            return
        }
        if ($el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat)) {
            $pat.Select()
            return
        }
        if ($el.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pat)) {
            $pat.Toggle()
            return
        }
        throw "Element '$label' supports neither Invoke, SelectionItem, nor Toggle"
    }

    # 2. Verify Collapsible Left Rail (248px <-> 56px)
    $homeNav = Find-ById "Aura.Nav.Home"
    $wBefore = [int]$homeNav.Current.BoundingRectangle.Width
    Invoke-El (Find-ById "Aura.TopBar.RailToggle") "TopBar.RailToggle (Collapse)"
    Start-Sleep -Milliseconds 450
    $wAfterCollapse = [int](Find-ById "Aura.Nav.Home").Current.BoundingRectangle.Width
    Invoke-El (Find-ById "Aura.TopBar.RailToggle") "TopBar.RailToggle (Expand)"
    Start-Sleep -Milliseconds 450
    $wAfterExpand = [int](Find-ById "Aura.Nav.Home").Current.BoundingRectangle.Width
    if ($wBefore -eq $wAfterCollapse) { throw "Left Rail did not change width on toggle ($wBefore -> $wAfterCollapse)" }
    Write-Output ("PASS [Rail]: Width toggled {0}px -> {1}px -> {2}px" -f $wBefore, $wAfterCollapse, $wAfterExpand)

    # 3. Verify Home + Library Navigation (Songs + ColumnBrowser, Albums, Artists, Playlists) + Back/Forward
    if ((Find-ById "Aura.Home.Root") -eq $null) { throw "Missing Aura.Home.Root" }

    Invoke-El (Find-ById "Aura.Nav.Songs") "Nav.Songs"
    Start-Sleep -Milliseconds 350
    if ((Find-ById "Aura.Songs.DataGrid") -eq $null) { throw "Missing Aura.Songs.DataGrid" }
    $colToggle = Find-ById "Aura.Songs.ColumnBrowserToggle"
    Invoke-El $colToggle "Songs.ColumnBrowserToggle On"
    Start-Sleep -Milliseconds 200
    Invoke-El $colToggle "Songs.ColumnBrowserToggle Off"
    Start-Sleep -Milliseconds 150

    Invoke-El (Find-ById "Aura.Nav.Albums") "Nav.Albums"
    Start-Sleep -Milliseconds 350
    if ((Find-ById "Aura.Albums.Grid") -eq $null) { throw "Missing Aura.Albums.Grid" }

    Invoke-El (Find-ById "Aura.Nav.Artists") "Nav.Artists"
    Start-Sleep -Milliseconds 350
    if ((Find-ById "Aura.Artists.MasterList") -eq $null) { throw "Missing Aura.Artists.MasterList" }

    Invoke-El (Find-ById "Aura.Nav.Playlists") "Nav.Playlists"
    Start-Sleep -Milliseconds 300

    Invoke-El (Find-ById "Aura.TopBar.Back") "TopBar.Back"
    Start-Sleep -Milliseconds 250
    if ((Find-ById "Aura.Artists.MasterList") -eq $null) { throw "Back navigation did not restore ArtistsView" }
    Invoke-El (Find-ById "Aura.TopBar.Forward") "TopBar.Forward"
    Start-Sleep -Milliseconds 250
    Write-Output "PASS [Library & History]: Home, Songs (+ColumnBrowser), Albums, Artists, Playlists, Back/Forward"

    # 4. Verify Global Search + Live SearchHints Popup + 5 Scope Pills
    $searchBox = Find-ById "Aura.TopBar.SearchBox"
    $valPat = $null
    if (-not $searchBox.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$valPat)) {
        throw "Aura.TopBar.SearchBox missing ValuePattern"
    }
    $searchBox.SetFocus()
    Start-Sleep -Milliseconds 100
    $valPat.SetValue("a")
    Start-Sleep -Milliseconds 500
    $seeAll = Find-ById "Aura.Search.SeeAllHints" 10 $true
    if ($seeAll -eq $null) { throw "SearchHintsPopup (Aura.Search.SeeAllHints) did not appear" }
    Invoke-El $seeAll "Search.SeeAllHints"
    Start-Sleep -Milliseconds 350

    foreach ($sid in @("Aura.Search.Scope.All","Aura.Search.Scope.Songs","Aura.Search.Scope.Albums","Aura.Search.Scope.Artists","Aura.Search.Scope.Playlists")) {
        if ((Find-ById $sid) -eq $null) { throw "Missing Search scope pill: $sid" }
    }
    Invoke-El (Find-ById "Aura.Search.Scope.Songs") "Scope.Songs"
    Start-Sleep -Milliseconds 120
    Invoke-El (Find-ById "Aura.Search.Scope.All") "Scope.All"
    Write-Output "PASS [Search]: Top-bar SearchHintsPopup + SearchView + 5 Aura.Search.Scope.* pills"

    # 5. Verify Adaptive Right Split Pane (Queue [Playing Next | History] <-> Lyrics)
    Invoke-El (Find-ById "Aura.Dock.Queue") "Dock.Queue"
    Start-Sleep -Milliseconds 400
    foreach ($qid in @("Aura.Queue.TabPlayingNext","Aura.Queue.TabHistory","Aura.Queue.Clear","Aura.RightPane.Close")) {
        if ((Find-ById $qid) -eq $null) { throw "Missing Queue element: $qid" }
    }
    Invoke-El (Find-ById "Aura.Queue.TabHistory") "Queue.TabHistory"
    Start-Sleep -Milliseconds 150
    if ((Find-ById "Aura.Queue.ClearHistory") -eq $null) { throw "Missing Aura.Queue.ClearHistory in History tab" }

    Invoke-El (Find-ById "Aura.Dock.Lyrics") "Dock.Lyrics (Cross-fade switch)"
    Start-Sleep -Milliseconds 350
    if ((Find-ById "Aura.Lyrics.List") -eq $null) { throw "Missing Aura.Lyrics.List in Lyrics pane" }

    Invoke-El (Find-ById "Aura.RightPane.Close") "RightPane.Close"
    Start-Sleep -Milliseconds 350
    Write-Output "PASS [RightPane]: Queue (Playing Next | History) <-> Lyrics cross-fade + Close"

    # 6. Verify Signal Path Flyout + Full-Screen Stage + Floating Mini-Player
    $audioBadge = Find-ById "Aura.Dock.AudioBadge"
    Invoke-El $audioBadge "Dock.AudioBadge Open"
    Start-Sleep -Milliseconds 250
    Invoke-El $audioBadge "Dock.AudioBadge Close"
    Start-Sleep -Milliseconds 200

    Invoke-El (Find-ById "Aura.Dock.FullScreen") "Dock.FullScreen Open"
    Start-Sleep -Milliseconds 450
    if ((Find-ById "Aura.FullScreen.Exit") -eq $null) { throw "Missing Aura.FullScreen.Exit" }
    $fsInfoBtn = Find-ById "Aura.FullScreen.Info"
    if ($fsInfoBtn -eq $null) { throw "Missing Aura.FullScreen.Info" }
    Invoke-El $fsInfoBtn "FullScreen.Info Open"
    Start-Sleep -Milliseconds 300
    if ((Find-ById "Aura.Info.Tab.Details") -eq $null) { throw "InfoInspectorSheet did not open above Full-Screen" }
    Invoke-El (Find-ById "Aura.Info.Close") "Info.Close (from FullScreen)"
    Start-Sleep -Milliseconds 250
    $fsLyricsToggle = Find-ById "Aura.FullScreen.LyricsToggle"
    Invoke-El $fsLyricsToggle "FullScreen.LyricsToggle On"
    Start-Sleep -Milliseconds 300
    Invoke-El $fsLyricsToggle "FullScreen.LyricsToggle Off"
    Start-Sleep -Milliseconds 250
    Invoke-El (Find-ById "Aura.FullScreen.Exit") "FullScreen.Exit"
    Start-Sleep -Milliseconds 400

    Invoke-El (Find-ById "Aura.Dock.MiniPlayer") "Dock.MiniPlayer Open"
    Start-Sleep -Milliseconds 500
    $miniToggle = Find-ById "Aura.MiniPlayer.ToggleMode" 12 $true
    $miniReturn = Find-ById "Aura.MiniPlayer.Return" 12 $true
    if ($miniToggle -eq $null -or $miniReturn -eq $null) { throw "MiniPlayer window controls not found" }
    Invoke-El $miniToggle "MiniPlayer.ToggleMode (AlbumView)"
    Start-Sleep -Milliseconds 250
    Invoke-El $miniReturn "MiniPlayer.Return"
    Start-Sleep -Milliseconds 500

    # Re-acquire MainWindow handle after Hide/Show cycle
    $root = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Children,
        $pidCond
    )
    if ($root -eq $null) { throw "MainWindow did not restore after closing MiniPlayer" }
    Write-Output "PASS [NowPlaying]: SignalPathFlyout + FullScreen (Centered <-> Split Lyrics) + MiniPlayer round-trip"

    function Click-VisibleText($container, [string]$label) {
        if ($container -eq $null) { return }
        $txtCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Text
        )
        $all = $container.FindAll([System.Windows.Automation.TreeScope]::Descendants, $txtCond)
        $clicked = 0
        foreach ($t in $all) {
            try {
                if ($t.Current.IsOffscreen) { continue }
                $r = $t.Current.BoundingRectangle
                if ($r.Width -lt 12 -or $r.Height -lt 8) { continue }
                $cx = [int]($r.Left + ($r.Width / 2.0))
                $cy = [int]($r.Top + ($r.Height / 2.0))
                [AuraSmokeWin32]::SetCursorPos($cx, $cy) | Out-Null
                [AuraSmokeWin32]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
                [AuraSmokeWin32]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
                Start-Sleep -Milliseconds 40
                $proc.Refresh()
                if ($proc.HasExited) { throw "App crashed when clicking text inside $label ('$($t.Current.Name)')" }
                $clicked++
                if ($clicked -ge 3) { break }
            } catch {
                $proc.Refresh()
                if ($proc.HasExited) { throw "App crashed when clicking text inside $label" }
            }
        }
        if ($clicked -eq 0) { throw "No clickable visible Text element found in $label" }
    }

    # 7. Verify Unified Settings (BreadcrumbBar + 5 Categories + Sub-pages)
    Invoke-El (Find-ById "Aura.Nav.Settings") "Nav.Settings"
    Start-Sleep -Milliseconds 350
    if ((Find-ById "Aura.Settings.Breadcrumb") -eq $null) { throw "Missing Aura.Settings.Breadcrumb" }
    foreach ($cid in @("Aura.Settings.Category.General","Aura.Settings.Category.Playback","Aura.Settings.Category.Library","Aura.Settings.Category.Accessibility","Aura.Settings.Category.About")) {
        if ((Find-ById $cid) -eq $null) { throw "Missing Settings category: $cid" }
    }
    Click-VisibleText $root "Settings"
    Invoke-El (Find-ById "Aura.Settings.Category.Playback") "Settings.Playback"
    Start-Sleep -Milliseconds 250
    if ((Find-ById "Aura.Settings.Playback.Apply") -eq $null) { throw "Missing Aura.Settings.Playback.Apply" }
    Invoke-El (Find-ById "Aura.Settings.Breadcrumb") "Settings.Breadcrumb Root"
    Start-Sleep -Milliseconds 250
    Invoke-El (Find-ById "Aura.Settings.Category.Library") "Settings.Library"
    Start-Sleep -Milliseconds 250
    if ((Find-ById "Aura.Settings.Library.AddFolder") -eq $null) { throw "Missing Aura.Settings.Library.AddFolder" }
    Invoke-El (Find-ById "Aura.Settings.Breadcrumb") "Settings.Breadcrumb Root 2"
    Start-Sleep -Milliseconds 250
    Invoke-El (Find-ById "Aura.Settings.Category.Accessibility") "Settings.Accessibility"
    Start-Sleep -Milliseconds 250
    if ((Find-ById "Aura.Settings.A11y.ReducedMotion") -eq $null) { throw "Missing Aura.Settings.A11y.ReducedMotion" }
    Write-Output "PASS [Settings]: BreadcrumbBar + 5 Categories + Playback/Library/Accessibility drill-down"

    # 8. Verify Full-Page Audio Inspector + Monochrome SpekSpectrumControl + Click-on-Text safety + Track-Follow across Pages
    Invoke-El (Find-ById "Aura.Nav.Songs") "Nav.Songs (Inspector Follow Prep)"
    Start-Sleep -Milliseconds 300
    $playAllBtn = Find-ById "Aura.Songs.PlayAll"
    if ($playAllBtn -eq $null) { throw "Missing Aura.Songs.PlayAll" }
    Invoke-El $playAllBtn "Songs.PlayAll (Track A)"
    Start-Sleep -Milliseconds 600

    Invoke-El (Find-ById "Aura.Nav.Inspector") "Nav.Inspector (Track A)"
    Start-Sleep -Milliseconds 400
    foreach ($iid in @("Aura.Inspector.Spectrum","Aura.Inspector.AnalyzeCurrent","Aura.Inspector.OpenFile")) {
        if ((Find-ById $iid) -eq $null) { throw "Missing Inspector element: $iid" }
    }
    $inspTitleElA = Find-ById "Aura.Inspector.TrackTitle"
    $inspBadgeElA = Find-ById "Aura.Inspector.FormatBadge"
    if ($inspTitleElA -eq $null -or $inspBadgeElA -eq $null) {
        throw "Inspector header/format badge missing after starting Track A"
    }
    $titleA = $inspTitleElA.Current.Name
    Click-VisibleText (Find-ById "Aura.Inspector.Root") "Inspector"

    # Switch to another page (Songs), change track while Inspector is inactive, then return to Inspector
    Invoke-El (Find-ById "Aura.Nav.Songs") "Nav.Songs (Leave Inspector)"
    Start-Sleep -Milliseconds 300
    Invoke-El (Find-ById "Aura.Dock.Next") "Dock.Next (Track B while Inspector inactive)"
    Start-Sleep -Milliseconds 600
    Invoke-El (Find-ById "Aura.Nav.Inspector") "Nav.Inspector (Track B)"
    Start-Sleep -Milliseconds 400
    $inspTitleElB = Find-ById "Aura.Inspector.TrackTitle"
    if ($inspTitleElB -eq $null) { throw "Inspector TrackTitle missing after switching to Track B" }
    $titleB = $inspTitleElB.Current.Name
    if ([string]::IsNullOrWhiteSpace($titleA) -or [string]::IsNullOrWhiteSpace($titleB)) {
        throw "Expected non-empty Inspector track titles (A='$titleA', B='$titleB')"
    }
    if ($titleA -eq $titleB) {
        throw "Inspector did not update when track changed from another page (stuck on '$titleA')"
    }
    Write-Output ("PASS [Inspector]: InspectorView + Spectrum + Text Click Safety + Follows Track Across Pages ('{0}' -> '{1}')" -f $titleA, $titleB)

    # 9. Verify 4-Tab Get Info Modal Sheet + Click-on-Text safety (InfoSheet & Lyrics)
    Invoke-El (Find-ById "Aura.Dock.Info") "Dock.Info"
    Start-Sleep -Milliseconds 350
    $infoSheet = Find-ById "Aura.Info.Sheet"
    foreach ($tid in @("Aura.Info.Tab.Details","Aura.Info.Tab.Audio","Aura.Info.Tab.Authenticity","Aura.Info.Tab.Lyrics")) {
        $tabEl = Find-ById $tid
        if ($tabEl -eq $null) { throw "Missing Info Sheet tab: $tid" }
        Invoke-El $tabEl $tid
        Start-Sleep -Milliseconds 120
        Click-VisibleText $infoSheet "InfoSheet/$tid"
    }
    Invoke-El (Find-ById "Aura.Info.Close") "Info.Close"
    Start-Sleep -Milliseconds 250
    Write-Output "PASS [InfoSheet]: 4-Tab Get Info (Details | Audio | Authenticity | Lyrics) + Text Click Safety + Close"

    # 10. Clean Shutdown & Zero Crash Log Verification
    Invoke-El (Find-ById "Aura.TopBar.Close") "TopBar.Close"
    $exited = $proc.WaitForExit(6000)
    if (-not $exited) {
        throw "App did not exit cleanly within 6s after clicking Aura.TopBar.Close."
    }

    $crashExists = Test-Path $crashLog
    $startupExists = Test-Path $startupLog
    Write-Output ("PASS [Shutdown]: ExitedCleanly={0}, ExitCode={1}, CrashLog={2}, StartupErrLog={3}" -f $proc.HasExited, $proc.ExitCode, $crashExists, $startupExists)

    if ($proc.ExitCode -ne 0) { throw "Non-zero exit code: $($proc.ExitCode)" }
    if ($crashExists) { Get-Content $crashLog; throw "Crash log detected!" }
    if ($startupExists) { Get-Content $startupLog; throw "Startup error log detected!" }

    Write-Output "SMOKE TEST PASSED (v2.0.2 - ALL CHECKS GREEN)"
}
finally {
    if ($proc -ne $null -and -not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    }
}
