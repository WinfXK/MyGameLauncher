using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace MyGameLauncher
{
    public static class HighResIconExtractor
    {
        #region Win32 API 声明 (终极版)

        // 从指定模块加载资源
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

        private const uint LOAD_LIBRARY_AS_DATAFILE = 0x00000002;

        // 释放已加载的模块
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr hModule);

        // 查找资源
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr FindResource(IntPtr hModule, IntPtr lpName, IntPtr lpType);

        // C#中，我们可以用string代替IntPtr来传递资源名和类型
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr FindResource(IntPtr hModule, string lpName, string lpType);

        // 获取资源大小
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SizeofResource(IntPtr hModule, IntPtr hResInfo);

        // 加载资源到内存
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LoadResource(IntPtr hModule, IntPtr hResInfo);

        // 获取资源在内存中的指针
        [DllImport("kernel32.dll")]
        private static extern IntPtr LockResource(IntPtr hResData);

        // 从资源数据创建图标句柄
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreateIconFromResourceEx(IntPtr presbits, uint dwResSize, bool fIcon, uint dwVer, int cxDesired, int cyDesired, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        // RT_GROUP_ICON 资源类型的整数表示
        private static readonly IntPtr RT_ICON = (IntPtr)3;
        private static readonly IntPtr RT_GROUP_ICON = (IntPtr)14;

        #endregion

        #region 内部结构体定义
        // GRPICONDIR 和 GRPICONDIRENTRY 是解析.ico文件格式和exe内嵌图标资源的关键结构
        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct GRPICONDIRENTRY
        {
            public byte bWidth;
            public byte bHeight;
            public byte bColorCount;
            public byte bReserved;
            public ushort wPlanes;
            public ushort wBitCount;
            public uint dwBytesInRes;
            public ushort nId;
        }

        #endregion

        /// <summary>
        /// 终极图标提取方法。直接解析文件资源，找到并构建最高分辨率的图标。
        /// </summary>
        public static Bitmap GetIconFromFile(string filePath)
        {
            IntPtr hModule = IntPtr.Zero;
            try
            {
                hModule = LoadLibraryEx(filePath, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE);
                if (hModule == IntPtr.Zero) throw new Exception("无法加载文件作为数据模块。");
                IntPtr hResource = FindResource(hModule, RT_GROUP_ICON, (IntPtr)1); // 通常图标组的ID是1
                if (hResource == IntPtr.Zero)
                {
                    var resNames = new List<IntPtr>();
                    EnumResourceNames(hModule, RT_GROUP_ICON, (h, t, name, l) => { resNames.Add(name); return true; }, IntPtr.Zero);
                    hResource = resNames.Count > 0 ? FindResource(hModule, resNames[0], RT_GROUP_ICON) : IntPtr.Zero;
                }
                if (hResource == IntPtr.Zero) throw new Exception("找不到图标组资源。");
                IntPtr hMem = LoadResource(hModule, hResource);
                if (hMem == IntPtr.Zero) throw new Exception("无法加载图标组资源。");
                IntPtr pResource = LockResource(hMem);
                if (pResource == IntPtr.Zero) throw new Exception("无法锁定图标组资源。");
                int entryCount = Marshal.ReadInt16(pResource, 4);
                int bestEntryIndex = -1;
                int maxDimension = 0;

                for (int i = 0; i < entryCount; i++)
                {
                    IntPtr entryPtr = new IntPtr(pResource.ToInt64() + 6 + (14 * i));
                    GRPICONDIRENTRY entry = (GRPICONDIRENTRY)Marshal.PtrToStructure(entryPtr, typeof(GRPICONDIRENTRY));

                    int currentDimension = entry.bWidth == 0 ? 256 : entry.bWidth;
                    if (currentDimension > maxDimension)
                    {
                        maxDimension = currentDimension;
                        bestEntryIndex = i;
                    }
                }

                if (bestEntryIndex == -1) throw new Exception("在图标组中找不到任何图标条目。");
                IntPtr bestEntryPtr = new IntPtr(pResource.ToInt64() + 6 + (14 * bestEntryIndex));
                GRPICONDIRENTRY bestEntry = (GRPICONDIRENTRY)Marshal.PtrToStructure(bestEntryPtr, typeof(GRPICONDIRENTRY));
                IntPtr hIconResource = FindResource(hModule, (IntPtr)bestEntry.nId, RT_ICON);
                if (hIconResource == IntPtr.Zero) throw new Exception("找不到最佳匹配的图标资源。");
                uint iconSize = SizeofResource(hModule, hIconResource);
                IntPtr hIconMem = LoadResource(hModule, hIconResource);
                if (hIconMem == IntPtr.Zero) throw new Exception("无法加载图标资源。");
                IntPtr pIconResource = LockResource(hIconMem);
                if (pIconResource == IntPtr.Zero) throw new Exception("无法锁定图标资源。");
                IntPtr hIcon = CreateIconFromResourceEx(pIconResource, iconSize, true, 0x00030000, 0, 0, 0);
                if (hIcon == IntPtr.Zero) throw new Exception("CreateIconFromResourceEx 失败。");
                using (Icon icon = (Icon)Icon.FromHandle(hIcon).Clone())
                {
                    return icon.ToBitmap();
                }
            }
            catch (Exception eee)
            {
                System.Diagnostics.Debug.WriteLine($"[Extractor] 高级提取失败: {filePath}. 错误: {eee.Message}");
                try
                {
                    using (Icon icon = Icon.ExtractAssociatedIcon(filePath))
                    {
                        return icon.ToBitmap();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Extractor] 系统关联图标提取失败: {filePath}. 错误: {ex.Message}");
                    return SystemIcons.Application.ToBitmap();
                }
            }
            finally
            {
                if (hModule != IntPtr.Zero)
                {
                    FreeLibrary(hModule);
                }
            }
        }

        private delegate bool EnumResNameProc(IntPtr hModule, IntPtr lpszType, IntPtr lpszName, IntPtr lParam);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool EnumResourceNames(IntPtr hModule, IntPtr lpszType, EnumResNameProc lpEnumFunc, IntPtr lParam);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool EnumResourceNames(IntPtr hModule, string lpszType, EnumResNameProc lpEnumFunc, IntPtr lParam);
    }
}