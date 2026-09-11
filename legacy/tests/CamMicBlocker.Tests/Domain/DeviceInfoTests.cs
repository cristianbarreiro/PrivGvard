using CamMicBlocker.Domain.Models;
using Xunit;

namespace CamMicBlocker.Tests.Domain;

public class DeviceInfoTests
{
    [Fact]
    public void Equality_SameInstanceId_AreEqual()
    {
        var device1 = new DeviceInfo
        {
            InstanceId = "USB\\VID_046D&PID_0825\\12345",
            FriendlyName = "Logitech Webcam",
            DeviceType = DeviceType.Camera
        };
        var device2 = new DeviceInfo
        {
            InstanceId = "USB\\VID_046D&PID_0825\\12345",
            FriendlyName = "Different Name",  // Name doesn't affect equality
            DeviceType = DeviceType.Camera
        };

        Assert.Equal(device1, device2);
        Assert.Equal(device1.GetHashCode(), device2.GetHashCode());
    }

    [Fact]
    public void Equality_DifferentInstanceId_AreNotEqual()
    {
        var device1 = new DeviceInfo
        {
            InstanceId = "USB\\VID_046D&PID_0825\\12345",
            FriendlyName = "Logitech Webcam",
            DeviceType = DeviceType.Camera
        };
        var device2 = new DeviceInfo
        {
            InstanceId = "USB\\VID_046D&PID_0825\\99999",
            FriendlyName = "Logitech Webcam",
            DeviceType = DeviceType.Camera
        };

        Assert.NotEqual(device1, device2);
    }

    [Fact]
    public void Equality_CaseInsensitive_AreEqual()
    {
        var device1 = new DeviceInfo
        {
            InstanceId = "USB\\VID_046D&PID_0825\\12345",
            FriendlyName = "Webcam",
            DeviceType = DeviceType.Camera
        };
        var device2 = new DeviceInfo
        {
            InstanceId = "usb\\vid_046d&pid_0825\\12345",
            FriendlyName = "Webcam",
            DeviceType = DeviceType.Camera
        };

        Assert.Equal(device1, device2);
    }

    [Fact]
    public void ToString_ContainsRelevantInfo()
    {
        var device = new DeviceInfo
        {
            InstanceId = "USB\\VID_046D",
            FriendlyName = "Logitech Webcam",
            DeviceType = DeviceType.Camera,
            IsEnabled = true
        };

        var str = device.ToString();
        Assert.Contains("Camera", str);
        Assert.Contains("Logitech Webcam", str);
        Assert.Contains("USB\\VID_046D", str);
        Assert.Contains("Enabled", str);
    }

    [Fact]
    public void CanBeUsedInHashSet()
    {
        var set = new HashSet<DeviceInfo>
        {
            new() { InstanceId = "ID1", FriendlyName = "Dev1", DeviceType = DeviceType.Camera },
            new() { InstanceId = "ID1", FriendlyName = "Dev1 Alias", DeviceType = DeviceType.Camera }, // Duplicate
            new() { InstanceId = "ID2", FriendlyName = "Dev2", DeviceType = DeviceType.Microphone }
        };

        Assert.Equal(2, set.Count); // ID1 appears only once
    }
}
