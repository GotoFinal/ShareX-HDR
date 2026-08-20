#include "avif/avif.h"
#include "avif/internal.h"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <exception>
#include <limits>
#include <thread>

#if defined(_WIN32)
#define SHAREX_AVIF_EXPORT extern "C" __declspec(dllexport)
#else
#define SHAREX_AVIF_EXPORT extern "C" __attribute__((visibility("default")))
#endif

namespace {

constexpr int kSuccess = 0;
constexpr int kInvalidArgument = 1;
constexpr int kAllocationFailure = 2;
constexpr int kCodecFailure = 3;
constexpr int kUnexpectedFailure = 4;
constexpr size_t kMaximumDecodedBytes = static_cast<size_t>(1024) * 1024 * 1024;
constexpr uint32_t kSampleMaximum = 1023;
constexpr uint8_t kMdcvType[4] = {'m', 'd', 'c', 'v'};

void write_error(char* destination, size_t capacity, const char* message) noexcept {
  if (destination == nullptr || capacity == 0) {
    return;
  }

  const char* safe_message = message == nullptr ? "Unknown AVIF error" : message;
  std::snprintf(destination, capacity, "%s", safe_message);
  destination[capacity - 1] = '\0';
}

int write_codec_error(const char* operation,
                      avifResult result,
                      const avifDiagnostics* diagnostics,
                      char* error,
                      size_t error_capacity) noexcept {
  char message[512]{};
  const char* detail = diagnostics != nullptr && diagnostics->error[0] != '\0'
                           ? diagnostics->error
                           : avifResultToString(result);
  std::snprintf(message, sizeof(message), "%s: %s", operation, detail);
  write_error(error, error_capacity, message);
  return kCodecFailure;
}

uint16_t read_u16_be(const uint8_t* data) noexcept {
  return static_cast<uint16_t>((static_cast<uint16_t>(data[0]) << 8) | data[1]);
}

uint32_t read_u32_be(const uint8_t* data) noexcept {
  return (static_cast<uint32_t>(data[0]) << 24) |
         (static_cast<uint32_t>(data[1]) << 16) |
         (static_cast<uint32_t>(data[2]) << 8) |
         static_cast<uint32_t>(data[3]);
}

void write_u16_be(uint8_t* data, uint16_t value) noexcept {
  data[0] = static_cast<uint8_t>(value >> 8);
  data[1] = static_cast<uint8_t>(value);
}

void write_u32_be(uint8_t* data, uint32_t value) noexcept {
  data[0] = static_cast<uint8_t>(value >> 24);
  data[1] = static_cast<uint8_t>(value >> 16);
  data[2] = static_cast<uint8_t>(value >> 8);
  data[3] = static_cast<uint8_t>(value);
}

uint16_t clamp_light_level(float value) noexcept {
  if (!(value > 0.0f)) {
    return 0;
  }
  return static_cast<uint16_t>(std::clamp(value, 1.0f, 65535.0f) + 0.5f);
}

uint32_t nits_to_metadata_value(float value) noexcept {
  const double scaled = std::clamp(static_cast<double>(value), 0.0, 429496.7295) * 10000.0;
  return static_cast<uint32_t>(scaled + 0.5);
}

void create_mdcv(float maximum_nits, float minimum_nits, uint8_t (&metadata)[24]) noexcept {
  // BT.2020 primaries and D65 in 0.00002 chromaticity units.
  write_u16_be(metadata + 0, 35400);
  write_u16_be(metadata + 2, 14600);
  write_u16_be(metadata + 4, 8500);
  write_u16_be(metadata + 6, 39850);
  write_u16_be(metadata + 8, 6550);
  write_u16_be(metadata + 10, 2300);
  write_u16_be(metadata + 12, 15635);
  write_u16_be(metadata + 14, 16450);
  write_u32_be(metadata + 16, nits_to_metadata_value(maximum_nits));
  write_u32_be(metadata + 20, nits_to_metadata_value(minimum_nits));
}

float read_mastering_peak(const void* encoded_data,
                          size_t encoded_size,
                          const avifImage* image) noexcept {
  // libavif 1.4 writes mdcv but deliberately ignores it on read. Locate only
  // the exact 32-byte standardized property produced below, and validate its
  // fixed BT.2020/D65 chromaticities to avoid treating AV1 payload bytes as a box.
  const auto* encoded = static_cast<const uint8_t*>(encoded_data);
  if (encoded != nullptr && encoded_size >= 32) {
    for (size_t offset = 0; offset <= encoded_size - 32; ++offset) {
      const uint8_t* box = encoded + offset;
      if (read_u32_be(box) == 32 && std::memcmp(box + 4, kMdcvType, 4) == 0 &&
          read_u16_be(box + 8) == 35400 && read_u16_be(box + 10) == 14600 &&
          read_u16_be(box + 12) == 8500 && read_u16_be(box + 14) == 39850 &&
          read_u16_be(box + 16) == 6550 && read_u16_be(box + 18) == 2300 &&
          read_u16_be(box + 20) == 15635 && read_u16_be(box + 22) == 16450) {
        return static_cast<float>(read_u32_be(box + 24)) / 10000.0f;
      }
    }
  }

  return image == nullptr ? 0.0f : image->clli.maxCLL;
}

bool validate_hdr_image(const avifImage* image) noexcept {
  return image != nullptr && image->width > 0 && image->height > 0 &&
         image->depth == 10 && image->yuvFormat == AVIF_PIXEL_FORMAT_YUV444 &&
         image->yuvRange == AVIF_RANGE_FULL &&
         image->colorPrimaries == AVIF_COLOR_PRIMARIES_BT2020 &&
         image->transferCharacteristics == AVIF_TRANSFER_CHARACTERISTICS_SMPTE2084 &&
         image->matrixCoefficients == AVIF_MATRIX_COEFFICIENTS_BT2020_NCL;
}

int thread_count() noexcept {
  const unsigned int hardware_threads = std::thread::hardware_concurrency();
  unsigned int thread_limit = 16u;
  char configured_limit[16]{};
  size_t configured_length = 0;
  if (getenv_s(&configured_length, configured_limit, sizeof(configured_limit),
               "SHAREX_AVIF_MAX_THREADS") == 0 &&
      configured_length > 1) {
    char* end = nullptr;
    const long parsed_limit = std::strtol(configured_limit, &end, 10);
    if (end != configured_limit && *end == '\0' && parsed_limit >= 1 &&
        parsed_limit <= 64) {
      thread_limit = static_cast<unsigned int>(parsed_limit);
    }
  }

  return static_cast<int>(std::clamp(
      hardware_threads == 0 ? 1u : hardware_threads, 1u, thread_limit));
}

struct ImageGuard {
  avifImage* value;
  ~ImageGuard() { avifImageDestroy(value); }
};

struct EncoderGuard {
  avifEncoder* value;
  ~EncoderGuard() { avifEncoderDestroy(value); }
};

struct DecoderGuard {
  avifDecoder* value;
  ~DecoderGuard() { avifDecoderDestroy(value); }
};

}  // namespace

