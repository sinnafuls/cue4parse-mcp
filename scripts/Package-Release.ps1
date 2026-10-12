[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'src/Cue4ParseMcp/Cue4ParseMcp.csproj'
$output = Join-Path $repo 'artifacts/releases'
$archiveName = "cue4parse-mcp-$Version-windows-x64.zip"
$archive = Join-Path $output $archiveName
$work = Join-Path ([IO.Path]::GetTempPath()) ("cue4parse-release-" + [Guid]::NewGuid())
$bundle = Join-Path $work 'bundle'

# A tag must have reviewed release notes; do not publish an undocumented version.
$changelog = Get-Content (Join-Path $repo 'CHANGELOG.md') -Raw
$section = [regex]::Match($changelog, "(?ms)^## $([regex]::Escape($Version))(?:[^\r\n]*)\r?\n(.*?)(?=^## |\z)")
if (-not $section.Success) { throw "CHANGELOG.md has no section for $Version." }
if (Test-Path $archive) { throw "Release archive already exists: $archive" }
New-Item -ItemType Directory -Path $output, $bundle -Force | Out-Null

try {
    & dotnet publish $project -c Release -r win-x64 --self-contained true `
        -p:Version=$Version -p:PublishSingleFile=false -p:PublishTrimmed=false -o $bundle
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
    if (-not (Test-Path (Join-Path $bundle 'CUE4Parse-Natives.dll'))) {
        throw 'CUE4Parse-Natives.dll is missing. Install CMake and the MSVC C++ build tools.'
    }

    # Only allowlisted repository files are copied; games.json and keys never enter the bundle.
    foreach ($file in @('README.md', 'CHANGELOG.md', 'games.example.json')) {
        Copy-Item (Join-Path $repo $file) $bundle
    }
    New-Item -ItemType Directory -Path (Join-Path $bundle 'docs') -Force | Out-Null
    Copy-Item (Join-Path $repo 'docs/INSTALL.md') (Join-Path $bundle 'docs/INSTALL.md')
    $licenses = Join-Path $bundle 'licenses'
    New-Item -ItemType Directory -Path $licenses -Force | Out-Null
    Copy-Item (Join-Path $repo 'lib/CUE4Parse/LICENSE') (Join-Path $licenses 'CUE4Parse-LICENSE.txt')
    foreach ($dependency in @('acl', 'acl/external/rtm', 'acl/external/sjson-cpp')) {
        $source = Join-Path $repo "lib/CUE4Parse/CUE4Parse-Natives/ACL/external/$dependency/LICENSE"
        Copy-Item $source (Join-Path $licenses ($dependency.Replace('/', '-') + '-LICENSE.txt'))
    }

    # Preserve license/notice files provided by the resolved NuGet packages.
    $assets = Get-Content (Join-Path $repo 'src/Cue4ParseMcp/obj/project.assets.json') -Raw | ConvertFrom-Json
    $packageFolders = @($assets.packageFolders.PSObject.Properties.Name)
    foreach ($entry in $assets.libraries.PSObject.Properties) {
        if ($entry.Value.type -ne 'package') { continue }
        foreach ($file in $entry.Value.files) {
            if ([IO.Path]::GetFileName($file) -notmatch '^(LICENSE|COPYING|NOTICE|THIRD[-_ ]PARTY[-_ ]NOTICES)(\.[^/]+)?$') { continue }
            foreach ($folder in $packageFolders) {
                $source = Join-Path $folder "$($entry.Value.path)/$file"
                if (-not (Test-Path $source)) { continue }
                $target = Join-Path $licenses "$($entry.Name)/$file"
                New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
                Copy-Item $source $target
                break
            }
        }
    }
    $runtimeConfig = Get-Content (Join-Path $bundle 'Cue4ParseMcp.runtimeconfig.json') -Raw | ConvertFrom-Json
    $runtime = $runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -EQ 'Microsoft.NETCore.App'
    if (-not $runtime) { throw 'Published application is not self-contained.' }
    foreach ($file in @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) {
        $source = $packageFolders | ForEach-Object { Join-Path $_ "microsoft.netcore.app.runtime.win-x64/$($runtime.version)/$file" } |
            Where-Object { Test-Path $_ } | Select-Object -First 1
        if (-not $source) { throw "Missing .NET runtime notice: $file" }
        $target = Join-Path $licenses "dotnet-runtime/$file"
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Copy-Item $source $target
    }

    Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $archive
    $extracted = Join-Path $work 'extracted'
    Expand-Archive -Path $archive -DestinationPath $extracted
    $exe = Join-Path $extracted 'Cue4ParseMcp.exe'
    & $exe help
    if ($LASTEXITCODE -ne 0) { throw "Packaged CLI failed with exit code $LASTEXITCODE." }

    # Start the actual extracted stdio server. Initialization proves the bundled template parses
    # and the published reflection-based MCP tool host starts without trimming damage.
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.WorkingDirectory = $extracted
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add('--config')
    $start.ArgumentList.Add((Join-Path $extracted 'games.example.json'))
    $start.Environment.Remove('CUE4PARSE_MCP_CONFIG') | Out-Null
    $process = [Diagnostics.Process]::Start($start)
    $stderr = $process.StandardError.ReadToEndAsync()
    try {
        $process.StandardInput.AutoFlush = $true
        $process.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"release-smoke","version":"1.0"}}}')
        do {
            $line = $process.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(30)).GetAwaiter().GetResult()
            if ($null -eq $line) { throw "Packaged server exited before initialize: $($stderr.GetAwaiter().GetResult())" }
            $reply = $line | ConvertFrom-Json
        } while ($reply.id -ne 1)
        if ($reply.error -or $reply.result.serverInfo.name -ne 'cue4parse-mcp' -or $reply.result.serverInfo.version -ne $Version) {
            throw "Unexpected packaged server identity/initialize result: $line"
        }
        $process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(10000)) { throw 'Packaged server did not shut down after stdin closed.' }
        if ($process.ExitCode -ne 0) { throw "Packaged server failed: $($stderr.GetAwaiter().GetResult())" }
    }
    finally {
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }

    $hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -Path "$archive.sha256" -Value "$hash  $archiveName" -Encoding ascii
    $notes = "Windows x64 self-contained ZIP: extract all files, copy games.example.json to games.json, and follow docs/INSTALL.md. No separate .NET installation is needed.`n`n" +
        "Known limitation: ImageSharp 3.1.12 retains the security advisories documented in README.md; NuGet warnings are not suppressed.`n`n" + $section.Groups[1].Value.Trim()
    Set-Content -Path (Join-Path $output 'RELEASE-NOTES.md') -Value $notes -Encoding utf8
    Write-Host "Verified release bundle: $archive"
    Write-Host "SHA256: $hash"
}
finally {
    # This is a unique directory created by this invocation, never a user's export/config folder.
    Remove-Item $work -Recurse -Force
}
