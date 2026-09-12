using Moq;
using PrivLock.Application.Services;
using PrivLock.Domain.Results;
using PrivLock.Platform.Abstractions;
using Xunit;

namespace PrivLock.Application.Tests;

public class SettingsServiceTests
{
    private readonly Mock<IAutostartProvider> _autostartMock;
    private readonly Mock<IStateStore> _storeMock;
    private readonly SettingsService _service;

    public SettingsServiceTests()
    {
        _autostartMock = new Mock<IAutostartProvider>();
        _storeMock = new Mock<IStateStore>();
        _storeMock.Setup(s => s.Load()).Returns(new DesiredState());

        _service = new SettingsService(_autostartMock.Object, _storeMock.Object);
    }

    [Fact]
    public void SetAutostart_True_EnablesAndSavesState()
    {
        _autostartMock.Setup(a => a.EnableAutostart()).Returns(OperationResult.Ok());

        var result = _service.SetAutostart(true);

        Assert.True(result.Success);
        _autostartMock.Verify(a => a.EnableAutostart(), Times.Once);
        _storeMock.Verify(s => s.Save(It.Is<DesiredState>(ds => ds.Autostart)), Times.Once);
    }

    [Fact]
    public void SetAutostart_False_DisablesAndSavesState()
    {
        _autostartMock.Setup(a => a.DisableAutostart()).Returns(OperationResult.Ok());

        var result = _service.SetAutostart(false);

        Assert.True(result.Success);
        _autostartMock.Verify(a => a.DisableAutostart(), Times.Once);
        _storeMock.Verify(s => s.Save(It.Is<DesiredState>(ds => !ds.Autostart)), Times.Once);
    }
}
