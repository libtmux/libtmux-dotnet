[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PsmuxPath,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string] $ExpectedSha256,

    [Parameter(Mandatory)]
    [string] $DataDirectory,

    [Parameter(Mandatory)]
    [ValidatePattern('^(?!default$)(?!.*__)[a-z0-9_-]{16,64}$')]
    [string] $NamespaceName,

    [Parameter(Mandatory)]
    [string] $DotnetPath,

    [Parameter(Mandatory)]
    [string] $TestAssembly,

    [Parameter(Mandatory)]
    [string] $ExampleAssembly,

    [Parameter(Mandatory)]
    [string] $PackageConsumerAssembly,

    [Parameter(Mandatory)]
    [ValidateSet('net8.0', 'net10.0')]
    [string] $TargetFramework,

    [string] $WslDistribution,

    [string] $WslRepository,

    [string] $WslDotnetPath,

    [switch] $RunWslSmoke,

    [ValidatePattern('^(?!.*__)[A-Za-z0-9_-]+$')]
    [string] $SessionName = 'smoke'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$savedOutputEncoding = [Console]::OutputEncoding
$supportedSha256 = '54e5c54db259218348f966b5d0d0b5153fdef6350074855ea9ce627d20537b0d'
$wslTimeoutScript = @'
case "$(LC_ALL=C /usr/bin/timeout --version 2>/dev/null)" in
    'timeout (GNU coreutils) '*) ;;
    *) exit 125 ;;
