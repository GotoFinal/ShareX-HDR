
using System.Numerics;
using System.Runtime.InteropServices;

namespace ShareX.ScreenCaptureLib.AdvancedGraphics.Direct3D.Shaders
{
    [StructLayout(LayoutKind.Sequential)]
    public struct ShaderInputStructure
    {
        public Vector3 Position;
        public float a;
        public Vector2 TextureCoord;
        public Vector2 a2;

        public ShaderInputStructure(Vector3 position, Vector2 textureCoord) : this()
        {
            Position = position;
            TextureCoord = textureCoord;
        }
    }
}
