using Fusebox.Core.Docked;

public class DockedTests
{
    [Fact]
    public void UsbSpeedNowVsMax()
    {
        // SuperSpeed device on a USB 2 link = the "runs slower than it can" case
        var slow = UsbSpeed.Describe(2, UsbSpeed.FlagCapableSuper, UsbSpeed.ProtoUsb300);
        Assert.Equal("USB 2 (480 Mb/s)", slow.Now);
        Assert.Equal("USB 3.2 Gen 1 (5 Gb/s)", slow.Max);
        Assert.True(slow.Slow);

        var gen2 = UsbSpeed.Describe(3, UsbSpeed.FlagRunsSuper | UsbSpeed.FlagCapableSuper | UsbSpeed.FlagRunsSuperPlus | UsbSpeed.FlagCapableSuperPlus, 4);
        Assert.Equal("USB 3.2 Gen 2 (10 Gb/s)", gen2.Now);
        Assert.False(gen2.Slow);

        var gen1On2Cap = UsbSpeed.Describe(3, UsbSpeed.FlagRunsSuper | UsbSpeed.FlagCapableSuper | UsbSpeed.FlagCapableSuperPlus, 4);
        Assert.True(gen1On2Cap.Slow);

        var mouse = UsbSpeed.Describe(1, 0, 0);
        Assert.Equal("USB 1 (12 Mb/s)", mouse.Now);
        Assert.False(mouse.Slow);
        Assert.False(UsbSpeed.Describe(2, 0, 0).Slow);
    }

    [Fact]
    public void DisplayMapping()
    {
        Assert.Equal("HDMI", DisplayMap.Connection(5));
        Assert.Equal("DisplayPort", DisplayMap.Connection(10));
        Assert.Equal("USB-C (DP alt mode)", DisplayMap.Connection(18));
        Assert.Equal("Built-in", DisplayMap.Connection(0x80000000));
        Assert.Equal(59.94, DisplayMap.Hz(60000, 1001));
        Assert.Equal(0, DisplayMap.Hz(1, 0));
        Assert.Equal("60 Hz", DisplayMap.HzText(59.997));
        Assert.Equal("59.94 Hz", DisplayMap.HzText(59.94));
    }

    [Fact]
    public void BluetoothBatteryAndPower()
    {
        Assert.Equal(80, BtBattery.Parse([80]));
        Assert.Null(BtBattery.Parse([255]));
        Assert.Null(BtBattery.Parse(null));
        var p = PowerMap.From(1, 9, 87);   // on AC, high + charging
        Assert.True(p.OnAc); Assert.True(p.Charging); Assert.Equal(87, p.Percent);
        var d = PowerMap.From(1, 128, 255);   // desktop, no battery
        Assert.False(d.HasBattery); Assert.Null(d.Percent);
    }

    [Fact]
    public void AudioFormFactor()
    {
        Assert.Equal("Headphones", AudioMap.FormFactor(3));
        Assert.Equal("Audio", AudioMap.FormFactor(10));
    }
}
