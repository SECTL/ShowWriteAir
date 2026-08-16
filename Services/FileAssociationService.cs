using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ShowWrite.Services
{
    /// <summary>
    /// 文件关联服务：将支持的图片扩展名注册到本程序，使图片可通过本程序直接打开。
    /// 使用 HKCU\Software\Classes 注册 ProgID 与扩展名关联，无需管理员权限。
    /// </summary>
    public static class FileAssociationService
    {
        private const string ProgId = "ShowWrite.Photo";
        private static readonly string[] SupportedExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

        [DllImport("shell32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        private const uint SHCNE_ASSOCCHANGED = 0x08000000;
        private const uint SHCNF_IDLIST = 0x0000;

        /// <summary>
        /// 获取当前可执行文件路径
        /// </summary>
        private static string GetExecutablePath()
        {
            return Environment.ProcessPath
                ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
        }

        /// <summary>
        /// 注册 ProgID 及所有支持的扩展名关联
        /// </summary>
        public static bool RegisterAssociations()
        {
            try
            {
                string exePath = GetExecutablePath();
                if (string.IsNullOrEmpty(exePath) || !System.IO.File.Exists(exePath))
                {
                    Logger.Error(nameof(FileAssociationService), $"可执行文件路径无效: {exePath}", null);
                    return false;
                }

                // 1. 注册 ProgID
                using (var progKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
                {
                    progKey.SetValue(null, "ShowWrite 图片");
                    progKey.SetValue("FriendlyTypeName", "ShowWrite 图片");

                    using (var iconKey = progKey.CreateSubKey("DefaultIcon"))
                    {
                        iconKey.SetValue(null, $"\"{exePath}\",0");
                    }

                    using (var cmdKey = progKey.CreateSubKey(@"shell\open\command"))
                    {
                        cmdKey.SetValue(null, $"\"{exePath}\" \"%1\"");
                    }
                }

                // 2. 关联扩展名到 ProgID
                foreach (var ext in SupportedExtensions)
                {
                    using (var extKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}"))
                    {
                        extKey.SetValue(null, ProgId);
                    }
                }

                // 3. 通知 Shell 刷新关联
                SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);

                Logger.Info(nameof(FileAssociationService), "文件关联注册成功");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(nameof(FileAssociationService), $"文件关联注册失败: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// 取消所有扩展名关联并删除 ProgID
        /// </summary>
        public static bool UnregisterAssociations()
        {
            try
            {
                foreach (var ext in SupportedExtensions)
                {
                    // 仅当扩展名指向我们的 ProgID 时才清除，避免误删其他程序的关联
                    using (var extKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ext}", writable: true))
                    {
                        if (extKey != null && (extKey.GetValue(null) as string) == ProgId)
                        {
                            extKey.SetValue(null, ""); // 清空默认值
                        }
                    }
                }

                // 删除 ProgID
                try
                {
                    Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
                }
                catch { }

                SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);

                Logger.Info(nameof(FileAssociationService), "文件关联已取消");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(nameof(FileAssociationService), $"取消文件关联失败: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// 检查是否已注册文件关联
        /// </summary>
        public static bool IsRegistered()
        {
            try
            {
                // 检查 ProgID 是否存在且命令指向当前可执行文件
                using (var cmdKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}\shell\open\command"))
                {
                    if (cmdKey == null)
                        return false;

                    var cmd = cmdKey.GetValue(null) as string;
                    if (string.IsNullOrEmpty(cmd))
                        return false;

                    string exePath = GetExecutablePath();
                    return cmd.IndexOf(exePath, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 获取支持的扩展名列表
        /// </summary>
        public static IEnumerable<string> GetSupportedExtensions()
        {
            return SupportedExtensions;
        }
    }
}
