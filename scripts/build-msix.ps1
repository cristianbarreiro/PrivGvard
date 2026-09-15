<#
.SYNOPSIS
    Builds and packages PrivGvard as an MSIX package and/or MSIX bundle for Microsoft Store distribution.

.DESCRIPTION
    Compiles the PrivLock.Desktop project as a self-contained, loose-binary distribution (required for MSIX),
    generates the AppxManifest.xml with appropriate architecture and publisher identity, packages visual assets,
    and runs MakeAppx.exe from the Windows SDK. Optionally creates a self-signed certificate for local testing
    or signs the package with SignTool.exe.

.PARAMETER Architecture
    Target architecture: 'x64' (default), 'arm64', or 'all' (builds both and creates a bundle).

.PARAMETER Configuration
    Build configuration: 'Release' (default) or 'Debug'.

.PARAMETER PackageVersion
    Four-part version (Major.Minor.Build.Revision). Defaults to reading PackageVersion from Directory.Build.props.

.PARAMETER Publisher
    Publisher distinguished name (e.g. "CN=cdev Studio, O=cdev Studio, C=US" or Partner Center Publisher ID).

.PARAMETER PublisherDisplayName
    Publisher display name shown in Store/UI. Defaults to "cdev Studio".

.PARAMETER CreateBundle
    Switch to generate a multi-architecture .msixbundle package.

.PARAMETER SignPackage
    Switch to sign the generated MSIX package(s) using SignTool.exe.

.PARAMETER CreateSelfSignedCert
    Switch to create a local self-signed test certificate for development sideloading.

.PARAMETER CertificatePath
    Path to a .pfx certificate file for signing.

.PARAMETER CertificatePassword
    Password for the .pfx certificate file.

.PARAMETER CertificateThumbprint
    SHA1 thumbprint of a certificate in Cert:\CurrentUser\My for signing.

.PARAMETER OutputDir
    Directory where the MSIX packages will be created. Defaults to 'publish_out/msix'.

.EXAMPLE
    .\scripts\build-msix.ps1 -Architecture x64
    Builds a Release x64 MSIX package.

.EXAMPLE
    .\scripts\build-msix.ps1 -Architecture all -CreateBundle
    Builds both x64 and arm64 packages and packages them into a .msixbundle.

.EXAMPLE
    .\scripts\build-msix.ps1 -Architecture x64 -SignPackage -CreateSelfSignedCert
    Builds and signs an x64 MSIX with a fresh self-signed test certificate for local sideload testing.
#>

param (
    [ValidateSet("x64", "arm64", "all")]
    [string]$Architecture = "x64",

    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",

    [string]$PackageVersion,

    [string]$Publisher = "CN=PrivGvard",

    [string]$PublisherDisplayName = "PrivGvard",

    [switch]$CreateBundle,

    [switch]$SignPackage,

    [switch]$CreateSelfSignedCert,

    [string]$CertificateThumbprint,

    [string]$OutputDir
)

$ErrorActionPreference = "Stop"

$ProjectRoot = [System.IO.Path]::GetFullPath("$PSScriptRoot\..")
$PackagingSourceDir = Join-Path $ProjectRoot "packaging"
$AssetsSourceDir = Join-Path $PackagingSourceDir "Assets"
$ManifestSourcePath = Join-Path $PackagingSourceDir "Package.appxmanifest"
$DesktopCsProjPath = Join-Path $ProjectRoot "src\PrivLock.Desktop\PrivLock.Desktop.csproj"
$PropsPath = Join-Path $ProjectRoot "Directory.Build.props"

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $ProjectRoot "publish_out\msix"
}

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " PrivGvard - MSIX Build and Packaging Pipeline" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# ---------------------------------------------------------------------
# 1. Resolve Package Version
# ---------------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($PackageVersion)) {
    if (Test-Path -LiteralPath $PropsPath) {
        $propsXml = [xml](Get-Content -LiteralPath $PropsPath -Raw)
        $PackageVersion = $propsXml.Project.PropertyGroup.PackageVersion
        if ([string]::IsNullOrWhiteSpace($PackageVersion)) {
            $PackageVersion = $propsXml.Project.PropertyGroup.Version + ".0"
        }
    }
    if ([string]::IsNullOrWhiteSpace($PackageVersion)) {
        $PackageVersion = "2.0.0.0"
    }
}
Write-Host "Package Version:    $PackageVersion" -ForegroundColor White
Write-Host "Configuration:      $Configuration" -ForegroundColor White
Write-Host "Publisher:          $Publisher" -ForegroundColor White
Write-Host "Target Arch:        $Architecture" -ForegroundColor White

