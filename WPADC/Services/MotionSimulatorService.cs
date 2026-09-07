using System.Windows.Threading;
using WPADC.Models;
using WPADC.Services.Interfaces;

namespace WPADC.Services
{
    /// <summary>
    /// 运动控制仿真服务——在没有真实固高GTS800运控卡的情况下，用软件模拟卡的全部行为。
    /// </summary>
    /// <remarks>
    /// 为啥要仿真？因为开发调试的时候不可能一直接着硬件，而且硬件操作有风险（撞机、过行程等），
    /// 所以我们用这个类把GTS800的API全部"假装"实现一遍，让上层代码不管接真卡还是仿真都能正常跑。
    ///
    /// 仿真和真实的几个核心区别，心里要有数：
    /// 1. 真实硬件靠DSP发脉冲驱动电机，位置从编码器读；仿真靠DispatcherTimer每20ms算一次位置，纯软件计算
    /// 2. 真实硬件用坐标系缓冲区模式，所有运动段预写入FIFO，硬件自动连续执行；
    ///    仿真只能逐段执行，一段跑完再跑下一段（所以段间会有微小停顿，真实硬件不会）
    /// 3. 真实硬件有跟随误差（规划位置和编码器位置的差值），仿真里这俩永远相等
    /// 4. 仿真里所有GT_XXX方法都返回0（成功），真实硬件可能返回各种错误码
    ///
    /// DispatcherTimer 20ms驱动原理：
    /// WPF的DispatcherTimer在UI线程上触发Tick事件，间隔20ms（即50Hz）。
    /// 每次Tick里根据当前运动模式（JOG/Trap/直线插补/圆弧插补/配方）算出新的轴位置，
    /// 然后通过PositionUpdated事件把位置广播出去，界面就能实时刷新。
    /// 选20ms是因为：太快了CPU占得厉害，太慢了运动看着卡顿，20ms是个比较舒服的折中。
    ///
    /// PulsePerMm=1000 的单位转换：
    /// GTS800内部所有位置/速度/加速度都是脉冲单位，但咱们人看mm更直观。
    /// 1mm = 1000脉冲，所以 mm→脉冲 乘1000，脉冲→mm 除1000。
    /// 这个值跟硬件配置（丝杠导程、编码器线数等）有关，真实环境可能不是1000。
    /// </remarks>
    public class MotionSimulatorService : IMotionService
    {
        // 日志服务，用来记录系统状态信息
        private readonly ILoggerService _loggerService;

        // 20ms周期的定时器，整个仿真的心脏——所有运动计算都在Tick回调里做
        private readonly DispatcherTimer _motionTimer;

        /// <summary>
        /// 运动模式枚举：仿真器当前到底在干啥。
        /// 同一时刻只能处于一种模式，因为仿真只有两个轴，没法同时干两件事。
        /// </summary>
        private enum MotionMode
        {
            /// <summary>闲着呢，啥也没干</summary>
            Idle,
            /// <summary>JOG点动——按住方向键就一直走，松开就减速停</summary>
            Jog,
            /// <summary>梯形速度点位运动——单轴或双轴独立走到目标位置，速度曲线是梯形</summary>
            Trap,
            /// <summary>直线插补——XY两轴联动走直线，保证合成轨迹是直线</summary>
            LinearInterp,
            /// <summary>圆弧插补——XY两轴联动走圆弧，用角度插值法算位置</summary>
            ArcInterp,
            /// <summary>圆弧接近模式（已弃用）——先走直线到圆弧起点，再开始圆弧。后来发现不需要，直接从当前位置开始圆弧就行</summary>
            ArcApproach,
            /// <summary>配方执行——按轨迹点列表一段一段跑，直线段和圆弧段交替</summary>
            Recipe
        }

        /// <summary>当前运动模式，默认空闲</summary>
        private MotionMode _mode = MotionMode.Idle;

        /// <summary>卡号，固定0。真实GTS800 SDK里卡号从1开始，仿真里无所谓，统一用0</summary>
        private const short CardNum = 0;
        /// <summary>
        /// 脉冲当量：1mm = 1000脉冲。
        /// 这是整个项目最关键的一个常量，所有mm↔脉冲转换都靠它。
        /// 上层接口用mm，底层GTS800 API用脉冲，中间就得乘除这个值。
        /// </summary>
        private const double PulsePerMm = 1000.0;
        /// <summary>涂胶DO输出类型编号，对应固高GTS的MC_GPO(12)通道类型。这是固高官方定义的，别乱改</summary>
        private const short GlueDoType = 12;
        /// <summary>涂胶DO输出位索引，第0个DO位。也是固高官方定义的</summary>
        private const short GlueDoIndex = 0;

        #region 轴位置与状态字段

        /// <summary>X轴/Y轴当前实际位置（脉冲）。仿真里"实际位置"就是算出来的位置，没有编码器误差</summary>
        private double _axisX, _axisY;
        /// <summary>X/Y轴规划位置（脉冲）——运动规划器算出来的理论位置。仿真里跟实际位置一样，真实硬件会有跟随误差</summary>
        private double _prfPosX, _prfPosY;
        /// <summary>X/Y轴规划速度（脉冲/s）。给GT_GetPrfVel用的，仿真里简单记录一下</summary>
        private double _prfVelX, _prfVelY;
        /// <summary>X/Y轴编码器位置（脉冲）。仿真里跟实际位置一样，真实硬件从编码器读</summary>
        private double _encPosX, _encPosY;
        /// <summary>X/Y轴跟随误差（脉冲）。仿真里永远是0，真实硬件是规划位置-编码器位置</summary>
        private double _axisErrorX, _axisErrorY;
        /// <summary>X/Y轴目标位置（脉冲）。运动终点，到了就停</summary>
        private double _targetX, _targetY;
        /// <summary>控制卡是否已打开。GT_Open设true，GT_Close设false</summary>
        private bool _isCardOpened;
        /// <summary>X/Y轴是否已完成回原点。回原点到位后设true，位置清零前设false</summary>
        private bool _isHomedX, _isHomedY;
        /// <summary>涂胶输出是否开启。SetGlueOutput设的，配方执行时也会根据轨迹点切换</summary>
        private bool _glueOutputOn;
        /// <summary>X/Y轴伺服使能状态。仿真里默认true（始终使能），真实硬件要GT_AxisOn才使能</summary>
        private bool _axisOnX, _axisOnY;

        /// <summary>X/Y轴状态字。bit10(0x400)=运动中，bit12(0x1000)=回原点完成。仿真里只用了这两个位</summary>
        private int _axisStsX, _axisStsY;
        /// <summary>X轴正/负软限位（脉冲）。默认±50000脉冲=±50mm，超限就停</summary>
        private int _softLimitPosX, _softLimitNegX;
        /// <summary>Y轴正/负软限位（脉冲）。同X轴</summary>
        private int _softLimitPosY, _softLimitNegY;
        /// <summary>X/Y轴软限位是否启用。EnableSoftLimit方法设的</summary>
        private bool _lmtsOnX, _lmtsOnY;

        /// <summary>数字输出(DO)当前值。GT_SetDo/GT_SetDoBit设的，仿真里没实际意义</summary>
        private int _doValue;
        /// <summary>数字输入(DI)当前值。仿真里始终为0，因为没有真实传感器</summary>
        private int _diValue;

        #endregion

        #region JOG运动字段

        /// <summary>X/Y轴JOG方向：1=正向，-1=负向，0=停止。StartJog设方向，StopJog清0</summary>
        private int _jogDirX, _jogDirY;
        /// <summary>JOG目标速度（mm/s）。StartJog传进来的，转脉冲后用于计算</summary>
        private double _jogVelocity;
        /// <summary>X/Y轴JOG运动参数。仿真里只用了acc/dec/smooth，真实硬件参数更复杂</summary>
        private Gts.TJogPrm _jogPrmX, _jogPrmY;
        /// <summary>JOG加速度/减速度（脉冲/s²）。从mm/s²转过来的，用于加减速过渡计算</summary>
        private double _jogAccel, _jogDecel;
        /// <summary>
        /// X/Y轴JOG当前速度（脉冲/s）。
        /// JOG不是瞬间到目标速度的，有个加减速过程，这个字段记录当前实际速度。
        /// 有方向时加速到目标速度，方向清零时减速到0。
        /// </summary>
        private double _jogCurrentVelX, _jogCurrentVelY;

        #endregion

        #region 梯形速度点位运动(Trap)字段

        /// <summary>X/Y轴Trap运动参数。acc/dec/velStart/smoothTime，固高官方结构体</summary>
        private Gts.TTrapPrm _trapPrmX, _trapPrmY;
        /// <summary>X/Y轴Trap运动目标速度（脉冲/s）。GT_SetVel设的</summary>
        private double _trapVelX, _trapVelY;
        /// <summary>X/Y轴Trap运动目标位置（脉冲）。GT_SetPos设的</summary>
        private int _trapPosX, _trapPosY;

        #endregion

        #region 插补运动公共字段

        /// <summary>插补运动起点X/Y（脉冲）。开始插补时记录当前位置，用于计算进度</summary>
        private double _startX, _startY;
        /// <summary>插补运动终点X/Y（脉冲）。直线插补就是目标点，圆弧插补由角度算出来</summary>
        private double _endX, _endY;
        /// <summary>插补运动合成速度（脉冲/s）。两轴联动时的总速度，不是单轴速度</summary>
        private double _interpSpeed;
        /// <summary>插补运动进度，0~1。0=在起点，1=到终点。用于线性插值算位置</summary>
        private double _interpProgress;
        /// <summary>插补运动总距离（脉冲）。直线=两点距离，圆弧=弧长</summary>
        private double _totalDistance;

        #endregion

        #region 圆弧插补字段

        /// <summary>圆弧圆心X/Y坐标（脉冲）。ArcInterpolation方法传进来的</summary>
        private double _arcCenterX, _arcCenterY;
        /// <summary>
        /// 圆弧起始角度（弧度）。从圆心指向起点的角度，用Math.Atan2算。
        /// 【踩坑记录】这个值必须从当前轴位置动态计算！
        /// 早期代码硬编码_arcStartAngle=0，假设起点在圆心正右方（3点钟方向），
        /// 结果只要起点不在3点钟方向，终点位置、半径、顺逆时针方向全错。
        /// 正确做法：_arcStartAngle = Math.Atan2(_axisY - centerYPulse, _axisX - centerXPulse)
        /// </summary>
        private double _arcStartAngle;
        /// <summary>圆弧扫掠角度（弧度）。正值=逆时针，负值=顺时针。绝对值×半径=弧长</summary>
        private double _arcSweepAngle;
        /// <summary>圆弧半径（脉冲）。从圆心到起点的距离</summary>
        private double _arcRadius;
        /// <summary>是否顺时针方向</summary>
        private bool _arcClockwise;
        /// <summary>圆弧运动速度/加速度/减速度（脉冲/s, 脉冲/s²）和圆弧角度（度）。StartArcMotion用的，已弃用</summary>
        private double _arcVelocity, _arcAccel, _arcDecel, _arcAngleDeg;

        #endregion

        #region 坐标系字段

        /// <summary>坐标系参数。固高GTS800的坐标系配置，仿真里基本不用</summary>
        private Gts.TCrdPrm _crdPrm;
        /// <summary>坐标系是否正在运行。GT_CrdStart设true，运动完成设false</summary>
        private bool _crdRunning;
        /// <summary>当前坐标系段号。配方执行时递增，表示执行到第几段</summary>
        private int _crdSegment;

        /// <summary>速度倍率覆盖比例，0~1之间。GT_SetOverride设的，1=全速，0.5=半速</summary>
        private double _overrideRatio = 1.0;
        /// <summary>前瞻功能是否已初始化。仿真里只记个标记，没实际用</summary>
        private bool _lookAheadInit;

        #endregion

        #region 比较触发字段

        /// <summary>比较触发是否激活。仿真里只记个标记，真实硬件用来在指定位置触发DO输出</summary>
        private bool _compareActive;
        /// <summary>比较触发电平。仿真里没实际用</summary>
        private short _compareLevel;

        #endregion

        #region 配方执行字段

        /// <summary>配方轨迹点列表。ExecuteRecipe传进来的，按索引逐段执行</summary>
        private List<TrajectoryPoint>? _recipePoints;
        /// <summary>当前执行的配方点索引。从0开始，每段完成+1</summary>
        private int _recipeIndex;
        /// <summary>配方段起始X/Y位置（脉冲）。每段开始时记录当前位置，用于计算插补进度</summary>
        private double _recipeStartX, _recipeStartY;

        #endregion

        #region 运动速度规划字段

