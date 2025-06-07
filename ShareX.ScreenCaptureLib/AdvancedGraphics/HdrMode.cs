namespace ShareX.ScreenCaptureLib.AdvancedGraphics;

public enum HdrMode
{
    NoHDR,
    Hdr10Bpc,
    Hdr16Bpc
}

public enum HdrToneMapType
{
    None = 0x0, // Let the display figure it out
    Clip = 0x1, // Truncate the image before display
    InfiniteRolloff = 0x2, // Reduce to finite range (i.e. x/(1+x))
    NormalizeToCll = 0x4, // Content range mapped to [0,1]
    MapCllToDisplay = 0x8 // Content range mapped to display range
}