using System.Numerics;
using System.Runtime.InteropServices;

namespace ShareX.ScreenCaptureLib.AdvancedGraphics.Direct3D.Shaders
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public unsafe struct ShaderInputStructure
    {
        public Vector2 Position;
        public Vector2 TextureCoord;

        public static uint SizeInBytes => (uint)sizeof(ShaderInputStructure);

        public ShaderInputStructure(Vector2 position, Vector2 textureCoord) : this()
        {
            Position = position;
            TextureCoord = textureCoord;
        }
    }
}