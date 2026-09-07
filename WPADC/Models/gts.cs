using System.Runtime.InteropServices;

namespace WPADC.Models
{
    /// <summary>
    /// 固高GTS800运动控制卡SDK的C#封装类。
    ///
    /// 什么是P/Invoke？
    /// P/Invoke（Platform Invocation Services）是.NET提供的一种机制，
    /// 让C#代码能直接调用C/C++写的DLL里的函数。固高的SDK是C++写的gts.dll，
    /// 我们用[DllImport]声明函数签名，运行时.NET会自动找到DLL并调用对应的函数。
    ///
    /// 这里面的东西分三类：
    /// 1. 常量（const short）—— 固高SDK定义的固定数值，不能改
    /// 2. 结构体（struct）—— 固高SDK定义的参数结构，字段顺序不能改（C++那边按内存偏移量读的）
    /// 3. 函数声明（[DllImport]）—— 固高SDK导出的函数，参数类型必须和C++原型严格对应
    ///
    /// 【重要】结构体的字段顺序绝对不能改！
    /// C++那边是按结构体偏移量来读字段的，如果C#这边改了字段顺序，
    /// C++读到的值就全乱了，会出各种莫名其妙的bug。
    /// 包括那些pad字段（填充字段）也不能删，它们是为了内存对齐的。
    ///
    /// 返回值约定：大部分函数返回short，0表示成功，非0表示错误码。
    /// </summary>
    public class Gts
    {
        // ============================================================
        // DLL版本号常量 —— 固高SDK的版本信息，一般不用管
        // ============================================================
        public const short DLL_VERSION_0 = 2;
        public const short DLL_VERSION_1 = 1;
        public const short DLL_VERSION_2 = 0;

        public const short DLL_VERSION_3 = 1;
        public const short DLL_VERSION_4 = 5;
        public const short DLL_VERSION_5 = 0;
        public const short DLL_VERSION_6 = 6;
        public const short DLL_VERSION_7 = 0;
        public const short DLL_VERSION_8 = 7;

        // ============================================================
        // 运动控制核心常量 —— 固高SDK定义的固定枚举值
        // MC_前缀 = Motion Control，用于指定IO类型、轴类型等
        // ============================================================

        public const short MC_NONE = -1;           // 无类型/无效值

        // 限位和IO类型常量 —— 用于GT_GetDi/GT_SetDo等IO函数的type参数
        public const short MC_LIMIT_POSITIVE = 0;  // 正限位输入
        public const short MC_LIMIT_NEGATIVE = 1;  // 负限位输入
        public const short MC_ALARM = 2;            // 报警输入
        public const short MC_HOME = 3;             // 原点信号输入
        public const short MC_GPI = 4;              // 通用输入（General Purpose Input）
        public const short MC_ARRIVE = 5;           // 到位信号
        public const short MC_MPG = 6;              // 手轮输入（Manual Pulse Generator）

        // 输出控制类型常量
        public const short MC_ENABLE = 10;          // 伺服使能输出
        public const short MC_CLEAR = 11;           // 报警清除输出
        public const short MC_GPO = 12;             // 通用输出（General Purpose Output）

        // 模拟/数字通道类型
        public const short MC_DAC = 20;             // DAC通道（模拟量输出）
        public const short MC_STEP = 21;            // 步进通道
        public const short MC_PULSE = 22;           // 脉冲通道
        public const short MC_ENCODER = 23;         // 编码器通道
        public const short MC_ADC = 24;             // ADC通道（模拟量输入）

        // 轴/规划/控制对象类型
        public const short MC_AXIS = 30;            // 轴对象
        public const short MC_PROFILE = 31;         // 规划对象
        public const short MC_CONTROL = 32;         // 控制对象

        // ============================================================
        // 位置捕捉模式常量 —— 用于GT_SetCaptureMode
        // 捕捉就是硬件级锁存编码器位置，精度比软件读取高得多
        // ============================================================
        public const short CAPTURE_HOME = 1;        // 捕捉原点信号
        public const short CAPTURE_INDEX = 2;       // 捕捉编码器Index信号（Z相信号）
        public const short CAPTURE_PROBE = 3;       // 捕捉探针信号
        public const short CAPTURE_HSIO0 = 6;       // 捕捉高速IO0
        public const short CAPTURE_HSIO1 = 7;       // 捕捉高速IO1
        public const short CAPTURE_HOME_GPI = 8;    // 捕捉原点+通用输入

        // ============================================================
        // PT运动模式常量 —— PT是Position-Time模式，逐点给定位置和时间
        // ============================================================
        public const short PT_MODE_STATIC = 0;      // 静态模式（缓冲区运动前等待）
        public const short PT_MODE_DYNAMIC = 1;     // 动态模式（实时修改运动参数）

        // PT段类型
        public const short PT_SEGMENT_NORMAL = 0;   // 普通段
        public const short PT_SEGMENT_EVEN = 1;     // 匀速段
        public const short PT_SEGMENT_STOP = 2;     // 停止段

        // ============================================================
        // 电子齿轮/凸轮常量 —— 用于跟随运动模式
        // ============================================================
        public const short GEAR_MASTER_ENCODER = 1;  // 齿轮主轴来源：编码器
        public const short GEAR_MASTER_PROFILE = 2;  // 齿轮主轴来源：规划位置
        public const short GEAR_MASTER_AXIS = 3;     // 齿轮主轴来源：轴位置

        public const short FOLLOW_MASTER_ENCODER = 1; // 凸轮主轴来源：编码器
        public const short FOLLOW_MASTER_PROFILE = 2; // 凸轮主轴来源：规划位置
        public const short FOLLOW_MASTER_AXIS = 3;    // 凸轮主轴来源：轴位置

        public const short FOLLOW_EVENT_START = 1;    // 凸轮事件：启动
        public const short FOLLOW_EVENT_PASS = 2;     // 凸轮事件：经过

        public const short GEAR_EVENT_START = 1;      // 齿轮事件：启动
        public const short GEAR_EVENT_PASS = 2;       // 齿轮事件：经过
        public const short GEAR_EVENT_AREA = 5;       // 齿轮事件：区域

        // 凸轮段类型
        public const short FOLLOW_SEGMENT_NORMAL = 0;  // 普通段
        public const short FOLLOW_SEGMENT_EVEN = 1;    // 匀速段
        public const short FOLLOW_SEGMENT_STOP = 2;    // 停止段
        public const short FOLLOW_SEGMENT_CONTINUE = 3;// 连续段

        // ============================================================
        // 坐标插补常量 —— 用于多轴联动（直线/圆弧/螺旋线插补）
        // ============================================================
        public const short INTERPOLATION_AXIS_MAX = 4; // 最大插补轴数
        public const short CRD_FIFO_MAX = 4096;        // 坐标FIFO最大深度
        public const short FIFO_MAX = 2;               // FIFO数量
        public const short CRD_MAX = 2;                // 坐标系数量
        public const short CRD_OPERATION_DATA_EXT_MAX = 2; // 扩展操作数据最大数

        // 坐标系缓冲区操作类型 —— 用于插补运动中同步执行IO操作
        public const short CRD_OPERATION_TYPE_NONE = 0;                  // 无操作
        public const short CRD_OPERATION_TYPE_BUF_IO_DELAY = 1;         // 缓冲IO延时
        public const short CRD_OPERATION_TYPE_LASER_ON = 2;             // 激光开
        public const short CRD_OPERATION_TYPE_LASER_OFF = 3;            // 激光关
        public const short CRD_OPERATION_TYPE_BUF_DA = 4;               // 缓冲DA输出
        public const short CRD_OPERATION_TYPE_LASER_CMD = 5;            // 激光命令
        public const short CRD_OPERATION_TYPE_LASER_FOLLOW = 6;         // 激光跟随
        public const short CRD_OPERATION_TYPE_LMTS_ON = 7;              // 限位使能
        public const short CRD_OPERATION_TYPE_LMTS_OFF = 8;             // 限位禁能
        public const short CRD_OPERATION_TYPE_SET_STOP_IO = 9;          // 设置停止IO
        public const short CRD_OPERATION_TYPE_BUF_MOVE = 10;            // 缓冲运动
        public const short CRD_OPERATION_TYPE_BUF_GEAR = 11;            // 缓冲齿轮
        public const short CRD_OPERATION_TYPE_SET_SEG_NUM = 12;         // 设置段号
        public const short CRD_OPERATION_TYPE_STOP_MOTION = 13;         // 停止运动
        public const short CRD_OPERATION_TYPE_SET_VAR_VALUE = 14;       // 设置变量值
        public const short CRD_OPERATION_TYPE_JUMP_NEXT_SEG = 15;       // 跳转下一段
        public const short CRD_OPERATION_TYPE_SYNCH_PRF_POS = 16;       // 同步规划位置
        public const short CRD_OPERATION_TYPE_VIRTUAL_TO_ACTUAL = 17;    // 虚拟转实际
        public const short CRD_OPERATION_TYPE_SET_USER_VAR = 18;        // 设置用户变量
        public const short CRD_OPERATION_TYPE_SET_DO_BIT_PULSE = 19;    // 设置DO位脉冲
        public const short CRD_OPERATION_TYPE_BUF_COMPAREPULSE = 20;    // 缓冲比较脉冲
        public const short CRD_OPERATION_TYPE_LASER_ON_EX = 21;         // 激光开（扩展）
        public const short CRD_OPERATION_TYPE_LASER_OFF_EX = 22;        // 激光关（扩展）
        public const short CRD_OPERATION_TYPE_LASER_CMD_EX = 23;        // 激光命令（扩展）
        public const short CRD_OPERATION_TYPE_LASER_FOLLOW_RATIO_EX = 24;// 激光跟随比例（扩展）
        public const short CRD_OPERATION_TYPE_LASER_FOLLOW_MODE = 25;   // 激光跟随模式
        public const short CRD_OPERATION_TYPE_LASER_FOLLOW_OFF = 26;    // 激光跟随关
        public const short CRD_OPERATION_TYPE_LASER_FOLLOW_OFF_EX = 27; // 激光跟随关（扩展）
        public const short CRD_OPERATION_TYPE_LASER_FOLLOW_SPLINE = 28; // 激光跟随样条
        public const short CRD_OPERATION_TYPE_MOTION_DATA = 29;         // 运动数据

        // 插补运动类型
        public const short INTERPOLATION_MOTION_TYPE_LINE = 0;      // 直线插补
        public const short INTERPOLATION_MOTION_TYPE_CIRCLE = 1;    // 圆弧插补
        public const short INTERPOLATION_MOTION_TYPE_HELIX = 2;     // 螺旋线插补
        public const short INTERPOLATION_MOTION_TYPE_CIRCLE_3D = 3; // 3D圆弧插补

        // 圆弧所在平面
        public const short INTERPOLATION_CIRCLE_PLAT_XY = 0; // XY平面圆弧
        public const short INTERPOLATION_CIRCLE_PLAT_YZ = 1; // YZ平面圆弧
        public const short INTERPOLATION_CIRCLE_PLAT_ZX = 2; // ZX平面圆弧

        // 螺旋线圆弧平面+直线轴
        public const short INTERPOLATION_HELIX_CIRCLE_XY_LINE_Z = 0; // XY圆弧+Z直线
        public const short INTERPOLATION_HELIX_CIRCLE_YZ_LINE_X = 1; // YZ圆弧+X直线
        public const short INTERPOLATION_HELIX_CIRCLE_ZX_LINE_Y = 2; // ZX圆弧+Y直线

        // 圆弧方向
        public const short INTERPOLATION_CIRCLE_DIR_CW = 0;  // 顺时针
        public const short INTERPOLATION_CIRCLE_DIR_CCW = 1; // 逆时针

        // 比较输出端口
        public const short COMPARE_PORT_HSIO = 0; // 高速IO口
        public const short COMPARE_PORT_GPO = 1;  // 通用输出口

        // 2D比较模式
        public const short COMPARE2D_MODE_2D = 1; // 2D比较模式
        public const short COMPARE2D_MODE_1D = 0; // 1D比较模式

