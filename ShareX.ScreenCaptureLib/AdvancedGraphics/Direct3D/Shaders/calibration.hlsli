//
// Copyright 2024 Andon "Kaldaien" Coleman
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to
// deal in the Software without restriction, including without limitation the
// rights to use, copy, modify, merge, publish, distribute, sublicense, and/or
// sell copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL
// THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.
//

float4 DrawMaxClipPattern (float clipLevel_scRGB, float2 uv)
{
  float2 texDims;

  texture0.
    GetDimensions ( texDims.x,
                    texDims.y );

  float2 scale  =
    float2 ( texDims.x / 10.0,
             texDims.y / 10.0 );

  float2 size   = texDims.xy / scale;
  float  total  =
      floor (uv.x * size.x) +
      floor (uv.y * size.y);

  bool   isEven =
    fmod (total, 2.0f) == 0.0f;

  float4 color1 = float4 ((clipLevel_scRGB).xxx, 1.0);
  float4 color2 = float4 (          (125.0).xxx, 1.0);

  return isEven ?
         color1 : color2;
}