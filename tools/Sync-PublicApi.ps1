<#
.SYNOPSIS
    Brings each library's PublicAPI.Unshipped.txt in line with its code.

.DESCRIPTION
    Builds the solution, then adds every symbol the public API analyzer reports as missing (RS0016) to the project's
    PublicAPI.Unshipped.txt, and removes every symbol it reports as gone (RS0017) from PublicAPI.Unshipped.txt or
    PublicAPI.Shipped.txt. Review the result with git diff: an unexpected entry means a member became public by mistake.

.EXAMPLE
    ./tools/Sync-PublicApi.ps1
#>
[CmdletBinding()]
param(
    # The solution or project to build
    [string] $Path = (Join-Path $PSScriptRoot '..' 'Naqel.slnx')
)

$ErrorActionPreference = 'Stop'

$output = & dotnet build $Path -nologo -clp:NoSummary 2>&1 | Out-String
$pattern = "error (?<code>RS0016|RS0017): Symbol '(?<symbol>.+?)' is (?:not )?part of the declared (?:public )?API.*?\[(?<project>[^\]]+?\.csproj)"
$found = [regex]::Matches($output, $pattern)

$changes = @{}
foreach ($match in $found) {
    $project = $match.Groups['project'].Value
    if (-not $changes.ContainsKey($project)) {
        $changes[$project] = @{ Add = [System.Collections.Generic.HashSet[string]]::new(); Remove = [System.Collections.Generic.HashSet[string]]::new() }
    }
    $set = if ($match.Groups['code'].Value -eq 'RS0016') { 'Add' } else { 'Remove' }
    [void] $changes[$project][$set].Add($match.Groups['symbol'].Value)
}

if ($changes.Count -eq 0) {
    if ($LASTEXITCODE -ne 0) {
        Write-Host $output
        throw 'The build failed for a reason other than the public API lists'
    }
    Write-Host 'The public API lists are up to date.'
    return
}

foreach ($project in $changes.Keys) {
    $directory = Split-Path $project
    $unshipped = Join-Path $directory 'PublicAPI.Unshipped.txt'
    $shipped = Join-Path $directory 'PublicAPI.Shipped.txt'
    $add = $changes[$project].Add
    $remove = $changes[$project].Remove

    foreach ($file in @($unshipped, $shipped)) {
        $lines = [System.Collections.Generic.List[string]]::new([string[]]@(Get-Content $file))
        $before = $lines.Count
        [void] $lines.RemoveAll({ param($line) $remove.Contains($line) })
        if ($file -eq $unshipped) {
            [void] $lines.RemoveAll({ param($line) $line -eq '' })
            foreach ($symbol in $add) {
                if (-not $lines.Contains($symbol)) { $lines.Add($symbol) }
            }
        }
        if ($lines.Count -ne $before -or $file -eq $unshipped) {
            # CRLF like the rest of the repository
            [System.IO.File]::WriteAllText($file, (($lines -join "`r`n") + "`r`n"))
        }
    }
    Write-Host ("{0}: +{1} -{2}" -f (Split-Path $directory -Leaf), $add.Count, $remove.Count)
}

Write-Host 'Run the script again until it reports no changes; some entries only show once others are in place.'
