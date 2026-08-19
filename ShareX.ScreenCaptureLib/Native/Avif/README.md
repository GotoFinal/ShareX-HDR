# ShareX AVIF native bridge

This x64 bridge exposes only packed RGBA10 HDR encode/decode, HDR probing, and
matching free calls. It wraps pinned `libavif` **v1.4.2**, commit
`c5240fc79fe5c2407e10afd35f5505ef6333ea49`, with its pinned local libaom
dependency. ShareX performs the scRGB/BT.2020/PQ conversion and validates the
returned dimensions and metadata around this narrow ABI.

The encoded files use 10-bit full-range YUV 4:4:4, BT.2020 primaries, ST 2084
PQ, BT.2020 non-constant-luminance matrix coefficients, lossless alpha, CLLI,
and an opaque AVIF `mdcv` mastering-display property. 4:4:4 is intentional for
screenshot text and UI edges.

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
`5FECC909CA7F55560EC7DE445C0D58331F8296C825A8960282DB5F6E5A848545`.
Dependency inspection shows only `KERNEL32.dll`; libavif, libaom, and the MSVC
runtime are statically linked.
