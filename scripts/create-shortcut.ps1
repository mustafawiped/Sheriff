[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$TargetPath = (Join-Path $PSScriptRoot '..\artifacts\publish\win-x64\Sheriff.exe'),
    [string]$ShortcutPath = (Join-Path ([Environment]::GetFolderPath('Programs')) 'Sheriff.lnk'),
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$target = (Resolve-Path -LiteralPath $TargetPath -ErrorAction Stop).Path
if ([IO.Path]::GetExtension($target) -ine '.exe') { throw 'TargetPath must point to Sheriff.exe.' }
$shortcut = [IO.Path]::GetFullPath($ShortcutPath)
if ([IO.Path]::GetExtension($shortcut) -ine '.lnk') { throw 'ShortcutPath must end in .lnk.' }
if ((Test-Path -LiteralPath $shortcut) -and !$Force) { throw "Shortcut already exists. Use -Force to replace it: $shortcut" }
if ($PSCmdlet.ShouldProcess($shortcut, "Create Start Menu shortcut for $target")) {
    $shell = $null
    $link = $null
    try {
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($shortcut)) | Out-Null
        $shell = New-Object -ComObject WScript.Shell
        $link = $shell.CreateShortcut($shortcut)
        $link.TargetPath = $target
        $link.WorkingDirectory = [IO.Path]::GetDirectoryName($target)
        $link.IconLocation = "$target,0"
        $link.Description = 'Sheriff application launcher'
        $link.Save()
        Write-Output "Shortcut created: $shortcut"
    } finally {
        if ($null -ne $link) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) | Out-Null }
        if ($null -ne $shell) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null }
    }
}
