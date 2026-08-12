/** Port of DocumentsController.GetMimeType. */
export function getMimeType(fileName: string): string {
  const ext = fileName.slice(fileName.lastIndexOf(".")).toLowerCase();
  switch (ext) {
    case ".pdf":
      return "application/pdf";
    case ".jpg":
    case ".jpeg":
      return "image/jpeg";
    case ".png":
      return "image/png";
    case ".tiff":
    case ".tif":
      return "image/tiff";
    case ".bmp":
      return "image/bmp";
    case ".gif":
      return "image/gif";
    case ".xlsx":
      return "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    case ".xls":
      return "application/vnd.ms-excel";
    case ".docx":
      return "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    case ".doc":
      return "application/msword";
    case ".txt":
      return "text/plain";
    default:
      return "application/octet-stream";
  }
}
