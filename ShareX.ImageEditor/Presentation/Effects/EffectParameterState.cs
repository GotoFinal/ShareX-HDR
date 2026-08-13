#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using ShareX.ImageEditor.Core.ImageEffects;
using SkiaSharp;
using CoreCheckboxParameter = ShareX.ImageEditor.Core.ImageEffects.Parameters.CheckboxEffectParameter;
using CoreColorParameter = ShareX.ImageEditor.Core.ImageEffects.Parameters.ColorEffectParameter;
using CoreEffectParameter = ShareX.ImageEditor.Core.ImageEffects.Parameters.EffectParameter;
using CoreEnumParameter = ShareX.ImageEditor.Core.ImageEffects.Parameters.EnumEffectParameter;
using CoreFilePathParameter = ShareX.ImageEditor.Core.ImageEffects.Parameters.FilePathEffectParameter;
using CoreNumericParameter = ShareX.ImageEditor.Core.ImageEffects.Parameters.NumericEffectParameter;
using CoreSliderParameter = ShareX.ImageEditor.Core.ImageEffects.Parameters.SliderEffectParameter;
using CoreTextParameter = ShareX.ImageEditor.Core.ImageEffects.Parameters.TextEffectParameter;

namespace ShareX.ImageEditor.Presentation.Effects;

public abstract partial class EffectParameterState : ObservableObject
{
    protected EffectParameterState(string key, string label)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Label = label ?? throw new ArgumentNullException(nameof(label));
    }

    public string Key { get; }

    public string Label { get; }

    internal abstract object? GetValue();

    internal abstract void ApplyValue(ImageEffect effect);

    public static EffectParameterState Create(EffectParameterDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return definition switch
        {
            SliderParameterDefinition slider => new SliderParameterState(slider),
            CheckboxParameterDefinition checkbox => new CheckboxParameterState(checkbox),
            EnumParameterDefinition enumParameter => new EnumParameterState(enumParameter),
            ColorParameterDefinition color => new ColorParameterState(color),
            NumericParameterDefinition numeric => new NumericParameterState(numeric),
            TextParameterDefinition text => new TextParameterState(text),
            FilePathParameterDefinition filePath => new FilePathParameterState(filePath),
            _ => throw new NotSupportedException($"Unsupported legacy parameter definition '{definition.GetType().FullName}'.")
        };
    }

    public static EffectParameterState Create(CoreEffectParameter parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);

        return parameter switch
        {
            CoreSliderParameter slider => new SliderParameterState(slider),
            CoreCheckboxParameter checkbox => new CheckboxParameterState(checkbox),
            CoreEnumParameter enumParameter => new EnumParameterState(enumParameter),
            CoreColorParameter color => new ColorParameterState(color),
            CoreNumericParameter numeric => new NumericParameterState(numeric),
            CoreTextParameter text => new TextParameterState(text),
            CoreFilePathParameter filePath => new FilePathParameterState(filePath),
            _ => throw new NotSupportedException($"Unsupported core parameter definition '{parameter.GetType().FullName}'.")
        };
    }

    protected static Color ToAvaloniaColor(SKColor color) => Color.FromArgb(color.Alpha, color.Red, color.Green, color.Blue);

    protected static SKColor ToSkColor(Color color) => new(color.R, color.G, color.B, color.A);
}

public sealed partial class SliderParameterState : EffectParameterState
{
    private readonly EffectParameterDefinition? _legacyDefinition;
    private readonly CoreSliderParameter? _coreParameter;

    [ObservableProperty]
    private double _value;

    public double Minimum { get; }

    public double Maximum { get; }

    public double TickFrequency { get; }

    public bool IsSnapToTickEnabled { get; }

    public string ValueStringFormat { get; }

