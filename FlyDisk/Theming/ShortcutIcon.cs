// Copyright (C) 2026 youke1686 (https://github.com/youke1686)
//
// This file is part of FlyDisk.
//
// FlyDisk is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// FlyDisk is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with FlyDisk.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using FlyDisk.Engine;
using FlyDisk.Models;

namespace FlyDisk.Theming
{
    /// <summary>
    /// 桌面快捷方式的图标维护（见 深色主题.md）。
    ///
    /// **只更新、不新建**：用户在**自己的桌面**上放了指向本 exe 的快捷方式时，把它的图标换成
    /// 当前主题那一枚；桌面没有这样的快捷方式就**什么都不做**（快捷方式由用户自建）。
    ///
    /// **为什么要把图标落盘**：`.lnk` 的图标由 `IconLocation`（"文件 + 索引"）决定，
    /// 取不到我们嵌进 exe 的托管资源。于是先把对应主题的 ico 原样写一份到数据目录，
    /// 再让快捷方式指向它。**两枚用不同文件名**（`app-light.ico` / `app-dark.ico`）：
    /// 同名覆盖时 Windows 图标缓存认旧内容不刷新，换文件名即为"新图标"。
    ///
    /// **桌面路径走 Known Folder API**：桌面可能被重定向到 OneDrive 等位置，
    /// 必须用 `SHGetKnownFolderPath(FOLDERID_Desktop)` 拿实际路径，不能硬拼 `%USERPROFILE%\Desktop`。
    ///
    /// 全程 try/catch，任何失败只落诊断日志、绝不影响启动（读改桌面 `.lnk` 涉及 COM 与权限）。
    /// </summary>
    public static class ShortcutIcon
    {
        private const string LightFileName = "app-light.ico";
        private const string DarkFileName = "app-dark.ico";

        /// <summary>
        /// 维护桌面快捷方式图标。启动时调用一次（主题已定，<see cref="ThemeManager.Initialize"/> 之后）。
        /// </summary>
        public static void Update()
        {
            try
            {
                string? desktop = GetDesktopPath();
                if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop)) return;

                string? ourExe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(ourExe)) return;
                string ourExeFull = Path.GetFullPath(ourExe);

                // 先保证两枚 ico 在数据目录里就位（内容没变就不重写），再挑当前主题那枚作目标。
                string? lightPath = EnsureIconFile(LightFileName, dark: false);
                string? darkPath = EnsureIconFile(DarkFileName, dark: true);
                string? targetIcon = ThemeManager.IsDark
                    ? darkPath ?? lightPath
                    : lightPath ?? darkPath;
                if (string.IsNullOrEmpty(targetIcon)) return;

                bool changed = false;
                foreach (string lnk in Directory.GetFiles(desktop, "*.lnk", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        if (UpdateOne(lnk, ourExeFull, targetIcon)) changed = true;
                    }
                    catch (Exception ex)
                    {
                        LogService.DebugFile($"ShortcutIcon skip {Path.GetFileName(lnk)}: {ex.Message}");
                    }
                }

                // 改了才通知外壳刷图标缓存，否则资源管理器可能还显示旧图。
                if (changed) RefreshShell();
            }
            catch (Exception ex)
            {
                LogService.DebugFile($"ShortcutIcon update failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 把某一枚 ico 写到数据目录（内容一致则跳过）。返回可用的绝对路径；写不出返回 <c>null</c>。
        /// </summary>
        private static string? EnsureIconFile(string fileName, bool dark)
        {
            byte[]? bytes = AppIcon.ReadBytes(dark);
            if (bytes is null) return null;

            string path = Path.Combine(ServiceConstants.DataDirectory, fileName);
            try
            {
                if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                {
                    Directory.CreateDirectory(ServiceConstants.DataDirectory);
                    File.WriteAllBytes(path, bytes);
                }
                return path;
            }
            catch (Exception ex)
            {
                LogService.DebugFile($"ShortcutIcon write {fileName} failed: {ex.Message}");
                return File.Exists(path) ? path : null;
            }
        }

        /// <summary>
        /// 处理单个 `.lnk`：目标是本 exe 且图标不是目标那枚时，改写图标并保存。
        /// 返回是否真的写盘了。不是我们的快捷方式则原样放过。
        /// </summary>
        private static bool UpdateOne(string lnkPath, string ourExeFull, string targetIcon)
        {
            IShellLinkW? link = null;
            try
            {
                link = (IShellLinkW)new ShellLink();
                var persist = (IPersistFile)link;   // 同一个 COM 对象，无需另行释放

                persist.Load(lnkPath, StgmRead);

                var target = new StringBuilder(1024);
                link.GetPath(target, target.Capacity, IntPtr.Zero, SlgpRawPath);

                string rawTarget = target.ToString();
                if (string.IsNullOrEmpty(rawTarget)) return false;

                // 目标可能带环境变量（少数工具会这么存），展开后再规范化比对。
                if (!string.Equals(Path.GetFullPath(Environment.ExpandEnvironmentVariables(rawTarget)),
                                   ourExeFull, StringComparison.OrdinalIgnoreCase))
                {
                    return false;   // 不是指向本 exe 的快捷方式 —— 不碰
                }

                var currentIcon = new StringBuilder(1024);
                link.GetIconLocation(currentIcon, currentIcon.Capacity, out int currentIndex);
                if (currentIndex == 0
                    && string.Equals(currentIcon.ToString(), targetIcon, StringComparison.OrdinalIgnoreCase))
                {
                    return false;   // 已经是目标图标，不做无谓改动
                }

                link.SetIconLocation(targetIcon, 0);
                persist.Save(lnkPath, true);
                return true;
            }
            finally
            {
                if (link is not null && Marshal.IsComObject(link)) Marshal.FinalReleaseComObject(link);
            }
        }

        // ===== 桌面路径（Known Folder，处理重定向） =====

        private static readonly Guid FolderIdDesktop = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");

        private static string? GetDesktopPath()
        {
            IntPtr ptr = IntPtr.Zero;
            try
            {
                Guid fid = FolderIdDesktop;   // 只读字段不能按 ref 传，取一份本地副本
                int hr = SHGetKnownFolderPath(ref fid, 0, IntPtr.Zero, out ptr);
                if (hr != 0 || ptr == IntPtr.Zero) return null;
                return Marshal.PtrToStringUni(ptr);
            }
            catch (Exception ex)
            {
                LogService.DebugFile($"ShortcutIcon desktop path failed: {ex.Message}");
                return null;
            }
            finally
            {
                if (ptr != IntPtr.Zero) Marshal.FreeCoTaskMem(ptr);
            }
        }

        private const uint SlgpRawPath = 0x00000004;
        private const uint StgmRead = 0x00000000;

        // ===== 外壳图标缓存刷新 =====

        private const uint ShcneAssocChanged = 0x08000000;
        private const uint ShcnfIdList = 0x0000;

        private static void RefreshShell()
        {
            try
            {
                SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero);
            }
            catch (Exception)
            {
                // 通知不到外壳不影响快捷方式本身已改好的事实
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        // ===== COM：IShellLink / IPersistFile =====

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
        private interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            [PreserveSig] int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }
    }
}