        // 接口板类型
        public const short INTERFACEBOARD20 = 2; // 接口板类型2.0
        public const short INTERFACEBOARD30 = 3; // 接口板类型3.0

        // 激光轴类型
        public const short AXIS_LASER = 7;    // 激光轴
        public const short AXIS_LASER_EX = 8; // 激光轴（扩展）

        // 激光控制模式
        public const short LASER_CTRL_MODE_PWM1 = 0;  // PWM模式1
        public const short LASER_CTRL_FREQUENCY = 1;   // 频率控制模式
        public const short LASER_CTRL_VOLTAGE = 2;     // 电压控制模式
        public const short LASER_CTRL_MODE_PWM2 = 3;   // PWM模式2

        // ============================================================
        // 结构体定义 —— 固高SDK规定的固定结构，字段顺序绝对不能改！
        // C++那边按内存偏移量读字段，改了顺序数据就全乱了
        // ============================================================

        /// <summary>
        /// 梯形速度规划参数 —— 用于定位运动（Trap模式）。
        /// Trap就是梯形速度曲线：加速→匀速→减速，最常用的运动模式。
        /// 通过GT_SetTrapPrm设置，GT_GetTrapPrm读取。
        /// 【固高SDK固定结构，字段顺序不能改】
        /// </summary>
        public struct TTrapPrm
        {
            public double acc;        // 加速度，单位pulse/ms²。控制卡内部用脉冲为单位，不是mm
            public double dec;        // 减速度，单位pulse/ms²。一般设成和acc一样
            public double velStart;   // 起始速度，单位pulse/ms。从0加速到这个速度后才开始匀加速，一般设很小就行
            public short  smoothTime; // 平滑时间，单位ms。0=不平滑，值越大加减速越平滑但响应越慢，常用0~50
        }

        /// <summary>
        /// 点动运动参数 —— 用于手动点动（Jog模式）。
        /// Jog就是按住方向键就一直走，松开就减速停止。
        /// 通过GT_SetJogPrm设置，GT_GetJogPrm读取。
        /// 【固高SDK固定结构，字段顺序不能改】
        /// </summary>
        public struct TJogPrm
        {
            public double acc;    // 加速度，单位pulse/ms²
            public double dec;    // 减速度，单位pulse/ms²
            public double smooth; // 平滑系数，0=不平滑，越大越平滑。和Trap的smoothTime类似
        }

        /// <summary>
        /// PID控制参数 —— 用于伺服闭环控制。
        /// 一般用固高配置工具调好就行，代码里很少直接改。
        /// 【固高SDK固定结构，字段顺序不能改】
        /// </summary>
        public struct TPid
        {
            public double kp;            // 比例增益
            public double ki;            // 积分增益
            public double kd;            // 微分增益
            public double kvff;          // 速度前馈增益
            public double kaff;          // 加速度前馈增益

            public int integralLimit;    // 积分限幅，防止积分饱和
            public int derivativeLimit;  // 微分限幅，防止微分冲击
            public short  limit;         // 输出限幅
        }

        /// <summary>
        /// 线程状态 —— 用于GT_GetThreadSts查询运动程序线程状态。
        /// 固高支持在控制卡上运行用户编写的运动程序（类似G代码），这个结构查询线程运行状态。
        /// 我们项目没用这个功能，一般直接用API控制就行。
        /// 【固高SDK固定结构，字段顺序不能改】
        /// </summary>
        public struct TThreadSts
        {
            public short run;      // 是否在运行，1=运行中，0=已停止
            public short error;    // 错误码，0=无错误
            public double result;  // 运算结果
            public short line;     // 当前执行到的行号
        }

        /// <summary>
        /// 变量信息 —— 用于GT_GetVarId获取运动程序中变量的信息。
        /// 我们项目没用运动程序功能，这个结构基本不用。
        /// 【固高SDK固定结构，字段顺序不能改】
        /// </summary>
        public struct TVarInfo
        {
            public short id;        // 变量ID
            public short dataType;  // 变量数据类型
            public double dumb0;    // 保留字段（dumb=dumb，固高占位用的）
            public double dumb1;    // 保留字段
            public double dumb2;    // 保留字段
            public double dumb3;    // 保留字段
        }

        /// <summary>
        /// 编译信息 —— 用于GT_Compile编译运动程序时的错误信息。
        /// 我们项目没用运动程序功能，这个结构基本不用。
        /// 【固高SDK固定结构，字段顺序不能改】
        /// </summary>
        public struct TCompileInfo
        {
            public string pFileName;  // 出错的文件名
            public short pLineNo1;    // 出错行号1
            public short pLineNo2;    // 出错行号2
            public string pMessage;   // 错误信息
        }

        /// <summary>
        /// 坐标系参数 —— 用于多轴联动插补运动的坐标系配置。
        /// 通过GT_SetCrdPrm设置，GT_GetCrdPrm读取。
        /// dimension指定联动轴数（2=2轴联动，3=3轴联动），
        /// profile1~profile8指定每个轴对应的规划通道号。
        /// 【固高SDK固定结构，字段顺序不能改】
        /// </summary>
        public struct TCrdPrm
        {
            public short dimension;   // 联动维数（2=2轴, 3=3轴, 4=4轴）
            public short profile1;    // 第1轴的规划通道号，一般设1
            public short profile2;    // 第2轴的规划通道号，一般设2
            public short profile3;    // 第3轴的规划通道号，一般设3
            public short profile4;    // 第4轴的规划通道号
            public short profile5;    // 第5轴的规划通道号
            public short profile6;    // 第6轴的规划通道号
            public short profile7;    // 第7轴的规划通道号
            public short profile8;    // 第8轴的规划通道号

            public double synVelMax;  // 最大合成速度，单位pulse/ms
            public double synAccMax;  // 最大合成加速度，单位pulse/ms²
            public short evenTime;    // 匀速段时间，单位ms
            public short setOriginFlag; // 是否设置原点，1=设置，0=不设置
            public int originPos1;    // 第1轴原点位置
            public int originPos2;    // 第2轴原点位置
            public int originPos3;    // 第3轴原点位置
            public int originPos4;    // 第4轴原点位置
            public int originPos5;    // 第5轴原点位置
            public int originPos6;    // 第6轴原点位置
            public int originPos7;    // 第7轴原点位置
            public int originPos8;    // 第8轴原点位置
        }

        /// <summary>
        /// 坐标系缓冲区IO操作 —— 插补运动中同步执行的IO操作。
        /// 比如走到某个位置时开胶/关胶，就需要在插补缓冲区里插入IO操作。
        /// 【固高SDK固定结构，字段顺序不能改】
        /// </summary>
        public struct TCrdBufOperation
        {
            public short flag;      // 操作标志，1=有效
            public ushort delay;    // 延时时间，单位ms
            public short doType;    // DO类型
            public ushort doMask;   // DO掩码，指定哪些位要操作
            public ushort doValue;  // DO值，1=高电平，0=低电平
            public ushort dataExt1; // 扩展数据1
            public ushort dataExt2; // 扩展数据2
        }

        /// <summary>
        /// 坐标系插补运动数据 —— 描述一段插补运动（直线/圆弧）的完整参数。
        /// 用于前瞻缓存区，GT_InitLookAhead时传入。
        /// 【固高SDK固定结构，字段顺序不能改】
        /// </summary>
        public struct TCrdData
        {
            public short motionType;    // 运动类型：0=直线, 1=圆弧
            public short circlePlat;    // 圆弧平面：0=XY, 1=YZ, 2=ZX
            public int posX;            // 终点X坐标，单位pulse
            public int posY;            // 终点Y坐标，单位pulse
            public int posZ;            // 终点Z坐标，单位pulse
            public int posA;            // 终点A坐标，单位pulse（第4轴）
            public double radius;       // 圆弧半径，单位pulse
            public short circleDir;     // 圆弧方向：0=顺时针, 1=逆时针
            public double lCenterX;     // 圆弧圆心X（仅圆心方式有效）
            public double lCenterY;     // 圆弧圆心Y
            public double lCenterZ;     // 圆弧圆心Z
            public double vel;          // 合成速度，单位pulse/ms
            public double acc;          // 合成加速度，单位pulse/ms²
            public short velEndZero;    // 终点速度是否为零，1=零, 0=非零
            public TCrdBufOperation operation; // 缓冲区IO操作

            // 以下字段由前瞻算法内部使用，一般不需要手动设置
            public double cosX;
            public double cosY;
            public double cosZ;
            public double cosA;
            public double velEnd;           // 终点速度
            public double velEndAdjust;     // 终点速度调整值
            public double r;                // 内部使用
        }

        /// <summary>
        /// 触发器参数 —— 用于位置比较触发功能。
        /// 当编码器走到指定位置时，自动触发一个输出信号。
        /// 【固高SDK固定结构，字段顺序不能改】
        /// </summary>
        public struct TTrigger
        {
            public short encoder;       // 编码器通道号
            public short probeType;     // 探针类型
            public short probeIndex;    // 探针索引
            public short offset;        // 偏移量
            public short windowOnly;    // 是否仅在窗口内有效
            public int firstPosition;   // 首次触发位置
            public int lastPosition;    // 最后触发位置
        }

        /// <summary>
        /// 触发器状态 —— 查询触发器是否已完成触发。
        /// 【固高SDK固定结构，字段顺序不能改】
        /// </summary>
        public struct TTriggerStatus
        {
            public short execute;   // 是否正在执行
            public short done;      // 是否已完成
            public int position;    // 触发位置
        }

        /// <summary>
        /// 2D比较数据 —— 2D位置比较输出的位置数据。
        /// 【固高SDK固定结构，字段顺序不能改】
        /// </summary>
        public struct T2DCompareData
        {
            public int px;  // X方向位置，单位pulse
            public int py;  // Y方向位置，单位pulse
        }

        /// <summary>
        /// 2D比较参数 —— 2D位置比较输出的配置参数。
        /// 【固高SDK固定结构，字段顺序不能改】
        /// </summary>
        public struct T2DComparePrm
        {
            public short encx;         // X方向编码器通道
            public short ency;         // Y方向编码器通道
            public short source;       // 触发源
            public short outputType;   // 输出类型
            public short startLevel;   // 起始电平
            public short time;         // 脉冲宽度
            public short maxerr;       // 最大误差
            public short threshold;    // 阈值
        }

        // ============================================================
        // 卡片管理函数 —— 打开/关闭控制卡、获取版本信息等
        // ============================================================

        /// <summary>获取DLL版本号</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetDllVersion(short cardNum,out string pDllVersion);
        /// <summary>设置卡号映射（多卡时用）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetCardNo(short cardNum,short index);
        /// <summary>获取卡号映射</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetCardNo(short cardNum,out short index);

        /// <summary>获取固件版本号</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetVersion(short cardNum,out string pVersion);
        /// <summary>获取接口板状态</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetInterfaceBoardSts(short cardNum,out short pStatus);
        /// <summary>设置接口板类型</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetInterfaceBoardSts(short cardNum,short type);

        // ============================================================
        // 打开/关闭控制卡 —— 最常用的函数，程序启动时GT_Open，退出时GT_Close
        // ============================================================

        /// <summary>
        /// 打开控制卡。cardNum=卡号(一般0)，channel=通道号(一般0)，param=保留(0)。
        /// 程序启动时第一个调用的函数，不打开卡其他所有函数都不好使。
        /// </summary>
        [DllImport("gts.dll")]
        public static extern short GT_Open(short cardNum,short channel,short param);
        /// <summary>
        /// 关闭控制卡。程序退出时调用，释放资源。
        /// </summary>
        [DllImport("gts.dll")]
        public static extern short GT_Close(short cardNum);

        /// <summary>加载配置文件（.cfg格式，固高配置工具生成的）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_LoadConfig(short cardNum,string pFile);

        // ============================================================
        // 报警/限位/编码器等硬件配置函数
        // ============================================================

