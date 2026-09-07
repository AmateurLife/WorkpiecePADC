using System.Windows;
using System.Windows.Threading;
using HalconDotNet;
using WPADC.Models;
using WPADC.Services.Interfaces;

namespace WPADC.Services.Vision
{
    /// <summary>
    /// 相机采集服务，实现 ICameraService 接口，封装 Halcon GigEVision2 采集接口。
    /// <para>
    /// 这个类干的事情说白了就是：连相机、采图、给外面发图像。
    /// 整个项目的图像数据都从这儿来，是视觉流程的起点。
    /// </para>
    /// <para>
    /// 核心概念说明：
    /// <list type="bullet">
    /// <item><b>HObject</b> —— Halcon 的图像对象类型，这是 Halcon 官方定义的类型，
    /// 可以理解为"一张图"在 Halcon 里的表示方式。HObject 不仅能表示图像，
    /// 还能表示区域(Region)、轮廓(XLD)等，但在这个类里我们只用它来传图像。
    /// HObject 是非托管资源，用完必须调 Dispose() 释放，不然会内存泄漏。</item>
    /// <item><b>HTuple</b> —— Halcon 的通用数据容器，类似一个万能数组，
    /// 可以存整数、浮点数、字符串等。这里用来存采集句柄 _acqHandle。</item>
    /// <item><b>GigEVision2</b> —— 千兆网相机协议，Halcon 官方支持的采集接口之一，
    /// OpenFramegrabber 的第一个参数 "GigEVision2" 就是固定写法，
    /// 表示用千兆网协议连接相机。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 关于仿真模式：当前代码没有单独的仿真分支，连不上真实相机就会直接抛异常。
    /// 如果要做仿真（比如没相机时用随机噪声图像代替真实采集），
    /// 可以在 ConnectCamera 里加个判断：检测不到相机时，不走 OpenFramegrabber，
    /// 而是用 HOperatorSet.GenImageConst 或 GenImageGrayRamp 生成一张假图，
    /// 然后 GrabImage 时返回这张假图就行。目前没做这个，后续有需要再加。
    /// </para>
    /// </summary>
    public class CameraService : ICameraService
    {
        /// <summary>
        /// Halcon 采集句柄，就是 OpenFramegrabber 返回的那个句柄。
        /// 后续所有相机操作（采图、设参数、关相机）都要传这个句柄进去，
        /// 相当于"跟哪台相机通信"的凭证。类型是 HTuple，Halcon 官方约定。
        /// </summary>
        private HTuple _acqHandle = new HTuple();

        /// <summary>
        /// 相机是否已连接。ConnectCamera 成功后置 true，DisconnectCamera 后置 false。
        /// 其他方法（StartLiveDisplay、GrabImage 等）会先检查这个标志，
        /// 没连上就不干活，避免空指针。
        /// </summary>
        private bool _isConnected;

        /// <summary>
        /// 实时预览任务的取消令牌源。
        /// 调 StopLiveDisplay 时调 Cancel()，LiveLoop 里检测到取消请求就退出循环。
        /// 这是 .NET 标准的协作式取消模式，不是强制终止线程。
        /// </summary>
        private CancellationTokenSource _cts;

        /// <summary>
        /// 实时预览的后台 Task。
        /// StartLiveDisplay 时通过 Task.Run 启动，跑的是 LiveLoop 方法。
        /// StopLiveDisplay 时等它结束（最多等2秒）。
        /// </summary>
        private Task _liveTask;

        /// <summary>
        /// 同步锁对象，保护 CurrentImage 的线程安全。
        /// <para>
        /// 为啥要锁？因为 LiveLoop 在后台线程写 CurrentImage，
        /// 而 UI 线程（或者别的调用方）可能同时在读 CurrentImage，
        /// 不加锁就会出竞态条件——你读到一半我正好在 Dispose 旧图，直接崩。
        /// 所以所有对 CurrentImage 的读写都必须在 lock(_lock) 里完成。
        /// </para>
        /// </summary>
        private readonly object _lock = new object();

        /// <summary>是否已释放资源，防止 Dispose 被重复调用</summary>
        private bool _disposed;

        /// <summary>
        /// 日志服务，用来记录系统状态信息。
        /// 相机连接/断开时通过它记录日志。
        /// </summary>
        private readonly ILoggerService _loggerService;

