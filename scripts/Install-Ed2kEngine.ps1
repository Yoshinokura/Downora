[CmdletBinding()]
param()
$ErrorActionPreference = "Stop"
$version = "3.1.0"
$archiveName = "aMule-$version-Windows-x64.zip"
$downloadUrl = "https://downloads.sourceforge.net/project/amule/$version/$archiveName"
$expectedSha256 = "81D3A024207C272524E4D992BFAF352A86256B00AFAC414F0405CE465642CFB7"

function Write-Step([string]$Message) { Write-Host ""; Write-Host "==> $Message" -ForegroundColor Cyan }

function Set-IniValue([System.Collections.Generic.List[string]]$Lines, [string]$Section, [string]$Key, [string]$Value) {
    $header = "[$Section]"
    $sectionIndex = -1
    for ($i = 0; $i -lt $Lines.Count; $i++) { if ($Lines[$i].Trim() -ieq $header) { $sectionIndex = $i; break } }
    if ($sectionIndex -lt 0) {
        if ($Lines.Count -gt 0 -and $Lines[$Lines.Count - 1] -ne "") { $Lines.Add("") }
        $Lines.Add($header)
        $sectionIndex = $Lines.Count - 1
    }
    $nextSection = $Lines.Count
    for ($i = $sectionIndex + 1; $i -lt $Lines.Count; $i++) { if ($Lines[$i].Trim() -match '^\[.+\]$') { $nextSection = $i; break } }
    for ($i = $sectionIndex + 1; $i -lt $nextSection; $i++) {
        if ($Lines[$i] -match '^\s*([^=]+?)\s*=') {
            if ($Matches[1].Trim() -ieq $Key) { $Lines[$i] = "$Key=$Value"; return }
        }
    }
    $Lines.Insert($nextSection, "$Key=$Value")
}

function Save-DownoraCredential([string]$Target, [string]$Password) {
    $source = @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
public static class DownoraCredentialWriter {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL {
        public uint Flags; public uint Type; public string TargetName; public string Comment;
        public long LastWritten; public uint CredentialBlobSize; public IntPtr CredentialBlob;
        public uint Persist; public uint AttributeCount; public IntPtr Attributes;
        public string TargetAlias; public string UserName;
    }
    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);
    public static void Save(string target, string password) {
        byte[] bytes = Encoding.Unicode.GetBytes(password);
        IntPtr blob = Marshal.AllocHGlobal(bytes.Length);
        try {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new CREDENTIAL { Type = 1, TargetName = target, UserName = "admin",
                CredentialBlob = blob, CredentialBlobSize = (uint)bytes.Length, Persist = 2 };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        } finally {
            Array.Clear(bytes, 0, bytes.Length);
            for (int i = 0; i < Encoding.Unicode.GetByteCount(password); i++) Marshal.WriteByte(blob, i, 0);
            Marshal.FreeHGlobal(blob);
        }
    }
}
'@
    if (-not ("DownoraCredentialWriter" -as [type])) { Add-Type -TypeDefinition $source -Language CSharp }
    [DownoraCredentialWriter]::Save($Target, $Password)
}

Write-Host "Downora eD2k engine setup (aMule $version)" -ForegroundColor Green
Write-Host "The official aMule portable package will be downloaded and verified."
Write-Host "No administrator rights are required. aMule remains a separate GPL-2.0 component."
if (-not [Environment]::Is64BitOperatingSystem) { throw "This setup currently supports 64-bit Windows only." }

$baseDir = Join-Path $env:LOCALAPPDATA "Downora\Engine\aMule"
$configDir = Join-Path $env:APPDATA "aMule"
$tempDir = Join-Path $env:TEMP ("Downora-aMule-" + [Guid]::NewGuid().ToString("N"))
$archivePath = Join-Path $tempDir $archiveName
New-Item -ItemType Directory -Force -Path $tempDir, $baseDir, $configDir | Out-Null

