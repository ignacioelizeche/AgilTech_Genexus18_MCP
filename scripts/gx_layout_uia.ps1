param(
    [Parameter(Mandatory = $true)]
    [string]$Payload
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win32UiaBridge
{
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
}
"@

$SW_RESTORE = 9
$MOUSEEVENTF_LEFTDOWN = 0x0002
$MOUSEEVENTF_LEFTUP = 0x0004

function Write-JsonResult {
    param([hashtable]$Result)
    $Result | ConvertTo-Json -Compress -Depth 8
}

function Get-GeneXusWindow {
    param([string]$TitleHint)

    $all = Get-Process | Where-Object {
        $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -and (
            $_.ProcessName -imatch "^genexus$" -or $_.MainWindowTitle -imatch "\bGeneXus\b"
        )
    }

    if ($TitleHint) {
        $filtered = @($all | Where-Object { $_.MainWindowTitle -like "*$TitleHint*" })
        if ($filtered.Length -gt 0) {
            return $filtered[0]
        }
    }

    return @($all)[0]
}

function Focus-Window {
    param([System.Diagnostics.Process]$Proc)
    [void][Win32UiaBridge]::ShowWindowAsync($Proc.MainWindowHandle, $SW_RESTORE)
    [void][Win32UiaBridge]::SetForegroundWindow($Proc.MainWindowHandle)
    Start-Sleep -Milliseconds 120
}

function Find-TabItem {
    param([System.Diagnostics.Process]$Proc, [string]$TabName, [int]$TimeoutSeconds = 10)

    $root = [System.Windows.Automation.AutomationElement]::FromHandle($Proc.MainWindowHandle)
    if (-not $root) { return $null }

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        $typeCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::TabItem
        )
        $tabItems = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $typeCondition)
        foreach ($candidate in $tabItems) {
            if ($candidate.Current.Name -ieq $TabName -or $candidate.Current.Name -like "*$TabName*") {
                return $candidate
            }
        }
        # Fallback: search by exact name
        $nameCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $TabName
        )
        $byExactName = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nameCondition)
        if ($byExactName) { return $byExactName }

        Start-Sleep -Milliseconds 200
    }

    # fallback: some custom toolstrips expose tabs as buttons instead of TabItem
    $all = @($root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition
    ))
    if ($all.Length -eq 0) { return $null }

    for ($j = 0; $j -lt $all.Length; $j++) {
        $el = $all[$j]
        $n = $el.Current.Name
        if ($n -and ($n -ieq $TabName -or $n -like "*$TabName*")) {
            return $el
        }
    }

    return $null
}