        /// <summary>
        /// 相机是否已连接。只读属性，外面用来判断能不能采图。
        /// </summary>
        public bool IsConnected => _isConnected;

        /// <summary>
        /// 是否正在实时预览。
        /// 判断逻辑：_liveTask 不为 null 且还没跑完（IsCompleted=false），就说明还在预览。
        /// </summary>
        public bool IsLiveDisplay => _liveTask != null && !_liveTask.IsCompleted;

        /// <summary>
        /// 当前采集的最新一帧图像。
        /// <para>
        /// 类型是 object 而不是 HObject，是因为接口 ICameraService 这么定义的，
        /// 方便不同实现（比如以后换个不用 Halcon 的相机）。
        /// 实际运行时里面装的就是 HObject。
        /// </para>
        /// <para>
        /// ⚠️ 线程安全提醒：访问这个属性必须通过 lock(_lock)，
        /// 因为后台 LiveLoop 线程在写，UI 线程可能同时在读。
        /// </para>
        /// </summary>
        public object CurrentImage { get; private set; }

        /// <summary>
        /// 相机错误事件。
        /// <para>
        /// <b>什么时候触发？</b> 连接失败或采图失败时触发，把错误消息字符串传出去。
        /// </para>
        /// <para>
        /// <b>谁发布？</b> CameraService 自己，在 ConnectCamera 的 catch 块和 GrabImage 的 catch 块里触发。
        /// </para>
        /// <para>
        /// <b>谁订阅？</b> MainViewModel 订阅，收到后记日志、更新界面状态（把"相机已连接"改成断开）。
        /// </para>
        /// <para>
        /// 事件类型是 Action&lt;string&gt;（不是 EventHandler），比较轻量，直接传错误消息字符串。
        /// </para>
        /// </summary>
        public event Action<string> CameraError;

        /// <summary>
        /// 图像采集完成事件，每采到一帧图就触发一次。
        /// <para>
        /// <b>谁发布？</b> CameraService 的 LiveLoop 方法，在后台线程采到图后，
        /// 通过 Dispatcher.BeginInvoke 把事件调度到 UI 线程上触发。
        /// </para>
        /// <para>
        /// <b>谁订阅？</b> MainViewModel.OnImageGrabbed 方法订阅。
        /// 订阅者拿到图像后做灰度转换、模板匹配、坐标变换等一整套视觉处理。
        /// </para>
        /// <para>
        /// <b>为什么用 Dispatcher.BeginInvoke？</b> 因为 LiveLoop 跑在后台 Task 里，
        /// 而 WPF 的 UI 控件只能在 UI 线程上操作。如果不调度到 UI 线程，
        /// 订阅者里更新界面就会抛跨线程异常。BeginInvoke 是异步的，不会阻塞采集线程。
        /// </para>
        /// <para>
        /// 事件类型是 EventHandler&lt;HObject&gt;，第二个参数就是采到的图像。
        /// 注意：传出去的 HObject 是 Clone 出来的副本，订阅者用完需要自己 Dispose。
        /// </para>
        /// </summary>
        public event EventHandler<HObject> ImageGrabbed;

        /// <summary>
        /// 构造函数，注入日志服务。
        /// </summary>
        /// <param name="loggerService">
        /// 日志服务，从 DI 容器注入。用于记录相机连接/断开等系统状态信息。
        /// </param>
        public CameraService(ILoggerService loggerService)
        {
            _loggerService = loggerService;
        }

