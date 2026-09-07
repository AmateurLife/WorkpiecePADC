using WPADC.Models;

namespace WPADC.Services.Interfaces
{
    /// <summary>
    /// 配置服务接口——管参数的保存和加载
    /// </summary>
    /// <remarks>
    /// 这个接口干的事很简单：把参数对象序列化成JSON存到文件，或者从文件反序列化回来。
    /// 系统里有5类参数需要持久化：相机参数、匹配参数、标定数据、运动参数、校正参数。
    ///
    /// 没有继承 IDisposable，因为不持有需要释放的资源。
    ///
    /// 调用方：MainViewModel 里注入了 _configService 但当前没直接调用，
    /// 参数的保存/加载目前走的是 MainViewModel 内部直接用 JsonSerializer 实现。
    /// 这个服务作为更规范的实现预留，以后重构时统一走这个接口。
    /// </remarks>
    public interface IConfigService
    {
        /// <summary>
        /// 保存配置到JSON文件——泛型方法，什么类型的配置都能存
        /// </summary>
        /// <typeparam name="T">配置类型，比如 CameraParams、MotionParams 等</typeparam>
        /// <param name="filePath">保存路径，比如 "Config/camera.json"</param>
        /// <param name="config">要保存的配置对象</param>
        /// <remarks>
        /// 内部用 System.Text.Json 序列化，写入UTF-8格式的JSON文件。
        /// 低频操作——只在用户点"保存参数"或程序自动保存时调用。
        /// </remarks>
        void SaveConfig<T>(string filePath, T config);

        /// <summary>
        /// 从JSON文件加载配置——泛型方法，什么类型的配置都能读
        /// </summary>
        /// <typeparam name="T">配置类型，比如 CameraParams、MotionParams 等</typeparam>
        /// <param name="filePath">文件路径，和 SaveConfig 用的路径一致</param>
        /// <returns>反序列化出来的配置对象；文件不存在或格式错误返回 null</returns>
        /// <remarks>
        /// 内部用 System.Text.Json 反序列化，读UTF-8格式的JSON文件。
        /// 如果文件不存在或JSON格式不对，返回 default(T?) 即 null，不会抛异常。
        /// 低频操作——程序启动时加载一次。
        /// </remarks>
        T? LoadConfig<T>(string filePath);

        /// <summary>
        /// 批量保存5类参数——一次性把所有参数存到指定目录
        /// </summary>
        /// <param name="camera">相机参数（曝光、增益、自动曝光/增益开关）</param>
        /// <param name="matching">匹配参数（最低分数、角度范围、贪婪度）</param>
        /// <param name="calib">标定数据（像素点、物理点、仿射变换矩阵）</param>
        /// <param name="motion">运动参数（速度、加速度、回零配置）</param>
        /// <param name="correction">校正参数（XY偏移阈值、角度阈值）</param>
        /// <param name="dirPath">保存目录，通常是 "Config/"</param>
        /// <remarks>
        /// 内部就是调5次 SaveConfig，分别存成5个JSON文件：
        /// camera.json、matching.json、calibration.json、motion.json、correction.json
        /// 低频操作——用户点"保存所有参数"时调用。
        /// </remarks>
        void SaveAllParams(CameraParams camera, MatchingParams matching, CalibrationData calib, MotionParams motion, CorrectionParams correction, string dirPath);

        /// <summary>
        /// 批量加载5类参数——一次性从指定目录读取所有参数
        /// </summary>
        /// <param name="dirPath">加载目录，和 SaveAllParams 用的目录一致</param>
        /// <returns>
        /// 元组 (camera, matching, calib, motion, correction)，
        /// 每个元素都可能为 null（对应文件不存在时）。
        /// 调用方需要检查 null 再使用。
        /// </returns>
        /// <remarks>
        /// 内部就是调5次 LoadConfig，分别读5个JSON文件。
        /// 低频操作——程序启动时调用一次。
        /// </remarks>
        (CameraParams? camera, MatchingParams? matching, CalibrationData? calib, MotionParams? motion, CorrectionParams? correction) LoadAllParams(string dirPath);
    }
}
