using System.IO;
using System.Windows;

namespace WPADC.Services
{
    /// <summary>
    /// Halcon 运行时引导：在任何 Halcon 调用之前，
    /// 定位仓库根部的 HalconRuntime 目录并设置 HALCONROOT / PATH，
    /// 使原生 halcon.dll 可被加载。
    /// </summary>
    public static class HalconRuntimeBootstrap
    {
        private static bool _initialized;

        /// <summary>
        /// 初始化 Halcon 运行时环境。幂等，可重复调用。
        /// </summary>
        /// <returns>找到并配置成功返回 true。</returns>
        public static bool EnsureInitialized()
        {
            if (_initialized)
            {
                return true;
            }

            string? runtimeDir = FindRuntimeDirectory(AppContext.BaseDirectory);
            if (runtimeDir == null)
            {
                MessageBox.Show(
                    "未找到 HalconRuntime 目录。\n\n" +
                    "请确认项目根目录下存在 HalconRuntime\\bin\\x64-win64（含 halcon.dll）。",
                    "Halcon 运行时缺失", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            string nativeDir = Path.Combine(runtimeDir, "bin", "x64-win64");

            // HALCONROOT：必须指向本项目 HalconRuntime，强制覆盖系统（C 盘）已设置的默认值，
            // 否则 Halcon 会去系统安装目录读取已过期的 license。
            Environment.SetEnvironmentVariable("HALCONROOT", runtimeDir);

            // HALCONLICENSE：显式指定许可证目录，优先使用项目自带的新 license，
            // 避免读取系统安装目录中已过期的 license 文件。
            string licenseDir = Path.Combine(runtimeDir, "license");
            Environment.SetEnvironmentVariable("HALCONLICENSE", licenseDir);

            // PATH：供 P/Invoke 解析原生 halcon.dll
            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            if (!path.Contains(nativeDir, StringComparison.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable("PATH", nativeDir + ";" + path);
            }

            // license 目录（用户可将 license.dat 放入其中）
            Directory.CreateDirectory(licenseDir);

            _initialized = true;
            return true;
        }

        /// <summary>从起始目录逐级向上查找名为 HalconRuntime 的目录。</summary>
        private static string? FindRuntimeDirectory(string startDir)
        {
            var dir = new DirectoryInfo(startDir);
            for (int i = 0; i < 8 && dir != null; i++)
            {
                string candidate = Path.Combine(dir.FullName, "HalconRuntime");
                if (Directory.Exists(candidate) &&
                    File.Exists(Path.Combine(candidate, "bin", "x64-win64", "halcon.dll")))
                {
                    return candidate;
                }
                dir = dir.Parent;
            }
            return null;
        }
    }
}