    public SliderParameterState(SliderParameterDefinition definition)
        : base(definition.Key, definition.Label)
    {
        _legacyDefinition = definition;
        Minimum = definition.Minimum;
        Maximum = definition.Maximum;
        TickFrequency = definition.TickFrequency;
        IsSnapToTickEnabled = definition.IsSnapToTickEnabled;
        ValueStringFormat = definition.ValueStringFormat;
        _value = definition.DefaultValue;
    }

    public SliderParameterState(CoreSliderParameter parameter)
        : base(parameter.Key, parameter.Label)
    {
        _coreParameter = parameter;
        Minimum = parameter.Minimum;
        Maximum = parameter.Maximum;
        TickFrequency = parameter.TickFrequency;
        IsSnapToTickEnabled = parameter.IsSnapToTickEnabled;
        ValueStringFormat = parameter.ValueStringFormat;
        _value = parameter.DefaultValue;
    }

    internal override object? GetValue() => Value;

    internal override void ApplyValue(ImageEffect effect)
    {
        if (_legacyDefinition != null)
        {
            _legacyDefinition.ApplyValue(effect, Value);
            return;
        }

        _coreParameter!.Apply(effect, Value);
    }
}

public sealed partial class CheckboxParameterState : EffectParameterState
{
    private readonly EffectParameterDefinition? _legacyDefinition;
    private readonly CoreCheckboxParameter? _coreParameter;

    [ObservableProperty]
    private bool _value;

    public CheckboxParameterState(CheckboxParameterDefinition definition)
        : base(definition.Key, definition.Label)
    {
        _legacyDefinition = definition;
        _value = definition.DefaultValue;
    }

    public CheckboxParameterState(CoreCheckboxParameter parameter)
        : base(parameter.Key, parameter.Label)
    {
        _coreParameter = parameter;
        _value = parameter.DefaultValue;
    }

    internal override object? GetValue() => Value;

    internal override void ApplyValue(ImageEffect effect)
    {
        if (_legacyDefinition != null)
        {
            _legacyDefinition.ApplyValue(effect, Value);
            return;
        }

        _coreParameter!.Apply(effect, Value);
    }
}

public sealed partial class EnumParameterState : EffectParameterState
{
    private readonly EffectParameterDefinition? _legacyDefinition;
    private readonly CoreEnumParameter? _coreParameter;

    [ObservableProperty]
    private EffectOptionDefinition _selectedOption;

    public IReadOnlyList<EffectOptionDefinition> Options { get; }

    public EnumParameterState(EnumParameterDefinition definition)
        : base(definition.Key, definition.Label)
    {
        _legacyDefinition = definition;
        Options = definition.Options;
        _selectedOption = definition.Options[definition.DefaultIndex];
    }

    public EnumParameterState(CoreEnumParameter parameter)
        : base(parameter.Key, parameter.Label)
    {
        _coreParameter = parameter;
        Options = parameter.Options.Select(static option => new EffectOptionDefinition(option.Label, option.Value)).ToArray();
        _selectedOption = Options[parameter.DefaultIndex];
    }

    internal override object? GetValue() => SelectedOption.Value;

    internal override void ApplyValue(ImageEffect effect)
    {
        if (_legacyDefinition != null)
        {
            _legacyDefinition.ApplyValue(effect, SelectedOption.Value);
            return;
        }

        _coreParameter!.Apply(effect, SelectedOption.Value);
    }
}

public sealed partial class ColorParameterState : EffectParameterState
{
    private readonly EffectParameterDefinition? _legacyDefinition;
    private readonly CoreColorParameter? _coreParameter;

    [ObservableProperty]
    private Color _value;

    public ColorParameterState(ColorParameterDefinition definition)
        : base(definition.Key, definition.Label)
    {
        _legacyDefinition = definition;
        _value = definition.DefaultValue;
    }

    public ColorParameterState(CoreColorParameter parameter)
        : base(parameter.Key, parameter.Label)
    {
        _coreParameter = parameter;
        _value = ToAvaloniaColor(parameter.DefaultValue);
    }

    internal override object? GetValue() => Value;

