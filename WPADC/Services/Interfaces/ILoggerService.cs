using System.Collections.ObjectModel;
using WPADC.Models;

namespace WPADC.Services.Interfaces
{
    /// <summary>
    /// 日志服务接口——管日志的记录、存储和显示
    /// </summary>
    /// <remarks>
    /// 这个接口干的事很直白：记日志、存日志、看日志。
    /// 整个系统里谁想记日志都找它，不管是运动控制报错了、相机断开了、
    /// 还是配方执行完了，都调 _loggerService.LogInfo/LogWarning/LogError。
    ///
    /// 日志存在哪？存在内存里的 ObservableCollection 里（最多500条），
    /// UI上的日志列表直接绑定到 Logs 属性，实时显示。
    /// 需要持久化的时候调 ExportToJson 导出到文件。
    ///
    /// 没有继承 IDisposable，因为日志服务不持有需要释放的资源。
    ///
    /// 调用方：MainViewModel 里到处都在调，是最常用的服务之一。
    /// </remarks>
    public interface ILoggerService
    {
        /// <summary>
        /// 日志集合——存在内存里的所有日志条目，最多500条
        /// </summary>
        /// <remarks>
        /// 用 ObservableCollection 是因为UI上的日志列表要实时更新，
        /// 集合变了列表自动刷新，不用手动 NotifyPropertyChanged。
        /// MainViewModel.Logs 属性直接返回这个集合：
        /// public ObservableCollection&lt;LogEntry&gt; Logs => _loggerService.Logs;
        /// 超过500条时旧日志会被自动移除（先进先出）。
        /// </remarks>
        ObservableCollection<LogEntry> Logs { get; }

        /// <summary>
        /// 日志添加事件——每条新日志写进来时触发
        /// </summary>
        /// <remarks>
        /// 参数：LogEntry——刚添加的那条日志
        ///
        /// 谁发布：LogInfo/LogWarning/LogError 内部，添加新日志后触发
        /// 谁订阅：目前主要是外部需要监听日志变化的场景
        ///
        /// 低频关注——不是每个调用方都关心这个事件，大多数场景直接读 Logs 集合就行。
        /// </remarks>
        event Action<LogEntry>? LogAdded;

        /// <summary>
        /// 记录信息级别日志——普通操作记录，比如"相机已连接"、"配方已保存"
        /// </summary>
        /// <param name="message">日志内容，尽量简洁明了，比如"模板 'xxx' 已保存"</param>
        /// <remarks>
        /// 谁调用：MainViewModel 里几乎所有正常操作完成后都会调一下，
        /// 是三种日志级别里调用最频繁的。
        /// </remarks>
        void LogInfo(string message);

        /// <summary>
        /// 记录警告级别日志——不太对劲但还能继续跑的情况，比如"运控卡已断开"
        /// </summary>
        /// <param name="message">警告内容，带【异常】或【警告】前缀方便识别</param>
        /// <remarks>
        /// 谁调用：MainViewModel 里检测到异常状态但没崩的时候调用，
        /// 比如相机断开连接、运控卡掉线等。
        /// 中频调用——比 LogInfo 少得多，但比 LogError 多。
        /// </remarks>
        void LogWarning(string message);

        /// <summary>
        /// 记录错误级别日志——出问题了，需要用户关注，比如"请先连接相机"
        /// </summary>
        /// <param name="message">错误内容，告诉用户出了什么问题</param>
        /// <remarks>
        /// 谁调用：MainViewModel 里操作失败时调用，
        /// 比如没连相机就点采集、模板创建失败等。
        /// 低频调用——希望越少越好。
        /// </remarks>
        void LogError(string message);

        /// <summary>
        /// 导出日志为JSON文件——把内存里的日志持久化到磁盘
        /// </summary>
        /// <param name="filePath">导出文件路径，由用户在保存对话框里选择</param>
        /// <remarks>
        /// 谁调用：MainViewModel.ExportLogsCommand，用户点击"导出日志"按钮
        /// 参数从哪来：filePath 来自 SaveFileDialog 的返回值
        /// 低频操作——用户需要排查问题时才导出。
        /// </remarks>
        void ExportToJson(string filePath);

        /// <summary>
        /// 清空所有日志——把 Logs 集合清零
        /// </summary>
        /// <remarks>
        /// 谁调用：目前主工程里没有调用方，接口作为扩展预留（比如将来增加"重置系统状态"时清空日志）。
        /// 低频操作——偶尔清一下。
        /// </remarks>
        void Clear();
    }
}