        /// <summary>关闭轴报警（清除报警信号）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_AlarmOff(short cardNum,short axis);
        /// <summary>打开轴报警（使能报警检测）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_AlarmOn(short cardNum,short axis);
        /// <summary>使能限位检测，limitType: 0=正限位, 1=负限位</summary>
        [DllImport("gts.dll")]
        public static extern short GT_LmtsOn(short cardNum,short axis, short limitType);
        /// <summary>禁能限位检测</summary>
        [DllImport("gts.dll")]
        public static extern short GT_LmtsOff(short cardNum,short axis, short limitType);
        /// <summary>设置规划位置比例系数</summary>
        [DllImport("gts.dll")]
        public static extern short GT_ProfileScale(short cardNum,short axis, short alpha, short beta);
        /// <summary>设置编码器比例系数</summary>
        [DllImport("gts.dll")]
        public static extern short GT_EncScale(short cardNum,short axis, short alpha, short beta);
        /// <summary>设置步进方向信号</summary>
        [DllImport("gts.dll")]
        public static extern short GT_StepDir(short cardNum,short step);
        /// <summary>设置步进脉冲信号</summary>
        [DllImport("gts.dll")]
        public static extern short GT_StepPulse(short cardNum,short step);
        /// <summary>设置电机偏置（DAC偏移）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetMtrBias(short cardNum,short dac, short bias);
        /// <summary>获取电机偏置</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetMtrBias(short cardNum,short dac, out short pBias);
        /// <summary>设置电机输出限幅</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetMtrLmt(short cardNum,short dac, short limit);
        /// <summary>获取电机输出限幅</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetMtrLmt(short cardNum,short dac, out short pLimit);
        /// <summary>设置编码器计数方向</summary>
        [DllImport("gts.dll")]
        public static extern short GT_EncSns(short cardNum,ushort sense);
        /// <summary>使能编码器</summary>
        [DllImport("gts.dll")]
        public static extern short GT_EncOn(short cardNum,short encoder);
        /// <summary>禁能编码器</summary>
        [DllImport("gts.dll")]
        public static extern short GT_EncOff(short cardNum,short encoder);
        /// <summary>设置跟随误差限值</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetPosErr(short cardNum,short control, int error);
        /// <summary>获取跟随误差</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetPosErr(short cardNum,short control, out int pError);
        /// <summary>设置平滑停止减速度</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetStopDec(short cardNum,short profile, double decSmoothStop, double decAbruptStop);
        /// <summary>获取平滑停止减速度</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetStopDec(short cardNum,short profile, out double pDecSmoothStop, out double pDecAbruptStop);
        /// <summary>设置限位信号灵敏度</summary>
        [DllImport("gts.dll")]
        public static extern short GT_LmtSns(short cardNum,ushort sense);
        /// <summary>设置控制模式（位置/速度/扭矩）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_CtrlMode(short cardNum,short axis, short mode);
        /// <summary>设置停止IO</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetStopIo(short cardNum,short axis, short stopType, short inputType, short inputIndex);
        /// <summary>设置通用输入灵敏度</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GpiSns(short cardNum,ushort sense);
        /// <summary>设置ADC滤波时间</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetAdcFilter(short cardNum,short adc,short filterTime);
        /// <summary>设置轴规划速度滤波</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetAxisPrfVelFilter(short cardNum,short axis,short filterNumExp);
        /// <summary>获取轴规划速度滤波</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetAxisPrfVelFilter(short cardNum,short axis,out short pFilterNumExp);
        /// <summary>设置轴编码器速度滤波</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetAxisEncVelFilter(short cardNum,short axis,short filterNumExp);
        /// <summary>获取轴编码器速度滤波</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetAxisEncVelFilter(short cardNum,short axis,out short pFilterNumExp);
        /// <summary>设置轴输入整形（减振）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetAxisInputShaping(short cardNum,short axis, short enable, short count, double k);

        // ============================================================
        // 数字输出(DO)函数 —— 控制输出信号，比如开关胶阀、使能伺服等
        // ============================================================

        /// <summary>设置DO输出值（整体设置）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetDo(short cardNum,short doType,int value);
        /// <summary>设置DO单个位输出，doIndex=位索引，value=0或1</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetDoBit(short cardNum,short doType,short doIndex,short value);
        /// <summary>获取DO输出值</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetDo(short cardNum,short doType,out int pValue);
        /// <summary>设置DO位反转输出（指定时间后自动翻转）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetDoBitReverse(short cardNum,short doType,short doIndex,short value,short reverseTime);
        /// <summary>设置DO掩码输出（只修改指定位）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetDoMask(short cardNum,short doType,ushort doMask,int value);
        /// <summary>使能DO位脉冲输出（自动发指定数量的脉冲）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_EnableDoBitPulse(short cardNum,short doType,short doIndex,ushort highLevelTime,ushort lowLevelTime,int pulseNum,short firstLevel);
        /// <summary>禁能DO位脉冲输出</summary>
        [DllImport("gts.dll")]
        public static extern short GT_DisableDoBitPulse(short cardNum,short doType, short doIndex);

        // ============================================================
        // 数字输入(DI)函数 —— 读取输入信号，比如限位、原点、报警等
        // ============================================================

        /// <summary>获取DI输入值</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetDi(short cardNum,short diType,out int pValue);
        /// <summary>获取DI反转计数（边沿计数）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetDiReverseCount(short cardNum,short diType,short diIndex,out uint reverseCount,short count);
        /// <summary>设置DI反转计数</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetDiReverseCount(short cardNum,short diType,short diIndex,ref uint reverseCount,short count);
        /// <summary>获取DI原始输入值（未经滤波）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetDiRaw(short cardNum,short diType,out int pValue);

        // ============================================================
        // 模拟量输出(DAC)/输入(ADC)函数
        // ============================================================

        /// <summary>设置DAC输出值</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetDac(short cardNum,short dac,ref short value,short count);
        /// <summary>获取DAC输出值</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetDac(short cardNum,short dac,out short value,short count,out uint pClock);

        /// <summary>获取ADC输入值（浮点）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetAdc(short cardNum,short adc,out double pValue,short count,out uint pClock);
        /// <summary>获取ADC输入值（整型）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetAdcValue(short cardNum,short adc,out short pValue,short count,out uint pClock);

        // ============================================================
        // 编码器函数 —— 读取编码器位置和速度
        // ============================================================

        /// <summary>设置编码器位置（清零或设指定值）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetEncPos(short cardNum,short encoder,int encPos);
        /// <summary>获取编码器位置，单位pulse</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetEncPos(short cardNum,short encoder,out double pValue,short count,out uint pClock);
        /// <summary>获取编码器位置（指定时钟周期）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetEncPosPre(short cardNum,short encoder,out double pValue,short count,uint pClock);
        /// <summary>获取编码器速度</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetEncVel(short cardNum,short encoder,out double pValue,short count,out uint pClock);

        // ============================================================
        // 位置捕捉函数 —— 硬件级锁存编码器位置，精度极高
        // ============================================================

        /// <summary>设置捕捉模式（原点/Index/探针等）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetCaptureMode(short cardNum,short encoder,short mode);
        /// <summary>获取捕捉模式</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetCaptureMode(short cardNum,short encoder,out short pMode,short count);
        /// <summary>获取捕捉状态和捕捉位置</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetCaptureStatus(short cardNum,short encoder,out short pStatus,out int pValue,short count,out uint pClock);
        /// <summary>设置捕捉灵敏度</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetCaptureSense(short cardNum,short encoder, short mode,short sense);
        /// <summary>清除捕捉状态</summary>
        [DllImport("gts.dll")]
        public static extern short GT_ClearCaptureStatus(short cardNum,short encoder);
        /// <summary>设置重复捕捉次数</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetCaptureRepeat(short cardNum,short encoder,short count);
        /// <summary>获取重复捕捉状态</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetCaptureRepeatStatus(short cardNum,short encoder,out short pCount);
        /// <summary>获取重复捕捉位置</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetCaptureRepeatPos(short cardNum,short encoder, out int pValue, short startNum, short count);
        /// <summary>设置捕捉编码器通道</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetCaptureEncoder(short cardNum,short trigger,short encoder);
        /// <summary>获取捕捉脉冲宽度</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetCaptureWidth(short cardNum,short trigger,out short pWidth,short count);
        /// <summary>获取原点和GPI捕捉状态</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetCaptureHomeGpi(short cardNum,short trigger,out short pHomeSts,out short pHomePos,out short pGpiSts,out short pGpiPos,short count);

        // ============================================================
        // 系统函数 —— 复位、时钟等
        // ============================================================

        /// <summary>复位控制卡（恢复初始状态）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_Reset(short cardNum);
        /// <summary>获取控制卡时钟（用于同步读取）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetClock(short cardNum,out uint pClock,out uint pLoop);
        /// <summary>获取高精度时钟</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetClockHighPrecision(short cardNum,out uint pClock);

        // ============================================================
        // 轴状态和控制函数 —— 最常用的函数组之一
        // ============================================================

        /// <summary>
        /// 获取轴状态字。状态字里包含各种标志位：运动中、到位、报警、限位等。
        /// 需要配合位掩码来解析具体标志。
        /// </summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetSts(short cardNum,short axis,out int pSts,short count,out uint pClock);
        /// <summary>清除轴状态字</summary>
        [DllImport("gts.dll")]
        public static extern short GT_ClrSts(short cardNum,short axis,short count);
        /// <summary>
        /// 轴使能（伺服ON）。使能后电机才能接受运动指令。
        /// 开机后第一步就是调这个函数。
        /// </summary>
        [DllImport("gts.dll")]
        public static extern short GT_AxisOn(short cardNum,short axis);
        /// <summary>
        /// 轴禁能（伺服OFF）。禁能后电机处于自由状态，不能运动。
        /// </summary>
        [DllImport("gts.dll")]
        public static extern short GT_AxisOff(short cardNum,short axis);
        /// <summary>
        /// 停止轴运动。mask=轴掩码（bit0=轴1, bit1=轴2...），option=停止方式。
        /// 常用：mask=1<<0停止轴1，option=0平滑停止。
        /// </summary>
        [DllImport("gts.dll")]
        public static extern short GT_Stop(short cardNum,int mask,int option);
        /// <summary>设置规划位置</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetPrfPos(short cardNum,short profile,int prfPos);
        /// <summary>同步轴位置（把规划位置同步到编码器位置）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SynchAxisPos(short cardNum,int mask);
        /// <summary>清零轴位置</summary>
        [DllImport("gts.dll")]
        public static extern short GT_ZeroPos(short cardNum,short axis,short count);

        // ============================================================
        // 软限位/间隙补偿/丝杠补偿函数
        // ============================================================

        /// <summary>设置软限位位置，单位pulse</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetSoftLimit(short cardNum,short axis,int positive,int negative);
        /// <summary>获取软限位位置</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetSoftLimit(short cardNum,short axis,out int pPositive,out int pNegative);
        /// <summary>设置轴位置误差带（到位判定范围）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetAxisBand(short cardNum,short axis,int band,int time);
        /// <summary>获取轴位置误差带</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetAxisBand(short cardNum,short axis,out int pBand,out int pTime);
        /// <summary>设置反向间隙补偿</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetBacklash(short cardNum,short axis,int compValue,double compChangeValue,int compDir);
        /// <summary>获取反向间隙补偿</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetBacklash(short cardNum,short axis,out int pCompValue,out double pCompChangeValue,out int pCompDir);
        /// <summary>设置丝杠误差补偿表</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetLeadScrewComp(short cardNum,short axis,short n,int startPos,int lenPos,out int pCompPos,out int pCompNeg);
        /// <summary>使能丝杠误差补偿</summary>
        [DllImport("gts.dll")]
        public static extern short GT_EnableLeadScrewComp(short cardNum,short axis,short mode);
        /// <summary>获取各种补偿值</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetCompensate(short cardNum,short axis, out double pPitchError, out double pCrossError, out double pBacklashError, out double pEncPos, out double pPrfPos);

        // ============================================================
        // 龙门同步控制函数 —— 双驱同步
        // ============================================================

