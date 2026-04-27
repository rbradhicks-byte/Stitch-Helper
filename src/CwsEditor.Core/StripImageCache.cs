using ICSharpCode.SharpZipLib.Zip;
using ImageMagick;

namespace CwsEditor.Core;

internal sealed class StripImageCache : IDisposable
{
    private readonly object _sync = new();
    private readonly object _archiveSync = new();
    private readonly CwsDocument _document;
    private readonly int _capacity;
    private readonly FileStream _archiveStream;
    private readonly ZipFile _zipFile;
    private readonly Dictionary<string, LinkedListNode<CacheItem>> _nodesByEntry = [];
    private readonly LinkedList<CacheItem> _lru = [];
    private volatile bool _disposed;

    public StripImageCache(CwsDocument document, int capacity = 24)
    {
        _document = document;
        _capacity = Math.Max(2, capacity);
        _archiveStream = new FileStream(_document.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        _zipFile = new ZipFile(_archiveStream) { IsStreamOwner = false };
    }

    public MagickImage GetFullStrip(CwsStrip strip)
    {
        string key = $"full::{strip.ImageEntryName}";
        return GetOrLoad(key, () => LoadImage(strip.ImageEntryName));
    }

    public MagickImage GetThumbStrip(CwsStrip strip)
    {
        if (!string.IsNullOrWhiteSpace(strip.ThumbEntryName))
        {
            string key = $"thumb::{strip.ThumbEntryName}";
            return GetOrLoad(key, () => LoadImage(strip.ThumbEntryName));
        }

        if (strip.ThumbBytes is null)
        {
            using MagickImage fullImage = GetFullStrip(strip);
            MagickImage resized = (MagickImage)fullImage.Clone();
            resized.Resize((uint)_document.ThumbnailWidth, (uint)Math.Max(1, (int)Math.Round(strip.Height * (_document.ThumbnailWidth / (double)_document.CompositeWidth))));
            return resized;
        }

        string embeddedKey = $"thumb-bytes::{strip.ImageEntryName}";
        return GetOrLoad(embeddedKey, () => new MagickImage(strip.ThumbBytes));
    }

    public void PrefetchFullStrip(CwsStrip strip)
    {
        using MagickImage _ = GetFullStrip(strip);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (CacheItem item in _lru)
            {
                item.Image.Dispose();
            }

            _lru.Clear();
            _nodesByEntry.Clear();
        }

        lock (_archiveSync)
        {
            _zipFile.Close();
            _archiveStream.Dispose();
        }
    }

    private MagickImage GetOrLoad(string key, Func<MagickImage> factory)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_nodesByEntry.TryGetValue(key, out LinkedListNode<CacheItem>? existing))
            {
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return (MagickImage)existing.Value.Image.Clone();
            }
        }

        MagickImage loaded = factory();
        lock (_sync)
        {
            if (_disposed)
            {
                loaded.Dispose();
                ObjectDisposedException.ThrowIf(_disposed, this);
            }

            if (_nodesByEntry.TryGetValue(key, out LinkedListNode<CacheItem>? existing))
            {
                loaded.Dispose();
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return (MagickImage)existing.Value.Image.Clone();
            }

            LinkedListNode<CacheItem> node = new(new CacheItem(key, loaded));
            _lru.AddFirst(node);
            _nodesByEntry[key] = node;
            TrimIfNeeded();
            return (MagickImage)loaded.Clone();
        }
    }

    private void TrimIfNeeded()
    {
        while (_nodesByEntry.Count > _capacity)
        {
            LinkedListNode<CacheItem>? tail = _lru.Last;
            if (tail is null)
            {
                break;
            }

            _lru.RemoveLast();
            _nodesByEntry.Remove(tail.Value.Key);
            tail.Value.Image.Dispose();
        }
    }

    private MagickImage LoadImage(string entryName)
    {
        lock (_archiveSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ZipEntry entry = _zipFile.GetEntry(entryName) ?? throw new CwsEditorException($"Archive entry was not found: {entryName}");
            using Stream entryStream = _zipFile.GetInputStream(entry);
            using MemoryStream copy = new();
            entryStream.CopyTo(copy);
            copy.Position = 0;
            return new MagickImage(copy);
        }
    }

    private sealed record CacheItem(string Key, MagickImage Image);
}
