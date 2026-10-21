namespace PCHealth.Inventory;

public enum FileCategory
{
    Other,
    Video,
    Image,
    Audio,
    Document,
    Archive,
    Installer,
    DiskImage,
    Code,
}

/// <summary>Classifies files by extension for the storage summary. A coarse grouping for display, not a content check.</summary>
public static class FileCategories
{
    private static readonly Dictionary<string, FileCategory> ByExtension = Build(
        (FileCategory.Video, [".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".m4v", ".mpg", ".mpeg"]),
        (FileCategory.Image, [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic", ".raw", ".cr2", ".nef", ".dng", ".psd"]),
        (FileCategory.Audio, [".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a", ".wma"]),
        (FileCategory.Document, [".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".txt", ".rtf", ".md", ".csv"]),
        (FileCategory.Archive, [".zip", ".7z", ".rar", ".tar", ".gz", ".bz2", ".xz", ".zst"]),
        (FileCategory.Installer, [".exe", ".msi", ".msix", ".appx", ".msixbundle"]),
        (FileCategory.DiskImage, [".iso", ".img", ".vhd", ".vhdx", ".vmdk", ".qcow2"]),
        (FileCategory.Code, [".cs", ".c", ".cpp", ".h", ".py", ".js", ".ts", ".java", ".go", ".rs", ".json", ".xml", ".yml", ".yaml"]));

    public static FileCategory Classify(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return ByExtension.GetValueOrDefault(Path.GetExtension(fileName), FileCategory.Other);
    }

    private static Dictionary<string, FileCategory> Build(params (FileCategory Category, string[] Extensions)[] groups)
    {
        var map = new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase);
        foreach (var (category, extensions) in groups)
        {
            foreach (var extension in extensions)
            {
                map.Add(extension, category);
            }
        }

        return map;
    }
}
