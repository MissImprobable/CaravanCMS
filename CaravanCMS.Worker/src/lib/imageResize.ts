import { PhotonImage, SamplingFilter, resize } from "@cf-wasm/photon/workerd";

const MAX_DIMENSION = 2000;
const JPEG_QUALITY = 85;

const RESIZABLE_MIME_TYPES = new Set(["image/jpeg", "image/png", "image/webp", "image/bmp"]);

export function isResizableImage(mimeType: string): boolean {
  return RESIZABLE_MIME_TYPES.has(mimeType.toLowerCase());
}

/**
 * Resizes an image to fit within MAX_DIMENSION on its longest edge, re-encoded as JPEG at
 * JPEG_QUALITY. Images already smaller than the target are left untouched (never upscaled).
 * Runs entirely in-Worker via Photon (WASM) — no Cloudflare Images product/binding needed.
 */
export function resizeImage(bytes: Uint8Array): { bytes: Uint8Array; contentType: string } {
  const input = PhotonImage.new_from_byteslice(bytes);
  try {
    const width = input.get_width();
    const height = input.get_height();
    const scale = Math.min(1, MAX_DIMENSION / Math.max(width, height));

    if (scale >= 1) {
      // Already within bounds — re-encode as JPEG for consistent storage without resizing.
      const outBytes = input.get_bytes_jpeg(JPEG_QUALITY);
      return { bytes: outBytes, contentType: "image/jpeg" };
    }

    const newWidth = Math.round(width * scale);
    const newHeight = Math.round(height * scale);
    const output = resize(input, newWidth, newHeight, SamplingFilter.Lanczos3);
    try {
      const outBytes = output.get_bytes_jpeg(JPEG_QUALITY);
      return { bytes: outBytes, contentType: "image/jpeg" };
    } finally {
      output.free();
    }
  } finally {
    input.free();
  }
}

// KNOWN ISSUE: @cf-wasm/photon (v0.4.0) intermittently throws "attempted to take ownership of
// a Rust value while it was borrowed" on resize() calls after the first one in a given Worker
// isolate — a WASM object-lifecycle bug in the library itself (confirmed: removing the input
// .free() call above did NOT fix it, ruling out a simple double-free in our own code). Not
// resolved upstream as of this writing (no matching GitHub issue, issue creation restricted on
// the repo). The caller (routes/documents.ts) catches resizeImage() failures and falls back to
// uploading the original unresized image rather than failing the request — revisit if this
// library ships a fix, or consider switching to Cloudflare's native Images product instead.