SHAREX_AVIF_EXPORT int sharex_avif_encode_rgba10(
    const uint16_t* rgba10,
    uint32_t width,
    uint32_t height,
    uint32_t stride_pixels,
    int quality,
    int speed,
    float mastering_maximum_nits,
    float mastering_minimum_nits,
    float max_cll,
    float max_fall,
    void** encoded_data,
    size_t* encoded_size,
    char* error,
    size_t error_capacity) noexcept {
  if (encoded_data != nullptr) {
    *encoded_data = nullptr;
  }
  if (encoded_size != nullptr) {
    *encoded_size = 0;
  }
  if (error != nullptr && error_capacity > 0) {
    error[0] = '\0';
  }

  if (rgba10 == nullptr || width == 0 || height == 0 || stride_pixels < width ||
      encoded_data == nullptr || encoded_size == nullptr ||
      static_cast<size_t>(stride_pixels) >
          std::numeric_limits<uint32_t>::max() / (sizeof(uint16_t) * 4)) {
    write_error(error, error_capacity, "Invalid ShareX AVIF encoder arguments");
    return kInvalidArgument;
  }

  try {
    const size_t sample_count = static_cast<size_t>(stride_pixels) * height * 4;
    for (size_t index = 0; index < sample_count; ++index) {
      if (rgba10[index] > kSampleMaximum) {
        write_error(error, error_capacity, "AVIF input contains a sample outside the 10-bit range");
        return kInvalidArgument;
      }
    }

    avifImage* image = avifImageCreate(width, height, 10, AVIF_PIXEL_FORMAT_YUV444);
    if (image == nullptr) {
      write_error(error, error_capacity, "libavif could not allocate an image");
      return kAllocationFailure;
    }
    ImageGuard image_guard{image};

    image->yuvRange = AVIF_RANGE_FULL;
    image->colorPrimaries = AVIF_COLOR_PRIMARIES_BT2020;
    image->transferCharacteristics = AVIF_TRANSFER_CHARACTERISTICS_SMPTE2084;
    image->matrixCoefficients = AVIF_MATRIX_COEFFICIENTS_BT2020_NCL;
    image->alphaPremultiplied = AVIF_FALSE;
    image->clli.maxCLL = clamp_light_level(max_cll);
    image->clli.maxPALL = clamp_light_level(max_fall);

    uint8_t mdcv[24]{};
    create_mdcv(mastering_maximum_nits, mastering_minimum_nits, mdcv);
    const uint8_t no_uuid[16]{};
    avifResult result = avifImagePushProperty(
        image, kMdcvType, no_uuid, mdcv, sizeof(mdcv));
    if (result != AVIF_RESULT_OK) {
      return write_codec_error("Could not attach AVIF mastering metadata", result, nullptr,
                               error, error_capacity);
    }

    avifRGBImage rgb{};
    avifRGBImageSetDefaults(&rgb, image);
    rgb.format = AVIF_RGB_FORMAT_RGBA;
    rgb.depth = 10;
    rgb.alphaPremultiplied = AVIF_FALSE;
    rgb.pixels = reinterpret_cast<uint8_t*>(const_cast<uint16_t*>(rgba10));
    rgb.rowBytes = stride_pixels * sizeof(uint16_t) * 4;

    result = avifImageRGBToYUV(image, &rgb);
    if (result != AVIF_RESULT_OK) {
      return write_codec_error("Could not convert PQ RGB to AVIF YUV", result, nullptr,
                               error, error_capacity);
    }

    avifEncoder* encoder = avifEncoderCreate();
    if (encoder == nullptr) {
      write_error(error, error_capacity, "libavif could not allocate an encoder");
      return kAllocationFailure;
    }
    EncoderGuard encoder_guard{encoder};
    encoder->maxThreads = thread_count();
    encoder->speed = std::clamp(speed, AVIF_SPEED_SLOWEST, AVIF_SPEED_FASTEST);
    encoder->quality = std::clamp(quality, AVIF_QUALITY_WORST, AVIF_QUALITY_BEST);
    encoder->qualityAlpha = AVIF_QUALITY_LOSSLESS;
    encoder->autoTiling = AVIF_TRUE;

    avifRWData output = AVIF_DATA_EMPTY;
    result = avifEncoderWrite(encoder, image, &output);
    if (result != AVIF_RESULT_OK) {
      return write_codec_error("AVIF encoding failed", result, &encoder->diag,
                               error, error_capacity);
    }

    struct OutputGuard {
      avifRWData* value;
      ~OutputGuard() { avifRWDataFree(value); }
    } output_guard{&output};

    if (output.data == nullptr || output.size == 0) {
      write_error(error, error_capacity, "libavif returned an empty encoded image");
      return kCodecFailure;
    }

    void* copy = std::malloc(output.size);
    if (copy == nullptr) {
      write_error(error, error_capacity, "Could not allocate the AVIF output buffer");
      return kAllocationFailure;
    }

    std::memcpy(copy, output.data, output.size);
    *encoded_data = copy;
    *encoded_size = output.size;
    return kSuccess;
  } catch (const std::exception& exception) {
    write_error(error, error_capacity, exception.what());
    return kUnexpectedFailure;
  } catch (...) {
    write_error(error, error_capacity, "Unexpected native AVIF encoder failure");
    return kUnexpectedFailure;
  }
}

