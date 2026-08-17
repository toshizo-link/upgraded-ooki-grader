# Ooki Grader Windows installation acceptance run

This is an instruction sheet for a Codex task running on the Windows test
machine. Its purpose is to return evidence, not to repair the product during
the run.

## Mission

Prove whether the current working tree can be turned into fresh, internally
consistent Windows host media and installed on a real x64 Windows machine.
Exercise the exact path a technician would use at a cram school, including a
failed attempt followed by a retry, HTTPS and service health, a reboot, the
Windows PowerShell 5.1 classroom-PC launcher, and uninstall data preservation.

Return both:

1. `WINDOWS-ACCEPTANCE-REPORT.md`, with every check marked `PASS`, `FAIL`, or
   `BLOCKED` and the exact command/exit code for failures.
2. `OokiGrader-Windows-Acceptance-Results.zip`, containing only the sanitized
   text and JSON evidence described below.

Do not edit application or installer source during this run. A failure is a
result to report, not permission to weaken or work around a check.

## Safety gate

Before making changes, ask the operator to confirm all of the following:

- this is a disposable test machine or a machine explicitly approved for an
  Ooki Grader installation test;
- it is not a production school host and contains no real student data;
- installing a Windows service, a local test CA, a scoped firewall rule, and a
  managed hosts-file entry is acceptable;
- a reboot is acceptable;
- the machine is x64 Windows, not ARM64 Windows;
- the current account can elevate to local administrator.

If any answer is no, run only the read-only source/build checks and mark the
machine-changing phases `BLOCKED`. Never delete or overwrite an existing Ooki
Grader installation, database, certificate, firewall rule, or test-results
directory. Use a new run directory under `C:\OokiGrader-Acceptance`.

Never collect or return passwords, API keys, cookies, tokens, private keys,
PFX/P12 files, database contents, student files, DPAPI blobs, or the contents
of `appsettings.Production.json`. Do not disable antivirus, firewall, TLS
validation, checksum validation, or certificate validation. Do not accept a
browser certificate warning.

## Inputs to locate

- The repository containing this instruction sheet, including the uncommitted
  installer changes supplied by the primary task.
- .NET SDK `10.0.302`, Node.js 24, npm, Git, Windows PowerShell 5.1, and a
  64-bit PowerShell 7.4 or later for building. Record missing prerequisites as
  `BLOCKED`; the host-media bootstrap itself must still be tested on a clean
  snapshot without a compatible `pwsh` when such a snapshot is available.
- Microsoft `PowerShell-7.6.4-win-x64.msi`. Obtain it only from Microsoft's
  official PowerShell release if it was not supplied. Its SHA-256 must be:

  ```text
  d11942df52fd12470169797abfa4781d9480efdc81000ba4fa55a5b921ed8dd0
  ```

Use a unique prerelease version such as
`0.9.2-acceptance.YYYYMMDDHHMMSS`. Do not use or modify the historical 0.9.0
or 0.9.1 folders.

## Evidence setup

Open an elevated PowerShell 7 terminal. Choose a new run ID and create exact
paths; do not reuse a prior run:

```powershell
$RunId = Get-Date -Format 'yyyyMMdd-HHmmss'
$AcceptanceRoot = "C:\OokiGrader-Acceptance\$RunId"
$ResultsRoot = Join-Path $AcceptanceRoot 'Results'
$BuildRoot = Join-Path $AcceptanceRoot 'Build'
$Version = "0.9.2-acceptance.$($RunId.Replace('-', ''))"
New-Item -ItemType Directory -Path $ResultsRoot, $BuildRoot -ErrorAction Stop |
    Out-Null
Start-Transcript -LiteralPath (Join-Path $ResultsRoot 'transcript.txt')
```

Record the repository path in `$RepositoryRoot` and the verified MSI path in
`$PowerShellMsiPath`. Save these read-only facts as text:

```powershell
Set-Location -LiteralPath $RepositoryRoot
git rev-parse HEAD | Set-Content (Join-Path $ResultsRoot 'git-head.txt')
git status --short | Set-Content (Join-Path $ResultsRoot 'git-status.txt')
git diff --check 2>&1 | Tee-Object (Join-Path $ResultsRoot 'git-diff-check.txt')
dotnet --info | Set-Content (Join-Path $ResultsRoot 'dotnet-info.txt')
node --version | Set-Content (Join-Path $ResultsRoot 'node-version.txt')
npm --version | Set-Content (Join-Path $ResultsRoot 'npm-version.txt')
$PSVersionTable | Out-String | Set-Content (
    Join-Path $ResultsRoot 'powershell-version.txt')
Get-FileHash -Algorithm SHA256 -LiteralPath $PowerShellMsiPath |
    Format-List | Out-String | Set-Content (
        Join-Path $ResultsRoot 'powershell-msi-hash.txt')
```

