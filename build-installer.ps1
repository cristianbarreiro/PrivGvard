# =====================================================================
# PrivGvard - Automated Build & Packaging Pipeline
# =====================================================================

$ErrorActionPreference = "Stop"
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
Write-Host " Building & Packaging PrivGvard Installer & Portable" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Run Unit Tests
Write-Host "`n[1/5] Running unit test suite..." -ForegroundColor Yellow
dotnet test "$ProjectRoot\CamMicBlocker.sln" --configuration Release
if ($LASTEXITCODE -ne 0) {
    Write-Error "Unit tests failed! Aborting installer build."
    exit 1
}

# 2. Clean previous build outputs
Write-Host "`n[2/5] Cleaning previous output directories..." -ForegroundColor Yellow
$PublishRoot = Join-Path $ProjectRoot "publish_out"
$PublishDir = Join-Path $PublishRoot "win-x64"
$InstallerOutDir = Join-Path $ProjectRoot "installer_out"

# A forced termination would bypass the reversible-session shutdown coordinator and can strand
# camera/microphone state. Require the operator to exit PrivGvard normally instead.
$RunningInstances = Get-Process -Name "PrivGvard", "PrivLock", "CamMicBlocker" -ErrorAction SilentlyContinue
if ($RunningInstances) {
    Write-Error "PrivGvard (or legacy instance) is running. Exit it normally from the tray/window so privacy state is restored, then rerun this script."
    exit 1
}

Remove-VerifiedBuildDirectory $PublishDir "publish_out\win-x64"
Remove-VerifiedBuildDirectory $InstallerOutDir "installer_out"
if (-not (Test-Path -LiteralPath $InstallerOutDir)) { New-Item -ItemType Directory -Path $InstallerOutDir | Out-Null }

# 3. Publish Single-File Self-Contained Binary
Write-Host "`n[3/5] Publishing single-file self-contained win-x64 release..." -ForegroundColor Yellow
dotnet publish "$ProjectRoot\src\PrivLock.Desktop\PrivLock.Desktop.csproj" `
    -c Release `
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

# Inno recursively consumes this directory. Revalidate it after publish so a reparse point cannot
# make the installer capture files from outside the intended RID-specific output directory.
Assert-NoReparsePointInBuildPath $PublishDir
Assert-NoReparsePointInDirectoryTree $PublishDir

# 4. Locate Inno Setup Compiler (ISCC.exe) and compile setup executable
Write-Host "`n[4/5] Compiling Windows Setup Installer with Inno Setup..." -ForegroundColor Yellow

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
& $IsccPath "/O$InstallerOutDir" "/FPrivGvard-Setup-1.0.0" "$ProjectRoot\installer\setup.iss"

if ($LASTEXITCODE -ne 0) {
    Write-Error "Installer compilation failed!"
    exit 1
}

# 5. Create Portable Distribution ZIP
Write-Host "`n[5/5] Packaging Portable distribution ZIP..." -ForegroundColor Yellow
$PortableZipPath = Join-Path $InstallerOutDir "PrivGvard-Portable-1.0.0.zip"
if (Test-Path -LiteralPath $PortableZipPath) {
    Remove-Item -LiteralPath $PortableZipPath -Force
}
Compress-Archive -Path "$PublishDir\*" -DestinationPath $PortableZipPath -Force

Write-Host "`n==========================================================" -ForegroundColor Green
Write-Host " SUCCESS! Release assets generated successfully at:" -ForegroundColor Green
Write-Host " Setup:    $InstallerOutDir\PrivGvard-Setup-1.0.0.exe" -ForegroundColor White
Write-Host " Portable: $InstallerOutDir\PrivGvard-Portable-1.0.0.zip" -ForegroundColor White
Write-Host " Executable: $PublishDir\PrivGvard.exe" -ForegroundColor White
Write-Host "==========================================================" -ForegroundColor Green