        /// <summary>加速度倍率（脉冲/s²），用于回原点等场景。值很大(50000000)，让回原点快速加减速</summary>
        private double _accelRate = 50000000;
        /// <summary>当前加速度（脉冲/s²）。不同运动模式可能不同，配方里圆弧段用300*PulsePerMm，直线段用500*PulsePerMm</summary>
        private double _currentAccel = 500000;
        /// <summary>当前减速度（脉冲/s²）。通常跟加速度一样</summary>
        private double _currentDecel = 500000;
        /// <summary>当前速度（脉冲/s）。梯形速度规划算出来的，0=静止</summary>
        private double _currentVel;
        /// <summary>已行进距离（脉冲）。从0开始累加，到_totalDistance就停</summary>
        private double _distanceTraveled;

        #endregion

        #region 轨迹记录字段

        /// <summary>
        /// 运动轨迹点列表，用于界面画轨迹线。
        /// 每个点存(x坐标mm, y坐标mm, 是否涂胶)。
        /// 界面绑定了TrajectoryTrail属性，直接用这个列表画线。
        /// </summary>
        private readonly List<(double x, double y, bool glueOn)> _trail = new();
        /// <summary>轨迹点最大数量限制。超过就删最早的，防止内存无限增长。10万个点够画很久了</summary>
        private const int MaxTrailSize = 100000;
        /// <summary>上一个轨迹记录点的X/Y坐标（mm）。用于去重——位置没变化就不重复添加</summary>
        private double _prevTrailX, _prevTrailY;

        #endregion

        #region 回原点字段

        /// <summary>X/Y轴回原点参数。固高官方结构体，仿真里只用了velHigh/velLow/acc/dec</summary>
        private Gts.THomePrm _homePrmX, _homePrmY;
        /// <summary>X/Y轴是否正在执行回原点。GT_GoHome设true，到位后设false</summary>
        private bool _homeRunningX, _homeRunningY;

        #endregion

        /// <summary>控制卡是否已打开。界面用来判断能不能操作</summary>
        public bool IsCardOpened => _isCardOpened;
        /// <summary>是否正在运动中（非空闲状态）。界面用来显示运动状态指示灯</summary>
        public bool IsMoving => _mode != MotionMode.Idle;
        /// <summary>是否正在执行配方。界面用来禁用某些操作按钮</summary>
        public bool IsExecutingRecipe => _mode == MotionMode.Recipe;
        /// <summary>涂胶输出开关状态。配方执行时会自动切换，也可以手动SetGlueOutput</summary>
        public bool GlueOutputOn { get => _glueOutputOn; set => _glueOutputOn = value; }
        /// <summary>运动轨迹点列表，供界面绑定显示轨迹线</summary>
        public List<(double x, double y, bool glueOn)> TrajectoryTrail => _trail;

        /// <summary>
        /// 位置更新事件。每次20ms Tick算完新位置就触发。
        /// 参数：(X坐标mm, Y坐标mm, 是否运动中)
        ///
        /// 谁发布：MotionTimer_Tick方法（本类的私有方法，20ms定时器回调）
        /// 谁订阅：MainViewModel订阅这个事件来更新界面上的坐标显示
        /// </summary>
        public event Action<double, double, bool>? PositionUpdated;
        /// <summary>
        /// 配方执行完成事件。所有轨迹段跑完后触发。
        /// 谁订阅：MainViewModel，用来恢复界面状态（启用按钮、显示完成提示等）
        /// </summary>
        public event Action? RecipeExecutionCompleted;
        /// <summary>
        /// 涂胶状态变化事件。配方执行中涂胶开关切换时触发。
        /// 参数：涂胶是否开启
        /// 谁订阅：MainViewModel，用来更新界面上的涂胶指示灯
        /// </summary>
        public event Action<bool>? GlueStateChanged;

        /// <summary>
        /// 构造函数，初始化运动仿真器。
        /// </summary>
        /// <param name="loggerService">日志服务，DI容器注入的，用来记录系统状态信息</param>
        public MotionSimulatorService(ILoggerService loggerService)
        {
            _loggerService = loggerService;

            // 创建20ms周期的定时器，这是整个仿真的心脏
            // Interval=20ms意味着每秒Tick约50次，每次Tick算一次位置
            _motionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            _motionTimer.Tick += MotionTimer_Tick;

            // 初始化JOG参数——这些是固高官方结构体的字段
            // acc/dec=1.0是归一化值，真实硬件里还要乘基准加速度
            // smooth=0.5是平滑系数，0=不平滑（纯梯形），1=最平滑（S型）
            _jogPrmX = new Gts.TJogPrm { acc = 1.0, dec = 1.0, smooth = 0.5 };
            _jogPrmY = new Gts.TJogPrm { acc = 1.0, dec = 1.0, smooth = 0.5 };

            // 初始化Trap参数
            // velStart=0：从静止开始加速
            // smoothTime=25：平滑时间25ms，控制S型加减速的平滑程度
            _trapPrmX = new Gts.TTrapPrm { acc = 1.0, dec = 1.0, velStart = 0, smoothTime = 25 };
            _trapPrmY = new Gts.TTrapPrm { acc = 1.0, dec = 1.0, velStart = 0, smoothTime = 25 };

            // 初始化软限位：±50000脉冲 = ±50mm
            // 这是默认值，可以通过SetSoftLimit方法修改
            _softLimitPosX = 50000;
            _softLimitNegX = -50000;
            _softLimitPosY = 50000;
            _softLimitNegY = -50000;

            // 初始化回原点参数
            // mode=HOME_MODE_LIMIT：限位回零模式（先找限位开关再回零）
            // moveDir=1：正向搜索
            // velHigh=5000脉冲/s：高速搜索速度
            // velLow=1000脉冲/s：低速搜索速度（接近原点时降速）
            // homeOffset=0：原点偏移为0（回零后不额外移动）
            _homePrmX = new Gts.THomePrm { mode = Gts.HOME_MODE_LIMIT, moveDir = 1, velHigh = 5000, velLow = 1000, acc = 1.0, dec = 1.0, homeOffset = 0 };
            _homePrmY = new Gts.THomePrm { mode = Gts.HOME_MODE_LIMIT, moveDir = 1, velHigh = 5000, velLow = 1000, acc = 1.0, dec = 1.0, homeOffset = 0 };
        }

        /// <summary>
        /// 打开运动控制卡（GTS800 SDK的GT_Open仿真版）。
        /// </summary>
        /// <param name="cardNum">卡号，仿真里固定0</param>
        /// <param name="channel">通道号，仿真里不用</param>
        /// <param name="param">参数，仿真里不用</param>
        /// <returns>0=成功（仿真永远成功），真实硬件可能返回错误码</returns>
        /// <remarks>
        /// 仿真模式下做了什么：
        /// 1. 标记卡已打开
        /// 2. 把所有轴位置清零（真实硬件不会，真实硬件的位置是上电后的随机值）
        /// 3. 标记轴未回原点
        /// 4. 关闭涂胶输出
        /// 5. 使能所有轴（仿真里默认使能）
        /// 6. 发布系统状态事件
        /// </remarks>
        public short GT_Open(short cardNum, short channel, short param)
        {
            _isCardOpened = true;
            // 仿真里打开卡就把所有位置清零，相当于"上电归零"
            // 真实硬件上电后位置是未知的，必须先回原点
            _axisX = _axisY = 0;
            _prfPosX = _prfPosY = 0;
            _encPosX = _encPosY = 0;
            _prfVelX = _prfVelY = 0;
            _axisErrorX = _axisErrorY = 0;
            _isHomedX = _isHomedY = false;
            _glueOutputOn = false;
            _doValue = 0;
            _diValue = 0;
            _axisStsX = _axisStsY = 0;
            _axisOnX = _axisOnY = true; // 仿真里默认使能
            _loggerService.LogInfo("运动控制卡(模拟)已连接");
            return 0;
        }

        /// <summary>
        /// 关闭运动控制卡。停止定时器，清状态。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <returns>0=成功</returns>
        public short GT_Close(short cardNum)
        {
            _motionTimer.Stop(); // 先停定时器，不然还在算位置
            _isCardOpened = false;
            _mode = MotionMode.Idle;
            // 清零JOG速度，防止下次打开卡时残留
            _jogDirX = _jogDirY = 0;
            _jogCurrentVelX = 0;
            _jogCurrentVelY = 0;
            _currentVel = 0;
            _crdRunning = false;
            return 0;
        }

        /// <summary>
        /// 复位运动控制卡。比GT_Close更彻底——不仅停运动，还把位置清零。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <returns>0=成功</returns>
        public short GT_Reset(short cardNum)
        {
            _motionTimer.Stop();
            _mode = MotionMode.Idle;
            // 复位所有轴位置到0
            _axisX = _axisY = 0;
            _prfPosX = _prfPosY = 0;
            _encPosX = _encPosY = 0;
            _prfVelX = _prfVelY = 0;
            _axisErrorX = _axisErrorY = 0;
            _jogDirX = _jogDirY = 0;
            _jogCurrentVelX = 0;
            _jogCurrentVelY = 0;
            _currentVel = 0;
            _crdRunning = false;
            _doValue = 0;
            _glueOutputOn = false;
            return 0;
        }

        /// <summary>
        /// 加载配置文件。仿真里啥也不干，直接返回成功。
        /// 真实硬件要加载GTS800.cfg来配置轴参数。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <param name="pFile">配置文件路径，仿真里忽略</param>
        /// <returns>0=成功</returns>
        public short GT_LoadConfig(short cardNum, string pFile)
        {
            return 0;
        }

        /// <summary>
        /// 清除轴状态字。仿真里就是把状态字清零。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <param name="axis">轴号（1=X, 2=Y）——固高GTS800轴号从1开始，这是官方规定</param>
        /// <param name="count">清除几个轴，仿真里只处理单个轴</param>
        /// <returns>0=成功</returns>
        public short GT_ClrSts(short cardNum, short axis, short count)
        {
            if (axis == 1) _axisStsX = 0;
            else if (axis == 2) _axisStsY = 0;
            return 0;
        }

        /// <summary>
        /// 使能轴伺服。仿真里就是打个标记，真实硬件会真的给伺服驱动器上电。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <param name="axis">轴号（1=X, 2=Y）</param>
        /// <returns>0=成功</returns>
        public short GT_AxisOn(short cardNum, short axis)
        {
            if (axis == 1) _axisOnX = true;
            else if (axis == 2) _axisOnY = true;
            return 0;
        }

        /// <summary>
        /// 关闭轴伺服。仿真里打个标记，真实硬件会断开伺服驱动器，轴就自由了（会受重力下滑）。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <param name="axis">轴号（1=X, 2=Y）</param>
        /// <returns>0=成功</returns>
        public short GT_AxisOff(short cardNum, short axis)
        {
            if (axis == 1) _axisOnX = false;
            else if (axis == 2) _axisOnY = false;
            return 0;
        }

        /// <summary>
        /// 清零轴位置——把规划位置和编码器位置都设为0。
        /// 通常在回原点完成后调用，把当前位置定义为原点。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <param name="axis">轴号（1=X, 2=Y）</param>
        /// <param name="count">清零几个轴</param>
        /// <returns>0=成功</returns>
        public short GT_ZeroPos(short cardNum, short axis, short count)
        {
            if (axis == 1)
            {
                _axisX = 0; _prfPosX = 0; _encPosX = 0;
                _axisErrorX = 0;
            }
            else if (axis == 2)
            {
                _axisY = 0; _prfPosY = 0; _encPosY = 0;
                _axisErrorY = 0;
            }
            return 0;
        }

        /// <summary>
        /// 获取轴状态字。仿真里返回内部维护的状态字，真实硬件从GTS800寄存器读。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <param name="axis">轴号（1=X, 2=Y）</param>
        /// <param name="pSts">输出状态字。常用位：bit10(0x400)=运动中，bit12(0x1000)=回原点完成</param>
        /// <param name="count">读取几个轴</param>
        /// <param name="pClock">输出时钟，仿真里返回0，真实硬件返回内部时钟值</param>
        /// <returns>0=成功</returns>
        public short GT_GetSts(short cardNum, short axis, out int pSts, short count, out uint pClock)
        {
            pClock = 0;
            if (axis == 1) pSts = _axisStsX;
            else if (axis == 2) pSts = _axisStsY;
            else pSts = 0;
            return 0;
        }

        /// <summary>
        /// 获取轴规划位置。仿真里返回内部计算的理论位置。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <param name="profile">规划轴号（1=X, 2=Y），跟轴号一样</param>
        /// <param name="pValue">输出规划位置（脉冲）</param>
        /// <param name="count">读取几个轴</param>
        /// <param name="pClock">输出时钟</param>
        /// <returns>0=成功</returns>
        public short GT_GetPrfPos(short cardNum, short profile, out double pValue, short count, out uint pClock)
        {
            pClock = 0;
            if (profile == 1) pValue = _prfPosX;
            else if (profile == 2) pValue = _prfPosY;
            else pValue = 0;
            return 0;
        }

