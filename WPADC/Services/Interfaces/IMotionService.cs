using WPADC.Models;

namespace WPADC.Services.Interfaces
{
    /// <summary>
    /// 运动控制服务接口——整个运动控制模块的"合同"
    /// </summary>
    /// <remarks>
    /// 接口是啥？就是一份合同，规定了这个服务必须提供哪些方法，但不关心具体怎么实现。
    /// 为什么要写接口？方便以后换实现。现在用的是仿真模式（MotionSimulatorService），
    /// 以后接真实硬件只需要写一个 MotionRealService 实现同样的接口，MainViewModel 一行都不用改。
    ///
    /// 这个接口只管高层业务方法（回原点、JOG、插补、执行配方等），
    /// 不包含底层固高GT_XXX仿真方法，那些是仿真器内部的事。
    ///
    /// 为什么继承 IDisposable？因为运动服务内部持有定时器（位置刷新定时器等），
    /// 不释放的话定时器会一直跑，程序退出时必须显式 Dispose。
    /// MainViewModel.Dispose() 里会调用 _motionService.Dispose() 来清理。
    ///
    /// 调用方：MainViewModel 里的各种 Command/方法（ConnectCardCommand、HomeCommand、
    /// JogMove/StopJog、LinearInterpCommand、ArcInterpCommand、LoadRecipeDialog 等）
    /// </remarks>
    public interface IMotionService : IDisposable
    {
        /// <summary>
        /// 运控卡是否已打开
        /// </summary>
        /// <remarks>
        /// OpenCard() 成功后变为 true，CloseCard() 后变回 false。
        /// MainViewModel 用这个属性更新 IsCardConnected 绑定属性，驱动UI上的连接状态指示灯。
        /// 低频访问——只在打开/关闭卡的时候读一下。
        /// </remarks>
        bool IsCardOpened { get; }

        /// <summary>
        /// 是否正在运动——任意一个轴在动就返回 true
        /// </summary>
        /// <remarks>
        /// MainViewModel 用这个判断运动状态，UI上显示"运动中"提示。
        /// 也用来决定某些操作能不能执行（比如运动中不允许回原点）。
        /// 中频访问——JOG/插补期间会频繁读取。
        /// </remarks>
        bool IsMoving { get; }

        /// <summary>
        /// 是否正在执行配方（点胶配方跑完之前为 true）
        /// </summary>
        /// <remarks>
        /// ExecuteRecipe() 开始后变 true，配方跑完或 StopRecipe() 后变 false。
        /// MainViewModel 用 _isDispensing 标志配合这个属性，控制检测定时器的启停——
        /// 点胶期间暂停检测，点胶完成后再恢复。
        /// 低频访问——只在配方开始/结束时变化。
        /// </remarks>
        bool IsExecutingRecipe { get; }

        /// <summary>
        /// 胶阀输出状态——true 表示出胶，false 表示关胶
        /// </summary>
        /// <remarks>
        /// 可读可写。写的时候会调用 SetGlueOutput() 控制实际输出，
        /// 读的时候返回当前状态。
        /// MainViewModel 的 GlueOutputOn 绑定属性直接映射到这个，
        /// 用户点UI上的"出胶开关"→绑定属性变化→set 访问器→SetGlueOutput()。
        /// 中频访问——录制配方轨迹时频繁切换。
        /// </remarks>
        bool GlueOutputOn { get; set; }

        /// <summary>
        /// 轨迹记录——保存运动过程中经过的每个点（X坐标, Y坐标, 该点是否出胶）
        /// </summary>
        /// <remarks>
        /// 仿真模式下，插补运动的每一步都会往这个列表里加一个点，
        /// 用来在UI上画出点胶轨迹（红色=出胶，灰色=关胶）。
        /// MainViewModel 的轨迹显示功能直接读这个列表。
        /// ClearTrajectoryTrail() 可以清空。
        /// 高频写入——插补运动期间每几毫秒写一次。
        /// </remarks>
        List<(double x, double y, bool glueOn)> TrajectoryTrail { get; }

