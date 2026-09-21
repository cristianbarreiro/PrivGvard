using Moq;
using PrivLock.Application.Services;
using PrivLock.Platform.Abstractions;
using Xunit;

namespace PrivLock.Application.Tests;

public class LocalizationServiceTests
{
    private readonly Mock<IStateStore> _storeMock;
    private readonly LocalizationService _service;

    public LocalizationServiceTests()
    {
        _storeMock = new Mock<IStateStore>();
        _storeMock.Setup(s => s.Load()).Returns(new DesiredState { Language = "es" });

        _service = new LocalizationService(_storeMock.Object);
        _service.Initialize();
    }

    [Fact]
    public void Initialize_LoadsSavedLanguage()
    {
        Assert.Equal("es", _service.CurrentLanguage);
        Assert.Equal("🔒 Bloqueado", _service.GetString("StatusBlocked"));
    }

    [Fact]
    public void SetLanguage_English_SwitchesLanguageAndSaves()
    {
        var languageChangedFired = false;
        _service.LanguageChanged += lang => languageChangedFired = (lang == "en");

        _service.SetLanguage("en");

        Assert.Equal("en", _service.CurrentLanguage);
        Assert.Equal("🔒 Blocked", _service.GetString("StatusBlocked"));
        Assert.True(languageChangedFired);
        _storeMock.Verify(s => s.Save(It.Is<DesiredState>(ds => ds.Language == "en")), Times.Once);
    }

    [Fact]
    public void SetLanguage_DynamicSwitching_EsToEnAndBackToEs()
    {
        var languageHistory = new List<string>();
        _service.LanguageChanged += lang => languageHistory.Add(lang);

        // Initial is "es"
        Assert.Equal("es", _service.CurrentLanguage);
        Assert.Equal("Sin protección", _service.GetString("BadgeUnprotected"));
        Assert.Equal("Usuario estándar", _service.GetString("UserStandard"));
        Assert.Equal("Abrir PrivGvard", _service.GetString("TrayOpen"));
        Assert.Equal("Salir", _service.GetString("TrayExit"));

        // Switch ES -> EN
        _service.SetLanguage("en");
        Assert.Equal("en", _service.CurrentLanguage);
        Assert.Equal("Unprotected", _service.GetString("BadgeUnprotected"));
        Assert.Equal("Standard User", _service.GetString("UserStandard"));
        Assert.Equal("Open PrivGvard", _service.GetString("TrayOpen"));
        Assert.Equal("Exit", _service.GetString("TrayExit"));

        // Switch EN -> ES
        _service.SetLanguage("es");
        Assert.Equal("es", _service.CurrentLanguage);
        Assert.Equal("Sin protección", _service.GetString("BadgeUnprotected"));
        Assert.Equal("Usuario estándar", _service.GetString("UserStandard"));
        Assert.Equal("Abrir PrivGvard", _service.GetString("TrayOpen"));
        Assert.Equal("Salir", _service.GetString("TrayExit"));

        Assert.Equal(new[] { "en", "es" }, languageHistory);
    }
}
