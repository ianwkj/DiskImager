namespace DiskImager.Core;

/// <summary>
/// A disk expressed as fixed-size blocks at absolute disk offsets. Only "used" blocks are listed;
/// anything not listed is free space and never needs to be copied.
/// Implemented by a live disk (backup / clone) and by an image file (restore).
/// </summary>
public interface IBlockSource : IDisposable
{
    int BlockSize { get; }
    long DiskSize { get; }
    int SectorSize { get; }
    DiskStyle Style { get; }
    long MaxPartitionEnd { get; }
    IReadOnlyList<long> Blocks { get; }
    /// <summary>Fills buffer[0..length) with the block's bytes.</summary>
    void ReadBlock(long blockIndex, byte[] buffer, out int length);
}
