<#
.SYNOPSIS
  Build hma-mcp and install it to %LOCALAPPDATA%\hma-mcp (or -Destination), even while an MCP client is running it.

.DESCRIPTION
  Windows won't overwrite a running .exe or its loaded .dll files, but it does allow renaming them. This script
  publishes to a staging folder, renames any locked files in the destination out of the way (*.old-<time>),
  copies the new build in, and removes old copies that are no longer locked. Sessions already running keep the
  old version; new sessions get the new one.

.EXAMPLE
  ./scripts/install.ps1
  ./scripts/install.ps1 -Destination D:\tools\hma-mcp
#>
param(
   [string]$Destination = (Join-Path $env:LOCALAPPDATA 'hma-mcp'),
   [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$staging = Join-Path ([IO.Path]::GetTempPath()) ("hma-mcp-staging-" + [Guid]::NewGuid().ToString('N'))

Write-Host "Publishing to $staging"
dotnet publish (Join-Path $repo 'src/HmaMcp') -c $Configuration -o $staging --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

New-Item -ItemType Directory -Force $Destination | Out-Null
$stamp = Get-Date -Format 'yyyyMMddHHmmss'
$moved = 0; $copied = 0
foreach ($file in Get-ChildItem $staging -Recurse -File) {
   $relative = $file.FullName.Substring($staging.Length).TrimStart('\', '/')
   $target = Join-Path $Destination $relative
   New-Item -ItemType Directory -Force (Split-Path -Parent $target) | Out-Null
   if (Test-Path $target) {
      # identical files can stay, locked or not
      if ((Get-FileHash $target).Hash -eq (Get-FileHash $file.FullName).Hash) { continue }
      try {
         Copy-Item $file.FullName $target -Force
         $copied++
         continue
      } catch {
         Rename-Item $target "$target.old-$stamp"
         $moved++
      }
   }
   Copy-Item $file.FullName $target -Force
   $copied++
}

# clean up old copies a previous run left behind, where nothing holds them any more
foreach ($old in Get-ChildItem $Destination -Recurse -File -Filter '*.old-*') {
   try { Remove-Item $old.FullName -Force } catch { }
}
Remove-Item $staging -Recurse -Force

Write-Host "Installed to $Destination ($copied file(s) updated, $moved locked file(s) set aside)."
if ($moved -gt 0) { Write-Host "Running MCP sessions keep the old version until they restart." }
