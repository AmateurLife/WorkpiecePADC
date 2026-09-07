using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HalconDotNet;
using WPADC.Models;
using WPADC.Services;
using WPADC.Services.Interfaces;
using WPADC.Services.Vision;
using WPADC.Views;

namespace WPADC.ViewModels
{
    /// <summary>
    /// 主视图模型——整个程序的"大脑"，所有业务逻辑都在这里
    /// </summary>
    /// <remarks>
    /// 这个类是整个WPADC纠偏点胶系统的核心控制器，负责把视觉检测、运动控制、点胶配方
    /// 这三大模块串起来。界面上看到的所有数据、按钮、状态变化，最终都从这里驱动。
    ///
    /// 主要职责：
    /// - 相机采集 &amp; 图像处理：连接工业相机、实时预览、曝光增益调节
    /// - 模板匹配 &amp; Mark检测：双Mark形状匹配、像素→物理坐标变换
    /// - 运动控制：JOG手动、直线/圆弧插补、回原点
    /// - 点胶配方管理：录制轨迹、保存/加载配方、自动纠偏执行
    /// - 急停安全机制：全局最高优先级，一键停止所有运动和出胶
    ///
    /// 架构说明：
    /// - 采用MVVM模式：继承 CommunityToolkit.Mvvm 的 ObservableObject 基类（提供 SetProperty 属性通知），
    ///   命令用 CommunityToolkit.Mvvm 的 RelayCommand（Execute + CanExecute 委托）。
    ///   说明：这里直接复用了社区工具包，而不是手写 ViewModelBase/RelayCommand，属性与命令都手动显式声明
    /// - 通过DI构造函数注入9个服务依赖，所有服务都是接口类型，方便单元测试时替换成Mock
    /// - 实现IDisposable接口，程序退出时释放相机、运控卡等硬件资源
    ///
    /// 数据流向：
    /// 相机采图 → 灰度转换 → 双Mark模板匹配 → 像素→物理坐标变换 → 平滑滤波 → 纠偏计算 → 自动触发配方执行
    /// </remarks>
    public partial class MainViewModel : ObservableObject, IDisposable
    {
        // ==================== DI注入的服务字段 ====================
        // 这10个服务全部通过构造函数注入，类型都是接口而不是具体实现类
        // 好处：1.依赖关系一目了然 2.换实现只改DI注册 3.单测可以传Mock

        /// <summary>
        /// 相机服务——管工业相机的连接、采集、曝光增益
        /// </summary>
        /// <remarks>
        /// 接口类型ICameraService，实际注入的是CameraService（Halcon GigEVision2 采集，支持海康等千兆网相机）
        /// 常用方法：ConnectCamera、DisconnectCamera、StartLiveDisplay、SetExposureGain
        /// </remarks>
        private readonly ICameraService _cameraService;

        /// <summary>
        /// 标定服务——管九点标定数据和仿射变换矩阵
        /// </summary>
        /// <remarks>
        /// 九点标定就是拿9个已知像素+物理坐标对，算出一个2D仿射变换矩阵
        /// 后续所有像素→物理坐标的转换都靠这个矩阵
        /// </remarks>
        private readonly ICalibrationService _calibrationService;

        /// <summary>
        /// 畸变校正服务——处理镜头畸变补偿
        /// </summary>
        /// <remarks>
        /// 工业镜头多少有点畸变（边缘变形），这个服务负责校正
        /// 目前项目中用得不多，主要是预留接口
        /// </remarks>
        private readonly IDistortionCorrectionService _distortionService;

        /// <summary>
        /// 模板管理服务——创建/保存/加载/删除Mark模板
        /// </summary>
        /// <remarks>
        /// 核心服务之一。双Mark模板（Mark1+Mark2）就是它管的
        /// 常用方法：CreateMarkTemplate、SaveTemplate、LoadTemplate
        /// 关键属性：Mark1Created、Mark2Created、TemplateLoaded、ModelID1、ModelID2
        /// </remarks>
        private readonly ITemplateManagerService _templateService;

        /// <summary>
        /// Mark检测服务——执行基于模板的形状匹配检测
        /// </summary>
        /// <remarks>
        /// 核心服务之一。拿模板去图像上找Mark点，返回匹配分数和像素坐标
        /// 核心方法：FindBothMarks（同时找Mark1和Mark2）
        /// </remarks>
        private readonly IMarkDetectionService _markDetection;

        /// <summary>
        /// 坐标变换服务——像素坐标→物理坐标的仿射变换
        /// </summary>
        /// <remarks>
        /// 底层就是矩阵乘法：[像素Row,Col] × 仿射矩阵 → [物理X,Y]
        /// 不过实际代码中直接用了CalibrationData.PixelToWorld，这个服务用得不多
        /// </remarks>
        private readonly ICoordinateTransformService _coordTransform;

        /// <summary>
        /// 运动控制服务——JOG/插补/回原点/急停等操作
        /// </summary>
        /// <remarks>
        /// 核心服务之一。当前是仿真模式（MotionSimulatorService），实际部署时换成固高SDK实现
        /// 常用方法：MoveHome、StartJog、StopJog、LinearInterpolation、ArcInterpolation、
        ///          ExecuteRecipe、EmergencyStop、SetGlueOutput
        /// 关键事件：PositionUpdated、RecipeExecutionCompleted、GlueStateChanged
        /// </remarks>
        private readonly IMotionService _motionService;

        /// <summary>
        /// 日志服务——记录运行日志并显示到UI
        /// </summary>
        /// <remarks>
        /// 每个方法里到处都在用，LogInfo/LogWarning/LogError三个级别
        /// 日志同时输出到界面列表和磁盘文件
        /// </remarks>
        private readonly ILoggerService _loggerService;

        /// <summary>
        /// 配置服务——管理系统配置参数
        /// </summary>
        /// <remarks>
        /// 目前项目中用得不多，主要是预留接口
        /// </remarks>
        private readonly IConfigService _configService;

        // ==================== 图像 & 检测状态字段 ====================

        /// <summary>
        /// 当前采集的Halcon图像对象，绑定到界面上的HalconWindow显示
        /// </summary>
        private HObject _currentImage;

        /// <summary>
        /// 是否为第一帧图像标志
        /// </summary>
        /// <remarks>
        /// 第一帧图像到达时，需要把图像尺寸通知给View层做窗口适配（缩放比例计算）
        /// 适配完就设为false，后续帧不再触发
        /// </remarks>
        private bool _isFirstImage = true;

        /// <summary>
        /// 检测进行中标志——防止检测逻辑重入
        /// </summary>
        /// <remarks>
        /// 【踩坑记录】这个标志必须在所有return路径上重置为false！
        /// 之前有个bug：_pauseAxisUpdates为true时提前return，但忘了重置_isDetecting，
        /// 导致后续检测永远被跳过（_isDetecting一直是true），配方无法触发。
        /// 所以finally块里一定要 _isDetecting = false
        ///
        /// 用volatile修饰是因为它在UI线程和Task.Run线程之间共享
        /// </remarks>
        private volatile bool _isDetecting;

        /// <summary>
        /// 检测使能标志——控制检测定时器是否触发检测逻辑
        /// </summary>
        /// <remarks>
        /// StartDetection设为true，StopDetection设为false
        /// 跟_isDetecting的区别：_detectionEnabled是"允不允许检测"，_isDetecting是"当前是不是正在检测"
        /// </remarks>
        private bool _detectionEnabled;

        /// <summary>
        /// 点胶进行中标志——当前正在执行点胶配方
        /// </summary>
        /// <remarks>
        /// 为true时：状态栏显示"点胶中"（绿色），暂停轴位置更新，防止检测干扰运动
        /// 配方执行完成后在OnRecipeExecutionCompleted回调中重置为false
        /// </remarks>
        private bool _isDispensing;

        /// <summary>
        /// 暂停轴位置更新标志——插补运动/配方执行期间为true
        /// </summary>
        /// <remarks>
        /// 点胶时轴在动，如果检测还在跑，会频繁更新Mark坐标导致显示抖动
        /// 所以点胶期间把轴更新暂停掉，配方执行完再恢复
        /// </remarks>
        private bool _pauseAxisUpdates;

        /// <summary>
        /// 当前绑定的点胶配方——检测到Mark偏移后据此执行纠偏点胶
        /// </summary>
        /// <remarks>
        /// 加载模板时自动关联配方，SaveRecipe时也会绑定
        /// 为null时检测到双Mark会提示"等待配方创建"
        /// </remarks>
        private DispensingRecipe _boundRecipe;

        /// <summary>
        /// 检测定时器——每200ms触发一次Mark检测
        /// </summary>
        /// <remarks>
        /// DispatcherTimer是在UI线程上触发的，所以回调里可以直接更新UI属性
        /// 间隔200ms是经验值：太快浪费CPU，太慢响应不够及时
        /// </remarks>
        private DispatcherTimer _detectionTimer;

        /// <summary>
        /// 应用程序基目录路径——定位模板/配方/标定等文件的根目录
        /// </summary>
        /// <remarks>
        /// 等于AppDomain.CurrentDomain.BaseDirectory，即exe所在目录
        /// 所有文件路径都基于此拼接：Templates/、Recipes/、Calibration/、CalibrationData/
        /// </remarks>
        private readonly string _baseDir;

        /// <summary>
        /// 标定数据——包含仿射变换矩阵和九点标定参数
        /// </summary>
        /// <remarks>
        /// 加载优先级：1.按名称从CalibrationData目录加载 2.从Calibration目录加载 3.使用HDevelop导出默认数据
        /// 核心方法：PixelToWorld（像素→物理坐标变换）
        /// </remarks>
        private CalibrationData _calibData;

        // ==================== 运动状态字段 ====================

        /// <summary>
        /// 轴运动中标志——用于检测"运动停止"这个时刻
        /// </summary>
        /// <remarks>
        /// 在OnMotionPositionUpdated回调里，当_wasMoving=true且isMoving=false时，
        /// 说明轴刚停下来，可以做一些收尾工作（比如判断回原完成）
        /// </remarks>
        private bool _wasMoving;

        /// <summary>
        /// 回原点进行中标志
        /// </summary>
        /// <remarks>
        /// MoveHome是异步的，设为true后等轴停下来再重置
        /// 目前只是标记用，没有做太多逻辑
        /// </remarks>
        private bool _homeInProgress;

        // ==================== 平滑滤波 & 跳变检测字段 ====================
        // 这些字段配合SmoothValue方法和IsPositionJump方法使用
        // 目的：让检测到的Mark坐标不抖动，同时过滤掉异常跳变

        /// <summary>Mark1平滑后物理坐标X（mm）</summary>
        private double _smoothMark1PhysX, _smoothMark1PhysY;
        /// <summary>Mark2平滑后物理坐标X/Y（mm）</summary>
        private double _smoothMark2PhysX, _smoothMark2PhysY;
        /// <summary>Mark1上一次原始物理坐标，用于跳变检测</summary>
        private double _lastRawM1PhysX, _lastRawM1PhysY;
        /// <summary>Mark2上一次原始物理坐标，用于跳变检测</summary>
        private double _lastRawM2PhysX, _lastRawM2PhysY;
        /// <summary>是否已有Mark1上一次原始坐标（首次检测时为false，没有历史数据可比较）</summary>
        private bool _hasLastRawM1, _hasLastRawM2;
        /// <summary>平滑后的偏移量（ΔX, ΔY, ΔAngle）——对偏差值也做平滑，避免纠偏量突变</summary>
        private double _smoothDeltaX, _smoothDeltaY, _smoothDeltaAngle;

        /// <summary>
        /// 指数移动平均平滑因子（0~1）
        /// </summary>
        /// <remarks>
        /// 值越大越跟随原始值（响应快但抖），值越小越平滑（稳但滞后）
        /// 0.3是经验值，兼顾响应速度和稳定性
        /// 公式：smoothValue += SmoothFactor * (rawValue - smoothValue)
        /// </remarks>
        private const double SmoothFactor = 0.3;

        /// <summary>
        /// 最大跳变阈值（mm）——超过此距离的坐标变化视为异常跳变，不予采纳
        /// </summary>
        /// <remarks>
        /// 3.0mm是经验值。正常工件移动不会超过这个值，超过说明模板匹配出了问题
        /// 跳变值不参与平滑滤波，直接忽略，等下一帧正常值再更新
        /// </remarks>
        private const double MaxJumpMM = 3.0;

        // ==================== 检测 & 模板状态字段 ====================

        /// <summary>检测计数器——累计检测次数，首次检测（_detectionCount==1）不做平滑直接赋值</summary>
        private int _detectionCount;
        /// <summary>当前加载的模板名称——加载模板时设置，保存配方时关联到配方</summary>
        private string _currentTemplateName = "";
        /// <summary>首次检测完成标志</summary>
        private bool _firstDetectionDone;
        /// <summary>等待配方创建标志——模板已加载但尚未关联配方时为true</summary>
        private bool _waitingForRecipe;
        /// <summary>正在设置起点标志——配方录制过程中标记"起点确认"阶段</summary>
        private bool _settingStartPoint;

        /// <summary>
        /// 判断坐标是否发生异常跳变
        /// </summary>
        /// <param name="rawX">当前原始X坐标</param>
        /// <param name="rawY">当前原始Y坐标</param>
        /// <param name="lastX">上一次X坐标</param>
        /// <param name="lastY">上一次Y坐标</param>
        /// <param name="hasLast">是否已有上一次坐标记录（首次检测时为false）</param>
        /// <returns>true=发生跳变（距离超过3mm），false=正常</returns>
        /// <remarks>
        /// 算法很简单：算当前坐标和上一次坐标的欧氏距离，超过MaxJumpMM(3.0mm)就是跳变。
        /// 跳变通常是因为模板匹配失败或图像异常，平滑滤波时会忽略跳变值。
        /// </remarks>
        private bool IsPositionJump(double rawX, double rawY, double lastX, double lastY, bool hasLast)
        {
            if (!hasLast) return false;
            double dx = rawX - lastX;
            double dy = rawY - lastY;
            return Math.Sqrt(dx * dx + dy * dy) > MaxJumpMM;
        }

        #region 相机参数
        // 这些参数对应界面上的相机设置区域，连接相机时会传给CameraService

        /// <summary>相机序列号——海康威视相机的唯一标识，用于连接指定相机</summary>
        private string _cameraSerialNumber = "c42f90f6ad7a_GEV_MVCA06010GC";
        /// <summary>相机序列号——连接相机时传给CameraService，必须跟相机实际序列号一致</summary>
        public string CameraSerialNumber { get => _cameraSerialNumber; set => SetProperty(ref _cameraSerialNumber, value); }

        /// <summary>曝光时间（μs）——控制相机传感器曝光时长，值越大图像越亮</summary>
        private double _exposureTime = 5000;
        /// <summary>曝光时间（μs）——默认5000μs=5ms，根据现场光照调整</summary>
        public double ExposureTime { get => _exposureTime; set => SetProperty(ref _exposureTime, value); }

        /// <summary>模拟增益值——放大传感器信号强度，值越大图像越亮但噪声也越多</summary>
        private double _gain = 1;
        /// <summary>模拟增益值——默认1，一般不超过10，太高了噪点很重</summary>
        public double Gain { get => _gain; set => SetProperty(ref _gain, value); }

        /// <summary>自动曝光开关——开启后相机自动调节曝光时间</summary>
        private bool _autoExposure;
        /// <summary>自动曝光开关——常用，现场光照变化大时打开</summary>
        public bool AutoExposure { get => _autoExposure; set => SetProperty(ref _autoExposure, value); }

        /// <summary>自动增益开关——开启后相机自动调节增益值</summary>
        private bool _autoGain;
        /// <summary>自动增益开关——不太常用，一般手动调增益更可控</summary>
        public bool AutoGain { get => _autoGain; set => SetProperty(ref _autoGain, value); }

        #endregion

        #region 匹配参数
        // Halcon形状模板匹配的参数，直接影响检测成功率和速度

        /// <summary>
        /// 最小匹配分数阈值（0~1）——低于这个分数的匹配结果直接丢弃
        /// </summary>
        /// <remarks>
        /// 0.7是经验值，太低容易误匹配，太高可能漏匹配
        /// 这个值是只读的（=> 0.7），界面不能改，写死在代码里
        /// </remarks>
        public double MinScore => 0.7;

        /// <summary>
        /// 搜索贪婪度（0~1）——值越大搜索越快但可能遗漏匹配
        /// </summary>
        /// <remarks>
        /// 0.9是经验值，偏快但偶尔会漏。如果发现漏匹配可以降到0.7-0.8
        /// 同样是只读的，写死在代码里
        /// </remarks>
        public double Greediness => 0.9;

        #endregion

        #region 运动参数
        // JOG和回原点的运动参数，对应界面上的运动设置区域

        /// <summary>回原点速度（mm/s）——默认50，回原不需要太快</summary>
        private double _homeVelocity = 50;
        /// <summary>回原点速度（mm/s）——传给MotionService.MoveHome</summary>
        public double HomeVelocity { get => _homeVelocity; set => SetProperty(ref _homeVelocity, value); }

        /// <summary>JOG手动移动速度（mm/s）——默认100，手动调位时用</summary>
        private double _jogVelocity = 100;
        /// <summary>JOG手动移动速度（mm/s）——传给MotionService.StartJog</summary>
        public double JogVelocity { get => _jogVelocity; set => SetProperty(ref _jogVelocity, value); }

        /// <summary>JOG加速度（mm/s²）——默认100</summary>
        private double _jogAcceleration = 100;
        /// <summary>JOG加速度（mm/s²）——传给MotionService.StartJog</summary>
        public double JogAcceleration { get => _jogAcceleration; set => SetProperty(ref _jogAcceleration, value); }

        /// <summary>JOG减速度（mm/s²）——默认100</summary>
        private double _jogDeceleration = 100;
        /// <summary>JOG减速度（mm/s²）——传给MotionService.StartJog</summary>
        public double JogDeceleration { get => _jogDeceleration; set => SetProperty(ref _jogDeceleration, value); }

        #endregion

        #region 插补参数 - 直线
        // 直线插补的参数，对应界面上的"直线插补"设置区域

        /// <summary>直线插补目标X坐标（mm）——默认10</summary>
        private double _linearTargetX = 10;
        /// <summary>直线插补目标X坐标（mm）——从当前位置直线运动到这个X</summary>
        public double LinearTargetX { get => _linearTargetX; set => SetProperty(ref _linearTargetX, value); }