        /// <summary>使能龙门同步</summary>
        [DllImport("gts.dll")]
        public static extern short GT_EnableGantry(short cardNum,short gantryMaster,short gantrySlave,double masterKp,double slaveKp);
        /// <summary>禁能龙门同步</summary>
        [DllImport("gts.dll")]
        public static extern short GT_DisableGantry(short cardNum);
        /// <summary>设置龙门同步误差限值</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetGantryErrLmt(short cardNum,int gantryErrLmt);
        /// <summary>获取龙门同步误差限值</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetGantryErrLmt(short cardNum,out int pGantryErrLmt);

        // ============================================================
        // 规划位置/速度/加速度读取函数
        // ============================================================

        /// <summary>获取规划位置</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetPrfPos(short cardNum,short profile,out double pValue,short count,out uint pClock);
        /// <summary>获取规划速度</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetPrfVel(short cardNum,short profile,out double pValue,short count,out uint pClock);
        /// <summary>获取规划加速度</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetPrfAcc(short cardNum,short profile,out double pValue,short count,out uint pClock);
        /// <summary>获取规划模式</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetPrfMode(short cardNum,short profile,out int pValue,short count,out uint pClock);

        // ============================================================
        // 轴实际位置/速度/加速度读取函数 —— 界面显示用的
        // ============================================================

        /// <summary>获取轴规划位置（考虑了运动学转换后的）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetAxisPrfPos(short cardNum,short axis,out double pValue,short count,out uint pClock);
        /// <summary>获取轴规划速度</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetAxisPrfVel(short cardNum,short axis,out double pValue,short count,out uint pClock);
        /// <summary>获取轴规划加速度</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetAxisPrfAcc(short cardNum,short axis,out double pValue,short count,out uint pClock);
        /// <summary>获取轴编码器位置（实际位置）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetAxisEncPos(short cardNum,short axis,out double pValue,short count,out uint pClock);
        /// <summary>获取轴编码器速度（实际速度）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetAxisEncVel(short cardNum,short axis,out double pValue,short count,out uint pClock);
        /// <summary>获取轴编码器加速度</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetAxisEncAcc(short cardNum,short axis,out double pValue,short count,out uint pClock);
        /// <summary>获取轴跟随误差（规划位置-编码器位置）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetAxisError(short cardNum,short axis,out double pValue,short count,out uint pClock);

        // ============================================================
        // 变量读写函数 —— 控制卡上的用户变量
        // ============================================================

        /// <summary>设置长整型变量</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetLongVar(short cardNum,short index,int value);
        /// <summary>获取长整型变量</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetLongVar(short cardNum,short index,out int pValue);
        /// <summary>设置双精度变量</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetDoubleVar(short cardNum,short index,double pValue);
        /// <summary>获取双精度变量</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetDoubleVar(short cardNum,short index, out double pValue);

        // ============================================================
        // 控制滤波器和PID设置函数
        // ============================================================

        /// <summary>设置控制滤波器</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetControlFilter(short cardNum,short control,short index);
        /// <summary>获取控制滤波器</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetControlFilter(short cardNum,short control,out short pIndex);

        /// <summary>设置PID参数</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetPid(short cardNum,short control,short index,ref TPid pPid);
        /// <summary>获取PID参数</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetPid(short cardNum,short control,short index,out TPid pPid);

        /// <summary>设置速度前馈滤波器</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetKvffFilter(short cardNum,short control,short index,short kvffFilterExp,double accMax);
        /// <summary>获取速度前馈滤波器</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetKvffFilter(short cardNum,short control, short index, out short pKvffFilterExp, out double pAccMax);

        // ============================================================
        // 运动规划函数 —— 设置目标位置/速度，启动运动
        // ============================================================

        /// <summary>
        /// 更新运动参数。设置完位置/速度/加速度后，调这个函数让控制卡开始执行。
        /// mask=轴掩码，指定哪些轴开始运动。
        /// </summary>
        [DllImport("gts.dll")]
        public static extern short GT_Update(short cardNum,int mask);
        /// <summary>设置目标位置，单位pulse</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetPos(short cardNum,short profile,int pos);
        /// <summary>获取目标位置</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetPos(short cardNum,short profile,out int pPos);
        /// <summary>设置目标速度</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetVel(short cardNum,short profile,double vel);
        /// <summary>获取目标速度</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetVel(short cardNum,short profile,out double pVel);

        // ============================================================
        // Trap模式（梯形速度曲线定位运动）—— 最常用的运动模式
        // ============================================================

        /// <summary>设置规划模式为Trap（梯形速度曲线）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_PrfTrap(short cardNum,short profile);
        /// <summary>设置Trap运动参数</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetTrapPrm(short cardNum,short profile,ref TTrapPrm pPrm);
        /// <summary>获取Trap运动参数</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetTrapPrm(short cardNum,short profile,out TTrapPrm pPrm);

        // ============================================================
        // Jog模式（点动运动）—— 手动按方向键移动
        // ============================================================

        /// <summary>设置规划模式为Jog（点动）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_PrfJog(short cardNum,short profile);
        /// <summary>设置Jog运动参数</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetJogPrm(short cardNum,short profile,ref TJogPrm pPrm);
        /// <summary>获取Jog运动参数</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetJogPrm(short cardNum,short profile,out TJogPrm pPrm);

        // ============================================================
        // PT模式（位置-时间模式）—— 逐点给定位置和时间
        // ============================================================

        /// <summary>设置规划模式为PT，mode: 0=静态, 1=动态</summary>
        [DllImport("gts.dll")]
        public static extern short GT_PrfPt(short cardNum,short profile,short mode);
        /// <summary>设置PT循环次数</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetPtLoop(short cardNum,short profile,int loop);
        /// <summary>获取PT循环次数</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetPtLoop(short cardNum,short profile,out int pLoop);
        /// <summary>获取PT缓冲区剩余空间</summary>
        [DllImport("gts.dll")]
        public static extern short GT_PtSpace(short cardNum,short profile,out short pSpace,short fifo);
        /// <summary>往PT缓冲区写入一个数据点</summary>
        [DllImport("gts.dll")]
        public static extern short GT_PtData(short cardNum,short profile,double pos,int time,short type,short fifo);
        /// <summary>往PT缓冲区写入一个带段号的数据点</summary>
        [DllImport("gts.dll")]
        public static extern short GT_PtDataWN(short cardNum,short profile,double pos,int time,short type,int segNum,short fifo);
        /// <summary>清空PT缓冲区</summary>
        [DllImport("gts.dll")]
        public static extern short GT_PtClear(short cardNum,short profile,short fifo);
        /// <summary>启动PT运动</summary>
        [DllImport("gts.dll")]
        public static extern short GT_PtStart(short cardNum,int mask,int option);
        /// <summary>设置PT内存模式</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetPtMemory(short cardNum,short profile,short memory);
        /// <summary>获取PT内存模式</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetPtMemory(short cardNum,short profile,out short pMemory);
        /// <summary>获取PT当前段号</summary>
        [DllImport("gts.dll")]
        public static extern short GT_PtGetSegNum(short cardNum,short profile, out int pSegNum);

        // ============================================================
        // 电子齿轮模式 —— 从轴按比例跟随主轴
        // ============================================================

        /// <summary>设置规划模式为Gear（电子齿轮）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_PrfGear(short cardNum,short profile,short dir);
        /// <summary>设置齿轮主轴</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetGearMaster(short cardNum,short profile,short masterIndex,short masterType,short masterItem);
        /// <summary>获取齿轮主轴</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetGearMaster(short cardNum,short profile,out short pMasterIndex,out short pMasterType,out short pMasterItem);
        /// <summary>设置齿轮比</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetGearRatio(short cardNum,short profile,int masterEven,int slaveEven,int masterSlope);
        /// <summary>获取齿轮比</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetGearRatio(short cardNum,short profile,out int pMasterEven,out int pSlaveEven,out int pMasterSlope);
        /// <summary>启动齿轮运动</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GearStart(short cardNum,int mask);
        /// <summary>设置齿轮事件</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetGearEvent(short cardNum,short profile,short gearEvent,int startPara0,int startPara1);
        /// <summary>获取齿轮事件</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetGearEvent(short cardNum,short profile, out short pEvent,out int pStartPara0, out int pStartPara1);

        // ============================================================
        // 电子凸轮模式 —— 从轴按凸轮曲线跟随主轴
        // ============================================================

        /// <summary>设置规划模式为Follow（电子凸轮）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_PrfFollow(short cardNum,short profile,short dir);
        /// <summary>设置凸轮主轴</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetFollowMaster(short cardNum,short profile,short masterIndex,short masterType,short masterItem);
        /// <summary>获取凸轮主轴</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetFollowMaster(short cardNum,short profile,out short pMasterIndex,out short pMasterType,out short pMasterItem);
        /// <summary>设置凸轮循环次数</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetFollowLoop(short cardNum,short profile,int loop);
        /// <summary>获取凸轮循环次数</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetFollowLoop(short cardNum,short profile,out int pLoop);
        /// <summary>设置凸轮事件</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetFollowEvent(short cardNum,short profile,short followEvent,short masterDir,int pos);
        /// <summary>获取凸轮事件</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetFollowEvent(short cardNum,short profile,out short pFollowEvent,out short pMasterDir,out int pPos);
        /// <summary>获取凸轮缓冲区剩余空间</summary>
        [DllImport("gts.dll")]
        public static extern short GT_FollowSpace(short cardNum,short profile,out short pSpace,short fifo);
        /// <summary>往凸轮缓冲区写入数据</summary>
        [DllImport("gts.dll")]
        public static extern short GT_FollowData(short cardNum,short profile,int masterSegment,double slaveSegment,short type,short fifo);
        /// <summary>清空凸轮缓冲区</summary>
        [DllImport("gts.dll")]
        public static extern short GT_FollowClear(short cardNum,short profile,short fifo);
        /// <summary>启动凸轮运动</summary>
        [DllImport("gts.dll")]
        public static extern short GT_FollowStart(short cardNum,int mask,int option);
        /// <summary>切换凸轮缓冲区</summary>
        [DllImport("gts.dll")]
        public static extern short GT_FollowSwitch(short cardNum,int mask);
        /// <summary>设置凸轮内存模式</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetFollowMemory(short cardNum,short profile,short memory);
        /// <summary>获取凸轮内存模式</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetFollowMemory(short cardNum,short profile,out short memory);
        /// <summary>获取凸轮运行状态</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetFollowStatus(short cardNum,short profile, out short pFifoNum, out short pSwitchStatus);
        /// <summary>设置凸轮相位</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetFollowPhasing(short cardNum,short profile, short profilePhasing);
        /// <summary>获取凸轮相位</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetFollowPhasing(short cardNum,short profile, out short pProfilePhasing);

        // ============================================================
        // 运动程序编译/下载/运行函数 —— 在控制卡上运行用户程序
        // ============================================================

        /// <summary>编译运动程序</summary>
        [DllImport("gts.dll")]
        public static extern short GT_Compile(short cardNum,string pFileName, out TCompileInfo pWrongInfo);
        /// <summary>下载运动程序到控制卡</summary>
        [DllImport("gts.dll")]
        public static extern short GT_Download(short cardNum,string pFileName);

        /// <summary>获取函数ID</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetFunId(short cardNum,string pFunName,out short pFunId);
        /// <summary>绑定函数到线程</summary>
        [DllImport("gts.dll")]
        public static extern short GT_Bind(short cardNum,short thread,short funId, short page);

        /// <summary>运行线程</summary>
        [DllImport("gts.dll")]
        public static extern short GT_RunThread(short cardNum,short thread);
        /// <summary>停止线程</summary>
        [DllImport("gts.dll")]
        public static extern short GT_StopThread(short cardNum,short thread);
        /// <summary>暂停线程</summary>
        [DllImport("gts.dll")]
        public static extern short GT_PauseThread(short cardNum,short thread);

        /// <summary>获取线程状态</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetThreadSts(short cardNum,short thread,out TThreadSts pThreadSts);

        /// <summary>获取变量ID</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetVarId(short cardNum,string pFunName,string pVarName,out TVarInfo pVarInfo);
        /// <summary>设置变量值</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetVarValue(short cardNum,short page,ref TVarInfo pVarInfo,ref double pValue,short count);
        /// <summary>获取变量值</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetVarValue(short cardNum,short page,ref TVarInfo pVarInfo,out double pValue,short count);