    internal override void ApplyValue(ImageEffect effect)
    {
        if (_legacyDefinition != null)
        {
            _legacyDefinition.ApplyValue(effect, Value);
            return;
        }

        _coreParameter!.Apply(effect, ToSkColor(Value));
    }
}

public sealed partial class NumericParameterState : EffectParameterState
{
    private static long s_changeSequence;

    private readonly EffectParameterDefinition? _legacyDefinition;
    private readonly CoreNumericParameter? _coreParameter;

    [ObservableProperty]
    private decimal? _value;

    public long LastChangedSequence { get; private set; }

    public decimal Minimum { get; }

    public decimal Maximum { get; }
    public decimal DefaultValue { get; }


    public decimal Increment { get; }

    public string FormatString { get; }

    public NumericParameterState(NumericParameterDefinition definition)
        : base(definition.Key, definition.Label)
    {
        _legacyDefinition = definition;
        Minimum = definition.Minimum;
        Maximum = definition.Maximum;
        Increment = definition.Increment;
        FormatString = definition.FormatString;
        _value = definition.DefaultValue;
        DefaultValue = definition.DefaultValue;
    }

    public NumericParameterState(CoreNumericParameter parameter)
        : base(parameter.Key, parameter.Label)
    {
        _coreParameter = parameter;
        Minimum = parameter.Minimum;
        Maximum = parameter.Maximum;
        Increment = parameter.Increment;
        FormatString = parameter.FormatString;
        _value = parameter.DefaultValue;
        DefaultValue = parameter.DefaultValue;
    }

    internal override object? GetValue() => Value;

    partial void OnValueChanged(decimal? value)
    {
        LastChangedSequence = System.Threading.Interlocked.Increment(ref s_changeSequence);
    }

    internal override void ApplyValue(ImageEffect effect)
    {
        if (_legacyDefinition != null)
        {
            _legacyDefinition.ApplyValue(effect, Value);
            return;
        }

        _coreParameter!.Apply(effect, Value);
    }
}

public sealed partial class TextParameterState : EffectParameterState
{
    private readonly EffectParameterDefinition? _legacyDefinition;
    private readonly CoreTextParameter? _coreParameter;

    [ObservableProperty]
    private string _value;

    public TextParameterState(TextParameterDefinition definition)
        : base(definition.Key, definition.Label)
    {
        _legacyDefinition = definition;
        _value = definition.DefaultValue;
    }

    public TextParameterState(CoreTextParameter parameter)
        : base(parameter.Key, parameter.Label)
    {
        _coreParameter = parameter;
        _value = parameter.DefaultValue;
    }

    internal override object? GetValue() => Value;

    internal override void ApplyValue(ImageEffect effect)
    {
        if (_legacyDefinition != null)
        {
            _legacyDefinition.ApplyValue(effect, Value);
            return;
        }

        _coreParameter!.Apply(effect, Value);
    }
}

public sealed partial class FilePathParameterState : EffectParameterState
{
    private readonly EffectParameterDefinition? _legacyDefinition;
    private readonly CoreFilePathParameter? _coreParameter;

    [ObservableProperty]
    private string _value;

    public string? FileFilter { get; }

    public FilePathParameterState(FilePathParameterDefinition definition)
        : base(definition.Key, definition.Label)
    {
        _legacyDefinition = definition;
        FileFilter = definition.FileFilter;
        _value = definition.DefaultValue;
    }

    public FilePathParameterState(CoreFilePathParameter parameter)
        : base(parameter.Key, parameter.Label)
    {
        _coreParameter = parameter;
        FileFilter = parameter.FileFilter;
        _value = parameter.DefaultValue;
    }

    internal override object? GetValue() => Value;

    internal override void ApplyValue(ImageEffect effect)
    {
        if (_legacyDefinition != null)
        {
            _legacyDefinition.ApplyValue(effect, Value);
            return;
        }

        _coreParameter!.Apply(effect, Value);
    }
}