namespace OokiGrader.Tool.Tests;

public sealed class HostUpdateMediaBuilderTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void BuilderUsesVerifiedPortableInstallerFoundationAndAtomicOutput()
    {
        var script = ReadInstallerFile("New-OokiGraderHostUpdateMedia.ps1");

        Assert.Contains("New-OokiGraderHostInstallMedia.ps1", script);
        Assert.Contains("Update-OokiGrader-Host.ps1", script);
        Assert.Contains("ooki-host-update-media/v1", script);
        Assert.Contains("checksums.txt", script);
        Assert.Contains("[IO.Directory]::Move($staging, $target)", script);
        Assert.Contains("outputs are immutable", script);
    }

    [Fact]
    public void BootstrapDiscoversAndReverifiesInstalledBackup()
    {
        var phase = ReadUpdateTemplate("Update-Phase.ps1.template");

        Assert.Contains("operations\\installation.json", phase);
        Assert.Contains("Backup.DestinationRoot", phase);
        Assert.Contains("manifest.json", phase);
        Assert.Contains("applicationVersion", phase);
        Assert.Contains("destinationEncryptionConfirmed", phase);
        Assert.Contains("AddHours(-24)", phase);
        Assert.Contains("maintenanceMode", phase);
        Assert.Contains("$LASTEXITCODE -notin @(0, 3)", phase);
        Assert.DoesNotContain("$LASTEXITCODE -notin @(0, 2)", phase);
        Assert.Contains("restoreOrMigrationMarkerPresent", phase);
        Assert.Contains("VerifiedBackupManifestSha256", phase);
        Assert.Contains("UPDATE WITHOUT BACKUP", phase);
        Assert.Contains("ProceedWithoutBackup", phase);
        Assert.Contains("'-UpdateConfirmed'", phase);
        Assert.DoesNotContain("-Confirm:$false", phase);
        Assert.Contains("Update-OokiGraderOnHost.ps1", phase);
        Assert.Contains("Type $requiredConfirmation", phase);
    }

    [Fact]
    public void LauncherUsesBuiltInWindowsPowerShellAndAdministratorCheck()
    {
        var launcher = ReadUpdateTemplate(
            "01-Update-OokiGrader-Host.cmd.template");

        Assert.Contains("WindowsPowerShell\\v1.0\\powershell.exe", launcher);
        Assert.Contains("WindowsPrincipal", launcher);
        Assert.Contains("Administrator", launcher);
        Assert.DoesNotContain("pwsh.exe", launcher);
    }

    [Fact]
    public void ReleaseIncludesFriendlyOnHostUpdateWrapper()
    {
        var releaseBuilder = ReadInstallerFile(
            "New-OokiGraderReleasePackage.ps1");

        Assert.Contains("'Update-OokiGraderOnHost.ps1'", releaseBuilder);
    }

    private static string ReadInstallerFile(string name) =>
        File.ReadAllText(Path.Combine(RepositoryRoot, "installer", name));

    private static string ReadUpdateTemplate(string name) =>
        File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "installer",
            "HostUpdateMedia",
            name));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "global.json"))
                && Directory.Exists(Path.Combine(current.FullName, "installer")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }
}
