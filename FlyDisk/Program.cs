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
using System.Windows.Forms;
using FlyDisk.Engine;
using FlyDisk.Localization;
using FlyDisk.Models;
using FlyDisk.Theming;

namespace FlyDisk
{
    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();

            // 启动版权/授权声明（GPL 建议交互式程序启动时给出提示；本程序不弹窗，改为写诊断日志，
            // 见 THIRD-PARTY-NOTICES.md 第六节）。放在最前，保证每次运行开头都有一份、且不依赖后续初始化是否成功。
            LogService.DebugFile("FlyDisk - Copyright (C) 2026 youke1686 (https://github.com/youke1686)");
            LogService.DebugFile("FlyDisk is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.");
            LogService.DebugFile("FlyDisk is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.");
            LogService.DebugFile("You should have received a copy of the GNU General Public License along with FlyDisk.  If not, see <https://www.gnu.org/licenses/>.");

            // 32 位进程警示：L1 的非托管池受 32 位地址空间限制（约 4GB，**与物理内存水位无关**），
            // 会过早撞上"无法分配新的 Slab" ⇒ 性能骤降。发布配置一旦退回 win-x86（32 位，别被名字骗了：
            // x86 = 32 位、x64 才是 64 位）就会踩到，这里在诊断日志里留痕，便于排查。
            // 主界面另有一条同样的警示，见 Form1_Load。
            if (!Environment.Is64BitProcess)
            {
                LogService.DebugFile(
                    "警告：当前以 32 位进程运行。L1 非托管内存受 32 位地址空间限制（约 4GB，与物理内存无关），" +
                    "会过早出现“无法分配新的 Slab”导致性能骤降。请改用 64 位发布：RID 用 win-x64（win-x86 是 32 位）。");
            }

            // 主题：**必须在任何窗体构造之前**定下初始调色板，否则第一个窗体拿到的是默认浅色。
            ThemeManager.Initialize();

            // 桌面快捷方式图标：用户在**自己桌面**放了指向本 exe 的快捷方式时，把图标换成当前主题那枚
            // （没有就什么都不做，快捷方式由用户自建）。要主题先定下来才知道用哪枚，故放在 Initialize 之后。
            ShortcutIcon.Update();

            // 帮助文档：把嵌进 exe 的那份写一份到数据目录（每次启动覆盖写，保证与当前版本一致，见 HelpDocument）。
            HelpDocument.EnsureExtracted();

            // 语言：**只在首次打开时**按系统语言检测一次，结果写入配置，之后以配置为准（见 后续待办.md 第八节第 8 条）。
            // 必须赶在任何窗体构造之前定下来——窗体是在构造时取词的。
            DiskConfig config = ConfigService.Load();
            if (string.IsNullOrWhiteSpace(config.Language))
            {
                config.Language = Locale.DetectSystemLanguage();
                ConfigService.Save(config, out _);   // 写失败也无妨：下次启动再检测一次
            }
            Locale.SetLanguage(config.Language);

            // 崩溃 / 强杀残留：上次进程被杀时，iSCSI 会话与发现门户还留在系统里。启动期幂等清理一次
            // （服务没在跑时内部直接跳过；任何失败都只记日志、不拦启动）。
            IscsiInitiator.CleanupStale(config.TargetIqn, config.ListenAddress, config.ListenPort);

            // 兜底：本程序占着用户的整块盘（脱机 + 独占），一旦有未捕获异常带着进程死掉，
            // 那块盘就留在"看不见"的状态里。所以要把异常留痕并明确告知恢复路径，而不是静默退出。
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => ReportCrash(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ReportCrash(e.ExceptionObject as Exception);

            Application.Run(new Form1());
        }

        private static void ReportCrash(Exception? ex)
        {
            string text = ex?.ToString() ?? "未知异常";
            try
            {
                Directory.CreateDirectory(ServiceConstants.DataDirectory);
                File.AppendAllText(Path.Combine(ServiceConstants.DataDirectory, "crash.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}{Environment.NewLine}");
            }
            catch
            {
                // 写不进崩溃日志也不能再抛（此时已经在异常处理里）
            }

            MessageBox.Show(
                Locale.T("crash.body",
                    Path.Combine(ServiceConstants.DataDirectory, "crash.log"),
                    ex?.Message ?? string.Empty),
                Locale.T("crash.title"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
