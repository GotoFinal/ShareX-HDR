# ShareX AVIF native bridge

This bridge exposes packed RGBA10 HDR encode/decode and HDR probing, plus
separate BGRA8 SDR encode/decode and SDR probing, with matching free calls.
It wraps pinned `libavif` **v1.4.2**, commit
`c5240fc79fe5c2407e10afd35f5505ef6333ea49`, with its pinned local libaom
dependency. ShareX performs the scRGB/BT.2020/PQ conversion and validates the
returned dimensions and metadata around this narrow ABI.

The HDR encoded files use 10-bit full-range YUV 4:4:4, BT.2020 primaries, ST 2084
PQ, BT.2020 non-constant-luminance matrix coefficients, lossless alpha, CLLI,
and an opaque AVIF `mdcv` mastering-display property. 4:4:4 is intentional for
screenshot text and UI edges.

Ordinary SDR AVIF uses a separate 8-bit full-range 4:4:4 path, Rec.709 primaries,
sRGB transfer, identity matrix, and straight lossless alpha, without PQ, CLLI,
or mastering metadata. Identity matrix avoids RGB/YUV rounding at quality 100.
The SDR decoder accepts only this single-frame subset and rejects unmanaged
color profiles, transforms, gain maps, and HDR variants. The existing HDR ABI
and metadata remain independent of the ordinary image-format setting.

Example x64 build:

```powershell
git clone --branch v1.4.2 --depth 1 https://github.com/AOMediaCodec/libavif.git C:/src/libavif
git -C C:/src/libavif checkout c5240fc79fe5c2407e10afd35f5505ef6333ea49

cmake -S . -B build -A x64 -DAVIF_SOURCE_DIR=C:/src/libavif
cmake --build build --config Release --target sharex_avif --parallel
```

The checked-in `Build-NativeBridge.ps1` automates the pinned clone, build, PE
architecture validation, and output copy for both x64 and ARM64. The release
workflow uses it to cross-build the ARM64 bridge before publishing the ARM64
installer and portable archive.

The CMake project disables apps, examples, tests, libyuv, and shared upstream
libraries; it builds libavif and libaom statically into `ShareX.Avif.dll` with
the static MSVC runtime. Keep the libavif and libaom notices beside this source
when updating the packaged binary.

AVIF availability is reported at runtime. If a matching native bridge is not
packaged, the managed HDR PNG and OpenEXR formats remain available.

The currently packaged x64 `ShareX.Avif.dll` SHA-256 is
`E91B35B5B619923907D0F8044E5D48E8FD300149824B52DFFE2F33224A0E2629`.
Dependency inspection shows only `KERNEL32.dll`; libavif, libaom, and the MSVC
runtime are statically linked.
