"""Keep the native psmux harness fail-closed before its first process launch."""

from __future__ import annotations

import pathlib
import re


SCRIPT = pathlib.Path(__file__).parents[1] / "Invoke-PsmuxSmoke.ps1"
REPOSITORY = pathlib.Path(__file__).parents[3]
EXAMPLE_PROGRAM = REPOSITORY / "examples" / "LibTmux.Examples" / "Program.cs"
PACKAGE_PROGRAM = REPOSITORY / "tests" / "LibTmux.PackageConsumer" / "Program.cs"


def source() -> str:
    """Return the checked-in PowerShell harness."""
    return SCRIPT.read_text(encoding="utf-8")


def test_first_psmux_launch_uses_verified_binary_and_isolated_data() -> None:
    """Unsafe release binaries must be rejected without ever executing them."""
    script = source()
    first_launch = script.index("$bannerResult = Invoke-CapturedNative")

    assert script.index("Get-FileHash", 0, first_launch) >= 0
    assert script.index("$ExpectedSha256 -ine $supportedSha256", 0, first_launch) >= 0
    assert "54e5c54db259218348f966b5d0d0b5153fdef6350074855ea9ce627d20537b0d" in script
    assert script.index("$binaryText.Contains('66cf613')", 0, first_launch) >= 0
    assert script.index("$binaryText.Contains('2026-08-18')", 0, first_launch) >= 0
    assert script.index("$env:PSMUX_DATA_DIR = $DataDirectory", 0, first_launch) >= 0
    assert script.index("$env:PSMUX_NO_WARM = '1'", 0, first_launch) >= 0
    assert '"set -g warm off`n"' in script
    assert "PsmuxPath must be local to a Windows drive" in script
    assert script.index("WslDotnetPath must be", 0, first_launch) >= 0
    assert script.index("WslRepository must be", 0, first_launch) >= 0
    assert script.index("$wslDotnetResult = Invoke-CapturedNative", 0, first_launch) >= 0


def test_native_processes_are_bounded_and_return_explicit_status() -> None:
    """PowerShell must not infer success from a missing or stale exit code."""
    script = source()

    assert "$LASTEXITCODE" not in script
    assert "@(&" not in script
    assert script.count("Invoke-CapturedNative `") == 17
    assert "Start-Process" not in script
    assert "$startInfo = [Diagnostics.ProcessStartInfo]::new()" in script
    assert "$startInfo.WorkingDirectory = $CaptureDirectory" in script
    assert "$startInfo.UseShellExecute = $false" in script
    assert "$startInfo.RedirectStandardOutput = $true" in script
    assert "$startInfo.RedirectStandardError = $true" in script
    assert "$process.StandardOutput.BaseStream.CopyToAsync($stdoutBuffer)" in script
    assert "$process.StandardError.BaseStream.CopyToAsync($stderrBuffer)" in script
    assert "$null = $stdoutTask.GetAwaiter().GetResult()" in script
    assert "$null = $stderrTask.GetAwaiter().GetResult()" in script
    assert "$startInfo.StandardOutputEncoding" not in script
    assert "$startInfo.StandardErrorEncoding" not in script
    assert "ConvertFrom-Utf8Bytes $stdoutBuffer.ToArray() $Leg 'stdout'" in script
    assert "ConvertFrom-Utf8Bytes $stderrBuffer.ToArray() $Leg 'stderr'" in script
    assert '"$Leg emitted invalid UTF-8 on $StreamName."' in script
    assert "$process.WaitForExit($TimeoutSeconds * 1000)" in script
    assert "$process.Kill()" in script
    assert "$process.WaitForExit(5000)" in script
    assert "$exitCode = $process.ExitCode" in script
    assert "ExitCode = $exitCode" in script
    assert "ConvertTo-NativeArgument" in script
    assert "$KillDescendantsOnTimeout = $false" in script
    assert "@('/PID', $process.Id.ToString(), '/T', '/F')" in script
    assert '"$Leg process-tree termination"' in script