        // ============================================================
        // 坐标系插补运动函数 —— 多轴联动（直线/圆弧/螺旋线）
        // 这是我们点胶轨迹执行用的核心函数组
        // ============================================================

        /// <summary>设置坐标系参数</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetCrdPrm(short cardNum,short crd, ref TCrdPrm pCrdPrm);
        /// <summary>获取坐标系参数</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetCrdPrm(short cardNum,short crd,out TCrdPrm pCrdPrm);
        /// <summary>获取坐标系缓冲区剩余空间</summary>
        [DllImport("gts.dll")]
        public static extern short GT_CrdSpace(short cardNum,short crd,out int pSpace,short fifo);
        /// <summary>往坐标系缓冲区写入运动数据（通用接口）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_CrdData(short cardNum,short crd,System.IntPtr pCrdData,short fifo);
        /// <summary>往坐标系缓冲区写入圆弧运动数据</summary>
        [DllImport("gts.dll")]
        public static extern short GT_CrdDataCircle(short cardNum,short crd, ref TCrdData pCrdData, short fifo);

        // ---- 直线插补函数 ----

        /// <summary>2轴直线插补（XY）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_LnXY(short cardNum,short crd, int x, int y, double synVel, double synAcc, double velEnd, short fifo);
        /// <summary>3轴直线插补（XYZ）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_LnXYZ(short cardNum,short crd, int x, int y, int z, double synVel, double synAcc, double velEnd, short fifo);
        /// <summary>4轴直线插补（XYZA）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_LnXYZA(short cardNum,short crd, int x, int y, int z, int a, double synVel, double synAcc, double velEnd, short fifo);
        /// <summary>2轴快速直线（G0，终点速度不限）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_LnXYG0(short cardNum,short crd, int x, int y, double synVel, double synAcc, short fifo);
        /// <summary>3轴快速直线（G0）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_LnXYZG0(short cardNum,short crd, int x, int y, int z, double synVel, double synAcc, short fifo);
        /// <summary>4轴快速直线（G0）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_LnXYZAG0(short cardNum,short crd, int x, int y, int z, int a, double synVel, double synAcc, short fifo);

        // ---- 圆弧插补函数 ----

        /// <summary>XY平面圆弧插补（半径方式）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_ArcXYR(short cardNum,short crd, int x, int y, double radius, short circleDir, double synVel, double synAcc, double velEnd, short fifo);
        /// <summary>XY平面圆弧插补（圆心方式）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_ArcXYC(short cardNum,short crd, int x, int y, double xCenter, double yCenter, short circleDir, double synVel, double synAcc, double velEnd, short fifo);
        /// <summary>YZ平面圆弧插补（半径方式）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_ArcYZR(short cardNum,short crd, int y, int z, double radius, short circleDir, double synVel, double synAcc, double velEnd, short fifo);
        /// <summary>YZ平面圆弧插补（圆心方式）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_ArcYZC(short cardNum,short crd, int y, int z, double yCenter, double zCenter, short circleDir, double synVel, double synAcc, double velEnd, short fifo);
        /// <summary>ZX平面圆弧插补（半径方式）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_ArcZXR(short cardNum,short crd, int z, int x, double radius, short circleDir, double synVel, double synAcc, double velEnd, short fifo);
        /// <summary>ZX平面圆弧插补（圆心方式）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_ArcZXC(short cardNum,short crd, int z, int x, double zCenter, double xCenter, short circleDir, double synVel, double synAcc, double velEnd, short fifo);
        /// <summary>3D圆弧插补（经过中间点方式）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_ArcXYZ(short cardNum,short crd,int x,int y,int z,double interX,double interY,double interZ,double synVel,double synAcc,double velEnd,short fifo);

        // ---- Override2系列：支持运行中修改速度的直线插补 ----