esac
deadline=$1
shift
exec /usr/bin/timeout --signal=KILL "$deadline" "$@"
'@
$wslDotnetValidationScript = @'
set -eu
candidate=$1
framework_major=$2
case "$candidate" in
    /*) ;;
    *) exit 126 ;;
esac
resolved=$(/usr/bin/readlink -f -- "$candidate") || exit 126
case "$resolved" in
    /*) ;;
    *) exit 126 ;;
esac
[ -f "$resolved" ] && [ -x "$resolved" ] || exit 126
"$resolved" --info >/dev/null || exit 126
runtimes=$("$resolved" --list-runtimes) || exit 126
case "
$runtimes
" in
    *"
Microsoft.NETCore.App $framework_major."*) ;;
    *)
        printf '%s\n' \
            "required Microsoft.NETCore.App $framework_major runtime is unavailable" >&2
        exit 126
        ;;
esac
printf '%s\n' "$resolved"
'@
$wslPathResolutionScript = @'
set -eu
mode=$1
requirement=$2
candidate=$3
case "$mode" in
    windows)
        candidate=$(/usr/bin/wslpath -u -- "$candidate") || exit 126
        ;;
    linux) ;;
    *) exit 126 ;;
esac
case "$candidate" in
    /*) ;;
    *) exit 126 ;;
esac
if [ "$requirement" = directory ]; then
    resolved=$(/usr/bin/readlink -f -- "$candidate") || exit 126
    case "$resolved" in
        /*) ;;
        *) exit 126 ;;
    esac
    [ -d "$resolved" ] || exit 126
    candidate=$resolved
elif [ "$requirement" != path ]; then
    exit 126
fi
printf '%s\n' "$candidate"
'@
if ($ExpectedSha256 -ine $supportedSha256) {
    throw 'ExpectedSha256 must match the exact audited psmux client build.'
}
$ExpectedSha256 = $supportedSha256

function Get-IsolatedDataDirectory([string] $Path) {
    if ($Path -notmatch '^[A-Za-z]:\\') {
        throw 'DataDirectory must be an absolute path on a local Windows drive.'
    }

    $full = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($full)
    $drive = [IO.DriveInfo]::new($root)
    if ($drive.DriveType -ne [IO.DriveType]::Fixed) {
        throw 'DataDirectory must be on a fixed local Windows drive.'
    }
    if ([string]::Equals(
            $full.TrimEnd([char[]] @([IO.Path]::DirectorySeparatorChar)),
            $root.TrimEnd([char[]] @([IO.Path]::DirectorySeparatorChar)),
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'DataDirectory must not be a filesystem root.'
    }

    $relative = $full.Substring($root.Length)
    foreach ($segment in $relative.Split(
            [char[]] @([IO.Path]::DirectorySeparatorChar),
            [System.StringSplitOptions]::RemoveEmptyEntries)) {
        if ($segment -eq '.' -or $segment -eq '..' -or
                $segment.EndsWith(' ') -or $segment.EndsWith('.')) {
            throw 'DataDirectory contains an ambiguous Windows path segment.'
        }
        if ($segment.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
                $segment.ToCharArray().Where({ [char]::IsControl($_) }).Count -gt 0) {
            throw 'DataDirectory contains an invalid Windows path segment.'
        }

        $device = $segment.Split('.')[0]
        if ($device -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$') {
            throw 'DataDirectory contains a reserved Windows device name.'
        }
    }

    return $full.TrimEnd([char[]] @([IO.Path]::DirectorySeparatorChar))
}

function Assert-OnePassingTest([string] $ResultPath, [string] $Leg) {
    if (-not (Test-Path -LiteralPath $ResultPath -PathType Leaf)) {
        throw "$Leg did not write an xUnit result file."
    }

    [xml] $result = Get-Content -LiteralPath $ResultPath -Raw
    $assemblies = @($result.assemblies.assembly)
    if ($assemblies.Count -ne 1 -or
            [int] $assemblies[0].total -ne 1 -or
            [int] $assemblies[0].passed -ne 1 -or
            [int] $assemblies[0].failed -ne 0 -or
            [int] $assemblies[0].skipped -ne 0 -or
            [int] $assemblies[0].'not-run' -ne 0 -or
            [int] $assemblies[0].errors -ne 0) {
        throw "$Leg did not run exactly one passing, non-skipped psmux smoke test."
    }
}

function Get-BoundedNativeErrorDetail([string] $ErrorText) {
    if ([string]::IsNullOrWhiteSpace($ErrorText)) {
        return
    }

    $detail = [regex]::Replace(
        $ErrorText.Trim(),
        '[\p{Cc}\p{Cf}]+',
        ' ')
    $detail = [regex]::Replace($detail, '\s+', ' ').Trim()
    if ($detail.Length -eq 0) {
        return
    }
    if ($detail.Length -gt 512) {
        $detail = '...' + $detail.Substring($detail.Length - 509)
    }
    return $detail
}

function Get-NativeExitMessage(
        [string] $Leg,
        [int] $ExitCode,
        [string] $ErrorText) {
    $message = "$Leg exited $ExitCode."
    $detail = Get-BoundedNativeErrorDetail $ErrorText
    if (-not $detail) {
        return $message
    }
    return "$message stderr: $detail"
}

function Assert-QueryProgram(
        [string[]] $Output,
        [int] $ExitCode,
        [string] $ErrorText,
        [string] $ExpectedText,
        [string] $Leg) {
    if ($ExitCode -ne 0) {
        throw (Get-NativeExitMessage $Leg $ExitCode $ErrorText)
    }
    if ($Output.Where({ $_.Contains($ExpectedText) }).Count -eq 0) {
        throw "$Leg did not report the UTF-8 fixture text."
    }
}

function ConvertTo-NativeArgument([string] $Argument) {
    if ($null -eq $Argument) {
        throw 'Native command arguments must not be null.'
    }
    if ($Argument.Length -gt 0 -and $Argument -notmatch '[\s"]') {
        return $Argument
    }

    $quoted = [Text.StringBuilder]::new()
    $null = $quoted.Append('"')
    $backslashes = 0
    foreach ($character in $Argument.ToCharArray()) {
        if ($character -eq [char] 0x5c) {
            $backslashes++
            continue
        }
        if ($character -eq [char] 0x22) {
            $null = $quoted.Append([char] 0x5c, (2 * $backslashes) + 1)
            $null = $quoted.Append($character)
            $backslashes = 0
            continue
        }
        if ($backslashes -gt 0) {
            $null = $quoted.Append([char] 0x5c, $backslashes)
            $backslashes = 0
        }
        $null = $quoted.Append($character)
    }
    if ($backslashes -gt 0) {
        $null = $quoted.Append([char] 0x5c, 2 * $backslashes)
    }
    $null = $quoted.Append('"')
    return $quoted.ToString()
}

function ConvertFrom-NativeOutput([string] $Text) {
    if ([string]::IsNullOrEmpty($Text)) {
        return
    }

    $normalized = $Text.Replace("`r`n", "`n").Replace("`r", "`n")
    if ($normalized.EndsWith("`n")) {
        $normalized = $normalized.Substring(0, $normalized.Length - 1)
    }
    if ($normalized.Length -gt 0) {
        $normalized.Split([char] 0x0a)
    }
}

function ConvertFrom-Utf8Bytes(
        [byte[]] $Bytes,
        [string] $Leg,
        [string] $StreamName) {
    $utf8 = [Text.UTF8Encoding]::new($false, $true)
    try {
        return $utf8.GetString($Bytes)
    }
    catch [Text.DecoderFallbackException] {
        throw [IO.InvalidDataException]::new(
            "$Leg emitted invalid UTF-8 on $StreamName.",
            $_.Exception)
    }
}

function Invoke-CapturedNative(
        [string] $FilePath,
        [string[]] $ArgumentList,
        [string] $CaptureDirectory,
        [int] $TimeoutSeconds,
        [string] $Leg,
        [bool] $KillDescendantsOnTimeout = $false) {
    if ($TimeoutSeconds -le 0) {
        throw 'Native command timeouts must be positive.'
    }

    $commandLine = [string]::Join(
        ' ',
        @($ArgumentList | ForEach-Object { ConvertTo-NativeArgument $_ }))
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.Arguments = $commandLine
    $startInfo.WorkingDirectory = $CaptureDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $stdoutBuffer = [IO.MemoryStream]::new()
    $stderrBuffer = [IO.MemoryStream]::new()
    $timedOut = $false
    try {
        if (-not $process.Start()) {
            throw "$Leg could not start."
        }
        $stdoutTask = $process.StandardOutput.BaseStream.CopyToAsync($stdoutBuffer)
        $stderrTask = $process.StandardError.BaseStream.CopyToAsync($stderrBuffer)
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $timedOut = $true
            if (-not $process.HasExited) {
                if ($KillDescendantsOnTimeout) {
                    $taskkill = [IO.Path]::Combine(
                        $env:SystemRoot,
                        'System32',
                        'taskkill.exe')
                    $treeKill = Invoke-CapturedNative `
                        $taskkill `
                        @('/PID', $process.Id.ToString(), '/T', '/F') `
                        $CaptureDirectory `
                        10 `
                        "$Leg process-tree termination"
                    if ($treeKill.ExitCode -ne 0 -and -not $process.HasExited) {
                        throw (Get-NativeExitMessage `
                            "$Leg process-tree termination" `
                            $treeKill.ExitCode `
                            $treeKill.Error)
                    }
                }
                else {
                    $process.Kill()
                }
            }
            if (-not $process.WaitForExit(5000)) {
                throw "$Leg exceeded its timeout and its exact process survived termination."
            }
        }
        $process.WaitForExit()

        $null = $stdoutTask.GetAwaiter().GetResult()
        $null = $stderrTask.GetAwaiter().GetResult()
        $exitCode = $process.ExitCode
        try {
            $stdout = ConvertFrom-Utf8Bytes $stdoutBuffer.ToArray() $Leg 'stdout'
            $stderr = ConvertFrom-Utf8Bytes $stderrBuffer.ToArray() $Leg 'stderr'
        }
        catch [IO.InvalidDataException] {
            if ($timedOut) {
                throw [IO.InvalidDataException]::new(
                    "$Leg exceeded its $TimeoutSeconds-second timeout. " +
                        $_.Exception.Message,
                    $_.Exception)
            }
            elseif ($exitCode -ne 0) {
                throw [IO.InvalidDataException]::new(
                    "$Leg exited $exitCode. $($_.Exception.Message)",
                    $_.Exception)
            }
            throw
        }
        if ($timedOut) {
            $timeoutMessage = "$Leg exceeded its $TimeoutSeconds-second timeout."
            $detail = Get-BoundedNativeErrorDetail $stderr
            if ($detail) {
                $timeoutMessage += " stderr: $detail"
            }
            throw $timeoutMessage
        }
        return [pscustomobject] @{
            Output = @(ConvertFrom-NativeOutput $stdout)
            Error = $stderr
            ExitCode = $exitCode
        }
    }
    finally {
        $process.Dispose()
        $stdoutBuffer.Dispose()
        $stderrBuffer.Dispose()
    }
}

function Test-ExactProcessAlive(
        [int] $ProcessId,
        [long] $StartTimeUtcTicks) {
    $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (-not $process) {
        return $false
    }

    return $process.StartTime.ToUniversalTime().Ticks -eq $StartTimeUtcTicks
}

function Convert-ToWslPath(
        [string] $Path,
        [string] $Distribution,
        [string] $Kind,
        [string] $CaptureDirectory) {
    $pathMode = 'windows'
    if ($Path.StartsWith('/')) {
        $pathMode = 'linux'
    }
    $pathRequirement = 'path'
    $operation = 'translation'
    if ($Kind -ceq 'repository') {
        $pathRequirement = 'directory'
        $operation = 'resolution'
    }
    $translation = Invoke-CapturedNative `
        'wsl.exe' `
        @(
            '--distribution', $Distribution,
            '--exec', '/bin/sh', '-eu', '-c',
            $wslTimeoutScript,
            'libtmux-timeout', '15s',
            '/bin/sh', '-eu', '-c',
            $wslPathResolutionScript,
            'libtmux-path-resolution',
            $pathMode, $pathRequirement, $Path) `
        $CaptureDirectory `
        30 `
        "WSL $Kind path $operation" `
        $true
    if ($translation.ExitCode -ne 0) {
        throw (Get-NativeExitMessage `
            "WSL $Kind path $operation" `
            $translation.ExitCode `
            $translation.Error)
    }
    if ($translation.Output.Count -ne 1 -or
            [string]::IsNullOrWhiteSpace($translation.Output[0])) {
        throw "WSL could not translate the $Kind path."
    }

    $resolvedPath = $translation.Output[0].Trim()
    if (-not $resolvedPath.StartsWith('/') -or
            $resolvedPath.ToCharArray().Where(
                { [char]::IsControl($_) }).Count -ne 0) {
        throw "WSL returned an invalid $Kind path."
    }

    return $resolvedPath
}

$expectedBanner = @(
    'tmux 3.3.8'
    'psmux 3.3.8 (66cf613 2026-08-18)'
)
$fixtureBytes = [byte[]] (
    0x68, 0xc3, 0xa9, 0x6c, 0x6c, 0x6f, 0x2d, 0xe9, 0x9b, 0xaa,
    0x2d, 0xf0, 0x9f, 0x98, 0x80)
$expectedText = [Text.Encoding]::UTF8.GetString($fixtureBytes)
$fixtureByteList = [string]::Join(
    ',',
    @($fixtureBytes | ForEach-Object { $_.ToString() }))
$fixtureCommand =
    '[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);' +
    '[Console]::WriteLine([Text.Encoding]::UTF8.GetString([byte[]](' +
    $fixtureByteList + ')))'
$ownedEnvironment = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
foreach ($name in @(
    'LIBTMUX_PSMUX_BINARY'
    'LIBTMUX_PSMUX_EXPECTED_TEXT'
    'LIBTMUX_PSMUX_NAMESPACE'
    'LIBTMUX_PSMUX_SHA256'
    'LIBTMUX_PSMUX_SMOKE'
    'PSMUX_DATA_DIR'
    'PSMUX_NO_WARM'
    'TMUX'
    'TMUX_PANE'
)) {
    $null = $ownedEnvironment.Add($name)
}
foreach ($entry in [Environment]::GetEnvironmentVariables('Process').GetEnumerator()) {
    $name = [string] $entry.Key
    if ($name.StartsWith('PSMUX_', [System.StringComparison]::OrdinalIgnoreCase) -or
            $name.StartsWith(
                'LIBTMUX_PSMUX_',
                [System.StringComparison]::OrdinalIgnoreCase)) {
        $null = $ownedEnvironment.Add($name)
    }
}
$savedEnvironment = @{}
$savedNames = [System.Collections.Generic.List[string]]::new()

$creationAttempted = $false
$createdIdentity = $null
$createdPid = $null
$createdProcessStartTicks = $null
$createdSessionId = $null
$dataDirectoryCreated = $false
$exitCode = 1
$configPath = $null
$configCreated = $false
$nativeResultPath = $null
$wslResultPath = $null
$wslRepositoryPath = $null
$resolvedWslDotnetPath = $null
$primaryFailure = $null
$cleanupFailures = [System.Collections.Generic.List[System.Exception]]::new()
try {
    [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
    foreach ($name in $ownedEnvironment) {
        $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        $savedNames.Add($name)
        [Environment]::SetEnvironmentVariable($name, $null, 'Process')
    }

    if ($RunWslSmoke -and ([string]::IsNullOrWhiteSpace($WslDistribution) -or
            [string]::IsNullOrWhiteSpace($WslRepository) -or
            [string]::IsNullOrWhiteSpace($WslDotnetPath))) {
        throw 'RunWslSmoke requires WslDistribution, WslRepository, and WslDotnetPath.'
    }
    if ($RunWslSmoke -and
            ($WslDotnetPath -cne $WslDotnetPath.Trim() -or
            -not $WslDotnetPath.StartsWith('/') -or
            $WslDotnetPath.ToCharArray().Where(
                { [char]::IsControl($_) }).Count -ne 0)) {
        throw 'WslDotnetPath must be a control-free absolute Linux path.'
    }
    if ($RunWslSmoke -and
            ($WslRepository -cne $WslRepository.Trim() -or
            ($WslRepository[0] -ne '/' -and
                $WslRepository -notmatch '^(?:[A-Za-z]:\\|\\\\[^\\]+\\[^\\]+)') -or
            $WslRepository.ToCharArray().Where(
                { [char]::IsControl($_) }).Count -ne 0)) {
        throw 'WslRepository must be a control-free absolute Linux or Windows path.'
    }

    foreach ($pathInput in @(
            $PsmuxPath,
            $DotnetPath,
            $TestAssembly,
            $ExampleAssembly,
            $PackageConsumerAssembly)) {
        if ($pathInput -notmatch '^(?:[A-Za-z]:\\|\\\\[^\\]+\\[^\\]+)') {
            throw 'Every executable and assembly path must be an absolute Windows path.'
        }
    }
    if ($PsmuxPath -notmatch '^[A-Za-z]:\\') {
        throw 'PsmuxPath must be local to a Windows drive.'
    }

    $psmuxFile = Get-Item -LiteralPath $PsmuxPath -ErrorAction Stop
    if ($psmuxFile.PSIsContainer -or $psmuxFile.Extension -ine '.exe') {
        throw 'PsmuxPath must identify an existing .exe file.'
    }
    if (($psmuxFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'PsmuxPath must not be a symbolic link or reparse point.'
    }
    $psmuxDrive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($psmuxFile.FullName))
    if ($psmuxDrive.DriveType -ne [IO.DriveType]::Fixed) {
        throw 'PsmuxPath must be on a fixed local Windows drive.'
    }

    $dotnetFile = Get-Item -LiteralPath $DotnetPath -ErrorAction Stop
    if ($dotnetFile.PSIsContainer -or $dotnetFile.Extension -ine '.exe') {
        throw 'DotnetPath must identify an existing dotnet.exe file.'
    }

    $testFile = Get-Item -LiteralPath $TestAssembly -ErrorAction Stop
    if ($testFile.PSIsContainer -or $testFile.Extension -ine '.dll') {
        throw 'TestAssembly must identify the built unit-test DLL.'
    }
    $exampleFile = Get-Item -LiteralPath $ExampleAssembly -ErrorAction Stop
    if ($exampleFile.PSIsContainer -or $exampleFile.Extension -ine '.dll') {
        throw 'ExampleAssembly must identify the built examples DLL.'
    }
    $packageFile = Get-Item -LiteralPath $PackageConsumerAssembly -ErrorAction Stop
    if ($packageFile.PSIsContainer -or $packageFile.Extension -ine '.dll') {
        throw 'PackageConsumerAssembly must identify the built package-consumer DLL.'
    }
    foreach ($assembly in @($testFile, $exampleFile, $packageFile)) {
        if ($assembly.Directory.Name -cne $TargetFramework) {
            throw "Every smoke assembly must come from the $TargetFramework output directory."
        }
    }

    $actualHash = (Get-FileHash -LiteralPath $psmuxFile.FullName -Algorithm SHA256).Hash
    if ($actualHash -ine $ExpectedSha256) {
        throw "psmux SHA-256 mismatch: expected $ExpectedSha256, got $actualHash."
    }

    $binaryText = [Text.Encoding]::ASCII.GetString(
        [IO.File]::ReadAllBytes($psmuxFile.FullName))
    if (-not $binaryText.Contains('66cf613') -or
            -not $binaryText.Contains('2026-08-18')) {
        throw 'psmux does not contain the audited build markers.'
    }

    $DataDirectory = Get-IsolatedDataDirectory $DataDirectory
    if (Test-Path -LiteralPath $DataDirectory) {
        throw 'DataDirectory must not exist; choose a fresh high-entropy path for this run.'
    }
    New-Item -ItemType Directory -Path $DataDirectory | Out-Null
    $dataDirectoryCreated = $true
    $env:PSMUX_DATA_DIR = $DataDirectory
    $env:PSMUX_NO_WARM = '1'

    if ($RunWslSmoke) {
        $wslRepositoryPath = Convert-ToWslPath `
            $WslRepository $WslDistribution 'repository' $DataDirectory
        $wslRuntimeMajor = '8'
        if ($TargetFramework -ceq 'net10.0') {
            $wslRuntimeMajor = '10'
        }
        $wslDotnetResult = Invoke-CapturedNative `
            'wsl.exe' `
            @(
                '--distribution', $WslDistribution,
                '--cd', $wslRepositoryPath,
                '--exec', '/bin/sh', '-eu', '-c',
                $wslTimeoutScript,
                'libtmux-timeout', '15s',
                '/bin/sh', '-eu', '-c',
                $wslDotnetValidationScript,
                'libtmux-dotnet-validation',
                $WslDotnetPath, $wslRuntimeMajor) `
            $DataDirectory `
            30 `
            'WSL .NET executable validation' `
            $true
        if ($wslDotnetResult.ExitCode -ne 0) {
            throw (Get-NativeExitMessage `
                'WSL .NET executable validation' `
                $wslDotnetResult.ExitCode `
                $wslDotnetResult.Error)
        }
        if ($wslDotnetResult.Output.Count -ne 1 -or
                [string]::IsNullOrWhiteSpace($wslDotnetResult.Output[0])) {
            throw 'WSL .NET executable validation returned no canonical path.'
        }
        $resolvedWslDotnetPath = $wslDotnetResult.Output[0]
        if ($resolvedWslDotnetPath -cne $resolvedWslDotnetPath.Trim() -or
                -not $resolvedWslDotnetPath.StartsWith('/') -or
                $resolvedWslDotnetPath.ToCharArray().Where(
                    { [char]::IsControl($_) }).Count -ne 0) {
            throw 'WSL .NET executable validation returned an invalid canonical path.'
        }
    }

    $bannerResult = Invoke-CapturedNative `
        $psmuxFile.FullName `
        @('-V') `
        $DataDirectory `
        30 `
        'psmux version query'
    $banner = $bannerResult.Output
    if ($bannerResult.ExitCode -ne 0) {
        throw (Get-NativeExitMessage `
            'psmux version query' `
            $bannerResult.ExitCode `
            $bannerResult.Error)
    }
    if ([string]::Join("`n", $banner) -cne
                [string]::Join("`n", $expectedBanner)) {
        throw "psmux reported an unaudited banner: $([string]::Join(' | ', $banner))"
    }

    $configPath = Join-Path $DataDirectory 'libtmux-smoke.conf'
    [IO.File]::WriteAllText(
        $configPath,
        "set -g warm off`n",
        [Text.UTF8Encoding]::new($false))
    $configCreated = $true

    $existingResult = Invoke-CapturedNative `
        $psmuxFile.FullName `
        @('-L', $NamespaceName, 'list-sessions', '-F', '#{session_name}') `
        $DataDirectory `
        30 `
        'psmux isolated namespace inspection'
    $existing = $existingResult.Output
    if ($existingResult.ExitCode -ne 0) {
        throw (Get-NativeExitMessage `
            'psmux isolated namespace inspection' `
            $existingResult.ExitCode `
            $existingResult.Error)
    }
    if ($existing.Count -ne 0) {
        throw "The isolated namespace is not empty: $([string]::Join(', ', $existing))"
    }

    $creationAttempted = $true
    $creationResult = Invoke-CapturedNative `
        $psmuxFile.FullName `
        @(
            '-f', $configPath,
            '-L', $NamespaceName,
            'new-session',
            '-d',
            '-s', $SessionName,
            '--', 'powershell.exe', '-NoLogo', '-NoProfile', '-NoExit') `
        $DataDirectory `
        30 `
        'psmux session creation'
    $creationExitCode = $creationResult.ExitCode

    $identityFormat = "#{pid}:#{start_time}`t#{session_id}`t#{session_name}"
    $escapedSessionName = [regex]::Escape($SessionName)
    $identityPattern = '^[1-9][0-9]*:[1-9][0-9]*\t(\$[0-9]+)\t' +
        $escapedSessionName + '$'
    for ($attempt = 0; $attempt -lt 50 -and -not $createdSessionId; $attempt++) {
        $identityResult = Invoke-CapturedNative `
            $psmuxFile.FullName `
            @(
                '-L', $NamespaceName,
                'display-message',
                '-p',
                '-t', $SessionName,
                $identityFormat) `
            $DataDirectory `
            30 `
            'psmux session identity query'
        $identity = $identityResult.Output
        if ($identityResult.ExitCode -eq 0 -and $identity.Count -eq 1 -and
                $identity[0] -match $identityPattern) {
            $candidatePid = [int] ($identity[0].Split(':')[0])
            $candidateProcess = Get-Process -Id $candidatePid -ErrorAction Stop
            $createdIdentity = $identity[0]
            $createdPid = $candidatePid
            $createdProcessStartTicks = $candidateProcess.StartTime.ToUniversalTime().Ticks
            $createdSessionId = $Matches[1]
            break
        }

        Start-Sleep -Milliseconds 100
    }

    if ($creationExitCode -ne 0) {
        throw (Get-NativeExitMessage `
            'psmux new-session after the creation attempt' `
            $creationExitCode `
            $creationResult.Error)
    }
    if (-not $createdSessionId) {
        throw 'psmux created no session with an exact verifiable identity.'
    }

    $sendResult = Invoke-CapturedNative `
        $psmuxFile.FullName `
        @(
            '-L', $NamespaceName,
            'send-keys',
            '-t', "${SessionName}:0.0",
            $fixtureCommand,
            'Enter') `
        $DataDirectory `
        30 `
        'psmux fixture input'
    if ($sendResult.ExitCode -ne 0) {
        throw (Get-NativeExitMessage `
            'psmux fixture input' `
            $sendResult.ExitCode `
            $sendResult.Error)
    }

    $ready = $false
    for ($attempt = 0; $attempt -lt 50 -and -not $ready; $attempt++) {
        $captureResult = Invoke-CapturedNative `
            $psmuxFile.FullName `
            @(
                '-L', $NamespaceName,
                'capture-pane',
                '-p',
                '-t', "${SessionName}:0.0") `
            $DataDirectory `
            30 `
            'psmux pane capture'
        $capture = $captureResult.Output
        if ($captureResult.ExitCode -ne 0) {
            throw (Get-NativeExitMessage `
                'psmux pane capture' `
                $captureResult.ExitCode `
                $captureResult.Error)
        }
        $ready = $capture.Where({ $_ -clike "*$expectedText*" }).Count -gt 0
        if (-not $ready) {
            Start-Sleep -Milliseconds 100
        }
    }
    if (-not $ready) {
        throw 'The UTF-8 fixture did not become visible in the smoke pane.'
    }

    $env:LIBTMUX_PSMUX_BINARY = $psmuxFile.FullName
    $env:LIBTMUX_PSMUX_EXPECTED_TEXT = $expectedText
    $env:LIBTMUX_PSMUX_NAMESPACE = $NamespaceName
    $env:LIBTMUX_PSMUX_SHA256 = $ExpectedSha256.ToLowerInvariant()
    $env:LIBTMUX_PSMUX_SMOKE = '1'

    $nativeResultPath = Join-Path $DataDirectory 'libtmux-native-result.xml'
    $nativeTestResult = Invoke-CapturedNative `
        $dotnetFile.FullName `
        @(
            $testFile.FullName,
            '-noColor',
            '-noLogo',
            '-failSkips',
            '-result-xml', $nativeResultPath,
            '-class', 'LibTmux.UnitTests.Connection.PsmuxProcessSmokeTests') `
        $DataDirectory `
        180 `
        'Native Windows .NET smoke' `
        $true
    $exitCode = $nativeTestResult.ExitCode
    if ($exitCode -ne 0) {
        throw (Get-NativeExitMessage `
            'Native Windows .NET smoke' `
            $exitCode `
            $nativeTestResult.Error)
    }
    Assert-OnePassingTest $nativeResultPath 'Native Windows .NET'
    $nativeExampleResult = Invoke-CapturedNative `
        $dotnetFile.FullName `
        @($exampleFile.FullName, '--psmux') `
        $DataDirectory `
        60 `
        'Native Windows example' `
        $true
    Assert-QueryProgram `
        $nativeExampleResult.Output `
        $nativeExampleResult.ExitCode `
        $nativeExampleResult.Error `
        $expectedText `
        'Native Windows example'
    $nativePackageResult = Invoke-CapturedNative `
        $dotnetFile.FullName `
        @($packageFile.FullName, '--psmux') `
        $DataDirectory `
        60 `
        'Native Windows packed consumer' `
        $true
    Assert-QueryProgram `
        $nativePackageResult.Output `
        $nativePackageResult.ExitCode `
        $nativePackageResult.Error `
        $expectedText `
        'Native Windows packed consumer'

    if ($exitCode -eq 0 -and $RunWslSmoke) {
        $wslPsmuxPath = Convert-ToWslPath `
            $psmuxFile.FullName $WslDistribution 'audited psmux' $DataDirectory
        $wslDataDirectory = Convert-ToWslPath `
            $DataDirectory $WslDistribution 'isolated data-directory' $DataDirectory
        $wslTestAssembly = Convert-ToWslPath `
            $testFile.FullName $WslDistribution 'unit-test assembly' $DataDirectory
        $wslExampleAssembly = Convert-ToWslPath `
            $exampleFile.FullName $WslDistribution 'example assembly' $DataDirectory
        $wslPackageAssembly = Convert-ToWslPath `
            $packageFile.FullName $WslDistribution 'package-consumer assembly' $DataDirectory
        $wslResultPath = Join-Path $DataDirectory 'libtmux-wsl-result.xml'
        $wslResultArgument = "$($wslDataDirectory.TrimEnd('/'))/libtmux-wsl-result.xml"
        $wslTestResult = Invoke-CapturedNative `
            'wsl.exe' `
            @(
                '--distribution', $WslDistribution,
                '--cd', $wslRepositoryPath,
                '--exec', '/bin/sh', '-eu', '-c',
                $wslTimeoutScript,
                'libtmux-timeout', '240s',
                '/usr/bin/env',
                "LIBTMUX_PSMUX_BINARY=$wslPsmuxPath",
                "LIBTMUX_PSMUX_EXPECTED_TEXT=$expectedText",
                "LIBTMUX_PSMUX_NAMESPACE=$NamespaceName",
                "LIBTMUX_PSMUX_SHA256=$($ExpectedSha256.ToLowerInvariant())",
                'LIBTMUX_PSMUX_SMOKE=1',
                "PSMUX_DATA_DIR=$DataDirectory",
                'WSLENV=PSMUX_DATA_DIR/w',
                $resolvedWslDotnetPath,
                $wslTestAssembly,
                '-noColor',
                '-noLogo',
                '-failSkips',
                '-result-xml', $wslResultArgument,
                '-class', 'LibTmux.UnitTests.Connection.PsmuxProcessSmokeTests') `
            $DataDirectory `
            300 `
            'WSL .NET smoke' `
            $true
        $exitCode = $wslTestResult.ExitCode
        if ($exitCode -ne 0) {
            throw (Get-NativeExitMessage `
                'WSL .NET smoke' `
                $exitCode `
                $wslTestResult.Error)
        }
        Assert-OnePassingTest $wslResultPath 'WSL .NET'
        $wslExampleResult = Invoke-CapturedNative `
            'wsl.exe' `
            @(
                '--distribution', $WslDistribution,
                '--cd', $wslRepositoryPath,
                '--exec', '/bin/sh', '-eu', '-c',
                $wslTimeoutScript,
                'libtmux-timeout', '120s',
                '/usr/bin/env',
                "LIBTMUX_PSMUX_BINARY=$wslPsmuxPath",
                "LIBTMUX_PSMUX_EXPECTED_TEXT=$expectedText",
                "LIBTMUX_PSMUX_NAMESPACE=$NamespaceName",
                "PSMUX_DATA_DIR=$DataDirectory",
                'WSLENV=PSMUX_DATA_DIR/w',
                $resolvedWslDotnetPath,
                $wslExampleAssembly,
                '--psmux') `
            $DataDirectory `
            180 `
            'WSL example' `
            $true
        Assert-QueryProgram `
            $wslExampleResult.Output `
            $wslExampleResult.ExitCode `
            $wslExampleResult.Error `
            $expectedText `
            'WSL example'

        $wslPackageResult = Invoke-CapturedNative `
            'wsl.exe' `
            @(
                '--distribution', $WslDistribution,
                '--cd', $wslRepositoryPath,
                '--exec', '/bin/sh', '-eu', '-c',
                $wslTimeoutScript,
                'libtmux-timeout', '120s',
                '/usr/bin/env',
                "LIBTMUX_PSMUX_BINARY=$wslPsmuxPath",
                "LIBTMUX_PSMUX_EXPECTED_TEXT=$expectedText",
                "LIBTMUX_PSMUX_NAMESPACE=$NamespaceName",
                "PSMUX_DATA_DIR=$DataDirectory",
                'WSLENV=PSMUX_DATA_DIR/w',
                $resolvedWslDotnetPath,
                $wslPackageAssembly,
                '--psmux') `
            $DataDirectory `
            180 `
            'WSL packed consumer' `
            $true
        Assert-QueryProgram `
            $wslPackageResult.Output `
            $wslPackageResult.ExitCode `
            $wslPackageResult.Error `
            $expectedText `
            'WSL packed consumer'
    }
}
catch {
    $primaryFailure = $_
}
finally {
    try {
        if ($creationAttempted) {
            $ownedSidPath = Join-Path $DataDirectory "$NamespaceName`__$SessionName.sid"
            if (-not $createdSessionId) {
                throw 'A creation attempt has no exact identity; retaining its data directory.'
            }
            else {
                # aa26 resolves $N to its complete namespaced registry base;
                # adding -L here would prefix the namespace a second time.
                $currentIdentityResult = Invoke-CapturedNative `
                    $psmuxFile.FullName `
                    @(
                        'display-message',
                        '-p',
                        '-t', $createdSessionId,
                        $identityFormat) `
                    $DataDirectory `
                    30 `
                    'psmux cleanup identity query'
                $currentIdentity = $currentIdentityResult.Output
                if ($currentIdentityResult.ExitCode -ne 0) {
                    if (Test-Path -LiteralPath $ownedSidPath) {
                        throw (Get-NativeExitMessage `
                            'psmux cleanup identity query' `
                            $currentIdentityResult.ExitCode `
                            $currentIdentityResult.Error)
                    }
                }
                elseif ($currentIdentity.Count -ne 1) {
                    if (Test-Path -LiteralPath $ownedSidPath) {
                        throw 'The created session still exists but its identity cannot be verified.'
                    }
                }
                elseif ($currentIdentity[0] -cne $createdIdentity) {
                    throw 'The created session identity changed; refusing to kill its replacement.'
                }
                else {
                    $killResult = Invoke-CapturedNative `
                        $psmuxFile.FullName `
                        @('kill-session', '-t', $createdSessionId) `
                        $DataDirectory `
                        30 `
                        'psmux exact session cleanup'
                    if ($killResult.ExitCode -ne 0) {
                        throw (Get-NativeExitMessage `
                            'psmux exact session cleanup' `
                            $killResult.ExitCode `
                            $killResult.Error)
                    }

                    for ($attempt = 0; $attempt -lt 50 -and
                            (Test-Path -LiteralPath $ownedSidPath); $attempt++) {
                        Start-Sleep -Milliseconds 100
                    }
                    if (Test-Path -LiteralPath $ownedSidPath) {
                        throw 'The exact created session registry survived cleanup.'
                    }
                }

                for ($attempt = 0; $attempt -lt 50 -and
                        (Test-ExactProcessAlive `
                            $createdPid $createdProcessStartTicks); $attempt++) {
                    Start-Sleep -Milliseconds 100
                }
                if (Test-ExactProcessAlive $createdPid $createdProcessStartTicks) {
                    throw 'The exact created session process survived cleanup.'
                }
            }
        }
    }
    catch {
        $cleanupFailures.Add($_.Exception)
    }

    foreach ($ownedPath in @($nativeResultPath, $wslResultPath, $configPath)) {
        try {
            if ($ownedPath -and (Test-Path -LiteralPath $ownedPath -PathType Leaf)) {
                if ($ownedPath -eq $configPath -and -not $configCreated) {
                    throw 'Refusing to remove a configuration file this run did not create.'
                }
                Remove-Item -LiteralPath $ownedPath -Force
            }
        }
        catch {
            $cleanupFailures.Add($_.Exception)
        }
    }

    try {
        if ($dataDirectoryCreated -and $cleanupFailures.Count -eq 0 -and
                (Test-Path -LiteralPath $DataDirectory -PathType Container)) {
            $liveRegistry = @(Get-ChildItem `
                -LiteralPath $DataDirectory `
                -Filter '*.port' `
                -Recurse `
                -ErrorAction Stop)
            if ($liveRegistry.Count -ne 0) {
                throw 'The owned data directory still contains a live psmux registry.'
            }
            Remove-Item -LiteralPath $DataDirectory -Recurse -Force
        }
    }
    catch {
        $cleanupFailures.Add($_.Exception)
    }

    try {
        foreach ($name in $savedNames) {
            [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
        }
    }
    catch {
        $cleanupFailures.Add($_.Exception)
    }

    try {
        [Console]::OutputEncoding = $savedOutputEncoding
    }
    catch {
        $cleanupFailures.Add($_.Exception)
    }
}

if ($primaryFailure) {
    if ($cleanupFailures.Count -gt 0) {
        $primaryFailure.Exception.Data['LibTmux.PsmuxSmokeCleanupFailure'] =
            [System.AggregateException]::new($cleanupFailures)
    }
    throw $primaryFailure
}
if ($cleanupFailures.Count -gt 0) {
    throw [System.AggregateException]::new(
        'The psmux smoke cleanup failed.',
        $cleanupFailures)
}

exit $exitCode