        /// <summary>
        /// 连接 GigEVision2 相机，并设置曝光和增益参数。
        /// <para>
        /// 整个流程：
        /// <list type="number">
        /// <item>先调 DisconnectCamera()，防止重复连接（如果已经连着就先断开再重连）</item>
        /// <item>调 OpenFramegrabber 打开相机，拿到采集句柄 _acqHandle</item>
        /// <item>调 GrabImageStart 让相机进入就绪状态</item>
        /// <item>设置曝光、增益参数（自动/手动、具体数值）</item>
        /// <item>标记 _isConnected = true，发系统状态事件</item>
        /// </list>
        /// </para>
        /// <para>
        /// OpenFramegrabber 的参数说明（这些是 Halcon 官方固定搭配，不用改）：
        /// <list type="bullet">
        /// <item>"GigEVision2" —— 采集接口名称，固定写法</item>
        /// <item>后面的 0, 0, 0, 0, 0, 0 —— 分别是 horizontalResolution、verticalResolution、
        /// imageWidth、imageHeight、startRow、startColumn，0 表示用默认值</item>
        /// <item>"progressive" —— 逐行扫描模式（工业相机常用），另一个选项是 "interlaced"（隔行）</item>
        /// <item>-1 —— bitsPerChannel，-1 表示用相机默认位深</item>
        /// <item>"default" —— colorSpace，默认颜色空间</item>
        /// <item>-1 —— generic 参数，-1 表示默认</item>
        /// <item>"false" —— externalTrigger，不用外触发，用软件触发</item>
        /// <item>"default" —— cameraType</item>
        /// <item>param.CameraSerialNumber —— 相机序列号，这个是我们自己传的，用来区分多台相机</item>
        /// <item>0, -1 —— port、device，用默认值</item>
        /// </list>
        /// </para>
        /// </summary>
        /// <param name="param">相机连接参数，从界面传过来，包含序列号、曝光时间、增益、自动曝光/增益开关</param>
        /// <returns>成功返回"相机连接成功"，失败返回带错误信息的字符串</returns>
        public string ConnectCamera(CameraParams param)
        {
            try
            {
                // 先断开旧连接，防止重复连接导致句柄泄漏
                DisconnectCamera();

                // 打开千兆网相机，拿到采集句柄
                // 这些参数是 Halcon OpenFramegrabber 的标准参数，大部分用默认值就行
                // 唯一需要指定的是 "GigEVision2" 和相机序列号
                HOperatorSet.OpenFramegrabber("GigEVision2", 0, 0, 0, 0, 0, 0, "progressive",
                    -1, "default", -1, "false", "default",
                    param.CameraSerialNumber, 0, -1, out _acqHandle);

                // 让相机进入就绪状态，-1 表示不限超时
                // 调了这个之后才能调 GrabImage 采图
                HOperatorSet.GrabImageStart(_acqHandle, -1);

                // 设置曝光和增益参数，这四个 SetFramegrabberParam 都是 Halcon 官方算子
                // "ExposureAuto"/"GainAuto" 是 Halcon 定义的标准参数名，"On"/"Off" 也是固定值
                // "ExposureTime" 单位是微秒，"Gain" 单位是 dB
                HOperatorSet.SetFramegrabberParam(_acqHandle, "ExposureAuto", param.AutoExposure ? "On" : "Off");
                HOperatorSet.SetFramegrabberParam(_acqHandle, "GainAuto", param.AutoGain ? "On" : "Off");
                HOperatorSet.SetFramegrabberParam(_acqHandle, "ExposureTime", param.ExposureTime);
                HOperatorSet.SetFramegrabberParam(_acqHandle, "Gain", param.Gain);

                _isConnected = true;
                // 记录相机连接状态日志
                _loggerService.LogInfo("相机已连接");
                return "相机连接成功";
            }
            catch (HalconException ex)
            {
                // 连接失败，触发错误事件，让订阅者知道
                CameraError?.Invoke($"相机连接失败: {ex.Message}");
                return $"相机连接失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 断开相机连接，释放所有采集资源。
        /// <para>
        /// 释放顺序很重要：
        /// <list type="number">
        /// <item>先停实时预览（StopLiveDisplay），不然后台还在采图</item>
        /// <item>释放当前图像（CurrentImage 里的 HObject）</item>
        /// <item>关闭采集句柄（CloseFramegrabber）</item>
        /// <item>重置状态标志</item>
        /// </list>
        /// </para>
        /// <para>
        /// CloseFramegrabber 用 try-catch 包着是因为：如果相机已经物理断开了（比如拔了网线），
        /// CloseFramegrabber 可能会抛异常，这时候不用管，反正句柄已经没用了。
        /// </para>
        /// </summary>
        public void DisconnectCamera()
        {
            // 先停预览，不然后台线程还在跑
            StopLiveDisplay();

            lock (_lock)
            {
                // 释放当前图像，HObject 是非托管资源，必须手动 Dispose
                (CurrentImage as HObject)?.Dispose();
                CurrentImage = null;

                // 关闭采集句柄，释放相机资源
                if (_acqHandle != null && _acqHandle.Length > 0)
                {
                    // try-catch：相机可能已经物理断开了，关的时候会报错，忽略就行
                    try { HOperatorSet.CloseFramegrabber(_acqHandle); } catch { }
                    _acqHandle = new HTuple();
                }
                _isConnected = false;
            }

            // 记录相机断开状态日志
            _loggerService.LogInfo("相机已断开");
        }

        /// <summary>
        /// 设置相机曝光时间和增益参数。
        /// <para>
        /// 这个方法通常在实时预览过程中调用，用户在界面上调了曝光/增益滑块，
        /// 就会调这个方法实时更新相机参数，不用断开重连。
        /// </para>
        /// <para>
        /// 如果相机没连上或者句柄无效，直接 return 不做任何操作，避免报错。
        /// </para>
        /// </summary>
        /// <param name="exposureTime">曝光时间，单位微秒(μs)，常见范围 100~100000</param>
        /// <param name="gain">增益值，单位 dB，常见范围 0~20，越大越亮但噪声也越大</param>
        /// <param name="autoExposure">是否启用自动曝光，开了之后相机会自动调节曝光时间</param>
        /// <param name="autoGain">是否启用自动增益，开了之后相机会自动调节增益</param>
        public void SetExposureGain(double exposureTime, double gain, bool autoExposure, bool autoGain)
        {
            // 没连上相机就不操作，静默返回
            if (!_isConnected || _acqHandle.Length == 0) return;
            try
            {
                // 四个参数的设置顺序：先设自动/手动模式，再设具体数值
                // 因为如果设了自动模式，手动设的数值会被相机自动覆盖
                HOperatorSet.SetFramegrabberParam(_acqHandle, "ExposureAuto", autoExposure ? "On" : "Off");
                HOperatorSet.SetFramegrabberParam(_acqHandle, "GainAuto", autoGain ? "On" : "Off");
                HOperatorSet.SetFramegrabberParam(_acqHandle, "ExposureTime", exposureTime);
                HOperatorSet.SetFramegrabberParam(_acqHandle, "Gain", gain);
            }
            catch (HalconException) { }
            // 设置参数失败就静默忽略，不影响主流程
            // 比如相机不支持某个参数，或者相机临时断开了，都不用管
        }

        /// <summary>
        /// 启动实时预览模式。
        /// <para>
        /// 原理：在后台 Task 里跑一个循环（LiveLoop），不停地采图、发事件，
        /// 直到外面调 StopLiveDisplay 取消为止。
        /// </para>
        /// <para>
        /// 如果相机没连上，或者已经在预览中了，就不重复启动。
        /// </para>
        /// </summary>
        public void StartLiveDisplay()
        {
            // 没连相机就不启动
            if (!_isConnected) return;
            // 已经在预览中了就不重复启动
            if (_liveTask != null && !_liveTask.IsCompleted) return;

            _cts = new CancellationTokenSource();
            // Task.Run 在线程池线程上跑，不会阻塞 UI
            _liveTask = Task.Run(() => LiveLoop(_cts.Token));
        }

        /// <summary>
        /// 停止实时预览模式。
        /// <para>
        /// 通过 CancellationToken 取消 LiveLoop 循环，然后等后台任务结束（最多等2秒）。
        /// 2秒超时是防止相机卡死时一直等下去。
        /// </para>
        /// </summary>
        public void StopLiveDisplay()
        {
            // 通知 LiveLoop 退出
            _cts?.Cancel();
            try { _liveTask?.Wait(2000); } catch (AggregateException) { }
            // Wait 可能抛 AggregateException（任务内部异常），忽略就行
            finally
            {
                _cts?.Dispose();
                _cts = null;
                _liveTask = null;
            }
        }

        /// <summary>
        /// 单帧采集图像。跟实时预览不同，这个只采一帧就返回。
        /// <para>
        /// 通常用在标定、创建模板等不需要连续采图的场景。
        /// 比如用户点"采集一帧"按钮，就调这个方法拿一张图。
        /// </para>
        /// </summary>
        /// <returns>采到的 HObject 图像；没连相机或采集失败返回 null</returns>
        public HObject GrabImage()
        {
            if (!_isConnected || _acqHandle.Length == 0) return null;
            try
            {
                // GrabImage 是 Halcon 官方算子，从采集句柄对应的相机抓一帧图
                HOperatorSet.GrabImage(out HObject image, _acqHandle);
                return image;
            }
            catch (HalconException ex)
            {
                // 采图失败，触发错误事件
                CameraError?.Invoke($"采图失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 实时预览循环，在后台 Task 中持续运行。
        /// <para>
        /// 这是整个实时预览的核心。每一轮循环做这些事：
        /// <list type="number">
        /// <item>调 GrabImage 从相机采一帧图</item>
        /// <item>检查图像是否有效（null 或未初始化就跳过）</item>
        /// <item>更新 CurrentImage（加锁保护，因为外面可能同时在读）</item>
        /// <item>Clone 一份图像，通过 Dispatcher.BeginInvoke 在 UI 线程触发 ImageGrabbed 事件</item>
        /// </list>
        /// </para>
        /// <para>
        /// <b>线程安全说明（重要！）：</b>
        /// <list type="bullet">
        /// <item>这个方法跑在后台线程（Task.Run 启动的），对 CurrentImage 的写操作必须加 lock</item>
        /// <item>ImageGrabbed 事件通过 Application.Current.Dispatcher.BeginInvoke 调度到 UI 线程，
        /// 这样订阅者（MainViewModel）可以安全地更新 WPF 控件</item>
        /// <item>BeginInvoke 是异步的，不会阻塞采集线程，采集线程可以马上开始下一帧</item>
        /// </ul>
        /// </list>
        /// </para>
        /// <para>
        /// <b>内存管理说明：</b>
        /// <list type="bullet">
        /// <item>CurrentImage 里存的是 ho_Image.Clone() 的副本，因为 ho_Image 在 finally 里会被 Dispose</item>
        /// <item>传给 ImageGrabbed 的 imageClone 也是 Clone 出来的独立副本，
        /// 订阅者用完后在 finally 里 Dispose，不会影响 CurrentImage</item>
        /// <item>ho_Image 本身在每轮循环的 finally 里 Dispose，防止泄漏</item>
        /// </list>
        /// </para>
        /// <para>
        /// <b>异常处理：</b>
        /// HalconException 通常意味着相机断连了（比如网线拔了），这时候直接 break 退出循环，
        /// 不再尝试采图。其他异常只 Dispose 图像，继续下一轮。
        /// </para>
        /// </summary>
        /// <param name="token">取消令牌，StopLiveDisplay 时会触发取消</param>
        private void LiveLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                HObject ho_Image = null;
                try
                {
                    // 从相机采一帧图
                    HOperatorSet.GrabImage(out ho_Image, _acqHandle);

                    // 图像无效就跳过，等下一帧
                    if (ho_Image == null || !ho_Image.IsInitialized())
                        continue;

                    // 更新 CurrentImage，加锁防止跟外面读的线程冲突
                    lock (_lock)
                    {
                        // 先 Dispose 旧图，再存新图的 Clone 副本
                        // 必须用 Clone，因为 ho_Image 后面还要用，而且 finally 里会 Dispose
                        (CurrentImage as HObject)?.Dispose();
                        CurrentImage = ho_Image.Clone();
                    }

                    // 给订阅者准备一份独立的图像副本
                    var imageClone = ho_Image.Clone();

                    // 通过 Dispatcher 调度到 UI 线程触发事件
                    // 这样订阅者（MainViewModel）可以安全更新 WPF 控件
                    if (Application.Current?.Dispatcher != null)
                    {
                        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                // 在 UI 线程上触发 ImageGrabbed 事件
                                ImageGrabbed?.Invoke(this, imageClone);
                            }
                            catch { }
                            // 订阅者内部异常不传播，防止影响后续帧的采集
                            finally
                            {
                                // 订阅者用完了，释放图像副本
                                imageClone?.Dispose();
                            }
                        }));
                    }
                }
                catch (HalconException)
                {
                    // Halcon 异常通常是相机断连，退出循环
                    ho_Image?.Dispose();
                    break;
                }
                catch
                {
                    // 其他异常不退出循环，继续采下一帧
                    ho_Image?.Dispose();
                }
                finally
                {
                    // 每一轮都释放原始图像，防止内存泄漏
                    ho_Image?.Dispose();
                }
            }
        }

        /// <summary>
        /// 释放相机服务资源，断开相机连接。
        /// 实现 IDisposable 接口，通常在应用退出或服务替换时调用。
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                DisconnectCamera();
                _disposed = true;
            }
        }
    }
}