try {
    $amuled = Get-ChildItem -Path $baseDir -Filter "amuled.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    $api = Get-ChildItem -Path $baseDir -Filter "amuleapi.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $amuled -or -not $api) {
        Write-Step "Downloading the official aMule $version portable package"
        Invoke-WebRequest -Uri $downloadUrl -OutFile $archivePath -UseBasicParsing
        $actualHash = (Get-FileHash -Path $archivePath -Algorithm SHA256).Hash
        if ($actualHash -ne $expectedSha256) { throw "aMule package SHA-256 mismatch. The downloaded file was rejected." }
        Write-Step "Verifying and extracting aMule"
        Expand-Archive -Path $archivePath -DestinationPath $baseDir -Force
        $amuled = Get-ChildItem -Path $baseDir -Filter "amuled.exe" -Recurse | Select-Object -First 1
        $api = Get-ChildItem -Path $baseDir -Filter "amuleapi.exe" -Recurse | Select-Object -First 1
        if (-not $amuled -or -not $api) { throw "The verified archive does not contain both amuled.exe and amuleapi.exe." }
    } else { Write-Step "Using the existing Downora aMule engine" }

    Write-Step "Preparing a private local API connection"
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    $ecBytes = New-Object byte[] 32
    $apiBytes = New-Object byte[] 32
    $random.GetBytes($ecBytes)
    $random.GetBytes($apiBytes)
    $ecPassword = ([BitConverter]::ToString($ecBytes)).Replace("-", "")
    $apiPassword = ([BitConverter]::ToString($apiBytes)).Replace("-", "")
    $md5Provider = [Security.Cryptography.MD5]::Create()
    $md5 = ([BitConverter]::ToString($md5Provider.ComputeHash([Text.Encoding]::UTF8.GetBytes($ecPassword)))).Replace("-", "").ToLowerInvariant()
    $random.Dispose()
    $md5Provider.Dispose()
    $configPath = Join-Path $configDir "amule.conf"
    $lines = [System.Collections.Generic.List[string]]::new()
    if (Test-Path $configPath) { foreach ($line in [IO.File]::ReadAllLines($configPath)) { $lines.Add($line) } }
    Set-IniValue $lines "ExternalConnect" "AcceptExternalConnections" "1"
    Set-IniValue $lines "ExternalConnect" "ECAddress" "127.0.0.1"
    Set-IniValue $lines "ExternalConnect" "ECPort" "4712"
    Set-IniValue $lines "ExternalConnect" "ECPassword" $md5
    Set-IniValue $lines "ExternalConnect" "RequireEncryption" "0"
    Set-IniValue $lines "AmuleApi" "Enabled" "1"
    Set-IniValue $lines "AmuleApi" "HttpPort" "4713"
    Set-IniValue $lines "AmuleApi" "BindAddress" "127.0.0.1"
    Set-IniValue $lines "AmuleApi" "Path" "amuleapi"
    [IO.File]::WriteAllLines($configPath, $lines, [Text.UTF8Encoding]::new($false))

    $passwordProcess = [Diagnostics.Process]::new()
    $passwordProcess.StartInfo = [Diagnostics.ProcessStartInfo]::new()
    $passwordProcess.StartInfo.FileName = $api.FullName
    $passwordProcess.StartInfo.Arguments = '--config-dir="' + $configDir + '" --set-admin-pass-stdin'
    $passwordProcess.StartInfo.WorkingDirectory = Split-Path -Parent $api.FullName
    $passwordProcess.StartInfo.UseShellExecute = $false
    $passwordProcess.StartInfo.RedirectStandardInput = $true
    $passwordProcess.StartInfo.RedirectStandardOutput = $true
    $passwordProcess.StartInfo.RedirectStandardError = $true
    $passwordProcess.StartInfo.CreateNoWindow = $true
    if (-not $passwordProcess.Start()) { throw "Could not configure the aMule API password." }
    $passwordProcess.StandardInput.WriteLine($apiPassword)
    $passwordProcess.StandardInput.Close()
    $stdout = $passwordProcess.StandardOutput.ReadToEnd()
    $stderr = $passwordProcess.StandardError.ReadToEnd()
    $passwordProcess.WaitForExit()
    if ($passwordProcess.ExitCode -ne 0) { throw "aMule API password setup failed. $stderr $stdout" }

    Write-Step "Starting the local eD2k engine"
    if (-not (Get-Process -Name "amuled" -ErrorAction SilentlyContinue)) {
        $startInfo = [Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $amuled.FullName
        $startInfo.Arguments = "--full-daemon"
        $startInfo.WorkingDirectory = Split-Path -Parent $amuled.FullName
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        [void][Diagnostics.Process]::Start($startInfo)
    }

    $apiUri = "http://127.0.0.1:4713/api/v1/auth/login?include_token=true"
    $verified = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        Start-Sleep -Seconds 1
        try {
            $login = Invoke-RestMethod -Uri $apiUri -Method Post -ContentType "application/json" -Body (@{ password = $apiPassword } | ConvertTo-Json -Compress) -TimeoutSec 3
            if ($login.token) { $verified = $true; break }
        } catch { }
    }
    if (-not $verified) { throw "The aMule API did not become ready. Check the aMule configuration and logs in %APPDATA%\aMule." }

    Save-DownoraCredential "Downora:127.0.0.1:4713" $apiPassword
    Write-Step "Setup complete"
    Write-Host "The eD2k engine is installed, configured, running, and connected to Windows Credential Manager." -ForegroundColor Green
    Write-Host "You can now paste an ed2k:// link into Downora."
}
finally {
    if (Test-Path $tempDir) { Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue }
    if ($ecPassword) { $ecPassword = $null }
    if ($apiPassword) { $apiPassword = $null }
}
