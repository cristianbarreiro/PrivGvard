using PrivLock.Platform.Windows.System;
using Xunit;

namespace PrivLock.Platform.Windows.Tests;

public sealed class PackageIdentityHelperTests
{
    [Fact]
    public void IsRunningAsPackaged_InTestHost_ReturnsFalseWithoutThrowing()
    {
        // When running under standard xUnit testhost, process has no package identity
        var isPackaged = PackageIdentityHelper.IsRunningAsPackaged;
        Assert.False(isPackaged);
    }

    [Fact]
    public void PackageFullName_InTestHost_ReturnsNullWithoutThrowing()
    {
        var fullName = PackageIdentityHelper.PackageFullName;
        Assert.Null(fullName);
    }
}
