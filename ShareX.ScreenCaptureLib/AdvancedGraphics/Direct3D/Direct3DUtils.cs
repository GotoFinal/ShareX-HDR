using System.Numerics;
using ShareX.ScreenCaptureLib.AdvancedGraphics.Direct3D.Shaders;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ShareX.ScreenCaptureLib.AdvancedGraphics.Direct3D;

public class Direct3DUtils
{
    public static ID3D11Texture2D CreateCanvasTexture(uint width, uint height, ID3D11Device device)
    {
        // We'll build one big RGBA8_UNORM texture that can be used as a render‐target
        // or as a copy‐destination.  (We do NOT need mipmaps, and we will copy into it.)
        var desc = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.None, // Not a render‐target, just a copy target
            CPUAccessFlags = CpuAccessFlags.None, // We will COPY from this into a staging at the very end
            MiscFlags = ResourceOptionFlags.None
        };

        return device.CreateTexture2D(desc);
    }


    /// After you finish copying all regions into this “canvas,” you can do:
    ///    var staging = CreateStagingFor(canvasTex);
    ///    ctx.CopyResource(staging, canvasTex);
    ///    Map+Encode…
    public static ID3D11Texture2D CreateStagingFor(ID3D11Texture2D gpuTex)
    {
        var desc = gpuTex.Description;
        var stagingDesc = new Texture2DDescription
        {
            Width = desc.Width,
            Height = desc.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = desc.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None
        };
        return gpuTex.Device.CreateTexture2D(stagingDesc);
    }

    public static ShaderInputStructure[] ConstructForScreen(ModernCaptureMonitorDescription region)
    {
        return
        [ // Left-Top
            new ShaderInputStructure
            {
                Position = new Vector2(region.DestD3DVsTopLeft.X, region.DestD3DVsTopLeft.Y),
                TextureCoord = new Vector2(region.DestD3DPsSamplerTopLeft.X, region.DestD3DPsSamplerTopLeft.Y),
            },
            // Right-Top
            new ShaderInputStructure
            {
                Position = new Vector2(region.DestD3DVsBottomRight.X, region.DestD3DVsTopLeft.Y),
                TextureCoord = new Vector2(region.DestD3DPsSamplerBottomRight.X, region.DestD3DPsSamplerTopLeft.Y)
            },
            // Left-Bottom
            new ShaderInputStructure
            {
                Position = new Vector2(region.DestD3DVsTopLeft.X, region.DestD3DVsBottomRight.Y),
                TextureCoord = new Vector2(region.DestD3DPsSamplerTopLeft.X, region.DestD3DPsSamplerBottomRight.Y)
            },
            // Right-Top
            new ShaderInputStructure
            {
                Position = new Vector2(region.DestD3DVsBottomRight.X, region.DestD3DVsTopLeft.Y),
                TextureCoord = new Vector2(region.DestD3DPsSamplerBottomRight.X, region.DestD3DPsSamplerTopLeft.Y)
            },
            // Right-Bottom
            new ShaderInputStructure
            {
                Position = new Vector2(region.DestD3DVsBottomRight.X, region.DestD3DVsBottomRight.Y),
                TextureCoord = new Vector2(region.DestD3DPsSamplerBottomRight.X, region.DestD3DPsSamplerBottomRight.Y)
            },
            // Left-Bottom
            new ShaderInputStructure
            {
                Position = new Vector2(region.DestD3DVsTopLeft.X, region.DestD3DVsBottomRight.Y),
                TextureCoord = new Vector2(region.DestD3DPsSamplerTopLeft.X, region.DestD3DPsSamplerBottomRight.Y)
            }
        ];
    }
}