        /// <summary>直线插补目标Y坐标（mm）——默认10</summary>
        private double _linearTargetY = 10;
        /// <summary>直线插补目标Y坐标（mm）——从当前位置直线运动到这个Y</summary>
        public double LinearTargetY { get => _linearTargetY; set => SetProperty(ref _linearTargetY, value); }

        /// <summary>直线插补速度（mm/s）——默认375</summary>
        private double _linearVelocity = 375;
        /// <summary>直线插补速度（mm/s）——传给MotionService.LinearInterpolation</summary>
        public double LinearVelocity { get => _linearVelocity; set => SetProperty(ref _linearVelocity, value); }

        /// <summary>直线插补加速度（mm/s²）——默认825</summary>
        private double _linearAcceleration = 825;
        /// <summary>直线插补加速度（mm/s²）——传给MotionService.LinearInterpolation</summary>
        public double LinearAcceleration { get => _linearAcceleration; set => SetProperty(ref _linearAcceleration, value); }

        /// <summary>直线插补减速度（mm/s²）——默认825</summary>
        private double _linearDeceleration = 825;
        /// <summary>直线插补减速度（mm/s²）——传给MotionService.LinearInterpolation</summary>
        public double LinearDeceleration { get => _linearDeceleration; set => SetProperty(ref _linearDeceleration, value); }

        #endregion

        #region 插补参数 - 圆弧（固高R半径模式）
        // 圆弧插补参数，对应界面上的"圆弧插补"设置区域
        // 固高运动控制卡的R半径模式：起点=当前轴位置，终点=ArcEndPointX/Y，R=半径，方向=CW/CCW
        // 不需要输入圆心坐标，由起点+终点+R自动计算圆心

        /// <summary>圆弧终点X坐标（mm）——默认0</summary>
        private double _arcEndPointX = 0;
        /// <summary>圆弧终点X坐标（mm）——圆弧运动的目标X位置</summary>
        public double ArcEndPointX { get => _arcEndPointX; set => SetProperty(ref _arcEndPointX, value); }

        /// <summary>圆弧终点Y坐标（mm）——默认0</summary>
        private double _arcEndPointY = 0;
        /// <summary>圆弧终点Y坐标（mm）——圆弧运动的目标Y位置</summary>
        public double ArcEndPointY { get => _arcEndPointY; set => SetProperty(ref _arcEndPointY, value); }

        /// <summary>
        /// 圆弧半径R（正=劣弧＜180°，负=优弧＞180°）
        /// </summary>
        /// <remarks>
        /// 固高R半径模式的关键参数：
        /// - R > 0：走劣弧（圆心角≤180°，短弧）
        /// - R < 0：走优弧（圆心角>180°，长弧）
        /// - 起点=上一段插补结束点位（当前轴位置），不需要重复输入起点
        /// - 圆心由起点+终点+R自动推导，不需要手动输入
        /// </remarks>
        private double _arcRadiusR = 0;
        /// <summary>
        /// 圆弧半径R（正=劣弧＜180°，负=优弧＞180°）
        /// </summary>
        /// <remarks>
        /// 固高R半径模式：起点=上一段插补结束点位（当前轴位置），不需要重复输入起点
        /// </remarks>
        public double ArcRadiusR { get => _arcRadiusR; set => SetProperty(ref _arcRadiusR, value); }

        /// <summary>圆弧方向——true=顺时针(CW)，false=逆时针(CCW)</summary>
        private bool _arcClockwise = true;
        /// <summary>圆弧方向——true=顺时针(CW)，false=逆时针(CCW)，默认顺时针</summary>
        public bool ArcClockwise { get => _arcClockwise; set => SetProperty(ref _arcClockwise, value); }

        /// <summary>圆弧插补速度（mm/s）——默认250</summary>
        private double _arcVelocity = 250;
        /// <summary>圆弧插补速度（mm/s）——传给MotionService.ArcInterpolation</summary>
        public double ArcVelocity { get => _arcVelocity; set => SetProperty(ref _arcVelocity, value); }

        /// <summary>圆弧插补加速度（mm/s²）——默认500</summary>
        private double _arcAcceleration = 500;
        /// <summary>圆弧插补加速度（mm/s²）——传给MotionService.ArcInterpolation</summary>
        public double ArcAcceleration { get => _arcAcceleration; set => SetProperty(ref _arcAcceleration, value); }

        /// <summary>圆弧插补减速度（mm/s²）——默认500</summary>
        private double _arcDeceleration = 500;
        /// <summary>圆弧插补减速度（mm/s²）——传给MotionService.ArcInterpolation</summary>
        public double ArcDeceleration { get => _arcDeceleration; set => SetProperty(ref _arcDeceleration, value); }

        #endregion

        #region 运动状态
        // 设备连接状态、轴位置、回原状态等，界面上的状态指示灯和坐标显示绑定到这里

        /// <summary>运控卡连接状态——ConnectCard后变true，DisconnectCard后变false</summary>
        private bool _isCardConnected;
        /// <summary>运控卡连接状态——影响IsDeviceReady的计算</summary>
        public bool IsCardConnected { get => _isCardConnected; set { if (SetProperty(ref _isCardConnected, value)) { OnPropertyChanged(nameof(IsDeviceReady)); RefreshAllCommands(); } } }

        /// <summary>
        /// 运动服务是否正在运动——直接转发_motionService.IsMoving，MainWindow绘制时用
        /// </summary>
        public bool IsMoving => _motionService.IsMoving;

        /// <summary>
        /// 运动轨迹记录——直接转发_motionService.TrajectoryTrail，MainWindow绘制轨迹时用
        /// </summary>
        public List<(double x, double y, bool glueOn)> TrajectoryTrail => _motionService.TrajectoryTrail;

        /// <summary>
        /// 获取轴状态——直接转发_motionService.GetAxisStatus，MainWindow绘制当前位置时用
        /// </summary>
        public AxisStatusInfo GetAxisStatus(short axis) => _motionService.GetAxisStatus(axis);

        /// <summary>相机连接状态——ConnectCamera后变true，DisconnectCamera后变false</summary>
        private bool _isCameraConnected;
        /// <summary>相机连接状态——影响IsDeviceReady的计算</summary>
        public bool IsCameraConnected { get => _isCameraConnected; set { if (SetProperty(ref _isCameraConnected, value)) { OnPropertyChanged(nameof(IsDeviceReady)); RefreshAllCommands(); } } }

        /// <summary>调试模式开关——开启后可绕过设备连接检查</summary>
        private bool _isDebugMode;
        /// <summary>
        /// 调试模式开关——不开运控卡和相机也能操作所有功能
        /// </summary>
        /// <remarks>
        /// 切换时同步更新IsDeviceReady属性和所有命令的可用状态（CommandManager.InvalidateRequerySuggested）
        /// 开发调试时很有用，不用每次都连硬件
        /// </remarks>
        public bool IsDebugMode
        {
            get => _isDebugMode;
            set
            {
                if (SetProperty(ref _isDebugMode, value))
                {
                    OnPropertyChanged(nameof(IsDeviceReady));
                    CommandManager.InvalidateRequerySuggested();
                    RefreshAllCommands();
                }
            }
        }

        /// <summary>
        /// 设备就绪状态——决定大部分按钮能不能点
        /// </summary>
        /// <remarks>
        /// 计算逻辑：IsDebugMode || (IsCardConnected && IsCameraConnected)
        /// 也就是说：调试模式下永远就绪，否则必须运控卡和相机都连上才行
        /// 绝大多数Command的CanExecute都检查这个属性
        /// </remarks>
        public bool IsDeviceReady => IsDebugMode || (IsCardConnected && IsCameraConnected);

        /// <summary>X轴当前位置（mm）——由OnMotionPositionUpdated回调实时更新</summary>
        private double _axisXPos;
        /// <summary>X轴当前位置（mm）——绑定到界面坐标显示</summary>
        public double AxisXPos { get => _axisXPos; set => SetProperty(ref _axisXPos, value); }

        /// <summary>Y轴当前位置（mm）——由OnMotionPositionUpdated回调实时更新</summary>
        private double _axisYPos;
        /// <summary>Y轴当前位置（mm）——绑定到界面坐标显示</summary>
        public double AxisYPos { get => _axisYPos; set => SetProperty(ref _axisYPos, value); }

        /// <summary>X轴已回原点标志——回原完成后由OnMotionPositionUpdated设置</summary>
        private bool _axisXHomed;
        /// <summary>X轴已回原点标志——界面上的回原状态指示</summary>
        public bool AxisXHomed { get => _axisXHomed; set => SetProperty(ref _axisXHomed, value); }

        /// <summary>Y轴已回原点标志——回原完成后由OnMotionPositionUpdated设置</summary>
        private bool _axisYHomed;
        /// <summary>Y轴已回原点标志——界面上的回原状态指示</summary>
        public bool AxisYHomed { get => _axisYHomed; set => SetProperty(ref _axisYHomed, value); }