        [DllImport("gts.dll")]
        public static extern short GT_LnXYOverride2(short cardNum,short crd,int x,int y,double synVel,double synAcc,double velEnd,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_LnXYZOverride2(short cardNum,short crd,int x,int y,int z,double synVel,double synAcc,double velEnd,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_LnXYZAOverride2(short cardNum,short crd,int x,int y,int z,int a,double synVel,double synAcc,double velEnd,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_LnXYG0Override2(short cardNum,short crd,int x,int y,double synVel,double synAcc,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_LnXYZG0Override2(short cardNum,short crd,int x,int y,int z,double synVel,double synAcc,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_LnXYZAG0Override2(short cardNum,short crd,int x,int y,int z,int a,double synVel,double synAcc,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_ArcXYROverride2(short cardNum,short crd,int x,int y,double radius,short circleDir,double synVel,double synAcc,double velEnd,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_ArcXYCOverride2(short cardNum,short crd,int x,int y,double xCenter,double yCenter,short circleDir,double synVel,double synAcc,double velEnd,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_ArcYZROverride2(short cardNum,short crd,int y,int z,double radius,short circleDir,double synVel,double synAcc,double velEnd,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_ArcYZCOverride2(short cardNum,short crd,int y,int z,double yCenter,double zCenter,short circleDir,double synVel,double synAcc,double velEnd,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_ArcZXROverride2(short cardNum,short crd,int z,int x,double radius,short circleDir,double synVel,double synAcc,double velEnd,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_ArcZXCOverride2(short cardNum,short crd,int z,int x,double zCenter,double xCenter,short circleDir,double synVel,double synAcc,double velEnd,short fifo);

        // ---- 螺旋线插补函数 ----

        /// <summary>XY圆弧+Z直线螺旋线（半径方式）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_HelixXYRZ(short cardNum,short crd,int x,int y,int z,double radius,short circleDir,double synVel,double synAcc,double velEnd,short fifo);
        /// <summary>XY圆弧+Z直线螺旋线（圆心方式）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_HelixXYCZ(short cardNum,short crd,int x,int y,int z,double xCenter,double yCenter,short circleDir,double synVel,double synAcc,double velEnd,short fifo);
        /// <summary>YZ圆弧+X直线螺旋线（半径方式）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_HelixYZRX(short cardNum,short crd,int x,int y,int z,double radius,short circleDir,double synVel,double synAcc,double velEnd,short fifo);
        /// <summary>YZ圆弧+X直线螺旋线（圆心方式）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_HelixYZCX(short cardNum,short crd,int x,int y,int z,double yCenter,double zCenter,short circleDir,double synVel,double synAcc,double velEnd,short fifo);
        /// <summary>ZX圆弧+Y直线螺旋线（半径方式）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_HelixZXRY(short cardNum,short crd,int x,int y,int z,double radius,short circleDir,double synVel,double synAcc,double velEnd,short fifo);
        /// <summary>ZX圆弧+Y直线螺旋线（圆心方式）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_HelixZXCY(short cardNum,short crd,int x,int y,int z,double zCenter,double xCenter,short circleDir,double synVel,double synAcc,double velEnd,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_HelixXYRZOverride2(short cardNum,short crd,int x,int y,int z,double radius,short circleDir,double synVel,double synAcc,double velEnd,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_HelixXYCZOverride2(short cardNum,short crd,int x,int y,int z,double xCenter,double yCenter,short circleDir,double synVel,double synAcc,double velEnd,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_HelixYZRXOverride2(short cardNum,short crd,int x,int y,int z,double radius,short circleDir,double synVel,double synAcc,double velEnd,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_HelixYZCXOverride2(short cardNum,short crd,int x,int y,int z,double yCenter,double zCenter,short circleDir,double synVel,double synAcc,double velEnd,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_HelixZXRYOverride2(short cardNum,short crd,int x,int y,int z,double radius,short circleDir,double synVel,double synAcc,double velEnd,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_HelixZXCYOverride2(short cardNum,short crd,int x,int y,int z,double zCenter,double xCenter,short circleDir,double synVel,double synAcc,double velEnd,short fifo);

        // ---- 带段号(WN)的插补函数：可以给每段运动编个号，方便追踪执行到哪段了 ----

        [DllImport("gts.dll")]
        public static extern short GT_LnXYWN(short cardNum,short crd,int x,int y,double synVel,double synAcc,double velEnd,int segNum,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_LnXYZWN(short cardNum,short crd,int x,int y,int z,double synVel,double synAcc,double velEnd,int segNum,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_LnXYZAWN(short cardNum,short crd,int x,int y,int z,int a,double synVel,double synAcc,double velEnd,int segNum,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_LnXYG0WN(short cardNum,short crd,int x,int y,double synVel,double synAcc,int segNum,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_LnXYZG0WN(short cardNum,short crd,int x,int y,int z,double synVel,double synAcc,int segNum,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_LnXYZAG0WN(short cardNum,short crd,int x,int y,int z,int a,double synVel,double synAcc,int segNum,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_ArcXYRWN(short cardNum,short crd,int x,int y,double radius,short circleDir,double synVel,double synAcc,double velEnd,int segNum,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_ArcXYCWN(short cardNum,short crd,int x,int y,double xCenter,double yCenter,short circleDir,double synVel,double synAcc,double velEnd,int segNum,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_ArcYZRWN(short cardNum,short crd,int y,int z,double radius,short circleDir,double synVel,double synAcc,double velEnd,int segNum,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_ArcYZCWN(short cardNum,short crd,int y,int z,double yCenter,double zCenter,short circleDir,double synVel,double synAcc,double velEnd,int segNum,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_ArcZXRWN(short cardNum,short crd,int z,int x,double radius,short circleDir,double synVel,double synAcc,double velEnd,int segNum,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_ArcZXCWN(short cardNum,short crd,int z,int x,double zCenter,double xCenter,short circleDir,double synVel,double synAcc,double velEnd,int segNum,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_ArcXYZWN(short cardNum,short crd,int x,int y,int z,double interX,double interY,double interZ,double synVel,double synAcc,double velEnd,int segNum,short fifo);

        // ---- Override2+WN组合：同时支持速度倍率和段号 ----

        [DllImport("gts.dll")]
        public static extern short GT_LnXYOverride2WN(short cardNum,short crd,int x,int y,double synVel,double synAcc,double velEnd,int segNum,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_LnXYZOverride2WN(short cardNum,short crd,int x,int y,int z,double synVel,double synAcc,double velEnd,int segNum,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_LnXYZAOverride2WN(short cardNum,short crd,int x,int y,int z,int a,double synVel,double synAcc,double velEnd,int segNum,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_LnXYG0Override2WN(short cardNum,short crd,int x,int y,double synVel,double synAcc,int segNum,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_LnXYZG0Override2WN(short cardNum,short crd,int x,int y,int z,double synVel,double synAcc,int segNum,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_LnXYZAG0Override2WN(short cardNum,short crd,int x,int y,int z,int a,double synVel,double synAcc,int segNum,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_ArcXYROverride2WN(short cardNum,short crd,int x,int y,double radius,short circleDir,double synVel,double synAcc,double velEnd,int segNum,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_ArcXYCOverride2WN(short cardNum,short crd,int x,int y,double xCenter,double yCenter,short circleDir,double synVel,double synAcc,double velEnd,int segNum,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_ArcYZROverride2WN(short cardNum,short crd,int y,int z,double radius,short circleDir,double synVel,double synAcc,double velEnd,int segNum,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_ArcYZCOverride2WN(short cardNum,short crd,int y,int z,double yCenter,double zCenter,short circleDir,double synVel,double synAcc,double velEnd,int segNum,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_ArcZXROverride2WN(short cardNum,short crd,int z,int x,double radius,short circleDir,double synVel,double synAcc,double velEnd,int segNum,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_ArcZXCOverride2WN(short cardNum,short crd,int z,int x,double zCenter,double xCenter,short circleDir,double synVel,double synAcc,double velEnd,int segNum,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_HelixXYRZWN(short cardNum,short crd,int x,int y,int z,double radius,short circleDir,double synVel,double synAcc,double velEnd,int segNum,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_HelixXYCZWN(short cardNum,short crd,int x,int y,int z,double xCenter,double yCenter,short circleDir,double synVel,double synAcc,double velEnd,int segNum,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_HelixYZRXWN(short cardNum,short crd,int x,int y,int z,double radius,short circleDir,double synVel,double synAcc,double velEnd,int segNum,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_HelixYZCXWN(short cardNum,short crd,int x,int y,int z,double yCenter,double zCenter,short circleDir,double synVel,double synAcc,double velEnd,int segNum,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_HelixZXRYWN(short cardNum,short crd,int x,int y,int z,double radius,short circleDir,double synVel,double synAcc,double velEnd,int segNum,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_HelixZXCYWN(short cardNum,short crd,int x,int y,int z,double zCenter,double xCenter,short circleDir,double synVel,double synAcc,double velEnd,int segNum,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_HelixXYRZOverride2WN(short cardNum,short crd,int x,int y,int z,double radius,short circleDir,double synVel,double synAcc,double velEnd,int segNum,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_HelixXYCZOverride2WN(short cardNum,short crd,int x,int y,int z,double xCenter,double yCenter,short circleDir,double synVel,double synAcc,double velEnd,int segNum,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_HelixYZRXOverride2WN(short cardNum,short crd,int x,int y,int z,double radius,short circleDir,double synVel,double synAcc,double velEnd,int segNum,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_HelixYZCXOverride2WN(short cardNum,short crd,int x,int y,int z,double yCenter,double zCenter,short circleDir,double synVel,double synAcc,double velEnd,int segNum,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_HelixZXRYOverride2WN(short cardNum,short crd,int x,int y,int z,double radius,short circleDir,double synVel,double synAcc,double velEnd,int segNum,short fifo);
        [DllImport("gts.dll")]
        public static extern short GT_HelixZXCYOverride2WN(short cardNum,short crd,int x,int y,int z,double zCenter,double xCenter,short circleDir,double synVel,double synAcc,double velEnd,int segNum,short fifo);

        // ============================================================
        // 坐标系缓冲区IO操作函数 —— 插补运动中同步执行IO
        // 比如走到指定位置时开关胶阀，就需要用这些函数
        // ============================================================

        /// <summary>缓冲区IO输出</summary>
        [DllImport("gts.dll")]
        public static extern short GT_BufIO(short cardNum,short crd, ushort doType, ushort doMask, ushort doValue, short fifo);
        /// <summary>缓冲区使能DO位脉冲</summary>
        [DllImport("gts.dll")]
        public static extern short GT_BufEnableDoBitPulse(short cardNum,short crd,short doType,short doIndex,ushort highLevelTime,ushort lowLevelTime,int pulseNum,short firstLevel,short fifo);
        /// <summary>缓冲区禁能DO位脉冲</summary>
        [DllImport("gts.dll")]
        public static extern short GT_BufDisableDoBitPulse(short cardNum,short crd,short doType,short doIndex,short fifo);
        /// <summary>缓冲区延时</summary>
        [DllImport("gts.dll")]
        public static extern short GT_BufDelay(short cardNum,short crd, ushort delayTime, short fifo);
        /// <summary>缓冲区比较脉冲输出</summary>
        [DllImport("gts.dll")]
        public static extern short GT_BufComparePulse(short cardNum,short crd,short level,short outputType,short time,short fifo);
        /// <summary>缓冲区DA输出</summary>
        [DllImport("gts.dll")]
        public static extern short GT_BufDA(short cardNum,short crd, short chn, short daValue, short fifo);
        /// <summary>缓冲区使能限位</summary>
        [DllImport("gts.dll")]
        public static extern short GT_BufLmtsOn(short cardNum,short crd, short axis, short limitType, short fifo);
        /// <summary>缓冲区禁能限位</summary>
        [DllImport("gts.dll")]
        public static extern short GT_BufLmtsOff(short cardNum,short crd, short axis, short limitType, short fifo);
        /// <summary>缓冲区设置停止IO</summary>
        [DllImport("gts.dll")]
        public static extern short GT_BufSetStopIo(short cardNum,short crd, short axis, short stopType, short inputType, short inputIndex, short fifo);
        /// <summary>缓冲区单轴运动</summary>
        [DllImport("gts.dll")]
        public static extern short GT_BufMove(short cardNum,short crd, short moveAxis, int pos, double vel, double acc, short modal, short fifo);
        /// <summary>缓冲区齿轮运动</summary>
        [DllImport("gts.dll")]
        public static extern short GT_BufGear(short cardNum,short crd, short gearAxis, int pos, short fifo);
        /// <summary>缓冲区齿轮运动（百分比加减速）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_BufGearPercent(short cardNum,short crd,short gearAxis,int pos,short accPercent,short decPercent,short fifo);
        /// <summary>缓冲区停止运动</summary>
        [DllImport("gts.dll")]
        public static extern short GT_BufStopMotion(short cardNum,short crd,short fifo);
        /// <summary>缓冲区设置变量值</summary>
        [DllImport("gts.dll")]
        public static extern short GT_BufSetVarValue(short cardNum,short crd,short pageId,out TVarInfo pVarInfo,double value,short fifo);
        /// <summary>缓冲区跳转下一段</summary>
        [DllImport("gts.dll")]
        public static extern short GT_BufJumpNextSeg(short cardNum,short crd,short axis,short limitType,short fifo);
        /// <summary>缓冲区同步规划位置</summary>
        [DllImport("gts.dll")]
        public static extern short GT_BufSynchPrfPos(short cardNum,short crd,short encoder,short profile,short fifo);
        /// <summary>缓冲区虚拟转实际</summary>
        [DllImport("gts.dll")]
        public static extern short GT_BufVirtualToActual(short cardNum,short crd,short fifo);
        /// <summary>缓冲区设置长整型变量</summary>
        [DllImport("gts.dll")]
        public static extern short GT_BufSetLongVar(short cardNum,short crd,short index,int value,short fifo);
        /// <summary>缓冲区设置双精度变量</summary>
        [DllImport("gts.dll")]
        public static extern short GT_BufSetDoubleVar(short cardNum,short crd,short index,double value,short fifo);

        // ============================================================
        // 坐标系运动启动/控制函数
        // ============================================================

        /// <summary>启动坐标系插补运动</summary>
        [DllImport("gts.dll")]
        public static extern short GT_CrdStart(short cardNum,short mask,short option);
        /// <summary>启动坐标系单步运动</summary>
        [DllImport("gts.dll")]
        public static extern short GT_CrdStartStep(short cardNum,short mask, short option);
        /// <summary>设置坐标系单步模式</summary>
        [DllImport("gts.dll")]
        public static extern short GT_CrdStepMode(short cardNum,short mask, short option);
        /// <summary>设置坐标系速度倍率（0~1之间，实时调速）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetOverride(short cardNum,short crd,double synVelRatio);
        /// <summary>设置坐标系速度倍率2（改进版）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetOverride2(short cardNum,short crd, double synVelRatio);

        // ============================================================
        // 前瞻函数 —— 插补运动的速度规划优化
        // ============================================================

        /// <summary>初始化前瞻缓存区。T=插补周期，accMax=最大加速度，n=缓存段数</summary>
        [DllImport("gts.dll")]
        public static extern short GT_InitLookAhead(short cardNum,short crd,short fifo,double T,double accMax,short n,ref TCrdData pLookAheadBuf);
        /// <summary>获取前瞻缓存区剩余空间</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetLookAheadSpace(short cardNum,short crd,out int pSpace,short fifo);
        /// <summary>获取前瞻已处理段数</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetLookAheadSegCount(short cardNum,short crd,out int pSegCount,short fifo);
        /// <summary>清空坐标系缓冲区</summary>
        [DllImport("gts.dll")]
        public static extern short GT_CrdClear(short cardNum,short crd,short fifo);
        /// <summary>获取坐标系运行状态</summary>
        [DllImport("gts.dll")]
        public static extern short GT_CrdStatus(short cardNum,short crd,out short pRun,out int pSegment,short fifo);
        /// <summary>设置用户段号</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetUserSegNum(short cardNum,short crd,int segNum,short fifo);
        /// <summary>获取当前执行到的用户段号</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetUserSegNum(short cardNum,short crd,out int pSegment,short fifo);
        /// <summary>获取当前执行到的用户段号（带WN版本）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetUserSegNumWN(short cardNum,short crd,out int pSegment,short fifo);
        /// <summary>获取剩余段数</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetRemainderSegNum(short cardNum,short crd,out int pSegment,short fifo);
        /// <summary>设置坐标系停止减速度</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetCrdStopDec(short cardNum,short crd,double decSmoothStop,double decAbruptStop);
        /// <summary>获取坐标系停止减速度</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetCrdStopDec(short cardNum,short crd,out double pDecSmoothStop,out double pDecAbruptStop);
        /// <summary>设置坐标系限位停止模式</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetCrdLmtStopMode(short cardNum,short crd,short lmtStopMode);
        /// <summary>获取坐标系限位停止模式</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetCrdLmtStopMode(short cardNum,short crd,out short pLmtStopMode);
        /// <summary>获取用户目标速度</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetUserTargetVel(short cardNum,short crd,out double pTargetVel);
        /// <summary>获取段目标位置</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetSegTargetPos(short cardNum,short crd,out int pTargetPos);
        /// <summary>获取坐标系当前位置</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetCrdPos(short cardNum,short crd,out double pPos);
        /// <summary>获取坐标系当前合成速度</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetCrdVel(short cardNum,short crd,out double pSynVel);
        /// <summary>设置坐标系单轴最大速度</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetCrdSingleMaxVel(short cardNum,short crd,ref double pMaxVel);
        /// <summary>获取坐标系单轴最大速度</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetCrdSingleMaxVel(short cardNum,short crd,out double pMaxVel);
        /// <summary>获取坐标系命令计数</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetCmdCount(short cardNum,short crd, out short pResult, short fifo);

        // ============================================================
        // 坐标系缓冲区激光控制函数 —— 激光加工专用
        // ============================================================

        /// <summary>缓冲区激光开</summary>
        [DllImport("gts.dll")]
        public static extern short GT_BufLaserOn(short cardNum,short crd,short fifo,short channel);
        /// <summary>缓冲区激光关</summary>
        [DllImport("gts.dll")]
        public static extern short GT_BufLaserOff(short cardNum,short crd,short fifo,short channel);
        /// <summary>缓冲区激光功率命令</summary>
        [DllImport("gts.dll")]
        public static extern short GT_BufLaserPrfCmd(short cardNum,short crd,double laserPower,short fifo,short channel);
        /// <summary>缓冲区激光跟随比例</summary>
        [DllImport("gts.dll")]
        public static extern short GT_BufLaserFollowRatio(short cardNum,short crd,double ratio,double minPower,double maxPower,short fifo,short channel);
        /// <summary>缓冲区激光跟随模式</summary>
        [DllImport("gts.dll")]
        public static extern short GT_BufLaserFollowMode(short cardNum,short crd,short source ,short fifo,short channel,double startPower );
        /// <summary>缓冲区激光跟随关</summary>
        [DllImport("gts.dll")]
        public static extern short GT_BufLaserFollowOff(short cardNum,short crd,short fifo,short channel);
        /// <summary>缓冲区激光跟随样条</summary>
        [DllImport("gts.dll")]
        public static extern short GT_BufLaserFollowSpline(short cardNum,short crd,short tableId,double minPower,double maxPower,short fifo,short channel);

        // ============================================================
        // PVT模式 —— 位置-速度-时间模式，最灵活的运动模式
        // ============================================================

        /// <summary>设置规划模式为PVT</summary>
        [DllImport("gts.dll")]
        public static extern short GT_PrfPvt(short cardNum,short profile);
        /// <summary>设置PVT循环次数</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetPvtLoop(short cardNum,short profile,int loop);
        /// <summary>获取PVT循环次数</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetPvtLoop(short cardNum,short profile,out int pLoopCount,out int pLoop);
        /// <summary>获取PVT运行状态</summary>
        [DllImport("gts.dll")]
        public static extern short GT_PvtStatus(short cardNum,short profile,out short pTableId,out double pTime,short count);
        /// <summary>启动PVT运动</summary>
        [DllImport("gts.dll")]
        public static extern short GT_PvtStart(short cardNum,int mask);
        /// <summary>选择PVT数据表</summary>
        [DllImport("gts.dll")]
        public static extern short GT_PvtTableSelect(short cardNum,short profile,short tableId);

        /// <summary>设置PVT数据表（时间-位置-速度）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_PvtTable(short cardNum,short tableId,int count,ref double pTime,ref double pPos,ref double pVel);
        /// <summary>设置PVT数据表（扩展版，指定起止速度）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_PvtTableEx(short cardNum,short tableId,int count,ref double pTime,ref double pPos,ref double pVelBegin,ref double pVelEnd);
        /// <summary>设置PVT数据表（完整版）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_PvtTableComplete(short cardNum,short tableId,int count,ref double pTime,ref double pPos,ref double pA,ref double pB,ref double pC,double velBegin,double velEnd);
        /// <summary>设置PVT数据表（百分比方式）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_PvtTablePercent(short cardNum,short tableId,int count,ref double pTime,ref double pPos,ref double pPercent,double velBegin);
        /// <summary>计算PVT百分比参数</summary>
        [DllImport("gts.dll")]
        public static extern short GT_PvtPercentCalculate(short cardNum,int n,ref double pTime,ref double pPos,ref double pPercent,double velBegin,ref double pVel);
        /// <summary>设置PVT数据表（连续模式）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_PvtTableContinuous(short cardNum,short tableId,int count,ref double pPos,ref double pVel,ref double pPercent,ref double pVelMax,ref double pAcc,ref double pDec,double timeBegin);
        /// <summary>计算PVT连续参数</summary>
        [DllImport("gts.dll")]
        public static extern short GT_PvtContinuousCalculate(short cardNum,int n,ref double pPos,ref double pVel,ref double pPercent,ref double pVelMax,ref double pAcc,ref double pDec,ref double pTime);
        /// <summary>设置PVT数据表（移动百分比方式）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_PvtTableMovePercent(short cardNum,short tableId, long distance, double vm, double acc, double pa1, double pa2, double dec, double pd1, double pd2, out double pVel, out double pAcc, out double pDec, out double pTime);

        // ============================================================
        // 回零函数 —— 上电后必须先回零才能定位
        // ============================================================

        /// <summary>初始化回零模块</summary>
        [DllImport("gts.dll")]
        public static extern short GT_HomeInit(short cardNum);
        /// <summary>
        /// 简易回零。axis=轴号，pos=回零后设置的位置，vel=回零速度，acc=加速度，offset=偏移量。
        /// 这是简化版的回零，功能比GT_GoHome少，但用起来简单。
        /// </summary>
        [DllImport("gts.dll")]
        public static extern short GT_Home(short cardNum,short axis,int pos,double vel,double acc,int offset);
        /// <summary>Index回零（找编码器Z相信号）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_Index(short cardNum,short axis,int pos,int offset);
        /// <summary>回零停止</summary>
        [DllImport("gts.dll")]
        public static extern short GT_HomeStop(short cardNum,short axis,int pos,double vel,double acc);
        /// <summary>获取回零状态</summary>
        [DllImport("gts.dll")]
        public static extern short GT_HomeSts(short cardNum,short axis,out ushort pStatus);

        // ============================================================
        // 手轮跟随函数 —— 用手轮手动控制轴运动
        // ============================================================

        /// <summary>初始化手轮模块</summary>
        [DllImport("gts.dll")]
        public static extern short GT_HandwheelInit(short cardNum);
        /// <summary>设置手轮停止减速度</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetHandwheelStopDec(short cardNum,short slave,double decSmoothStop,double decAbruptStop);
        /// <summary>启动手轮跟随</summary>
        [DllImport("gts.dll")]
        public static extern short GT_StartHandwheel(short cardNum,short slave,short master,short masterEven,short slaveEven,short intervalTime,double acc,double dec,double vel,short stopWaitTime);
        /// <summary>停止手轮跟随</summary>
        [DllImport("gts.dll")]
        public static extern short GT_EndHandwheel(short cardNum,short slave);

        // ============================================================
        // 触发器函数 —— 位置比较触发
        // ============================================================

        /// <summary>设置触发器参数</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetTrigger(short cardNum,short i,ref TTrigger pTrigger);
        /// <summary>获取触发器参数</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetTrigger(short cardNum,short i,out TTrigger pTrigger);
        /// <summary>获取触发器状态</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetTriggerStatus(short cardNum,short i,out TTriggerStatus pTriggerStatus,short count);
        /// <summary>清除触发器状态</summary>
        [DllImport("gts.dll")]
        public static extern short GT_ClearTriggerStatus(short cardNum,short i);

        // ============================================================
        // 比较输出函数 —— 位置比较触发输出脉冲
        // ============================================================

        /// <summary>设置比较输出端口</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetComparePort(short cardNum,short channel,short hsio0,short hsio1);

        /// <summary>比较脉冲输出</summary>
        [DllImport("gts.dll")]
        public static extern short GT_ComparePulse(short cardNum,short level,short outputType,short time);
        /// <summary>停止比较输出</summary>
        [DllImport("gts.dll")]
        public static extern short GT_CompareStop(short cardNum);
        /// <summary>获取比较输出状态</summary>
        [DllImport("gts.dll")]
        public static extern short GT_CompareStatus(short cardNum,out short pStatus,out int pCount);
        /// <summary>设置比较输出数据</summary>
        [DllImport("gts.dll")]
        public static extern short GT_CompareData(short cardNum,short encoder,short source,short pulseType,short startLevel,short time,ref int pBuf1,short count1,ref int pBuf2,short count2);
        /// <summary>线性比较输出</summary>
        [DllImport("gts.dll")]
        public static extern short GT_CompareLinear(short cardNum,short encoder,short channel,int startPos,int repeatTimes,int interval,short time,short source);
        /// <summary>连续比较脉冲模式</summary>
        [DllImport("gts.dll")]
        public static extern short GT_CompareContinuePulseMode(short cardNum,short mode, short count, short standTime);

        // ============================================================
        // 编码器响应检测函数
        // ============================================================

        /// <summary>设置编码器响应检测参数</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetEncResponseCheck(short cardNum,short control, short dacThreshold, double minEncVel, int time);
        /// <summary>获取编码器响应检测参数</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetEncResponseCheck(short cardNum,short control, out short pDacThreshold, out double pMinEncVel, out int pTime);
        /// <summary>使能编码器响应检测</summary>
        [DllImport("gts.dll")]
        public static extern short GT_EnableEncResponseCheck(short cardNum,short control);
        /// <summary>禁能编码器响应检测</summary>
        [DllImport("gts.dll")]
        public static extern short GT_DisableEncResponseCheck(short cardNum,short control);

        // ============================================================
        // 2D比较输出函数
        // ============================================================

        /// <summary>设置2D比较模式</summary>
        [DllImport("gts.dll")]
        public static extern short GT_2DCompareMode(short cardNum,short chn, short mode);
        /// <summary>2D比较脉冲输出</summary>
        [DllImport("gts.dll")]
        public static extern short GT_2DComparePulse(short cardNum,short chn, short level, short outputType, short time);
        /// <summary>停止2D比较输出</summary>
        [DllImport("gts.dll")]
        public static extern short GT_2DCompareStop(short cardNum,short chn);
        /// <summary>清除2D比较状态</summary>
        [DllImport("gts.dll")]
        public static extern short GT_2DCompareClear(short cardNum,short chn);
        /// <summary>获取2D比较状态</summary>
        [DllImport("gts.dll")]
        public static extern short GT_2DCompareStatus(short cardNum,short chn, out short pStatus, out int pCount, out short pFifo, out short pFifoCount, out short pBufCount);
        /// <summary>设置2D比较参数</summary>
        [DllImport("gts.dll")]
        public static extern short GT_2DCompareSetPrm(short cardNum,short chn, ref T2DComparePrm pPrm);
        /// <summary>写入2D比较数据</summary>
        [DllImport("gts.dll")]
        public static extern short GT_2DCompareData(short cardNum,short chn, short count, ref T2DCompareData pBuf, short fifo);
        /// <summary>启动2D比较输出</summary>
        [DllImport("gts.dll")]
        public static extern short GT_2DCompareStart(short cardNum,short chn);

        // ============================================================
        // 轴模式/HSIO/激光控制函数
        // ============================================================

        /// <summary>设置轴模式</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetAxisMode(short cardNum,short axis, short mode);
        /// <summary>获取轴模式</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetAxisMode(short cardNum,short axis, out short pMode);
        /// <summary>设置HSIO配置</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetHSIOOpt(short cardNum,ushort value, short channel);
        /// <summary>获取HSIO配置</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetHSIOOpt(short cardNum,out ushort pValue, short channel);
        /// <summary>设置激光功率模式</summary>
        [DllImport("gts.dll")]
        public static extern short GT_LaserPowerMode(short cardNum,short laserPowerMode, double maxValue, double minValue, short channel, short delaymode);
        /// <summary>激光功率命令输出</summary>
        [DllImport("gts.dll")]
        public static extern short GT_LaserPrfCmd(short cardNum,double outputCmd, short channel);
        /// <summary>激光输出频率设置</summary>
        [DllImport("gts.dll")]
        public static extern short GT_LaserOutFrq(short cardNum,double outFrq, short channel);
        /// <summary>设置激光脉冲宽度</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetPulseWidth(short cardNum,uint width, short channel);
        /// <summary>设置等待脉冲参数</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetWaitPulse(short cardNum,ushort mode, double waitPulseFrq, double waitPulseDuty, short channel);
        /// <summary>设置预燃电压</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetPreVltg(short cardNum,ushort mode, double voltageValue, short channel);
        /// <summary>设置电平延时</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetLevelDelay(short cardNum,ushort offDelay, ushort onDelay, short channel);
        /// <summary>使能FPK（首脉冲抑制）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_EnaFPK(short cardNum,ushort time1, ushort time2, ushort laserOffDelay, short channel);
        /// <summary>禁能FPK</summary>
        [DllImport("gts.dll")]
        public static extern short GT_DisFPK(short cardNum,short channel);
        /// <summary>设置激光禁止模式</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetLaserDisMode(short cardNum,short mode, short source, ref int pPos, ref double pScale, short channel);
        /// <summary>设置激光禁止比例</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetLaserDisRatio(short cardNum,ref double pRatio, double minPower, double maxPower, short channel);
        /// <summary>设置等待脉冲参数（扩展版）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetWaitPulseEx(short cardNum,ushort mode, double waitPulseFrq, double waitPulseDuty);
        /// <summary>设置激光模式</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetLaserMode(short cardNum,short mode);
        /// <summary>设置激光跟随样条曲线</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetLaserFollowSpline(short cardNum,short tableId,long n,ref double pX,ref double pY,double beginValue,double endValue,short channel);
        /// <summary>获取激光跟随样条曲线</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetLaserFollowSpline(short cardNum,short tableId,long n,out double pX,out double pY,out double pA,out double pB,out double pC,out long pCount,short channel);

        // ============================================================
        // 扩展模块函数 —— 外接IO扩展板等
        // ============================================================

        /// <summary>打开扩展模块DLL</summary>
        [DllImport("gts.dll")]
        public static extern short GT_OpenExtMdl(short cardNum,string pDllName);
        /// <summary>关闭扩展模块</summary>
        [DllImport("gts.dll")]
        public static extern short GT_CloseExtMdl(short cardNum);
        /// <summary>切换扩展模块卡号</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SwitchtoCardNoExtMdl(short cardNum,short card);
        /// <summary>复位扩展模块</summary>
        [DllImport("gts.dll")]
        public static extern short GT_ResetExtMdl(short cardNum);
        /// <summary>加载扩展模块配置</summary>
        [DllImport("gts.dll")]
        public static extern short GT_LoadExtConfig(short cardNum,string pFileName);
        /// <summary>设置扩展模块IO输出值</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetExtIoValue(short cardNum,short mdl,ushort value);
        /// <summary>获取扩展模块IO输入值</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetExtIoValue(short cardNum,short mdl,out ushort pValue);
        /// <summary>设置扩展模块IO位输出</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetExtIoBit(short cardNum,short mdl,short index,ushort value);
        /// <summary>获取扩展模块IO位输入</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetExtIoBit(short cardNum,short mdl,short index,out ushort pValue);
        /// <summary>获取扩展模块AD值</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetExtAdValue(short cardNum,short mdl,short chn,out ushort pValue);
        /// <summary>获取扩展模块AD电压值</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetExtAdVoltage(short cardNum,short mdl,short chn,out double pValue);
        /// <summary>设置扩展模块DA值</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetExtDaValue(short cardNum,short mdl,short chn,ushort value);
        /// <summary>设置扩展模块DA电压值</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetExtDaVoltage(short cardNum,short mdl,short chn,double value);
        /// <summary>获取扩展模块状态</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetStsExtMdl(short cardNum,short mdl,short chn,out ushort pStatus);
        /// <summary>获取扩展模块DO值</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetExtDoValue(short cardNum,short mdl,out ushort pValue);
        /// <summary>获取扩展模块模式</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetExtMdlMode(short cardNum,out short pMode);
        /// <summary>设置扩展模块模式</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetExtMdlMode(short cardNum,short mode);
        /// <summary>上传配置</summary>
        [DllImport("gts.dll")]
        public static extern short GT_UploadConfig(short cardNum);
        /// <summary>下载配置</summary>
        [DllImport("gts.dll")]
        public static extern short GT_DownloadConfig(short cardNum);
        /// <summary>获取控制卡UUID</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetUuid(short cardNum,out char pCode,short count);

        // ============================================================
        // 2D补偿函数 —— 2D平面误差补偿
        // ============================================================

        /// <summary>
        /// 2D补偿表结构 —— 定义一个2D网格上的补偿值表。
        /// 用于补偿XY平台在整个运动范围内的定位误差。
        /// 【固高SDK固定结构，字段顺序不能改】
        /// </summary>
        public struct TCompensate2DTable
        {
            public short count1;       // 方向1的网格数
            public short count2;       // 方向2的网格数
            public int posBegin1;      // 方向1的起始位置，单位pulse
            public int posBegin2;      // 方向2的起始位置，单位pulse
            public int step1;          // 方向1的步长，单位pulse
            public int step2;          // 方向2的步长，单位pulse
        }

        /// <summary>
        /// 2D补偿配置 —— 指定哪个轴使用哪个补偿表。
        /// 【固高SDK固定结构，字段顺序不能改】
        /// </summary>
        public struct TCompensate2D
        {
	        public short enable;        // 是否使能，1=使能，0=禁能
	        public short tableIndex;     // 补偿表索引
	        public short axisType1;      // 轴1类型
            public short axisType2;      // 轴2类型
	        public short axisIndex1;     // 轴1索引
            public short axisIndex2;     // 轴2索引
        }

        /// <summary>设置2D补偿表</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetCompensate2DTable(short cardNum,short tableIndex,ref TCompensate2DTable pTable,ref int pData,short externComp);
        /// <summary>获取2D补偿表</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetCompensate2DTable(short cardNum,short tableIndex,out TCompensate2DTable pTable,out short pExternComp);
        /// <summary>设置2D补偿配置</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetCompensate2D(short cardNum,short axis, ref TCompensate2D pComp2d);
        /// <summary>获取2D补偿配置</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetCompensate2D(short cardNum,short axis, out TCompensate2D pComp2d);
        /// <summary>获取2D补偿当前值</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetCompensate2DValue(short cardNum,short axis, out double pValue);

        // ============================================================
        // 智能回零(Smart Home) —— 功能更完善的回零方式
        // 支持多种回零模式：限位回零、原点回零、Index回零等
        // ============================================================

        // 回零阶段常量 —— 表示回零过程进行到哪一步了
        public const short HOME_STAGE_IDLE=0;                       // 空闲，还没开始
        public const short HOME_STAGE_START=1;                      // 开始回零
        public const short HOME_STAGE_SEARCH_LIMIT=10;              // 搜索限位
        public const short HOME_STAGE_SEARCH_LIMIT_STOP=11;         // 搜索限位停止
        public const short HOME_STAGE_SEARCH_LIMIT_ESCAPE = 13;     // 逃离限位
        public const short HOME_STAGE_SEARCH_LIMIT_RETURN=15;       // 返回搜索限位
        public const short HOME_STAGE_SEARCH_LIMIT_RETURN_STOP=16;  // 返回搜索限位停止
        public const short HOME_STAGE_SEARCH_HOME=20;               // 搜索原点信号
        public const short HOME_STAGE_SEARCH_HOME_RETURN=25;        // 返回搜索原点
        public const short HOME_STAGE_SEARCH_INDEX=30;              // 搜索Index信号
        public const short HOME_STAGE_SEARCH_GPI=40;                // 搜索GPI信号
        public const short HOME_STAGE_SEARCH_GPI_RETURN=45;         // 返回搜索GPI
        public const short HOME_STAGE_GO_HOME=80;                   // 走到原点位置
        public const short HOME_STAGE_END=100;                      // 回零完成

        // 回零错误码
        public const short HOME_ERROR_NONE=0;                // 无错误
        public const short HOME_ERROR_NOT_TRAP_MODE=1;       // 不是Trap模式
        public const short HOME_ERROR_DISABLE=2;             // 轴未使能
        public const short HOME_ERROR_ALARM=3;               // 轴报警
        public const short HOME_ERROR_STOP=4;                // 轴已停止
        public const short HOME_ERROR_STAGE=5;               // 阶段错误
        public const short HOME_ERROR_HOME_MODE=6;           // 回零模式错误
        public const short HOME_ERROR_SET_CAPTURE_HOME=7;    // 设置原点捕捉失败
        public const short HOME_ERROR_NO_HOME=8;             // 未找到原点信号
        public const short HOME_ERROR_SET_CAPTURE_INDEX=9;   // 设置Index捕捉失败
        public const short HOME_ERROR_NO_INDEX=10;           // 未找到Index信号

        // 回零模式 —— 决定回零的具体流程
        public const short HOME_MODE_LIMIT=10;               // 仅限位回零
        public const short HOME_MODE_LIMIT_HOME=11;          // 限位+原点回零
        public const short HOME_MODE_LIMIT_INDEX=12;         // 限位+Index回零
        public const short HOME_MODE_LIMIT_HOME_INDEX=13;    // 限位+原点+Index回零
        public const short HOME_MODE_HOME=20;                // 仅原点回零
        public const short HOME_MODE_HOME_INDEX=22;          // 原点+Index回零
        public const short HOME_MODE_INDEX = 30;             // 仅Index回零

        /// <summary>
        /// 智能回零参数 —— 配置回零流程的详细参数。
        /// 通过GT_GetHomePrm获取默认值，修改后传给GT_GoHome。
        /// 【固高SDK固定结构，字段顺序不能改，pad字段是内存对齐填充，不能删】
        /// </summary>
        public struct THomePrm
        {
	        public short mode;                // 回零模式，见HOME_MODE_xxx常量
	        public short moveDir;             // 搜索方向，1=正方向，-1=负方向
	        public short indexDir;            // Index搜索方向
	        public short edge;                // 信号边沿，0=下降沿，1=上升沿
	        public short triggerIndex;        // 触发器索引
			public short pad1_1;              // 填充字段（内存对齐用，不能删）
	        public short pad1_2;              // 填充字段
            public short pad1_3;              // 填充字段
	        public double velHigh;            // 高速搜索速度，单位pulse/ms
	        public double velLow;             // 低速精确定位速度，单位pulse/ms
	        public double acc;                // 回零加速度，单位pulse/ms²
	        public double dec;                // 回零减速度，单位pulse/ms²
	        public short smoothTime;          // 平滑时间，单位ms
			public short pad2_1;              // 填充字段
		    public short pad2_2;              // 填充字段
            public short pad2_3;              // 填充字段
	        public int homeOffset;            // 原点偏移量，单位pulse。回零完成后位置=homeOffset
	        public int searchHomeDistance;    // 搜索原点信号的最大距离，单位pulse
	        public int searchIndexDistance;   // 搜索Index信号的最大距离，单位pulse
	        public int escapeStep;            // 逃离限位的步长，单位pulse
            public int pad3_1;               // 填充字段
            public int pad3_2;               // 填充字段
            public int pad3_3;               // 填充字段
        }

        /// <summary>
        /// 智能回零状态 —— 查询回零过程的当前状态。
        /// 【固高SDK固定结构，字段顺序不能改】
        /// </summary>
        public struct THomeStatus
        {
	        public short run;            // 是否在运行，1=运行中，0=完成
	        public short stage;          // 当前阶段，见HOME_STAGE_xxx常量
            public short error;          // 错误码，见HOME_ERROR_xxx常量
            public short pad1;           // 填充字段
	        public int capturePos;       // 捕捉到的位置，单位pulse
	        public int targetPos;        // 目标位置，单位pulse
        }

        /// <summary>
        /// 启动智能回零。传入THomePrm参数，控制卡自动执行整个回零流程。
        /// 调用后用GT_GetHomeStatus轮询状态，直到run=0表示完成。
        /// </summary>
        [DllImport("gts.dll")]
        public static extern short GT_GoHome(short cardNum,short axis, ref THomePrm pHomePrm);
        /// <summary>获取智能回零默认参数</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetHomePrm(short cardNum,short axis, out THomePrm pHomePrm);
        /// <summary>获取智能回零状态</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetHomeStatus(short cardNum,short axis, out THomeStatus pHomeStatus);

        // ============================================================
        // 扩展控制配置函数
        // ============================================================

        /// <summary>
        /// 扩展控制配置 —— 配置轴的参考源和反馈源。
        /// 【固高SDK固定结构，字段顺序不能改】
        /// </summary>
        public struct TControlConfigEx
        {
	        public short refType;          // 参考源类型
            public short refIndex;         // 参考源索引
            public short feedbackType;     // 反馈源类型
            public short feedbackIndex;    // 反馈源索引
            public int errorLimit;         // 误差限值
            public short feedbackSmooth;   // 反馈平滑
            public short controlSmooth;    // 控制平滑
        }

        /// <summary>设置扩展控制配置</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetControlConfigEx(short cardNum,short control, ref TControlConfigEx pControl);
        /// <summary>获取扩展控制配置</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetControlConfigEx(short cardNum,short control, out TControlConfigEx pControl);

        // ============================================================
        // ADC滤波配置函数
        // ============================================================

        /// <summary>
        /// ADC配置结构 —— 配置模拟量输入通道的参数。
        /// 【固高SDK固定结构，字段顺序不能改】
        /// </summary>
        public struct TAdcConfig
        {
	        public short active;         // 是否激活
            public short reverse;        // 是否反向
            public double a;             // 线性变换系数a（y = ax + b）
            public double b;             // 线性变换系数b
            public short filterMode;     // 滤波模式
        }

        /// <summary>设置ADC配置</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetAdcConfig(short cardNum,short adc, ref TAdcConfig pAdc);
        /// <summary>获取ADC配置</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetAdcConfig(short cardNum,short adc, out TAdcConfig pAdc);
        /// <summary>设置ADC滤波参数</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetAdcFilterPrm(short cardNum,short adc, double k);
        /// <summary>获取ADC滤波参数</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetAdcFilterPrm(short cardNum,short adc, out double pk);

        // ============================================================
        // 叠加控制函数
        // ============================================================

        /// <summary>设置控制叠加（在位置控制上叠加额外运动）</summary>
        [DllImport("gts.dll")]
        public static extern short GT_SetControlSuperimposed(short cardNum,short control, short superimposedType, short superimposedIndex);
        /// <summary>获取控制叠加配置</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetControlSuperimposed(short cardNum,short control, out short pSuperimposedType, out short pSuperimposedIndex);

        // ============================================================
        // 激光辅助函数
        // ============================================================

        /// <summary>清零激光开启时间</summary>
        [DllImport("gts.dll")]
        public static extern short GT_ZeroLaserOnTime(short cardNum,short channel);
        /// <summary>获取激光开启时间</summary>
        [DllImport("gts.dll")]
        public static extern short GT_GetLaserOnTime(short cardNum,short channel,out uint pTime);
    }
}