Also record `Get-ComputerInfo`, `Get-CimInstance Win32_ComputerSystem`,
`Get-CimInstance Win32_OperatingSystem`, `Get-Volume`,
`Get-NetIPAddress -AddressFamily IPv4`, and `Get-NetConnectionProfile`. These
contain no application secret, but the operator may redact public addresses or
machine/user names before returning the archive.

## Phase A — source and package verification

Run the same build/test surface as CI and retain complete output and exit codes:

```powershell
dotnet restore OokiGrader.slnx
npm ci --prefix src/OokiGrader.Web --no-audit --no-fund
npm ci --prefix tools/openapi-client --no-audit --no-fund
dotnet build OokiGrader.slnx --configuration Release --no-restore
dotnet test OokiGrader.slnx --configuration Release --no-build
npm --prefix src/OokiGrader.Web run check
npm --prefix src/OokiGrader.Web test
npm --prefix src/OokiGrader.Web run build
npm --prefix src/OokiGrader.Web run api:check
```

Parse every installer script with both PowerShell engines. A parser error in
either engine is a failure:

```powershell
$env:OOKI_INSTALLER_PARSE_ROOT = Join-Path $RepositoryRoot 'installer'
$ParserProbe = @'
$failed = $false
Get-ChildItem -LiteralPath $env:OOKI_INSTALLER_PARSE_ROOT -Recurse -File |
  Where-Object Extension -in '.ps1', '.psm1' |
  ForEach-Object {
    $tokens = $null
    $errors = $null
    [Management.Automation.Language.Parser]::ParseFile(
      $_.FullName, [ref] $tokens, [ref] $errors) | Out-Null
    foreach ($error in $errors) {
      $failed = $true
      "{0}:{1}:{2}: {3}" -f $_.FullName,
        $error.Extent.StartLineNumber,
        $error.Extent.StartColumnNumber,
        $error.Message
    }
  }
if ($failed) { exit 1 }
'@
$EncodedProbe = [Convert]::ToBase64String(
    [Text.Encoding]::Unicode.GetBytes($ParserProbe))
& pwsh.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand $EncodedProbe `
    2>&1
& "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
    -NoLogo -NoProfile -NonInteractive -EncodedCommand $EncodedProbe 2>&1
Remove-Item Env:OOKI_INSTALLER_PARSE_ROOT
```

Build a new unsigned, checksum-verified acceptance package and then the actual
host media. A build that leaves a partial immutable target after failure is a
failure.

```powershell
$PackageOutput = Join-Path $BuildRoot 'Release'
$MediaOutput = Join-Path $BuildRoot 'Media'
& (Join-Path $RepositoryRoot 'installer\New-OokiGraderReleasePackage.ps1') `
    -Version $Version `
    -OutputRoot $PackageOutput `
    -SourceRoot $RepositoryRoot `
    -Confirm:$false
$PackageRoot = Join-Path $PackageOutput "OokiGrader-$Version-win-x64"
& (Join-Path $RepositoryRoot 'installer\New-OokiGraderHostInstallMedia.ps1') `
    -PackageRoot $PackageRoot `
    -Version $Version `
    -PowerShellMsiPath $PowerShellMsiPath `
    -OutputRoot $MediaOutput `
    -AllowChecksumVerifiedUnsignedOnSitePackage `
    -Confirm:$false