SHAREX_AVIF_EXPORT int sharex_avif_probe_hdr(
    const void* encoded_data,
    size_t encoded_size,
    uint32_t* width,
    uint32_t* height,
    uint32_t* depth,
    float* mastering_maximum_nits,
    uint16_t* max_cll,
    uint16_t* max_fall) noexcept {
  if (width != nullptr) *width = 0;
  if (height != nullptr) *height = 0;
  if (depth != nullptr) *depth = 0;
  if (mastering_maximum_nits != nullptr) *mastering_maximum_nits = 0.0f;
  if (max_cll != nullptr) *max_cll = 0;
  if (max_fall != nullptr) *max_fall = 0;

  if (encoded_data == nullptr || encoded_size == 0 || width == nullptr || height == nullptr ||
      depth == nullptr || mastering_maximum_nits == nullptr || max_cll == nullptr ||
      max_fall == nullptr) {
    return 0;
  }

  try {
    avifDecoder* decoder = avifDecoderCreate();
    if (decoder == nullptr) {
      return 0;
    }
    DecoderGuard decoder_guard{decoder};
    decoder->strictFlags = AVIF_STRICT_ENABLED;

    if (avifDecoderSetIOMemory(decoder, static_cast<const uint8_t*>(encoded_data), encoded_size) !=
            AVIF_RESULT_OK ||
        avifDecoderParse(decoder) != AVIF_RESULT_OK || !validate_hdr_image(decoder->image)) {
      return 0;
    }

    *width = decoder->image->width;
    *height = decoder->image->height;
    *depth = decoder->image->depth;
    *mastering_maximum_nits = read_mastering_peak(encoded_data, encoded_size, decoder->image);
    *max_cll = decoder->image->clli.maxCLL;
    *max_fall = decoder->image->clli.maxPALL;
    return *mastering_maximum_nits > 0.0f ? 1 : 0;
  } catch (...) {
    return 0;
  }
}

