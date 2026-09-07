using System.IO;
using System.Text.Json;
using WPADC.Models;
using WPADC.Services.Interfaces;

namespace WPADC.Services
{
    /// <summary>
    /// 配置持久化服务——统一管理5类参数的JSON序列化/反序列化。
    /// 程序关闭时保存参数到文件，启动时从文件加载，这样用户设的参数不会丢。
    /// </summary>
    /// <remarks>
    /// 5类配置参数：
    /// 1. camera.json —— 相机参数（曝光、增益、分辨率等）
    /// 2. matching.json —— 匹配参数（模板、评分阈值等）
    /// 3. calibration.json —— 标定数据（像素到mm的转换矩阵等）
    /// 4. motion.json —— 运动参数（速度、加速度、回原点方向等）
    /// 5. correction.json —— 修正参数（偏移补偿等）
    ///
    /// 所有配置都用JSON格式存，一个类别一个文件，放在同一个目录下。
    /// </remarks>
    public class ConfigService : IConfigService
    {
        /// <summary>
        /// JSON序列化选项，全局共用一份。
        /// </summary>
        /// <remarks>
        /// 两个关键配置：
        /// - WriteIndented=true：格式化输出，带缩进换行。这样用户可以直接打开JSON文件看，
        ///   不然全挤在一行没法读。生产环境可以关掉省空间，但调试阶段开着方便。
        /// - UnsafeRelaxedJsonEscaping：中文不转义成\uXXXX，直接输出中文。
        ///   不加这个的话，配置文件里的中文（比如注释、描述）全变成\uXXXX，没法看。
        ///   "Unsafe"听着吓人，其实只是放宽了JSON规范的转义要求，对咱们这种本地配置文件没风险。
        /// </remarks>
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>
        /// 将指定配置对象序列化为JSON并保存到文件。
        /// </summary>
        /// <typeparam name="T">配置对象的类型（CameraParams、MotionParams等）</typeparam>
        /// <param name="filePath">配置文件的目标保存路径，由调用方拼好传进来</param>
        /// <param name="config">要保存的配置对象实例</param>
        /// <exception cref="Exception">序列化或文件写入失败时抛出</exception>
        /// <remarks>
        /// 如果目标目录不存在会自动创建——防止首次运行时目录不存在报错。
        /// </remarks>
        public void SaveConfig<T>(string filePath, T config)
        {
            try
            {
                // 确保目录存在，不存在就创建
                string dir = Path.GetDirectoryName(filePath) ?? "";
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // 序列化并写入文件
                string json = JsonSerializer.Serialize(config, JsonOptions);
                File.WriteAllText(filePath, json);
            }
            catch (Exception ex)
            {
                throw new Exception($"配置保存失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 从JSON文件反序列化加载配置对象。
        /// </summary>
        /// <typeparam name="T">配置对象的类型</typeparam>
        /// <param name="filePath">配置文件的路径</param>
        /// <returns>反序列化后的配置对象；文件不存在时返回default（null或默认值）</returns>
        /// <exception cref="Exception">反序列化或文件读取失败时抛出</exception>
        /// <remarks>
        /// 文件不存在不报错，返回default——首次运行时没有配置文件是正常的，
        /// 调用方拿到null后用代码里的默认值就行。
        /// </remarks>
        public T? LoadConfig<T>(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return default; // 文件不存在返回默认值
                string json = File.ReadAllText(filePath);
                return JsonSerializer.Deserialize<T>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                throw new Exception($"配置加载失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 一次性保存全部5类配置参数到指定目录。
        /// </summary>
        /// <param name="camera">相机参数配置</param>
        /// <param name="matching">匹配参数配置</param>
        /// <param name="calib">标定数据配置</param>
        /// <param name="motion">运动参数配置</param>
        /// <param name="correction">修正参数配置</param>
        /// <param name="dirPath">配置文件的保存目录路径</param>
        /// <remarks>
        /// 文件组织方式：5个JSON文件放在同一个目录下，文件名固定：
        /// - camera.json —— 相机参数
        /// - matching.json —— 匹配参数
        /// - calibration.json —— 标定数据
        /// - motion.json —— 运动参数
        /// - correction.json —— 修正参数
        ///
        /// 目录不存在会自动创建。
        /// 每个文件独立保存，某个保存失败不影响其他文件。
        /// </remarks>
        public void SaveAllParams(CameraParams camera, MatchingParams matching,
            CalibrationData calib, MotionParams motion, CorrectionParams correction,
            string dirPath)
        {
            if (!Directory.Exists(dirPath)) Directory.CreateDirectory(dirPath);
            SaveConfig(Path.Combine(dirPath, "camera.json"), camera);
            SaveConfig(Path.Combine(dirPath, "matching.json"), matching);
            SaveConfig(Path.Combine(dirPath, "calibration.json"), calib);
            SaveConfig(Path.Combine(dirPath, "motion.json"), motion);
            SaveConfig(Path.Combine(dirPath, "correction.json"), correction);
        }

        /// <summary>
        /// 一次性从指定目录加载全部5类配置参数。
        /// </summary>
        /// <param name="dirPath">配置文件所在的目录路径</param>
        /// <returns>包含5类配置参数的元组，依次为相机、匹配、标定、运动、修正参数。
        /// 某个文件不存在时对应返回default。</returns>
        /// <remarks>
        /// 跟SaveAllParams对应，文件名必须一致。
        /// 某个文件缺失不影响其他文件的加载，缺失的返回null/默认值。
        /// </remarks>
        public (CameraParams? camera, MatchingParams? matching, CalibrationData? calib,
                MotionParams? motion, CorrectionParams? correction) LoadAllParams(string dirPath)
        {
            var camera = LoadConfig<CameraParams>(Path.Combine(dirPath, "camera.json"));
            var matching = LoadConfig<MatchingParams>(Path.Combine(dirPath, "matching.json"));
            var calib = LoadConfig<CalibrationData>(Path.Combine(dirPath, "calibration.json"));
            var motion = LoadConfig<MotionParams>(Path.Combine(dirPath, "motion.json"));
            var correction = LoadConfig<CorrectionParams>(Path.Combine(dirPath, "correction.json"));
            return (camera, matching, calib, motion, correction);
        }
    }
}
