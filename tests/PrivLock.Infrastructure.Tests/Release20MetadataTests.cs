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
}
