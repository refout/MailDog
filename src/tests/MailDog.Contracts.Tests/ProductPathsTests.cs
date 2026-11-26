using MailDog.Contracts.Platform;

namespace MailDog.Contracts.Tests;

public sealed class ProductPathsTests
{
    [Fact]
    public void LinuxPrefersXdgDataHome()
    {
        var root = ProductPaths.ResolveDataRoot(
            isLinux: true,
            localApplicationData: "/tmp/local",
            xdgDataHome: "/tmp/xdg",
            home: "/home/maildog",
            fallback: "/tmp/fallback");

        Assert.Equal("/tmp/xdg", root);
    }

    [Fact]
    public void LinuxFallsBackToHomeLocalShare()
    {
        var root = ProductPaths.ResolveDataRoot(
            isLinux: true,
            localApplicationData: null,
            xdgDataHome: null,
            home: "/home/maildog",
            fallback: "/tmp/fallback");

        Assert.Equal(Path.Combine("/home/maildog", ".local", "share"), root);
    }

    [Fact]
    public void LinuxDoesNotUseWindowsLocalAppData()
    {
        var root = ProductPaths.ResolveDataRoot(
            isLinux: true,
            localApplicationData: "/tmp/local",
            xdgDataHome: null,
            home: "/home/maildog",
            fallback: "/tmp/fallback");

        Assert.Equal(Path.Combine("/home/maildog", ".local", "share"), root);
    }

    [Fact]
    public void WindowsUsesLocalAppData()
    {
        var root = ProductPaths.ResolveDataRoot(
            isLinux: false,
            localApplicationData: @"C:\Users\maildog\AppData\Local",
            xdgDataHome: null,
            home: @"C:\Users\maildog",
            fallback: @"C:\fallback");

        Assert.Equal(@"C:\Users\maildog\AppData\Local", root);
    }

    [Fact]
    public void FallsBackWhenNoPlatformRootIsAvailable()
    {
        var root = ProductPaths.ResolveDataRoot(
            isLinux: false,
            localApplicationData: null,
            xdgDataHome: null,
            home: null,
            fallback: "/tmp/fallback");

        Assert.Equal("/tmp/fallback", root);
    }
}
