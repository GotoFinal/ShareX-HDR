#include "ultrahdr_api.h"

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <exception>
#include <limits>

#if defined(_WIN32)
#define SHAREX_UHDR_EXPORT extern "C" __declspec(dllexport)
#else
#define SHAREX_UHDR_EXPORT extern "C" __attribute__((visibility("default")))
#endif

namespace {

constexpr int kSuccess = 0;
constexpr int kInvalidArgument = 1;
constexpr int kAllocationFailure = 2;
constexpr int kCodecFailure = 3;
constexpr int kUnexpectedFailure = 4;

constexpr size_t kMaximumDecodedBytes = static_cast<size_t>(1024) * 1024 * 1024;

void write_error(char* destination, size_t capacity, const char* message) noexcept {
  if (destination == nullptr || capacity == 0) {
    return;
  }

  const char* safe_message = message == nullptr ? "Unknown Ultra HDR error" : message;
  std::snprintf(destination, capacity, "%s", safe_message);
  destination[capacity - 1] = '\0';
}

int check_codec_result(const uhdr_error_info_t& result, char* error, size_t error_capacity) noexcept {
  if (result.error_code == UHDR_CODEC_OK) {
    return kSuccess;
  }

  write_error(error, error_capacity,
              result.has_detail != 0 ? result.detail : "libultrahdr operation failed");
  return kCodecFailure;
}

}  // namespace

SHAREX_UHDR_EXPORT int sharex_uhdr_encode_rgba16f(
    const void* rgba16f,
    uint32_t width,
    uint32_t height,
    uint32_t stride_pixels,
    int base_quality,
    int gain_map_quality,
    float target_peak_nits,
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

  if (rgba16f == nullptr || width == 0 || height == 0 || stride_pixels < width ||
      encoded_data == nullptr || encoded_size == nullptr) {
    write_error(error, error_capacity, "Invalid ShareX Ultra HDR encoder arguments");
    return kInvalidArgument;
  }

  try {
    uhdr_codec_private_t* encoder = uhdr_create_encoder();
    if (encoder == nullptr) {
      write_error(error, error_capacity, "libultrahdr could not allocate an encoder");
      return kAllocationFailure;
    }

    struct EncoderGuard {
      uhdr_codec_private_t* value;
      ~EncoderGuard() { uhdr_release_encoder(value); }
    } guard{encoder};

    uhdr_raw_image_t image{};
    image.fmt = UHDR_IMG_FMT_64bppRGBAHalfFloat;
    image.cg = UHDR_CG_BT_709;
    image.ct = UHDR_CT_LINEAR;
    image.range = UHDR_CR_FULL_RANGE;
    image.w = width;
    image.h = height;
    image.planes[UHDR_PLANE_PACKED] = const_cast<void*>(rgba16f);
    image.stride[UHDR_PLANE_PACKED] = stride_pixels;

    int status = check_codec_result(
        uhdr_enc_set_raw_image(encoder, &image, UHDR_HDR_IMG), error, error_capacity);
    if (status != kSuccess) {
      return status;
    }

    status = check_codec_result(
        uhdr_enc_set_quality(encoder, std::clamp(base_quality, 0, 100), UHDR_BASE_IMG),
        error, error_capacity);
    if (status != kSuccess) {
      return status;
    }

    status = check_codec_result(
        uhdr_enc_set_quality(encoder, std::clamp(gain_map_quality, 0, 100), UHDR_GAIN_MAP_IMG),
        error, error_capacity);
    if (status != kSuccess) {
      return status;
    }

    status = check_codec_result(
        uhdr_enc_set_target_display_peak_brightness(
            encoder, std::clamp(target_peak_nits, 203.0f, 10000.0f)),
        error, error_capacity);
    if (status != kSuccess) {
      return status;
    }

    status = check_codec_result(
        uhdr_enc_set_output_format(encoder, UHDR_CODEC_JPG), error, error_capacity);
    if (status != kSuccess) {
      return status;
    }

    status = check_codec_result(uhdr_encode(encoder), error, error_capacity);
    if (status != kSuccess) {
      return status;
    }

    uhdr_compressed_image_t* result = uhdr_get_encoded_stream(encoder);
    if (result == nullptr || result->data == nullptr || result->data_sz == 0) {
      write_error(error, error_capacity, "libultrahdr returned an empty encoded image");
      return kCodecFailure;
    }

    if (result->data_sz > std::numeric_limits<size_t>::max()) {
      write_error(error, error_capacity, "Ultra HDR output is too large");
      return kCodecFailure;
    }

    void* output = std::malloc(result->data_sz);
    if (output == nullptr) {
      write_error(error, error_capacity, "Could not allocate the Ultra HDR output buffer");
      return kAllocationFailure;
    }

    std::memcpy(output, result->data, result->data_sz);
    *encoded_data = output;
    *encoded_size = result->data_sz;
    return kSuccess;
  } catch (const std::exception& exception) {
    write_error(error, error_capacity, exception.what());
    return kUnexpectedFailure;
  } catch (...) {
    write_error(error, error_capacity, "Unexpected native Ultra HDR encoder failure");
    return kUnexpectedFailure;
  }
}

