using System.Windows.Threading;
using WPADC.Models;
using WPADC.Services.Interfaces;

namespace WPADC.Services
{
    /// <summary>
    /// 固高GTS800运动控制卡的真实硬件实现类。
    /// </summary>
    /// <remarks>
    /// ============================================================
    /// 【仅供学习参考——本类不参与项目任何功能！】
    /// ============================================================
    /// 当前项目用的是仿真模式（MotionSimulatorService），这个类只是对照参考，
    /// 让你看看真实硬件跟仿真有啥区别。如果想切到真实硬件，
    /// 在DI容器里把MotionSimulatorService替换成MotionRealService就行。
    ///
    /// 跟仿真模式的核心区别，一定要搞清楚：
    ///
    /// 1. 运动驱动方式不同
    ///    仿真：DispatcherTimer 20ms驱动，软件算位置
    ///    真实：硬件DSP发脉冲驱动电机，软件只管下指令和读状态
    ///
    /// 2. 位置获取方式不同
    ///    仿真：内部变量直接就是位置，没有误差
    ///    真实：从编码器读位置，规划位置和编码器位置之间有跟随误差
    ///
    /// 3. 配方执行方式不同
    ///    仿真：逐段执行，一段跑完再设下一段参数（段间有微小停顿）
    ///    真实：坐标系缓冲区模式，所有段预写入FIFO，硬件自动连续执行（段间无停顿）
    ///
    /// 4. 参数单位不同
    ///    仿真：上层接口用mm，内部也用mm（乘除PulsePerMm只是走个形式）
    ///    真实：上层接口用mm，GTS800 API用脉冲，必须严格做单位转换
    ///
    /// 5. 错误处理不同
    ///    仿真：所有GT_XXX方法返回0（永远成功）
    ///    真实：可能返回各种错误码，必须检查返回值
    /// ============================================================
    /// </remarks>
    public class MotionRealService : IMotionService
    {
        #region 常量定义

        /// <summary>
        /// 卡号，GTS800默认用1。
        /// 注意：仿真器里用0，真实SDK里用1，这是固高的规定，别搞混了。
        /// </summary>
        private const short CardNum = 1;

        /// <summary>
        /// 脉冲当量：1mm = 1000脉冲。
        /// 跟仿真器保持一致，所有mm↔脉冲转换都用这个值。
        /// 真实环境里这个值取决于丝杠导程和编码器线数，可能不是1000，
        /// 但当前硬件配置就是1000，改了的话位置全错。
        /// </summary>
        private const double PulsePerMm = 1000.0;

        /// <summary>
        /// 涂胶DO输出类型编号，对应固高GTS的MC_GPO(12)通道类型。
        /// 这是固高官方定义的常量，12=通用数字输出，别乱改。
        /// </summary>
        private const short GlueDoType = 12;

        /// <summary>
        /// 涂胶DO输出位索引，第0个DO位。也是固高官方定义的。
        /// </summary>
        private const short GlueDoIndex = 0;

        /// <summary>
        /// X轴轴号。GTS800轴号从1开始，1=X轴——固高官方规定。
        /// </summary>
        private const short AxisX = 1;

        /// <summary>
        /// Y轴轴号。2=Y轴。
        /// </summary>
        private const short AxisY = 2;

        /// <summary>
        /// X轴规划轴号。GTS800中profile号通常跟轴号一一对应，1=X。
        /// </summary>
        private const short ProfileX = 1;

        /// <summary>
        /// Y轴规划轴号。2=Y。
        /// </summary>
        private const short ProfileY = 2;

        /// <summary>
        /// 坐标系编号。GTS800支持2个坐标系，这里用第1个。
        /// 插补运动必须在坐标系里执行。
        /// </summary>
        private const short CrdIndex = 1;

        /// <summary>
        /// 缓冲区编号。GTS800每个坐标系支持2个FIFO缓冲区，用0号。
        /// 缓冲区模式就是先把运动段写入FIFO，再一次性启动执行。
        /// </summary>
        private const short Fifo = 0;

        /// <summary>
        /// 位置轮询周期20ms。跟仿真器的定时器周期保持一致。
        /// 这个定时器只负责读位置和状态，不管运动计算——运动由硬件DSP驱动。
        /// </summary>
        private const int PollingIntervalMs = 20;

        #endregion

        #region 私有字段

        /// <summary>
        /// 日志服务，DI容器注入的。用来记录系统状态信息。
        /// </summary>
        private readonly ILoggerService _loggerService;

        /// <summary>
        /// 位置轮询定时器。
        /// 【跟仿真器的核心区别】
        /// 仿真器的DispatcherTimer既驱动运动计算又更新位置——它是"心脏"；
        /// 真实硬件的运动由DSP发脉冲驱动，这个定时器只是"眼睛"——定期看看位置到哪了。
        /// </summary>
        private readonly DispatcherTimer _pollingTimer;

        /// <summary>控制卡是否已打开</summary>
        private bool _isCardOpened;

        /// <summary>X/Y轴是否已完成回原点。轮询定时器里检测到bit12后设true</summary>
        private bool _isHomedX, _isHomedY;

        /// <summary>涂胶输出是否开启</summary>
        private bool _glueOutputOn;

        /// <summary>是否正在执行配方。ExecuteRecipe设true，配方完成或StopRecipe设false</summary>
        private bool _isExecutingRecipe;

        /// <summary>X轴正/负软限位（脉冲）。默认±50000=±50mm</summary>
        private int _softLimitPosX = 50000, _softLimitNegX = -50000;

        /// <summary>Y轴正/负软限位（脉冲）</summary>
        private int _softLimitPosY = 50000, _softLimitNegY = -50000;

        /// <summary>X/Y轴软限位是否启用</summary>
        private bool _lmtsOnX, _lmtsOnY;

        /// <summary>
        /// 运动轨迹点列表。仿真器在运动过程中实时记录轨迹；
        /// 真实硬件通过轮询定时器读取位置来记录轨迹，精度受轮询周期限制。
        /// </summary>
        private readonly List<(double x, double y, bool glueOn)> _trail = new();

        /// <summary>轨迹点最大数量限制，跟仿真器一样10万</summary>
        private const int MaxTrailSize = 100000;

        /// <summary>上一个轨迹记录点的X/Y坐标（mm），用于去重</summary>
        private double _prevTrailX, _prevTrailY;

        /// <summary>配方轨迹点列表。ExecuteRecipe传进来的</summary>
        private List<TrajectoryPoint>? _recipePoints;

