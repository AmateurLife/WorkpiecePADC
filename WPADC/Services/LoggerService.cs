using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using WPADC.Models;
using WPADC.Services.Interfaces;

namespace WPADC.Services
{
    /// <summary>
    /// 全局日志服务——整个程序就这一个日志管理器，谁想记日志就调它。
    /// 用ObservableCollection存日志，界面可以直接绑定显示，新增日志时列表自动更新。
    /// </summary>
    public class LoggerService : ILoggerService
    {
        /// <summary>
        /// 内部日志集合。ObservableCollection的好处是：往里面加/删东西，界面自动刷新，
        /// 不用手动调NotifyPropertyChanged。WPF绑定的标准做法。
        /// </summary>
        private readonly ObservableCollection<LogEntry> _logs = new();

        /// <summary>
        /// 获取日志条目集合，供UI层绑定显示。
        /// </summary>
        /// <remarks>
        /// 为啥限制500条？因为日志是会无限增长的——配方执行一次可能产生几十条日志，
        /// 跑个几小时就几千条了。ObservableCollection绑到界面上，数据太多会卡。
        /// 500条够看最近的操作记录了，再老的也没人看。
        /// 超过500条时自动删最旧的（AddLog方法里处理的）。
        /// </remarks>
        public ObservableCollection<LogEntry> Logs => _logs;

        /// <summary>
        /// 新增日志条目时触发的事件。
        ///
        /// 谁发布：AddLog方法（本类的私有方法）
        /// 谁订阅：目前暂无订阅者（UI直接绑定 Logs 集合即可实时刷新），此事件作为扩展预留，
        /// 以后需要自动滚到底部或做日志联动时再订阅
        /// </summary>
        public event Action<LogEntry>? LogAdded;

        /// <summary>
        /// 记录信息级别日志。最常用的级别，记录正常操作流程。
        /// 比如：连接成功、配方开始执行、回原点完成等。
        /// </summary>
        /// <param name="message">日志消息内容</param>
        public void LogInfo(string message)
        {
            AddLog("INFO", message);
        }

        /// <summary>
        /// 记录警告级别日志。不太对劲但还没出错的情况。
        /// 比如：配置文件找不到用了默认值、运动速度被限幅了等。
        /// </summary>
        /// <param name="message">警告消息内容</param>
        public void LogWarning(string message)
        {
            AddLog("WARN", message);
        }

        /// <summary>
        /// 记录错误级别日志。出错了，需要关注。
        /// 比如：运动控制卡连接失败、配方文件读取失败等。
        /// 这个级别不常用，但一旦出现说明有问题要处理。
        /// </summary>
        /// <param name="message">错误消息内容</param>
        public void LogError(string message)
        {
            AddLog("ERROR", message);
        }

        /// <summary>
        /// 添加日志条目到集合中。
        /// </summary>
        /// <param name="level">日志级别："INFO"、"WARN"、"ERROR"</param>
        /// <param name="message">日志消息内容</param>
        /// <remarks>
        /// 几个关键点：
        /// 1. 用Dispatcher.Invoke确保在UI线程上操作ObservableCollection——
        ///    因为日志可能从后台线程写（比如轮询定时器），直接改集合会跨线程报错
        /// 2. Insert(0, entry)把新日志插到最前面——界面列表倒序显示，最新的在最上面
        /// 3. 超过500条时RemoveAt(Count-1)删最旧的——防止内存无限增长
        /// 4. LogAdded事件在Dispatcher.Invoke外面触发——不用等UI更新完就通知订阅者
        /// </remarks>
        private void AddLog(string level, string message)
        {
            var entry = new LogEntry
            {
                Timestamp = DateTime.Now,
                Level = level,
                Message = message
            };

            // 在UI线程上操作ObservableCollection，防止跨线程异常
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                _logs.Insert(0, entry); // 最新的插最前面
                if (_logs.Count > 500) _logs.RemoveAt(_logs.Count - 1); // 超过500条删最旧的
            });

            // 通知订阅者（MainViewModel用来滚动到最新日志）
            LogAdded?.Invoke(entry);
        }

        /// <summary>
        /// 将当前所有日志导出为JSON文件。方便排查问题或存档。
        /// </summary>
        /// <param name="filePath">导出文件的目标路径，由调用方指定</param>
        /// <remarks>
        /// JSON格式说明：
        /// - WriteIndented=true：格式化输出，带缩进换行，人能看懂
        /// - UnsafeRelaxedJsonEscaping：中文不转义成\uXXXX，直接输出中文
        ///   不加这个的话，中文日志导出来全是\uXXXX，没法看
        ///
        /// 导出的是_logs.ToList()——把ObservableCollection转成普通List再序列化，
        /// 因为JSON序列化器对ObservableCollection的支持不如List好。
        ///
        /// 导出成功会记一条INFO日志，失败记ERROR日志。
        /// </remarks>
        public void ExportToJson(string filePath)
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true, // 格式化输出，带缩进换行
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping // 中文不转义
                };
                string json = JsonSerializer.Serialize(_logs.ToList(), options);
                File.WriteAllText(filePath, json);
                LogInfo($"日志已导出至: {filePath}");
            }
            catch (Exception ex)
            {
                LogError($"日志导出失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 清空所有日志条目。界面上的"清空日志"按钮调这个。
        /// 同样用Dispatcher.Invoke确保在UI线程上操作。
        /// </summary>
        public void Clear()
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() => _logs.Clear());
        }
    }
}
