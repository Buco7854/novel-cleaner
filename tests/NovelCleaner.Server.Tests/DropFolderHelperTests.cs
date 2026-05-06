using NovelCleaner.Server.Services;

namespace NovelCleaner.Server.Tests;

public sealed class DropFolderHelperTests : IDisposable
{
    private readonly string _root;

    public DropFolderHelperTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "novelcleaner-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* swallow */ }
    }

    [Fact]
    public void Copy_creates_destination_folder_if_missing()
    {
        var src = WriteSource("input.epub", "hello");
        var drop = Path.Combine(_root, "drop");
        Assert.False(Directory.Exists(drop));

        var result = DropFolderHelper.Copy(drop, "MyBook.epub", src);

        Assert.True(Directory.Exists(drop));
        Assert.True(File.Exists(result.DestinationPath));
        Assert.Equal("hello", File.ReadAllText(result.DestinationPath));
    }

    [Fact]
    public void Copy_uses_cleaned_suffix_with_original_extension()
    {
        var src = WriteSource("input.epub", "x");
        var drop = Path.Combine(_root, "drop");

        var result = DropFolderHelper.Copy(drop, "MyBook.epub", src);

        Assert.Equal(Path.Combine(drop, "MyBook_cleaned.epub"), result.DestinationPath);
    }

    [Fact]
    public void Copy_defaults_to_epub_extension_when_original_has_none()
    {
        var src = WriteSource("input", "x");
        var drop = Path.Combine(_root, "drop");

        var result = DropFolderHelper.Copy(drop, "noext", src);

        Assert.EndsWith("noext_cleaned.epub", result.DestinationPath);
    }

    [Fact]
    public void Copy_never_overwrites_existing_file_and_appends_counter()
    {
        var src = WriteSource("input.epub", "first");
        var drop = Path.Combine(_root, "drop");
        Directory.CreateDirectory(drop);

        var first  = DropFolderHelper.Copy(drop, "Book.epub", src);
        File.WriteAllText(src, "second");
        var second = DropFolderHelper.Copy(drop, "Book.epub", src);
        File.WriteAllText(src, "third");
        var third  = DropFolderHelper.Copy(drop, "Book.epub", src);

        Assert.Equal(Path.Combine(drop, "Book_cleaned.epub"),     first.DestinationPath);
        Assert.Equal(Path.Combine(drop, "Book_cleaned (1).epub"), second.DestinationPath);
        Assert.Equal(Path.Combine(drop, "Book_cleaned (2).epub"), third.DestinationPath);

        Assert.Equal("first",  File.ReadAllText(first.DestinationPath));
        Assert.Equal("second", File.ReadAllText(second.DestinationPath));
        Assert.Equal("third",  File.ReadAllText(third.DestinationPath));
    }

    [Fact]
    public void Copy_trims_drop_folder_path()
    {
        var src = WriteSource("input.epub", "x");
        var drop = Path.Combine(_root, "drop");

        var result = DropFolderHelper.Copy("  " + drop + "  ", "Book.epub", src);

        Assert.StartsWith(drop, result.DestinationPath);
    }

    private string WriteSource(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }
}
