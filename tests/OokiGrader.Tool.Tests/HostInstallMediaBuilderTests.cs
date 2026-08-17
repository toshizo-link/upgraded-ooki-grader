namespace OokiGrader.Tool.Tests;

public sealed class HostInstallMediaBuilderTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void BuilderCreatesVerifiedImmutableAtomicMedia()
    {
        var builder = ReadInstallerFile(
            "New-OokiGraderHostInstallMedia.ps1");

        Assert.Contains(
            "Assert-OokiReleasePackage",
            builder,
            StringComparison.Ordinal);
        Assert.Contains(
            "New-DeterministicPackageArchive",
            builder,
            StringComparison.Ordinal);
        Assert.Contains(
            "$entry.LastWriteTime = $EntryTimestamp",
            builder,
            StringComparison.Ordinal);
        Assert.Contains(
            "[IO.Compression.CompressionLevel]::NoCompression",
            builder,
            StringComparison.Ordinal);
        Assert.Contains(
            "[Array]::Sort($relativePaths, [StringComparer]::Ordinal)",
            builder,
            StringComparison.Ordinal);
        Assert.Contains(
            "[IO.Compression.ZipFile]::ExtractToDirectory",
            builder,
            StringComparison.Ordinal);
        Assert.Contains(
            ".staging-$mediaName-",
            builder,
            StringComparison.Ordinal);
        Assert.Contains(
            "[IO.Directory]::Move($staging, $target)",
            builder,
            StringComparison.Ordinal);
        Assert.Contains(
            "outputs are immutable",
            builder,
            StringComparison.Ordinal);
        Assert.Contains(
            "media-inventory.json",
            builder,
            StringComparison.Ordinal);
        Assert.Contains(
            "aggregateChecksums = 'checksums.txt'",
            builder,
            StringComparison.Ordinal);
        Assert.Contains(
            "Assert-MediaChecksums -Root $staging",
            builder,
            StringComparison.Ordinal);
        Assert.Contains(
            "Assert-OokiDisjointPaths",
            builder,
            StringComparison.Ordinal);
        Assert.Contains(
            "-File -Force -Recurse",
            builder,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Compress-Archive",
            builder,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BuilderPinsAndCopiesTheMicrosoftPowerShellMsi()
    {
        var builder = ReadInstallerFile(
            "New-OokiGraderHostInstallMedia.ps1");

        Assert.Contains(
            "$pinnedPowerShellVersion = '7.6.4'",
            builder,
            StringComparison.Ordinal);
        Assert.Contains(
            "d11942df52fd12470169797abfa4781d9480efdc81000ba4fa55a5b921ed8dd0",
            builder,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "Get-AuthenticodeSignature",
            builder,
            StringComparison.Ordinal);
        Assert.Contains(
            "O=Microsoft Corporation",
            builder,
            StringComparison.Ordinal);
        Assert.Contains(
            "[IO.File]::Copy($powerShellMsi, $stagedPowerShellMsi, $false)",
            builder,
            StringComparison.Ordinal);
        Assert.Contains(
            "Prerequisites/$pinnedPowerShellMsiName",
            builder,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BootstrapUpgradesAbsentOldOrNonX64PowerShell()
    {
        var bootstrap = ReadMediaTemplate(
            "Install-OokiGrader-Host.ps1.template");

        Assert.Contains(
            "function Test-CompatiblePowerShell7",
            bootstrap,
            StringComparison.Ordinal);
        Assert.Contains(
            "[version] $info.Version -ge [version] '7.4'",
            bootstrap,
            StringComparison.Ordinal);
        Assert.Contains(
            "[Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture"
                + " -eq [Runtime.InteropServices.Architecture]::X64",
            bootstrap,
            StringComparison.Ordinal);
        Assert.Contains(
            "[bool] $info.IsX64",
            bootstrap,
            StringComparison.Ordinal);
        Assert.Contains(
            "$pwsh = Find-CompatiblePowerShell7",
            bootstrap,
            StringComparison.Ordinal);
        Assert.Contains(
            "if ($null -eq $pwsh)",
            bootstrap,
            StringComparison.Ordinal);
        Assert.Contains(
            "'/passive'",
            bootstrap,
            StringComparison.Ordinal);
        Assert.Contains(
            "'/norestart'",
            bootstrap,
            StringComparison.Ordinal);
        Assert.Contains(
            "$msiProcess.ExitCode -notin @(0, 3010)",
            bootstrap,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BootstrapPersistsTranscriptAndStructuredResult()
    {
        var bootstrap = ReadMediaTemplate(
            "Install-OokiGrader-Host.ps1.template");
        var readme = ReadMediaTemplate("00-README-ja.txt.template");

        Assert.Contains(
            "Start-Transcript -Path $transcriptPath",
            bootstrap,
            StringComparison.Ordinal);
        Assert.Contains(
            "schema = 'ooki-host-install-run/v1'",
            bootstrap,
            StringComparison.Ordinal);
        Assert.Contains(
            "Write-DiagnosticResult -State 'failed'",
            bootstrap,
            StringComparison.Ordinal);
        Assert.Contains(
            "Write-DiagnosticResult -State 'installed-and-verified'",
            bootstrap,
            StringComparison.Ordinal);
        Assert.Contains("'/l*v'", bootstrap, StringComparison.Ordinal);
        Assert.Contains(
            "powerShellMsiLogPath = $msiLogPath",
            bootstrap,
            StringComparison.Ordinal);
        Assert.Contains(
            "C:\\OokiGrader-Setup\\logs",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "-powershell-msi.log",
            readme,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LauncherUsesAServiceIndependentAdministratorCheck()
    {
        var launcher = ReadMediaTemplate(
            "01-Install-OokiGrader-Host.cmd.template");

        Assert.Contains(
            "WindowsPrincipal",
            launcher,
            StringComparison.Ordinal);
        Assert.Contains(
            "WindowsBuiltInRole]::Administrator",
            launcher,
            StringComparison.Ordinal);
        Assert.DoesNotContain("net session", launcher, StringComparison.Ordinal);
    }

    [Fact]
    public void BootstrapStagesValidatesAndExactlyReusesPackages()
    {
        var bootstrap = ReadMediaTemplate(
            "Install-OokiGrader-Host.ps1.template");

        Assert.Contains(
            "Assert-ReleasePackageIntegrity",
            bootstrap,
            StringComparison.Ordinal);
        Assert.Contains(
            ".staging-$packageName-",
            bootstrap,
            StringComparison.Ordinal);
        Assert.Contains(
            "Test-ExactDirectoryMatch",
            bootstrap,
            StringComparison.Ordinal);
        Assert.Contains(
            "Assert-NoReparsePoints -Root $packageRoot",
            bootstrap,
            StringComparison.Ordinal);
        Assert.Contains(
            "[IO.Directory]::Move($stagedPackageRoot, $packageRoot)",
            bootstrap,
            StringComparison.Ordinal);
        Assert.Contains(
            "[IO.Directory]::Delete($extractRoot, $true)",
            bootstrap,
            StringComparison.Ordinal);
        Assert.Contains(
            "既存内容を上書きしません",
            bootstrap,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MediaReadmeLabelsCapacityAsRecommendations()
    {
        var readme = ReadMediaTemplate("00-README-ja.txt.template");

        Assert.Contains(
            "推奨構成（満たさなくてもインストールは続行します）",
            readme,
            StringComparison.Ordinal);
        Assert.Contains("Windows 11 Pro", readme, StringComparison.Ordinal);
        Assert.Contains("16 GiB", readme, StringComparison.Ordinal);
        Assert.Contains("165 GiB", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedWindowsArtifactsAreBytePreservingInGit()
    {
        var attributes = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            ".gitattributes"));

        Assert.Contains(
            "output/windows/releases/** -text",
            attributes,
            StringComparison.Ordinal);
        Assert.Contains(
            "output/windows/host-install-media/** -text",
            attributes,
            StringComparison.Ordinal);
        Assert.Contains(
            "installer/HostInstallMedia/** -text",
            attributes,
            StringComparison.Ordinal);
    }

    private static string ReadInstallerFile(string name) =>
        File.ReadAllText(Path.Combine(RepositoryRoot, "installer", name));

    private static string ReadMediaTemplate(string name) =>
        File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "installer",
            "HostInstallMedia",
            name));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "global.json"))
                && Directory.Exists(
                    Path.Combine(current.FullName, "installer")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException(
            "The repository root could not be located.");
    }
}
