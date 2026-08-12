import { PhotonImage, SamplingFilter, resize } from "@cf-wasm/photon/node";

/**
 * Node-context twin of src/lib/imageResize.ts — same Photon library, same
 * parameters (2000px longest edge, 85% JPEG), used by the one-time local
 * file migration so historical photos get byte-for-byte-equivalent
 * treatment to what the live Worker does for new uploads going forward.
 */
const MAX_DIMENSION = 2000;
const JPEG_QUALITY = 85;

export function resizeImageBytes(bytes: Uint8Array): Uint8Array {
  const input = PhotonImage.new_from_byteslice(bytes);
  try {
    const width = input.get_width();
    const height = input.get_height();
    const scale = Math.min(1, MAX_DIMENSION / Math.max(width, height));

    if (scale >= 1) {
      return input.get_bytes_jpeg(JPEG_QUALITY);
    }

    const newWidth = Math.round(width * scale);
    const newHeight = Math.round(height * scale);
    const output = resize(input, newWidth, newHeight, SamplingFilter.Lanczos3);
    try {
      return output.get_bytes_jpeg(JPEG_QUALITY);
    } finally {
      output.free();
    }
  } finally {
    input.free();
  }
}