def test_wsl_workloads_have_inner_process_group_deadlines() -> None:
    """Killing wsl.exe alone must not leave its adopted Linux workload alive."""
    script = source()

    assert "'timeout (GNU coreutils) '*" in script
    assert "LC_ALL=C /usr/bin/timeout --version" in script
    assert 'exec /usr/bin/timeout --signal=KILL "$deadline" "$@"' in script
    assert "--kill-after" not in script
    assert "--foreground" not in script
    assert script.count("$wslTimeoutScript,") == 5
    assert script.count("'libtmux-timeout'") == 5
    assert script.count("'15s'") == 2
    assert script.count("'240s'") == 1
    assert script.count("'120s'") == 2
    assert '"WSL $Kind path $operation" `\n        $true' in script
    assert "'WSL .NET smoke' `\n            $true" in script
    assert "'WSL example' `\n            $true" in script
    assert "'WSL packed consumer' `\n            $true" in script


def test_wsl_dotnet_is_validated_once_and_invoked_by_absolute_path() -> None:
    """Non-login WSL calls must not search an incomplete inherited PATH."""
    script = source()

    assert "[string] $WslDotnetPath" in script
    assert "candidate=$1" in script
    assert 'resolved=$(/usr/bin/readlink -f -- "$candidate") || exit 126' in script
    assert '[ -f "$resolved" ] && [ -x "$resolved" ]' in script
    assert '"$resolved" --info >/dev/null || exit 126' in script
    assert 'runtimes=$("$resolved" --list-runtimes) || exit 126' in script
    assert 'Microsoft.NETCore.App $framework_major.' in script
    assert '"required Microsoft.NETCore.App $framework_major runtime is unavailable"' in script
    assert "$wslRuntimeMajor = '8'" in script
    assert "$wslRuntimeMajor = '10'" in script
    assert "$WslDotnetPath, $wslRuntimeMajor" in script
    assert "'WSL .NET executable validation' `\n            $true" in script
    assert "$wslRepositoryPath = Convert-ToWslPath" in script
    assert script.count("'--cd', $wslRepositoryPath") == 4
    assert "$resolvedWslDotnetPath = $wslDotnetResult.Output[0]" in script
    assert "$resolvedWslDotnetPath.StartsWith('/')" in script
    assert script.count("$resolvedWslDotnetPath,") == 3
    assert re.search(r"^\s*\$WslDotnetPath\s*=", script, re.IGNORECASE | re.MULTILINE) is None
    assert script.count("'/bin/sh'") == 7
    assert script.count("'/usr/bin/env'") == 3
    assert script.count("/usr/bin/wslpath") == 1
    assert "command -v mise" not in script
    assert "'-lc'" not in script


def test_wsl_repository_is_canonicalized_for_both_input_forms() -> None:
    """Linux paths must bypass wslpath while every repository becomes a real directory."""
    script = source()

    assert "$pathMode = 'windows'" in script
    assert "if ($Path.StartsWith('/'))" in script
    assert "$pathMode = 'linux'" in script
    assert 'candidate=$(/usr/bin/wslpath -u -- "$candidate") || exit 126' in script
    assert 'resolved=$(/usr/bin/readlink -f -- "$candidate") || exit 126' in script
    assert '[ -d "$resolved" ] || exit 126' in script
    assert "$pathRequirement = 'directory'" in script
    assert "$wslPathResolutionScript," in script
    assert "$pathMode, $pathRequirement, $Path" in script
    assert 'return $resolvedPath' in script


def test_harness_owns_every_file_and_exact_cleanup_target() -> None:
    """The harness must not overwrite or broadly clean caller data."""
    script = source()

    assert "DataDirectory must not exist" in script
    assert "absolute path on a local Windows drive" in script
    assert script.count("[IO.DriveType]::Fixed") == 2
    assert r"if ($Path -notmatch '^[A-Za-z]:\\')" in script
    assert "GetEnvironmentVariables('Process').GetEnumerator()" in script
    assert "StartsWith('PSMUX_', [System.StringComparison]::OrdinalIgnoreCase)" in script
    assert "'PSMUX_NO_WARM'" in script
    assert "'LIBTMUX_PSMUX_'" in script
    assert "New-Item -ItemType Directory -Path $DataDirectory | Out-Null" in script
    assert "New-Item -ItemType Directory -Path $DataDirectory -Force" not in script
    assert "kill-server" not in script
    creation = script.index("new-session")
    identity = script.index("$createdSessionId = $Matches[1]", creation)
    creation_error = script.index("if ($creationExitCode -ne 0)", identity)
    cleanup = script.index("if ($creationAttempted)", creation_error)

    assert script.index("$creationAttempted = $true", 0, creation) >= 0
    assert identity < creation_error
    assert "@('kill-session', '-t', $createdSessionId)" in script[cleanup:]
    assert "-L $NamespaceName" not in script[cleanup:]
    assert "kill-session -t $SessionName" not in script
    assert "$currentIdentity[0] -cne $createdIdentity" in script[cleanup:]
    assert "refusing to kill its replacement" in script[cleanup:]
    assert "no exact identity; retaining its data directory" in script[cleanup:]
    assert "Test-ExactProcessAlive $createdPid $createdProcessStartTicks" in script[cleanup:]
    assert "[long] $StartTimeUtcTicks" in script
    assert "-Recurse" in script[cleanup:]
    assert "The owned data directory still contains a live psmux registry" in script
    assert "Remove-Item -LiteralPath $DataDirectory -Recurse -Force" in script
    assert "Refusing to remove a configuration file this run did not create" in script


