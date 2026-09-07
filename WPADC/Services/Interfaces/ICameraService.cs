using HalconDotNet;
using WPADC.Models;

namespace WPADC.Services.Interfaces
{
    /// <summary>
    /// 相机服务接口——管工业相机的连接、采集和参数设置
    /// </summary>
    /// <remarks>
    /// 这个接口管的是相机相关的所有操作：连相机、采图、调曝光增益、开关实时预览。
    /// 底层用的是 Halcon 的 GigEVision2 采集接口（千兆网/GenICam工业相机，含海康等品牌），通过 GigE 网口连接。
    ///
    /// 为什么继承 IDisposable？因为相机连接是硬件资源，不用了必须断开释放，
    /// 否则其他程序连不上。MainViewModel.Dispose() 里会调 _cameraService.Dispose()。
    ///
    /// 调用方：MainViewModel 里的相机相关 Command（ConnectCameraCommand、
    /// StartLiveDisplayCommand、StopLiveDisplayCommand、ApplyExposureGainCommand 等）
    ///
    /// 两种采集模式：
    /// - 实时预览（LiveDisplay）：连续采集，图像通过 ImageGrabbed 事件推送给订阅者
    /// - 单帧采集（GrabImage）：采一张返回一张，用于模板创建等需要稳定图像的场景
    /// </remarks>
    public interface ICameraService : IDisposable
    {
        /// <summary>
        /// 相机是否已连接——ConnectCamera() 成功后为 true
        /// </summary>
        /// <remarks>
        /// MainViewModel 用这个更新 IsCameraConnected 绑定属性，驱动UI上的连接状态。
        /// 很多操作（采集、预览、设置参数）都会先检查 IsConnected，没连就不让操作。
        /// 低频访问——只在连接/断开时变化。
        /// </remarks>
        bool IsConnected { get; }

        /// <summary>
        /// 是否正在实时显示——StartLiveDisplay() 后为 true，StopLiveDisplay() 后为 false
        /// </summary>
        /// <remarks>
        /// 实时预览模式下相机会持续采集并触发 ImageGrabbed 事件。
        /// MainViewModel 用这个判断当前是否在预览状态。
        /// 中频访问——用户开关预览时变化。
        /// </remarks>
        bool IsLiveDisplay { get; }

        /// <summary>
        /// 当前图像对象——最近一次采集到的图像
        /// </summary>
        /// <remarks>
        /// 返回 object 类型是因为接口层不想直接依赖 Halcon，
        /// 但实际运行时里面存的是 Halcon.HObject。
        /// MainViewModel 里用的时候需要 as HObject 转一下：
        /// var currentImg = _cameraService.CurrentImage as HObject;
        ///
        /// 在实时预览模式下，这个属性会被 ImageGrabbed 事件不断更新。
        /// 在非预览模式下，需要手动调 GrabImage() 采集后才能拿到图像。
        /// </remarks>
        object CurrentImage { get; }

        /// <summary>
        /// 相机错误事件——相机出问题时触发（比如断连、采集超时）
        /// </summary>
        /// <remarks>
        /// 参数：string errorMessage——错误描述
        ///
        /// 谁发布：相机服务内部，采集异常或连接断开时
        /// 谁订阅：MainViewModel.OnCameraError()，收到后停止检测、记录日志
        ///
        /// 低频事件——正常情况下不应该触发，出了问题才发。
        /// </remarks>
        event Action<string> CameraError;

        /// <summary>
        /// 图像采集完成事件——每采到一帧图像就触发
        /// </summary>
        /// <remarks>
        /// 参数：HObject——采集到的图像对象
        ///
        /// 谁发布：相机服务内部，实时预览模式下每帧采集完成后
        /// 谁订阅：MainViewModel.OnImageGrabbed()，收到后更新 Halcon 窗口显示
        ///
        /// 高频事件——实时预览模式下每帧触发一次（帧率取决于相机，通常30fps左右）。
        /// </remarks>
        event EventHandler<HObject> ImageGrabbed;

        /// <summary>
        /// 连接相机——通过 GigE 网口连接海康威视工业相机
        /// </summary>
        /// <param name="param">相机参数，主要是 CameraSerialNumber（相机序列号）</param>
        /// <returns>结果描述字符串，成功返回连接信息，失败返回错误原因</returns>
        /// <remarks>
        /// 谁调用：MainViewModel.ConnectCameraCommand，用户点击"连接相机"按钮
        /// 参数从哪来：CameraParams 来自用户配置或默认值
        /// 连接成功后 IsConnected 变 true。
        /// 低频操作——程序启动时连接一次。
        /// </remarks>
        string ConnectCamera(CameraParams param);

        /// <summary>
        /// 断开相机——释放相机连接资源
        /// </summary>
        /// <remarks>
        /// 谁调用：MainViewModel.DisconnectCamera（用户点击"断开相机"）以及程序退出时的 Dispose 流程
        /// 断开后 IsConnected 变 false，IsLiveDisplay 变 false。
        /// 低频操作——程序退出或手动断开时调用。
        /// </remarks>
        void DisconnectCamera();

        /// <summary>
        /// 设置曝光和增益——调整相机采集参数
        /// </summary>
        /// <param name="exposureTime">曝光时间，单位微秒(μs)，来自 CameraParams.ExposureTime</param>
        /// <param name="gain">增益值，来自 CameraParams.Gain</param>
        /// <param name="autoExposure">是否启用自动曝光，来自 CameraParams.AutoExposure</param>
        /// <param name="autoGain">是否启用自动增益，来自 CameraParams.AutoGain</param>
        /// <remarks>
        /// 谁调用：MainViewModel.SetExposureGainCommand，用户在参数面板上修改曝光/增益后
        /// 参数从哪来：UI上的输入框绑定到 MainViewModel 的 ExposureTime/Gain 等属性
        /// 中频操作——调机时可能频繁调整。
        /// </remarks>
        void SetExposureGain(double exposureTime, double gain, bool autoExposure, bool autoGain);

        /// <summary>
        /// 启动实时显示——开始连续采集，每帧通过 ImageGrabbed 事件推送
        /// </summary>
        /// <remarks>
        /// 谁调用：MainViewModel.StartLiveDisplayCommand，用户点击"实时预览"按钮
        /// 启动后 IsLiveDisplay 变 true，ImageGrabbed 事件开始持续触发。
        /// 中频操作——用户需要看实时画面时开启。
        /// </remarks>
        void StartLiveDisplay();

        /// <summary>
        /// 停止实时显示——结束连续采集
        /// </summary>
        /// <remarks>
        /// 谁调用：MainViewModel.StopLiveDisplayCommand
        /// 停止后 IsLiveDisplay 变 false，ImageGrabbed 事件不再触发。
        /// </remarks>
        void StopLiveDisplay();

        /// <summary>
        /// 单帧采集——采一张图返回，用于模板创建等需要稳定图像的场景
        /// </summary>
        /// <returns>采集到的 Halcon 图像对象（HObject）</returns>
        /// <remarks>
        /// 谁调用：MainViewModel 里创建模板时需要获取当前帧
        /// 和实时预览的区别：这个是采一张就完事，不是连续采集。
        /// 必须先连接相机才能调用，否则返回空图像。
        /// 中频操作——创建模板或手动采集时使用。
        /// </remarks>
        HObject GrabImage();
    }
}
