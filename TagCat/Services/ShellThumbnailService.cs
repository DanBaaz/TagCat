using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace MediaTagger.Services
{
    /// <summary>
    /// Pulls thumbnails straight from the Windows Shell (the same cache/generator
    /// Explorer uses), so video files get real frame thumbnails without needing ffmpeg.
    /// </summary>
    public static class ShellThumbnailService
    {
        [ComImport]
        [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage(
                [In, MarshalAs(UnmanagedType.Struct)] SIZE size,
                [In] SIIGBF flags,
                [Out] out IntPtr phbm);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx;
            public int cy;
            public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; }
        }

        [Flags]
        private enum SIIGBF
        {
            SIIGBF_RESIZETOFIT = 0x00,
            SIIGBF_BIGGERSIZEOK = 0x01,
            SIIGBF_THUMBNAILONLY = 0x10,
            SIIGBF_ICONONLY = 0x04,
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string path,
            IntPtr pbc,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object ppv);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr hObject);

        private static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

        /// <summary>
        /// Returns a frozen, UI-thread-safe BitmapSource thumbnail for the given file, or null on failure.
        /// </summary>
        public static BitmapSource? GetThumbnail(string filePath, int size = 160)
        {
            IntPtr hBitmap = IntPtr.Zero;
            try
            {
                Guid riid = IID_IShellItemImageFactory;
                int hr = SHCreateItemFromParsingName(filePath, IntPtr.Zero, ref riid, out object shellItem);
                if (hr != 0 || shellItem is not IShellItemImageFactory factory)
                    return null;

                hr = factory.GetImage(new SIZE(size, size), SIIGBF.SIIGBF_RESIZETOFIT | SIIGBF.SIIGBF_BIGGERSIZEOK, out hBitmap);
                if (hr != 0 || hBitmap == IntPtr.Zero)
                    return null;

                var bitmapSource = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bitmapSource.Freeze();
                return bitmapSource;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (hBitmap != IntPtr.Zero)
                    DeleteObject(hBitmap);
            }
        }
    }
}
