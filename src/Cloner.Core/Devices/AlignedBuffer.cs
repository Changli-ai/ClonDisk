using System.Runtime.InteropServices;

namespace Cloner.Core.Devices;

/// <summary>
/// Memoria nativa alineada a 4 KiB. La E/S sin caché (<c>FILE_FLAG_NO_BUFFERING</c>) exige
/// que la dirección del búfer esté alineada al tamaño de sector.
/// </summary>
internal sealed unsafe class AlignedBuffer : IDisposable
{
    public const int Alignment = 4096;

    private byte* _pointer;

    public AlignedBuffer(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        _pointer = (byte*)NativeMemory.AlignedAlloc((nuint)length, Alignment);
        Length = length;
        Span.Clear();
    }

    public int Length { get; }

    public Span<byte> Span
    {
        get
        {
            ObjectDisposedException.ThrowIf(_pointer is null, this);
            return new Span<byte>(_pointer, Length);
        }
    }

    public void Dispose()
    {
        if (_pointer is not null)
        {
            NativeMemory.AlignedFree(_pointer);
            _pointer = null;
        }
    }
}