# ---------------------------------------------------------------------
# 2. Locate Windows SDK Tools (MakeAppx, MakePri & SignTool)
# ---------------------------------------------------------------------
$WindowsKitsBin = "C:\Program Files (x86)\Windows Kits\10\bin"
$MakeAppxPath = $null
$MakePriPath = $null
$SignToolPath = $null

if (Test-Path -LiteralPath $WindowsKitsBin) {
    # Prefer x64 host tool from the highest installed Windows 10/11 SDK version
    $makeAppxFiles = Get-ChildItem -Path $WindowsKitsBin -Filter "makeappx.exe" -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like "*\x64\makeappx.exe" } |
        Sort-Object FullName -Descending

    if ($makeAppxFiles -and $makeAppxFiles.Count -gt 0) {
        $MakeAppxPath = $makeAppxFiles[0].FullName
    }

    $makePriFiles = Get-ChildItem -Path $WindowsKitsBin -Filter "makepri.exe" -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like "*\x64\makepri.exe" } |
        Sort-Object FullName -Descending

    if ($makePriFiles -and $makePriFiles.Count -gt 0) {
        $MakePriPath = $makePriFiles[0].FullName
    }

    $signToolFiles = Get-ChildItem -Path $WindowsKitsBin -Filter "signtool.exe" -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like "*\x64\signtool.exe" } |
        Sort-Object FullName -Descending

    if ($signToolFiles -and $signToolFiles.Count -gt 0) {
        $SignToolPath = $signToolFiles[0].FullName
    }
}

if (-not $MakeAppxPath) {
    throw "MakeAppx.exe was not found in Windows Kits. Please install the Windows 10/11 SDK."
}

if (-not $MakePriPath) {
    throw "MakePri.exe was not found in Windows Kits. Please install the Windows 10/11 SDK."
}

Write-Host "Using MakeAppx:     $MakeAppxPath" -ForegroundColor DarkGray
Write-Host "Using MakePri:      $MakePriPath" -ForegroundColor DarkGray
if ($SignPackage -or $CreateSelfSignedCert) {
    if (-not $SignToolPath) {
        throw "SignTool.exe was not found in Windows Kits. Please install the Windows 10/11 SDK."
    }
    Write-Host "Using SignTool:     $SignToolPath" -ForegroundColor DarkGray
}

# ---------------------------------------------------------------------
# 3. Ensure Visual Assets Exist
# ---------------------------------------------------------------------
$unplatedSample = Join-Path $AssetsSourceDir "Square44x44Logo.targetsize-48_altform-unplated.png"
if (-not (Test-Path -LiteralPath $AssetsSourceDir) -or -not (Test-Path -LiteralPath $unplatedSample)) {
    Write-Host "`nGenerating missing or updated Store visual assets..." -ForegroundColor Yellow
    & "$PSScriptRoot\generate-store-assets.ps1"
}

