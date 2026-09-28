$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'CodexUsage.cs'
$output = Join-Path $PSScriptRoot 'CodexUsage.exe'
Add-Type -Path $source `
    -OutputAssembly $output `
    -OutputType WindowsApplication `
    -ReferencedAssemblies @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll')
Write-Host "Built $output"
