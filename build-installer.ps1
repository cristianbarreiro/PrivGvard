param (
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",

    [switch]$SkipMsix,

    [switch]$MsixOnly,

    [switch]$SignMsix,

    [string]$CertificateThumbprint,

    [switch]$AllowTestIdentity
)

$ErrorActionPreference = "Stop"

if ($SkipMsix -and $MsixOnly) {
    Write-Error "Invalid arguments: Cannot specify both -SkipMsix and -MsixOnly."
    exit 1
}
$PathTrimCharacters = [char[]]@(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar
)
$ProjectRoot = [System.IO.Path]::GetFullPath($PSScriptRoot).TrimEnd($PathTrimCharacters)
$ProjectRootPrefix = $ProjectRoot + [System.IO.Path]::DirectorySeparatorChar

function Get-ExistingFileSystemItem([string]$LiteralPath) {
    try {
        return Get-Item -LiteralPath $LiteralPath -Force -ErrorAction Stop
    }
    catch [System.Management.Automation.ItemNotFoundException] {
        return $null
    }
}

function Assert-NoReparsePointInBuildPath([string]$ResolvedPath) {
    $CurrentPath = $ResolvedPath
    while ($true) {
        if (-not [System.String]::Equals(
                $CurrentPath,
                $ProjectRoot,
                [System.StringComparison]::OrdinalIgnoreCase) -and
            -not $CurrentPath.StartsWith(
                $ProjectRootPrefix,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Build path escaped the project root while validating reparse points: $CurrentPath"
        }

        $CurrentItem = Get-ExistingFileSystemItem $CurrentPath
        if ($null -ne $CurrentItem) {
            if (-not $CurrentItem.PSIsContainer) {
                throw "Expected a directory in the build path but found a file: $CurrentPath"
            }
            if (($CurrentItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to use a build path containing a reparse point: $CurrentPath"
            }
        }

        if ([System.String]::Equals(
                $CurrentPath,
                $ProjectRoot,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            return
        }

        $ParentPath = [System.IO.Path]::GetDirectoryName($CurrentPath)
        if ([string]::IsNullOrWhiteSpace($ParentPath) -or
            [System.String]::Equals(
                $ParentPath,
                $CurrentPath,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Could not reach the project root while validating build path: $ResolvedPath"
        }

        $CurrentPath = [System.IO.Path]::GetFullPath($ParentPath).TrimEnd($PathTrimCharacters)
    }
}

function Assert-NoReparsePointInDirectoryTree([string]$RootPath) {
    $PendingDirectories = [System.Collections.Generic.Stack[string]]::new()
    $PendingDirectories.Push($RootPath)

    while ($PendingDirectories.Count -gt 0) {
        $CurrentPath = $PendingDirectories.Pop()
        $CurrentItem = Get-Item -LiteralPath $CurrentPath -Force -ErrorAction Stop
        if (-not $CurrentItem.PSIsContainer) {
            throw "Expected a build output directory but found a file: $CurrentPath"
        }
        if (($CurrentItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to recursively delete a reparse point: $CurrentPath"
        }

        $Children = @(Get-ChildItem -LiteralPath $CurrentPath -Force -ErrorAction Stop)
        foreach ($Child in $Children) {
            if (($Child.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to recursively delete a directory tree containing a reparse point: $($Child.FullName)"
            }
            if ($Child.PSIsContainer) {
                $PendingDirectories.Push($Child.FullName)
            }
        }
    }
}

function Remove-VerifiedBuildDirectory([string]$Path, [string]$ExpectedRelativePath) {
    $ResolvedPath = [System.IO.Path]::GetFullPath($Path).TrimEnd($PathTrimCharacters)
    $ExpectedPath = [System.IO.Path]::GetFullPath(
        (Join-Path $ProjectRoot $ExpectedRelativePath)
    ).TrimEnd($PathTrimCharacters)

    if (-not $ExpectedPath.StartsWith(
            $ProjectRootPrefix,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        -not [System.String]::Equals(
            $ResolvedPath,
            $ExpectedPath,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to recursively delete unverified build directory: $ResolvedPath"
    }

    Assert-NoReparsePointInBuildPath $ResolvedPath

    $BuildItem = Get-ExistingFileSystemItem $ResolvedPath
    if ($null -ne $BuildItem) {
        Assert-NoReparsePointInDirectoryTree $ResolvedPath
        Remove-Item -LiteralPath $ResolvedPath -Recurse -Force -ErrorAction Stop
    }
}

Write-Host "==========================================================" -ForegroundColor Cyan
if ($MsixOnly) {
    Write-Host " Building & Packaging PrivGvard MSIX Release" -ForegroundColor Cyan
} elseif ($SkipMsix) {
    Write-Host " Building & Packaging PrivGvard Installer & Portable" -ForegroundColor Cyan
} else {
    Write-Host " Building & Packaging PrivGvard Release (Installer, Portable & MSIX)" -ForegroundColor Cyan
}
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Clean previous build outputs and stale bin/obj
Write-Host "`n[1/6] Cleaning output directories and active project build caches..." -ForegroundColor Yellow
$PublishRoot = Join-Path $ProjectRoot "publish_out"
$PublishDir = Join-Path $PublishRoot "win-x64"
$InstallerOutDir = Join-Path $ProjectRoot "installer_out"
$PublishDistDir = Join-Path $ProjectRoot "publish_dist"

# A forced termination would bypass the reversible-session shutdown coordinator and can strand
# camera/microphone state. Require the operator to exit PrivGvard normally instead.
$RunningInstances = Get-Process -Name "PrivGvard", "PrivLock", "CamMicBlocker" -ErrorAction SilentlyContinue
if ($RunningInstances) {
    Write-Error "PrivGvard (or legacy instance) is running. Exit it normally from the tray/window so privacy state is restored, then rerun this script."
    exit 1
}

# Clean stale bin/obj directories in active projects
$ActiveProjectDirs = @(
    Get-ChildItem -Path "$ProjectRoot\src", "$ProjectRoot\tests" -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name.StartsWith("PrivLock") }
)
foreach ($projDir in $ActiveProjectDirs) {
    foreach ($sub in @("bin", "obj")) {
        $targetDir = Join-Path $projDir.FullName $sub
        if (Test-Path -LiteralPath $targetDir) {
            $relPath = $targetDir.Substring($ProjectRoot.Length).TrimStart($PathTrimCharacters)
            Remove-VerifiedBuildDirectory $targetDir $relPath
        }
    }
}

# Clean stale publish_dist if present
if (Test-Path -LiteralPath $PublishDistDir) {
    Remove-VerifiedBuildDirectory $PublishDistDir "publish_dist"
}

# Safely clean the ENTIRE publish_out tree and installer_out
Remove-VerifiedBuildDirectory $PublishRoot "publish_out"
Remove-VerifiedBuildDirectory $InstallerOutDir "installer_out"
if (-not (Test-Path -LiteralPath $PublishDir)) { New-Item -ItemType Directory -Path $PublishDir -Force | Out-Null }
if (-not (Test-Path -LiteralPath $InstallerOutDir)) { New-Item -ItemType Directory -Path $InstallerOutDir -Force | Out-Null }

# 2. Run Unit Tests
Write-Host "`n[2/6] Running unit test suite..." -ForegroundColor Yellow
dotnet test "$ProjectRoot\PrivGvard.sln" --configuration $Configuration
if ($LASTEXITCODE -ne 0) {
    Write-Error "Unit tests failed! Aborting release build."
    exit 1
}

$CleanVersion = $null
if (-not $MsixOnly) {
    # 3. Publish Single-File Self-Contained Binary
    Write-Host "`n[3/6] Publishing single-file self-contained win-x64 release..." -ForegroundColor Yellow
    dotnet publish "$ProjectRoot\src\PrivLock.Desktop\PrivLock.Desktop.csproj" `
        -c $Configuration `
        -r win-x64 `
        --self-contained `
        -p:PublishSingleFile=true `
        -o $PublishDir

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Publishing failed! Aborting installer build."
        exit 1
    }

    $PublishedExecutable = Join-Path $PublishDir "PrivGvard.exe"
    if (-not (Test-Path -LiteralPath $PublishedExecutable -PathType Leaf)) {
        Write-Error "Publishing completed without producing the expected executable: $PublishedExecutable"
        exit 1
    }

    # Assert forbidden legacy executables do NOT exist inside publish_out
    $ForbiddenExecutables = @(Get-ChildItem -LiteralPath $PublishRoot -Recurse -File -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -ieq "PrivLock.exe" -or $_.Name -ieq "CamMicBlocker.exe"
    })
    if ($ForbiddenExecutables.Count -gt 0) {
        $ForbiddenList = ($ForbiddenExecutables | ForEach-Object { $_.FullName }) -join ", "
        Write-Error "Forbidden legacy executable(s) found in publish output: $ForbiddenList"
        exit 1
    }

    # Verify executable metadata
    $VersionInfo = (Get-Item -LiteralPath $PublishedExecutable).VersionInfo
    Write-Host "Verifying executable metadata for $PublishedExecutable..." -ForegroundColor Gray
    Write-Host "  Product: $($VersionInfo.ProductName)" -ForegroundColor Gray
    Write-Host "  Version: $($VersionInfo.ProductVersion)" -ForegroundColor Gray
    Write-Host "  Description: $($VersionInfo.FileDescription)" -ForegroundColor Gray

    if ($VersionInfo.ProductName -ieq "PrivLock" -or $VersionInfo.ProductName -ieq "CamMicBlocker" -or
        $VersionInfo.FileDescription -ieq "PrivLock" -or $VersionInfo.FileDescription -ieq "CamMicBlocker") {
        Write-Error "Executable metadata indicates legacy identity: ProductName='$($VersionInfo.ProductName)', FileDescription='$($VersionInfo.FileDescription)'"
        exit 1
    }

    if ($VersionInfo.ProductName -ne "PrivGvard") {
        Write-Error "Executable metadata ProductName mismatch: expected 'PrivGvard', got '$($VersionInfo.ProductName)'"
        exit 1
    }

    if ($VersionInfo.ProductMajorPart -lt 2) {
        Write-Error "Executable metadata version mismatch: expected major version 2+, got '$($VersionInfo.ProductMajorPart)'"
        exit 1
    }

    # Inno recursively consumes this directory. Revalidate it after publish so a reparse point cannot
    # make the installer capture files from outside the intended RID-specific output directory.
    Assert-NoReparsePointInBuildPath $PublishDir
    Assert-NoReparsePointInDirectoryTree $PublishDir

    # 4. Locate Inno Setup Compiler (ISCC.exe) and compile setup executable
    Write-Host "`n[4/6] Compiling Windows Setup Installer with Inno Setup..." -ForegroundColor Yellow

    $IsccCandidatePaths = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe"
    )

    $IsccPath = $null
    foreach ($path in $IsccCandidatePaths) {
        if (Test-Path $path) {
            $IsccPath = $path
            break
        }
    }

    if (-not $IsccPath) {
        $cmd = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
        if ($cmd) {
            $IsccPath = $cmd.Source
        }
    }

    if (-not $IsccPath) {
        Write-Error "ISCC.exe (Inno Setup Compiler) was not found! Please install Inno Setup 6."
        exit 1
    }

    Write-Host "Using ISCC compiler: $IsccPath" -ForegroundColor Gray
    $CleanVersion = $VersionInfo.ProductVersion.Split('+')[0].Trim()
    if ([string]::IsNullOrWhiteSpace($CleanVersion)) {
        $CleanVersion = "$($VersionInfo.ProductMajorPart).$($VersionInfo.ProductMinorPart).$($VersionInfo.ProductBuildPart)"
    }
    & $IsccPath "/O$InstallerOutDir" "/FPrivGvard-Setup-$CleanVersion" "$ProjectRoot\installer\setup.iss"

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Installer compilation failed!"
        exit 1
    }

    # 5. Create Portable Distribution ZIP
    Write-Host "`n[5/6] Packaging Portable distribution ZIP..." -ForegroundColor Yellow
    $PortableZipPath = Join-Path $InstallerOutDir "PrivGvard-Portable-$CleanVersion.zip"
    if (Test-Path -LiteralPath $PortableZipPath) {
        Remove-Item -LiteralPath $PortableZipPath -Force
    }
    Compress-Archive -Path "$PublishDir\*" -DestinationPath $PortableZipPath -Force
} else {
    Write-Host "`n[3/6] Publishing single-file self-contained win-x64 release... SKIPPED (-MsixOnly)" -ForegroundColor DarkGray
    Write-Host "[4/6] Compiling Windows Setup Installer with Inno Setup... SKIPPED (-MsixOnly)" -ForegroundColor DarkGray
    Write-Host "[5/6] Packaging Portable distribution ZIP... SKIPPED (-MsixOnly)" -ForegroundColor DarkGray

    # Resolve CleanVersion from Directory.Build.props when skipping single-file publish
    $PropsPath = Join-Path $ProjectRoot "Directory.Build.props"
    if (Test-Path -LiteralPath $PropsPath) {
        $propsXml = [xml](Get-Content -LiteralPath $PropsPath -Raw)
        $CleanVersion = $propsXml.Project.PropertyGroup.Version
    }
    if ([string]::IsNullOrWhiteSpace($CleanVersion)) {
        $CleanVersion = "2.0.0"
    }
}

# 6. Build MSIX Package
$GeneratedMsixFile = $null
if (-not $SkipMsix) {
    Write-Host "`n[6/6] Packaging MSIX Windows container (x64)..." -ForegroundColor Yellow
    $BuildMsixScript = Join-Path $ProjectRoot "scripts\build-msix.ps1"
    if (-not (Test-Path -LiteralPath $BuildMsixScript)) {
        Write-Error "MSIX build script not found at: $BuildMsixScript"
        exit 1
    }

    $msixParams = @{
        Architecture = "x64"
        Configuration = $Configuration
    }

    if (-not $AllowTestIdentity) {
        $msixParams["ValidateStoreIdentity"] = $true
    }

    if ($SignMsix) {
        $msixParams["SignPackage"] = $true
        if ($CertificateThumbprint) {
            $msixParams["CertificateThumbprint"] = $CertificateThumbprint
        }
    }

    try {
        & $BuildMsixScript @msixParams
        if ($LASTEXITCODE -ne 0) {
            Write-Error "MSIX packaging failed with exit code $LASTEXITCODE! Aborting release build."
            exit 1
        }
    }
    catch {
        Write-Error "MSIX packaging encountered an error: $_"
        exit 1
    }

    # Verify that the expected MSIX package exists and is not empty
    $MsixOutDir = Join-Path $PublishRoot "msix"
    $msixCandidates = @(Get-ChildItem -Path $MsixOutDir -Filter "*.msix" -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like "PrivGvard*x64.msix" } |
        Sort-Object LastWriteTime -Descending)

    if ($msixCandidates.Count -eq 0 -or $msixCandidates[0].Length -le 0) {
        Write-Error "MSIX verification failed: no valid non-empty MSIX package was found in $MsixOutDir"
        exit 1
    }
    $GeneratedMsixFile = $msixCandidates[0]

    # Deep verification of final MSIX package manifest and contents
    Write-Host "Verifying final MSIX package manifest and contents ($($GeneratedMsixFile.Name))..." -ForegroundColor Gray
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($GeneratedMsixFile.FullName)
    try {
        $manifestEntry = $zip.Entries | Where-Object { $_.FullName -eq "AppxManifest.xml" }
        if (-not $manifestEntry) {
            Write-Error "MSIX verification failed: AppxManifest.xml missing inside $($GeneratedMsixFile.FullName)"
            exit 1
        }
        $stream = $manifestEntry.Open()
        $reader = [System.IO.StreamReader]::new($stream)
        [xml]$pkgXml = $reader.ReadToEnd()
        $reader.Close()
        $stream.Close()

        $pkgName = $pkgXml.Package.Identity.Name
        $pkgPublisher = $pkgXml.Package.Identity.Publisher
        $pkgPublisherDisplayName = $pkgXml.Package.Properties.PublisherDisplayName
        $pkgVersion = $pkgXml.Package.Identity.Version
        $pkgArch = $pkgXml.Package.Identity.ProcessorArchitecture
        $pkgExecutable = $pkgXml.Package.Applications.Application.Executable

        Write-Host "  MSIX Package Identity Details:" -ForegroundColor Gray
        Write-Host "    Identity/Name:                   $pkgName" -ForegroundColor Gray
        Write-Host "    Identity/Publisher:              $pkgPublisher" -ForegroundColor Gray
        Write-Host "    Properties/PublisherDisplayName: $pkgPublisherDisplayName" -ForegroundColor Gray
        Write-Host "    Identity/Version:                $pkgVersion" -ForegroundColor Gray
        Write-Host "    Identity/ProcessorArchitecture:  $pkgArch" -ForegroundColor Gray
        Write-Host "    Application/Executable:          $pkgExecutable" -ForegroundColor Gray

        if ($pkgExecutable -ne "PrivGvard.exe") {
            Write-Error "MSIX verification failed: Executable is '$pkgExecutable', expected 'PrivGvard.exe'"
            exit 1
        }

        if (-not $AllowTestIdentity) {
            $expectedStoreName = "cdevstudios.PrivGvard"
            $expectedStorePublisher = "CN=85AB4167-A0AE-4FDF-B840-B95CD225F7DD"
            $expectedStorePubDisplay = "cdev studios"

            if ($pkgName -ne $expectedStoreName -or
                $pkgPublisher -ne $expectedStorePublisher -or
                $pkgPublisherDisplayName -ne $expectedStorePubDisplay) {
                Write-Error "MSIX Store identity verification failed:`n  Expected Name='$expectedStoreName', got '$pkgName'`n  Expected Publisher='$expectedStorePublisher', got '$pkgPublisher'`n  Expected PublisherDisplayName='$expectedStorePubDisplay', got '$pkgPublisherDisplayName'"
                exit 1
            }
        }

        $entryNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($e in $zip.Entries) { [void]$entryNames.Add($e.FullName.Replace('\', '/')) }

        if (-not $entryNames.Contains("PrivGvard.exe")) {
            Write-Error "MSIX verification failed: PrivGvard.exe is missing from package entries."
            exit 1
        }
        if (-not $entryNames.Contains("resources.pri")) {
            Write-Error "MSIX verification failed: resources.pri is missing from package entries."
            exit 1
        }
    }
    finally {
        $zip.Dispose()
    }
} else {
    Write-Host "`n[6/6] Packaging MSIX container (x64)... SKIPPED (-SkipMsix)" -ForegroundColor DarkGray
}

Write-Host "`n==========================================================" -ForegroundColor Green
Write-Host " PrivGvard Release Build Completed Successfully" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
Write-Host " Status:" -ForegroundColor Cyan
Write-Host "   Build:        PASS" -ForegroundColor Green
Write-Host "   Tests:        PASS" -ForegroundColor Green

if (-not $MsixOnly) {
    Write-Host "   Setup EXE:    PASS" -ForegroundColor Green
    Write-Host "   Portable:     PASS" -ForegroundColor Green
} else {
    Write-Host "   Setup EXE:    SKIPPED (-MsixOnly)" -ForegroundColor DarkGray
    Write-Host "   Portable:     SKIPPED (-MsixOnly)" -ForegroundColor DarkGray
}

if (-not $SkipMsix) {
    Write-Host "   MSIX (x64):   PASS" -ForegroundColor Green
} else {
    Write-Host "   MSIX (x64):   SKIPPED (-SkipMsix)" -ForegroundColor DarkGray
}

Write-Host "`n Artifacts:" -ForegroundColor Cyan
if (-not $MsixOnly) {
    Write-Host "   Setup:        $InstallerOutDir\PrivGvard-Setup-$CleanVersion.exe" -ForegroundColor White
    Write-Host "   Portable:     $InstallerOutDir\PrivGvard-Portable-$CleanVersion.zip" -ForegroundColor White
    Write-Host "   Executable:   $PublishDir\PrivGvard.exe" -ForegroundColor White
}
if ($GeneratedMsixFile) {
    Write-Host "   MSIX:         $($GeneratedMsixFile.FullName)" -ForegroundColor White
}
Write-Host "==========================================================" -ForegroundColor Green