SHAREX_AVIF_EXPORT int sharex_avif_decode_rgba10(
    const void* encoded_data,
    size_t encoded_size,
    void** rgba10_data,
    uint32_t* width,
    uint32_t* height,
    uint32_t* stride_pixels,
    float* mastering_maximum_nits,
    char* error,
    size_t error_capacity) noexcept {
  if (rgba10_data != nullptr) *rgba10_data = nullptr;
  if (width != nullptr) *width = 0;
  if (height != nullptr) *height = 0;
  if (stride_pixels != nullptr) *stride_pixels = 0;
  if (mastering_maximum_nits != nullptr) *mastering_maximum_nits = 0.0f;
  if (error != nullptr && error_capacity > 0) error[0] = '\0';

  if (encoded_data == nullptr || encoded_size == 0 || rgba10_data == nullptr ||
      width == nullptr || height == nullptr || stride_pixels == nullptr ||
      mastering_maximum_nits == nullptr) {
    write_error(error, error_capacity, "Invalid ShareX AVIF decoder arguments");
    return kInvalidArgument;
  }

  try {
    avifDecoder* decoder = avifDecoderCreate();
    if (decoder == nullptr) {
      write_error(error, error_capacity, "libavif could not allocate a decoder");
      return kAllocationFailure;
    }
    DecoderGuard decoder_guard{decoder};
    decoder->strictFlags = AVIF_STRICT_ENABLED;
    decoder->maxThreads = thread_count();

    avifImage* image = avifImageCreateEmpty();
    if (image == nullptr) {
      write_error(error, error_capacity, "libavif could not allocate a decoded image");
      return kAllocationFailure;
    }
    ImageGuard image_guard{image};

    avifResult result = avifDecoderReadMemory(
        decoder, image, static_cast<const uint8_t*>(encoded_data), encoded_size);
    if (result != AVIF_RESULT_OK) {
      return write_codec_error("AVIF decoding failed", result, &decoder->diag,
                               error, error_capacity);
    }
    if (!validate_hdr_image(image) ||
        static_cast<size_t>(image->width) >
            kMaximumDecodedBytes / sizeof(uint16_t) / 4 / image->height) {
      write_error(error, error_capacity,
                  "Only full-range 10-bit 4:4:4 BT.2020/PQ AVIF images are supported");
      return kCodecFailure;
    }

    avifRGBImage rgb{};
    avifRGBImageSetDefaults(&rgb, image);
    rgb.format = AVIF_RGB_FORMAT_RGBA;
    rgb.depth = 10;
    rgb.alphaPremultiplied = AVIF_FALSE;
    result = avifRGBImageAllocatePixels(&rgb);
    if (result != AVIF_RESULT_OK) {
      return write_codec_error("Could not allocate decoded AVIF RGB samples", result, nullptr,
                               error, error_capacity);
    }
    struct RgbGuard {
      avifRGBImage* value;
      ~RgbGuard() { avifRGBImageFreePixels(value); }
    } rgb_guard{&rgb};

    result = avifImageYUVToRGB(image, &rgb);
    if (result != AVIF_RESULT_OK) {
      return write_codec_error("Could not convert decoded AVIF YUV to PQ RGB", result, nullptr,
                               error, error_capacity);
    }

    const size_t active_row_bytes = static_cast<size_t>(image->width) * sizeof(uint16_t) * 4;
    const size_t output_bytes = active_row_bytes * image->height;
    void* output = std::malloc(output_bytes);
    if (output == nullptr) {
      write_error(error, error_capacity, "Could not allocate the decoded AVIF output buffer");
      return kAllocationFailure;
    }

    auto* destination = static_cast<uint8_t*>(output);
    for (uint32_t y = 0; y < image->height; ++y) {
      std::memcpy(destination + static_cast<size_t>(y) * active_row_bytes,
                  rgb.pixels + static_cast<size_t>(y) * rgb.rowBytes,
                  active_row_bytes);
    }

    *rgba10_data = output;
    *width = image->width;
    *height = image->height;
    *stride_pixels = image->width;
    *mastering_maximum_nits = read_mastering_peak(encoded_data, encoded_size, image);
    return kSuccess;
  } catch (const std::exception& exception) {
    write_error(error, error_capacity, exception.what());
    return kUnexpectedFailure;
  } catch (...) {
    write_error(error, error_capacity, "Unexpected native AVIF decoder failure");
    return kUnexpectedFailure;
  }
}

SHAREX_AVIF_EXPORT void sharex_avif_free(void* allocation) noexcept {
  std::free(allocation);
}
