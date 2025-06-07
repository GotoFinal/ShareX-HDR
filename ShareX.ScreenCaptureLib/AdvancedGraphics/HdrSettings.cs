using System;

namespace ShareX.ScreenCaptureLib.AdvancedGraphics;

// TODO: all of this should be exposed in GUI
public class HdrSettings
{
    // TODO: store in better place?
    public static HdrSettings Instance { get; } = new HdrSettings();

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

    public bool Use99ThPercentileMaxCll { get; set; } = true;
    public HdrMode HdrMode { get; set; } = HdrMode.Hdr16Bpc;
    public HdrToneMapType HdrToneMapType { get; set; } = HdrToneMapType.MapCllToDisplay;
}