using ShareX.ScreenCaptureLib.AdvancedGraphics.Direct3D.Shaders;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ShareX.ScreenCaptureLib.AdvancedGraphics.Direct3D;

public class Tonemapping
{
    public static ID3D11Texture2D TonemapOnCpu(ModernCaptureMonitorDescription stateRegion, DeviceAccess deviceAccess, ID3D11Texture2D dupStateStaging,
        ID3D11Device device, ID3D11DeviceContext ctx, ShaderHdrMetadata stateHdrMetadata)
    {
        return dupStateStaging; // TODO
    }

    public static ID3D11Texture2D TonemapOnGpu(ModernCaptureMonitorDescription region, DeviceAccess deviceAccess,
        ID3D11Texture2D inputHdrTex,
        ID3D11Device device,
        ID3D11DeviceContext ctx, ShaderHdrMetadata metadata)
    {
        var quadVerts = Direct3DUtils.ConstructForScreen(region);

        var vertexBuffer = device.CreateBuffer(quadVerts, BindFlags.VertexBuffer);

        ShaderHdrMetadata[] metadataArray = [metadata];
        var hdrCBuffer = device.CreateBuffer(metadataArray, BindFlags.ConstantBuffer);

        var inDesc = inputHdrTex.Description;
        var ldrDesc = new Texture2DDescription
        {
            Width = inDesc.Width,
            Height = inDesc.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        };
        var ldrTex = device.CreateTexture2D(ldrDesc);
        var ldrRtv = device.CreateRenderTargetView(ldrTex);

        // i dont even know what the fuck this is???
        var hdrSrvDesc = new ShaderResourceViewDescription
        {
            Format = inDesc.Format,
            ViewDimension = ShaderResourceViewDimension.Texture2D,
            Texture2D = new Texture2DShaderResourceView
            {
                MostDetailedMip = 0,
                MipLevels = 1
            }
        };
        var hdrSrv = device.CreateShaderResourceView(inputHdrTex, hdrSrvDesc);

        ctx.OMSetRenderTargets(ldrRtv);

        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.IASetInputLayout(deviceAccess.inputLayout);
        ctx.IASetVertexBuffer(0, vertexBuffer, ShaderInputStructure.SizeInBytes);

        ctx.VSSetShader(deviceAccess.vxShader);
        ctx.PSSetShader(deviceAccess.pxShader);
        ctx.PSSetConstantBuffer(0, hdrCBuffer);
        ctx.PSSetSampler(0, deviceAccess.samplerState);
        ctx.PSSetShaderResource(0, hdrSrv);

        ctx.RSSetViewport(new Viewport(0, 0, inDesc.Width, inDesc.Height, 0, 1));

        ctx.Draw(vertexCount: 6, startVertexLocation: 0);

        hdrSrv.Dispose();
        hdrCBuffer.Dispose();
        vertexBuffer.Dispose();
        ldrRtv.Dispose();

        return ldrTex;
    }
}