        /// <summary>
        /// 获取轴规划速度。仿真里返回内部记录的速度值。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <param name="profile">规划轴号（1=X, 2=Y）</param>
        /// <param name="pValue">输出规划速度（脉冲/s）</param>
        /// <param name="count">读取几个轴</param>
        /// <param name="pClock">输出时钟</param>
        /// <returns>0=成功</returns>
        public short GT_GetPrfVel(short cardNum, short profile, out double pValue, short count, out uint pClock)
        {
            pClock = 0;
            if (profile == 1) pValue = _prfVelX;
            else if (profile == 2) pValue = _prfVelY;
            else pValue = 0;
            return 0;
        }

        /// <summary>
        /// 获取轴编码器位置。仿真里跟实际位置一样，真实硬件从编码器读（可能有跟随误差）。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <param name="axis">轴号（1=X, 2=Y）</param>
        /// <param name="pValue">输出编码器位置（脉冲）</param>
        /// <param name="count">读取几个轴</param>
        /// <param name="pClock">输出时钟</param>
        /// <returns>0=成功</returns>
        public short GT_GetAxisEncPos(short cardNum, short axis, out double pValue, short count, out uint pClock)
        {
            pClock = 0;
            if (axis == 1) pValue = _encPosX;
            else if (axis == 2) pValue = _encPosY;
            else pValue = 0;
            return 0;
        }

        /// <summary>
        /// 获取轴跟随误差。仿真里永远是0，真实硬件=规划位置-编码器位置。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <param name="axis">轴号（1=X, 2=Y）</param>
        /// <param name="pValue">输出跟随误差（脉冲）</param>
        /// <param name="count">读取几个轴</param>
        /// <param name="pClock">输出时钟</param>
        /// <returns>0=成功</returns>
        public short GT_GetAxisError(short cardNum, short axis, out double pValue, short count, out uint pClock)
        {
            pClock = 0;
            if (axis == 1) pValue = _axisErrorX;
            else if (axis == 2) pValue = _axisErrorY;
            else pValue = 0;
            return 0;
        }

        /// <summary>
        /// 设置轴运动模式为梯形速度点位运动(Trap)。
        /// 仿真里就是切一下模式，真实硬件要调GTS800 API设置规划器模式。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <param name="profile">规划轴号（1=X, 2=Y）</param>
        /// <returns>0=成功</returns>
        public short GT_PrfTrap(short cardNum, short profile)
        {
            // 只在空闲或已经是Trap模式时才切换，避免打断正在进行的其他运动
            if (_mode == MotionMode.Idle || _mode == MotionMode.Trap)
            {
                _mode = MotionMode.Trap;
            }
            return 0;
        }

        /// <summary>
        /// 设置Trap运动参数。固高官方结构体，仿真里存一下就行。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <param name="profile">规划轴号（1=X, 2=Y）</param>
        /// <param name="pPrm">Trap运动参数，包含acc/dec/velStart/smoothTime</param>
        /// <returns>0=成功</returns>
        public short GT_SetTrapPrm(short cardNum, short profile, ref Gts.TTrapPrm pPrm)
        {
            if (profile == 1) _trapPrmX = pPrm;
            else if (profile == 2) _trapPrmY = pPrm;
            return 0;
        }

        /// <summary>
        /// 设置轴目标位置。配合GT_SetVel和GT_Update一起用，是Trap运动的标准三步曲。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <param name="profile">规划轴号（1=X, 2=Y）</param>
        /// <param name="pos">目标位置（脉冲）。注意是int类型，GTS800位置参数是32位整数</param>
        /// <returns>0=成功</returns>
        public short GT_SetPos(short cardNum, short profile, int pos)
        {
            if (profile == 1) _trapPosX = pos;
            else if (profile == 2) _trapPosY = pos;
            return 0;
        }

        /// <summary>
        /// 设置轴运动速度。Trap模式下设置目标速度，JOG模式下设置JOG速度。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <param name="profile">规划轴号（1=X, 2=Y）</param>
        /// <param name="vel">目标速度（脉冲/s）。注意是double类型，GTS800速度参数是浮点数</param>
        /// <returns>0=成功</returns>
        public short GT_SetVel(short cardNum, short profile, double vel)
        {
            if (profile == 1) _trapVelX = vel;
            else if (profile == 2) _trapVelY = vel;
            return 0;
        }

        /// <summary>
        /// 更新轴运动——启动Trap点位运动。这是Trap运动三步曲的最后一步。
        /// 前两步是GT_SetPos设位置、GT_SetVel设速度，这步才真正开始动。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <param name="mask">轴掩码，bit0=X轴，bit1=Y轴。0x03=两轴同时动</param>
        /// <returns>0=成功，-1=卡没打开</returns>
        public short GT_Update(short cardNum, int mask)
        {
            if (!_isCardOpened) return -1;

            _mode = MotionMode.Trap;
            _prevTrailX = _axisX / PulsePerMm;
            _prevTrailY = _axisY / PulsePerMm;

            // 根据掩码设置各轴目标位置
            // mask的每一位对应一个轴：bit0=轴1(X)，bit1=轴2(Y)
            if ((mask & 0x01) != 0) _targetX = _trapPosX;
            if ((mask & 0x02) != 0) _targetY = _trapPosY;

            // 取两轴速度最大值作为合成速度
            // 因为是双轴独立运动，不是插补，所以用最大速度就行
            _interpSpeed = Math.Max(_trapVelX, _trapVelY);
            if (_interpSpeed <= 0) _interpSpeed = 5000; // 防止速度为0卡住

            // 初始化运动规划参数
            _startX = _axisX;
            _startY = _axisY;
            double dx = _targetX - _startX;
            double dy = _targetY - _startY;
            _totalDistance = Math.Sqrt(dx * dx + dy * dy); // 两点间距离
            _distanceTraveled = 0;
            _currentVel = 0; // 从静止开始

            _motionTimer.Start(); // 启动定时器，开始运动
            return 0;
        }

        /// <summary>
        /// 设置轴运动模式为JOG。仿真里啥也不干，JOG模式的设置在StartJog方法里做。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <param name="profile">规划轴号</param>
        /// <returns>0=成功</returns>
        public short GT_PrfJog(short cardNum, short profile)
        {
            return 0;
        }

        /// <summary>
        /// 设置JOG运动参数。固高官方结构体，仿真里存一下。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <param name="profile">规划轴号（1=X, 2=Y）</param>
        /// <param name="pPrm">JOG运动参数，包含acc/dec/smooth</param>
        /// <returns>0=成功</returns>
        public short GT_SetJogPrm(short cardNum, short profile, ref Gts.TJogPrm pPrm)
        {
            if (profile == 1) _jogPrmX = pPrm;
            else if (profile == 2) _jogPrmY = pPrm;
            return 0;
        }

        /// <summary>
        /// 设置坐标系参数。插补运动前必须先设置坐标系。
        /// 仿真里存一下，真实硬件要调GTS800 API。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <param name="crd">坐标系号</param>
        /// <param name="pCrdPrm">坐标系参数，包含维度、轴映射、最大速度等</param>
        /// <returns>0=成功</returns>
        public short GT_SetCrdPrm(short cardNum, short crd, ref Gts.TCrdPrm pCrdPrm)
        {
            _crdPrm = pCrdPrm;
            return 0;
        }

        /// <summary>
        /// 清除坐标系缓冲区，重置坐标系运行状态。
        /// 插补运动开始前必须先清缓冲区。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <param name="crd">坐标系号</param>
        /// <param name="fifo">FIFO编号，GTS800每个坐标系有2个FIFO缓冲区</param>
        /// <returns>0=成功</returns>
        public short GT_CrdClear(short cardNum, short crd, short fifo)
        {
            _crdRunning = false;
            _crdSegment = 0;
            return 0;
        }

        /// <summary>
        /// 启动坐标系运动。插补段写入缓冲区后，调这个才开始动。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <param name="mask">坐标系掩码，bit0=坐标系1</param>
        /// <param name="option">启动选项，0=正常启动</param>
        /// <returns>0=成功</returns>
        public short GT_CrdStart(short cardNum, short mask, short option)
        {
            _crdRunning = true;
            _motionTimer.Start(); // 仿真里启动定时器
            return 0;
        }

        /// <summary>
        /// 获取坐标系运行状态。用来判断插补运动是否还在跑。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <param name="crd">坐标系号</param>
        /// <param name="pRun">输出运行状态：1=运行中，0=已停止</param>
        /// <param name="pSegment">输出当前段号，配方执行时用来知道跑到第几段了</param>
        /// <param name="fifo">FIFO编号</param>
        /// <returns>0=成功</returns>
        public short GT_CrdStatus(short cardNum, short crd, out short pRun, out int pSegment, short fifo)
        {
            pRun = (short)(_crdRunning ? 1 : 0);
            pSegment = _crdSegment;
            return 0;
        }

        /// <summary>
        /// 获取坐标系缓冲区剩余空间。仿真里随便算一下，真实硬件FIFO空间有限（CRD_FIFO_MAX）。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <param name="crd">坐标系号</param>
        /// <param name="pSpace">输出剩余空间（段数）</param>
        /// <param name="fifo">FIFO编号</param>
        /// <returns>0=成功</returns>
        public short GT_CrdSpace(short cardNum, short crd, out int pSpace, short fifo)
        {
            pSpace = Gts.CRD_FIFO_MAX - _crdSegment;
            return 0;
        }

        /// <summary>
        /// 直线插补运动指令（XY平面）——设置终点和速度参数，写入坐标系缓冲区。
        /// 仿真里直接设置运动参数并启动，真实硬件要写入FIFO再GT_CrdStart。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <param name="crd">坐标系号</param>
        /// <param name="x">终点X坐标（脉冲）</param>
        /// <param name="y">终点Y坐标（脉冲）</param>
        /// <param name="synVel">合成速度（脉冲/s）——两轴联动的总速度，不是单轴速度</param>
        /// <param name="synAcc">合成加速度（脉冲/s²）</param>
        /// <param name="velEnd">终点速度（脉冲/s）——到达终点时的速度，0=到终点停，非0=到终点不停继续下一段</param>
        /// <param name="fifo">FIFO编号</param>
        /// <returns>0=成功，-1=卡没打开</returns>
        public short GT_LnXY(short cardNum, short crd, int x, int y, double synVel, double synAcc, double velEnd, short fifo)
        {
            if (!_isCardOpened) return -1;
            _mode = MotionMode.LinearInterp;
            _startX = _axisX;
            _startY = _axisY;
            _endX = x;
            _endY = y;
            // 应用速度倍率——GT_SetOverride设的，比如0.5就是半速运行
            _interpSpeed = synVel * _overrideRatio;
            _interpProgress = 0;
            _distanceTraveled = 0;
            _currentVel = 0;
            double dx = x - _axisX;
            double dy = y - _axisY;
            _totalDistance = Math.Sqrt(dx * dx + dy * dy);
            _prevTrailX = _axisX / PulsePerMm;
            _prevTrailY = _axisY / PulsePerMm;
            return 0;
        }

        /// <summary>
        /// 快速定位直线插补（G0）——终点速度为0，到点就停。
        /// 相当于GT_LnXY的velEnd=0版本，用于非加工的快速移动。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <param name="crd">坐标系号</param>
        /// <param name="x">终点X坐标（脉冲）</param>
        /// <param name="y">终点Y坐标（脉冲）</param>
        /// <param name="synVel">合成速度（脉冲/s）</param>
        /// <param name="synAcc">合成加速度（脉冲/s²）</param>
        /// <param name="fifo">FIFO编号</param>
        /// <returns>0=成功</returns>
        public short GT_LnXYG0(short cardNum, short crd, int x, int y, double synVel, double synAcc, short fifo)
        {
            return GT_LnXY(cardNum, crd, x, y, synVel, synAcc, 0, fifo);
        }

