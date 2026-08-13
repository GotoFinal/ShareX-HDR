using ShareX.ImageEditor.Core.Editor;
using ShareX.ImageEditor.Hosting;
using ShareX.ImageEditor.Presentation.Effects;
using SkiaSharp;

namespace ShareX.ScreenCaptureLib.Tests.HDR;

public sealed class EditorImageEffectDescriptorTests
{
    [Fact]
    public void CreateDescriptor_CapturesTypedNormalizedParameterValues()
    {
        Assert.True(ImageEffectCatalog.TryGetDefinition("brightness", out EffectDefinition? definition));
        Assert.NotNull(definition);

        EffectParameterState[] states = definition.CoreParameters
            .Select(EffectParameterState.Create)
            .ToArray();
        SliderParameterState amount = Assert.IsType<SliderParameterState>(Assert.Single(states));
        amount.Value = 37.5;

        EditorImageEffectDescriptor descriptor = definition.CreateDescriptor(states);

        Assert.Equal(EditorImageEffectDescriptor.CurrentSchemaVersion, descriptor.SchemaVersion);
        Assert.Equal("brightness", descriptor.EffectId);
        EditorImageEffectParameter parameter = Assert.Single(descriptor.Parameters);
        Assert.Equal("amount", parameter.Key);
        Assert.Equal(EditorImageEffectParameterKind.Number, parameter.Kind);
        Assert.Equal(37.5, parameter.NumberValue);
        Assert.Equal(EditorImageEffectReplayDeterminism.Unspecified, descriptor.ReplayDeterminism);
        Assert.Null(descriptor.ReplaySeed);
    }

    [Fact]
    public void Descriptor_OwnsParameterListAndRejectsDuplicateKeys()
    {
        var parameters = new List<EditorImageEffectParameter>
        {
            EditorImageEffectParameter.Boolean("enabled", true)
        };

        var descriptor = new EditorImageEffectDescriptor("test", parameters);
        parameters.Clear();

        Assert.Single(descriptor.Parameters);
        Assert.Throws<ArgumentException>(() => new EditorImageEffectDescriptor(
            "duplicate",
            [
                EditorImageEffectParameter.Number("amount", 1),
                EditorImageEffectParameter.Number("AMOUNT", 2)
            ]));
        Assert.Throws<ArgumentOutOfRangeException>(() => EditorImageEffectParameter.Number("amount", double.NaN));
    }

    [Fact]
    public void ApplyImageEffect_RecordsDescriptorThroughUndoRedo()
    {
        using var editor = new EditorCore();
        editor.LoadImage(CreateSolidBitmap(SKColors.CornflowerBlue));
        var descriptor = new EditorImageEffectDescriptor(
            "brightness",
            [EditorImageEffectParameter.Number("amount", 25)]);

        Assert.True(editor.ApplyImageEffect(source => source.Copy(), descriptor));

        EditorSourceOperation operation = Assert.Single(editor.SourceOperations);
        Assert.Equal(EditorSourceOperationKind.ImageEffect, operation.Kind);
        Assert.Same(descriptor, operation.ImageEffectDescriptor);

        editor.Undo();
        Assert.Empty(editor.SourceOperations);

        editor.Redo();
        EditorSourceOperation restored = Assert.Single(editor.SourceOperations);
        Assert.Equal(EditorSourceOperationKind.ImageEffect, restored.Kind);
        Assert.Same(descriptor, restored.ImageEffectDescriptor);
    }

    [Fact]
    public void CatalogEffects_AreCapturedAndConservativelySdrOnlyUntilRegistered()
    {
        var nativeEffectIds = new HashSet<string>(
            ["alpha", "exposure", "grayscale", "saturation"], StringComparer.OrdinalIgnoreCase);
        Assert.NotEmpty(ImageEffectCatalog.Definitions);

        foreach (EffectDefinition definition in ImageEffectCatalog.Definitions)
        {
            IEnumerable<EffectParameterState> states = definition.CoreParameters.Count > 0
                ? definition.CoreParameters.Select(EffectParameterState.Create)
                : definition.Parameters.Select(EffectParameterState.Create);
            EditorImageEffectDescriptor descriptor = definition.CreateDescriptor(states);

            Assert.Equal(definition.Id, descriptor.EffectId);
            Assert.Equal(
                nativeEffectIds.Contains(descriptor.EffectId)
                    ? EditorImageEffectHdrCapability.NativeFp16
                    : EditorImageEffectHdrCapability.SdrOnly,
                EditorImageEffectHdrCapabilities.GetCapability(descriptor.EffectId));
            Assert.Equal(
                nativeEffectIds.Contains(descriptor.EffectId)
                    ? EditorImageEffectReplayDeterminism.Deterministic
                    : EditorImageEffectReplayDeterminism.Unspecified,
                descriptor.ReplayDeterminism);
        }
    }

    [Fact]
    public void HdrReplayGate_RequiresRegisteredVersionedDeterministicDescriptor()
    {
        var validDescriptor = new EditorImageEffectDescriptor(
            "alpha",
            [EditorImageEffectParameter.Number("amount", 50)],
            EditorImageEffectReplayDeterminism.Deterministic);
        using var validExport = new EditorOverlayExport(
            1,
            1,
            EditorSourceOperation.None(),
            [EditorSourceOperation.ImageEffect(validDescriptor)],
            Array.Empty<EditorOverlayOperation>());

        Assert.True(validExport.CanReplayOverHdr);

        var unspecifiedDescriptor = new EditorImageEffectDescriptor(
            "alpha",
            [EditorImageEffectParameter.Number("amount", 50)]);
        using var unspecifiedExport = new EditorOverlayExport(
            1,
            1,
            EditorSourceOperation.None(),
            [EditorSourceOperation.ImageEffect(unspecifiedDescriptor)],
            Array.Empty<EditorOverlayOperation>());
        using var unregisteredExport = new EditorOverlayExport(
            1,
            1,
            EditorSourceOperation.None(),
            [EditorSourceOperation.ImageEffect(new EditorImageEffectDescriptor(
                "not_registered",
                [],
                EditorImageEffectReplayDeterminism.Deterministic))],
            Array.Empty<EditorOverlayOperation>());

        Assert.False(unspecifiedExport.CanReplayOverHdr);
        Assert.False(unregisteredExport.CanReplayOverHdr);
    }

    private static SKBitmap CreateSolidBitmap(SKColor color)
    {
        var bitmap = new SKBitmap(2, 2, SKColorType.Bgra8888, SKAlphaType.Premul);
        bitmap.Erase(color);
        return bitmap;
    }
}