        /// <summary>配方执行中标志</summary>
        private bool _isExecutingRecipe;
        /// <summary>
        /// 配方执行中标志——为true时禁止重复触发配方命令
        /// </summary>
        /// <remarks>
        /// 变更时刷新命令可用状态（CommandManager.InvalidateRequerySuggested）
        /// HomeCommand的CanExecute也检查这个：配方执行中只有急停时才能回原
        /// </remarks>
        public bool IsExecutingRecipe
        {
            get => _isExecutingRecipe;
            set
            {
                if (SetProperty(ref _isExecutingRecipe, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                    RefreshAllCommands();
                }
            }
        }

        /// <summary>胶阀输出开关——控制出胶/关胶</summary>
        private bool _glueOutputOn;
        /// <summary>
        /// 胶阀输出开关——界面上的"出胶开/出胶关"按钮绑定到这里
        /// </summary>
        /// <remarks>
        /// 【踩坑记录】单独出胶时胶阀气压不会仿真变化的问题：
        /// 旧代码只在setter中设置一次气压值，没有持续仿真波动，看起来气压不动。
        /// 修复：添加 _gluePressureTimer (500ms间隔) 持续随机波动气压(0.55~0.75 MPa)，
        /// 出胶时启动定时器，关胶时停止定时器并气压归零。
        /// </remarks>
        public bool GlueOutputOn
        {
            get => _glueOutputOn;
            set
            {
                if (SetProperty(ref _glueOutputOn, value))
                {
                    _motionService.SetGlueOutput(value);
                    _loggerService.LogInfo(value ? "出胶已开启" : "出胶已关闭");
                    OnPropertyChanged(nameof(GlueButtonText));
                    if (value)
                    {
                        var rnd = new Random();
                        GluePressure = 0.6 + rnd.NextDouble() * 0.1;
                        _gluePressureTimer?.Start();
                    }
                    else
                    {
                        GluePressure = 0;
                        _gluePressureTimer?.Stop();
                    }
                    OnPropertyChanged(nameof(GluePressureInfo));
                }
            }
        }

        /// <summary>胶阀按钮文本——根据出胶状态切换显示"🔴 出胶关"或"出胶开"</summary>
        public string GlueButtonText => _glueOutputOn ? "🔴 出胶关" : "出胶开";

        /// <summary>配方录制中标志——为true时JOG停止和插补运动会自动记录轨迹点</summary>
        private bool _isRecordingRecipe;
        /// <summary>配方录制中标志——NewRecipe设为true，SaveRecipe/CancelRecipe设为false</summary>
        public bool IsRecordingRecipe
        {
            get => _isRecordingRecipe;
            set { if (SetProperty(ref _isRecordingRecipe, value)) RefreshAllCommands(); }
        }

        /// <summary>
        /// 胶阀气压仿真定时器（500ms间隔）
        /// </summary>
        /// <remarks>
        /// 出胶时启动，每500ms随机波动气压值(0.55~0.75 MPa)模拟真实气压变化
        /// 关胶时停止，气压归零。这样气压显示看起来更真实
        /// </remarks>
        private DispatcherTimer _gluePressureTimer;

        /// <summary>急停激活状态——为true时所有运动停止，界面显示红色"急停"</summary>
        private bool _isEstopActive;
        /// <summary>
        /// 急停激活状态——第一次按急停按钮激活，第二次按解除
        /// </summary>
        /// <remarks>
        /// 变更时同步更新急停按钮文本（"急停"/"解除急停"）和命令可用状态
        /// </remarks>
        public bool IsEstopActive
        {
            get => _isEstopActive;
            set
            {
                if (SetProperty(ref _isEstopActive, value))
                {
                    OnPropertyChanged(nameof(EstopButtonText));
                    CommandManager.InvalidateRequerySuggested();
                    RefreshAllCommands();
                }
            }
        }

        /// <summary>急停按钮文本——急停中显示"解除急停"，正常显示"急停"</summary>
        public string EstopButtonText => _isEstopActive ? "解除急停" : "急停";

        /// <summary>起点已确认标志——配方录制时确认起点后变true</summary>
        private bool _startPointConfirmed;
        /// <summary>起点已确认标志——ConfirmStartPoint设为true，控制录制流程进度</summary>
        public bool StartPointConfirmed
        {
            get => _startPointConfirmed;
            set { if (SetProperty(ref _startPointConfirmed, value)) RefreshAllCommands(); }
        }

        /// <summary>起点X坐标（mm）——ConfirmStartPoint时记录当前轴位置</summary>
        private double _startPointX;
        /// <summary>起点X坐标（mm）——配方保存时写入DispensingRecipe.StartPointX</summary>
        public double StartPointX { get => _startPointX; set => SetProperty(ref _startPointX, value); }

        /// <summary>起点Y坐标（mm）——ConfirmStartPoint时记录当前轴位置</summary>
        private double _startPointY;
        /// <summary>起点Y坐标（mm）——配方保存时写入DispensingRecipe.StartPointY</summary>
        public double StartPointY { get => _startPointY; set => SetProperty(ref _startPointY, value); }

        /// <summary>终点已确认标志——配方录制时确认终点后变true</summary>
        private bool _endPointConfirmed;
        /// <summary>终点已确认标志——ConfirmEndPoint设为true，之后才能保存配方</summary>
        public bool EndPointConfirmed { get => _endPointConfirmed; set { if (SetProperty(ref _endPointConfirmed, value)) RefreshAllCommands(); } }

        #endregion

        #region 视觉结果
        // Mark检测的结果数据：像素坐标、物理坐标、匹配分数、偏差值等
        // 这些属性绑定到界面上的检测信息显示区域

        /// <summary>Mark1像素行坐标（Row）——Halcon坐标系，行=Y方向</summary>
        private double _mark1PixelRow;
        /// <summary>Mark1像素行坐标（Row）——模板匹配直接返回的像素坐标</summary>
        public double Mark1PixelRow { get => _mark1PixelRow; set => SetProperty(ref _mark1PixelRow, value); }

        /// <summary>Mark1像素列坐标（Col）——Halcon坐标系，列=X方向</summary>
        private double _mark1PixelCol;
        /// <summary>Mark1像素列坐标（Col）——模板匹配直接返回的像素坐标</summary>
        public double Mark1PixelCol { get => _mark1PixelCol; set => SetProperty(ref _mark1PixelCol, value); }

        /// <summary>Mark2像素行坐标（Row）</summary>
        private double _mark2PixelRow;
        /// <summary>Mark2像素行坐标（Row）</summary>
        public double Mark2PixelRow { get => _mark2PixelRow; set => SetProperty(ref _mark2PixelRow, value); }

        /// <summary>Mark2像素列坐标（Col）</summary>
        private double _mark2PixelCol;
        /// <summary>Mark2像素列坐标（Col）</summary>
        public double Mark2PixelCol { get => _mark2PixelCol; set => SetProperty(ref _mark2PixelCol, value); }

        /// <summary>Mark1物理坐标X（mm）——经仿射变换后的世界坐标，已做平滑滤波</summary>
        private double _mark1PhysX;
        /// <summary>Mark1物理坐标X（mm）——用于纠偏计算</summary>
        public double Mark1PhysX { get => _mark1PhysX; set => SetProperty(ref _mark1PhysX, value); }

        /// <summary>Mark1物理坐标Y（mm）——经仿射变换后的世界坐标，已做平滑滤波</summary>
        private double _mark1PhysY;
        /// <summary>Mark1物理坐标Y（mm）</summary>
        public double Mark1PhysY { get => _mark1PhysY; set => SetProperty(ref _mark1PhysY, value); }

        /// <summary>Mark2物理坐标X（mm）——经仿射变换后的世界坐标，已做平滑滤波</summary>
        private double _mark2PhysX;
        /// <summary>Mark2物理坐标X（mm）</summary>
        public double Mark2PhysX { get => _mark2PhysX; set => SetProperty(ref _mark2PhysX, value); }

        /// <summary>Mark2物理坐标Y（mm）——经仿射变换后的世界坐标，已做平滑滤波</summary>
        private double _mark2PhysY;
        /// <summary>Mark2物理坐标Y（mm）</summary>
        public double Mark2PhysY { get => _mark2PhysY; set => SetProperty(ref _mark2PhysY, value); }

        /// <summary>Mark1模板匹配分数（0~1）——越高越可信，低于MinScore(0.7)视为未匹配</summary>
        private double _score1;
        /// <summary>Mark1模板匹配分数（0~1）</summary>
        public double Score1 { get => _score1; set => SetProperty(ref _score1, value); }

        /// <summary>Mark2模板匹配分数（0~1）——越高越可信，低于MinScore(0.7)视为未匹配</summary>
        private double _score2;
        /// <summary>Mark2模板匹配分数（0~1）</summary>
        public double Score2 { get => _score2; set => SetProperty(ref _score2, value); }

        /// <summary>标准中心X坐标（mm）——首次检测到双Mark时记录，后续检测与之对比算偏差</summary>
        private double _stdCenterX;
        /// <summary>标准中心X坐标——"标准位置"就是第一个被检测到的工件位置</summary>
        public double StdCenterX { get => _stdCenterX; set => SetProperty(ref _stdCenterX, value); }

        /// <summary>标准中心Y坐标（mm）——首次检测到双Mark时记录</summary>
        private double _stdCenterY;
        /// <summary>标准中心Y坐标</summary>
        public double StdCenterY { get => _stdCenterY; set => SetProperty(ref _stdCenterY, value); }

        /// <summary>标准角度（rad）——首次检测到双Mark时记录，两Mark连线的角度</summary>
        private double _stdAngle;
        /// <summary>标准角度（rad）——用于计算角度偏差ΔAngle</summary>
        public double StdAngle { get => _stdAngle; set => SetProperty(ref _stdAngle, value); }

        /// <summary>当前中心X坐标（mm）——每次检测实时计算的双Mark中点</summary>
        private double _curCenterX;
        /// <summary>当前中心X坐标——CurCenterX - StdCenterX = ΔX</summary>
        public double CurCenterX { get => _curCenterX; set => SetProperty(ref _curCenterX, value); }

        /// <summary>当前中心Y坐标（mm）——每次检测实时计算的双Mark中点</summary>
        private double _curCenterY;
        /// <summary>当前中心Y坐标——CurCenterY - StdCenterY = ΔY</summary>
        public double CurCenterY { get => _curCenterY; set => SetProperty(ref _curCenterY, value); }

        /// <summary>当前角度（rad）——每次检测实时计算的两Mark连线角度</summary>
        private double _curAngle;
        /// <summary>当前角度（rad）——CurAngle - StdAngle = ΔAngle</summary>
        public double CurAngle { get => _curAngle; set => SetProperty(ref _curAngle, value); }

        /// <summary>X方向偏移量（mm）——当前与标准的差值，已做平滑滤波</summary>
        private double _deltaX;
        /// <summary>X方向偏移量（mm）——纠偏点胶时的X方向补偿量</summary>
        public double DeltaX { get => _deltaX; set => SetProperty(ref _deltaX, value); }

        /// <summary>Y方向偏移量（mm）——当前与标准的差值，已做平滑滤波</summary>
        private double _deltaY;
        /// <summary>Y方向偏移量（mm）——纠偏点胶时的Y方向补偿量</summary>
        public double DeltaY { get => _deltaY; set => SetProperty(ref _deltaY, value); }

        /// <summary>角度偏移量（rad）——当前与标准的差值，已做平滑滤波</summary>
        private double _deltaAngle;
        /// <summary>角度偏移量（rad）——纠偏点胶时的旋转补偿量</summary>
        public double DeltaAngle { get => _deltaAngle; set => SetProperty(ref _deltaAngle, value); }

        /// <summary>检测运行中标志——界面上的检测状态指示灯</summary>
        private bool _detectionRunning;
        /// <summary>检测运行中标志——StartDetection设true，StopDetection设false</summary>
        public bool DetectionRunning
        {
            get => _detectionRunning;
            set => SetProperty(ref _detectionRunning, value);
        }

        #endregion

        #region 检测状态显示
        // 界面状态栏的信息：状态文字、颜色、时间、各种格式化显示文本
        // 这些都是只读计算属性，根据其他属性值自动生成显示文本

        /// <summary>检测状态文本——显示当前设备运行阶段（未检测/待机中/检测中/点胶中/急停等）</summary>
        private string _detectionStatusText = "未检测";
        /// <summary>检测状态文本——由UpdateDeviceStatus()方法统一设置</summary>
        public string DetectionStatusText { get => _detectionStatusText; set => SetProperty(ref _detectionStatusText, value); }

        /// <summary>当前时间字符串——每秒更新一次</summary>
        private string _currentTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        /// <summary>当前时间字符串——由构造函数中的clockTimer每秒刷新</summary>
        public string CurrentTime { get => _currentTime; set => SetProperty(ref _currentTime, value); }

        /// <summary>检测状态颜色（十六进制ARGB）——不同状态显示不同颜色</summary>
        private string _detectionStatusColor = "#FF9E9E9E";
        /// <summary>
        /// 检测状态颜色——由UpdateDeviceStatus()方法统一设置
        /// </summary>
        /// <remarks>
        /// 颜色对照：红色#FFD32F2F=急停/异常，橙色#FFFF8F00=配方编辑/等待配方，
        /// 绿色#FF2E7D32=点胶中/检测中，灰色#FF9E9E9E=待机，蓝灰色#FF78909C=待检测
        /// </remarks>
        public string DetectionStatusColor { get => _detectionStatusColor; set => SetProperty(ref _detectionStatusColor, value); }

        /// <summary>Mark1模板创建状态文本——"✓ 已创建"或"✗ 未创建"</summary>
        public string Mark1TemplateStatus => _templateService.Mark1Created ? "✓ 已创建" : "✗ 未创建";
        /// <summary>Mark2模板创建状态文本——"✓ 已创建"或"✗ 未创建"</summary>
        public string Mark2TemplateStatus => _templateService.Mark2Created ? "✓ 已创建" : "✗ 未创建";
        /// <summary>Mark1匹配状态文本——匹配成功显示分数，否则"✗ 未匹配"</summary>
        public string Mark1MatchStatus => Score1 > 0 ? $"✓ 匹配成功 ({Score1:F2})" : "✗ 未匹配";
        /// <summary>Mark2匹配状态文本——匹配成功显示分数，否则"✗ 未匹配"</summary>
        public string Mark2MatchStatus => Score2 > 0 ? $"✓ 匹配成功 ({Score2:F2})" : "✗ 未匹配";
        /// <summary>Mark1像素坐标信息文本——格式"M1 R:xxx C:xxx"</summary>
        public string Mark1PixelInfo => Mark1PixelRow != 0 || Mark1PixelCol != 0 ? $"M1 R:{Mark1PixelRow:F1} C:{Mark1PixelCol:F1}" : "M1: --";
        /// <summary>Mark2像素坐标信息文本——格式"M2 R:xxx C:xxx"</summary>
        public string Mark2PixelInfo => Mark2PixelRow != 0 || Mark2PixelCol != 0 ? $"M2 R:{Mark2PixelRow:F1} C:{Mark2PixelCol:F1}" : "M2: --";
        /// <summary>Mark1物理坐标信息文本——格式"M1 X:xxx Y:xxx"</summary>
        public string Mark1PhysInfo => Mark1PhysX != 0 || Mark1PhysY != 0 ? $"M1 X:{Mark1PhysX:F2} Y:{Mark1PhysY:F2}" : "M1: --";
        /// <summary>Mark2物理坐标信息文本——格式"M2 X:xxx Y:xxx"</summary>
        public string Mark2PhysInfo => Mark2PhysX != 0 || Mark2PhysY != 0 ? $"M2 X:{Mark2PhysX:F2} Y:{Mark2PhysY:F2}" : "M2: --";
        /// <summary>X偏移量信息文本——格式"ΔX: x.xxx mm"</summary>
        public string DeltaXInfo => DeltaX != 0 ? $"ΔX: {DeltaX:F3} mm" : "ΔX: --";
        /// <summary>Y偏移量信息文本——格式"ΔY: x.xxx mm"</summary>
        public string DeltaYInfo => DeltaY != 0 ? $"ΔY: {DeltaY:F3} mm" : "ΔY: --";
        /// <summary>角度偏移量信息文本——同时显示弧度和角度，格式"Δθ: x.xxxx rad (xx.xx°)"</summary>
        public string DeltaAngleInfo => DeltaAngle != 0 ? $"Δθ: {DeltaAngle:F4} rad ({DeltaAngle * 180 / Math.PI:F2}°)" : "Δθ: --";
        /// <summary>标定状态文本——"✓ 已标定"或"✗ 未标定"</summary>
        public string CalibrationStatus => _calibData?.IsCalibrated == true ? "✓ 已标定" : "✗ 未标定";
        /// <summary>Mark1匹配分数信息文本——格式"M1匹配: x.xxx"</summary>
        public string Score1Info => Score1 > 0 ? $"M1匹配: {Score1:F3}" : "M1匹配: --";
        /// <summary>Mark2匹配分数信息文本——格式"M2匹配: x.xxx"</summary>
        public string Score2Info => Score2 > 0 ? $"M2匹配: {Score2:F3}" : "M2匹配: --";

        /// <summary>胶阀气压值（MPa）——出胶时0.55~0.75波动，关胶时为0</summary>
        private double _gluePressure;
        /// <summary>胶阀气压值（MPa）——由_gluePressureTimer持续更新</summary>
        public double GluePressure { get => _gluePressure; set => SetProperty(ref _gluePressure, value); }
        /// <summary>
        /// 胶阀气压信息文本——格式"0.xx MPa"或"0 MPa"
        /// </summary>
        /// <remarks>
        /// 【踩坑记录】这里用GluePressure > 0判断出胶状态，而不是用_isDispensing
        /// 原因：GluePressure是实际气压值，能更准确反映当前出胶状态
        /// _isDispensing是逻辑标志，可能跟实际状态不同步
        /// </remarks>
        public string GluePressureInfo => GluePressure > 0 ? $"{GluePressure:F2} MPa" : "0 MPa";

        /// <summary>
        /// 刷新所有状态显示属性——手动触发所有计算属性的PropertyChanged通知
        /// </summary>
        /// <remarks>
        /// 因为Mark1TemplateStatus、DeltaXInfo这些是只读计算属性（=> 表达式），
        /// 它们依赖的属性变化时不会自动触发通知，所以需要手动调OnPropertyChanged
        /// 调用场景：模板创建后、检测完成后、设备状态变更后
        /// </remarks>
        private void RefreshStatusDisplay()
        {
            OnPropertyChanged(nameof(Mark1TemplateStatus));
            OnPropertyChanged(nameof(Mark2TemplateStatus));
            OnPropertyChanged(nameof(Mark1MatchStatus));
            OnPropertyChanged(nameof(Mark2MatchStatus));
            OnPropertyChanged(nameof(Mark1PixelInfo));
            OnPropertyChanged(nameof(Mark2PixelInfo));
            OnPropertyChanged(nameof(Mark1PhysInfo));
            OnPropertyChanged(nameof(Mark2PhysInfo));
            OnPropertyChanged(nameof(DeltaXInfo));
            OnPropertyChanged(nameof(DeltaYInfo));
            OnPropertyChanged(nameof(DeltaAngleInfo));
            OnPropertyChanged(nameof(CalibrationStatus));
            OnPropertyChanged(nameof(GluePressureInfo));
        }

        /// <summary>
        /// 更新设备状态显示（状态文字和颜色）——按优先级链依次判断
        /// </summary>
        /// <remarks>
        /// 优先级从高到低：急停 > 配方编辑 > 点胶中 > 等待配方 > 待机 > 待检测 > 检测中
        /// 每个状态对应不同的文字和颜色：
        /// - 急停：红色 #FFD32F2F（最高优先级，任何状态下急停都覆盖）
        /// - 配方编辑/等待配方：橙色 #FFFF8F00（录制配方时的中间状态）
        /// - 点胶中/检测中：绿色 #FF2E7D32（正常运行状态）
        /// - 待机：灰色 #FF9E9E9E（运控卡或相机未连接）
        /// - 待检测：蓝灰色 #FF78909C（已连接但未开始检测）
        /// </remarks>
        private void UpdateDeviceStatus()
        {
            // 急停最高优先级
            if (_isEstopActive)
            {
                DetectionStatusText = "急停";
                DetectionStatusColor = "#FFD32F2F";
                return;
            }

            // 配方编辑状态
            if (IsRecordingRecipe)
            {
                if (!StartPointConfirmed)
                {
                    DetectionStatusText = "待确认起点";
                    DetectionStatusColor = "#FFFF8F00";
                }
                else if (!EndPointConfirmed)
                {
                    DetectionStatusText = "待确认终点";
                    DetectionStatusColor = "#FFFF8F00";
                }
                else
                {
                    DetectionStatusText = "配方待保存";
                    DetectionStatusColor = "#FFFF8F00";
                }
                return;
            }

            // 点胶中
            if (_isDispensing)
            {
                DetectionStatusText = "点胶中";
                DetectionStatusColor = "#FF2E7D32"; // 绿色
                return;
            }

            // 等待配方创建
            if (_waitingForRecipe)
            {
                DetectionStatusText = "等待配方创建";
                DetectionStatusColor = "#FFFF8F00";
                return;
            }

            // 未连接运控卡或相机 → 待机
            if (!IsCardConnected || !IsCameraConnected)
            {
                DetectionStatusText = "待机中";
                DetectionStatusColor = "#FF9E9E9E";
                return;
            }

            // 已连接运控卡和相机但未开始检测 → 待检测
            if (!_detectionEnabled)
            {
                DetectionStatusText = "待检测";
                DetectionStatusColor = "#FF78909C";
                return;
            }

            // 检测中
            DetectionStatusText = "检测中";
            DetectionStatusColor = "#FF2E7D32";
        }

        /// <summary>
        /// 手动刷新所有有CanExecute的命令的可用状态。
        /// CommunityToolkit.Mvvm的RelayCommand默认不监听WPF的CommandManager自动刷新，
        /// 所以需要在关键属性变化时手动调用NotifyCanExecuteChanged()来更新按钮状态。
        /// </summary>
        private void RefreshAllCommands()
        {
            // 注意：构造函数中LoadRecipesFromDisk()会在命令初始化之前调用RefreshAllCommands()，
            // 所以这里必须做null检查，否则会报NullReferenceException
            DisconnectCameraCommand?.NotifyCanExecuteChanged();
            StartLiveDisplayCommand?.NotifyCanExecuteChanged();
            StopLiveDisplayCommand?.NotifyCanExecuteChanged();
            ApplyExposureGainCommand?.NotifyCanExecuteChanged();
            DrawMark1Command?.NotifyCanExecuteChanged();
            DrawMark2Command?.NotifyCanExecuteChanged();
            SaveTemplateCommand?.NotifyCanExecuteChanged();
            LoadTemplateCommand?.NotifyCanExecuteChanged();
            DeleteTemplateCommand?.NotifyCanExecuteChanged();
            StartDetectionCommand?.NotifyCanExecuteChanged();
            StopDetectionCommand?.NotifyCanExecuteChanged();
            OpenCalibrationCommand?.NotifyCanExecuteChanged();
            DisconnectCardCommand?.NotifyCanExecuteChanged();
            HomeCommand?.NotifyCanExecuteChanged();
            EmergencyStopCommand?.NotifyCanExecuteChanged();
            DeviceResetCommand?.NotifyCanExecuteChanged();
            LinearInterpCommand?.NotifyCanExecuteChanged();
            ArcInterpCommand?.NotifyCanExecuteChanged();
            NewRecipeCommand?.NotifyCanExecuteChanged();
            DeleteRecipeCommand?.NotifyCanExecuteChanged();
            ClearTrailCommand?.NotifyCanExecuteChanged();
            LoadRecipeCommand?.NotifyCanExecuteChanged();
            CancelRecipeCommand?.NotifyCanExecuteChanged();
            ConfirmStartPointCommand?.NotifyCanExecuteChanged();
            ConfirmEndPointCommand?.NotifyCanExecuteChanged();
            SaveRecipeCommand?.NotifyCanExecuteChanged();
        }

        #endregion

        #region 配方管理
        // 配方列表和当前配方轨迹点，绑定到界面上的配方列表和轨迹预览

        /// <summary>配方列表——所有已保存的点胶配方，启动时从磁盘加载</summary>
        public ObservableCollection<DispensingRecipe> Recipes { get; } = new();
        /// <summary>当前配方的轨迹点集合——用于显示和编辑配方路径，绑定到轨迹预览控件</summary>
        public ObservableCollection<TrajectoryPoint> CurrentRecipePoints { get; } = new();

        /// <summary>配方列表选中索引——界面上的配方列表选中项</summary>
        private int _selectedRecipeIndex = -1;
        /// <summary>配方列表选中索引——-1表示未选中</summary>
        public int SelectedRecipeIndex
        {
            get => _selectedRecipeIndex;
            set
            {
                if (SetProperty(ref _selectedRecipeIndex, value))
                    RefreshAllCommands(); // 选中配方变化时刷新DeleteRecipeCommand等
            }
        }

        #endregion

        #region 日志

        /// <summary>日志条目集合——直接引用LoggerService的Logs，绑定到UI日志列表</summary>
        public ObservableCollection<LogEntry> Logs => _loggerService.Logs;

        #endregion

        #region 事件
        // 这些事件是ViewModel→View的通信通道，View层订阅后做界面操作（如进入ROI绘制模式）

        /// <summary>轨迹更新事件——配方轨迹点变更时通知View重绘轨迹预览</summary>
        public event Action TrajectoryUpdated;
        /// <summary>配方轨迹尾迹更新事件——配方执行过程中实时更新尾迹显示</summary>
        public event Action RecipeTrailUpdated;
        /// <summary>请求绘制Mark1区域事件——通知View进入ROI矩形绘制模式</summary>
        public event EventHandler RequestDrawMark1;
        /// <summary>请求绘制Mark2区域事件——通知View进入ROI矩形绘制模式</summary>
        public event EventHandler RequestDrawMark2;
        /// <summary>首帧图像接收事件——传递图像尺寸信息，View据此做HalconWindow窗口适配</summary>
        public event EventHandler<ImageSizeEventArgs> FirstImageReceived;

        #endregion

        #region CurrentImage

        /// <summary>当前显示图像——绑定到HalconWindow，每次检测更新</summary>
        public HObject CurrentImage
        {
            get => _currentImage;
            set => SetProperty(ref _currentImage, value);
        }

        /// <summary>标定数据只读访问——供外部（如标定窗口）读取当前标定数据</summary>
        public CalibrationData CalibData => _calibData;

        #endregion

        /// <summary>
        /// 构造函数——通过DI容器注入所有10个服务依赖
        /// </summary>
        /// <param name="eventAggregator">事件聚合器——模块间松耦合通信</param>
        /// <param name="loggerService">日志服务——记录运行日志</param>
        /// <param name="configService">配置服务——管理系统配置参数</param>
        /// <param name="cameraService">相机服务——工业相机连接和采集</param>
        /// <param name="calibrationService">标定服务——九点标定数据管理</param>
        /// <param name="distortionService">畸变校正服务——镜头畸变补偿</param>
        /// <param name="templateService">模板管理服务——双Mark模板的创建/保存/加载</param>
        /// <param name="markDetection">Mark检测服务——基于模板的形状匹配</param>
        /// <param name="coordTransform">坐标变换服务——像素→物理坐标变换</param>
        /// <param name="motionService">运动控制服务——JOG/插补/回原点等</param>
        /// <remarks>
        /// DI构造函数注入模式：所有依赖通过参数传入，不在构造函数内手动new
        /// 优点：
        /// 1. 依赖关系显式声明，看参数列表就知道这个类依赖什么
        /// 2. 切换实现只需修改DI注册代码（App.xaml.cs），不用改这里
        /// 3. 单元测试方便，可以传入Mock对象
        ///
        /// 构造函数里做的事：
        /// 1. 初始化两个DispatcherTimer（时钟、气压仿真）
        /// 2. 保存DI注入的服务实例到字段
        /// 3. 订阅运动服务和相机服务的事件
        /// 4. 初始化检测定时器
        /// 5. 加载标定数据（三级回退策略）
        /// 6. 从磁盘加载配方列表
        /// </remarks>
        public MainViewModel(
            ILoggerService loggerService,
            IConfigService configService,
            ICameraService cameraService,
            ICalibrationService calibrationService,
            IDistortionCorrectionService distortionService,
            ITemplateManagerService templateService,
            IMarkDetectionService markDetection,
            ICoordinateTransformService coordTransform,
            IMotionService motionService)
        {
            _baseDir = AppDomain.CurrentDomain.BaseDirectory;

            // 时间更新定时器，每秒刷新界面时间显示
            var clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            clockTimer.Tick += (s, e) => CurrentTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            clockTimer.Start();

            // 胶阀气压仿真定时器，出胶时每500ms随机波动气压(0.55~0.75 MPa)
            _gluePressureTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _gluePressureTimer.Tick += (s, e) =>
            {
                if (_glueOutputOn)
                {
                    var rnd = new Random();
                    GluePressure = 0.55 + rnd.NextDouble() * 0.2;
                    OnPropertyChanged(nameof(GluePressureInfo));
                }
            };

            // 通过DI注入的服务实例
            _loggerService = loggerService;
            _configService = configService;
            _cameraService = cameraService;
            _calibrationService = calibrationService;
            _distortionService = distortionService;
            _templateService = templateService;
            _markDetection = markDetection;
            _coordTransform = coordTransform;
            _motionService = motionService;

            // 订阅运动服务和相机服务的事件
            _motionService.PositionUpdated += OnMotionPositionUpdated;
            _motionService.RecipeExecutionCompleted += OnRecipeExecutionCompleted;
            _motionService.GlueStateChanged += OnGlueStateChanged;
            _cameraService.CameraError += OnCameraError;
            _cameraService.ImageGrabbed += OnImageGrabbed;

            // 检测定时器，200ms间隔周期性触发Mark检测
            _detectionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _detectionTimer.Tick += OnDetectionTimerTick;

            // 加载标定数据：优先加载"标定1"，其次从磁盘加载，最后使用默认HDevelop导出数据
            _calibData = LoadCalibrationByName("标定1");
            if (_calibData == null)
            {
                _calibData = LoadCalibrationFromDisk();
            }
            if (_calibData == null)
            {
                _calibData = CalibrationData.CreateFromHDevelopExport();
            }
            if (_calibData.HomMat2D[0] != 0 || _calibData.HomMat2D[4] != 0 || Math.Abs(_calibData.HomMat2D[1]) < 0.02)
            {
                _calibData = CalibrationData.CreateFromHDevelopExport();
                _loggerService.LogInfo("标定矩阵已重置为默认矩阵（15mm间距）");
            }

            LoadRecipesFromDisk();

            #region 命令初始化
            // 没有CanExecute的命令（永远可用）
            ConnectCameraCommand = new RelayCommand(ConnectCamera);
            ConnectCardCommand = new RelayCommand(ConnectCard);
            DebugModeCommand = new RelayCommand(ToggleDebugMode);
            ExportLogsCommand = new RelayCommand(ExportLogs);

            // 有CanExecute的命令——类型声明为RelayCommand（不是ICommand），
            // 因为需要调用NotifyCanExecuteChanged()手动刷新按钮状态
            DisconnectCameraCommand = new RelayCommand(DisconnectCamera, () => IsCameraConnected);
            StartLiveDisplayCommand = new RelayCommand(StartLiveDisplay, () => IsDeviceReady);
            StopLiveDisplayCommand = new RelayCommand(StopLiveDisplay, () => IsDeviceReady);
            ApplyExposureGainCommand = new RelayCommand(ApplyExposureGain, () => IsDeviceReady);
            DrawMark1Command = new RelayCommand(DrawMark1, () => IsDeviceReady);
            DrawMark2Command = new RelayCommand(DrawMark2, () => IsDeviceReady && _templateService.Mark1Created);
            SaveTemplateCommand = new RelayCommand(SaveTemplate, () => IsDeviceReady);
            LoadTemplateCommand = new RelayCommand(LoadTemplate, () => IsDeviceReady);
            DeleteTemplateCommand = new RelayCommand(DeleteTemplate, () => IsDeviceReady);
            StartDetectionCommand = new RelayCommand(StartDetection, () => IsDeviceReady && _templateService.TemplateLoaded);
            StopDetectionCommand = new RelayCommand(StopDetection, () => IsDeviceReady);
            OpenCalibrationCommand = new RelayCommand(OpenCalibration, () => IsDeviceReady);
            DisconnectCardCommand = new RelayCommand(DisconnectCard, () => IsCardConnected);
            HomeCommand = new RelayCommand(Home, () => IsDeviceReady && (!IsExecutingRecipe || IsEstopActive));
            EmergencyStopCommand = new RelayCommand(EmergencyStop, () => IsCardConnected || IsDebugMode);
            DeviceResetCommand = new RelayCommand(DeviceReset, () => IsCardConnected || IsCameraConnected);
            LinearInterpCommand = new RelayCommand(DoLinearInterp, () => IsDeviceReady);
            ArcInterpCommand = new RelayCommand(DoArcInterp, () => IsDeviceReady);
            NewRecipeCommand = new RelayCommand(NewRecipe, () => IsDeviceReady);
            DeleteRecipeCommand = new RelayCommand(DeleteRecipe, () => IsDeviceReady && SelectedRecipeIndex >= 0);
            ClearTrailCommand = new RelayCommand(ClearTrail, () => IsDeviceReady);
            LoadRecipeCommand = new RelayCommand(LoadRecipeDialog, () => IsDeviceReady && Recipes.Count > 0);
            CancelRecipeCommand = new RelayCommand(CancelRecipe, () => IsRecordingRecipe);
            ConfirmStartPointCommand = new RelayCommand(ConfirmStartPoint, () => IsRecordingRecipe && !StartPointConfirmed);
            ConfirmEndPointCommand = new RelayCommand(ConfirmEndPoint, () => IsRecordingRecipe && StartPointConfirmed && !EndPointConfirmed);
            SaveRecipeCommand = new RelayCommand(SaveRecipe, () => IsRecordingRecipe && StartPointConfirmed && EndPointConfirmed);
            #endregion
        }

        #region 相机命令

        /// <summary>连接相机命令——CanExecute始终为true，任何时候都能点</summary>
        public ICommand ConnectCameraCommand { get; }
        /// <summary>断开相机命令——CanExecute=IsCameraConnected，相机没连就不能断</summary>
        public RelayCommand DisconnectCameraCommand { get; }
        /// <summary>开启实时预览命令——CanExecute=IsDeviceReady</summary>
        public RelayCommand StartLiveDisplayCommand { get; }
        /// <summary>停止实时预览命令——CanExecute=IsDeviceReady</summary>
        public RelayCommand StopLiveDisplayCommand { get; }
        /// <summary>应用曝光增益参数命令——CanExecute=IsDeviceReady</summary>
        public RelayCommand ApplyExposureGainCommand { get; }

        /// <summary>
        /// 连接相机——使用当前配置的序列号、曝光时间和增益参数
        /// </summary>
        /// <remarks>
        /// 参数来源：CameraSerialNumber、ExposureTime、Gain、AutoExposure、AutoGain
        /// 连接后更新IsCameraConnected和IsDeviceReady，刷新状态显示
        /// </remarks>
        private void ConnectCamera()
        {
            var param = new CameraParams
            {
                CameraSerialNumber = CameraSerialNumber,
                ExposureTime = ExposureTime,
                Gain = Gain,
                AutoExposure = AutoExposure,
                AutoGain = AutoGain
            };
            var result = _cameraService.ConnectCamera(param);
            IsCameraConnected = _cameraService.IsConnected;
            OnPropertyChanged(nameof(IsDeviceReady));
            UpdateDeviceStatus();
            _loggerService.LogInfo(result);
        }

        /// <summary>
        /// 断开相机连接——安全停机
        /// </summary>
        /// <remarks>
        /// 断开前先停止检测定时器，防止相机断开后检测逻辑异常
        /// 会弹窗提示用户相机已断开
        /// </remarks>
        private void DisconnectCamera()
        {
            // 安全停机：停止检测
            if (_detectionEnabled)
            {
                _detectionTimer.Stop();
                _detectionEnabled = false;
                DetectionRunning = false;
            }
            _cameraService.DisconnectCamera();
            IsCameraConnected = false;
            OnPropertyChanged(nameof(IsDeviceReady));
            UpdateDeviceStatus();
            _loggerService.LogWarning("【异常】相机已断开，检测已停止");
            System.Windows.MessageBox.Show("相机已断开！\n检测已停止，请检查连接后重新连接。", "设备异常", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        /// <summary>开启实时预览——重置首帧标志以触发窗口适配</summary>
        private void StartLiveDisplay()
        {
            if (!_cameraService.IsConnected)
            {
                _loggerService.LogError("请先连接相机");
                return;
            }
            _isFirstImage = true;
            _cameraService.StartLiveDisplay();
            _loggerService.LogInfo("实时预览已开启");
        }

        /// <summary>停止实时预览</summary>
        private void StopLiveDisplay()
        {
            _cameraService.StopLiveDisplay();
            _loggerService.LogInfo("实时预览已关闭");
        }

        /// <summary>应用当前曝光时间和增益参数到相机——对应界面"应用参数"按钮</summary>
        private void ApplyExposureGain()
        {
            if (!_cameraService.IsConnected)
            {
                _loggerService.LogError("请先连接相机");
                return;
            }
            _cameraService.SetExposureGain(ExposureTime, Gain, AutoExposure, AutoGain);
            _loggerService.LogInfo($"曝光={ExposureTime}μs, 增益={Gain}, 自动曝光={AutoExposure}, 自动增益={AutoGain}");
        }

        #endregion

        #region 模板命令

        /// <summary>绘制Mark1区域命令——CanExecute=IsDeviceReady</summary>
        public RelayCommand DrawMark1Command { get; }
        /// <summary>绘制Mark2区域命令——CanExecute=IsDeviceReady &amp;&amp; Mark1已创建</summary>
        public RelayCommand DrawMark2Command { get; }
        /// <summary>保存模板命令——CanExecute=IsDeviceReady</summary>
        public RelayCommand SaveTemplateCommand { get; }
        /// <summary>加载模板命令——CanExecute=IsDeviceReady</summary>
        public RelayCommand LoadTemplateCommand { get; }

        /// <summary>
        /// 绘制Mark1 ROI区域——通知View进入矩形绘制模式
        /// </summary>
        /// <remarks>
        /// 前置条件：相机已连接 &amp;&amp; 当前有图像（实时预览已开启）
        /// 通过RequestDrawMark1事件通知View层，View层处理鼠标绘制ROI矩形
        /// </remarks>
        private void DrawMark1()
        {
            if (!_cameraService.IsConnected)
            {
                _loggerService.LogError("请先连接相机");
                return;
            }
            if (_cameraService.CurrentImage == null)
            {
                _loggerService.LogError("请先开启实时预览，确保画面有图像");
                return;
            }
            _loggerService.LogInfo("请在图像窗口绘制Mark1矩形区域");
            RequestDrawMark1?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 绘制Mark2 ROI区域——前置条件：相机已连接 &amp;&amp; Mark1已创建 &amp;&amp; 当前有图像
        /// </summary>
        private void DrawMark2()
        {
            if (!_cameraService.IsConnected)
            {
                _loggerService.LogError("请先连接相机");
                return;
            }
            if (!_templateService.Mark1Created)
            {
                _loggerService.LogError("请先绘制Mark1");
                return;
            }
            if (_cameraService.CurrentImage == null)
            {
                _loggerService.LogError("请先开启实时预览，确保画面有图像");
                return;
            }
            _loggerService.LogInfo("请在图像窗口绘制Mark2矩形区域");
            RequestDrawMark2?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 设置Mark1区域并创建模板——由View层ROI绘制完成后调用
        /// </summary>
        /// <param name="row1">ROI起始行（像素坐标）</param>
        /// <param name="col1">ROI起始列（像素坐标）</param>
        /// <param name="row2">ROI结束行（像素坐标）</param>
        /// <param name="col2">ROI结束列（像素坐标）</param>
        /// <remarks>
        /// 彩色图像会先转为灰度图再创建模板（Halcon形状模板要求灰度图）
        /// 创建成功后刷新状态显示
        /// </remarks>
        public void SetMark1Region(double row1, double col1, double row2, double col2)
        {
            try
            {
                var currentImg = _cameraService.CurrentImage as HObject;
                if (currentImg == null || !currentImg.IsInitialized())
                {
                    _loggerService.LogError("当前图像无效，无法创建Mark1模板");
                    return;
                }

                HObject grayImage = null;
                try
                {
                    HOperatorSet.CountChannels(currentImg, out HTuple channels);
                    if (channels.I == 3)
                        HOperatorSet.Rgb1ToGray(currentImg, out grayImage);
                    else
                        grayImage = currentImg.Clone();

                    _templateService.CreateMarkTemplate(1, grayImage, row1, col1, row2, col2);
                    _loggerService.LogInfo($"Mark1模板创建成功: 中心({_templateService.RefMark1Row:F1},{_templateService.RefMark1Col:F1})");
                    RefreshStatusDisplay();
                    RefreshAllCommands(); // Mark1创建后刷新DrawMark2Command等
                }
                finally
                {
                    grayImage?.Dispose();
                }
            }
            catch (Exception ex)
            {
                _loggerService.LogError($"Mark1模板创建失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 设置Mark2区域并创建模板——由View层ROI绘制完成后调用
        /// </summary>
        /// <param name="row1">ROI起始行（像素坐标）</param>
        /// <param name="col1">ROI起始列（像素坐标）</param>
        /// <param name="row2">ROI结束行（像素坐标）</param>
        /// <param name="col2">ROI结束列（像素坐标）</param>
        /// <remarks>彩色图像会先转为灰度图再创建模板</remarks>
        public void SetMark2Region(double row1, double col1, double row2, double col2)
        {
            try
            {
                var currentImg = _cameraService.CurrentImage as HObject;
                if (currentImg == null || !currentImg.IsInitialized())
                {
                    _loggerService.LogError("当前图像无效，无法创建Mark2模板");
                    return;
                }

                HObject grayImage = null;
                try
                {
                    HOperatorSet.CountChannels(currentImg, out HTuple channels);
                    if (channels.I == 3)
                        HOperatorSet.Rgb1ToGray(currentImg, out grayImage);
                    else
                        grayImage = currentImg.Clone();

                    _templateService.CreateMarkTemplate(2, grayImage, row1, col1, row2, col2);
                    _loggerService.LogInfo($"Mark2模板创建成功: 中心({_templateService.RefMark2Row:F1},{_templateService.RefMark2Col:F1})");
                    RefreshStatusDisplay();
                    RefreshAllCommands(); // Mark2创建后刷新StartDetectionCommand等
                }
                finally
                {
                    grayImage?.Dispose();
                }
            }
            catch (Exception ex)
            {
                _loggerService.LogError($"Mark2模板创建失败: {ex.Message}");
            }
        }

        /// <summary>保存当前双Mark模板到磁盘——弹出输入框让用户命名，保存到Templates/目录</summary>
        private void SaveTemplate()
        {
            try
            {
                if (!_templateService.TemplateLoaded)
                {
                    _loggerService.LogError("双Mark模板未完整创建，请先绘制Mark1和Mark2");
                    return;
                }

                string name = Microsoft.VisualBasic.Interaction.InputBox(
                    "请输入模板名称：", "保存模板", $"模板{DateTime.Now:yyyyMMdd_HHmmss}");

                if (string.IsNullOrWhiteSpace(name)) return;

                var dir = Path.Combine(_baseDir, "Templates", name);
                _templateService.SaveTemplate(dir);
                _loggerService.LogInfo($"双Mark模板 '{name}' 已保存");
            }
            catch (Exception ex)
            {
                _loggerService.LogError($"保存模板失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 加载模板——弹出模板选择窗口，同时尝试加载关联配方
        /// </summary>
        /// <remarks>
        /// 配方加载优先级：
        /// 1. 模板目录下的recipe.json（模板和配方放一起，最优先）
        /// 2. Recipes列表中按名称前缀匹配（兼容旧数据）
        /// 3. 无关联配方——提示用户新建
        ///
        /// 加载后还会重建模板的ROI区域（RebuildRegions），确保检测时能正确匹配
        /// </remarks>
        private void LoadTemplate()
        {
            try
            {
                var templatesDir = Path.Combine(_baseDir, "Templates");
                var dialog = new TemplateSelectionWindow(templatesDir);
                dialog.Owner = Application.Current.MainWindow;

                if (dialog.ShowDialog() != true) return;

                if (dialog.DeleteRequested)
                {
                    string templateDir = dialog.SelectedTemplatePath;
                    string templateName = Path.GetFileName(templateDir);
                    Directory.Delete(templateDir, true);
                    _loggerService.LogInfo($"模板 '{templateName}' 已删除");
                    return;
                }

                string templateDir2 = dialog.SelectedTemplatePath;

                if (!File.Exists(Path.Combine(templateDir2, "MarkModel1.shm")))
                {
                    _loggerService.LogError("模板文件不完整");
                    return;
                }

                _templateService.LoadTemplate(templateDir2);
                RefreshAllCommands(); // 模板加载后刷新StartDetectionCommand等

                _currentTemplateName = Path.GetFileName(templateDir2);

                // 先尝试从模板目录加载关联配方
                var recipeFile = Path.Combine(templateDir2, "recipe.json");
                bool recipeLoaded = false;
                if (File.Exists(recipeFile))
                {
                    try
                    {
                        var recipeJson = File.ReadAllText(recipeFile);
                        var loadedRecipe = JsonSerializer.Deserialize<DispensingRecipe>(recipeJson);
                        if (loadedRecipe != null)
                        {
                            loadedRecipe.TemplateName = _currentTemplateName;
                            CurrentRecipePoints.Clear();
                            foreach (var pt in loadedRecipe.Points)
                                CurrentRecipePoints.Add(pt);
                            StartPointX = loadedRecipe.StartPointX;
                            StartPointY = loadedRecipe.StartPointY;
                            _boundRecipe = loadedRecipe;
                            recipeLoaded = true;
                            _loggerService.LogInfo($"已自动加载关联配方: {loadedRecipe.Name}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _loggerService.LogError($"加载关联配方失败: {ex.Message}");
                    }
                }

                // 如果模板目录没有配方，按名称前缀从Recipes列表中查找
                if (!recipeLoaded)
                {
                    var matchedRecipe = Recipes.FirstOrDefault(r => r.Name.StartsWith(_currentTemplateName));
                    if (matchedRecipe != null)
                    {
                        matchedRecipe.TemplateName = _currentTemplateName;
                        CurrentRecipePoints.Clear();
                        foreach (var pt in matchedRecipe.Points)
                            CurrentRecipePoints.Add(pt);
                        StartPointX = matchedRecipe.StartPointX;
                        StartPointY = matchedRecipe.StartPointY;
                        _boundRecipe = matchedRecipe;
                        recipeLoaded = true;
                        _loggerService.LogInfo($"已按前缀匹配配方: {matchedRecipe.Name}");
                    }
                }

                if (!recipeLoaded)
                {
                    _boundRecipe = null;
                    CurrentRecipePoints.Clear();
                    _loggerService.LogInfo($"模板 '{_currentTemplateName}' 没有关联配方，请新建配方");
                }

                if (_cameraService.CurrentImage is HObject img && img.IsInitialized())
                {
                    HObject grayImg = null;
                    try
                    {
                        HOperatorSet.CountChannels(img, out HTuple ch);
                        if (ch.I == 3)
                            HOperatorSet.Rgb1ToGray(img, out grayImg);
                        else
                            grayImg = img.Clone();
                        _templateService.RebuildRegions(grayImg);
                    }
                    finally
                    {
                        grayImg?.Dispose();
                    }
                }

                _loggerService.LogInfo($"双Mark模板 '{Path.GetFileName(templateDir2)}' 已加载");
            }
            catch (Exception ex)
            {
                _loggerService.LogError($"加载模板失败: {ex.Message}");
            }
        }

        /// <summary>删除模板命令——CanExecute=IsDeviceReady</summary>
        public RelayCommand DeleteTemplateCommand { get; }

        /// <summary>删除模板——弹出模板选择窗口让用户选择要删除的模板，直接删除整个目录</summary>
        private void DeleteTemplate()
        {
            try
            {
                var templatesDir = Path.Combine(_baseDir, "Templates");
                var dialog = new TemplateSelectionWindow(templatesDir);
                dialog.Owner = Application.Current.MainWindow;

                if (dialog.ShowDialog() != true) return;

                if (dialog.DeleteRequested)
                {
                    string templateDir = dialog.SelectedTemplatePath;
                    string templateName = Path.GetFileName(templateDir);
                    Directory.Delete(templateDir, true);
                    _loggerService.LogInfo($"模板 '{templateName}' 已删除");
                    RefreshAllCommands(); // 模板删除后刷新命令状态
                }
                else
                {
                    _loggerService.LogInfo("请使用删除按钮删除模板");
                }
            }
            catch (Exception ex)
            {
                _loggerService.LogError($"删除模板失败: {ex.Message}");
            }
        }

        #endregion

        #region 检测命令

        /// <summary>开始检测命令——CanExecute=IsDeviceReady &amp;&amp; 模板已加载</summary>
        public RelayCommand StartDetectionCommand { get; }
        /// <summary>停止检测命令——CanExecute=IsDeviceReady</summary>
        public RelayCommand StopDetectionCommand { get; }

        /// <summary>
        /// 开始检测——启动检测定时器，开始周期性Mark检测
        /// </summary>
        /// <remarks>
        /// 前置条件：相机已连接 &amp;&amp; 双Mark模板已创建
        /// 启动后：_detectionEnabled=true，_detectionTimer开始每200ms触发一次
        /// 同时重置检测计数和平滑滤波状态
        /// </remarks>
        private void StartDetection()
        {
            if (!_cameraService.IsConnected)
            {
                _loggerService.LogError("请先连接相机");
                return;
            }
            if (!_templateService.TemplateLoaded)
            {
                _loggerService.LogError("双Mark模板未完整创建，请先绘制Mark1和Mark2");
                return;
            }

            _detectionEnabled = true;
            _isDetecting = false;
            _detectionCount = 0;
            _hasLastRawM1 = false; _hasLastRawM2 = false;
            DetectionRunning = true;
            UpdateDeviceStatus();
            RefreshStatusDisplay();
            _detectionTimer.Start();

            _loggerService.LogInfo("开始检测：最小匹配分数=0.70, 贪婪度=0.90");
        }

        /// <summary>
        /// 停止检测——同时停止点胶和配方执行，运控卡已连接时自动回原点
        /// </summary>
        /// <remarks>
        /// 停止检测会做以下事情：
        /// 1. 关闭检测使能标志和定时器
        /// 2. 如果正在点胶，停止点胶、关胶阀、停配方执行
        /// 3. 运控卡已连接时自动回原点（安全复位）
        /// </remarks>
        private void StopDetection()
        {
            _detectionEnabled = false;
            _isDetecting = false;

            if (_isDispensing)
            {
                _isDispensing = false;
                IsExecutingRecipe = false;
                _motionService.StopRecipe();
                _motionService.ClearTrajectoryTrail();
                GlueOutputOn = false;
                _loggerService.LogInfo("点胶已停止");
            }

            _detectionTimer.Stop();
            DetectionRunning = false;
            UpdateDeviceStatus();
            RefreshStatusDisplay();

            if (IsCardConnected)
            {
                Home();
                _loggerService.LogInfo("检测已关闭，自动回原点");
            }
            else
            {
                _loggerService.LogInfo("检测已关闭");
            }
        }

        #endregion

        #region 九点标定命令

        /// <summary>打开九点标定窗口命令——CanExecute=IsDeviceReady</summary>
        public RelayCommand OpenCalibrationCommand { get; }

        /// <summary>
        /// 打开九点标定对话框——标定完成后保存数据并立即生效
        /// </summary>
        /// <remarks>
        /// 标定窗口（CalibrationWindow）是模态对话框，用户手动采集9个点后关闭
        /// 关闭时如果DialogResult=true，新的仿射变换矩阵立即生效并保存到磁盘
        /// </remarks>
        private void OpenCalibration()
        {
            var dialog = new CalibrationWindow(_calibData);
            dialog.Owner = Application.Current.MainWindow;
            if (dialog.ShowDialog() == true)
            {
                _calibData = dialog.ResultData;
                _calibData.IsCalibrated = true;

                SaveCalibrationToDisk(_calibData);
                OnPropertyChanged(nameof(CalibrationStatus));
                _loggerService.LogInfo("九点标定完成，新仿射变换矩阵已生成并保存，标定数据已立即生效");
            }
        }

        #endregion

        #region 运动控制命令

        /// <summary>连接运控卡命令——CanExecute始终为true</summary>
        public ICommand ConnectCardCommand { get; }
        /// <summary>断开运控卡命令——CanExecute=IsCardConnected</summary>
        public RelayCommand DisconnectCardCommand { get; }
        /// <summary>回原点命令——CanExecute=IsDeviceReady &amp;&amp; (未在执行配方 || 急停中)</summary>
        public RelayCommand HomeCommand { get; }
        /// <summary>急停命令——CanExecute=IsCardConnected || IsDebugMode（急停应该随时能用）</summary>
        public RelayCommand EmergencyStopCommand { get; }
        /// <summary>设备重启命令——CanExecute=IsCardConnected || IsCameraConnected</summary>
        public RelayCommand DeviceResetCommand { get; }
        /// <summary>调试模式切换命令——无条件可用</summary>
        public ICommand DebugModeCommand { get; }

        /// <summary>直线插补命令——CanExecute=IsDeviceReady</summary>
        public RelayCommand LinearInterpCommand { get; }
        /// <summary>圆弧插补命令——CanExecute=IsDeviceReady</summary>
        public RelayCommand ArcInterpCommand { get; }

        /// <summary>
        /// 连接运控卡——打开控制卡并启用软限位
        /// </summary>
        /// <remarks>
        /// 连接后：IsCardConnected=true，IsEstopActive=false
        /// 同时启用软限位（防止轴超出行程）
        /// </remarks>
        private void ConnectCard()
        {
            var result = _motionService.OpenCard();
            IsCardConnected = _motionService.IsCardOpened;
            IsEstopActive = false;
            OnPropertyChanged(nameof(IsDeviceReady));
            UpdateDeviceStatus();
            _loggerService.LogInfo(result);
            _motionService.EnableSoftLimit(true, true);
        }

        /// <summary>
        /// 断开运控卡连接——安全停机
        /// </summary>
        /// <remarks>
        /// 断开前做完整的安全停机：
        /// 1. 停止检测定时器
        /// 2. 停止点胶、关胶阀、气压归零
        /// 3. 急停运动
        /// 4. 关闭控制卡
        /// 会弹窗提示运控卡已断开
        /// </remarks>
        private void DisconnectCard()
        {
            // 安全停机：停止所有运动和检测
            if (_detectionEnabled)
            {
                _detectionTimer.Stop();
                _detectionEnabled = false;
                DetectionRunning = false;
            }
            if (_isDispensing)
            {
                _isDispensing = false;
                IsExecutingRecipe = false;
                _glueOutputOn = false;
                OnPropertyChanged(nameof(GlueOutputOn));
                OnPropertyChanged(nameof(GlueButtonText));
                _motionService.SetGlueOutput(false);
                GluePressure = 0;
                _gluePressureTimer?.Stop();
                OnPropertyChanged(nameof(GluePressureInfo));
            }
            _motionService.EmergencyStop();
            _motionService.CloseCard();
            IsCardConnected = false;
            OnPropertyChanged(nameof(IsDeviceReady));
            UpdateDeviceStatus();
            _loggerService.LogWarning("【异常】运动控制卡已断开，所有运动已停止");
            System.Windows.MessageBox.Show("运动控制卡已断开！\n所有运动已停止，请检查连接后重新连接。", "设备异常", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        /// <summary>
        /// 回原点操作——解除急停状态，关闭胶阀，执行回原点运动
        /// </summary>
        /// <remarks>
        /// 如果运控卡未连接会先尝试连接。回原速度用HomeVelocity，回原模式0.5（固高模式）
        /// </remarks>
        public void Home()
        {
            if (!IsCardConnected)
            {
                ConnectCard();
                if (!IsCardConnected)
                {
                    _loggerService.LogError("无法连接运动控制卡");
                    return;
                }
            }
            IsEstopActive = false;
            _isDispensing = false;
            IsExecutingRecipe = false;
            GlueOutputOn = false;
            _motionService.MoveHome(HomeVelocity, 0.5);
            _loggerService.LogInfo("回原点执行中...");
        }

        /// <summary>
        /// 急停/解除急停切换——第一次按急停，第二次按解除
        /// </summary>
        /// <remarks>
        /// 【5条安全机制】急停是全局最高优先级，触发时执行：
        /// 1. 立即停止所有运动（_motionService.EmergencyStop）
        /// 2. 强制关闭胶阀（SetGlueOutput(false)）
        /// 3. 气压归零（GluePressure = 0）
        /// 4. 停止气压仿真定时器
        /// 5. 记录日志
        ///
        /// 解除急停时：如果检测之前是开启的，恢复检测定时器
        /// </remarks>
        private void EmergencyStop()
        {
            if (!_isEstopActive)
            {
                // First press: activate estop - 全局最高优先级
                _isEstopActive = true;
                IsEstopActive = true;
                _motionService.EmergencyStop();
                // 强制关闭胶阀
                _glueOutputOn = false;
                OnPropertyChanged(nameof(GlueOutputOn));
                OnPropertyChanged(nameof(GlueButtonText));
                _motionService.SetGlueOutput(false);
                GluePressure = 0;
                _gluePressureTimer?.Stop();
                OnPropertyChanged(nameof(GluePressureInfo));
                _isDispensing = false;
                IsExecutingRecipe = false;
                _pauseAxisUpdates = false;
                _detectionTimer.Stop();
                UpdateDeviceStatus();
                _loggerService.LogWarning("【急停】已触发，所有运动已停止，胶阀已关闭");
            }
            else
            {
                // Second press: resume
                _isEstopActive = false;
                IsEstopActive = false;
                if (_detectionEnabled)
                {
                    _detectionTimer.Start();
                    DetectionRunning = true;
                }
                UpdateDeviceStatus();
                _loggerService.LogInfo("急停已解除，恢复检测");
            }
        }

        /// <summary>
        /// 设备重启——重置所有状态到初始连接状态
        /// </summary>
        /// <remarks>
        /// 执行操作（按顺序）：
        /// 1. 停止检测（定时器+标志）
        /// 2. 停止点胶（关胶阀+气压归零+停定时器）
        /// 3. 清除模板和配方绑定
        /// 4. 清除所有Mark坐标和偏差数据
        /// 5. 清除平滑滤波状态
        /// 6. 解除急停
        /// 7. 回原点并清除轨迹
        /// 8. 恢复到连接状态（保持运控卡和相机连接）
        /// </remarks>
        private void DeviceReset()
        {
            // 停止检测
            _detectionEnabled = false;
            _detectionTimer.Stop();
            DetectionRunning = false;
            _isDetecting = false;

            // 停止点胶
            _isDispensing = false;
            IsExecutingRecipe = false;
            _pauseAxisUpdates = false;
            GlueOutputOn = false;
            GluePressure = 0;
            _gluePressureTimer?.Stop();

            // 清除模板和配方绑定
            _boundRecipe = null;
            _waitingForRecipe = false;
            _currentTemplateName = "";
            CurrentRecipePoints.Clear();

            // 清除mark点坐标
            Mark1PhysX = 0; Mark1PhysY = 0;
            Mark2PhysX = 0; Mark2PhysY = 0;
            Mark1PixelRow = 0; Mark1PixelCol = 0;
            Mark2PixelRow = 0; Mark2PixelCol = 0;
            Score1 = 0; Score2 = 0;
            DeltaX = 0; DeltaY = 0; DeltaAngle = 0;
            _detectionCount = 0;
            _hasLastRawM1 = false; _hasLastRawM2 = false;
            _smoothMark1PhysX = 0; _smoothMark1PhysY = 0;
            _smoothMark2PhysX = 0; _smoothMark2PhysY = 0;

            // 解除急停
            _isEstopActive = false;
            IsEstopActive = false;

            // 回原点并清除轨迹
            if (IsCardConnected)
            {
                _motionService.EmergencyStop();
                _motionService.MoveHome(HomeVelocity, 0.5);
            }
            // 立即清除轨迹（MoveHome是异步的，但轨迹可以立即清）
            _motionService.ClearTrajectoryTrail();
            TrajectoryUpdated?.Invoke();

            // 恢复到连接状态（保持运控卡和相机连接）
            UpdateDeviceStatus();
            RefreshStatusDisplay();
            _loggerService.LogInfo("设备已重启，轨迹已清除，坐标已回原，恢复到初始连接状态");
        }

        /// <summary>切换调试模式——开启后所有功能解锁，不需要连接运控卡和相机</summary>
        private void ToggleDebugMode()
        {
            IsDebugMode = !IsDebugMode;
            UpdateDeviceStatus();
            _loggerService.LogInfo(IsDebugMode ? "调试模式已开启，所有功能已解锁" : "调试模式已关闭");
        }

        /// <summary>
        /// JOG手动移动——按住按钮移动，松开停止
        /// </summary>
        /// <param name="axis">轴号（1=X, 2=Y，与固高卡轴编号约定一致）</param>
        /// <param name="direction">方向（1=正方向, -1=负方向）</param>
        /// <remarks>
        /// 【踩坑记录】JOG调速失效问题：
        /// WPF的TextBox默认UpdateSourceTrigger=LostFocus，
        /// 用户输入新速度后不会立即生效，要等焦点离开才更新
        /// 修复：所有速度TextBox在XAML里加UpdateSourceTrigger=PropertyChanged
        /// </remarks>
        public void JogMove(int axis, int direction)
        {
            if (!IsCardConnected) return;
            _motionService.StartJog(axis, direction, JogVelocity, JogAcceleration, JogDeceleration);
        }

        /// <summary>
        /// 停止JOG移动——松开按钮时调用，若正在录制配方则自动记录当前点位
        /// </summary>
        /// <param name="axis">轴号（1=X, 2=Y，与 StartJog 的轴号约定一致）</param>
        /// <remarks>
        /// 录制模式下的自动记录流程：
        /// JOG移动到目标位置 → 松开按钮调用StopJog → 自动将当前位置作为RapidMove点加入配方轨迹
        /// </remarks>
        public void StopJog(int axis)
        {
            _motionService.StopJog(axis);
            // 录制模式下，JOG停止时自动记录当前位置为快速定位点
            if (IsRecordingRecipe)
            {
                var pt = new TrajectoryPoint
                {
                    X = AxisXPos,
                    Y = AxisYPos,
                    Type = TrajectoryType.RapidMove, // JOG停止点视为快速定位
                    Speed = JogVelocity,
                    GlueOn = GlueOutputOn
                };
                CurrentRecipePoints.Add(pt);
                TrajectoryUpdated?.Invoke();
                _loggerService.LogInfo($"录制Jog点: ({AxisXPos:F1},{AxisYPos:F1}), 速度={JogVelocity:F1}, 出胶={(GlueOutputOn ? "开" : "关")}");
            }
        }

        /// <summary>
        /// 执行直线插补运动——录制模式下同时记录直线轨迹点
        /// </summary>
        /// <remarks>
        /// 录制流程：用户设置目标坐标和速度 → 调用此方法 → 记录Linear类型轨迹点 → 执行运动控制
        /// 目标坐标来自LinearTargetX/Y，速度来自LinearVelocity，加减速度来自LinearAcceleration/Deceleration
        /// </remarks>
        private void DoLinearInterp()
        {
            // 录制模式下，先记录直线轨迹点再执行运动
            if (IsRecordingRecipe)
            {
                var pt = new TrajectoryPoint
                {
                    X = LinearTargetX,
                    Y = LinearTargetY,
                    Type = TrajectoryType.Linear,
                    Speed = LinearVelocity,
                    GlueOn = GlueOutputOn
                };
                CurrentRecipePoints.Add(pt);
                TrajectoryUpdated?.Invoke();
                _loggerService.LogInfo($"录制直线: ({LinearTargetX:F1},{LinearTargetY:F1}), 速度={LinearVelocity:F1}, 出胶={(GlueOutputOn ? "开" : "关")}");
            }
            _motionService.LinearInterpolation(LinearTargetX, LinearTargetY, LinearVelocity, LinearAcceleration, LinearDeceleration);
            _loggerService.LogInfo($"直线插补: 目标({LinearTargetX:F1},{LinearTargetY:F1})mm, 速度{LinearVelocity:F1}mm/s, 加速度{LinearAcceleration:F1}, 减速度{LinearDeceleration:F1}");
        }

        /// <summary>
        /// 执行圆弧插补运动（固高R半径模式）——录制模式下同时记录圆弧轨迹点
        /// </summary>
        /// <remarks>
        /// 固高R半径模式圆弧插补：
        /// - 起点 = 当前轴位置（上一段插补结束点位）
        /// - 终点 = ArcEndPointX/Y
        /// - R正 = 劣弧(＜180°)，R负 = 优弧(＞180°)
        /// - 方向 = ArcClockwise(CW/CCW)
        ///
        /// 圆心计算算法：
        /// 1. 根据起点、终点和半径R计算两个候选圆心（垂直平分线交点）
        /// 2. R > 0 选择劣弧（圆心角 ≤ π），R < 0 选择优弧（圆心角 > π）
        /// 3. 根据 ArcClockwise 确定顺/逆时针扫掠角方向
        ///
        /// 【踩坑记录】圆弧起始角度必须从当前位置计算：
        /// 不能假设起始角度为0，必须用Atan2从圆心到起点计算实际起始角度
        ///
        /// 录制模式下记录 Arc 类型轨迹点（含圆心坐标、半径、角度等完整信息）
        /// </remarks>
        private void DoArcInterp()
        {
            double startX = AxisXPos;
            double startY = AxisYPos;
            double endX = ArcEndPointX;
            double endY = ArcEndPointY;
            double r = ArcRadiusR;

            // 半径为0，无法构成圆弧
            if (Math.Abs(r) < 0.001)
            {
                _loggerService.LogError("圆弧半径R不能为0");
                return;
            }

            double dx = endX - startX;
            double dy = endY - startY;
            // 起终点之间的距离
            double d = Math.Sqrt(dx * dx + dy * dy);

            // 起终点重合，无法确定圆弧
            if (d < 0.001)
            {
                _loggerService.LogError("圆弧起点与终点不能重合");
                return;
            }

            double absR = Math.Abs(r);
            // 半径必须大于等于起终点距离的一半（几何约束）
            if (absR < d / 2.0 - 0.001)
            {
                _loggerService.LogError($"半径|R|={absR:F2}必须≥起终点距离的一半({d / 2.0:F2})");
                return;
            }

            // 计算圆心到起终点连线中点的距离（勾股定理）
            double h = Math.Sqrt(Math.Max(0, absR * absR - (d / 2.0) * (d / 2.0)));
            // 起终点连线的中点
            double midX = (startX + endX) / 2.0;
            double midY = (startY + endY) / 2.0;
            // 垂直于起终点连线的单位向量
            double perpX = -dy / d;
            double perpY = dx / d;

            // 两个候选圆心：沿垂直方向偏移 ±h
            double c1x = midX + h * perpX;
            double c1y = midY + h * perpY;
            double c2x = midX - h * perpX;
            double c2y = midY - h * perpY;

            // 计算候选圆心1对应的扫掠角
            double startAngle1 = Math.Atan2(startY - c1y, startX - c1x);
            double endAngle1 = Math.Atan2(endY - c1y, endX - c1x);
            double sweep1 = endAngle1 - startAngle1;
            if (ArcClockwise && sweep1 > 0) sweep1 -= 2 * Math.PI;
            if (!ArcClockwise && sweep1 < 0) sweep1 += 2 * Math.PI;

            // 计算候选圆心2对应的扫掠角
            double startAngle2 = Math.Atan2(startY - c2y, startX - c2x);
            double endAngle2 = Math.Atan2(endY - c2y, endX - c2x);
            double sweep2 = endAngle2 - startAngle2;
            if (ArcClockwise && sweep2 > 0) sweep2 -= 2 * Math.PI;
            if (!ArcClockwise && sweep2 < 0) sweep2 += 2 * Math.PI;

            // 根据R的符号选择圆心：R>0选劣弧(圆心角≤π)，R<0选优弧(圆心角>π)
            double cx, cy, sweepAngle;
            if (r > 0)
            {
                if (Math.Abs(sweep1) <= Math.PI + 0.001)
                {
                    cx = c1x; cy = c1y; sweepAngle = sweep1;
                }
                else
                {
                    cx = c2x; cy = c2y; sweepAngle = sweep2;
                }
            }
            else
            {
                if (Math.Abs(sweep1) > Math.PI - 0.001)
                {
                    cx = c1x; cy = c1y; sweepAngle = sweep1;
                }
                else
                {
                    cx = c2x; cy = c2y; sweepAngle = sweep2;
                }
            }

            double arcAngleDeg = Math.Abs(sweepAngle) * 180.0 / Math.PI;

            // 录制模式下，记录圆弧轨迹点（含圆心、半径、角度等完整信息）
            if (IsRecordingRecipe)
            {
                var pt = new TrajectoryPoint
                {
                    X = endX,
                    Y = endY,
                    Type = TrajectoryType.Arc,
                    Speed = ArcVelocity,
                    GlueOn = GlueOutputOn,
                    ArcCenterX = cx,
                    ArcCenterY = cy,
                    ArcRadius = absR,
                    ArcClockwise = ArcClockwise,
                    ArcAngle = arcAngleDeg
                };
                CurrentRecipePoints.Add(pt);
                TrajectoryUpdated?.Invoke();
                _loggerService.LogInfo($"录制圆弧: 终点({endX:F1},{endY:F1}), R={r:F1}, {(ArcClockwise ? "CW" : "CCW")}, 出胶={(GlueOutputOn ? "开" : "关")}");
            }

            // 执行圆弧插补运动
            _motionService.ArcInterpolation(cx, cy, absR, ArcClockwise, ArcVelocity, ArcAcceleration, ArcDeceleration, arcAngleDeg);
            string dir = ArcClockwise ? "顺时针" : "逆时针";
            string arcType = r > 0 ? "劣弧" : "优弧";
            _loggerService.LogInfo($"圆弧插补: 起点({startX:F1},{startY:F1}), 终点({endX:F1},{endY:F1}), R={r:F1}mm({arcType}), {dir}, 角度{arcAngleDeg:F1}°, 速度{ArcVelocity:F1}mm/s");
        }

        #endregion

        #region 配方命令

        /// <summary>新建配方命令——CanExecute=IsDeviceReady</summary>
        public RelayCommand NewRecipeCommand { get; }
        /// <summary>删除配方命令——CanExecute=IsDeviceReady &amp;&amp; 有选中配方</summary>
        public RelayCommand DeleteRecipeCommand { get; }
        /// <summary>清除轨迹命令——CanExecute=IsDeviceReady</summary>
        public RelayCommand ClearTrailCommand { get; }
        /// <summary>调用配方命令——CanExecute=IsDeviceReady &amp;&amp; 有配方可选</summary>
        public RelayCommand LoadRecipeCommand { get; }
        /// <summary>取消配方命令——CanExecute=正在录制配方</summary>
        public RelayCommand CancelRecipeCommand { get; }

        /// <summary>
        /// 新建配方——进入录制模式，停止检测，等待用户确认起点
        /// </summary>
        /// <remarks>
        /// 流程：停止检测 → 设置录制状态 → 清空轨迹 → 等待用户JOG到起点并确认
        /// 如果运控卡没连会先自动连接
        /// </remarks>
        private void NewRecipe()
        {
            _waitingForRecipe = false;

            if (!IsCardConnected)
            {
                ConnectCard();
            }

            _detectionEnabled = false;
            _detectionTimer.Stop();
            DetectionRunning = false;
            UpdateDeviceStatus();

            IsRecordingRecipe = true;
            StartPointConfirmed = false;
            EndPointConfirmed = false;
            _settingStartPoint = true;
            CurrentRecipePoints.Clear();
            _motionService.ClearTrajectoryTrail();
            _loggerService.LogInfo("新建配方：第一步，请通过JOG移动到起点位置，然后点击「确认起点」");
            TrajectoryUpdated?.Invoke();
        }

        /// <summary>确认起点命令——CanExecute=正在录制 &amp;&amp; 起点未确认</summary>
        public RelayCommand ConfirmStartPointCommand { get; }

        /// <summary>确认终点命令——CanExecute=正在录制 &amp;&amp; 起点已确认 &amp;&amp; 终点未确认</summary>
        public RelayCommand ConfirmEndPointCommand { get; }

        /// <summary>
        /// 确认起点——将当前轴位置记录为配方起点，添加为首个RapidMove点
        /// </summary>
        /// <remarks>
        /// 确认后刷新命令可用状态（ConfirmEndPoint和SaveRecipe的CanExecute依赖StartPointConfirmed）
        /// </remarks>
        private void ConfirmStartPoint()
        {
            if (!IsRecordingRecipe) return;

            StartPointX = AxisXPos;
            StartPointY = AxisYPos;
            StartPointConfirmed = true;
            _settingStartPoint = false;

            CurrentRecipePoints.Add(new TrajectoryPoint
            {
                X = StartPointX,
                Y = StartPointY,
                Type = TrajectoryType.RapidMove,
                Speed = JogVelocity,
                GlueOn = GlueOutputOn
            });
            TrajectoryUpdated?.Invoke();

            CommandManager.InvalidateRequerySuggested();
            _loggerService.LogInfo($"起点已确认: ({StartPointX:F2}, {StartPointY:F2})。现在可以开始绘制点胶轨迹");
        }

        /// <summary>
        /// 确认终点——将当前轴位置记录为配方终点，添加为末尾RapidMove点
        /// </summary>
        /// <remarks>确认后刷新命令可用状态（SaveRecipe的CanExecute依赖EndPointConfirmed）</remarks>
        private void ConfirmEndPoint()
        {
            if (!IsRecordingRecipe || !StartPointConfirmed) return;
            EndPointConfirmed = true;

            CurrentRecipePoints.Add(new TrajectoryPoint
            {
                X = AxisXPos,
                Y = AxisYPos,
                Type = TrajectoryType.RapidMove,
                Speed = JogVelocity,
                GlueOn = GlueOutputOn
            });
            TrajectoryUpdated?.Invoke();

            CommandManager.InvalidateRequerySuggested();
            _loggerService.LogInfo($"终点已确认: ({AxisXPos:F2}, {AxisYPos:F2})。可以保存配方");
        }

        /// <summary>保存配方命令——CanExecute=正在录制 &amp;&amp; 起点已确认 &amp;&amp; 终点已确认</summary>
        public RelayCommand SaveRecipeCommand { get; }

        /// <summary>
        /// 保存配方——将当前录制的轨迹点保存为命名配方，关联当前模板，并自动恢复检测
        /// </summary>
        /// <remarks>
        /// 保存流程：
        /// 1. 弹出输入框让用户命名配方
        /// 2. 记录起终点坐标和当前双Mark参考位置（用于后续纠偏计算）
        /// 3. 若同名配方已存在则覆盖，否则新增到Recipes列表
        /// 4. 将配方JSON同步写入模板目录（recipe.json）
        /// 5. 绑定配方（_boundRecipe）并自动恢复检测模式
        /// </remarks>
        private void SaveRecipe()
        {
            if (CurrentRecipePoints.Count == 0)
            {
                _loggerService.LogError("轨迹为空，无法保存");
                return;
            }

            string name = Microsoft.VisualBasic.Interaction.InputBox(
                "请输入配方名称：", "保存配方", $"配方{Recipes.Count + 1}");

            if (string.IsNullOrWhiteSpace(name)) return;

            var recipe = new DispensingRecipe
            {
                Name = name,
                TemplateName = _currentTemplateName,
                StartPointX = StartPointX,
                StartPointY = StartPointY,
                EndPointX = AxisXPos,
                EndPointY = AxisYPos,
                RefMark1X = Mark1PhysX,
                RefMark1Y = Mark1PhysY,
                RefMark2X = Mark2PhysX,
                RefMark2Y = Mark2PhysY,
                Points = new List<TrajectoryPoint>(CurrentRecipePoints)
            };

            var existing = Recipes.FirstOrDefault(r => r.Name == name);
            if (existing != null)
            {
                var idx = Recipes.IndexOf(existing);
                Recipes[idx] = recipe;
            }
            else
            {
                Recipes.Add(recipe);
                RefreshAllCommands(); // 配方列表变化后刷新LoadRecipeCommand等
            }

            if (!string.IsNullOrEmpty(_currentTemplateName))
            {
                try
                {
                    var templateDir = Path.Combine(_baseDir, "Templates", _currentTemplateName);
                    if (Directory.Exists(templateDir))
                    {
                        var recipeJson = JsonSerializer.Serialize(recipe, new JsonSerializerOptions { WriteIndented = true });
                        File.WriteAllText(Path.Combine(templateDir, "recipe.json"), recipeJson);
                        _loggerService.LogInfo($"配方已关联到模板 '{_currentTemplateName}'");
                    }
                }
                catch (Exception ex)
                {
                    _loggerService.LogError($"保存配方到模板路径失败: {ex.Message}");
                }
            }

            _boundRecipe = recipe;
            IsRecordingRecipe = false;
            _homeInProgress = false;
            _waitingForRecipe = false;
            _motionService.ClearTrajectoryTrail();
            SaveRecipesToDisk();
            TrajectoryUpdated?.Invoke();
            _loggerService.LogInfo($"配方 '{name}' 已保存，起点({StartPointX:F2},{StartPointY:F2})，共{CurrentRecipePoints.Count}个轨迹点");

            _detectionEnabled = true;
            _detectionTimer.Start();
            DetectionRunning = true;
            UpdateDeviceStatus();
            _loggerService.LogInfo("配方已保存，自动恢复检测");
        }

        /// <summary>
        /// 取消配方录制——清空轨迹数据，退出录制模式，恢复检测
        /// </summary>
        private void CancelRecipe()
        {
            IsRecordingRecipe = false;
            StartPointConfirmed = false;
            EndPointConfirmed = false;
            CurrentRecipePoints.Clear();
            _motionService.ClearTrajectoryTrail();
            TrajectoryUpdated?.Invoke();

            _detectionEnabled = true;
            _detectionTimer.Start();
            DetectionRunning = true;
            UpdateDeviceStatus();
            _loggerService.LogInfo("配方编辑已取消，恢复检测");
        }

        /// <summary>
        /// 删除配方——移除当前选中的配方并持久化到磁盘
        /// </summary>
        private void DeleteRecipe()
        {
            if (SelectedRecipeIndex < 0 || SelectedRecipeIndex >= Recipes.Count) return;
            var name = Recipes[SelectedRecipeIndex].Name;
            Recipes.RemoveAt(SelectedRecipeIndex);
            SelectedRecipeIndex = -1;
            RefreshAllCommands(); // 配方删除后刷新LoadRecipeCommand等
            CurrentRecipePoints.Clear();
            SaveRecipesToDisk();
            _loggerService.LogInfo($"配方 '{name}' 已删除");
        }

        /// <summary>
        /// 调用配方对话框——列出当前模板关联的所有配方，用户选择后回原并执行
        /// </summary>
        /// <remarks>
        /// 流程：检查模板 → 筛选匹配配方 → 弹出选择对话框 → 回原点 → 执行配方
        /// 注意：手动调用配方时纠偏量传0，只有自动触发（AutoExecuteRecipe）才带纠偏
        /// </remarks>
        private void LoadRecipeDialog()
        {
            if (string.IsNullOrEmpty(_currentTemplateName))
            {
                _loggerService.LogError("请先加载模板再调用配方");
                return;
            }

            var matchingRecipes = Recipes.Where(r => r.TemplateName == _currentTemplateName).ToList();

            if (matchingRecipes.Count == 0)
            {
                _loggerService.LogError($"当前模板 '{_currentTemplateName}' 没有关联配方，请新建配方");
                return;
            }

            var names = matchingRecipes.Select(r => r.Name).ToList();
            string selected = Microsoft.VisualBasic.Interaction.InputBox(
                "当前模板配方：\n" + string.Join("\n", names.Select((n, i) => $"{i + 1}. {n}")) + "\n\n请输入配方编号：",
                "调用配方", "1");

            if (!int.TryParse(selected, out int idx) || idx < 1 || idx > matchingRecipes.Count)
            {
                _loggerService.LogError("无效的配方编号");
                return;
            }

            var chosenRecipe = matchingRecipes[idx - 1];
            SelectedRecipeIndex = Recipes.IndexOf(chosenRecipe);
            OnRecipeSelected();

            Home();
            IsExecutingRecipe = true;
            _motionService.ExecuteRecipe(chosenRecipe, DeltaX, DeltaY, DeltaAngle);
            _loggerService.LogInfo($"调用配方 '{chosenRecipe.Name}'，回原后开始执行");
        }

        /// <summary>
        /// 配方选中事件处理——将选中配方的轨迹点加载到当前预览
        /// </summary>
        /// <remarks>由界面上的配方列表选中项变化时触发</remarks>
        public void OnRecipeSelected()
        {
            if (SelectedRecipeIndex < 0 || SelectedRecipeIndex >= Recipes.Count) return;
            var recipe = Recipes[SelectedRecipeIndex];
            CurrentRecipePoints.Clear();
            foreach (var pt in recipe.Points)
                CurrentRecipePoints.Add(pt);
            TrajectoryUpdated?.Invoke();
        }

        /// <summary>
        /// 清除轨迹——清除运动轨迹和配方预览，重置录制状态
        /// </summary>
        private void ClearTrail()
        {
            _motionService.ClearTrajectoryTrail();
            CurrentRecipePoints.Clear();
            IsRecordingRecipe = false;
            StartPointConfirmed = false;
            EndPointConfirmed = false;
            _homeInProgress = false;
            TrajectoryUpdated?.Invoke();
            _loggerService.LogInfo("轨迹和配方预览已清除");
        }

        #endregion

        #region 日志命令

        /// <summary>导出日志命令——CanExecute始终为true</summary>
        public ICommand ExportLogsCommand { get; }

        /// <summary>
        /// 导出日志——将当前日志序列化为JSON文件保存到Logs/目录
        /// </summary>
        /// <remarks>文件名格式：log_yyyyMMdd_HHmmss.json</remarks>
        private void ExportLogs()
        {
            try
            {
                var dir = Path.Combine(_baseDir, "Logs");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, $"log_{DateTime.Now:yyyyMMdd_HHmmss}.json");
                _loggerService.ExportToJson(file);
                _loggerService.LogInfo($"日志已导出: {file}");
            }
            catch (Exception ex)
            {
                _loggerService.LogError($"导出日志失败: {ex.Message}");
            }
        }

        #endregion

        #region 事件处理
        // 这些是运动服务和相机服务的事件回调，通过构造函数中的 += 订阅

        /// <summary>
        /// 图像采集回调——接收相机图像并执行视觉检测管线
        /// </summary>
        /// <param name="sender">事件源（相机服务）</param>
        /// <param name="ho_Image">Halcon图像对象——相机采集的原始图像</param>
        /// <remarks>
        /// 这是整个视觉处理管线的入口，处理流程：
        /// 1. 灰度转换（3通道彩色→1通道灰度，灰度图直接用）
        /// 2. 首帧图像触发FirstImageReceived事件，通知View做窗口适配
        /// 3. 检测启用且模板已加载时，执行双Mark模板匹配
        /// 4. 双Mark均匹配成功：像素→物理→平滑→偏差→自动触发配方
        /// 5. 仅单Mark匹配：更新对应Mark坐标，状态显示"仅Mark1/2匹配"
        /// 6. 无Mark匹配：状态显示"未匹配"（红色）
        ///
        /// 注意：检测逻辑在Task.Run中执行（避免阻塞UI线程），
        /// 结果通过Dispatcher.Invoke回到UI线程更新属性
        /// </remarks>
        private void OnImageGrabbed(object sender, HObject ho_Image)
        {
            if (ho_Image == null || !ho_Image.IsInitialized()) return;

            HObject ho_GrayImage = null;
            try
            {
                // 灰度转换：3通道彩色图转灰度，单通道直接克隆
                HOperatorSet.CountChannels(ho_Image, out HTuple channels);
                if (channels.I == 3)
                    HOperatorSet.Rgb1ToGray(ho_Image, out ho_GrayImage);
                else
                    ho_GrayImage = ho_Image.Clone();

                if (ho_GrayImage == null || !ho_GrayImage.IsInitialized()) return;

                // 首帧图像：通知视图层图像尺寸，用于显示控件初始化
                if (_isFirstImage)
                {
                    _isFirstImage = false;
                    HOperatorSet.GetImageSize(ho_GrayImage, out HTuple width, out HTuple height);
                    FirstImageReceived?.Invoke(this, new ImageSizeEventArgs
                    {
                        Width = width.I,
                        Height = height.I
                    });
                    _loggerService.LogInfo($"首次接收图像，尺寸: {width.I} x {height.I}");
                }

                // 检测启用且模板已加载时，执行双Mark匹配检测
                if (_detectionEnabled && _templateService.TemplateLoaded)
                {
                    if (_isDetecting)
                    {
                        // 正在检测中，仅更新显示图像，跳过本次检测
                        var oldImg = CurrentImage;
                        CurrentImage = ho_GrayImage.Clone();
                        oldImg?.Dispose();
                    }
                    else
                    {
                        // 开始新的检测周期
                        _isDetecting = true;
                        var processImage = ho_GrayImage.Clone();

                        Task.Run(() =>
                        {
                            try
                            {
                                // 双Mark模板匹配
                                var (result1, result2) = _markDetection.FindBothMarks(
                                    processImage, _templateService.ModelID1, _templateService.ModelID2,
                                    MinScore, 0.7);

                                Application.Current?.Dispatcher.Invoke(() =>
                                {
                                    // 点胶期间暂停轴更新（非调试模式下），跳过本次检测
                                    if (_pauseAxisUpdates && !IsDebugMode)
                                    {
                                        _isDetecting = false;
                                        return;
                                    }
                                    // 双Mark均匹配成功
                                    if (result1.Found && result2.Found)
                                    {
                                        Score1 = result1.Score;
                                        Score2 = result2.Score;
                                        Mark1PixelRow = result1.Row;
                                        Mark1PixelCol = result1.Col;
                                        Mark2PixelRow = result2.Row;
                                        Mark2PixelCol = result2.Col;

                                        double rawM1PhysX, rawM1PhysY, rawM2PhysX, rawM2PhysY;

                                        // 像素坐标→物理坐标变换（已标定时使用标定矩阵，未标定时直接用像素值）
                                        if (_calibData.IsCalibrated)
                                        {
                                            (rawM1PhysX, rawM1PhysY) = _calibData.PixelToWorld(result1.Row, result1.Col);
                                            (rawM2PhysX, rawM2PhysY) = _calibData.PixelToWorld(result2.Row, result2.Col);
                                        }
                                        else
                                        {
                                            rawM1PhysX = result1.Col; rawM1PhysY = result1.Row;
                                            rawM2PhysX = result2.Col; rawM2PhysY = result2.Row;
                                        }

                                        _detectionCount++;
                                        // 首次检测：直接赋值，不做平滑
                                        if (_detectionCount == 1)
                                        {
                                            _smoothMark1PhysX = rawM1PhysX;
                                            _smoothMark1PhysY = rawM1PhysY;
                                            _smoothMark2PhysX = rawM2PhysX;
                                            _smoothMark2PhysY = rawM2PhysY;
                                        }
                                        else
                                        {
                                            if (!IsPositionJump(rawM1PhysX, rawM1PhysY, _lastRawM1PhysX, _lastRawM1PhysY, _hasLastRawM1))
                                            {
                                                _smoothMark1PhysX += SmoothFactor * (rawM1PhysX - _smoothMark1PhysX);
                                                _smoothMark1PhysY += SmoothFactor * (rawM1PhysY - _smoothMark1PhysY);
                                            }

                                            if (!IsPositionJump(rawM2PhysX, rawM2PhysY, _lastRawM2PhysX, _lastRawM2PhysY, _hasLastRawM2))
                                            {
                                                _smoothMark2PhysX += SmoothFactor * (rawM2PhysX - _smoothMark2PhysX);
                                                _smoothMark2PhysY += SmoothFactor * (rawM2PhysY - _smoothMark2PhysY);
                                            }
                                        }

                                        _lastRawM1PhysX = rawM1PhysX; _lastRawM1PhysY = rawM1PhysY; _hasLastRawM1 = true;
                                        _lastRawM2PhysX = rawM2PhysX; _lastRawM2PhysY = rawM2PhysY; _hasLastRawM2 = true;

                                        Mark1PhysX = _smoothMark1PhysX;
                                        Mark1PhysY = _smoothMark1PhysY;
                                        Mark2PhysX = _smoothMark2PhysX;
                                        Mark2PhysY = _smoothMark2PhysY;

                                        // 计算当前工件中心和角度（以Mark2为偏移基准点）
                                        double m2OffsetX = Mark2PhysX;
                                        double m2OffsetY = Mark2PhysY;
                                        double m1RelX = Mark1PhysX - m2OffsetX;
                                        double m1RelY = Mark1PhysY - m2OffsetY;

                                        CurCenterX = (m1RelX + 0) / 2.0;
                                        CurCenterY = (m1RelY + 0) / 2.0;
                                        CurAngle = Math.Atan2(0 - m1RelY, 0 - m1RelX);

                                        // 计算偏差：与标准位置对比
                                        if (StdCenterX != 0 || StdCenterY != 0)
                                        {
                                            // 原始偏差值
                                            double rawDeltaX = CurCenterX - StdCenterX;
                                            double rawDeltaY = CurCenterY - StdCenterY;
                                            double rawDeltaAngle = CurAngle - StdAngle;

                                            // 对偏差也做平滑滤波
                                            _smoothDeltaX += SmoothFactor * (rawDeltaX - _smoothDeltaX);
                                            _smoothDeltaY += SmoothFactor * (rawDeltaY - _smoothDeltaY);
                                            _smoothDeltaAngle += SmoothFactor * (rawDeltaAngle - _smoothDeltaAngle);

                                            DeltaX = _smoothDeltaX;
                                            DeltaY = _smoothDeltaY;
                                            DeltaAngle = _smoothDeltaAngle;
                                        }
                                        else
                                        {
                                            // 首次检测到双Mark，记录为标准位置（偏差归零）
                                            StdCenterX = CurCenterX;
                                            StdCenterY = CurCenterY;
                                            StdAngle = CurAngle;
                                            _smoothDeltaX = 0; _smoothDeltaY = 0; _smoothDeltaAngle = 0;
                                            _loggerService.LogInfo("标准位置已记录，后续检测自动计算偏差");
                                        }

                                        DetectionStatusText = "双Mark匹配";
                                        DetectionStatusColor = "#FF2E7D32";

                                        // 自动触发配方逻辑：
                                        // 已绑定配方 && 未在点胶中 && 未在等待配方 → 自动执行纠偏点胶
                                        if (_boundRecipe != null && !_isDispensing && !_waitingForRecipe)
                                        {
                                            AutoExecuteRecipe();
                                        }
                                        else if (_boundRecipe == null && !_waitingForRecipe)
                                        {
                                            // 检测到双Mark但无关联配方，提示用户新建
                                            _waitingForRecipe = true;
                                            UpdateDeviceStatus();
                                            _loggerService.LogInfo("检测到双Mark点但无关联配方，请新建配方");
                                        }
                                    }
                                    else
                                    {
                                        if (result1.Found)
                                        {
                                            Score1 = result1.Score;
                                            Mark1PixelRow = result1.Row;
                                            Mark1PixelCol = result1.Col;
                                            double rawM1PhysX, rawM1PhysY;
                                            if (_calibData.IsCalibrated)
                                                (rawM1PhysX, rawM1PhysY) = _calibData.PixelToWorld(result1.Row, result1.Col);
                                            else
                                            {
                                                rawM1PhysX = result1.Col; rawM1PhysY = result1.Row;
                                            }
                                            Mark1PhysX = rawM1PhysX;
                                            Mark1PhysY = rawM1PhysY;
                                        }
                                        else
                                        {
                                            Score1 = 0;
                                            Mark1PixelRow = 0; Mark1PixelCol = 0;
                                        }

                                        if (result2.Found)
                                        {
                                            Score2 = result2.Score;
                                            Mark2PixelRow = result2.Row;
                                            Mark2PixelCol = result2.Col;
                                            double rawM2PhysX, rawM2PhysY;
                                            if (_calibData.IsCalibrated)
                                                (rawM2PhysX, rawM2PhysY) = _calibData.PixelToWorld(result2.Row, result2.Col);
                                            else
                                            {
                                                rawM2PhysX = result2.Col; rawM2PhysY = result2.Row;
                                            }
                                            Mark2PhysX = rawM2PhysX;
                                            Mark2PhysY = rawM2PhysY;
                                        }
                                        else
                                        {
                                            Score2 = 0;
                                            Mark2PixelRow = 0; Mark2PixelCol = 0;
                                        }

                                        if (!result1.Found && !result2.Found)
                                        {
                                            DetectionStatusText = "未匹配";
                                            DetectionStatusColor = "#FFC62828";
                                        }
                                        else if (!result1.Found)
                                        {
                                            DetectionStatusText = "仅Mark2匹配";
                                            DetectionStatusColor = "#FFFF8F00";
                                        }
                                        else
                                        {
                                            DetectionStatusText = "仅Mark1匹配";
                                            DetectionStatusColor = "#FFFF8F00";
                                        }
                                    }

                                    var oldImg = CurrentImage;
                                    CurrentImage = processImage.Clone();
                                    oldImg?.Dispose();

                                    RefreshStatusDisplay();
                                    TrajectoryUpdated?.Invoke();
                                });
                            }
                            catch (Exception ex)
                            {
                                Application.Current?.Dispatcher.Invoke(() =>
                                {
                                    DetectionStatusText = "检测异常";
                                    DetectionStatusColor = "#FFC62828";
                                    _loggerService.LogError($"检测异常: {ex.Message}");
                                    RefreshStatusDisplay();
                                });
                            }
                            finally
                            {
                                processImage?.Dispose();
                                _isDetecting = false;
                            }
                        });
                    }
                }
                else
                {
                    var oldImg = CurrentImage;
                    CurrentImage = ho_GrayImage.Clone();
                    oldImg?.Dispose();
                }
            }
            catch { }
            finally
            {
                ho_GrayImage?.Dispose();
            }
        }

        /// <summary>
        /// 检测定时器回调——每200ms触发一次，执行视觉检测
        /// </summary>
        /// <remarks>
        /// 跟OnImageGrabbed中的检测逻辑基本相同，区别是这里从_currentImage取图像
        /// （OnImageGrabbed是从相机回调直接拿图像）
        ///
        /// 【踩坑记录】_isDetecting必须在所有return路径上重置为false！
        /// 旧代码有个bug：_pauseAxisUpdates为true时提前return，但忘了重置_isDetecting，
        /// 导致后续检测永远被跳过（_isDetecting一直是true），配方无法触发。
        /// 现在在finally块里确保 _isDetecting = false
        /// </remarks>
        private void OnDetectionTimerTick(object sender, EventArgs e)
        {
            if (!_detectionEnabled || _isDetecting) return;
            if (_currentImage == null || !_currentImage.IsInitialized()) return;
            if (!_templateService.TemplateLoaded) return;

            // 标记检测中，防止重入
            _isDetecting = true;
            var processImage = _currentImage.Clone();

            Task.Run(() =>
            {
                try
                {
                    var (result1, result2) = _markDetection.FindBothMarks(
                        processImage, _templateService.ModelID1, _templateService.ModelID2,
                        MinScore, 0.7);

                    Application.Current?.Dispatcher.Invoke(() =>
                    {
                        // 【关键】点胶期间暂停检测，必须重置 _isDetecting 否则后续检测被卡死
                        if (_pauseAxisUpdates && !IsDebugMode)
                        {
                            _isDetecting = false;
                            return;
                        }
                        if (result1.Found && result2.Found)
                        {
                            Score1 = result1.Score;
                            Score2 = result2.Score;
                            Mark1PixelRow = result1.Row;
                            Mark1PixelCol = result1.Col;
                            Mark2PixelRow = result2.Row;
                            Mark2PixelCol = result2.Col;

                            double rawM1PhysX, rawM1PhysY, rawM2PhysX, rawM2PhysY;

                            if (_calibData.IsCalibrated)
                            {
                                (rawM1PhysX, rawM1PhysY) = _calibData.PixelToWorld(result1.Row, result1.Col);
                                (rawM2PhysX, rawM2PhysY) = _calibData.PixelToWorld(result2.Row, result2.Col);
                            }
                            else
                            {
                                rawM1PhysX = result1.Col; rawM1PhysY = result1.Row;
                                rawM2PhysX = result2.Col; rawM2PhysY = result2.Row;
                            }

                            _detectionCount++;
                            if (_detectionCount == 1)
                            {
                                _smoothMark1PhysX = rawM1PhysX;
                                _smoothMark1PhysY = rawM1PhysY;
                                _smoothMark2PhysX = rawM2PhysX;
                                _smoothMark2PhysY = rawM2PhysY;
                            }
                            else
                            {
                                if (!IsPositionJump(rawM1PhysX, rawM1PhysY, _lastRawM1PhysX, _lastRawM1PhysY, _hasLastRawM1))
                                {
                                    _smoothMark1PhysX += SmoothFactor * (rawM1PhysX - _smoothMark1PhysX);
                                    _smoothMark1PhysY += SmoothFactor * (rawM1PhysY - _smoothMark1PhysY);
                                }

                                if (!IsPositionJump(rawM2PhysX, rawM2PhysY, _lastRawM2PhysX, _lastRawM2PhysY, _hasLastRawM2))
                                {
                                    _smoothMark2PhysX += SmoothFactor * (rawM2PhysX - _smoothMark2PhysX);
                                    _smoothMark2PhysY += SmoothFactor * (rawM2PhysY - _smoothMark2PhysY);
                                }
                            }

                            _lastRawM1PhysX = rawM1PhysX; _lastRawM1PhysY = rawM1PhysY; _hasLastRawM1 = true;
                            _lastRawM2PhysX = rawM2PhysX; _lastRawM2PhysY = rawM2PhysY; _hasLastRawM2 = true;

                            Mark1PhysX = _smoothMark1PhysX;
                            Mark1PhysY = _smoothMark1PhysY;
                            Mark2PhysX = _smoothMark2PhysX;
                            Mark2PhysY = _smoothMark2PhysY;

                            double m2OffX = Mark2PhysX;
                            double m2OffY = Mark2PhysY;
                            double m1RelX2 = Mark1PhysX - m2OffX;
                            double m1RelY2 = Mark1PhysY - m2OffY;

                            CurCenterX = (m1RelX2 + 0) / 2.0;
                            CurCenterY = (m1RelY2 + 0) / 2.0;
                            CurAngle = Math.Atan2(0 - m1RelY2, 0 - m1RelX2);

                            if (StdCenterX != 0 || StdCenterY != 0)
                            {
                                double rawDeltaX = CurCenterX - StdCenterX;
                                double rawDeltaY = CurCenterY - StdCenterY;
                                double rawDeltaAngle = CurAngle - StdAngle;

                                _smoothDeltaX += SmoothFactor * (rawDeltaX - _smoothDeltaX);
                                _smoothDeltaY += SmoothFactor * (rawDeltaY - _smoothDeltaY);
                                _smoothDeltaAngle += SmoothFactor * (rawDeltaAngle - _smoothDeltaAngle);

                                DeltaX = _smoothDeltaX;
                                DeltaY = _smoothDeltaY;
                                DeltaAngle = _smoothDeltaAngle;
                            }
                            else
                            {
                                StdCenterX = CurCenterX;
                                StdCenterY = CurCenterY;
                                StdAngle = CurAngle;
                                _smoothDeltaX = 0; _smoothDeltaY = 0; _smoothDeltaAngle = 0;
                                _loggerService.LogInfo("标准位置已记录，后续检测自动计算偏差");
                            }

                            DetectionStatusText = "双Mark匹配";
                            DetectionStatusColor = "#FF2E7D32";

                            if (_boundRecipe != null && !_isDispensing && !_waitingForRecipe)
                            {
                                AutoExecuteRecipe();
                            }
                            else if (_boundRecipe == null && !_waitingForRecipe)
                            {
                                _waitingForRecipe = true;
                                UpdateDeviceStatus();
                                _loggerService.LogInfo("检测到双Mark点但无关联配方，请新建配方");
                            }
                        }
                        else
                        {
                            if (result1.Found)
                            {
                                Score1 = result1.Score;
                                Mark1PixelRow = result1.Row;
                                Mark1PixelCol = result1.Col;
                                double rawM1PhysX, rawM1PhysY;
                                if (_calibData.IsCalibrated)
                                    (rawM1PhysX, rawM1PhysY) = _calibData.PixelToWorld(result1.Row, result1.Col);
                                else
                                {
                                    rawM1PhysX = result1.Col; rawM1PhysY = result1.Row;
                                }
                                Mark1PhysX = rawM1PhysX;
                                Mark1PhysY = rawM1PhysY;
                            }
                            else
                            {
                                Score1 = 0;
                                Mark1PixelRow = 0; Mark1PixelCol = 0;
                            }

                            if (result2.Found)
                            {
                                Score2 = result2.Score;
                                Mark2PixelRow = result2.Row;
                                Mark2PixelCol = result2.Col;
                                double rawM2PhysX, rawM2PhysY;
                                if (_calibData.IsCalibrated)
                                    (rawM2PhysX, rawM2PhysY) = _calibData.PixelToWorld(result2.Row, result2.Col);
                                else
                                {
                                    rawM2PhysX = result2.Col; rawM2PhysY = result2.Row;
                                }
                                Mark2PhysX = rawM2PhysX;
                                Mark2PhysY = rawM2PhysY;
                            }
                            else
                            {
                                Score2 = 0;
                                Mark2PixelRow = 0; Mark2PixelCol = 0;
                            }

                            if (!result1.Found && !result2.Found)
                            {
                                DetectionStatusText = "未匹配";
                                DetectionStatusColor = "#FFC62828";
                            }
                            else if (!result1.Found)
                            {
                                DetectionStatusText = "仅Mark2匹配";
                                DetectionStatusColor = "#FFFF8F00";
                            }
                            else
                            {
                                DetectionStatusText = "仅Mark1匹配";
                                DetectionStatusColor = "#FFFF8F00";
                            }
                        }

                        RefreshStatusDisplay();
                        TrajectoryUpdated?.Invoke();
                    });
                }
                catch (Exception ex)
                {
                    Application.Current?.Dispatcher.Invoke(() =>
                    {
                        DetectionStatusText = "检测异常";
                        DetectionStatusColor = "#FFC62828";
                        _loggerService.LogError($"检测异常: {ex.Message}");
                        RefreshStatusDisplay();
                    });
                }
                finally
                {
                    processImage?.Dispose();
                    _isDetecting = false;
                }
            });
        }

        /// <summary>
        /// 带偏移纠偏的配方自动执行——检测到双Mark后自动调用
        /// </summary>
        /// <remarks>
        /// 纠偏算法（核心中的核心）：
        /// 1. 计算配方参考中心 (RefMark1 + RefMark2) / 2 ——录制时双Mark的中点
        /// 2. 计算当前检测中心 (当前Mark1 + 当前Mark2) / 2 ——当前双Mark的中点
        /// 3. 计算旋转角偏差: ΔAngle = 当前角度 - 配方参考角度
        /// 4. 计算平移偏移: offset = currentCenter - recipeCenter
        /// 5. 旋转纠偏: 对每个轨迹点绕配方参考中心旋转 ΔAngle
        /// 6. 平移纠偏: 每个旋转后的轨迹点 + offset
        /// 7. 圆弧点: 圆心也需要旋转+平移纠偏（圆弧半径不变）
        ///
        /// 前置条件：_boundRecipe不为null，双Mark匹配分数都>0
        /// </remarks>
        private void AutoExecuteRecipe()
        {
            if (_boundRecipe == null) return;
            if (Score1 <= 0 || Score2 <= 0) return; // 双Mark必须同时匹配才能纠偏

            // 检查配方与当前模板是否匹配，不匹配则解绑
            if (!string.IsNullOrEmpty(_currentTemplateName) && _boundRecipe.TemplateName != _currentTemplateName)
            {
                _boundRecipe = null;
                _waitingForRecipe = true;
                UpdateDeviceStatus();
                _loggerService.LogInfo("配方与当前模板不匹配，请新建配方");
                return;
            }

            _isDispensing = true;
            _pauseAxisUpdates = true; // 点胶期间暂停轴位置更新，避免干扰
            UpdateDeviceStatus();

            // ---- 纠偏计算 ----
            // 步骤1: 计算配方参考中心（录制时双Mark的中点）
            double refM1X = _boundRecipe.RefMark1X;
            double refM1Y = _boundRecipe.RefMark1Y;
            double refM2X = _boundRecipe.RefMark2X;
            double refM2Y = _boundRecipe.RefMark2Y;
            double refCX = (refM1X + refM2X) / 2.0;
            double refCY = (refM1Y + refM2Y) / 2.0;
            double refAngle = Math.Atan2(refM2Y - refM1Y, refM2X - refM1X);

            // 步骤2: 计算当前检测中心（当前双Mark的中点）
            double curM1X = Mark1PhysX;
            double curM1Y = Mark1PhysY;
            double curM2X = Mark2PhysX;
            double curM2Y = Mark2PhysY;
            double curCX = (curM1X + curM2X) / 2.0;
            double curCY = (curM1Y + curM2Y) / 2.0;
            double curAngle = Math.Atan2(curM2Y - curM1Y, curM2X - curM1X);

            // 步骤3: 计算旋转角偏差和平移偏移
            double dAngle = curAngle - refAngle;
            double deltaX = curCX - refCX;
            double deltaY = curCY - refCY;
            DeltaX = deltaX;
            DeltaY = deltaY;
            DeltaAngle = dAngle;

            var execRecipe = _boundRecipe.Clone();
            double cos = Math.Cos(dAngle);
            double sin = Math.Sin(dAngle);

            // 步骤4-6: 对每个轨迹点进行旋转+平移纠偏
            for (int i = 0; i < execRecipe.Points.Count; i++)
            {
                var p = execRecipe.Points[i];
                // 先将点平移到以配方参考中心为原点的坐标系
                double rx = p.X - refCX;
                double ry = p.Y - refCY;
                // 旋转纠偏：绕配方参考中心旋转 ΔAngle
                p.X = rx * cos - ry * sin + curCX;
                p.Y = rx * sin + ry * cos + curCY;

                // 步骤7: 圆弧点的圆心也需要旋转+平移纠偏
                if (p.Type == TrajectoryType.Arc)
                {
                    double acx = p.ArcCenterX - refCX;
                    double acy = p.ArcCenterY - refCY;
                    p.ArcCenterX = acx * cos - acy * sin + curCX;
                    p.ArcCenterY = acx * sin + acy * cos + curCY;
                }
            }

            IsExecutingRecipe = true;
            CurrentRecipePoints.Clear();
            foreach (var pt in execRecipe.Points)
                CurrentRecipePoints.Add(pt.Clone());
            _motionService.ExecuteRecipe(execRecipe, 0, 0, 0);
            _loggerService.LogInfo($"自动调用配方 '{_boundRecipe.Name}'，纠偏 ΔX={deltaX:F3} ΔY={deltaY:F3} ΔA={dAngle:F4}");
        }

        /// <summary>
        /// 出胶状态变更回调——配方执行过程中出胶/关胶时触发
        /// </summary>
        /// <remarks>
        /// 更新UI上的出胶状态和气压仿真显示
        /// 【踩坑记录】配方出胶时气压仿真：
        /// 旧代码只设置一次静态气压值，不会持续变化，看起来气压不动。
        /// 必须用_gluePressureTimer持续模拟波动效果(0.55~0.75 MPa)，
        /// 关胶时停止定时器并气压归零
        /// </remarks>
        private void OnGlueStateChanged(bool glueOn)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (IsExecutingRecipe)
                {
                    _glueOutputOn = glueOn;
                    OnPropertyChanged(nameof(GlueOutputOn));
                    OnPropertyChanged(nameof(GlueButtonText));
                }
                if (glueOn)
                {
                    var rnd = new Random();
                    GluePressure = 0.6 + rnd.NextDouble() * 0.1;
                    _gluePressureTimer?.Start();
                }
                else
                {
                    GluePressure = 0;
                    _gluePressureTimer?.Stop();
                }
                OnPropertyChanged(nameof(GluePressureInfo));
            });
        }

        /// <summary>
        /// 运动位置更新回调——实时更新轴坐标和回原状态
        /// </summary>
        /// <param name="x">X轴当前位置（mm）——来自MotionService</param>
        /// <param name="y">Y轴当前位置（mm）——来自MotionService</param>
        /// <param name="isMoving">轴是否正在运动中——用于检测运动停止时刻</param>
        /// <remarks>
        /// 当轴从运动状态变为静止时（_wasMoving=true, isMoving=false），
        /// 检测回原操作是否完成。执行配方期间会触发轨迹更新通知
        /// </remarks>
        private void OnMotionPositionUpdated(double x, double y, bool isMoving)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                AxisXPos = x;
                AxisYPos = y;
                AxisXHomed = _motionService.GetAxisStatus(1).IsHomed;
                AxisYHomed = _motionService.GetAxisStatus(2).IsHomed;

                if (_wasMoving && !isMoving)
                {
                    // 轴从运动变为静止，检查是否为回原完成
                    if (_homeInProgress)
                    {
                        _homeInProgress = false;
                    }
                }
                _wasMoving = isMoving;

                if (IsExecutingRecipe)
                    RecipeTrailUpdated?.Invoke();
                TrajectoryUpdated?.Invoke();
            });
        }

        /// <summary>
        /// 配方执行完成回调——重置点胶状态和所有检测数据，恢复检测等待下一工件
        /// </summary>
        /// <remarks>
        /// 点胶完成后必须重置所有平滑滤波状态和偏差数据，
        /// 否则下一个工件的检测会继承上一个工件的残留数据导致纠偏错误。
        /// 重置内容包括：检测计数、平滑坐标、偏差值、标准位置、Mark坐标、匹配分数
        /// 同时恢复检测定时器，等待下一个工件
        /// </remarks>
        private void OnRecipeExecutionCompleted()
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                IsExecutingRecipe = false;

                if (_isDispensing)
                {
                    _isDispensing = false;
                    _pauseAxisUpdates = false;
                    _detectionCount = 0;
                    _hasLastRawM1 = false; _hasLastRawM2 = false;
                    _smoothMark1PhysX = 0; _smoothMark1PhysY = 0;
                    _smoothMark2PhysX = 0; _smoothMark2PhysY = 0;
                    _smoothDeltaX = 0; _smoothDeltaY = 0; _smoothDeltaAngle = 0;
                    StdCenterX = 0; StdCenterY = 0; StdAngle = 0;
                    Mark1PhysX = 0; Mark1PhysY = 0;
                    Mark2PhysX = 0; Mark2PhysY = 0;
                    Mark1PixelRow = 0; Mark1PixelCol = 0;
                    Mark2PixelRow = 0; Mark2PixelCol = 0;
                    CurCenterX = 0; CurCenterY = 0; CurAngle = 0;
                    DeltaX = 0; DeltaY = 0; DeltaAngle = 0;
                    Score1 = 0; Score2 = 0;

                    if (_boundRecipe != null)
                    {
                        CurrentRecipePoints.Clear();
                        foreach (var pt in _boundRecipe.Points)
                            CurrentRecipePoints.Add(pt);
                    }

                    _loggerService.LogInfo("点胶完成，坐标已重置，恢复检测等待下一工件");

                    if (_detectionEnabled)
                    {
                        _detectionTimer.Start();
                        UpdateDeviceStatus();
                    }
                }
                else
                {
                    _loggerService.LogInfo("配方执行完成");
                }
            });
        }

        /// <summary>相机错误回调——记录相机异常信息到日志</summary>
        private void OnCameraError(string msg)
        {
            _loggerService.LogError(msg);
        }

        #endregion

        #region 标定持久化
        // 标定数据的保存和加载，使用System.Text.Json序列化

        /// <summary>
        /// 将标定数据保存到磁盘（Calibration/calibration.json）
        /// </summary>
        /// <param name="data">标定数据对象</param>
        /// <remarks>保存时转换为CalibrationJsonModel格式，方便JSON序列化</remarks>
        private void SaveCalibrationToDisk(CalibrationData data)
        {
            try
            {
                var dir = Path.Combine(_baseDir, "Calibration");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, "calibration.json");

                var jsonModel = new CalibrationJsonModel
                {
                    PixelPoints = data.PixelPoints.Select(p => new double[] { p.row, p.col }).ToList(),
                    WorldPoints = data.WorldPoints.Select(p => new double[] { p.x, p.y }).ToList(),
                    HomMat2D = data.HomMat2D,
                    CameraParameters = data.CameraParameters,
                    IsCalibrated = data.IsCalibrated
                };

                string json = JsonSerializer.Serialize(jsonModel, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(file, json);
            }
            catch { }
        }

        /// <summary>
        /// 从磁盘加载默认标定数据（Calibration/calibration.json）
        /// </summary>
        /// <returns>标定数据对象，加载失败返回null</returns>
        /// <remarks>这是二级回退策略的第二级</remarks>
        private CalibrationData LoadCalibrationFromDisk()
        {
            try
            {
                var file = Path.Combine(_baseDir, "Calibration", "calibration.json");
                if (!File.Exists(file)) return null;

                string json = File.ReadAllText(file);
                var jsonModel = JsonSerializer.Deserialize<CalibrationJsonModel>(json);
                if (jsonModel == null) return null;

                var data = new CalibrationData();
                data.PixelPoints = jsonModel.PixelPoints.Select(p => (p[0], p[1])).ToList();
                data.WorldPoints = jsonModel.WorldPoints.Select(p => (p[0], p[1])).ToList();
                data.HomMat2D = jsonModel.HomMat2D;
                data.CameraParameters = jsonModel.CameraParameters ?? "";
                data.IsCalibrated = jsonModel.IsCalibrated;
                return data;
            }
            catch { return null; }
        }

        /// <summary>
        /// 按名称加载标定数据——从CalibrationData/目录下的JSON文件中查找
        /// </summary>
        /// <param name="name">标定数据名称，默认加载"标定1"</param>
        /// <returns>标定数据对象，未找到返回null</returns>
        /// <remarks>
        /// 这是三级回退策略的第一级（最优先）
        /// 程序启动时默认调用此方法加载名为"标定1"的标定数据
        /// 标定数据包含9个标定点的像素/世界坐标和仿射变换矩阵
        /// </remarks>
        private CalibrationData LoadCalibrationByName(string name)
        {
            try
            {
                var dir = Path.Combine(_baseDir, "CalibrationData");
                if (!Directory.Exists(dir)) return null;
                var files = Directory.GetFiles(dir, "*.json");
                foreach (var f in files)
                {
                    try
                    {
                        var json = File.ReadAllText(f);
                        var d = JsonSerializer.Deserialize<CalibrationSaveData>(json);
                        if (d != null && d.Name == name)
                        {
                            var data = new CalibrationData();
                            data.PixelPoints.Clear();
                            data.WorldPoints.Clear();
                            for (int i = 0; i < 9; i++)
                            {
                                data.PixelPoints.Add((d.PixelRows[i], d.PixelCols[i]));
                                data.WorldPoints.Add((d.WorldXs[i], d.WorldYs[i]));
                            }
                            data.HomMat2D = d.HomMat2D;
                            data.IsCalibrated = true;
                            _loggerService.LogInfo($"已默认加载标定数据 '{name}'");
                            return data;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 标定数据JSON序列化模型——用于标定数据的磁盘持久化
        /// </summary>
        /// <remarks>
        /// CalibrationData本身不方便直接序列化（有Tuple等），
        /// 所以用这个中间模型做转换：CalibrationData ↔ CalibrationJsonModel ↔ JSON文件
        /// </remarks>
        private class CalibrationJsonModel
        {
            public List<double[]> PixelPoints { get; set; } = new();
            public List<double[]> WorldPoints { get; set; } = new();
            public double[] HomMat2D { get; set; } = new double[6];
            public string CameraParameters { get; set; } = "";
            public bool IsCalibrated { get; set; }
        }

        #endregion

        #region 配方持久化
        // 配方列表的保存和加载，使用System.Text.Json序列化

        /// <summary>
        /// 将配方列表保存到磁盘（Recipes/recipes.json）
        /// </summary>
        /// <remarks>保存时将ObservableCollection转为List再序列化</remarks>
        private void SaveRecipesToDisk()
        {
            try
            {
                var dir = Path.Combine(_baseDir, "Recipes");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, "recipes.json");
                var json = JsonSerializer.Serialize(Recipes.ToList(), new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(file, json);
            }
            catch { }
        }

        /// <summary>
        /// 从磁盘加载配方列表（Recipes/recipes.json）——构造函数中调用
        /// </summary>
        private void LoadRecipesFromDisk()
        {
            try
            {
                var file = Path.Combine(_baseDir, "Recipes", "recipes.json");
                if (!File.Exists(file)) return;
                var json = File.ReadAllText(file);
                var list = JsonSerializer.Deserialize<List<DispensingRecipe>>(json);
                if (list == null) return;
                Recipes.Clear();
                foreach (var r in list) Recipes.Add(r);
                RefreshAllCommands(); // 配方列表加载后刷新LoadRecipeCommand等
            }
            catch { }
        }

        #endregion

        /// <summary>
        /// 释放所有资源——程序退出时调用
        /// </summary>
        /// <remarks>
        /// 资源清理顺序：
        /// 1. 停止检测定时器和气压仿真定时器
        /// 2. 取消运动控制、相机服务的事件订阅（防止内存泄漏）
        /// 3. 释放运动控制和相机服务（关闭硬件连接）
        /// 4. 释放当前Halcon图像对象
        /// 5. 保存配方到磁盘（确保不丢失）
        /// </remarks>
        public void Dispose()
        {
            _detectionTimer.Stop();
            _gluePressureTimer?.Stop();
            _motionService.PositionUpdated -= OnMotionPositionUpdated;
            _motionService.RecipeExecutionCompleted -= OnRecipeExecutionCompleted;
            _motionService.GlueStateChanged -= OnGlueStateChanged;
            _cameraService.CameraError -= OnCameraError;
            _cameraService.ImageGrabbed -= OnImageGrabbed;

            _motionService.Dispose();
            _cameraService.Dispose();

            if (_currentImage != null && _currentImage.IsInitialized())
            {
                try { _currentImage.Dispose(); } catch { }
                _currentImage = null;
            }

            SaveRecipesToDisk();
        }
    }

    /// <summary>
    /// 首帧图像尺寸事件参数——通知View层图像的宽高信息，用于HalconWindow窗口适配
    /// </summary>
    public class ImageSizeEventArgs : EventArgs
    {
        /// <summary>图像宽度（像素）</summary>
        public int Width { get; set; }
        /// <summary>图像高度（像素）</summary>
        public int Height { get; set; }
    }
}
