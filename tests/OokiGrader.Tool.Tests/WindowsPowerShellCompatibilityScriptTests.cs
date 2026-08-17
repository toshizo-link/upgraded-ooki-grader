using System.Management.Automation.Language;

namespace OokiGrader.Tool.Tests;

public sealed class WindowsPowerShellCompatibilityScriptTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Theory]
    [InlineData("New-OokiGraderCertificate.ps1")]
    [InlineData("New-OokiGraderPeerTrustPackage.ps1")]
    [InlineData("Install-OokiGraderPeerTrust.ps1")]
    public void CompatibilityPowerShellSourcesParseWithoutErrors(
        string fileName)
    {
        var path = Path.Combine(RepositoryRoot, "installer", fileName);

        Parser.ParseFile(path, out _, out var errors);

        Assert.True(
            errors.Length == 0,
            $"{fileName} failed PowerShell parsing:"
            + Environment.NewLine
            + string.Join(
                Environment.NewLine,
                errors.Select(error =>
                    $"{error.Extent.StartLineNumber}:"
                    + $"{error.Extent.StartColumnNumber} "
                    + error.Message)));
    }

    [Fact]
    public void CertificateKeepsLivePkiObjectsInsideWindowsPowerShell()
    {
        var script = ReadInstallerFile("New-OokiGraderCertificate.ps1");

        Assert.Contains(
            "DefaultParameterSetName = 'External'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "[switch] $WindowsPowerShellWorker",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "if ($PSVersionTable.PSEdition -eq 'Core')",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "System32\\WindowsPowerShell\\v1.0\\powershell.exe",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "schema = 'ooki-certificate-worker/v1'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$workerExitCode = $LASTEXITCODE",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "[IO.File]::ReadAllText($responsePath)",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Write-CertificateResult",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Import-Module PKI -UseWindowsPowerShell",
            script,
            StringComparison.OrdinalIgnoreCase);

        var coreBranch = script.IndexOf(
            "elseif ($PSVersionTable.PSEdition -eq 'Core')",
            StringComparison.Ordinal);
        var bypass = script.IndexOf("'Bypass'", StringComparison.Ordinal);
        var firstLivePkiOperation = script.IndexOf(
            "$ca = New-SelfSignedCertificate",
            StringComparison.Ordinal);

        Assert.True(coreBranch >= 0);
        Assert.True(bypass > coreBranch);
        Assert.True(firstLivePkiOperation > bypass);
        Assert.Equal(
            1,
            script.Split("'Bypass'", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void CertificateAclUsesLocaleIndependentWellKnownSids()
    {
        var script = ReadInstallerFile("New-OokiGraderCertificate.ps1");

        Assert.Contains(
            "'*S-1-5-18:(OI)(CI)F'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "'*S-1-5-32-544:(OI)(CI)F'",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "'SYSTEM:(OI)(CI)F'",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "'BUILTIN\\Administrators:(OI)(CI)F'",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PeerLauncherAvoidsWindowsPowerShellFileBooleanSwitchValues()
    {
        var builder = ReadInstallerFile(
            "New-OokiGraderPeerTrustPackage.ps1");
        var installer = ReadInstallerFile(
            "Install-OokiGraderPeerTrust.ps1");

        Assert.Contains(
            "-NonInteractive -ExecutionPolicy RemoteSigned -File",
            builder,
            StringComparison.Ordinal);
        Assert.Contains(
            "-PackageMode -CreateDesktopShortcut",
            builder,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "-PackageMode -CreateDesktopShortcut -Confirm:$false",
            builder,
            StringComparison.Ordinal);
        Assert.Contains(
            "if ($PackageMode) {\n    $ConfirmPreference = 'None'\n}",
            installer,
            StringComparison.Ordinal);

        var checksumVerification = installer.IndexOf(
            "peer trust package checksum manifest",
            StringComparison.OrdinalIgnoreCase);
        var firstMutation = installer.IndexOf(
            "$hostsResult = Set-OokiManagedHostsEntry",
            StringComparison.Ordinal);
        Assert.True(checksumVerification >= 0);
        Assert.True(firstMutation > checksumVerification);
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