def test_each_runtime_leg_requires_one_non_skipped_test() -> None:
    """A stale class name or a skipped smoke must not produce a green harness."""
    script = source()

    assert script.count("-failSkips") == 2
    assert script.count("-result-xml") == 2
    assert "Assert-OnePassingTest $nativeResultPath 'Native Windows .NET'" in script
    assert "Assert-OnePassingTest $wslResultPath 'WSL .NET'" in script
    assert "'Native Windows .NET smoke' `\n            $exitCode `\n            $nativeTestResult.Error" in script
    assert "'WSL .NET smoke' `\n                $exitCode `\n                $wslTestResult.Error" in script
    assert "[int] $assemblies[0].total -ne 1" in script
    assert "[int] $assemblies[0].passed -ne 1" in script
    assert "[string] $ExampleAssembly" in script
    assert "[string] $PackageConsumerAssembly" in script
    assert "[ValidateSet('net8.0', 'net10.0')]" in script
    assert "$assembly.Directory.Name -cne $TargetFramework" in script
    assert "'Native Windows example'" in script
    assert "'Native Windows packed consumer'" in script
    assert "'WSL example'" in script
    assert "'WSL packed consumer'" in script
    assert script.count("Assert-QueryProgram") == 5


def test_nonzero_process_details_are_bounded_and_preserve_exit_status() -> None:
    """A useful stderr excerpt must not displace the primary native failure."""
    script = source()

    assert "'[\\p{Cc}\\p{Cf}]+'" in script
    assert "[regex]::Replace($detail, '\\s+', ' ').Trim()" in script
    assert "$detail.Length -gt 512" in script
    assert "'...' + $detail.Substring($detail.Length - 509)" in script
    assert 'return "$message stderr: $detail"' in script
    assert '"$Leg exited $exitCode. $($_.Exception.Message)"' in script
    timeout = script.index("$timedOut = $true")
    termination = script.index("$process.WaitForExit(5000)", timeout)
    drain = script.index("$null = $stdoutTask.GetAwaiter().GetResult()", termination)
    report = script.index("$timeoutMessage =", drain)
    assert timeout < termination < drain < report
    for result in (
        "$nativeExampleResult.Error",
        "$nativePackageResult.Error",
        "$wslTestResult.Error",
        "$wslExampleResult.Error",
        "$wslPackageResult.Error",
    ):
        assert result in script


def test_windows_powershell_source_is_ascii_and_restores_process_state() -> None:
    """Windows PowerShell 5.1 must parse the same UTF-8 fixture without a BOM."""
    script = source()

    script.encode("ascii")
    assert "[Text.Encoding]::UTF8.GetString($fixtureBytes)" in script
    assert "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)" in script
    assert "$fixtureByteList" in script
    assert '"Write-Output \'$expectedText\'"' not in script
    assert "$fixtureCommand," in script
    assert "foreach ($name in $savedNames)" in script
    assert "[Console]::OutputEncoding = $savedOutputEncoding" in script
    assert "LibTmux.PsmuxSmokeCleanupFailure" in script


def test_native_managed_psmux_producers_force_strict_utf8() -> None:
    """Redirected Windows apps must not inherit the unrepresentable OEM code page."""
    for path, invocation in (
        (EXAMPLE_PROGRAM, "await Snippets.Psmux.QueryPsmux();"),
        (PACKAGE_PROGRAM, "return await RunPsmuxAsync();"),
    ):
        program = path.read_text(encoding="utf-8")
        branch = program.index('if (args is ["--psmux"])')
        encoding = program.index(
            "Console.OutputEncoding = new UTF8Encoding(false, true);",
            branch,
        )
        output = program.index(invocation, encoding)

        assert branch < encoding < output