SHAREX_UHDR_EXPORT int sharex_uhdr_decode_rgba16f(
    const void* encoded_data,
    size_t encoded_size,
    void** rgba16f_data,
    uint32_t* width,
    uint32_t* height,
    uint32_t* stride_pixels,
    int* color_gamut,
    float* hdr_capacity_max,
    char* error,
    size_t error_capacity) noexcept {
  if (rgba16f_data != nullptr) {
    *rgba16f_data = nullptr;
  }
  if (width != nullptr) {
    *width = 0;
  }
  if (height != nullptr) {
    *height = 0;
  }
  if (stride_pixels != nullptr) {
    *stride_pixels = 0;
  }
  if (color_gamut != nullptr) {
    *color_gamut = UHDR_CG_UNSPECIFIED;
  }
  if (hdr_capacity_max != nullptr) {
    *hdr_capacity_max = 0.0f;
  }
  if (error != nullptr && error_capacity > 0) {
    error[0] = '\0';
  }

  if (encoded_data == nullptr || encoded_size == 0 ||
      encoded_size > static_cast<size_t>(std::numeric_limits<int>::max()) ||
      rgba16f_data == nullptr || width == nullptr || height == nullptr ||
      stride_pixels == nullptr || color_gamut == nullptr || hdr_capacity_max == nullptr) {
    write_error(error, error_capacity, "Invalid ShareX Ultra HDR decoder arguments");
    return kInvalidArgument;
  }

  try {
    uhdr_codec_private_t* decoder = uhdr_create_decoder();
    if (decoder == nullptr) {
      write_error(error, error_capacity, "libultrahdr could not allocate a decoder");
      return kAllocationFailure;
    }

    struct DecoderGuard {
      uhdr_codec_private_t* value;
      ~DecoderGuard() { uhdr_release_decoder(value); }
    } guard{decoder};

    uhdr_compressed_image_t image{};
    image.data = const_cast<void*>(encoded_data);
    image.data_sz = encoded_size;
    image.capacity = encoded_size;
    image.cg = UHDR_CG_UNSPECIFIED;
    image.ct = UHDR_CT_UNSPECIFIED;
    image.range = UHDR_CR_UNSPECIFIED;

    int status = check_codec_result(
        uhdr_dec_set_image(decoder, &image), error, error_capacity);
    if (status != kSuccess) {
      return status;
    }

    status = check_codec_result(
        uhdr_dec_set_out_img_format(decoder, UHDR_IMG_FMT_64bppRGBAHalfFloat),
        error, error_capacity);
    if (status != kSuccess) {
      return status;
    }

    status = check_codec_result(
        uhdr_dec_set_out_color_transfer(decoder, UHDR_CT_LINEAR), error, error_capacity);
    if (status != kSuccess) {
      return status;
    }

    status = check_codec_result(uhdr_dec_probe(decoder), error, error_capacity);
    if (status != kSuccess) {
      return status;
    }

    const int probed_width = uhdr_dec_get_image_width(decoder);
    const int probed_height = uhdr_dec_get_image_height(decoder);
    if (probed_width <= 0 || probed_height <= 0 ||
        static_cast<size_t>(probed_width) >
            kMaximumDecodedBytes / sizeof(uint16_t) / 4 / static_cast<size_t>(probed_height)) {
      write_error(error, error_capacity, "Ultra HDR dimensions exceed the supported decode limit");
      return kInvalidArgument;
    }

    uhdr_gainmap_metadata_t* metadata = uhdr_dec_get_gainmap_metadata(decoder);
    if (metadata == nullptr || !std::isfinite(metadata->hdr_capacity_max) ||
        metadata->hdr_capacity_max < 1.0f) {
      write_error(error, error_capacity, "Ultra HDR gain-map peak metadata is invalid");
      return kCodecFailure;
    }

    status = check_codec_result(uhdr_decode(decoder), error, error_capacity);
    if (status != kSuccess) {
      return status;
    }

    uhdr_raw_image_t* decoded = uhdr_get_decoded_image(decoder);
    if (decoded == nullptr || decoded->planes[UHDR_PLANE_PACKED] == nullptr ||
        decoded->fmt != UHDR_IMG_FMT_64bppRGBAHalfFloat || decoded->ct != UHDR_CT_LINEAR ||
        decoded->w != static_cast<uint32_t>(probed_width) ||
        decoded->h != static_cast<uint32_t>(probed_height) ||
        decoded->stride[UHDR_PLANE_PACKED] < decoded->w ||
        (decoded->cg != UHDR_CG_BT_709 && decoded->cg != UHDR_CG_DISPLAY_P3 &&
         decoded->cg != UHDR_CG_BT_2100)) {
      write_error(error, error_capacity, "libultrahdr returned an invalid decoded image");
      return kCodecFailure;
    }

    const size_t active_row_bytes = static_cast<size_t>(decoded->w) * sizeof(uint16_t) * 4;
    const size_t output_bytes = active_row_bytes * decoded->h;
    void* output = std::malloc(output_bytes);
    if (output == nullptr) {
      write_error(error, error_capacity, "Could not allocate the decoded Ultra HDR buffer");
      return kAllocationFailure;
    }

    const size_t source_row_bytes =
        static_cast<size_t>(decoded->stride[UHDR_PLANE_PACKED]) * sizeof(uint16_t) * 4;
    const auto* source = static_cast<const uint8_t*>(decoded->planes[UHDR_PLANE_PACKED]);
    auto* destination = static_cast<uint8_t*>(output);
    for (uint32_t y = 0; y < decoded->h; ++y) {
      std::memcpy(destination + static_cast<size_t>(y) * active_row_bytes,
                  source + static_cast<size_t>(y) * source_row_bytes,
                  active_row_bytes);
    }

    *rgba16f_data = output;
    *width = decoded->w;
    *height = decoded->h;
    *stride_pixels = decoded->w;
    *color_gamut = decoded->cg;
    *hdr_capacity_max = metadata->hdr_capacity_max;
    return kSuccess;
  } catch (const std::exception& exception) {
    write_error(error, error_capacity, exception.what());
    return kUnexpectedFailure;
  } catch (...) {
    write_error(error, error_capacity, "Unexpected native Ultra HDR decoder failure");
    return kUnexpectedFailure;
  }
}
SHAREX_UHDR_EXPORT int sharex_uhdr_is_image(const void* data, size_t size) noexcept {
  if (data == nullptr || size == 0 || size > static_cast<size_t>(std::numeric_limits<int>::max())) {
    return 0;
  }

  return is_uhdr_image(const_cast<void*>(data), static_cast<int>(size));
}


SHAREX_UHDR_EXPORT void sharex_uhdr_free(void* allocation) noexcept {
  std::free(allocation);
}
