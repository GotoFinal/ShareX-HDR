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

static const uint SKIV_TONEMAP_TYPE_NONE               = 0x0;
static const uint SKIV_TONEMAP_TYPE_CLIP               = 0x1;
static const uint SKIV_TONEMAP_TYPE_INFINITE_ROLLOFF   = 0x2;
static const uint SKIV_TONEMAP_TYPE_NORMALIZE_TO_CLL   = 0x4;
static const uint SKIV_TONEMAP_TYPE_MAP_CLL_TO_DISPLAY = 0x8;

float TonemapNone (float L)
{
  return L;
}

float TonemapClip (float L, float Ld)
{
  return
    min (L, Ld);
}

float TonemapSDR (float L, float Lc, float Ld)
{
  return
    (L + (1.0f / pow (Lc, 2.0f)) * pow (L, 2.0f)) / (1.0f + L);
}

float TonemapHDR (float L, float Lc, float Ld)
{
  float a = (  Ld / pow (Lc, 2.0f));
  float b = (1.0f / Ld);

  return
    L * (1 + a * L) / (1 + b * L);
}