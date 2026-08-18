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
    public void BuilderAddsNoHostRuntimePrerequisite()
    {
        var builder = ReadInstallerFile(
            "New-OokiGraderHostInstallMedia.ps1");

        Assert.Contains("bundledPrerequisites = @()", builder);
        Assert.Contains("$requiredHostInstallerFiles", builder);
        Assert.Contains("'Install-OokiGraderPeerTrust.ps1'", builder);
        Assert.Contains(
            "releaseInventory.hostInstallEntryPoint",
            builder,
            StringComparison.Ordinal);
        Assert.Contains(
            "releaseInventory.minimumTechnicianPowerShell",
            builder,
            StringComparison.Ordinal);
        Assert.Contains(
            "requiredApplicationPowerShell = '5.1 (included with Windows)'",
            builder,
            StringComparison.Ordinal);
        Assert.DoesNotContain("PowerShellMsiPath", builder);
        Assert.DoesNotContain("pinnedPowerShell", builder);
        Assert.DoesNotContain("Prerequisites/", builder);
    }

    [Fact]
    public void BootstrapUsesBuiltInWindowsPowerShell()
    {
        var bootstrap = ReadMediaTemplate(
            "Install-OokiGrader-Host.ps1.template");

        Assert.StartsWith("#requires -Version 5.1", bootstrap);
        Assert.Contains(
            "System32\\WindowsPowerShell\\v1.0\\powershell.exe",
            bootstrap,
            StringComparison.Ordinal);
        Assert.Contains("& $runtimePowerShell @onSiteArguments", bootstrap);
        Assert.Contains("'-ExecutionPolicy'", bootstrap);
        Assert.Contains("'Bypass'", bootstrap);
        Assert.DoesNotContain("pwsh.exe", bootstrap);
        Assert.DoesNotContain("msiexec.exe", bootstrap);
        Assert.DoesNotContain("Prerequisites", bootstrap);
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
        Assert.Contains(
            "powerShellPath = $runtimePowerShell",
            bootstrap,
            StringComparison.Ordinal);
        Assert.Contains(
            "C:\\OokiGrader-Setup\\logs",
            readme,
            StringComparison.Ordinal);
        Assert.DoesNotContain("MSI", readme, StringComparison.Ordinal);
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
            "Existing content will not be overwritten",
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
