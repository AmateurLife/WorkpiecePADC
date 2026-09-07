namespace WPADC.Models
{
    /// <summary>
    /// 日志条目模型 —— 记录系统运行时的单条日志信息。
    /// 所有操作日志、错误日志、调试信息都用这个结构存。
    /// 日志列表在主界面底部显示，方便排查问题。
    /// </summary>
    public class LogEntry
    {
        /// <summary>
        /// 日志时间戳，默认为当前时间。
        /// 精确到毫秒，方便看操作之间的时间间隔和定位耗时。
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.Now;

        /// <summary>
        /// 日志级别，默认"INFO"。
        /// 常用的级别：
        ///   "INFO"  —— 一般信息，比如"开始标定"、"运动完成"
        ///   "WARN"  —— 警告，不影响运行但需要注意，比如"匹配得分偏低"
        ///   "ERROR" —— 错误，影响功能正常使用，比如"相机连接失败"、"运动超时"
        ///   "DEBUG" —— 调试信息，开发时用，发布时一般不显示
        /// </summary>
        public string Level { get; set; } = "INFO";

        /// <summary>
        /// 日志消息内容。
        /// 具体的日志文字，比如"九点标定完成，标定误差: 0.02mm"。
        /// </summary>
        public string Message { get; set; } = "";

        /// <summary>
        /// 返回格式化的日志字符串，格式：[时间戳] [级别] 消息
        /// 比如：[2024-01-15 10:30:45.123] [INFO] 九点标定完成
        /// 这个格式方便日志文件里按时间排序和搜索。
        /// </summary>
        /// <returns>格式化后的日志字符串</returns>
        public override string ToString()
        {
            return $"[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level}] {Message}";
        }
    }
}