        /// <summary>
        /// 圆弧插补运动指令（XY平面）——指定圆心坐标模式(C模式)。
        /// C模式就是直接告诉圆心在哪，比R模式（只给半径）更直观。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <param name="crd">坐标系号</param>
        /// <param name="x">终点X坐标（脉冲）</param>
        /// <param name="y">终点Y坐标（脉冲）</param>
        /// <param name="xCenter">圆心X坐标（脉冲）</param>
        /// <param name="yCenter">圆心Y坐标（脉冲）</param>
        /// <param name="circleDir">圆弧方向：CW=顺时针(0)，CCW=逆时针(1)——固高官方常量</param>
        /// <param name="synVel">合成速度（脉冲/s）</param>
        /// <param name="synAcc">合成加速度（脉冲/s²）</param>
        /// <param name="velEnd">终点速度（脉冲/s）</param>
        /// <param name="fifo">FIFO编号</param>
        /// <returns>0=成功，-1=卡没打开或半径太小</returns>
        public short GT_ArcXYC(short cardNum, short crd, int x, int y, double xCenter, double yCenter, short circleDir, double synVel, double synAcc, double velEnd, short fifo)
        {
            if (!_isCardOpened) return -1;

            // 从当前轴位置计算圆弧半径
            // 半径=圆心到起点的距离，起点就是当前轴位置
            double dx = _axisX - xCenter;
            double dy = _axisY - yCenter;
            double radius = Math.Sqrt(dx * dx + dy * dy);

            // 半径太小（<1脉冲）就没意义了，报错
            if (radius < 1.0) return -1;

            _mode = MotionMode.ArcInterp;
            _startX = _axisX;
            _startY = _axisY;
            _endX = x;
            _endY = y;
            _arcCenterX = xCenter;
            _arcCenterY = yCenter;
            _arcRadius = radius;
            _interpSpeed = synVel * _overrideRatio;
            _interpProgress = 0;
            _distanceTraveled = 0;
            _currentVel = 0;

            // 【关键】从当前轴位置动态计算起始角度
            // Atan2(dy, dx)返回从圆心到当前点的角度，范围[-π, π]
            // 这就是"3点钟方向"是0度的数学约定
            _arcStartAngle = Math.Atan2(dy, dx);

            // 计算终点角度和扫掠角度
            double endDx = x - xCenter;
            double endDy = y - yCenter;
            double endAngle = Math.Atan2(endDy, endDx);
            _arcSweepAngle = endAngle - _arcStartAngle;

            // 根据顺逆时针方向调整扫掠角度范围
            // 顺时针：扫掠角度应为负值（角度递减方向）
            // 逆时针：扫掠角度应为正值（角度递增方向）
            bool clockwise = circleDir == Gts.INTERPOLATION_CIRCLE_DIR_CW;
            if (clockwise && _arcSweepAngle > 0) _arcSweepAngle -= 2 * Math.PI;
            if (!clockwise && _arcSweepAngle < 0) _arcSweepAngle += 2 * Math.PI;

            // 扫掠角度接近0时按整圆处理
            // 起点和终点重合时，Atan2算出来的扫掠角度≈0，但用户要的可能是整圆
            if (Math.Abs(_arcSweepAngle) < 0.0001)
                _arcSweepAngle = clockwise ? -2 * Math.PI : 2 * Math.PI;

            // 圆弧弧长 = |扫掠角度| × 半径
            _totalDistance = Math.Abs(_arcSweepAngle) * _arcRadius;
            _prevTrailX = _axisX / PulsePerMm;
            _prevTrailY = _axisY / PulsePerMm;
            return 0;
        }

        /// <summary>
        /// 圆弧插补运动指令（XY平面）——指定半径模式(R模式)。
        /// R模式只给半径和方向，圆心坐标要自己算。
        /// </summary>
        /// <remarks>
        /// R模式的圆心计算思路：
        /// 已知起点、终点、半径，圆心一定在起点-终点连线的垂直平分线上。
        /// 用勾股定理算出圆心到中点的距离h，然后根据顺逆时针方向选哪一侧。
        ///
        /// 正R值=劣弧（圆心在远侧，弧短），负R值=优弧（圆心在近侧，弧长）。
        /// 这个约定跟G代码的R编程一样。
        /// </remarks>
        /// <param name="cardNum">卡号</param>
        /// <param name="crd">坐标系号</param>
        /// <param name="x">终点X坐标（脉冲）</param>
        /// <param name="y">终点Y坐标（脉冲）</param>
        /// <param name="radius">圆弧半径（脉冲），正值=劣弧，负值=优弧</param>
        /// <param name="circleDir">圆弧方向：CW=顺时针，CCW=逆时针</param>
        /// <param name="synVel">合成速度（脉冲/s）</param>
        /// <param name="synAcc">合成加速度（脉冲/s²）</param>
        /// <param name="velEnd">终点速度（脉冲/s）</param>
        /// <param name="fifo">FIFO编号</param>
        /// <returns>0=成功，-1=卡没打开或半径不合法（两点距离>2倍半径）</returns>
        public short GT_ArcXYR(short cardNum, short crd, int x, int y, double radius, short circleDir, double synVel, double synAcc, double velEnd, short fifo)
        {
            if (!_isCardOpened) return -1;

            // 计算起点到终点的距离，验证半径合法性
            // 两点间距离不能超过直径（2×半径），否则圆弧不存在
            double dx = x - _axisX;
            double dy = y - _axisY;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist > 2 * radius + 0.001) return -1;

            // 勾股定理算圆心到中点的距离h
            // h² = radius² - (dist/2)²
            double halfDist = dist / 2.0;
            double h = Math.Sqrt(radius * radius - halfDist * halfDist);
            // 起点-终点连线中点
            double midX = (_axisX + x) / 2.0;
            double midY = (_axisY + y) / 2.0;

            // 起点-终点连线的垂直方向单位向量
            // 旋转90度：(-dy, dx) / dist
            double perpX = -dy / dist;
            double perpY = dx / dist;

            // 根据顺逆时针方向选择圆心在垂直平分线的哪一侧
            bool clockwise = circleDir == Gts.INTERPOLATION_CIRCLE_DIR_CW;
            double cx, cy;
            if (clockwise)
            {
                // 顺时针：圆心在前进方向右侧
                cx = midX + h * perpX;
                cy = midY + h * perpY;
            }
            else
            {
                // 逆时针：圆心在前进方向左侧
                cx = midX - h * perpX;
                cy = midY - h * perpY;
            }

            // 算出圆心后，转成C模式（指定圆心）调用
            return GT_ArcXYC(cardNum, crd, x, y, cx, cy, circleDir, synVel, synAcc, velEnd, fifo);
        }

        /// <summary>
        /// 缓冲区IO输出指令。真实硬件里用来在运动过程中同步控制DO输出，
        /// 比如走到某个位置自动开涂胶。仿真里啥也不干。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <param name="crd">坐标系号</param>
        /// <param name="doType">DO类型，12=MC_GPO通用输出</param>
        /// <param name="doMask">DO掩码，控制哪些位</param>
        /// <param name="doValue">DO值</param>
        /// <param name="fifo">FIFO编号</param>
        /// <returns>0=成功</returns>
        public short GT_BufIO(short cardNum, short crd, ushort doType, ushort doMask, ushort doValue, short fifo)
        {
            return 0;
        }

        /// <summary>
        /// 缓冲区延时指令。真实硬件里在运动段之间插入延时，仿真里啥也不干。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <param name="crd">坐标系号</param>
        /// <param name="delayTime">延时时间（ms）</param>
        /// <param name="fifo">FIFO编号</param>
        /// <returns>0=成功</returns>
        public short GT_BufDelay(short cardNum, short crd, ushort delayTime, short fifo)
        {
            return 0;
        }

        /// <summary>
        /// 停止轴运动。不管当前是什么模式（JOG/Trap/插补/回原点），统统停掉。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <param name="mask">轴掩码，bit0=X轴，bit1=Y轴</param>
        /// <param name="option">停止选项：0=平滑停止（减速），1=急停（立即停）</param>
        /// <returns>0=成功</returns>
        public short GT_Stop(short cardNum, int mask, int option)
        {
            // 把目标位置设为当前位置，这样运动计算会立刻认为"到了"
            _targetX = _axisX;
            _targetY = _axisY;
            // 清零JOG方向和速度
            _jogDirX = _jogDirY = 0;
            _jogCurrentVelX = 0;
            _jogCurrentVelY = 0;
            _currentVel = 0;
            _mode = MotionMode.Idle;
            _crdRunning = false;
            _homeRunningX = false;
            _homeRunningY = false;
            _motionTimer.Stop();
            return 0;
        }

        /// <summary>
        /// 设置轴软限位范围。超限就强制停。
        /// </summary>
        /// <param name="axis">轴号（1=X, 2=Y）</param>
        /// <param name="positive">正限位（脉冲）</param>
        /// <param name="negative">负限位（脉冲）</param>
        public void SetSoftLimit(int axis, int positive, int negative)
        {
            if (axis == 1) { _softLimitPosX = positive; _softLimitNegX = negative; }
            else if (axis == 2) { _softLimitPosY = positive; _softLimitNegY = negative; }
        }

        /// <summary>
        /// 启用或禁用软限位。禁用后轴可以无限走（小心撞机）。
        /// </summary>
        /// <param name="enableX">是否启用X轴软限位</param>
        /// <param name="enableY">是否启用Y轴软限位</param>
        public void EnableSoftLimit(bool enableX, bool enableY)
        {
            _lmtsOnX = enableX;
            _lmtsOnY = enableY;
        }

        /// <summary>
        /// 获取软限位范围，单位转成mm方便界面显示。
        /// </summary>
        /// <returns>X正限位、X负限位、Y正限位、Y负限位（mm）</returns>
        public (double posX, double negX, double posY, double negY) GetSoftLimits()
        {
            return (_softLimitPosX / PulsePerMm, _softLimitNegX / PulsePerMm,
                    _softLimitPosY / PulsePerMm, _softLimitNegY / PulsePerMm);
        }

        /// <summary>
        /// 设置数字输出(DO)值。仿真里存一下，真实硬件控制GTS800的DO端口。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <param name="doType">DO类型</param>
        /// <param name="value">DO值</param>
        /// <returns>0=成功</returns>
        public short GT_SetDo(short cardNum, short doType, int value)
        {
            _doValue = value;
            return 0;
        }

        /// <summary>
        /// 设置数字输出(DO)指定位的值。比GT_SetDo更精细，可以只控制某一位。
        /// 涂胶控制就是用这个方法——doType=12, doIndex=0。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <param name="doType">DO类型，12=MC_GPO通用输出（固高官方定义）</param>
        /// <param name="doIndex">DO位索引，第几个输出位</param>
        /// <param name="value">位值：0=关，1=开</param>
        /// <returns>0=成功</returns>
        public short GT_SetDoBit(short cardNum, short doType, short doIndex, short value)
        {
            // 涂胶DO特殊处理——doType=12且doIndex=0时，同步更新_glueOutputOn
            if (doType == GlueDoType && doIndex == GlueDoIndex)
            {
                _glueOutputOn = value != 0;
            }
            // 按位设置DO值：value=1把对应位置1，value=0把对应位清0
            if (value != 0)
                _doValue |= (1 << doIndex);
            else
                _doValue &= ~(1 << doIndex);
            return 0;
        }

        /// <summary>
        /// 获取数字输入(DI)值。仿真里始终返回0（没有真实传感器）。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <param name="diType">DI类型</param>
        /// <param name="pValue">输出DI值</param>
        /// <returns>0=成功</returns>
        public short GT_GetDi(short cardNum, short diType, out int pValue)
        {
            pValue = _diValue;
            return 0;
        }

        /// <summary>
        /// 获取数字输出(DO)值。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <param name="doType">DO类型</param>
        /// <param name="pValue">输出DO值</param>
        /// <returns>0=成功</returns>
        public short GT_GetDo(short cardNum, short doType, out int pValue)
        {
            pValue = _doValue;
            return 0;
        }

        /// <summary>
        /// 启动轴回原点运动。仿真里就是以Trap模式走到(0,0)。
        /// 真实硬件的回原点流程复杂得多（找限位→找原点信号→找Z相→偏移），由DSP自动执行。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <param name="axis">轴号（1=X, 2=Y）</param>
        /// <param name="pHomePrm">回原点参数，固高官方结构体</param>
        /// <returns>0=成功，-1=卡没打开</returns>
        public short GT_GoHome(short cardNum, short axis, ref Gts.THomePrm pHomePrm)
        {
            if (!_isCardOpened) return -1;

            // 保存回原点参数并标记正在回原点
            if (axis == 1)
            {
                _homePrmX = pHomePrm;
                _homeRunningX = true;
            }
            else if (axis == 2)
            {
                _homePrmY = pHomePrm;
                _homeRunningY = true;
            }

            // 仿真里回原点就是走到(0,0)
            _mode = MotionMode.Trap;
            _targetX = 0;
            _targetY = 0;
            _interpSpeed = Math.Max(_homePrmX.velHigh, _homePrmY.velHigh);
            _currentAccel = _accelRate;
            _currentDecel = _accelRate;
            _startX = _axisX;
            _startY = _axisY;
            double dx = _targetX - _startX;
            double dy = _targetY - _startY;
            _totalDistance = Math.Sqrt(dx * dx + dy * dy);
            _distanceTraveled = 0;
            _currentVel = 0;
            _prevTrailX = _axisX / PulsePerMm;
            _prevTrailY = _axisY / PulsePerMm;
            _motionTimer.Start();
            return 0;
        }

