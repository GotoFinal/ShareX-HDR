using System.Numerics;
using System.Runtime.InteropServices;

namespace ShareX.ScreenCaptureLib.AdvancedGraphics.Direct3D.Shaders
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public unsafe struct Vertex(Vector2 position, Vector2 textureCoord)
    {
        public Vector2 Position = position;
        public Vector2 TextureCoord = textureCoord;

        public static uint SizeInBytes => (uint)sizeof(Vertex);
    }
}