        /// <summary>
        /// 运动位置更新事件——每当X/Y轴位置发生变化时触发
        /// </summary>
        /// <remarks>
        /// 参数：(X_mm, Y_mm, IsMoving)
        /// - X_mm: X轴当前位置，单位mm
        /// - Y_mm: Y轴当前位置，单位mm
        /// - IsMoving: 当前是否在运动
        ///
        /// 谁发布：运动服务内部的位置刷新定时器（仿真与真实实现均为20ms一次）
        /// 谁订阅：MainViewModel.OnMotionPositionUpdated()，用来更新UI上的坐标显示和回原状态
        ///
        /// 高频事件——运动期间每20ms触发一次，是整个系统最频繁的事件之一。
        /// </remarks>
        event Action<double, double, bool>? PositionUpdated;

        /// <summary>
        /// 配方执行完成事件——点胶配方跑完后触发
        /// </summary>
        /// <remarks>
        /// 谁发布：ExecuteRecipe() 内部，配方所有轨迹点执行完毕后
        /// 谁订阅：MainViewModel.OnRecipeExecutionCompleted()，用来恢复检测、重置状态
        ///
        /// 低频事件——每次点胶完成触发一次。
        /// </remarks>
        event Action? RecipeExecutionCompleted;

        /// <summary>
        /// 胶阀状态变化事件——出胶/关胶状态切换时触发
        /// </summary>
        /// <remarks>
        /// 参数：bool newState——true=出胶，false=关胶
        ///
        /// 谁发布：SetGlueOutput() 内部
        /// 谁订阅：MainViewModel.OnGlueStateChanged()，用来同步UI上的出胶状态指示
        ///
        /// 中频事件——录制配方轨迹时频繁触发。
        /// </remarks>
        event Action<bool>? GlueStateChanged;

        /// <summary>
        /// 打开运控卡——初始化运动控制硬件（或仿真环境）
        /// </summary>
        /// <returns>结果描述字符串，成功返回"运控卡打开成功"之类的消息</returns>
        /// <remarks>
        /// 谁调用：MainViewModel.ConnectCard（用户点击UI上的"连接运控卡"按钮，绑定 ConnectCardCommand）
        /// 参数从哪来：无参数，内部自动初始化
        /// 调用后：IsCardOpened 变 true，同时会 EnableSoftLimit(true, true) 启用软限位保护
        /// 低频操作——整个程序运行期间一般只调用一次。
        /// </remarks>
        string OpenCard();

        /// <summary>
        /// 关闭运控卡——释放运动控制硬件资源
        /// </summary>
        /// <remarks>
        /// 谁调用：MainViewModel 的急停/关闭逻辑
        /// 调用后：IsCardOpened 变 false，所有运动停止
        /// 低频操作——程序退出或异常时调用。
        /// </remarks>
        void CloseCard();

        /// <summary>
        /// 所有轴回原点——让X轴和Y轴回到机械原点位置
        /// </summary>
        /// <param name="param">运动参数，包含回零速度、回零方向等，来自用户在UI上设置的 MotionParams</param>
        /// <remarks>
        /// 谁调用：MainViewModel.HomeCommand，用户点击"回原点"按钮
        /// 参数从哪来：MainViewModel 里的 MotionParams 绑定属性，用户在参数面板上配置
        /// 回原完成后 PositionUpdated 事件会更新 IsHomed 状态
        /// 低频操作——每次开机或急停恢复后调用一次。
        /// </remarks>
        void HomeAllAxis(MotionParams param);

        /// <summary>
        /// 设置胶阀输出——控制出胶/关胶
        /// </summary>
        /// <param name="on">true=出胶，false=关胶</param>
        /// <remarks>
        /// 谁调用：
        /// - MainViewModel.GlueOutputOn 的 set 访问器（用户点UI开关）
        /// - 急停/关闭时强制关胶（传 false）
        /// - 执行配方时自动控制出胶时机
        /// 参数从哪来：用户操作或配方逻辑
        /// </remarks>
        void SetGlueOutput(bool on);