        /// <summary>
        /// 当前执行的配方点索引。
        /// 【注意】真实硬件用缓冲区模式，所有段一次性写入FIFO，不需要逐段索引。
        /// 这个字段只是保留着，万一以后要改成逐段模式可以用。
        /// </summary>
#pragma warning disable CS0414 // 字段已赋值但从未使用
        private int _recipeIndex;
#pragma warning restore CS0414

        #endregion

        #region 属性

        /// <summary>控制卡是否已打开</summary>
        public bool IsCardOpened => _isCardOpened;

        /// <summary>
        /// 是否正在运动中。
        /// 【跟仿真器的区别】仿真器看内部_mode枚举；真实硬件读轴状态字的bit10。
        /// bit10(0x400)=运动中标志，固高GTS800官方定义。
        /// </summary>
        public bool IsMoving
        {
            get
            {
                if (!_isCardOpened) return false;
                // 读X轴和Y轴状态字，看有没有轴在动
                short ret = Gts.GT_GetSts(CardNum, AxisX, out int stsX, 1, out uint _);
                if (ret != 0) return false;
                ret = Gts.GT_GetSts(CardNum, AxisY, out int stsY, 1, out _);
                if (ret != 0) return false;
                return (stsX & 0x400) != 0 || (stsY & 0x400) != 0;
            }
        }

        /// <summary>是否正在执行配方</summary>
        public bool IsExecutingRecipe => _isExecutingRecipe;

        /// <summary>涂胶输出开关状态</summary>
        public bool GlueOutputOn { get => _glueOutputOn; set => _glueOutputOn = value; }

        /// <summary>运动轨迹点列表，供界面绑定显示</summary>
        public List<(double x, double y, bool glueOn)> TrajectoryTrail => _trail;

        #endregion

        #region 事件

        /// <summary>
        /// 位置更新事件。参数：(X坐标mm, Y坐标mm, 是否运动中)
        /// 【跟仿真器的区别】仿真器在MotionTimer_Tick里算完位置就触发；
        /// 真实硬件在_pollingTimer里读完编码器位置后触发。
        /// 订阅者一样：MainViewModel用来更新界面坐标显示。
        /// </summary>
        public event Action<double, double, bool>? PositionUpdated;

        /// <summary>
        /// 配方执行完成事件。
        /// 轮询定时器检测到坐标系运动停止后触发。
        /// </summary>
        public event Action? RecipeExecutionCompleted;

        /// <summary>
        /// 涂胶状态变化事件。参数：涂胶是否开启。
        /// 真实硬件里涂胶状态由GT_BufIO在缓冲区里控制，软件层面不好感知，
        /// 所以这个事件在真实硬件里可能不太准。
        /// </summary>
        public event Action<bool>? GlueStateChanged;

        #endregion

        #region 构造函数

