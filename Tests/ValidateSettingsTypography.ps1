$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
[xml]$settings = Get-Content (Join-Path $root 'SettingsDialog.xaml') -Raw
[xml]$mainWindow = Get-Content (Join-Path $root 'MainWindow.xaml') -Raw
[xml]$project = Get-Content (Join-Path $root 'JuChuang.csproj') -Raw
$mainCode = Get-Content (Join-Path $root 'MainWindow.xaml.cs') -Raw
$ns = 'http://schemas.microsoft.com/winfx/2006/xaml'

$fontSizes = @{
    '常规' = '15'
    '消息通知' = '15'
    '开机自动启动' = '14'
    '任务栏闪烁' = '14'
    'Windows Toast 通知' = '14'
    '新消息提示音' = '14'
    '免打扰时段' = '14'
    '登录 Windows 后启动 JNest' = '13'
    '有新消息时提示任务栏注意' = '13'
    'JNest 在后台或最小化时弹出系统通知' = '13'
    '新消息到达时播放系统提示音' = '13'
    '在设定时段内不弹出 Toast 通知和提示音' = '13'
    '开始' = '13'
    '结束' = '13'
    '设置即时生效并自动保存' = '13'
}
$descriptionNames = @(
    '登录 Windows 后启动 JNest'
    '有新消息时提示任务栏注意'
    'JNest 在后台或最小化时弹出系统通知'
    '新消息到达时播放系统提示音'
    '在设定时段内不弹出 Toast 通知和提示音'
)
$textBlocks = @($settings.SelectNodes("//*[local-name()='TextBlock']"))
foreach ($name in $fontSizes.Keys) {
    $nodes = @($textBlocks | Where-Object { $_.GetAttribute('Text') -eq $name })
    if ($nodes.Count -ne 1) { throw "Expected 1 TextBlock '$name', got $($nodes.Count)" }
    if ($nodes[0].GetAttribute('FontSize') -ne $fontSizes[$name]) {
        throw "Unexpected font size for '$name'"
    }
    if ($name -in $descriptionNames -and
        ($nodes[0].GetAttribute('Foreground') -ne '#596273' -or
         $nodes[0].GetAttribute('Margin') -ne '0,3,0,0')) {
        throw "Unexpected description color/margin for '$name'"
    }
}
foreach ($name in @('DndStartTime','DndEndTime')) {
    $nodes = @($settings.SelectNodes("//*[local-name()='ComboBox']") |
        Where-Object { $_.GetAttribute('Name',$ns) -eq $name })
    if ($nodes.Count -ne 1 -or $nodes[0].GetAttribute('FontSize') -ne '13') {
        throw "Missing 13 DIP time combobox $name"
    }
}
$buttonStyle = @($settings.SelectNodes("//*[local-name()='Style']") |
    Where-Object { $_.GetAttribute('Key',$ns) -eq 'SettingsCloseButtonStyle' })
if ($buttonStyle.Count -ne 1) { throw 'Settings button style missing' }
foreach ($prop in @(@('Width','104'),@('Height','36'),@('FontSize','14'))) {
    $setters = @($buttonStyle[0].SelectNodes("*[local-name()='Setter']") |
        Where-Object { $_.GetAttribute('Property') -eq $prop[0] -and $_.GetAttribute('Value') -eq $prop[1] })
    if ($setters.Count -ne 1) { throw "Settings button $($prop[0]) must be $($prop[1])" }
}
$list = @($mainWindow.SelectNodes("//*[local-name()='ListBox']") |
    Where-Object { $_.GetAttribute('Name',$ns) -eq 'AccountsList' })
if ($list.Count -ne 1) { throw 'Missing AccountsList' }
foreach ($gesture in @('PreviewMouseRightButtonDown', 'PreviewMouseRightButtonUp')) {
    if ($list[0].GetAttribute($gesture) -ne 'AccountsList_IgnoreRightClick') {
        throw "Right-click guard missing for $gesture"
    }
}
if ($mainCode -notmatch '(?s)void\s+AccountsList_IgnoreRightClick\s*\([^)]*\)\s*\{\s*if\s*\(AccountSelectionInputPolicy\.ShouldSuppress\(e\.ChangedButton\)\)\s*e\.Handled\s*=\s*true;\s*\}') {
    throw 'Stable account right-click handler no longer applies input policy'
}
# Settings is a single natural-height page: no hidden overflow or nested scroll bars.
if ($settings.DocumentElement.GetAttribute('SizeToContent') -ne 'Height') { throw 'Settings window must fit its content' }
if ($settings.OuterXml -match '<ScrollViewer|<ScrollBar|JNestSettingsScroll') { throw 'Settings must not contain a scrollbar' }
if (@($textBlocks | Where-Object { $_.GetAttribute('Text') -eq '设置' }).Count -ne 0) {
    throw 'Redundant Settings heading remains'
}
$gridRows = @($settings.SelectNodes("//*[local-name()='Grid.RowDefinitions']/*[local-name()='RowDefinition']"))
if ($gridRows.Count -lt 2 -or $gridRows[0].GetAttribute('Height') -ne 'Auto') {
    throw 'Settings content must use natural height'
}
if ($project.Project.PropertyGroup.Version -notin @('2.0.0', '2.0.1-beta.1', '2.0.1-beta.2', '2.0.1-beta.3', '2.0.2-beta.1')) {
    throw 'Project must identify v2.0.0 or a v2.0.1/v2.0.2 maintenance beta'
}
if ($settings.OuterXml -match 'ChangeLockPasswordButton|ChangeLockPassword_Click|隐私与安全') { throw 'Obsolete password settings UI remains' }
if ($settings.OuterXml -notmatch 'DndAllDayHint') { throw 'Missing all-day DND hint' }
Write-Host 'PASS: natural-height settings layout, typography, DND hint, right-click guard and version'