        /// <summary>
        /// 设置比较触发数据。真实硬件用来在指定位置触发DO输出（比如走到某位置自动拍照）。
        /// 仿真里只记个标记。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <param name="encoder">编码器号</param>
        /// <param name="source">触发源</param>
        /// <param name="pulseType">脉冲类型</param>
        /// <param name="startLevel">起始电平</param>
        /// <param name="time">时间</param>
        /// <param name="pBuf1">缓冲区1</param>
        /// <param name="count1">缓冲区1计数</param>
        /// <param name="pBuf2">缓冲区2</param>
        /// <param name="count2">缓冲区2计数</param>
        /// <returns>0=成功</returns>
        public short GT_CompareData(short cardNum, short encoder, short source, short pulseType, short startLevel, short time, ref int pBuf1, short count1, ref int pBuf2, short count2)
        {
            _compareActive = true;
            _compareLevel = startLevel;
            return 0;
        }

        /// <summary>
        /// 设置比较触发脉冲输出。仿真里只记个标记。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <param name="level">触发电平</param>
        /// <param name="outputType">输出类型</param>
        /// <param name="time">时间</param>
        /// <returns>0=成功</returns>
        public short GT_ComparePulse(short cardNum, short level, short outputType, short time)
        {
            _compareActive = true;
            _compareLevel = level;
            return 0;
        }

        /// <summary>
        /// 停止比较触发。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <returns>0=成功</returns>
        public short GT_CompareStop(short cardNum)
        {
            _compareActive = false;
            return 0;
        }

        /// <summary>
        /// 初始化前瞻功能。真实硬件里前瞻算法能预读后续运动段，自动调整速度实现段间平滑过渡。
        /// 仿真里只记个标记，没实际用。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <param name="crd">坐标系号</param>
        /// <param name="fifo">FIFO编号</param>
        /// <param name="T">前瞻周期</param>
        /// <param name="accMax">最大加速度</param>
        /// <param name="n">前瞻段数</param>
        /// <param name="pLookAheadBuf">前瞻缓冲区</param>
        /// <returns>0=成功</returns>
        public short GT_InitLookAhead(short cardNum, short crd, short fifo, double T, double accMax, short n, ref Gts.TCrdData pLookAheadBuf)
        {
            _lookAheadInit = true;
            return 0;
        }

        /// <summary>
        /// 设置速度倍率覆盖比例。0~1之间，1=全速，0.5=半速。
        /// 在插补运动中实时调整速度用的，比如操作员觉得太快了可以调慢。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <param name="crd">坐标系号</param>
        /// <param name="synVelRatio">速度倍率，0~1之间</param>
        /// <returns>0=成功</returns>
        public short GT_SetOverride(short cardNum, short crd, double synVelRatio)
        {
            _overrideRatio = synVelRatio;
            return 0;
        }

        /// <summary>
        /// 设置坐标系数据。仿真里啥也不干。
        /// </summary>
        /// <param name="cardNum">卡号</param>
        /// <param name="crd">坐标系号</param>
        /// <param name="pCrdData">坐标系数据指针</param>
        /// <param name="fifo">FIFO编号</param>
        /// <returns>0=成功</returns>
        public short GT_CrdData(short cardNum, short crd, IntPtr pCrdData, short fifo)
        {
            return 0;
        }

        /// <summary>
        /// 打开运动控制卡（简化接口）。上层代码调这个就行，不用管卡号通道号那些细节。
        /// </summary>
        /// <returns>连接结果消息</returns>
        public string OpenCard()
        {
            GT_Open(CardNum, 0, 0);
            return "运动控制卡(模拟)连接成功";
        }

        /// <summary>
        /// 关闭运动控制卡（简化接口）。
        /// </summary>
        public void CloseCard()
        {
            GT_Close(CardNum);
        }

        /// <summary>
        /// 所有轴回原点。用指定的运动参数构造回原点参数，然后启动X/Y轴回原点。
        /// </summary>
        /// <param name="param">运动参数，从配置文件加载的，包含回原点方向、速度、加速度等</param>
        /// <remarks>
        /// 仿真里回原点就是走到(0,0)，真实硬件要找限位开关→找原点信号→找Z相→偏移。
        /// velLow是velHigh的20%，接近原点时降速，防止冲过头。
        /// </remarks>
        public void HomeAllAxis(MotionParams param)
        {
            // 构造回原点参数：限位回零模式
            var homePrm = new Gts.THomePrm
            {
                mode = Gts.HOME_MODE_LIMIT,           // 限位回零模式
                moveDir = param.HomeDirection,         // 搜索方向（从MotionParams来）
                velHigh = param.HomeVelocity * PulsePerMm,  // 高速搜索速度（mm→脉冲/s）
                velLow = param.HomeVelocity * PulsePerMm * 0.2, // 低速=高速的20%
                acc = param.Acceleration,              // 加速度
                dec = param.Acceleration,              // 减速度
                homeOffset = 0                         // 原点偏移为0
            };
            // 依次启动X轴和Y轴回原点
            GT_GoHome(CardNum, 1, ref homePrm);
            GT_GoHome(CardNum, 2, ref homePrm);
        }

        /// <summary>
        /// 设置涂胶输出开关。同时更新内部状态和DO输出。
        /// </summary>
        /// <param name="on">true=开启涂胶，false=关闭涂胶</param>
        public void SetGlueOutput(bool on)
        {
            _glueOutputOn = on;
            GT_SetDoBit(CardNum, GlueDoType, GlueDoIndex, (short)(on ? 1 : 0));
        }

        /// <summary>
        /// 启动JOG点动运动——按住方向键就一直走，松开就减速停。
        /// </summary>
        /// <param name="axis">轴号（1=X, 2=Y）</param>
        /// <param name="direction">运动方向：1=正向，-1=负向</param>
        /// <param name="velocity">JOG速度（mm/s），从MotionParams.JogVelocity来</param>
        /// <param name="accel">加速度（mm/s²），从MotionParams来</param>
        /// <param name="decel">减速度（mm/s²），从MotionParams来</param>
        /// <remarks>
        /// JOG的特点是"持续运动直到停止"——不像Trap有目标位置，JOG只有方向和速度，
        /// 会一直走直到调StopJog。仿真里通过_jogDirX/Y控制方向，定时器里根据方向加速或减速。
        /// </remarks>
        public void StartJog(int axis, int direction, double velocity, double accel, double decel)
        {
            if (!_isCardOpened) return;
            // 设置JOG方向
            if (axis == 1) _jogDirX = direction;
            else if (axis == 2) _jogDirY = direction;
            _jogVelocity = velocity;
            // mm/s² 转换为 脉冲/s²
            _jogAccel = accel * PulsePerMm;
            _jogDecel = decel * PulsePerMm;
            // 首次进入JOG模式时初始化
            if (_mode != MotionMode.Jog)
            {
                _mode = MotionMode.Jog;
                _homeRunningX = false;
                _homeRunningY = false;
                _crdRunning = false;
                _jogCurrentVelX = 0; // 从0开始加速
                _jogCurrentVelY = 0;
                _prevTrailX = _axisX / PulsePerMm;
                _prevTrailY = _axisY / PulsePerMm;
                _motionTimer.Start(); // 启动定时器
            }
        }

        /// <summary>
        /// 停止指定轴的JOG运动。清零方向，速度会自然减速到0（不是瞬间停）。
        /// 当所有轴JOG速度都降为0后，才切换到空闲模式并停定时器。
        /// </summary>
        /// <param name="axis">轴号（1=X, 2=Y）</param>
        public void StopJog(int axis)
        {
            // 清零指定轴方向——方向清零后，定时器Tick里会自动减速到0
            if (axis == 1) { _jogDirX = 0; _jogCurrentVelX = 0; }
            else if (axis == 2) { _jogDirY = 0; _jogCurrentVelY = 0; }

            // 所有轴JOG方向为0且速度已降为0时，切换到空闲模式
            if (_jogDirX == 0 && _jogDirY == 0
                && Math.Abs(_jogCurrentVelX) < 1.0 && Math.Abs(_jogCurrentVelY) < 1.0)
            {
                _jogCurrentVelX = 0;
                _jogCurrentVelY = 0;
                _mode = MotionMode.Idle;
                _motionTimer.Stop();
            }
        }

        /// <summary>
        /// 绝对定位运动——移动到指定坐标位置。用梯形速度规划。
        /// </summary>
        /// <param name="axis">轴号（1=X, 2=Y）</param>
        /// <param name="position">目标位置（mm），绝对坐标</param>
        /// <param name="velocity">运动速度（mm/s）</param>
        public void AbsoluteMove(int axis, double position, double velocity)
        {
            if (!_isCardOpened) return;
            _mode = MotionMode.Trap;
            // mm 转换为 脉冲
            if (axis == 1) _targetX = position * PulsePerMm;
            else if (axis == 2) _targetY = position * PulsePerMm;
            _interpSpeed = velocity * PulsePerMm;
            _currentAccel = 500 * PulsePerMm; // 默认加速度500mm/s²
            _currentDecel = 500 * PulsePerMm;
            _startX = _axisX;
            _startY = _axisY;
            double dx = _targetX - _startX;
            double dy = _targetY - _startY;
            _totalDistance = Math.Sqrt(dx * dx + dy * dy);
            _distanceTraveled = 0;
            _currentVel = 0;
            _prevTrailX = _axisX / PulsePerMm;
            _prevTrailY = _axisY / PulsePerMm;
            _motionTimer.Start();
        }

        /// <summary>
        /// 相对运动——从当前位置移动指定距离。内部转成绝对坐标调AbsoluteMove。
        /// </summary>
        /// <param name="axis">轴号（1=X, 2=Y）</param>
        /// <param name="distance">移动距离（mm），正值=正向，负值=负向</param>
        /// <param name="velocity">运动速度（mm/s）</param>
        public void RelativeMove(int axis, double distance, double velocity)
        {
            if (!_isCardOpened) return;
            // 当前位置(mm) + 移动距离(mm) = 目标绝对位置(mm)
            if (axis == 1) AbsoluteMove(axis, (_axisX / PulsePerMm) + distance, velocity);
            else if (axis == 2) AbsoluteMove(axis, (_axisY / PulsePerMm) + distance, velocity);
        }

        /// <summary>
        /// 直线插补运动——双轴联动沿直线移动到目标位置。
        /// </summary>
        /// <remarks>
        /// 直线插补的核心思路：
        /// X和Y轴按比例同步运动，保证合成轨迹是直线。
        /// 用梯形速度规划控制合成速度的加减速过程。
        /// 进度progress = 已走距离/总距离，0~1之间，
        /// 然后X = 起点X + (终点X-起点X) × progress，Y同理。
        /// </remarks>
        /// <param name="targetX">目标X坐标（mm）</param>
        /// <param name="targetY">目标Y坐标（mm）</param>
        /// <param name="velocity">合成速度（mm/s）——两轴联动的总速度</param>
        /// <param name="acceleration">加速度（mm/s²）</param>
        /// <param name="deceleration">减速度（mm/s²）</param>
        public void LinearInterpolation(double targetX, double targetY, double velocity, double acceleration, double deceleration)
        {
            if (!_isCardOpened) return;
            _homeRunningX = false;
            _homeRunningY = false;
            _mode = MotionMode.LinearInterp;
            _startX = _axisX;
            _startY = _axisY;
            // mm 转换为 脉冲
            _endX = targetX * PulsePerMm;
            _endY = targetY * PulsePerMm;
            _interpSpeed = velocity * PulsePerMm;
            _currentAccel = acceleration * PulsePerMm;
            _currentDecel = deceleration * PulsePerMm;
            _interpProgress = 0;
            _distanceTraveled = 0;
            _currentVel = 0;
            double dx = _endX - _axisX;
            double dy = _endY - _axisY;
            _totalDistance = Math.Sqrt(dx * dx + dy * dy);
            _prevTrailX = _axisX / PulsePerMm;
            _prevTrailY = _axisY / PulsePerMm;
            _motionTimer.Start();
        }

