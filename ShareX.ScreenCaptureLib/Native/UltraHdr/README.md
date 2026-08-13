# ShareX Ultra HDR native bridge

The bridge intentionally exposes only RGBA16F JPEG encode/decode, Ultra HDR
detection, and matching free calls. It wraps the pinned Google `libultrahdr` C
API so native allocations never cross allocator ownership boundaries. ShareX
performs additional managed size, color-space, and document validation around
this narrow ABI.

The current integration target is `libultrahdr` **v2.0.1**, commit
`a532a8c1418890cff7ce1e90cbe59ba6ebc6fa6d`. ShareX's capture master is
linear scRGB where `1.0 = 80 nits`; libultrahdr's RGBA16F convention
uses `1.0 = 203 nits`. Managed code must therefore scale RGB by `80 / 203`
before encoding and by `203 / 80` after decoding. The decoder returns linear
RGBA16F plus dimensions, pixel stride, output gamut, and gain-map HDR-capacity
metadata. Managed code converts supported P3/BT.2100 output primaries to
ShareX's BT.709/D65 scRGB master and premultiplies alpha.

Example x64 build after producing `uhdr-static.lib` from the pinned source:

```powershell
git clone --branch v2.0.1 --depth 1 https://github.com/google/libultrahdr.git C:/src/libultrahdr
git -C C:/src/libultrahdr checkout a532a8c1418890cff7ce1e90cbe59ba6ebc6fa6d

cmake -S C:/src/libultrahdr -B C:/build/libultrahdr -A x64 `
  -DUHDR_ENABLE_HEIF=OFF `
  -DBUILD_SHARED_LIBS=OFF
cmake --build C:/build/libultrahdr --config Release

cmake -S . -B build -A x64 `
  -DUHDR_SOURCE_DIR=C:/src/libultrahdr `
  -DUHDR_STATIC_LIBRARY=C:/build/libultrahdr/Release/uhdr-static.lib `
  -DUHDR_JPEG_LIBRARY=C:/build/libultrahdr/turbojpeg/src/turbojpeg-build/Release/jpeg-static.lib
cmake --build build --config Release
```

HEIF is deliberately disabled; the shipping bridge is JPEG-only and has no
libheif dependency. Keep the copied libultrahdr/libjpeg-turbo/IJG notices next
to this source when updating the binary.

ARM64 packaging is deliberately deferred for the current milestone. Any later
ARM64 bridge must use the same pinned source and qualification suite; never load
the x64 bridge into an ARM64 ShareX process.