        /// <summary>
        /// 启动JOG运动——按住按钮持续移动，松开停止
        /// </summary>
        /// <param name="axis">轴号：1=X轴，2=Y轴（固高运动控制卡的轴编号约定）</param>
        /// <param name="direction">方向：1=正方向，-1=负方向</param>
        /// <param name="velocity">JOG速度，单位mm/s，来自 MotionParams.JogVelocity</param>
        /// <param name="accel">加速度，单位mm/s²，来自 MotionParams.JogAcceleration</param>
        /// <param name="decel">减速度，单位mm/s²，来自 MotionParams.JogAcceleration（当前加减速度用同一个值）</param>
        /// <remarks>
        /// 谁调用：MainWindow.xaml.cs 的 Jog 方向按钮鼠标事件（JogXPos_PreviewMouseDown 等）
        /// → MainViewModel.JogMove() → 本方法。松开按钮时对应调 StopJog()。
        /// 参数从哪来：axis和direction由按钮绑定决定，速度/加速度来自用户配置的MotionParams
        /// 注意：JOG是持续运动，必须配合 StopJog() 使用，松开按钮时调用 StopJog()
        /// 中频操作——手动调机时频繁使用。
        /// </remarks>
        void StartJog(int axis, int direction, double velocity, double accel, double decel);

        /// <summary>
        /// 停止JOG运动——松开方向按钮时调用
        /// </summary>
        /// <param name="axis">轴号：1=X轴，2=Y轴</param>
        /// <remarks>
        /// 谁调用：MainWindow.xaml.cs 的 Jog_PreviewMouseUp（松开方向按钮）→ MainViewModel.StopJog()
        /// 和 StartJog() 配对使用。
        /// </remarks>
        void StopJog(int axis);

        /// <summary>
        /// 绝对位置移动——移动到指定坐标位置
        /// </summary>
        /// <param name="axis">轴号：1=X轴，2=Y轴</param>
        /// <param name="position">目标位置，单位mm（绝对坐标，不是相对距离）</param>
        /// <param name="velocity">运动速度，单位mm/s</param>
        /// <remarks>
        /// 谁调用：配方执行内部使用（单轴定位），以及调试模式下的手动移动
        /// 和 RelativeMove 的区别：这个是"走到坐标X=100"，RelativeMove是"往正方向走10mm"
        /// </remarks>
        void AbsoluteMove(int axis, double position, double velocity);

        /// <summary>
        /// 相对位置移动——从当前位置移动指定距离
        /// </summary>
        /// <param name="axis">轴号：1=X轴，2=Y轴</param>
        /// <param name="distance">移动距离，单位mm（正=正方向，负=负方向）</param>
        /// <param name="velocity">运动速度，单位mm/s</param>
        /// <remarks>
        /// 谁调用：配方执行内部使用
        /// 和 AbsoluteMove 的区别：这个是"再走10mm"，AbsoluteMove是"走到X=100"
        /// </remarks>
        void RelativeMove(int axis, double distance, double velocity);

        /// <summary>
        /// 直线插补运动——X轴和Y轴同时运动，走出一条直线
        /// </summary>
        /// <param name="targetX">目标X坐标，单位mm</param>
        /// <param name="targetY">目标Y坐标，单位mm</param>
        /// <param name="velocity">插补速度，单位mm/s</param>
        /// <param name="acceleration">加速度，单位mm/s²</param>
        /// <param name="deceleration">减速度，单位mm/s²</param>
        /// <remarks>
        /// 谁调用：MainViewModel.LinearInterpolationCommand，用户点击"直线插补"按钮
        /// 参数从哪来：目标坐标来自UI上的输入框（LinearTargetX/Y），速度来自参数面板
        /// 仿真模式下会往 TrajectoryTrail 里记录轨迹点
        /// 中频操作——录制配方轨迹时使用。
        /// </remarks>
        void LinearInterpolation(double targetX, double targetY, double velocity, double acceleration, double deceleration);