$MediaRoot = Join-Path $MediaOutput "OokiGrader-$Version-Windows-Host-Install"
```

Require all of these before continuing:

- the release package passes `Assert-OokiReleasePackage` with the on-site
  unsigned allowance;
- the media builder reports `packaged-and-verified`;
- `media-inventory.json` and `checksums.txt` cover every media file other than
  the aggregate checksum file itself;
- the ZIP contains exactly one top-level `OokiGrader-$Version-win-x64`
  directory and a fresh extraction passes package verification;
- the real PowerShell MSI is present below `Prerequisites`, has the pinned hash,
  and has a valid Microsoft signature;
- the generated README calls Windows 11 Pro, 16 GiB RAM, and 165 GiB free space
  recommendations, while x64, NTFS, integrity, port availability, safe paths,
  and certificate correctness remain requirements.

Copy the complete media folder to a path containing both spaces and Japanese,
for example `C:\OokiGrader-Acceptance\$RunId\現地 メディア`. Continue only
from that copy.

## Phase B — media rejection and retry

Use a second copy of the media for the corruption test. Append one byte to the
copied release ZIP, run `01-Install-OokiGrader-Host.cmd` elevated, and require a
nonzero exit before package extraction or any service/certificate/firewall
mutation. Preserve the failure text. Delete only that deliberately corrupted
media copy.

From the intact media, start the launcher. At the final `INSTALL` confirmation,
cancel before entering the word. Confirm that no Ooki Grader service, CA, or
firewall rule was created. Run the same launcher again. It must validate and
reuse the exact extracted package instead of aborting because the directory
already exists. Record whether the machine began with:

- no PowerShell 7;
- an old or x86 PowerShell 7; or
- a compatible x64 PowerShell 7.

Do not uninstall a pre-existing PowerShell merely to force another case. Test
the missing/old cases only on additional clean snapshots. If MSI returns 3010,
reboot and require the same launcher to resume successfully.

## Phase C — preflight recommendation semantics

Use an empty local NTFS directory that has at least 5 GiB free. A 10–12 GiB
throwaway VHDX is useful for proving the 165 GiB check is advisory, but create
one only on a disposable target and record its exact path. Never repartition a
physical disk for this test.

Run `Test-OokiGraderPreflight.ps1 -PassThru` from the freshly extracted package
with `-AllowChecksumVerifiedOnSitePackage`, then save the complete JSON. Require:

- `blockingFailures` is zero before install;
- `recommendationFailures` records any unmet recommendations;
- `windows-supported`, `memory`, and `data-capacity` have
  `blocking: false` and `classification: recommendation`;
- when free space is below 165 GiB but at least 5 GiB, `data-capacity` fails but
  installation remains ready;
- the separate 5 GiB emergency-reserve check passes and is blocking;
- the runtime check proves both OS and process architecture are `X64`, not just
  “64 bit”;
- NTFS, package integrity, port availability, safe path topology, and private
  host addressing are still blocking requirements;
- absence of the Microsoft Defender cmdlet is reported as a recommendation,
  not a command-not-found abort.

Do not manufacture failures by disabling Defender, encryption, or firewall.

## Phase D — clean host installation

Select the active private IPv4 address attached to the intended trusted LAN.
Accept `Private` or `DomainAuthenticated`; stop on `Public`. On a domain LAN,
the installer must not attempt to change the profile to Private and its
firewall rule must use the Domain profile. Ensure the address remains stable
for the test/reboot period. Production still requires a real static assignment
or DHCP reservation.

Use the extracted package and run `Install-OokiGraderOnSite.ps1` elevated with:

- a unique empty acceptance `DataRoot`;
- `ooki-grader-acceptance.test` as the DNS name;
- the selected active host IPv4 address;
- `-NonInteractive`;
- `-AcceptChecksumVerifiedUnsignedOnSitePackage`;
- `-HostAddressReservationConfirmed` (meaning stable for this isolated test);
- `-InstallationConfirmed`;
- `-Confirm:$false`.

Omit `BackupRoot` for this clean-install test unless a separate encrypted test
destination is genuinely available. Do not falsely confirm backup encryption.
The final result must be `installed-and-verified`. Warnings for recommendation
failures are expected and must not stop the run.

If installation fails, do not hand-edit the package, service, ACL, hosts file,
certificate store, or firewall rule to make it pass. Capture the exception,
exit code, transcript, application event-log entries, service state, package
hashes, and the presence of operation/staging markers, then attempt the exact
same installer command once more. Report whether the retry converges.

## Phase E — installed-state checks

All checks below must pass:

1. `OokiGrader.Host` is Running, delayed automatic, and uses the intended
   virtual service account and versioned executable.
2. `sc.exe qc`, `sc.exe qsidtype`, and `sc.exe showsid` agree with the service
   manifest.
3. Application ACLs let SYSTEM/Administrators administer, Users and the service
   read, but do not let the service modify immutable application files.
4. Data ACLs let the service modify managed data. Translate ACE identities to
   numeric SIDs when recording evidence so a Japanese Windows display name
   cannot hide a localization error.
5. The PFX copied below the managed data root is readable by the service but is
   not returned in the evidence archive.
6. The `Ooki Grader HTTPS` inbound rule is enabled only for the selected
   Private or Domain profile, TCP port, and derived school subnet. It is not an
   Any-profile/Any-address rule.
7. The managed hosts entry maps the canonical host name to loopback on the host.
8. `https://ooki-grader-acceptance.test/health/ready` returns HTTP 200 with
   healthy database, schema, storage, physical storage, and certificate. Do not
   use `-SkipCertificateCheck`, IP-address URLs, HTTP, or a proxy/TLS bypass.
