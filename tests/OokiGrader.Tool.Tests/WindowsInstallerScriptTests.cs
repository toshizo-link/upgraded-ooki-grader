using System.Management.Automation.Language;

namespace OokiGrader.Tool.Tests;

public sealed class WindowsInstallerScriptTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void EveryPowerShellSourceParsesWithoutErrors()
    {
        var installerRoot = Path.Combine(RepositoryRoot, "installer");
        var paths = Directory
            .EnumerateFiles(
                installerRoot,
                "*",
                SearchOption.TopDirectoryOnly)
            .Where(path =>
                string.Equals(
                    Path.GetExtension(path),
                    ".ps1",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    Path.GetExtension(path),
                    ".psm1",
                    StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(paths);

        foreach (var path in paths)
        {
            Parser.ParseFile(
                path,
                out _,
                out var errors);

            Assert.True(
                errors.Length == 0,
                $"{Path.GetFileName(path)} failed PowerShell parsing:"
                + Environment.NewLine
                + string.Join(
                    Environment.NewLine,
                    errors.Select(error =>
                        $"{error.Extent.StartLineNumber}:"
                        + $"{error.Extent.StartColumnNumber} "
                        + error.Message)));
        }
    }

    [Fact]
    public void EveryTechnicianScriptAcceptsWhatIf()
    {
        foreach (var path in Directory.EnumerateFiles(
            Path.Combine(RepositoryRoot, "installer"),
            "*.ps1",
            SearchOption.TopDirectoryOnly))
        {
            Assert.Contains(
                "SupportsShouldProcess",
                File.ReadAllText(path),
                StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Install-OokiGrader.ps1")]
    [InlineData("Upgrade-OokiGrader.ps1")]
    [InlineData("Repair-OokiGrader.ps1")]
    [InlineData("Restore-OokiGrader.ps1")]
    [InlineData("Uninstall-OokiGrader.ps1")]
    [InlineData("New-OokiGraderCertificate.ps1")]
    [InlineData("Install-OokiGraderPeerTrust.ps1")]
    [InlineData("New-OokiGraderReleasePackage.ps1")]
    [InlineData("New-OokiGraderWindowsInstaller.ps1")]
    public void MutatingScriptsSupportWhatIfAndGuardTheirActions(
        string fileName)
    {
        var script = ReadInstallerFile(fileName);

        Assert.Contains(
            "SupportsShouldProcess",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "ShouldProcess",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Set-StrictMode -Version Latest",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void InstallConfiguresServiceAclFirewallAndExternalGates()
    {
        var install = ReadInstallerFile("Install-OokiGrader.ps1");
        var module = ReadInstallerFile("OokiGrader.Windows.psm1");

        Assert.Contains(
            "Set-OokiWindowsService",
            install,
            StringComparison.Ordinal);
        Assert.Contains(
            "Set-OokiDataAcl",
            install,
            StringComparison.Ordinal);
        Assert.Contains(
            "Set-OokiFirewallRule",
            install,
            StringComparison.Ordinal);
        Assert.Contains(
            "Assert-OokiReleasePackage",
            install,
            StringComparison.Ordinal);
        Assert.Contains(
            "Install-OokiHostCertificate",
            install,
            StringComparison.Ordinal);
        Assert.Contains(
            "Write-OokiInstallationManifest",
            install,
            StringComparison.Ordinal);
        Assert.Contains(
            "Set-OokiInstallAcl",
            install,
            StringComparison.Ordinal);
        Assert.Contains(
            "configuration') 'appsettings.Production.json'",
            install,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Join-Path $versionRoot 'appsettings.Production.json'",
            install,
            StringComparison.Ordinal);
        Assert.Contains(
            "$sourceFiles.Count -ne $destinationFiles.Count",
            module,
            StringComparison.Ordinal);
        Assert.Contains(
            "contains missing or extra files",
            module,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Start-Process -FilePath $FilePath",
            module,
            StringComparison.Ordinal);
        Assert.Contains(
            "'delayed-auto'",
            module,
            StringComparison.Ordinal);
        Assert.Contains(
            "'restart/10000/restart/60000/none/0'",
            module,
            StringComparison.Ordinal);
        Assert.Contains(
            "-Profile Private",
            module,
            StringComparison.Ordinal);
        Assert.Contains(
            "release signature",
            install,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "peer",
            install,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UpgradeRequiresBackupAndDoesNotRestoreLiveData()
    {
        var script = ReadInstallerFile("Upgrade-OokiGrader.ps1");

        Assert.Contains(
            "[switch] $MaintenanceConfirmed",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "[switch] $OfflineConfirmed",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "[switch] $FreshPreUpgradeBackupConfirmed",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "'backup',",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "'verify',",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "'restore',",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "'plan',",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "'apply'",
            script,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "The service remains stopped",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "-AllowCheckFailure",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$beforeHealth.database.state -ne 'healthy'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$afterHealth.database.state -ne 'healthy'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "restoreOrMigrationMarkerPresent",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreRequiresStoppedServiceAndDelegatesAtomicRestoreToTool()
    {
        var script = ReadInstallerFile("Restore-OokiGrader.ps1");

        Assert.Contains(
            "[switch] $MaintenanceConfirmed",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "[switch] $OfflineConfirmed",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$ConfirmRestore.Equals(",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$service.Status -ne 'Stopped'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Restore must target the exact version recorded",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$installation.expectedSignerThumbprint",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "'execute',",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "'--confirm-restore',",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "rollbackSnapshotCreated",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "restoreOrMigrationMarkerPresent",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Write-OokiWindowsEvent",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Start-Service",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RepairRefusesToStartServiceWithAnOperationMarker()
    {
        var script = ReadInstallerFile("Repair-OokiGrader.ps1");

        Assert.Contains(
            "'restore.in-progress'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "'migration.in-progress'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "will not start the service",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReleasePackagePublishesHostToolInventoryAndOptionalSigning()
    {
        var script = ReadInstallerFile(
            "New-OokiGraderReleasePackage.ps1");

        Assert.Contains(
            "'src/OokiGrader.Host/OokiGrader.Host.csproj'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "'src/OokiGrader.Tool/OokiGrader.Tool.csproj'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "'OokiGrader.Host.exe'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "'OokiGrader.Tool.exe'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "'Restore-OokiGrader.ps1'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "'release-inventory.json'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "'checksums.txt'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "[string] $SigningHook",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$signature.Status -ne 'Valid'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$signingHookState = 'not-requested'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "productionSigningClaimed = ($null -ne $signer)",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"-p:Version=$Version\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "'-p:PublishSingleFile=true'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "'restore',",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "'--runtime',",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "'-p:IncludeNativeLibrariesForSelfExtract=true'",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsSetupTargetBuildsSignedInnoExecutableAndGuardsUpgrade()
    {
        var builder = ReadInstallerFile(
            "New-OokiGraderWindowsInstaller.ps1");
        var setup = ReadInstallerFile("OokiGrader.Setup.iss");

        Assert.Contains(
            "OokiGrader-Setup-$Version-x64.exe",
            builder,
            StringComparison.Ordinal);
        Assert.Contains(
            "Inno Setup 6",
            builder,
            StringComparison.Ordinal);
        Assert.Contains(
            "Assert-OokiReleasePackage",
            builder,
            StringComparison.Ordinal);
        Assert.Contains(
            "ExpectedSignerThumbprint",
            builder,
            StringComparison.Ordinal);
        Assert.Contains(
            "SigningHook",
            builder,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"/Sooki=$signToolCommand\"",
            builder,
            StringComparison.Ordinal);
        Assert.Contains(
            "OutputBaseFilename=OokiGrader-Setup-{#OokiVersion}-x64",
            setup,
            StringComparison.Ordinal);
        Assert.Contains(
            "MinVersion=10.0.22000",
            setup,
            StringComparison.Ordinal);
        Assert.Contains(
            "ArchitecturesInstallIn64BitMode=x64compatible",
            setup,
            StringComparison.Ordinal);
        Assert.Contains(
            "Install-OokiGrader.ps1",
            setup,
            StringComparison.Ordinal);
        Assert.Contains(
            "InstallerManagedApplicationRemoval",
            setup,
            StringComparison.Ordinal);
        Assert.Contains(
            "Upgrade-OokiGrader.ps1",
            setup,
            StringComparison.Ordinal);
        Assert.Contains(
            "Restore-OokiGrader.ps1",
            setup,
            StringComparison.Ordinal);
        Assert.Contains(
            "New-OokiGraderCertificate.ps1",
            setup,
            StringComparison.Ordinal);
        Assert.Contains(
            "Install-OokiGraderPeerTrust.ps1",
            setup,
            StringComparison.Ordinal);
        Assert.Contains(
            "{autopf}\\PowerShell\\7\\pwsh.exe",
            setup,
            StringComparison.Ordinal);
        Assert.Contains(
            "$PSVersionTable.PSVersion -lt [version]''7.4''",
            setup,
            StringComparison.Ordinal);
        Assert.Contains(
            "CurUninstallStepChanged",
            setup,
            StringComparison.Ordinal);
        Assert.Contains(
            "SignTool=ooki",
            setup,
            StringComparison.Ordinal);
        Assert.Contains(
            "SignedUninstaller=yes",
            setup,
            StringComparison.Ordinal);
    }

    [Fact]
    public void UninstallQuarantinesApplicationAndPreservesAllData()
    {
        var script = ReadInstallerFile("Uninstall-OokiGrader.ps1");

        Assert.Contains(
            "[IO.Directory]::Move($install, $archivePath)",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "dataRootPreserved = $true",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "destructiveDataRemovalSupported = $false",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Remove-Item -Recurse",
            script,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "[IO.Directory]::Delete($data",
            script,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CertificateScriptsRequireExactSanAndTrustInputs()
    {
        var certificate = ReadInstallerFile(
            "New-OokiGraderCertificate.ps1");
        var trust = ReadInstallerFile(
            "Install-OokiGraderPeerTrust.ps1");

        Assert.Contains(
            "'2.5.29.17={text}'",
            certificate,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"DNS=$_\"",
            certificate,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"IPAddress=$_\"",
            certificate,
            StringComparison.Ordinal);
        Assert.Contains(
            "AcknowledgeLocalCaPrivateKeyRisk",
            certificate,
            StringComparison.Ordinal);
        Assert.Contains(
            "ExpectedThumbprint",
            trust,
            StringComparison.Ordinal);
        Assert.Contains(
            "CertificateAuthority",
            trust,
            StringComparison.Ordinal);
        Assert.Contains(
            "ShouldProcess",
            trust,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptsDoNotContainCredentialParametersOrBroadRecursiveDeletion()
    {
        foreach (var path in Directory.EnumerateFiles(
            Path.Combine(RepositoryRoot, "installer"),
            "*.ps*",
            SearchOption.TopDirectoryOnly))
        {
            var script = File.ReadAllText(path);
            Assert.DoesNotContain(
                "ApiKey",
                script,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "Remove-Item -Recurse",
                script,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "Directory]::Delete($DataRoot",
                script,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string ReadInstallerFile(string fileName) =>
        File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "installer",
            fileName));

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
