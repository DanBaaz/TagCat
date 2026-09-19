using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using VideoDedupe.Models;
namespace VideoDedupe.Extraction;

/// <summary>
/// Frame extraction through Media Foundation's SourceReader.
///
/// This is the decoder Windows itself uses. It is more code than the WinRT
/// MediaComposition route because it means talking to COM directly, but it works
/// reliably in ordinary desktop applications, which MediaComposition thumbnails do
/// not — they frequently return nothing at all outside packaged apps, including for
/// plain H.264 MP4 files.
///
/// Nothing here is third-party: mfplat and mfreadwrite ship with Windows.
/// </summary>
public sealed class MediaFoundationFrameExtractor : IFrameExtractor
{
    private static readonly string[] Extensions =
    [
        ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".wmv", ".asf", ".3gp",
        ".webm", ".mpg", ".mpeg", ".ts", ".m2ts"
    ];

    private static int _startupCount;

    public MediaFoundationFrameExtractor()
    {
        EnsureStarted();
    }

    public IReadOnlyCollection<string> SupportedExtensions => Extensions;

    public Task<VideoMetadata> ReadMetadataAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            using var reader = SourceReader.Open(path);
            return new VideoMetadata(reader.Duration, reader.Width, reader.Height);
        }, cancellationToken);

    public async IAsyncEnumerable<GrayFrame> ExtractFramesAsync(
        string path,
        IReadOnlyList<TimeSpan> timestamps,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The reader is not thread-safe and seeking is inherently sequential, so the
        // whole file is handled on one worker rather than parallelised internally.
        // Parallelism happens across files instead.
        var frames = await Task.Run(() =>
        {
            var results = new List<GrayFrame>(timestamps.Count);

            using var reader = SourceReader.Open(path);

            foreach (var timestamp in timestamps)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var frame = reader.ReadFrameAt(timestamp);
                if (frame is not null) results.Add(frame);
            }

            return results;
        }, cancellationToken).ConfigureAwait(false);

        foreach (var frame in frames) yield return frame;
    }

    private static void EnsureStarted()
    {
        if (Interlocked.Increment(ref _startupCount) != 1) return;

        var hr = MFStartup(MfVersion, MfStartupLite);
        if (hr < 0)
        {
            Interlocked.Decrement(ref _startupCount);
            throw new FrameExtractionException($"Media Foundation could not start (0x{hr:X8}).");
        }
    }

    public void Dispose()
    {
        // MFShutdown is deliberately not called. Other extractor instances may still
        // be live, and shutting the platform down under them causes obscure failures.
        // Windows reclaims it when the process exits.
    }

    // ---- Native plumbing -------------------------------------------------

    private const uint MfVersion = 0x00020070;
    private const uint MfStartupLite = 1;

    private const uint FirstVideoStream = 0xFFFFFFFC;
    private const uint AllStreams = 0xFFFFFFFE;
    private const uint MediaSource = 0xFFFFFFFF;

    private static readonly Guid MfMediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid MfVideoFormatRgb32 = new("00000016-0000-0010-8000-00AA00389B71");
    private static readonly Guid MfMtMajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MfMtSubtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MfMtFrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MfPdDuration = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
    private static readonly Guid MfSourceReaderEnableVideoProcessing = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateAttributes(out IMFAttributes attributes, uint initialSize);

    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int MFCreateSourceReaderFromURL(
        string url, IMFAttributes? attributes, out IMFSourceReader reader);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMediaType(out IMFMediaType mediaType);

    /// <summary>Thin managed wrapper that owns the COM objects for one file.</summary>
    private sealed class SourceReader : IDisposable
    {
        private IMFSourceReader? _reader;

        public int Width { get; private init; }
        public int Height { get; private init; }
        public int Stride { get; private init; }
        public TimeSpan Duration { get; private init; }

        public static SourceReader Open(string path)
        {
            // Local copies exist because these COM calls take their GUIDs by
            // reference, and a static readonly field cannot be passed that way.
            var enableProcessing = MfSourceReaderEnableVideoProcessing;
            var majorTypeKey = MfMtMajorType;
            var subtypeKey = MfMtSubtype;
            var videoMajorType = MfMediaTypeVideo;
            var rgb32Subtype = MfVideoFormatRgb32;
            var frameSizeKey = MfMtFrameSize;
            var durationKey = MfPdDuration;

            // Video processing must be enabled or the reader refuses to convert
            // exotic pixel formats to RGB32 and every ReadSample comes back empty.
            var hr = MFCreateAttributes(out var attributes, 1);
            Check(hr, "Creating reader attributes");

            hr = attributes.SetUINT32(ref enableProcessing, 1);
            Check(hr, "Enabling video processing");

            hr = MFCreateSourceReaderFromURL(path, attributes, out var reader);
            Check(hr, "Opening the file");

            // Select only the video stream; audio would just waste decoding effort.
            reader.SetStreamSelection(AllStreams, false);
            hr = reader.SetStreamSelection(FirstVideoStream, true);
            Check(hr, "Selecting the video stream");

            hr = MFCreateMediaType(out var mediaType);
            Check(hr, "Creating the output media type");

            hr = mediaType.SetGUID(ref majorTypeKey, ref videoMajorType);
            Check(hr, "Setting the major type");

            hr = mediaType.SetGUID(ref subtypeKey, ref rgb32Subtype);
            Check(hr, "Setting the RGB32 subtype");

            hr = reader.SetCurrentMediaType(FirstVideoStream, IntPtr.Zero, mediaType);
            Check(hr, "Requesting RGB32 output");

            Marshal.ReleaseComObject(mediaType);

            // Read back what the reader actually settled on.
            hr = reader.GetCurrentMediaType(FirstVideoStream, out var actual);
            Check(hr, "Reading back the output format");

            hr = actual.GetUINT64(ref frameSizeKey, out var packedSize);
            Check(hr, "Reading the frame size");

            var width = (int)(packedSize >> 32);
            var height = (int)(packedSize & 0xFFFFFFFF);

            Marshal.ReleaseComObject(actual);

            if (width <= 0 || height <= 0)
            {
                Marshal.ReleaseComObject(reader);
                Marshal.ReleaseComObject(attributes);
                throw new FrameExtractionException("The file reported a zero frame size.");
            }

            long durationTicks = 0;
            if (reader.GetPresentationAttribute(MediaSource, ref durationKey, out var durationVariant) >= 0)
            {
                durationTicks = durationVariant.longValue;
            }

            Marshal.ReleaseComObject(attributes);

            return new SourceReader
            {
                _reader = reader,
                Width = width,
                Height = height,
                Stride = width * 4,
                Duration = TimeSpan.FromTicks(durationTicks)
            };
        }

        /// <summary>
        /// Seeks and decodes one frame. Returns null rather than throwing when a
        /// particular position yields nothing, since the caller can work with a
        /// partial set of frames.
        /// </summary>
        public GrayFrame? ReadFrameAt(TimeSpan position)
        {
            if (_reader is null) return null;

            var variant = PropVariant.FromLong(position.Ticks);
            var timeFormat = Guid.Empty;

            var hr = _reader.SetCurrentPosition(ref timeFormat, ref variant);
            if (hr < 0) return null;

            // After a seek the first sample returned can be a keyframe earlier than
            // the requested position, and the reader may emit empty "gap" samples.
            // A few attempts covers both without risking an unbounded loop.
            for (var attempt = 0; attempt < 8; attempt++)
            {
                hr = _reader.ReadSample(FirstVideoStream, 0, IntPtr.Zero,
                    out var flags, out var sampleTime, out var sample);

                if (hr < 0) return null;

                const uint EndOfStream = 0x00000002;
                if ((flags & EndOfStream) != 0) return null;

                if (sample is null) continue;

                try
                {
                    return Convert(sample, TimeSpan.FromTicks(sampleTime));
                }
                finally
                {
                    Marshal.ReleaseComObject(sample);
                }
            }

            return null;
        }

        private GrayFrame? Convert(IMFSample sample, TimeSpan timestamp)
        {
            var hr = sample.ConvertToContiguousBuffer(out var buffer);
            if (hr < 0) return null;

            try
            {
                hr = buffer.Lock(out var scan0, out _, out var currentLength);
                if (hr < 0) return null;

                try
                {
                    var expected = Stride * Height;
                    if (currentLength < expected) return null;

                    unsafe
                    {
                        var span = new ReadOnlySpan<byte>((void*)scan0, expected);
                        return GrayFrame.FromBgra32(span, Width, Height, Stride, timestamp);
                    }
                }
                finally
                {
                    buffer.Unlock();
                }
            }
            finally
            {
                Marshal.ReleaseComObject(buffer);
            }
        }

        private static void Check(int hr, string what)
        {
            if (hr < 0)
            {
                throw new FrameExtractionException($"{what} failed (0x{hr:X8}).");
            }
        }

        public void Dispose()
        {
            if (_reader is null) return;
            Marshal.ReleaseComObject(_reader);
            _reader = null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort vt;
        public ushort reserved1;
        public ushort reserved2;
        public ushort reserved3;
        public long longValue;

        private const ushort VtI8 = 20;

        public static PropVariant FromLong(long value) => new() { vt = VtI8, longValue = value };
    }

    // ---- COM interfaces --------------------------------------------------
    // Method order below must match the native vtable exactly. Slots that are never
    // called are still declared, because omitting one would shift every method after
    // it and silently call the wrong function.

    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFAttributes
    {
        [PreserveSig] int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] int GetItemType(ref Guid key, out int type);
        [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out bool result);
        [PreserveSig] int Compare(IMFAttributes other, int matchType, out bool result);
        [PreserveSig] int GetUINT32(ref Guid key, out uint value);
        [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
        [PreserveSig] int GetDouble(ref Guid key, out double value);
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid key, out uint length);
        [PreserveSig] int GetString(ref Guid key, IntPtr value, uint size, IntPtr length);
        [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);
        [PreserveSig] int GetBlobSize(ref Guid key, out uint size);
        [PreserveSig] int GetBlob(ref Guid key, IntPtr buffer, uint size, IntPtr actual);
        [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out uint size);
        [PreserveSig] int GetUnknown(ref Guid key, ref Guid riid, out IntPtr value);
        [PreserveSig] int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, uint value);
        [PreserveSig] int SetUINT64(ref Guid key, ulong value);
        [PreserveSig] int SetDouble(ref Guid key, double value);
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(ref Guid key, IntPtr buffer, uint size);
        [PreserveSig] int SetUnknown(ref Guid key, IntPtr unknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IMFAttributes destination);
    }

    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaType
    {
        // IMFAttributes slots 0-29.
        [PreserveSig] int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] int GetItemType(ref Guid key, out int type);
        [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out bool result);
        [PreserveSig] int Compare(IMFAttributes other, int matchType, out bool result);
        [PreserveSig] int GetUINT32(ref Guid key, out uint value);
        [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
        [PreserveSig] int GetDouble(ref Guid key, out double value);
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid key, out uint length);
        [PreserveSig] int GetString(ref Guid key, IntPtr value, uint size, IntPtr length);
        [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);
        [PreserveSig] int GetBlobSize(ref Guid key, out uint size);
        [PreserveSig] int GetBlob(ref Guid key, IntPtr buffer, uint size, IntPtr actual);
        [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out uint size);
        [PreserveSig] int GetUnknown(ref Guid key, ref Guid riid, out IntPtr value);
        [PreserveSig] int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, uint value);
        [PreserveSig] int SetUINT64(ref Guid key, ulong value);
        [PreserveSig] int SetDouble(ref Guid key, double value);
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(ref Guid key, IntPtr buffer, uint size);
        [PreserveSig] int SetUnknown(ref Guid key, IntPtr unknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IMFAttributes destination);

        // IMFMediaType's own slots.
        [PreserveSig] int GetMajorType(out Guid majorType);
        [PreserveSig] int IsCompressedFormat(out bool compressed);
        [PreserveSig] int IsEqual(IMFMediaType other, out uint flags);
        [PreserveSig] int GetRepresentation(Guid representation, out IntPtr value);
        [PreserveSig] int FreeRepresentation(Guid representation, IntPtr value);
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSample
    {
        // IMFAttributes slots 0-29.
        [PreserveSig] int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] int GetItemType(ref Guid key, out int type);
        [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out bool result);
        [PreserveSig] int Compare(IMFAttributes other, int matchType, out bool result);
        [PreserveSig] int GetUINT32(ref Guid key, out uint value);
        [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
        [PreserveSig] int GetDouble(ref Guid key, out double value);
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid key, out uint length);
        [PreserveSig] int GetString(ref Guid key, IntPtr value, uint size, IntPtr length);
        [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);
        [PreserveSig] int GetBlobSize(ref Guid key, out uint size);
        [PreserveSig] int GetBlob(ref Guid key, IntPtr buffer, uint size, IntPtr actual);
        [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out uint size);
        [PreserveSig] int GetUnknown(ref Guid key, ref Guid riid, out IntPtr value);
        [PreserveSig] int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, uint value);
        [PreserveSig] int SetUINT64(ref Guid key, ulong value);
        [PreserveSig] int SetDouble(ref Guid key, double value);
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(ref Guid key, IntPtr buffer, uint size);
        [PreserveSig] int SetUnknown(ref Guid key, IntPtr unknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IMFAttributes destination);

        // IMFSample's own slots.
        [PreserveSig] int GetSampleFlags(out uint flags);
        [PreserveSig] int SetSampleFlags(uint flags);
        [PreserveSig] int GetSampleTime(out long time);
        [PreserveSig] int SetSampleTime(long time);
        [PreserveSig] int GetSampleDuration(out long duration);
        [PreserveSig] int SetSampleDuration(long duration);
        [PreserveSig] int GetBufferCount(out uint count);
        [PreserveSig] int GetBufferByIndex(uint index, out IMFMediaBuffer buffer);
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
    }

    [ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out int length);
        [PreserveSig] int SetCurrentLength(int length);
        [PreserveSig] int GetMaxLength(out int length);
    }

    [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSourceReader
    {
        [PreserveSig] int GetStreamSelection(uint streamIndex, out bool selected);
        [PreserveSig] int SetStreamSelection(uint streamIndex, bool selected);
        [PreserveSig] int GetNativeMediaType(uint streamIndex, uint mediaTypeIndex, out IMFMediaType mediaType);
        [PreserveSig] int GetCurrentMediaType(uint streamIndex, out IMFMediaType mediaType);
        [PreserveSig] int SetCurrentMediaType(uint streamIndex, IntPtr reserved, IMFMediaType mediaType);
        [PreserveSig] int SetCurrentPosition(ref Guid timeFormat, ref PropVariant position);
        [PreserveSig] int ReadSample(uint streamIndex, uint controlFlags, IntPtr actualStreamIndex,
            out uint streamFlags, out long timestamp, out IMFSample? sample);
        [PreserveSig] int Flush(uint streamIndex);
        [PreserveSig] int GetServiceForStream(uint streamIndex, ref Guid service, ref Guid riid, out IntPtr obj);
        [PreserveSig] int GetPresentationAttribute(uint streamIndex, ref Guid attribute, out PropVariant value);
    }
}
