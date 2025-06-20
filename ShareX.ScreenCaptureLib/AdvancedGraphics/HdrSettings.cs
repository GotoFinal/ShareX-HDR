using System;

namespace ShareX.ScreenCaptureLib.AdvancedGraphics;

// TODO: all of this should be exposed in GUI
public class HdrSettings
{
    private float hdrBrightnessNits = 203;

    public float HdrBrightnessNits
    {
        get => Math.Clamp(hdrBrightnessNits, 80, 400);
        set => hdrBrightnessNits = value;
    }

    private float brightnessScale = 100;

    public float BrightnessScale
    {
        get => Math.Clamp(brightnessScale, 1, 2000);
        set => brightnessScale = value;
    }

    private float sdrWhiteScale = 100;

    public float SdrWhiteScale
    {
        get => Math.Clamp(sdrWhiteScale,
            0, 2000);
        set => sdrWhiteScale = value;
    }

    public bool Use99ThPercentileMaxCll { get; set; } = true;
    public HdrMode HdrMode { get; set; } = HdrMode.Hdr16Bpc;
    public HdrToneMapType HdrToneMapType { get; set; } = HdrToneMapType.NormalizeToCll;

    public PerformanceMode PerformanceMode { get; set; } = PerformanceMode.Balanced;

    public bool ReuseBuffers => PerformanceMode is PerformanceMode.Max;

    // TODO: fiix these... still leaks memory
    public bool AvoidBuffering => PerformanceMode is PerformanceMode.SaveMemory or PerformanceMode.LowMemory;
    public bool SaveDevices => PerformanceMode != PerformanceMode.LowMemory;
}

public enum PerformanceMode
{
    Max,
    Balanced,
    SaveMemory,
    LowMemory
}