        /// <summary>
        /// 圆弧插补运动——X轴和Y轴联动走出一段圆弧
        /// </summary>
        /// <param name="centerX">圆弧圆心X坐标，单位mm</param>
        /// <param name="centerY">圆弧圆心Y坐标，单位mm</param>
        /// <param name="radius">圆弧半径，单位mm</param>
        /// <param name="clockwise">true=顺时针，false=逆时针</param>
        /// <param name="velocity">插补速度，单位mm/s</param>
        /// <param name="acceleration">加速度，单位mm/s²</param>
        /// <param name="deceleration">减速度，单位mm/s²</param>
        /// <param name="arcAngleDeg">圆弧角度，单位度，默认360度（整圆）。半圆就传180</param>
        /// <remarks>
        /// 谁调用：MainViewModel.ArcInterpolationCommand，用户点击"圆弧插补"按钮
        /// 参数从哪来：圆心和半径由UI输入计算得出，速度来自参数面板
        /// 圆心+半径+角度模式：这是固高运动控制卡的标准圆弧插补方式，
        /// 不是"起点+终点+半径"模式，是直接指定圆心和半径。
        /// 仿真模式下会往 TrajectoryTrail 里记录轨迹点
        /// 中频操作——录制配方轨迹时使用。
        /// </remarks>
        void ArcInterpolation(double centerX, double centerY, double radius, bool clockwise, double velocity, double acceleration, double deceleration, double arcAngleDeg = 360.0);

        /// <summary>
        /// 执行配方——按照配方里的轨迹点列表依次运动，同时控制胶阀开关
        /// </summary>
        /// <param name="recipe">点胶配方对象，包含轨迹点列表和起止点坐标</param>
        /// <param name="offsetX">X方向纠偏量，单位mm，来自视觉检测结果（DeltaX）</param>
        /// <param name="offsetY">Y方向纠偏量，单位mm，来自视觉检测结果（DeltaY）</param>
        /// <param name="offsetAngle">角度纠偏量，单位弧度，来自视觉检测结果（DeltaAngle）</param>
        /// <remarks>
        /// 谁调用：MainViewModel.LoadRecipeDialog（用户点击"调用配方"）以及检测到 Mark 偏移后的自动执行路径
        /// 参数从哪来：
        /// - recipe: 用户创建并保存的点胶配方
        /// - offsetX/Y/Angle: 坐标变换服务计算出的纠偏量，从 VisionResult 里取
        ///
        /// 执行过程：先回原点，然后按轨迹点逐个执行（直线/圆弧插补+胶阀控制）
        /// 执行完成后触发 RecipeExecutionCompleted 事件
        /// 低频操作——每个工件点胶一次。
        /// </remarks>
        void ExecuteRecipe(DispensingRecipe recipe, double offsetX, double offsetY, double offsetAngle);

        /// <summary>
        /// 停止配方执行——中途取消点胶
        /// </summary>
        /// <remarks>
        /// 谁调用：MainViewModel.StopDetection()（关闭检测时若正在点胶则停止配方执行）以及断开运控卡逻辑
        /// 调用后：配方执行中断，IsExecutingRecipe 变 false，同时清空轨迹记录
        /// 低频操作——异常或手动取消时调用。
        /// </remarks>
        void StopRecipe();

        /// <summary>
        /// 停止所有轴——紧急情况下让所有轴立刻停下来
        /// </summary>
        /// <remarks>
        /// 谁调用：急停逻辑、关闭运控卡时
        /// 和 EmergencyStop 的区别：这个是正常减速停止，EmergencyStop 是急停（立刻停）
        /// </remarks>
        void StopAllAxis();

        /// <summary>
        /// 强制回原——不检查当前状态，直接执行回原动作
        /// </summary>
        /// <remarks>
        /// 谁调用：设备重启逻辑
        /// 和 HomeAllAxis 的区别：HomeAllAxis 会先检查状态再回原，这个不管三七二十一直接回。
        /// 用于急停恢复后需要强制回原的场景。
        /// 低频操作——异常恢复时使用。
        /// </remarks>
        void ForceHome();