# Ensure Output Directory
if (-not (Test-Path -LiteralPath $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

$architecturesToBuild = @()
if ($Architecture -eq "all") {
    $architecturesToBuild = @("x64", "arm64")
    $CreateBundle = $true
} else {
    $architecturesToBuild = @($Architecture)
}

$builtPackages = @()

# ---------------------------------------------------------------------
# 4. Build and Package each Architecture
# ---------------------------------------------------------------------
foreach ($arch in $architecturesToBuild) {
    $rid = "win-$arch"
    Write-Host "`n----------------------------------------------------------" -ForegroundColor Yellow
    Write-Host " [1/3] Compiling and publishing ($arch / $rid)..." -ForegroundColor Yellow
    Write-Host "----------------------------------------------------------" -ForegroundColor Yellow

    $stagingDir = Join-Path $OutputDir "staging-$arch"
    if (Test-Path -LiteralPath $stagingDir) {
        Remove-Item -LiteralPath $stagingDir -Recurse -Force
    }

    $publishArgs = @(
        "publish",
        $DesktopCsProjPath,
        "-c", $Configuration,
        "-r", $rid,
        "--self-contained",
        "-p:PublishSingleFile=false",
        "-o", $stagingDir
    )

    Write-Host "dotnet $($publishArgs -join ' ')" -ForegroundColor DarkGray
    & dotnet @publishArgs

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $rid!"
    }

    # Prepare AppxManifest.xml for this architecture
    Write-Host "`n [2/3] Configuring AppxManifest.xml for $arch..." -ForegroundColor Yellow
    [xml]$manifestXml = Get-Content -LiteralPath $ManifestSourcePath -Raw
    $manifestXml.Package.Identity.ProcessorArchitecture = $arch
    $manifestXml.Package.Identity.Version = $PackageVersion
    $manifestXml.Package.Identity.Publisher = $Publisher
    $manifestXml.Package.Properties.PublisherDisplayName = $PublisherDisplayName

    $targetManifestPath = Join-Path $stagingDir "AppxManifest.xml"
    $manifestXml.Save($targetManifestPath)

    # Copy Assets folder into staging
    $targetAssetsDir = Join-Path $stagingDir "Assets"
    if (Test-Path -LiteralPath $targetAssetsDir) {
        Remove-Item -LiteralPath $targetAssetsDir -Recurse -Force
    }
    Copy-Item -Path $AssetsSourceDir -Destination $stagingDir -Recurse -Force

    # Generate resources.pri using MakePri
    Write-Host "`n Generating resources.pri for $arch..." -ForegroundColor Yellow
    $priconfigPath = Join-Path $stagingDir "priconfig.xml"
    & $MakePriPath createconfig /cf $priconfigPath /dq en-US /pv 10.0.0 /o
    if ($LASTEXITCODE -ne 0) {
        throw "MakePri.exe createconfig failed!"
    }

    # Remove <packaging> auto-split section so that all scale and unplated targetsize assets
    # are indexed into a single monolithic resources.pri inside the main package.
    [xml]$configXml = Get-Content -LiteralPath $priconfigPath -Raw
    if ($configXml.resources.packaging) {
        $configXml.resources.RemoveChild($configXml.resources.packaging) | Out-Null
        $configXml.Save($priconfigPath)
    }

    $priPath = Join-Path $stagingDir "resources.pri"
    & $MakePriPath new /pr $stagingDir /cf $priconfigPath /of $priPath /mn $targetManifestPath /o
    if ($LASTEXITCODE -ne 0) {
        throw "MakePri.exe new failed to compile resources.pri!"
    }

    # Clean up temporary priconfig.xml
    if (Test-Path -LiteralPath $priconfigPath) {
        Remove-Item -LiteralPath $priconfigPath -Force
    }

    # Pack MSIX
    Write-Host "`n [3/3] Packaging MSIX container for $arch..." -ForegroundColor Yellow
    $packageFileName = "PrivGvard-$PackageVersion-$arch.msix"
    $packageFilePath = Join-Path $OutputDir $packageFileName

    if (Test-Path -LiteralPath $packageFilePath) {
        Remove-Item -LiteralPath $packageFilePath -Force
    }

    & $MakeAppxPath pack /d $stagingDir /p $packageFilePath /o
    if ($LASTEXITCODE -ne 0) {
        throw "MakeAppx.exe packaging failed for $arch!"
    }

    Write-Host "Successfully created MSIX package: $packageFilePath" -ForegroundColor Green
    $builtPackages += $packageFilePath
}

# ---------------------------------------------------------------------
# 5. Create MSIX Bundle if requested
# ---------------------------------------------------------------------
$bundleFilePath = $null
if ($CreateBundle -and $builtPackages.Count -gt 0) {
    Write-Host "`n----------------------------------------------------------" -ForegroundColor Yellow
    Write-Host " Creating MSIX Bundle..." -ForegroundColor Yellow
    Write-Host "----------------------------------------------------------" -ForegroundColor Yellow

    $bundleStagingDir = Join-Path $OutputDir "bundle-staging"
    if (Test-Path -LiteralPath $bundleStagingDir) {
        Remove-Item -LiteralPath $bundleStagingDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $bundleStagingDir -Force | Out-Null

    foreach ($pkg in $builtPackages) {
        Copy-Item -LiteralPath $pkg -Destination $bundleStagingDir -Force
    }

    $bundleFileName = "PrivGvard-$PackageVersion.msixbundle"
    $bundleFilePath = Join-Path $OutputDir $bundleFileName

    if (Test-Path -LiteralPath $bundleFilePath) {
        Remove-Item -LiteralPath $bundleFilePath -Force
    }

    & $MakeAppxPath bundle /d $bundleStagingDir /p $bundleFilePath /o
    if ($LASTEXITCODE -ne 0) {
        throw "MakeAppx.exe bundle creation failed!"
    }

    # Cleanup bundle staging
    Remove-Item -LiteralPath $bundleStagingDir -Recurse -Force
    Write-Host "Successfully created MSIX bundle: $bundleFilePath" -ForegroundColor Green
}

# ---------------------------------------------------------------------
# 6. Digital Signing (Optional, for sideloading / testing)
# ---------------------------------------------------------------------
if ($SignPackage -or $CreateSelfSignedCert) {
    Write-Host "`n----------------------------------------------------------" -ForegroundColor Yellow
    Write-Host " Digital Signing..." -ForegroundColor Yellow
    Write-Host "----------------------------------------------------------" -ForegroundColor Yellow

    $thumbprint = $CertificateThumbprint

    if (-not $thumbprint) {
        $certSubject = $Publisher
        if (-not $certSubject.StartsWith("CN=")) {
            $certSubject = "CN=$Publisher"
        }

        # Search existing certificate in Cert:\CurrentUser\My
        $existingCert = Get-ChildItem "Cert:\CurrentUser\My" -ErrorAction SilentlyContinue |
            Where-Object { $_.Subject -eq $certSubject } |
            Select-Object -First 1

        if ($existingCert) {
            $thumbprint = $existingCert.Thumbprint
            Write-Host "Found matching certificate in Cert:\CurrentUser\My (Thumbprint: $thumbprint)" -ForegroundColor Cyan
        } elseif ($CreateSelfSignedCert) {
            Write-Host "Generating local development certificate ($certSubject)..." -ForegroundColor Cyan
            $newCert = New-SelfSignedCertificate `
                -Type Custom `
                -Subject $certSubject `
                -KeyUsage DigitalSignature `
                -FriendlyName "PrivGvard MSIX Development" `
                -CertStoreLocation "Cert:\CurrentUser\My" `
                -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3")

            $thumbprint = $newCert.Thumbprint

            # Export public .cer only (no private keys)
            $certDir = Join-Path $ProjectRoot "certificates"
            if (-not (Test-Path -LiteralPath $certDir)) {
                New-Item -ItemType Directory -Path $certDir -Force | Out-Null
            }
            $cerPath = Join-Path $certDir "PrivGvard-Dev.cer"
            Export-Certificate -Cert $newCert -FilePath $cerPath -Force | Out-Null
            Write-Host "Public certificate exported to: $cerPath" -ForegroundColor DarkGray
        } else {
            Write-Warning "No certificate found for $certSubject in Cert:\CurrentUser\My. Specify -CertificateThumbprint or -CreateSelfSignedCert."
        }
    }

    if ($thumbprint) {
        $targetsToSign = @()
        if ($bundleFilePath) {
            $targetsToSign += $bundleFilePath
        } else {
            $targetsToSign += $builtPackages
        }

        foreach ($target in $targetsToSign) {
            Write-Host "Signing $target with thumbprint $thumbprint..." -ForegroundColor Cyan
            & $SignToolPath sign /fd SHA256 /sha1 $thumbprint /s My $target

            if ($LASTEXITCODE -ne 0) {
                Write-Warning "SignTool signing returned exit code $LASTEXITCODE"
            } else {
                Write-Host "Successfully signed: $([System.IO.Path]::GetFileName($target))" -ForegroundColor Green
            }
        }
    }
}

Write-Host "`n==========================================================" -ForegroundColor Green
Write-Host " BUILD COMPLETE!" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
foreach ($pkg in $builtPackages) {
    Write-Host " Package: $([System.IO.Path]::GetFullPath($pkg))" -ForegroundColor White
}
if ($bundleFilePath) {
    Write-Host " Bundle:  $([System.IO.Path]::GetFullPath($bundleFilePath))" -ForegroundColor White
}
Write-Host "`nFor Microsoft Store submission:" -ForegroundColor Cyan
Write-Host " Upload the .msix or .msixbundle to Microsoft Partner Center." -ForegroundColor DarkGray
Write-Host " Microsoft Store handles official production signing automatically." -ForegroundColor DarkGray
