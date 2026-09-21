using System.Reflection;
using PrivLock.Infrastructure.Common.Localization;
using PrivLock.Infrastructure.Common.Storage;
using Xunit;

namespace PrivLock.Infrastructure.Tests;

public class Release20MetadataTests
{
    [Fact]
    public void AssemblyVersion_IsMajorVersion2()
    {
        var version = typeof(StorageMigrationHelper).Assembly.GetName().Version;
        Assert.NotNull(version);
        Assert.Equal(2, version.Major);
        Assert.Equal(0, version.Minor);
        Assert.Equal(0, version.Build);
    }

    [Fact]
    public void Localization_AppTitle_IsPrivGvard()
    {
        Assert.Equal("PrivGvard", LocalizationCatalog.Get("AppTitle", "es"));
        Assert.Equal("PrivGvard", LocalizationCatalog.Get("AppTitle", "en"));
    }

    [Fact]
    public void StorageMigration_TargetDirectoryName_IsPrivGvard()
    {
        var dataDir = StorageMigrationHelper.GetDefaultDataDirectory();
        Assert.EndsWith("PrivGvard", dataDir, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackageAppxManifest_HasOfficialStoreIdentity()
    {
        var repoRoot = FindRepoRoot();
        var manifestPath = Path.Combine(repoRoot, "packaging", "Package.appxmanifest");
        Assert.True(File.Exists(manifestPath), $"Manifest not found at {manifestPath}");

        var doc = System.Xml.Linq.XDocument.Load(manifestPath);
        var ns = doc.Root?.Name.Namespace ?? System.Xml.Linq.XNamespace.None;

        var identity = doc.Root?.Element(ns + "Identity");
        Assert.NotNull(identity);
        Assert.Equal("cdevstudios.PrivGvard", identity.Attribute("Name")?.Value);
        Assert.Equal("CN=85AB4167-A0AE-4FDF-B840-B95CD225F7DD", identity.Attribute("Publisher")?.Value);

        var properties = doc.Root?.Element(ns + "Properties");
        Assert.NotNull(properties);
        var pubDisplayName = properties.Element(ns + "PublisherDisplayName")?.Value;
        Assert.Equal("cdev studios", pubDisplayName);
        Assert.False(pubDisplayName?.EndsWith(" "), "PublisherDisplayName must not have trailing whitespace.");
        Assert.Equal("PrivGvard", properties.Element(ns + "DisplayName")?.Value);

        var app = doc.Root?.Element(ns + "Applications")?.Element(ns + "Application");
        Assert.NotNull(app);
        Assert.Equal("PrivGvard.exe", app.Attribute("Executable")?.Value);
    }

    private static string FindRepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "PrivGvard.sln")))
            {
                return current;
            }
            var parent = Directory.GetParent(current);
            current = parent?.FullName;
        }
        throw new InvalidOperationException("Could not find repository root containing PrivGvard.sln");
    }
}