        /// <summary>
        /// 圆弧插补运动——双轴联动走圆弧。用角度插值法算位置。
        /// </summary>
        /// <remarks>
        /// 角度插值法的思路：
        /// 1. 算出起始角度（从圆心到当前位置的角度）
        /// 2. 算出扫掠角度（要转多少度）
        /// 3. 根据梯形速度规划算出当前进度progress
        /// 4. 当前角度 = 起始角度 + 扫掠角度 × progress
        /// 5. X = 圆心X + 半径 × cos(当前角度)，Y = 圆心Y + 半径 × sin(当前角度)
        ///
        /// 【踩坑记录——_arcStartAngle必须从当前轴位置计算】
        /// 早期代码硬编码_arcStartAngle=0，假设起点在圆心正右方（3点钟方向），
        /// 这导致只要起点不在3点钟方向，算出来的终点位置、半径、顺逆时针方向全错。
        /// 正确做法：_arcStartAngle = Math.Atan2(_axisY - centerYPulse, _axisX - centerXPulse)
        /// 同时也移除了错误的ArcApproach模式（试图先走直线到"假起点"再开始圆弧）。
        /// </remarks>
        /// <param name="centerX">圆心X坐标（mm）</param>
        /// <param name="centerY">圆心Y坐标（mm）</param>
        /// <param name="radius">圆弧半径（mm）</param>
        /// <param name="clockwise">是否顺时针方向</param>
        /// <param name="velocity">合成速度（mm/s）</param>
        /// <param name="acceleration">加速度（mm/s²）</param>
        /// <param name="deceleration">减速度（mm/s²）</param>
        /// <param name="arcAngleDeg">圆弧角度（度），默认360度=整圆</param>
        public void ArcInterpolation(double centerX, double centerY, double radius,
            bool clockwise, double velocity, double acceleration, double deceleration, double arcAngleDeg = 360.0)
        {
            if (!_isCardOpened) return;
            _homeRunningX = false;
            _homeRunningY = false;

            // mm 转换为 脉冲
            double centerXPulse = centerX * PulsePerMm;
            double centerYPulse = centerY * PulsePerMm;
            double radiusPulse = radius * PulsePerMm;

            _arcCenterX = centerXPulse;
            _arcCenterY = centerYPulse;
            _arcRadius = radiusPulse;
            _arcClockwise = clockwise;
            _arcVelocity = velocity * PulsePerMm;
            _arcAccel = acceleration * PulsePerMm;
            _arcDecel = deceleration * PulsePerMm;

            // 【关键】从当前轴位置计算起始角度，不能硬编码为0！
            // Atan2算出从圆心到当前点的角度，这就是圆弧的起始角度
            _arcStartAngle = Math.Atan2(_axisY - centerYPulse, _axisX - centerXPulse);

            // 计算扫掠角度：顺时针为负，逆时针为正
            double desiredSweepRad = arcAngleDeg * Math.PI / 180.0;
            if (clockwise)
                _arcSweepAngle = -Math.Abs(desiredSweepRad);
            else
                _arcSweepAngle = Math.Abs(desiredSweepRad);

            // 扫掠角度接近0时按整圆处理
            if (Math.Abs(_arcSweepAngle) < 0.0001)
                _arcSweepAngle = clockwise ? -2 * Math.PI : 2 * Math.PI;

            // 圆弧弧长 = |扫掠角度| × 半径
            _totalDistance = Math.Abs(_arcSweepAngle) * _arcRadius;

            // 直接开始圆弧运动——起点就是当前位置，不需要ArcApproach
            _mode = MotionMode.ArcInterp;
            _startX = _axisX;
            _startY = _axisY;
            _interpSpeed = _arcVelocity;
            _currentAccel = _arcAccel;
            _currentDecel = _arcDecel;
            _interpProgress = 0;
            _distanceTraveled = 0;
            _currentVel = 0;
            _prevTrailX = _axisX / PulsePerMm;
            _prevTrailY = _axisY / PulsePerMm;
            _motionTimer.Start();
        }

        /// <summary>
        /// 启动圆弧运动（内部方法，已弃用）。
        /// 这是早期ArcApproach模式用的，先走直线到"假起点"再开始圆弧。
        /// 后来发现_arcStartAngle=0是错误的硬编码，整个ArcApproach模式都不需要了。
        /// 保留这个方法只是因为代码还在，新代码应该用ArcInterpolation。
        /// </summary>
        private void StartArcMotion()
        {
            _mode = MotionMode.ArcInterp;
            _startX = _axisX;
            _startY = _axisY;
            _interpSpeed = _arcVelocity;
            _currentAccel = _arcAccel;
            _currentDecel = _arcDecel;
            _interpProgress = 0;
            _distanceTraveled = 0;
            _currentVel = 0;

            // ⚠️ 此处硬编码为0是错误的！仅保留用于ArcApproach兼容
            _arcStartAngle = 0;

            double desiredSweepRad = _arcAngleDeg * Math.PI / 180.0;
            if (_arcClockwise)
                _arcSweepAngle = -Math.Abs(desiredSweepRad);
            else
                _arcSweepAngle = Math.Abs(desiredSweepRad);

            if (Math.Abs(_arcSweepAngle) < 0.0001)
                _arcSweepAngle = _arcClockwise ? -2 * Math.PI : 2 * Math.PI;

            _totalDistance = Math.Abs(_arcSweepAngle) * _arcRadius;
            _prevTrailX = _axisX / PulsePerMm;
            _prevTrailY = _axisY / PulsePerMm;
        }

        /// <summary>
        /// 执行涂胶配方——按轨迹点序列依次执行运动。
        /// </summary>
        /// <remarks>
        /// 配方执行的流程：
        /// 1. 把配方轨迹点列表保存到_recipePoints
        /// 2. 从第一个轨迹点开始，调StartNextRecipeSegment设置运动参数
        /// 3. 定时器Tick里的Recipe分支负责更新位置和判断当前段是否完成
        /// 4. 当前段完成后，自动调StartNextRecipeSegment执行下一段
        /// 5. 所有段执行完成后触发RecipeExecutionCompleted事件
        ///
        /// 仿真里是逐段执行，段之间有微小延迟（因为要等一段跑完再设下一段参数）。
        /// 真实硬件用坐标系缓冲区模式，所有段预写入FIFO，硬件自动连续执行，段间无停顿。
        ///
        /// offsetX/offsetY/offsetAngle是偏移参数，用于工件位置补偿，
        /// 但仿真里目前没用到这些偏移（真实硬件的ExecuteRecipe里用了）。
        /// </remarks>
        /// <param name="recipe">涂胶配方，包含轨迹点列表，从配方编辑界面来</param>
        /// <param name="offsetX">X方向偏移（mm），视觉定位算出来的补偿量</param>
        /// <param name="offsetY">Y方向偏移（mm），视觉定位算出来的补偿量</param>
        /// <param name="offsetAngle">角度偏移（度），视觉定位算出来的旋转补偿</param>
        public void ExecuteRecipe(DispensingRecipe recipe, double offsetX, double offsetY, double offsetAngle)
        {
            if (!_isCardOpened || recipe.Points.Count == 0) return;

            _recipePoints = recipe.Points;
            _recipeIndex = 0;
            _recipeStartX = _axisX;
            _recipeStartY = _axisY;
            _mode = MotionMode.Recipe;
            // 清空轨迹，重新记录配方执行轨迹
            _trail.Clear();

            StartNextRecipeSegment();
            _motionTimer.Start();
        }

        /// <summary>
        /// 停止配方执行。
        /// </summary>
        public void StopRecipe()
        {
            _mode = MotionMode.Idle;
            _crdRunning = false;
            _motionTimer.Stop();
        }

        /// <summary>
        /// 停止所有轴运动。调GT_Stop，mask=0x03表示X/Y两轴都停。
        /// </summary>
        public void StopAllAxis()
        {
            GT_Stop(CardNum, 0x03, 0);
        }

        /// <summary>
        /// 强制回原点——不经过运动过程，直接把所有轴位置清零。
        /// </summary>
        /// <remarks>
        /// 跟MoveHome不同，ForceHome是"瞬移"到原点，没有运动过程。
        /// 用途：调试时快速归零、紧急情况下快速复位。
        /// 会清除所有运动状态和轨迹记录。
        /// </remarks>
        public void ForceHome()
        {
            _motionTimer.Stop();
            _mode = MotionMode.Idle;
            // 直接清零所有位置——相当于"上帝之手"把轴搬到原点
            _axisX = 0; _axisY = 0;
            _prfPosX = 0; _prfPosY = 0;
            _encPosX = 0; _encPosY = 0;
            _axisErrorX = 0; _axisErrorY = 0;
            _targetX = 0; _targetY = 0;
            _isHomedX = true; _isHomedY = true;
            _homeRunningX = false; _homeRunningY = false;
            _jogDirX = _jogDirY = 0;
            _jogCurrentVelX = 0; _jogCurrentVelY = 0;
            _currentVel = 0;
            _crdRunning = false;
            _trail.Clear();

            // 通知位置更新——告诉界面"位置变了，刷新一下"
            double displayX = 0;
            double displayY = 0;
            PositionUpdated?.Invoke(displayX, displayY, false);
        }

        /// <summary>
        /// 回原点运动——以梯形速度规划运动到坐标原点(0,0)。
        /// 跟ForceHome不同，这个有真实的运动过程，能看到轴在走。
        /// </summary>
        /// <param name="homeVelocity">回原点速度（mm/s），从MotionParams来</param>
        /// <param name="acceleration">加速度（mm/s²），这里没用，内部写死了250mm/s²</param>
        public void MoveHome(double homeVelocity, double acceleration)
        {
            if (!_isCardOpened) return;

            // 先停止当前运动
            _motionTimer.Stop();
            _mode = MotionMode.Trap;
            _jogDirX = _jogDirY = 0;
            _jogCurrentVelX = 0;
            _jogCurrentVelY = 0;
            _crdRunning = false;
            _homeRunningX = true;
            _homeRunningY = true;

            // 目标位置为原点(0,0)
            _targetX = 0;
            _targetY = 0;
            _startX = _axisX;
            _startY = _axisY;
            double dx = _targetX - _startX;
            double dy = _targetY - _startY;
            _totalDistance = Math.Sqrt(dx * dx + dy * dy);
            _distanceTraveled = 0;
            _currentVel = 0;
            _prevTrailX = _axisX / PulsePerMm;
            _prevTrailY = _axisY / PulsePerMm;

            // 设置回原点速度和加速度
            _interpSpeed = homeVelocity * PulsePerMm;
            _currentAccel = 250 * PulsePerMm; // 写死250mm/s²
            _currentDecel = 250 * PulsePerMm;
            if (_interpSpeed <= 0) _interpSpeed = 5000; // 防止速度为0

            _motionTimer.Start();
        }

        /// <summary>
        /// 紧急停止——立即停掉所有运动，不经过减速过程。
        /// </summary>
        /// <remarks>
        /// 跟GT_Stop的区别：
        /// GT_Stop会把目标位置设为当前位置，然后按正常流程停；
        /// EmergencyStop直接停定时器，所有运动状态立刻清零。
        /// 真实硬件上急停会切断脉冲输出，电机可能突然停住有冲击，只在紧急情况用。
        /// </remarks>
        public void EmergencyStop()
        {
            // 立即停止定时器，不再执行运动计算
            _motionTimer.Stop();
            // 目标位置设为当前位置
            _targetX = _axisX;
            _targetY = _axisY;
            // 清零所有运动方向和速度
            _jogDirX = _jogDirY = 0;
            _jogCurrentVelX = 0;
            _jogCurrentVelY = 0;
            _currentVel = 0;
            _mode = MotionMode.Idle;
            _crdRunning = false;
            _homeRunningX = false;
            _homeRunningY = false;
        }

        /// <summary>
        /// 获取轴状态信息。界面用来显示轴的位置、运动状态、回原点状态等。
        /// </summary>
        /// <param name="axis">轴号（1=X, 2=Y）</param>
        /// <returns>AxisStatusInfo结构体，包含位置(mm)、是否运动、是否回原点、是否报警、是否使能</returns>
        public AxisStatusInfo GetAxisStatus(short axis)
        {
            bool moving = _mode != MotionMode.Idle;
            bool homed = axis == 1 ? _isHomedX : _isHomedY;
            double pos = axis == 1 ? _axisX / PulsePerMm : _axisY / PulsePerMm;
            return new AxisStatusInfo
            {
                AxisIndex = axis,
                Position = pos,
                IsMoving = moving,
                IsHomed = homed,
                HasAlarm = false, // 仿真里永远没报警
                IsEnabled = _isCardOpened
            };
        }

        /// <summary>
        /// 清除运动轨迹记录。界面上的"清轨迹"按钮调这个。
        /// </summary>
        public void ClearTrajectoryTrail()
        {
            _trail.Clear();
        }

