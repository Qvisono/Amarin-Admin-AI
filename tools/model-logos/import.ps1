[CmdletBinding()]
param([switch]$Check)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$project = Join-Path $PSScriptRoot 'VectorImporter/VectorImporter.csproj'
$arguments = @('run', '--project', $project, '--configuration', 'Release', '--', $repositoryRoot)
if ($Check) { $arguments += '--check' }
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "Model logo import failed (exit $LASTEXITCODE)." }