9. `Test-OokiGraderHealth.ps1` reports `healthy` and `tlsBypassUsed: false`.
10. The installation manifest matches the service, paths, version, port, and
    certificate thumbprint. Record hashes/metadata only, not configuration or
    private-key contents.

Generate the peer trust package as the installer does. On at least one Windows
PowerShell 5.1 machine, launch `Install-On-This-PC.cmd` from a path containing
spaces and Japanese. It must exit zero, validate every package checksum, import
only the public CA, create the exact managed hosts entry and shortcut, and pass
real HTTPS readiness. If no second clean Windows machine is available, run the
launcher syntax/checksum test on the host and mark independent peer TLS
validation `BLOCKED`, not `PASS`.

## Phase F — reboot, uninstall, and immediate retry

Save the report and transcript, reboot Windows normally, resume the same Codex
task, and record:

- service reached Running without manual intervention;
- canonical HTTPS readiness and the health script still pass without a TLS
  bypass;
- certificate private-key access and data ACLs still work;
- the firewall scope and managed hosts entry did not broaden or disappear.

Then stop normal use and run the supported uninstaller with its explicit
offline confirmation. Require the service entry to disappear, application
files to move to the recovery area, and the acceptance `DataRoot` and database
to remain byte-for-byte present. Do not open or return the database.

Immediately run the same clean installation again. It must not fail with a
service “marked for deletion” race, an existing-package error, an existing
certificate error, or an immutable peer-package error. Reboot once more only if
the product explicitly reports that it is required. Leave preserved test data
and recovery evidence in place for the operator to remove after review.

## Sanitized result bundle

The ZIP may contain only:

- the Markdown report and transcript after manual secret review;
- build/test output and exit codes;
- Git commit/status/diff-check output;
- tool/runtime versions and sanitized system profile;
- release and media inventories, checksum manifests, and hashes;
- preflight/install/health JSON;
- `sc.exe` service metadata;
- numeric-SID ACL summaries (not protected file contents);
- firewall rule/profile/address/port summaries;
- certificate public metadata and thumbprints (never PFX/private keys);
- relevant Ooki Grader Windows event-log messages;
- reboot/uninstall/reinstall results.

Exclude all `.pfx`, `.p12`, `.db`, `.db-wal`, `.db-shm`, keys, secrets,
cookies, tokens, student files, and production configuration contents. Before
creating the ZIP, recursively search candidate text for likely secrets and
manually review it. List any omitted evidence in the report.

The report must end with these explicit gates:

```text
Fresh x64 package/media build: PASS|FAIL|BLOCKED
Recommendation-only host sizing: PASS|FAIL|BLOCKED
Corruption rejection: PASS|FAIL|BLOCKED
Interrupted-install retry: PASS|FAIL|BLOCKED
Clean install and local HTTPS: PASS|FAIL|BLOCKED
Japanese/localized ACL safety: PASS|FAIL|BLOCKED
Domain/Private firewall behavior: PASS|FAIL|BLOCKED
WinPS 5.1 peer launcher: PASS|FAIL|BLOCKED
Independent classroom-PC TLS: PASS|FAIL|BLOCKED
Reboot persistence: PASS|FAIL|BLOCKED
Uninstall data preservation: PASS|FAIL|BLOCKED
Immediate reinstall: PASS|FAIL|BLOCKED
Overall onsite readiness: PASS|FAIL|BLOCKED
```

`Overall onsite readiness` is `PASS` only when every item except optional
independent peer validation is `PASS`; independent peer validation must still
be completed before a real school deployment.