        /// <summary>
        /// 回原点运动——单次回原动作的低层封装
        /// </summary>
        /// <param name="homeVelocity">回原速度，单位mm/s，来自 MotionParams.HomeVelocity</param>
        /// <param name="acceleration">回原加速度，单位mm/s²，当前固定传0.5</param>
        /// <remarks>
        /// 谁调用：MainViewModel.HomeCommand 和 ForceHome() 内部
        /// 参数从哪来：homeVelocity 来自用户配置，acceleration 当前硬编码为0.5
        /// </remarks>
        void MoveHome(double homeVelocity, double acceleration);

        /// <summary>
        /// 急停——所有轴立刻停止，胶阀立刻关闭，不管当前在干什么
        /// </summary>
        /// <remarks>
        /// 谁调用：MainViewModel.EmergencyStopCommand，用户点击急停按钮
        /// 急停后需要手动解除才能恢复操作。
        /// 这是安全功能，优先级最高。
        /// 低频操作——紧急情况才用，希望永远不要用到。
        /// </remarks>
        void EmergencyStop();

        /// <summary>
        /// 获取轴状态——读取单个轴的实时状态信息
        /// </summary>
        /// <param name="axis">轴号：1=X轴，2=Y轴（固高运动控制卡的轴编号约定）</param>
        /// <returns>AxisStatusInfo 包含：位置、是否运动中、是否已回原、是否报警、是否使能</returns>
        /// <remarks>
        /// 谁调用：MainViewModel.OnMotionPositionUpdated()，位置刷新定时器回调里
        /// 参数从哪来：axis 固定传1和2，分别查X轴和Y轴
        /// 高频调用——位置刷新定时器每20ms调用一次，是整个系统调用最频繁的方法。
        /// </remarks>
        AxisStatusInfo GetAxisStatus(short axis);

        /// <summary>
        /// 清空轨迹记录——把 TrajectoryTrail 列表清零
        /// </summary>
        /// <remarks>
        /// 谁调用：
        /// - 新建配方时清空旧轨迹
        /// - 执行配方前清空
        /// - 停止点胶时清空
        /// - 取消配方编辑时清空
        /// 低频操作——每次配方相关操作前后调用。
        /// </remarks>
        void ClearTrajectoryTrail();

        /// <summary>
        /// 设置软限位——给单个轴配置正负方向的软件限位值
        /// </summary>
        /// <param name="axis">轴号：1=X轴，2=Y轴</param>
        /// <param name="positive">正方向限位值，单位mm（超过这个位置就不让走了）</param>
        /// <param name="negative">负方向限位值，单位mm（低于这个位置就不让走了）</param>
        /// <remarks>
        /// 谁调用：OpenCard() 成功后自动调用，设置默认限位保护
        /// 软限位是软件层面的保护，防止运动超出机械允许范围。
        /// 和硬限位（物理限位开关）不同，这个是程序控制的。
        /// 低频操作——只在打开运控卡时设置一次。
        /// </remarks>
        void SetSoftLimit(int axis, int positive, int negative);

        /// <summary>
        /// 启用或禁用软限位保护
        /// </summary>
        /// <param name="enableX">true=启用X轴软限位，false=禁用</param>
        /// <param name="enableY">true=启用Y轴软限位，false=禁用</param>
        /// <remarks>
        /// 谁调用：OpenCard() 成功后自动调用 EnableSoftLimit(true, true)
        /// 正常使用时两个轴都启用，调试时可能需要临时禁用。
        /// 低频操作——只在打开运控卡时调用一次。
        /// </remarks>
        void EnableSoftLimit(bool enableX, bool enableY);

        /// <summary>
        /// 获取当前软限位值——读取X轴和Y轴的正负限位
        /// </summary>
        /// <returns>元组 (X正限位, X负限位, Y正限位, Y负限位)，单位mm</returns>
        /// <remarks>
        /// 谁调用：UI上需要显示当前限位设置时
        /// 低频操作——偶尔读取显示。
        /// </remarks>
        (double posX, double negX, double posY, double negY) GetSoftLimits();
    }
}
