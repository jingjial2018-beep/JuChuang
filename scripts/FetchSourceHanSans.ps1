# Source Han Sans 2.005R Simplified Chinese subset OTFs.
# Downloads only at build time; fonts are embedded into JNest's WPF resources.
# Git blob SHA verification pins the official exact binary bytes.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root 'Assets\Fonts'
New-Item -ItemType Directory -Path $target -Force | Out-Null
$commit = 'a4f7cf94edfb9d7ffbdfc4841de276358bd7e0f2'
$files = @(
  @{ Name = 'SourceHanSansCN-Regular.otf'; Sha = '5e6605995d5cbc432c4273f566a5f2b8c376c682' },
  @{ Name = 'SourceHanSansCN-Medium.otf'; Sha = 'd870b3549d2cbd61ab24ec4f496cacb2cf1963fc' }
)
foreach ($file in $files) {
  $destination = Join-Path $target $file.Name
  if (Test-Path $destination) {
    $hash = (& git hash-object -- $destination).Trim()
    if ($LASTEXITCODE -eq 0 -and $hash -eq $file.Sha) { continue }
  }
  $download = "$destination.download"
  try {
    $url = "https://raw.githubusercontent.com/adobe-fonts/source-han-sans/$commit/SubsetOTF/CN/$($file.Name)"
    Invoke-WebRequest -Uri $url -OutFile $download -MaximumRedirection 4
    $actual = (& git hash-object -- $download).Trim()
    if ($LASTEXITCODE -ne 0 -or $actual -ne $file.Sha) {
      throw "Official font integrity check failed: $($file.Name)"
    }
    Move-Item -Path $download -Destination $destination -Force
  } finally {
    if (Test-Path $download) { Remove-Item $download -Force }
  }
}
