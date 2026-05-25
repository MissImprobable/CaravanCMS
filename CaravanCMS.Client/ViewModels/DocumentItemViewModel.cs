using CaravanCMS.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CaravanCMS.Client.ViewModels;

/// <summary>Wraps a DocumentDto with a lazily-loaded thumbnail for image files.</summary>
public partial class DocumentItemViewModel : ObservableObject
{
    public DocumentDto Doc { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    private ImageSource? _thumbnailSource;

    public bool HasThumbnail => ThumbnailSource is not null;

    // Pass-through properties so CollectionViewSource GroupDescription/SortDescription keep working
    public string?   DocumentType  => Doc.DocumentType;
    public string?   Category      => Doc.Category;
    public string    FileName      => Doc.FileName;
    public string?   MimeType      => Doc.MimeType;
    public string?   Notes         => Doc.Notes;
    public DateTime? UploadedDate  => Doc.UploadedDate;

    public DocumentItemViewModel(DocumentDto doc) => Doc = doc;

    /// <summary>Downloads and decodes a thumbnail for image files. No-ops silently for all other types.</summary>
    public async Task LoadThumbnailAsync(Func<int, Task<byte[]>> downloadAsync)
    {
        if (Doc.MimeType?.StartsWith("image/") != true) return;
        try
        {
            byte[] data = await downloadAsync(Doc.Id);
            ImageSource? thumb = DecodeImageThumbnail(data);
            if (thumb is not null)
                Application.Current?.Dispatcher.BeginInvoke(() => ThumbnailSource = thumb);
        }
        catch { }
    }

    private static ImageSource? DecodeImageThumbnail(byte[] data)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource     = new MemoryStream(data);
            bmp.DecodePixelWidth = 220;   // decode at display size — don't load the full image
            bmp.CacheOption      = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }
}
