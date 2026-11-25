using MailDog.UI.Services;

namespace MailDog.UI.Tests;

public sealed class AppLockServiceTests
{
    [Fact]
    public void HashUsesRandomSaltAndVerifiesOnlyTheOriginalPin()
    {
        var salt = AppLockService.CreateSalt();
        var hash = AppLockService.Hash("246810", salt);

        Assert.True(AppLockService.Verify("246810", salt, hash));
        Assert.False(AppLockService.Verify("246811", salt, hash));
        Assert.False(AppLockService.Verify(string.Empty, salt, hash));
        Assert.False(AppLockService.Verify("246810", AppLockService.CreateSalt(), hash));
    }

    [Fact]
    public void SaltAndHashAreStableAndBase64()
    {
        var salt = AppLockService.CreateSalt();
        var hash = AppLockService.Hash("1234", salt);

        Assert.Equal(24, salt.Length);
        Assert.Equal(44, hash.Length);
        Assert.Equal(hash, AppLockService.Hash("1234", salt));
        Assert.NotEqual(salt, AppLockService.CreateSalt());
    }

    [Fact]
    public void VerifyRejectsMalformedStoredValues()
    {
        Assert.False(AppLockService.Verify("1234", "not-base64", "not-base64"));
        Assert.False(AppLockService.Verify("1234", string.Empty, string.Empty));
        Assert.False(AppLockService.Verify("1234", null!, null!));
    }
}