        /// <summary>
        /// 构造函数，初始化真实运动控制服务。
        /// </summary>
        /// <param name="loggerService">日志服务，DI容器注入的</param>
        /// <remarks>
        /// 【跟仿真器的区别】
        /// 仿真器在构造函数里初始化JOG/Trap参数和软限位；
        /// 真实硬件这些参数在OpenCard时通过GT_LoadConfig加载配置文件设置。
        /// 这里只创建轮询定时器，不调任何GTS API——因为卡还没打开呢。
        /// </remarks>
        public MotionRealService(ILoggerService loggerService)
        {
            _loggerService = loggerService;

            // 创建位置轮询定时器
            // 仿真器：定时器驱动运动计算 + 位置更新（心脏）
            // 真实硬件：运动由脉冲发生器驱动，定时器只轮询位置（眼睛）
            _pollingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PollingIntervalMs) };
            _pollingTimer.Tick += PollingTimer_Tick;
        }

        #endregion

        #region IMotionService 实现 - 卡片操作

        /// <summary>
        /// 打开运动控制卡（简化接口）。
        /// </summary>
        /// <returns>连接结果消息</returns>
        /// <remarks>
        /// GTS800打开卡的标准流程，一步都不能少：
        ///
        /// 1. GT_Open(1, 0, 0) —— 跟PCI总线上的GTS800卡建立通信
        ///    卡号1，通道0，参数0（都是固高规定的默认值）
        ///
        /// 2. GT_Reset(1) —— 复位卡片，清除所有运动状态
        ///    必须在GT_Open之后、任何运动命令之前调用
        ///
        /// 3. GT_LoadConfig(1, "GTS800.cfg") —— 加载配置文件
        ///    配置文件包含：轴PID参数、编码器参数、限位IO映射、脉冲输出模式等
        ///    不加载配置文件轴没法正常运动！
        ///    配置文件通常跟gts.dll在同一目录
        ///
        /// 4. GT_ClrSts(1, 1, 2) —— 清除X/Y轴状态字
        ///    清掉上电后的残留报警、限位等状态
        ///
        /// 5. GT_AxisOn(1, 1) / GT_AxisOn(1, 2) —— 使能X/Y轴伺服
        ///    使能后伺服电机通电，轴锁定，才能接受运动命令
        ///
        /// 6. GT_ZeroPos(1, 1, 1) / GT_ZeroPos(1, 2, 1) —— 清零X/Y轴位置
        ///    把当前位置定义为0（不是真的移动，只是把计数器清零）
        ///
        /// 【跟仿真器的区别】
        /// 仿真器GT_Open只设_isCardOpened=true和重置内部变量；
        /// 真实硬件要跟PCI总线上的GTS800卡建立通信，加载硬件配置。
        /// </remarks>
        public string OpenCard()
        {
            // 步骤1：打开控制卡
            // 返回值：0=成功，非0=错误码（比如卡没插好、驱动没装等）
            short ret = Gts.GT_Open(CardNum, 0, 0);
            if (ret != 0)
                return $"运动控制卡打开失败，错误码：{ret}";

            // 步骤2：复位控制卡
            ret = Gts.GT_Reset(CardNum);
            if (ret != 0)
                return $"运动控制卡复位失败，错误码：{ret}";

            // 步骤3：加载配置文件
            // 【注意】部署时确保GTS800.cfg文件在正确路径
            ret = Gts.GT_LoadConfig(CardNum, "GTS800.cfg");
            if (ret != 0)
            {
                // 配置文件加载失败，但不阻止继续——某些参数可以手动设
            }

            // 步骤4：清除X/Y轴状态字
            // axis=1, count=2 表示从轴1开始清除2个轴
            Gts.GT_ClrSts(CardNum, AxisX, 2);

            // 步骤5：使能X/Y轴伺服——使能后电机通电，轴锁定
            Gts.GT_AxisOn(CardNum, AxisX);
            Gts.GT_AxisOn(CardNum, AxisY);

            // 步骤6：清零X/Y轴位置——把当前位置定义为0
            Gts.GT_ZeroPos(CardNum, AxisX, 1);
            Gts.GT_ZeroPos(CardNum, AxisY, 1);

            _isCardOpened = true;
            _isHomedX = false;
            _isHomedY = false;

            // 启动位置轮询定时器
            _pollingTimer.Start();

            _loggerService.LogInfo("运动控制卡(真实)已连接");

            return "运动控制卡(真实)连接成功";
        }

        /// <summary>
        /// 关闭运动控制卡。释放硬件资源，停止轮询。
        /// </summary>
        /// <remarks>
        /// GT_Close(1) 关闭跟GTS800卡的通信，释放PCI总线资源。
        /// 关闭后所有轴伺服自动下使能，电机断电，轴自由。
        /// </remarks>
        public void CloseCard()
        {
            if (!_isCardOpened) return;

            _pollingTimer.Stop(); // 先停轮询

            Gts.GT_Close(CardNum); // 关闭硬件通信

            _isCardOpened = false;
            _isHomedX = false;
            _isHomedY = false;
        }

        #endregion

        #region IMotionService 实现 - 回原点

        /// <summary>
        /// 所有轴回原点。构造回原点参数后调GT_GoHome，由硬件DSP自动执行。
        /// </summary>
        /// <param name="param">运动参数，从配置文件加载的</param>
        /// <remarks>
        /// GTS800回原点模式（固高官方定义）：
        /// - HOME_MODE_LIMIT(10)：限位回零——先找限位开关再回零
        /// - HOME_MODE_HOME(20)：原点开关回零——找原点信号
        /// - HOME_MODE_INDEX(30)：编码器索引回零——找Z相信号
        /// - 组合模式：如HOME_MODE_LIMIT_HOME(11)先找限位再找原点
        ///
        /// 回原点参数说明：
        /// - mode：回原点模式（我们用限位回零）
        /// - moveDir：搜索方向（1=正向，-1=负向）
        /// - velHigh：高速搜索速度（脉冲/s）——快速接近原点
        /// - velLow：低速搜索速度（脉冲/s）——接近原点时降速，防止冲过头
        /// - acc/dec：加/减速度
        /// - homeOffset：原点偏移量（脉冲），回零完成后自动移动到偏移位置
        /// - searchHomeDistance：搜索原点距离（脉冲），超过这个距离还没找到就报错
        /// - searchIndexDistance：搜索索引距离（脉冲）
        ///
        /// 【跟仿真器的区别】
        /// 仿真器GT_GoHome就是走到(0,0)；真实硬件启动后由DSP自动执行回原点流程，
        /// 软件通过轮询GT_GetSts的bit12(回原点完成标志)来等待完成。
        /// </remarks>
        public void HomeAllAxis(MotionParams param)
        {
            if (!_isCardOpened) return;

            // 构造回原点参数
            var homePrm = new Gts.THomePrm
            {
                mode = Gts.HOME_MODE_LIMIT,           // 限位回零模式
                moveDir = param.HomeDirection,         // 搜索方向
                indexDir = 1,                          // 编码器索引搜索方向
                edge = 0,                              // 下降沿触发
                triggerIndex = 0,                      // 触发索引
                velHigh = param.HomeVelocity * PulsePerMm,  // 高速搜索速度（mm→脉冲/s）
                velLow = param.HomeVelocity * PulsePerMm * 0.2, // 低速=高速的20%
                acc = param.Acceleration,              // 加速度
                dec = param.Acceleration,              // 减速度
                smoothTime = 25,                       // 平滑时间25ms
                homeOffset = 0,                        // 原点偏移为0
                searchHomeDistance = 500000,           // 搜索原点距离50万脉冲
                searchIndexDistance = 500000           // 搜索索引距离50万脉冲
            };

            // 清除轴状态字
            Gts.GT_ClrSts(CardNum, AxisX, 2);

            // 启动X轴回原点——GT_GoHome启动后由硬件DSP自动执行
            short ret = Gts.GT_GoHome(CardNum, AxisX, ref homePrm);
            if (ret != 0) return;

            // 启动Y轴回原点
            ret = Gts.GT_GoHome(CardNum, AxisY, ref homePrm);
            if (ret != 0) return;

            // 回原点完成状态通过轮询定时器检测
            // 检测到bit12（0x1000）后设置_isHomedX/Y = true
        }

        /// <summary>
        /// 强制回原点——停止所有运动，清零位置。没有运动过程，瞬间归零。
        /// </summary>
        /// <remarks>
        /// 【跟仿真器的区别】
        /// 仿真器直接设内部变量为0；真实硬件要调GT_Stop停运动、GT_ZeroPos清位置。
        /// </remarks>
        public void ForceHome()
        {
            if (!_isCardOpened) return;

            // 停止所有轴运动
            // mask=0x03：X轴(bit0)和Y轴(bit1)
            // option=0：平滑停止
            Gts.GT_Stop(CardNum, 0x03, 0);

            // 清零X/Y轴位置——把当前位置定义为原点
            Gts.GT_ZeroPos(CardNum, AxisX, 2);

            _isHomedX = true;
            _isHomedY = true;
            _isExecutingRecipe = false;
            _trail.Clear();

            // 通知位置更新
            PositionUpdated?.Invoke(0, 0, false);
        }

        /// <summary>
        /// 回原点运动——用梯形速度规划走到(0,0)。
        /// </summary>
        /// <param name="homeVelocity">回原点速度（mm/s）</param>
        /// <param name="acceleration">加速度（mm/s²）</param>
        /// <remarks>
        /// GTS800 Trap运动的标准流程（5步）：
        /// 1. GT_PrfTrap(1, profile) —— 设置规划模式为梯形速度
        /// 2. GT_SetTrapPrm(1, profile, ref trapPrm) —— 设置梯形速度参数
        /// 3. GT_SetPos(1, profile, pos) —— 设置目标位置（脉冲）
        /// 4. GT_SetVel(1, profile, vel) —— 设置目标速度（脉冲/s）
        /// 5. GT_Update(1, 1<<(profile-1)) —— 启动运动
        ///
        /// 单位转换（mm↔脉冲）：
        /// 位置：mm × PulsePerMm = 脉冲（int类型，GTS800位置参数是32位整数）
        /// 速度：mm/s × PulsePerMm = 脉冲/s（double类型）
        /// 加速度：mm/s² × PulsePerMm = 脉冲/s²
        ///
        /// 【跟仿真器的区别】
        /// 仿真器自己算梯形速度规划；真实硬件设参数后由DSP自动执行。
        /// </remarks>
        public void MoveHome(double homeVelocity, double acceleration)
        {
            if (!_isCardOpened) return;

            // 先停止当前所有运动
            Gts.GT_Stop(CardNum, 0x03, 0);

            // 设置X轴梯形速度参数，目标位置0
            SetTrapMotion(ProfileX, 0, homeVelocity, acceleration);

            // 设置Y轴梯形速度参数，目标位置0
            SetTrapMotion(ProfileY, 0, homeVelocity, acceleration);

            // 同时启动X/Y轴运动
            // mask = (1 << (ProfileX-1)) | (1 << (ProfileY-1)) = 0x03
            Gts.GT_Update(CardNum, (1 << (ProfileX - 1)) | (1 << (ProfileY - 1)));
        }

        #endregion

        #region IMotionService 实现 - 涂胶输出

        /// <summary>
        /// 设置涂胶输出开关。调GT_SetDoBit控制GTS800的DO端口。
        /// </summary>
        /// <param name="on">true=开涂胶，false=关涂胶</param>
        /// <remarks>
        /// GT_SetDoBit(1, doType, doIndex, value)：
        /// - doType=12(MC_GPO)：通用数字输出类型，固高官方定义
        /// - doIndex=0：第0个DO通道
        /// - value=1/0：输出高/低电平
        ///
        /// 真实硬件直接控制DO端口，驱动外部继电器/电磁阀开涂胶。
        /// </remarks>
        public void SetGlueOutput(bool on)
        {
            _glueOutputOn = on;
            Gts.GT_SetDoBit(CardNum, GlueDoType, GlueDoIndex, (short)(on ? 1 : 0));
            GlueStateChanged?.Invoke(on);
        }

        #endregion

        #region IMotionService 实现 - JOG运动

        /// <summary>
        /// 启动JOG点动运动——按住方向键就一直走，松开就减速停。
        /// </summary>
        /// <param name="axis">轴号（1=X, 2=Y）</param>
        /// <param name="direction">运动方向：1=正向，-1=负向</param>
        /// <param name="velocity">JOG速度（mm/s）</param>
        /// <param name="accel">加速度（mm/s²）</param>
        /// <param name="decel">减速度（mm/s²）</param>
        /// <remarks>
        /// GTS800 JOG运动流程（4步）：
        /// 1. GT_PrfJog(1, profile) —— 设置规划模式为JOG
        /// 2. GT_SetJogPrm(1, profile, ref jogPrm) —— 设置JOG参数
        /// 3. GT_SetVel(1, profile, vel) —— 设置JOG速度（正=正向，负=负向）
        /// 4. GT_Update(1, 1<<(profile-1)) —— 启动JOG运动
        ///
        /// JOG参数说明：
        /// - acc/dec：加/减速度（脉冲/s²），控制加减速快慢
        /// - smooth：平滑系数（0~1），0=纯梯形，1=最平滑S型
        ///
        /// 方向控制：速度正负号决定方向，direction=1时速度为正，direction=-1时速度为负。
        ///
        /// 【跟仿真器的区别】
        /// 仿真器通过_jogDirX/Y在定时器里算速度和位置；
        /// 真实硬件设好参数后由硬件持续输出脉冲，直到GT_Stop或切换模式。
        /// </remarks>
        public void StartJog(int axis, int direction, double velocity, double accel, double decel)
        {
            if (!_isCardOpened) return;

            short profile = (short)axis;

            // 步骤1：设置规划模式为JOG
            Gts.GT_PrfJog(CardNum, profile);

            // 步骤2：设置JOG参数
            var jogPrm = new Gts.TJogPrm
            {
                acc = accel * PulsePerMm,    // mm/s² → 脉冲/s²
                dec = decel * PulsePerMm,    // mm/s² → 脉冲/s²
                smooth = 0.5                 // 平滑系数
            };
            Gts.GT_SetJogPrm(CardNum, profile, ref jogPrm);

            // 步骤3：设置JOG速度（方向由正负号决定）
            double velPulse = direction * velocity * PulsePerMm; // mm/s → 脉冲/s
            Gts.GT_SetVel(CardNum, profile, velPulse);

            // 步骤4：启动JOG运动
            Gts.GT_Update(CardNum, 1 << (profile - 1));
        }

        /// <summary>
        /// 停止指定轴的JOG运动。调GT_Stop平滑停止。
        /// </summary>
        /// <param name="axis">轴号（1=X, 2=Y）</param>
        /// <remarks>
        /// GT_Stop(1, mask, option)：
        /// - mask：停止的轴掩码，bit0=轴1, bit1=轴2
        /// - option=0：平滑停止（按减速度减速到0）
        /// - option=1：急停（立即停止）
        /// </remarks>
        public void StopJog(int axis)
        {
            // 构造停止掩码：只停指定轴
            int mask = 1 << (axis - 1);
            Gts.GT_Stop(CardNum, mask, 0); // option=0：平滑停止
        }

        #endregion

        #region IMotionService 实现 - 点位运动

        /// <summary>
        /// 绝对定位运动——移动到指定坐标位置。用梯形速度规划。
        /// </summary>
        /// <param name="axis">轴号（1=X, 2=Y）</param>
        /// <param name="position">目标位置（mm），绝对坐标</param>
        /// <param name="velocity">运动速度（mm/s）</param>
        /// <remarks>
        /// 位置：mm × PulsePerMm = 脉冲（int类型，GTS800位置参数是32位整数）
        /// 速度：mm/s × PulsePerMm = 脉冲/s（double类型）
        ///
        /// SetTrapMotion封装了GT_PrfTrap+GT_SetTrapPrm+GT_SetPos+GT_SetVel四步，
        /// 调完后再调GT_Update才真正启动运动。
        /// </remarks>
        public void AbsoluteMove(int axis, double position, double velocity)
        {
            if (!_isCardOpened) return;

            short profile = (short)axis;

            // 设置梯形速度规划参数，位置从mm转脉冲
            SetTrapMotion(profile, (int)(position * PulsePerMm), velocity, 500.0);

            // 启动运动
            Gts.GT_Update(CardNum, 1 << (profile - 1));
        }

        /// <summary>
        /// 相对运动——从当前位置移动指定距离。
        /// </summary>
        /// <param name="axis">轴号（1=X, 2=Y）</param>
        /// <param name="distance">移动距离（mm），正值=正向，负值=负向</param>
        /// <param name="velocity">运动速度（mm/s）</param>
        /// <remarks>
        /// 【跟仿真器的区别】
        /// 仿真器把相对距离转成绝对坐标调AbsoluteMove；
        /// 真实硬件要先读当前规划位置（GT_GetPrfPos），再加上相对距离得到目标位置。
        /// </remarks>
        public void RelativeMove(int axis, double distance, double velocity)
        {
            if (!_isCardOpened) return;

            short profile = (short)axis;

            // 读取当前规划位置（脉冲）
            Gts.GT_GetPrfPos(CardNum, profile, out double currentPos, 1, out uint _);

            // 目标位置 = 当前位置 + 相对距离（mm→脉冲）
            int targetPos = (int)(currentPos + distance * PulsePerMm);

            // 设置梯形速度规划参数并启动
            SetTrapMotion(profile, targetPos, velocity, 500.0);

            Gts.GT_Update(CardNum, 1 << (profile - 1));
        }

        #endregion

        #region IMotionService 实现 - 插补运动

        /// <summary>
        /// 直线插补运动——双轴联动沿直线移动到目标位置。
        /// </summary>
        /// <param name="targetX">目标X坐标（mm）</param>
        /// <param name="targetY">目标Y坐标（mm）</param>
        /// <param name="velocity">合成速度（mm/s）</param>
        /// <param name="acceleration">加速度（mm/s²）</param>
        /// <param name="deceleration">减速度（mm/s²）</param>
        /// <remarks>
        /// GTS800坐标系插补流程（4步）：
        /// 1. GT_SetCrdPrm —— 设置坐标系参数（维度、轴映射、最大速度等）
        /// 2. GT_CrdClear —— 清除坐标系缓冲区
        /// 3. GT_LnXY —— 写入直线插补段到FIFO
        /// 4. GT_CrdStart —— 启动坐标系运动
        ///
        /// 坐标系参数说明：
        /// - dimension=2：2维坐标系（X/Y两轴）
        /// - profile1/profile2：X/Y轴对应的规划轴号
        /// - synVelMax：最大合成速度（脉冲/s），插补速度不能超过这个值
        /// - synAccMax：最大合成加速度（脉冲/s²）
        /// - evenTime：平滑时间（ms），控制段间速度过渡
        /// - setOriginFlag=0：不设置新原点
        ///
        /// 【跟仿真器的区别】
        /// 仿真器自己算插补位置；真实硬件把参数写入FIFO，由DSP自动执行插补。
        /// </remarks>
        public void LinearInterpolation(double targetX, double targetY, double velocity, double acceleration, double deceleration)
        {
            if (!_isCardOpened) return;

            // 步骤1：设置坐标系参数
            var crdPrm = new Gts.TCrdPrm
            {
                dimension = 2,               // 2维坐标系
                profile1 = ProfileX,         // X轴profile
                profile2 = ProfileY,         // Y轴profile
                synVelMax = 500 * PulsePerMm, // 最大合成速度
                synAccMax = 500 * PulsePerMm, // 最大合成加速度
                evenTime = 25,               // 平滑时间25ms
                setOriginFlag = 0,           // 不设置新原点
                originPos1 = 0,              // X轴原点位置
                originPos2 = 0               // Y轴原点位置
            };
            Gts.GT_SetCrdPrm(CardNum, CrdIndex, ref crdPrm);

            // 步骤2：清除坐标系缓冲区
            Gts.GT_CrdClear(CardNum, CrdIndex, Fifo);

            // 步骤3：写入直线插补段
            // 参数：目标X/Y（脉冲），合成速度（脉冲/s），合成加速度（脉冲/s²），终点速度（脉冲/s）
            int targetXPulse = (int)(targetX * PulsePerMm);
            int targetYPulse = (int)(targetY * PulsePerMm);
            double synVel = velocity * PulsePerMm;        // mm/s → 脉冲/s
            double synAcc = acceleration * PulsePerMm;    // mm/s² → 脉冲/s²
            double velEnd = 0;                             // 终点速度为0，到点就停

            short ret = Gts.GT_LnXY(CardNum, CrdIndex, targetXPulse, targetYPulse,
                synVel, synAcc, velEnd, Fifo);
            if (ret != 0) return;

            // 步骤4：启动坐标系运动
            // mask = 1 << (CrdIndex - 1) = 1，启动第1个坐标系
            Gts.GT_CrdStart(CardNum, (short)(1 << (CrdIndex - 1)), 0);
        }

        /// <summary>
        /// 圆弧插补运动——用圆心+半径+角度模式。
        /// </summary>
        /// <param name="centerX">圆心X坐标（mm）</param>
        /// <param name="centerY">圆心Y坐标（mm）</param>
        /// <param name="radius">圆弧半径（mm）</param>
        /// <param name="clockwise">是否顺时针方向</param>
        /// <param name="velocity">合成速度（mm/s）</param>
        /// <param name="acceleration">加速度（mm/s²）</param>
        /// <param name="deceleration">减速度（mm/s²）</param>
        /// <param name="arcAngleDeg">圆弧角度（度），默认360度=整圆</param>
        /// <remarks>
        /// GTS800提供两种圆弧插补API：
        ///
        /// 1. GT_ArcXYR —— 半径模式：指定终点+半径+方向
        ///    适用于已知半径和方向的场景
        ///
        /// 2. GT_ArcXYC —— 圆心模式：指定终点+圆心偏移+方向
        ///    适用于已知圆心坐标的场景
        ///
        /// 我们用GT_ArcXYC（圆心模式），因为接口参数已经提供了圆心坐标。
        ///
        /// 【重要踩坑】GT_ArcXYC的xCenter/yCenter参数是相对于起点的偏移量，不是绝对坐标！
        /// 必须算：xCenter = 圆心X - 起点X, yCenter = 圆心Y - 起点Y
        /// 而GT_ArcXYR的x/y参数是绝对坐标（相对于坐标系原点）
        ///
        /// 圆弧方向常量（固高官方定义）：
        /// INTERPOLATION_CIRCLE_DIR_CW = 0：顺时针
        /// INTERPOLATION_CIRCLE_DIR_CCW = 1：逆时针
        /// </remarks>
        public void ArcInterpolation(double centerX, double centerY, double radius,
            bool clockwise, double velocity, double acceleration, double deceleration, double arcAngleDeg = 360.0)
        {
            if (!_isCardOpened) return;

            // 设置坐标系参数
            var crdPrm = new Gts.TCrdPrm
            {
                dimension = 2,
                profile1 = ProfileX,
                profile2 = ProfileY,
                synVelMax = 500 * PulsePerMm,
                synAccMax = 500 * PulsePerMm,
                evenTime = 25,
                setOriginFlag = 0,
                originPos1 = 0,
                originPos2 = 0
            };
            Gts.GT_SetCrdPrm(CardNum, CrdIndex, ref crdPrm);

            // 清除坐标系缓冲区
            Gts.GT_CrdClear(CardNum, CrdIndex, Fifo);

            // 计算圆弧终点坐标（脉冲）
            double centerXPulse = centerX * PulsePerMm;
            double centerYPulse = centerY * PulsePerMm;

            // 读取当前规划位置作为起点
            Gts.GT_GetPrfPos(CardNum, ProfileX, out double startX, 1, out uint _);
            Gts.GT_GetPrfPos(CardNum, ProfileY, out double startY, 1, out _);

            // 计算起始角度和终点角度
            double startAngle = Math.Atan2(startY - centerYPulse, startX - centerXPulse);
            double sweepRad = arcAngleDeg * Math.PI / 180.0;
            double endAngle = clockwise ? startAngle - sweepRad : startAngle + sweepRad;

            // 计算终点坐标
            double radiusPulse = radius * PulsePerMm;
            int endXPulse = (int)(centerXPulse + radiusPulse * Math.Cos(endAngle));
            int endYPulse = (int)(centerYPulse + radiusPulse * Math.Sin(endAngle));

            // 圆心相对于起点的偏移量（GT_ArcXYC要求偏移量，不是绝对坐标！）
            double xCenterOffset = centerXPulse - startX;
            double yCenterOffset = centerYPulse - startY;

            // 圆弧方向
            short circleDir = clockwise ? Gts.INTERPOLATION_CIRCLE_DIR_CW : Gts.INTERPOLATION_CIRCLE_DIR_CCW;

            // 合成速度和加速度（mm→脉冲）
            double synVel = velocity * PulsePerMm;
            double synAcc = acceleration * PulsePerMm;
            double velEnd = 0;

            // 写入圆弧插补段（圆心模式）
            short ret = Gts.GT_ArcXYC(CardNum, CrdIndex, endXPulse, endYPulse,
                xCenterOffset, yCenterOffset, circleDir, synVel, synAcc, velEnd, Fifo);
            if (ret != 0) return;

            // 启动坐标系运动
            Gts.GT_CrdStart(CardNum, (short)(1 << (CrdIndex - 1)), 0);
        }

        #endregion

        #region IMotionService 实现 - 配方执行

        /// <summary>
        /// 执行涂胶配方——将所有轨迹段写入坐标系FIFO缓冲区，一次性启动执行。
        /// </summary>
        /// <param name="recipe">涂胶配方，包含轨迹点列表</param>
        /// <param name="offsetX">X方向偏移（mm），视觉定位算出来的补偿量</param>
        /// <param name="offsetY">Y方向偏移（mm），视觉定位算出来的补偿量</param>
        /// <param name="offsetAngle">角度偏移（度），视觉定位算出来的旋转补偿</param>
        /// <remarks>
        /// 真实GTS800执行配方的推荐方式——坐标系缓冲区模式：
        /// 1. 设置坐标系参数，清除缓冲区
        /// 2. 逐段写入FIFO（直线用GT_LnXY，圆弧用GT_ArcXYC）
        /// 3. 在涂胶状态变化处插入GT_BufIO控制DO输出
        /// 4. 调GT_CrdStart一次性启动，硬件自动连续执行所有段
        /// 5. 轮询GT_CrdStatus等待完成
        ///
        /// 【跟仿真器的核心区别】
        /// 仿真器逐段执行：一段跑完再设下一段参数，段间有微小停顿；
        /// 真实硬件所有段预写入FIFO，硬件自动连续执行，段间速度平滑过渡（前瞻算法），无停顿。
        ///
        /// velEnd参数：非末段保留50%速度，末段速度为0。
        /// 这样段间速度不会降到0，实现连续运动。
        /// </remarks>
        public void ExecuteRecipe(DispensingRecipe recipe, double offsetX, double offsetY, double offsetAngle)
        {
            if (!_isCardOpened || recipe.Points.Count == 0) return;

            _recipePoints = recipe.Points;
            _recipeIndex = 0;
            _isExecutingRecipe = true;
            _trail.Clear();

            // 设置坐标系参数
            var crdPrm = new Gts.TCrdPrm
            {
                dimension = 2,
                profile1 = ProfileX,
                profile2 = ProfileY,
                synVelMax = 500 * PulsePerMm,
                synAccMax = 500 * PulsePerMm,
                evenTime = 25,
                setOriginFlag = 0,
                originPos1 = 0,
                originPos2 = 0
            };
            Gts.GT_SetCrdPrm(CardNum, CrdIndex, ref crdPrm);

            // 清除坐标系缓冲区
            Gts.GT_CrdClear(CardNum, CrdIndex, Fifo);

            // 读取当前位置作为起始位置
            Gts.GT_GetPrfPos(CardNum, ProfileX, out double startX, 1, out uint _);
            Gts.GT_GetPrfPos(CardNum, ProfileY, out double startY, 1, out _);

            // 逐段写入FIFO
            bool prevGlueOn = false;
            for (int i = 0; i < _recipePoints.Count; i++)
            {
                var pt = _recipePoints[i];

                // 计算目标位置（mm→脉冲），加上偏移量
                int targetXPulse = (int)((pt.X + offsetX) * PulsePerMm);
                int targetYPulse = (int)((pt.Y + offsetY) * PulsePerMm);

                // 合成速度和加速度
                double synVel = pt.Speed * PulsePerMm;
                double synAcc = (pt.Type == TrajectoryType.Arc ? 300 : 500) * PulsePerMm;
                // 非末段保留50%速度，末段速度为0——这样段间不会停顿
                double velEnd = (i < _recipePoints.Count - 1) ? synVel * 0.5 : 0;

                if (pt.Type == TrajectoryType.RapidMove)
                {
                    // 快速定位移动（不涂胶），用GT_LnXYG0
                    Gts.GT_LnXYG0(CardNum, CrdIndex, targetXPulse, targetYPulse,
                        synVel, synAcc, Fifo);
                }
                else if (pt.Type == TrajectoryType.Linear)
                {
                    // 直线插补段
                    Gts.GT_LnXY(CardNum, CrdIndex, targetXPulse, targetYPulse,
                        synVel, synAcc, velEnd, Fifo);
                }
                else if (pt.Type == TrajectoryType.Arc)
                {
                    // 圆弧插补段
                    double centerXPulse = (pt.ArcCenterX + offsetX) * PulsePerMm;
                    double centerYPulse = (pt.ArcCenterY + offsetY) * PulsePerMm;
                    short circleDir = pt.ArcClockwise
                        ? Gts.INTERPOLATION_CIRCLE_DIR_CW
                        : Gts.INTERPOLATION_CIRCLE_DIR_CCW;

                    // GT_ArcXYC圆心模式——xCenter/yCenter是相对于起点的偏移量
                    Gts.GT_ArcXYC(CardNum, CrdIndex, targetXPulse, targetYPulse,
                        centerXPulse, centerYPulse, circleDir, synVel, synAcc, velEnd, Fifo);
                }

                // 涂胶状态变化时插入DO控制到缓冲区
                // GT_BufIO让DO输出跟运动段同步执行
                if (pt.GlueOn != prevGlueOn)
                {
                    ushort doValue = pt.GlueOn ? (ushort)1 : (ushort)0;
                    Gts.GT_BufIO(CardNum, CrdIndex, (ushort)GlueDoType,
                        1, doValue, Fifo);
                    prevGlueOn = pt.GlueOn;
                }
            }

            // 启动坐标系运动——硬件自动连续执行所有段
            Gts.GT_CrdStart(CardNum, (short)(1 << (CrdIndex - 1)), 0);
        }

        /// <summary>
        /// 停止配方执行。停坐标系运动，清缓冲区。
        /// </summary>
        public void StopRecipe()
        {
            _isExecutingRecipe = false;

            // 停止坐标系运动
            Gts.GT_Stop(CardNum, 0x03, 0);

            // 清除坐标系缓冲区中剩余的运动段
            Gts.GT_CrdClear(CardNum, CrdIndex, Fifo);
        }

        #endregion

        #region IMotionService 实现 - 停止与急停

        /// <summary>
        /// 停止所有轴运动。平滑停止（按减速度减速到0）。
        /// </summary>
        /// <remarks>
        /// GT_Stop(1, 0x03, 0)：
        /// - mask=0x03：X轴(bit0)和Y轴(bit1)
        /// - option=0：平滑停止
        /// </remarks>
        public void StopAllAxis()
        {
            Gts.GT_Stop(CardNum, 0x03, 0);
        }

        /// <summary>
        /// 紧急停止——立即停，不减速。有机械冲击，只在紧急情况用！
        /// </summary>
        /// <remarks>
        /// GT_Stop(1, 0x03, 1)：
        /// - option=1：急停模式，脉冲输出立即停止
        ///
        /// 正常停止用StopAllAxis（平滑停止），急停会伤机器。
        /// </remarks>
        public void EmergencyStop()
        {
            // option=1：急停模式，立即停止脉冲输出
            Gts.GT_Stop(CardNum, 0x03, 1);
            _isExecutingRecipe = false;
        }

        #endregion

        #region IMotionService 实现 - 轴状态与软限位

        /// <summary>
        /// 获取轴状态信息。从GTS800读硬件状态字和编码器位置。
        /// </summary>
        /// <param name="axis">轴号（1=X, 2=Y）</param>
        /// <returns>AxisStatusInfo结构体</returns>
        /// <remarks>
        /// 轴状态字常用位域（固高官方定义）：
        /// - bit0：正限位触发
        /// - bit1：负限位触发
        /// - bit2：报警
        /// - bit5：运动完成
        /// - bit10(0x400)：运动中
        /// - bit12(0x1000)：回原点完成
        /// </remarks>
        public AxisStatusInfo GetAxisStatus(short axis)
        {
            var info = new AxisStatusInfo
            {
                AxisIndex = axis,
                Position = 0,
                IsMoving = false,
                IsHomed = false,
                HasAlarm = false,
                IsEnabled = _isCardOpened
            };

            if (!_isCardOpened) return info;

            // 读轴状态字
            short ret = Gts.GT_GetSts(CardNum, axis, out int sts, 1, out uint _);
            if (ret != 0) return info;

            // 读规划位置（脉冲→mm）
            short profile = axis;
            ret = Gts.GT_GetPrfPos(CardNum, profile, out double pos, 1, out _);
            if (ret == 0)
                info.Position = pos / PulsePerMm;

            // 解析状态字
            info.IsMoving = (sts & 0x400) != 0;       // bit10：运动中
            info.IsHomed = (sts & 0x1000) != 0;        // bit12：回原点完成
            info.HasAlarm = (sts & 0x04) != 0;          // bit2：报警

            return info;
        }

        /// <summary>
        /// 清除运动轨迹记录。
        /// </summary>
        public void ClearTrajectoryTrail()
        {
            _trail.Clear();
        }

        /// <summary>
        /// 设置软限位。真实硬件的软限位通常在配置文件(GTS800.cfg)里设，
        /// 这里只是保存到内部变量给界面显示用。
        /// </summary>
        /// <param name="axis">轴号（1=X, 2=Y）</param>
        /// <param name="positive">正向软限位（脉冲）</param>
        /// <param name="negative">负向软限位（脉冲）</param>
        public void SetSoftLimit(int axis, int positive, int negative)
        {
            if (axis == 1) { _softLimitPosX = positive; _softLimitNegX = negative; }
            else if (axis == 2) { _softLimitPosY = positive; _softLimitNegY = negative; }
        }

        /// <summary>
        /// 启用或禁用软限位。调GT_LmtsOn/GT_LmtsOff控制硬件限位检测。
        /// </summary>
        /// <param name="enableX">是否启用X轴软限位</param>
        /// <param name="enableY">是否启用Y轴软限位</param>
        /// <remarks>
        /// GT_LmtsOn/GT_LmtsOff参数：
        /// - MC_LIMIT_POSITIVE：正限位
        /// - MC_LIMIT_NEGATIVE：负限位
        /// </remarks>
        public void EnableSoftLimit(bool enableX, bool enableY)
        {
            _lmtsOnX = enableX;
            _lmtsOnY = enableY;

            if (enableX)
            {
                Gts.GT_LmtsOn(CardNum, AxisX, Gts.MC_LIMIT_POSITIVE);
                Gts.GT_LmtsOn(CardNum, AxisX, Gts.MC_LIMIT_NEGATIVE);
            }
            else
            {
                Gts.GT_LmtsOff(CardNum, AxisX, Gts.MC_LIMIT_POSITIVE);
                Gts.GT_LmtsOff(CardNum, AxisX, Gts.MC_LIMIT_NEGATIVE);
            }

            if (enableY)
            {
                Gts.GT_LmtsOn(CardNum, AxisY, Gts.MC_LIMIT_POSITIVE);
                Gts.GT_LmtsOn(CardNum, AxisY, Gts.MC_LIMIT_NEGATIVE);
            }
            else
            {
                Gts.GT_LmtsOff(CardNum, AxisY, Gts.MC_LIMIT_POSITIVE);
                Gts.GT_LmtsOff(CardNum, AxisY, Gts.MC_LIMIT_NEGATIVE);
            }
        }

        /// <summary>
        /// 获取软限位值，单位转成mm方便界面显示。
        /// </summary>
        /// <returns>X正限位、X负限位、Y正限位、Y负限位（mm）</returns>
        public (double posX, double negX, double posY, double negY) GetSoftLimits()
        {
            return (_softLimitPosX / PulsePerMm, _softLimitNegX / PulsePerMm,
                    _softLimitPosY / PulsePerMm, _softLimitNegY / PulsePerMm);
        }

        #endregion

        #region 私有方法 - 梯形速度运动

        /// <summary>
        /// 设置梯形速度规划运动参数（Trap模式）。
        /// 封装了GT_PrfTrap+GT_SetTrapPrm+GT_SetPos+GT_SetVel四步。
        /// 注意：只设参数，不启动运动。调完后再调GT_Update才真正动。
        /// </summary>
        /// <param name="profile">规划轴号（1=X, 2=Y）</param>
        /// <param name="targetPos">目标位置（脉冲）</param>
        /// <param name="velocity">运动速度（mm/s）</param>
        /// <param name="acceleration">加速度（mm/s²）</param>
        /// <remarks>
        /// Trap参数说明：
        /// - acc/dec：加/减速度（脉冲/s²），控制加减速快慢
        /// - velStart：起始速度（脉冲/s），通常设0
        /// - smoothTime：平滑时间（ms），控制S型加减速的平滑程度
        ///   0=纯梯形（无平滑），25=典型值，越大越平滑
        /// </remarks>
        private void SetTrapMotion(short profile, int targetPos, double velocity, double acceleration)
        {
            // 步骤1：设置规划模式为梯形速度
            Gts.GT_PrfTrap(CardNum, profile);

            // 步骤2：设置梯形速度参数
            var trapPrm = new Gts.TTrapPrm
            {
                acc = acceleration * PulsePerMm,    // mm/s² → 脉冲/s²
                dec = acceleration * PulsePerMm,    // mm/s² → 脉冲/s²
                velStart = 0,                       // 起始速度为0
                smoothTime = 25                     // 平滑时间25ms
            };
            Gts.GT_SetTrapPrm(CardNum, profile, ref trapPrm);

            // 步骤3：设置目标位置（脉冲）
            Gts.GT_SetPos(CardNum, profile, targetPos);

            // 步骤4：设置目标速度（mm/s → 脉冲/s）
            Gts.GT_SetVel(CardNum, profile, velocity * PulsePerMm);
        }

        #endregion

        #region 私有方法 - 位置轮询

        /// <summary>
        /// 位置轮询定时器Tick——每20ms读一次硬件位置和状态。
        /// </summary>
        /// <remarks>
        /// 【跟仿真器的核心区别】
        /// 仿真器的MotionTimer_Tick是运动驱动的核心：算速度、算位置、判断完成、记录轨迹、发事件。
        /// 真实硬件的PollingTimer_Tick只负责：读位置、检查状态、记录轨迹、发事件。
        /// 运动计算和脉冲输出由硬件DSP完成，软件不管。
        /// </remarks>
        private void PollingTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isCardOpened) return;

            // 读X/Y轴规划位置（脉冲）
            Gts.GT_GetPrfPos(CardNum, ProfileX, out double posX, 1, out uint _);
            Gts.GT_GetPrfPos(CardNum, ProfileY, out double posY, 1, out _);

            // 读X/Y轴状态字
            Gts.GT_GetSts(CardNum, AxisX, out int stsX, 1, out _);
            Gts.GT_GetSts(CardNum, AxisY, out int stsY, 1, out _);

            // 判断是否运动中（bit10 = 0x400）
            bool isMoving = (stsX & 0x400) != 0 || (stsY & 0x400) != 0;

            // 脉冲→mm
            double displayX = posX / PulsePerMm;
            double displayY = posY / PulsePerMm;

            // 记录轨迹点（去重）
            double ddx = displayX - _prevTrailX;
            double ddy = displayY - _prevTrailY;
            if (ddx * ddx + ddy * ddy > 0.0001 || _trail.Count == 0)
            {
                _trail.Add((displayX, displayY, _glueOutputOn));
                _prevTrailX = displayX;
                _prevTrailY = displayY;
                if (_trail.Count > MaxTrailSize)
                    _trail.RemoveRange(0, _trail.Count - MaxTrailSize);
            }

            // 发布位置更新事件
            PositionUpdated?.Invoke(displayX, displayY, isMoving);

            // 检查回原点完成状态（bit12 = 0x1000）
            if (!_isHomedX && (stsX & 0x1000) != 0) _isHomedX = true;
            if (!_isHomedY && (stsY & 0x1000) != 0) _isHomedY = true;

            // 检查配方执行完成——读坐标系状态，run=0说明所有段跑完了
            if (_isExecutingRecipe)
            {
                short ret = Gts.GT_CrdStatus(CardNum, CrdIndex, out short run, out int segment, Fifo);
                if (ret == 0 && run == 0)
                {
                    _isExecutingRecipe = false;
                    RecipeExecutionCompleted?.Invoke();
                }
            }
        }

        #endregion

        #region IDisposable 实现

        /// <summary>
        /// 释放资源，关闭运动控制卡。
        /// </summary>
        public void Dispose()
        {
            CloseCard();
        }

        #endregion
    }
}