function Invoke-LayoutTab {
    param([System.Windows.Automation.AutomationElement]$TabItem)

    if (-not $TabItem) { return $false }

    $selectionPattern = $TabItem.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    if ($selectionPattern) {
        $selectionPattern.Select()
        return $true
    }

    $invokePattern = $TabItem.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    if ($invokePattern) {
        $invokePattern.Invoke()
        return $true
    }

    $r = $TabItem.Current.BoundingRectangle
    if ($r.Width -gt 1 -and $r.Height -gt 1) {
        $cx = [int]($r.X + ($r.Width / 2))
        $cy = [int]($r.Y + ($r.Height / 2))
        [void][Win32UiaBridge]::SetCursorPos($cx, $cy)
        Start-Sleep -Milliseconds 20
        [Win32UiaBridge]::mouse_event($MOUSEEVENTF_LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 20
        [Win32UiaBridge]::mouse_event($MOUSEEVENTF_LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
        return $true
    }

    return $false
}

function Get-ElementSnapshot {
    param([System.Windows.Automation.AutomationElement]$Element)

    if (-not $Element) { return $null }
    $r = $Element.Current.BoundingRectangle
    $value = $null
    $toggleState = $null
    $selectionItem = $null

    try {
        $vp = $Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        if ($vp) { $value = $vp.Current.Value }
    } catch {}

    try {
        $tp = $Element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        if ($tp) { $toggleState = [string]$tp.Current.ToggleState }
    } catch {}

    try {
        $sp = $Element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        if ($sp) { $selectionItem = [bool]$sp.Current.IsSelected }
    } catch {}

    @{
        name = $Element.Current.Name
        automationId = $Element.Current.AutomationId
        controlType = $Element.Current.ControlType.ProgrammaticName
        className = $Element.Current.ClassName
        enabled = $Element.Current.IsEnabled
        offscreen = $Element.Current.IsOffscreen
        value = $value
        toggleState = $toggleState
        selected = $selectionItem
        bounds = @{
            x = [math]::Round($r.X, 2)
            y = [math]::Round($r.Y, 2)
            width = [math]::Round($r.Width, 2)
            height = [math]::Round($r.Height, 2)
        }
    }
}

try {
    $req = $Payload | ConvertFrom-Json
    $action = [string]$req.action
    $titleHint = if ($req.PSObject.Properties.Name -contains "title") { [string]$req.title } else { "" }
    $tabName = if ($req.PSObject.Properties.Name -contains "tab" -and $req.tab) { [string]$req.tab } else { "Layout" }
    $inspectLimit = if ($req.PSObject.Properties.Name -contains "limit" -and $req.limit) { [int]$req.limit } else { 100 }

    if (-not $action) {
        Write-JsonResult @{
            action = "unknown"
            success = $false
            detail = "Missing action."
        }
        exit 1
    }

    $timeoutSeconds = 30
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $proc = $null
    while ($stopwatch.Elapsed.TotalSeconds -lt $timeoutSeconds -and -not $proc) {
        $proc = Get-GeneXusWindow -TitleHint $titleHint
        if (-not $proc) { Start-Sleep -Milliseconds 500 }
    }

    if ($action -eq "status") {
        if (-not $proc) {
            Write-JsonResult @{
                action = "status"
                running = $false
                focused = $false
                layoutTabDetected = $false
                detail = "GeneXus window not found after ${timeoutSeconds}s."
            }
            exit 0
        }

        $layoutTab = Find-TabItem -Proc $proc -TabName "Layout"
        $focused = $false
        try { $focused = ($proc.MainWindowHandle -eq [Win32UiaBridge]::GetForegroundWindow()) } catch {}
        Write-JsonResult @{
            action = "status"
            running = $true
            focused = $focused
            pid = $proc.Id
            title = $proc.MainWindowTitle
            layoutTabDetected = [bool]$layoutTab
            success = $true
        }
        exit 0
    }

    if (-not $proc) {
        Write-JsonResult @{
            action = $action
            success = $false
            detail = "GeneXus window not found."
        }
        exit 1
    }

    Focus-Window -Proc $proc

    switch ($action) {
        "focus" {
            Write-JsonResult @{
                action = "focus"
                success = $true
                pid = $proc.Id
                title = $proc.MainWindowTitle
                detail = "GeneXus window focused."
            }
            exit 0
        }
        "activate-layout" {
            $layoutTab = Find-TabItem -Proc $proc -TabName "Layout"
            if (-not $layoutTab) {
                Write-JsonResult @{
                    action = "activate-layout"
                    success = $false
                    pid = $proc.Id
                    title = $proc.MainWindowTitle
                    detail = "Layout tab not found by UI Automation."
                }
                exit 1
            }

            $selected = Invoke-LayoutTab -TabItem $layoutTab
            if (-not $selected) {
                Write-JsonResult @{
                    action = "activate-layout"
                    success = $false
                    pid = $proc.Id
                    title = $proc.MainWindowTitle
                    detail = "Layout tab found but no supported invoke/select pattern."
                }
                exit 1
            }

            Write-JsonResult @{
                action = "activate-layout"
                success = $true
                pid = $proc.Id
                title = $proc.MainWindowTitle
                tab = "Layout"
                detail = "Layout tab activated."
            }
            exit 0
        }
        "activate-tab" {
            $tab = Find-TabItem -Proc $proc -TabName $tabName
            if (-not $tab) {
                Write-JsonResult @{
                    action = "activate-tab"
                    success = $false
                    pid = $proc.Id
                    title = $proc.MainWindowTitle
                    tab = $tabName
                    detail = "Tab '$tabName' not found by UI Automation."
                }
                exit 1
            }

            $selected = Invoke-LayoutTab -TabItem $tab
            if (-not $selected) {
                Write-JsonResult @{
                    action = "activate-tab"
                    success = $false
                    pid = $proc.Id
                    title = $proc.MainWindowTitle
                    tab = $tabName
                    detail = "Tab '$tabName' found but no supported invoke/select pattern."
                }
                exit 1
            }

            Write-JsonResult @{
                action = "activate-tab"
                success = $true
                pid = $proc.Id
                title = $proc.MainWindowTitle
                tab = $tabName
                detail = "Tab '$tabName' activated."
            }
            exit 0
        }
        "inspect" {
            $tab = Find-TabItem -Proc $proc -TabName $tabName
            $tabActivated = $false
            if ($tab) {
                $tabActivated = Invoke-LayoutTab -TabItem $tab
                Start-Sleep -Milliseconds 100
            }

            $root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
            if (-not $root) {
                Write-JsonResult @{
                    action = "inspect"
                    success = $false
                    pid = $proc.Id
                    title = $proc.MainWindowTitle
                    tab = $tabName
                    detail = "Unable to get root automation element."
                }
                exit 1
            }

            # Enhanced DFS walk using RawViewWalker for maximum discovery
            $stack = New-Object System.Collections.Generic.Stack[System.Windows.Automation.AutomationElement]
            $stack.Push($root)
            
            $controls = @()
            $emitted = 0
            $total = 0
            $inspectStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
            $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker

            while ($stack.Count -gt 0 -and $emitted -lt $inspectLimit -and $inspectStopwatch.Elapsed.TotalSeconds -lt 30) {
                $current = $stack.Pop()
                if ($current -eq $root) {
                    $child = $walker.GetFirstChild($current)
                    while ($child -ne $null) {
                        $stack.Push($child)
                        $child = $walker.GetNextSibling($child)
                    }
                    continue
                }

                $total++
                $snap = Get-ElementSnapshot -Element $current
                if ($null -ne $snap) {
                    $controls += $snap
                    $emitted++
                }

                # If this looks like an editor window for our target, try a targeted FindAll for speed
                if ($req.PSObject.Properties.Name -contains "name" -and $req.name -and $current.Current.Name -ieq $req.name) {
                    try {
                        $descendants = $current.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
                        foreach ($d in $descendants) {
                            if ($emitted -ge $inspectLimit) { break }
                            $dsnap = Get-ElementSnapshot -Element $d
                            if ($null -ne $dsnap) {
                                $controls += $dsnap
                                $emitted++
                                $total++
                            }
                        }
                        # We already processed descendants, move to siblings
                        continue
                    } catch {}
                }

                # Standard DFS child discovery
                $child = $walker.GetFirstChild($current)
                while ($child -ne $null) {
                    $stack.Push($child)
                    $child = $walker.GetNextSibling($child)
                }
            }

            $empty = $emitted -eq 0
            Write-JsonResult @{
                action = "inspect"
                running = $true
                success = $true
                pid = $proc.Id
                title = $proc.MainWindowTitle
                tab = $tabName
                tabActivated = $tabActivated
                returned = $emitted
                total = $total
                empty = $empty
                controls = $controls
                detail = if ($empty) { "No controls found." } elseif ($emitted -lt $total) { "Output truncated. ${emitted} of ${total} controls shown." } else { "UI Automation snapshot captured." }
            }
            exit 0
        }
        "send-keys" {
            $keys = [string]$req.keys
            if (-not $keys) {
                Write-JsonResult @{
                    action = "send-keys"
                    success = $false
                    detail = "Missing --keys payload."
                }
                exit 1
            }
            [System.Windows.Forms.SendKeys]::SendWait($keys)
            Write-JsonResult @{
                action = "send-keys"
                success = $true
                pid = $proc.Id
                title = $proc.MainWindowTitle
                detail = "Keys sent."
            }
            exit 0
        }
        "type-text" {
            $text = [string]$req.text
            if (-not $text) {
                Write-JsonResult @{
                    action = "type-text"
                    success = $false
                    detail = "Missing --text payload."
                }
                exit 1
            }
            [System.Windows.Forms.SendKeys]::SendWait($text)
            Write-JsonResult @{
                action = "type-text"
                success = $true
                pid = $proc.Id
                title = $proc.MainWindowTitle
                detail = "Text sent."
            }
            exit 0
        }
        "click" {
            if (-not ($req.PSObject.Properties.Name -contains "x") -or -not ($req.PSObject.Properties.Name -contains "y")) {
                Write-JsonResult @{
                    action = "click"
                    success = $false
                    detail = "Missing --x/--y payload."
                }
                exit 1
            }

            $x = [int]$req.x
            $y = [int]$req.y
            [void][Win32UiaBridge]::SetCursorPos($x, $y)
            Start-Sleep -Milliseconds 30
            [Win32UiaBridge]::mouse_event($MOUSEEVENTF_LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 30
            [Win32UiaBridge]::mouse_event($MOUSEEVENTF_LEFTUP, 0, 0, 0, [UIntPtr]::Zero)

            Write-JsonResult @{
                action = "click"
                success = $true
                pid = $proc.Id
                title = $proc.MainWindowTitle
                detail = "Click dispatched at screen coordinates."
            }
            exit 0
        }
        default {
            Write-JsonResult @{
                action = $action
                success = $false
                detail = "Unsupported action. Use focus, activate-layout, activate-tab, send-keys, type-text, click, inspect, or status."
            }
            exit 1
        }
    }
} catch {
    Write-JsonResult @{
        action = "runtime"
        success = $false
        detail = $_.Exception.Message
    }
    exit 1
}