        /// <summary>
        /// 启动下一个配方段运动。由定时器Tick在当前段完成后自动调用。
        /// </summary>
        /// <remarks>
        /// 根据当前配方点类型设置运动参数：
        /// - 直线段：计算起点到终点的距离
        /// - 圆弧段：计算圆心、半径、起始角度、扫掠角度
        ///
        /// 圆弧段加速度比直线段低（300 vs 500 mm/s²），因为圆弧运动对加速度更敏感，
        /// 加速度太大会导致圆弧轨迹变形（仿真里看不出来，真实硬件会）。
        ///
        /// 所有配方点执行完毕后，触发RecipeExecutionCompleted事件。
        /// </remarks>
        private void StartNextRecipeSegment()
        {
            // 所有配方点已执行完毕
            if (_recipePoints == null || _recipeIndex >= _recipePoints.Count)
            {
                _mode = MotionMode.Idle;
                _crdRunning = false;
                _motionTimer.Stop();
                RecipeExecutionCompleted?.Invoke(); // 通知上层：配方跑完了
                return;
            }

            var pt = _recipePoints[_recipeIndex];
            _recipeStartX = _axisX;
            _recipeStartY = _axisY;
            _interpProgress = 0;
            _distanceTraveled = 0;
            _currentVel = 0;
            _interpSpeed = pt.Speed * PulsePerMm; // 每个配方点可以有不同的速度
            // 圆弧段用较低加速度，直线段用较高加速度
            _currentAccel = (pt.Type == TrajectoryType.Arc ? 300 : 500) * PulsePerMm;
            _currentDecel = _currentAccel;

            if (pt.Type == TrajectoryType.Arc)
            {
                // 设置圆弧参数
                _arcRadius = pt.ArcRadius * PulsePerMm;
                _arcCenterX = pt.ArcCenterX * PulsePerMm;
                _arcCenterY = pt.ArcCenterY * PulsePerMm;
                // 【踩坑】配方执行中圆弧起始角度也必须从当前轴位置计算，不能硬编码为0
                _arcStartAngle = Math.Atan2(_axisY - _arcCenterY, _axisX - _arcCenterX);

                double desiredSweepRad = pt.ArcAngle * Math.PI / 180.0;
                if (pt.ArcClockwise)
                    _arcSweepAngle = -Math.Abs(desiredSweepRad);
                else
                    _arcSweepAngle = Math.Abs(desiredSweepRad);

                if (Math.Abs(_arcSweepAngle) < 0.0001)
                    _arcSweepAngle = pt.ArcClockwise ? -2 * Math.PI : 2 * Math.PI;

                // 圆弧弧长 = |扫掠角度| × 半径
                _totalDistance = Math.Abs(_arcSweepAngle) * _arcRadius;
            }
            else
            {
                // 直线段：计算起点到终点的距离
                double dx = pt.X * PulsePerMm - _axisX;
                double dy = pt.Y * PulsePerMm - _axisY;
                _totalDistance = Math.Sqrt(dx * dx + dy * dy);
            }
        }

        /// <summary>
        /// 计算梯形速度规划曲线上的当前速度。这是运动控制最核心的算法。
        /// </summary>
        /// <remarks>
        /// 梯形速度规划的5步算法：
        ///
        /// 第1步：算加速段距离
        ///   sAcc = vMax² / (2 × accel)
        ///   从0加速到最大速度需要走多远
        ///
        /// 第2步：算减速段距离
        ///   sDec = vMax² / (2 × decel)
        ///   从最大速度减速到0需要走多远
        ///
        /// 第3步：判断能不能跑到最大速度
        ///   如果 sAcc + sDec > totalDistance，说明距离不够，跑不到最大速度就得开始减速
        ///   这时候变成"三角形速度规划"（没有匀速段），峰值速度要重新算：
        ///   vPeak = √(2 × accel × decel × totalDistance / (accel + decel))
        ///
        /// 第4步：根据已走距离判断当前处于哪个阶段，算出当前速度
        ///   - 加速段（distanceTraveled < sAcc）：v = √(2 × accel × distanceTraveled)
        ///   - 匀速段（sAcc ≤ distanceTraveled < totalDistance - sDec）：v = vPeak
        ///   - 减速段（distanceTraveled ≥ totalDistance - sDec）：v = √(2 × decel × (totalDistance - distanceTraveled))
        ///
        /// 第5步：保证最小启动速度
        ///   速度为0时没法启动（因为加速也是从0开始），所以设个最小速度minStartVel = accel × dt
        ///   只要还在加速段或匀速段，速度不能低于这个值
        ///
        /// dt=0.02是因为定时器周期20ms，每次Tick算一次速度。
        /// </remarks>
        /// <param name="distanceTraveled">已行进距离（脉冲）</param>
        /// <param name="totalDistance">总距离（脉冲）</param>
        /// <param name="maxVel">最大速度（脉冲/s）</param>
        /// <param name="accel">加速度（脉冲/s²）</param>
        /// <param name="decel">减速度（脉冲/s²）</param>
        /// <returns>当前时刻的规划速度（脉冲/s）</returns>
        private double ComputeTrapezoidalVelocity(double distanceTraveled, double totalDistance, double maxVel, double accel, double decel)
        {
            if (totalDistance <= 0) return 0;

            double dt = 0.02; // 定时器周期20ms
            // 最小启动速度——确保运动能启动，不会卡在v=0
            double minStartVel = accel * dt;

            // 第1步：计算加速段距离
            double sAcc = maxVel * maxVel / (2 * accel);
            // 第2步：计算减速段距离
            double sDec = maxVel * maxVel / (2 * decel);
            double vPeak = maxVel;

            // 第3步：距离不够跑到最大速度，降为三角形速度规划
            if (sAcc + sDec > totalDistance)
            {
                vPeak = Math.Sqrt(2 * accel * decel * totalDistance / (accel + decel));
                sAcc = vPeak * vPeak / (2 * accel);
                sDec = vPeak * vPeak / (2 * decel);
            }

            // 第4步：根据已行进距离判断当前阶段并计算速度
            double vel;
            if (distanceTraveled < sAcc)
            {
                // 加速段：v = √(2 × accel × s)，速度随距离平方根增长
                vel = Math.Sqrt(2 * accel * distanceTraveled);
            }
            else if (distanceTraveled < totalDistance - sDec)
            {
                // 匀速段：速度恒定
                vel = vPeak;
            }
            else
            {
                // 减速段：v = √(2 × decel × 剩余距离)，速度随剩余距离平方根减小
                vel = Math.Sqrt(Math.Max(0, 2 * decel * (totalDistance - distanceTraveled)));
            }

            // 第5步：保证最小启动速度，避免v=0导致无法启动
            if (vel < minStartVel && distanceTraveled < totalDistance - sDec)
                vel = minStartVel;

            return vel;
        }

