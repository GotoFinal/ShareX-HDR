using System;
using System.IO;
using System.Reflection;
using Vortice.Direct3D;

namespace ShareX.ScreenCaptureLib.AdvancedGraphics.Direct3D.Shaders
{
    class HLSLShaderIncludeHandler : Include
    {
        public IDisposable Shadow { get; set; }

        public void Close(Stream stream)
        {
            stream.Close();
            stream.Dispose();
        }

        public void Dispose()
        {
            Shadow?.Dispose();
        }

        public Stream Open(IncludeType type, string fileName, Stream parentStream)
        {
            var assembly = Assembly.GetExecutingAssembly();
            return assembly.GetManifestResourceStream($"{ShaderConstants.ResourcePrefix}.{fileName}");
        }
    }
}