        /// <summary>
        /// 运动定时器回调——每20ms执行一次，驱动所有运动模式的仿真计算。
        /// 这是整个仿真器的心脏，所有运动都在这里算。
        /// </summary>
        /// <remarks>
        /// 工作流程：
        /// 1. 根据当前运动模式（_mode）进入不同的switch分支
        /// 2. 在对应分支里更新轴位置
        /// 3. 判断运动是否完成
        /// 4. 同步更新规划位置、编码器位置、状态字
        /// 5. 检查软限位
        /// 6. 发布位置更新事件
        ///
        /// dt=0.02是定时器周期，所有速度/加速度计算都基于这个时间步长。
        /// </remarks>
        /// <param name="sender">事件源</param>
        /// <param name="e">事件参数</param>
        private void MotionTimer_Tick(object sender, EventArgs e)
        {
            double dt = 0.02; // 定时器周期20ms

            switch (_mode)
            {
                case MotionMode.Jog:
                    {
                        // === JOG模式：按指定方向持续运动，支持加减速过渡 ===

                        // X轴JOG速度控制
                        if (_jogDirX != 0)
                        {
                            // 有方向指令：加速到目标速度
                            // targetVel = 方向 × 目标速度，正负号代表方向
                            double targetVel = _jogDirX * _jogVelocity * PulsePerMm;
                            // 计算速度增量：方向 × 加速度 × 时间步长
                            double dv = Math.Sign(targetVel - _jogCurrentVelX) * _jogAccel * dt;
                            // 如果增量超过剩余差值，直接设为目标速度（防止过冲）
                            if (Math.Abs(dv) >= Math.Abs(targetVel - _jogCurrentVelX))
                                _jogCurrentVelX = targetVel;
                            else
                                _jogCurrentVelX += dv;
                        }
                        else if (Math.Abs(_jogCurrentVelX) > 0)
                        {
                            // 无方向指令：减速到0
                            double dv = -Math.Sign(_jogCurrentVelX) * _jogDecel * dt;
                            if (Math.Abs(dv) >= Math.Abs(_jogCurrentVelX))
                                _jogCurrentVelX = 0;
                            else
                                _jogCurrentVelX += dv;
                        }

                        // Y轴JOG速度控制（逻辑同X轴）
                        if (_jogDirY != 0)
                        {
                            double targetVel = _jogDirY * _jogVelocity * PulsePerMm;
                            double dv = Math.Sign(targetVel - _jogCurrentVelY) * _jogAccel * dt;
                            if (Math.Abs(dv) >= Math.Abs(targetVel - _jogCurrentVelY))
                                _jogCurrentVelY = targetVel;
                            else
                                _jogCurrentVelY += dv;
                        }
                        else if (Math.Abs(_jogCurrentVelY) > 0)
                        {
                            double dv = -Math.Sign(_jogCurrentVelY) * _jogDecel * dt;
                            if (Math.Abs(dv) >= Math.Abs(_jogCurrentVelY))
                                _jogCurrentVelY = 0;
                            else
                                _jogCurrentVelY += dv;
                        }

                        // 根据当前速度更新位置：位置 += 速度 × 时间步长
                        _axisX += _jogCurrentVelX * dt;
                        _axisY += _jogCurrentVelY * dt;
                        // JOG没有固定目标位置，目标位置跟着当前位置走
                        _targetX = _axisX;
                        _targetY = _axisY;
                        AddTrailPoint(_glueOutputOn);

                        // 所有轴方向为0且速度降为0时，切换到空闲模式
                        if (_jogDirX == 0 && _jogDirY == 0
                            && Math.Abs(_jogCurrentVelX) < 1.0 && Math.Abs(_jogCurrentVelY) < 1.0)
                        {
                            _jogCurrentVelX = 0;
                            _jogCurrentVelY = 0;
                            _mode = MotionMode.Idle;
                            _motionTimer.Stop();
                        }
                    }
                    break;

                case MotionMode.Trap:
                    {
                        // === 梯形速度点位运动模式 ===

                        if (_totalDistance > 1.0)
                        {
                            // 用梯形速度规划算当前速度
                            double vel = ComputeTrapezoidalVelocity(_distanceTraveled, _totalDistance, _interpSpeed, _currentAccel, _currentDecel);
                            _currentVel = vel;
                            // 本周期行进距离 = 速度 × 时间步长
                            double ds = vel * dt;
                            _distanceTraveled += ds;
                            if (_distanceTraveled >= _totalDistance)
                            {
                                // 到达目标位置——直接设为目标位置，消除累积误差
                                _distanceTraveled = _totalDistance;
                                _axisX = _targetX;
                                _axisY = _targetY;
                            }
                            else
                            {
                                // 按进度线性插值位置
                                double progress = _distanceTraveled / _totalDistance;
                                _axisX = _startX + (_targetX - _startX) * progress;
                                _axisY = _startY + (_targetY - _startY) * progress;
                            }
                        }
                        else
                        {
                            // 距离极小（<1脉冲），直接到达目标位置
                            _axisX = _targetX;
                            _axisY = _targetY;
                        }

                        // 检测回原点完成：位置接近0且目标也是0
                        if (Math.Abs(_axisX) < 10 && Math.Abs(_targetX) < 10)
                        {
                            _isHomedX = true;
                            _homeRunningX = false;
                        }
                        if (Math.Abs(_axisY) < 10 && Math.Abs(_targetY) < 10)
                        {
                            _isHomedY = true;
                            _homeRunningY = false;
                        }

                        AddTrailPoint(_glueOutputOn);

                        // 运动完成检测
                        if (_distanceTraveled >= _totalDistance || _totalDistance <= 1.0)
                        {
                            _axisX = _targetX;
                            _axisY = _targetY;
                            _currentVel = 0;
                            _mode = MotionMode.Idle;
                            _motionTimer.Stop();

                            // 回原点完成后的处理
                            if (_homeRunningX || _homeRunningY)
                            {
                                _homeRunningX = false;
                                _homeRunningY = false;
                                _isHomedX = true;
                                _isHomedY = true;
                                _trail.Clear(); // 回原点完成后清轨迹
                                PositionUpdated?.Invoke(0, 0, false);
                            }
                        }
                    }
                    break;

                case MotionMode.LinearInterp:
                    {
                        // === 直线插补模式：双轴联动走直线 ===

                        if (_totalDistance > 1.0)
                        {
                            double vel = ComputeTrapezoidalVelocity(_distanceTraveled, _totalDistance, _interpSpeed, _currentAccel, _currentDecel);
                            _currentVel = vel;
                            double ds = vel * dt;
                            _distanceTraveled += ds;
                            // 计算插补进度
                            _interpProgress = Math.Min(_distanceTraveled / _totalDistance, 1.0);
                            // 按进度线性插值X/Y位置
                            _axisX = _startX + (_endX - _startX) * _interpProgress;
                            _axisY = _startY + (_endY - _startY) * _interpProgress;
                        }
                        else
                        {
                            _axisX = _endX;
                            _axisY = _endY;
                            _interpProgress = 1;
                        }

                        AddTrailPoint(_glueOutputOn);

                        // 直线插补完成
                        if (_interpProgress >= 1)
                        {
                            _axisX = _endX;
                            _axisY = _endY;
                            _currentVel = 0;
                            _mode = MotionMode.Idle;
                            _crdRunning = false;
                            _motionTimer.Stop();
                        }
                    }
                    break;

                case MotionMode.ArcApproach:
                    {
                        // === 圆弧接近模式（已弃用）：先直线移动到圆弧起点 ===
                        // 这个模式是早期设计，后来发现不需要，直接从当前位置开始圆弧就行

                        if (_totalDistance > 1.0)
                        {
                            double vel = ComputeTrapezoidalVelocity(_distanceTraveled, _totalDistance, _interpSpeed, _currentAccel, _currentDecel);
                            _currentVel = vel;
                            double ds = vel * dt;
                            _distanceTraveled += ds;
                            _interpProgress = Math.Min(_distanceTraveled / _totalDistance, 1.0);
                            _axisX = _startX + (_endX - _startX) * _interpProgress;
                            _axisY = _startY + (_endY - _startY) * _interpProgress;
                        }
                        else
                        {
                            _axisX = _endX;
                            _axisY = _endY;
                            _interpProgress = 1;
                        }

                        AddTrailPoint(_glueOutputOn);

                        // 接近完成后切换到圆弧运动
                        if (_interpProgress >= 1)
                        {
                            _axisX = _endX;
                            _axisY = _endY;
                            StartArcMotion();
                        }
                    }
                    break;

                case MotionMode.ArcInterp:
                    {
                        // === 圆弧插补模式：双轴联动走圆弧 ===
                        // 用角度插值法：根据进度算当前角度，再算X/Y坐标

                        if (_totalDistance > 1.0)
                        {
                            double vel = ComputeTrapezoidalVelocity(_distanceTraveled, _totalDistance, _interpSpeed, _currentAccel, _currentDecel);
                            _currentVel = vel;
                            double ds = vel * dt;
                            _distanceTraveled += ds;
                            _interpProgress = Math.Min(_distanceTraveled / _totalDistance, 1.0);
                            // 当前角度 = 起始角度 + 扫掠角度 × 进度
                            double newAngle = _arcStartAngle + _arcSweepAngle * _interpProgress;
                            // 在角度区间内添加圆弧轨迹点（提高轨迹显示精度）
                            AddArcAngleTrailPoints(_arcStartAngle + _arcSweepAngle * ((_distanceTraveled - ds) / _totalDistance), newAngle, _glueOutputOn);
                            // 根据角度计算圆弧上的X/Y坐标
                            _axisX = _arcCenterX + _arcRadius * Math.Cos(newAngle);
                            _axisY = _arcCenterY + _arcRadius * Math.Sin(newAngle);
                        }
                        else
                        {
                            // 距离极小，直接到达终点角度
                            double finalAngle = _arcStartAngle + _arcSweepAngle;
                            _axisX = _arcCenterX + _arcRadius * Math.Cos(finalAngle);
                            _axisY = _arcCenterY + _arcRadius * Math.Sin(finalAngle);
                            _interpProgress = 1;
                        }

                        // 圆弧插补完成
                        if (_interpProgress >= 1)
                        {
                            double finalAngle2 = _arcStartAngle + _arcSweepAngle;
                            _axisX = _arcCenterX + _arcRadius * Math.Cos(finalAngle2);
                            _axisY = _arcCenterY + _arcRadius * Math.Sin(finalAngle2);
                            _currentVel = 0;
                            _mode = MotionMode.Idle;
                            _crdRunning = false;
                            _motionTimer.Stop();
                        }
                    }
                    break;

                case MotionMode.Recipe:
                    {
                        // === 配方执行模式：按轨迹点序列依次执行 ===

                        if (_recipePoints == null || _recipeIndex >= _recipePoints.Count)
                        {
                            _mode = MotionMode.Idle;
                            _crdRunning = false;
                            _currentVel = 0;
                            _motionTimer.Stop();
                            RecipeExecutionCompleted?.Invoke();
                            break;
                        }

                        var pt = _recipePoints[_recipeIndex];
                        // 通知涂胶状态变化——界面上的涂胶指示灯跟着变
                        GlueStateChanged?.Invoke(pt.GlueOn);
                        double recipeSpeed = pt.Speed * PulsePerMm;

                        if (_totalDistance > 1.0)
                        {
                            double vel = ComputeTrapezoidalVelocity(_distanceTraveled, _totalDistance, recipeSpeed, _currentAccel, _currentDecel);
                            _currentVel = vel;
                            double ds = vel * dt;
                            _distanceTraveled += ds;
                            _interpProgress = Math.Min(_distanceTraveled / _totalDistance, 1.0);

                            if (pt.Type == TrajectoryType.Arc)
                            {
                                // 圆弧段：根据角度计算位置
                                double newAngle = _arcStartAngle + _arcSweepAngle * _interpProgress;
                                AddArcAngleTrailPoints(_arcStartAngle + _arcSweepAngle * ((_distanceTraveled - ds) / _totalDistance), newAngle, pt.GlueOn);
                                _axisX = _arcCenterX + _arcRadius * Math.Cos(newAngle);
                                _axisY = _arcCenterY + _arcRadius * Math.Sin(newAngle);
                            }
                            else
                            {
                                // 直线段：从配方起点到当前点线性插值
                                _axisX = _recipeStartX + (pt.X * PulsePerMm - _recipeStartX) * _interpProgress;
                                _axisY = _recipeStartY + (pt.Y * PulsePerMm - _recipeStartY) * _interpProgress;
                            }
                        }
                        else _interpProgress = 1;

                        AddTrailPoint(pt.GlueOn);

                        // 当前段执行完成，切换到下一段
                        if (_interpProgress >= 1)
                        {
                            _recipeIndex++;
                            _crdSegment++;
                            if (_recipeIndex >= _recipePoints.Count)
                            {
                                // 所有段执行完成
                                _mode = MotionMode.Idle;
                                _crdRunning = false;
                                _currentVel = 0;
                                _motionTimer.Stop();
                                RecipeExecutionCompleted?.Invoke();
                            }
                            else
                            {
                                // 执行下一段
                                StartNextRecipeSegment();
                            }
                        }
                    }
                    break;
            }

            // 同步更新规划位置和编码器位置
            // 仿真里没有跟随误差，所以这三个值永远相等
            _prfPosX = _axisX;
            _prfPosY = _axisY;
            _encPosX = _axisX;
            _encPosY = _axisY;
            _axisErrorX = 0;
            _axisErrorY = 0;

            // 更新规划速度和轴状态字
            if (_mode != MotionMode.Idle)
            {
                if (_mode == MotionMode.Jog)
                {
                    // JOG模式：各轴速度独立
                    _prfVelX = Math.Abs(_jogCurrentVelX);
                    _prfVelY = Math.Abs(_jogCurrentVelY);
                }
                else
                {
                    // 其他模式：两轴速度一样（合成速度）
                    _prfVelX = _currentVel;
                    _prfVelY = _currentVel;
                }
                // 设置运动中标志位（bit10 = 0x400）——固高GTS800官方定义
                _axisStsX |= 0x400;
                _axisStsY |= 0x400;
            }
            else
            {
                _prfVelX = 0;
                _prfVelY = 0;
                // 清除运动中标志位
                _axisStsX &= ~0x400;
                _axisStsY &= ~0x400;
            }

            // 软限位检查——超限就强制停
            bool hitLimit = false;
            if (_lmtsOnX)
            {
                if (_axisX > _softLimitPosX) { _axisX = _softLimitPosX; hitLimit = true; }
                if (_axisX < _softLimitNegX) { _axisX = _softLimitNegX; hitLimit = true; }
            }
            if (_lmtsOnY)
            {
                if (_axisY > _softLimitPosY) { _axisY = _softLimitPosY; hitLimit = true; }
                if (_axisY < _softLimitNegY) { _axisY = _softLimitNegY; hitLimit = true; }
            }

            // 触发软限位时立即停止所有运动
            if (hitLimit && _mode != MotionMode.Idle)
            {
                _mode = MotionMode.Idle;
                _jogDirX = _jogDirY = 0;
                _crdRunning = false;
                _homeRunningX = false;
                _homeRunningY = false;
                _motionTimer.Stop();
            }

            // 发布位置更新事件——脉冲转mm，给界面显示用
            double displayX = _axisX / PulsePerMm;
            double displayY = _axisY / PulsePerMm;

            // PositionUpdated事件：MainViewModel订阅，用来更新坐标显示
            PositionUpdated?.Invoke(displayX, displayY, _mode != MotionMode.Idle);
        }

        /// <summary>
        /// 添加轨迹记录点。位置变化超过阈值才添加，避免重复点占内存。
        /// </summary>
        /// <param name="glueOn">当前涂胶状态——轨迹点会标记是否涂胶，界面用不同颜色画线</param>
        private void AddTrailPoint(bool glueOn)
        {
            double displayX = _axisX / PulsePerMm;
            double displayY = _axisY / PulsePerMm;
            // 计算与上一个记录点的距离
            double dx = displayX - _prevTrailX;
            double dy = displayY - _prevTrailY;
            // 距离超过0.01mm或轨迹为空时添加新点
            if (dx * dx + dy * dy > 0.0001 || _trail.Count == 0)
            {
                _trail.Add((displayX, displayY, glueOn));
                _prevTrailX = displayX;
                _prevTrailY = displayY;
                // 超过最大轨迹点数时删除最早的点
                if (_trail.Count > MaxTrailSize)
                    _trail.RemoveRange(0, _trail.Count - MaxTrailSize);
            }
        }

        /// <summary>
        /// 在圆弧角度区间内添加多个轨迹点，提高圆弧轨迹显示精度。
        /// </summary>
        /// <remarks>
        /// 为啥需要这个？因为定时器周期20ms比较大，高速圆弧运动时每周期角度变化很大，
        /// 如果只记录起止点，界面画出来的轨迹就是折线而不是圆弧。
        /// 所以在角度区间内均匀插入中间点，让轨迹看起来更圆滑。
        ///
        /// 步长约0.05弧度（约2.9度），最多50个中间点。
        /// 这个精度对界面显示来说足够了，再细也看不出区别。
        /// </remarks>
        /// <param name="fromAngle">起始角度（弧度）</param>
        /// <param name="toAngle">终止角度（弧度）</param>
        /// <param name="glueOn">当前涂胶状态</param>
        private void AddArcAngleTrailPoints(double fromAngle, double toAngle, bool glueOn)
        {
            double angleSpan = toAngle - fromAngle;
            // 计算中间点数量，步长约0.05弧度
            int steps = Math.Max(1, (int)(Math.Abs(angleSpan) / 0.05));
            if (steps > 50) steps = 50; // 最多50个点，防止圆弧太大时点太多
            for (int i = 1; i <= steps; i++)
            {
                double t = (double)i / steps;
                double angle = fromAngle + angleSpan * t;
                // 计算圆弧上的坐标并转换为mm
                double ix = (_arcCenterX + _arcRadius * Math.Cos(angle)) / PulsePerMm;
                double iy = (_arcCenterY + _arcRadius * Math.Sin(angle)) / PulsePerMm;
                // 去重：距离阈值0.01mm
                double ddx = ix - _prevTrailX;
                double ddy = iy - _prevTrailY;
                if (ddx * ddx + ddy * ddy > 0.0001 || _trail.Count == 0)
                {
                    _trail.Add((ix, iy, glueOn));
                    _prevTrailX = ix;
                    _prevTrailY = iy;
                    if (_trail.Count > MaxTrailSize)
                        _trail.RemoveRange(0, _trail.Count - MaxTrailSize);
                }
            }
        }

        /// <summary>
        /// 释放资源，关闭运动控制卡。
        /// </summary>
        public void Dispose()
        {
            GT_Close(CardNum);
        }
    }
}
