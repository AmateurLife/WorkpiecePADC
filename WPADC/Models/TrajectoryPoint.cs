namespace WPADC.Models
{
    /// <summary>
    /// 轨迹段类型枚举 —— 定义运动轨迹上每一段的运动方式。
    /// 点胶轨迹就是由一系列"段"组成的，每一段可以是直线、圆弧或者快速移动。
    /// </summary>
    public enum TrajectoryType
    {
        /// <summary>
        /// 直线运动 —— 从当前位置沿直线走到目标位置，走的时候出胶。
        /// 最常用的类型，点胶轨迹大部分都是直线段。
        /// </summary>
        Linear,

        /// <summary>
        /// 圆弧运动 —— 从当前位置沿圆弧走到目标位置，走的时候出胶。
        /// 用于拐弯处或者需要走弧线的场景，需要额外指定圆心和半径。
        /// 比直线少用，但有些工件的倒角、圆角必须用圆弧走。
        /// </summary>
        Arc,

        /// <summary>
        /// 快速定位移动 —— 从当前位置快速移到目标位置，移动过程中不出胶。
        /// 用于两个涂胶区域之间的空走移动，比如点胶完一段后移到下一段的起点。
        /// 速度通常比涂胶时快，因为不需要精确控制胶量。
        /// </summary>
        RapidMove
    }

    /// <summary>
    /// 轨迹点模型 —— 描述点胶路径上的一个运动段。
    /// 每个轨迹点包含：目标位置(X,Y,Z)、运动类型(直线/圆弧/快速移动)、
    /// 运动速度、是否出胶、以及圆弧参数（仅圆弧段需要）。
    /// 注意：轨迹点是"段"的概念，不是"点"的概念。
    /// 它描述的是"从上一个位置到这个位置怎么走"，而不是"停在这个位置"。
    /// </summary>
    public class TrajectoryPoint
    {
        /// <summary>
        /// 目标位置X坐标，单位mm。
        /// 这一段运动的终点X坐标，起点就是上一段的终点。
        /// 第一段的起点是当前位置（录制时就是平台当时的位置）。
        /// </summary>
        public double X { get; set; }

        /// <summary>
        /// 目标位置Y坐标，单位mm。
        /// 和X配对，终点Y坐标。
        /// </summary>
        public double Y { get; set; }

        /// <summary>
        /// 目标位置Z坐标，单位mm。
        /// Z轴一般控制点胶针头的高度，Z高=针头离工件远，Z低=针头离工件近。
        /// 点胶时Z要调到合适高度，太高胶线飘，太低刮工件。
        /// 快速移动时通常先抬Z再走XY，避免针头撞到工件。
        /// </summary>
        public double Z { get; set; }

        /// <summary>
        /// 轨迹段类型（直线/圆弧/快速移动），默认为直线。
        /// 决定了这一段运动走什么路径，影响运动控制卡用哪种插补模式。
        /// </summary>
        public TrajectoryType Type { get; set; } = TrajectoryType.Linear;

        /// <summary>
        /// 运动速度，单位mm/s，默认10.0。
        /// 这一段运动的目标速度。不同段可以设不同速度，
        /// 比如直线段可以快点，圆弧段要慢点保证精度。
        /// 这个速度会直接传给运动控制卡的插补指令。
        /// </summary>
        public double Speed { get; set; } = 10.0;

        /// <summary>
        /// 是否开启涂胶，默认true（涂胶）。
        /// 点胶走线时设true，快速移动时设false。
        /// 实际控制时，true=打开胶阀，false=关闭胶阀。
        /// 这个标志会转换成运动控制卡的IO输出控制信号。
        /// </summary>
        public bool GlueOn { get; set; } = true;

        /// <summary>
        /// 圆弧圆心X坐标，单位mm。仅Type为Arc时有效，其他类型忽略。
        /// 圆弧圆心不是轨迹上的点，而是圆弧所在圆的圆心位置。
        /// 录制配方时，圆心坐标是根据起点、终点和半径反算出来的。
        /// </summary>
        public double ArcCenterX { get; set; }

        /// <summary>
        /// 圆弧圆心Y坐标，单位mm。仅Type为Arc时有效，其他类型忽略。
        /// 和ArcCenterX配对。
        /// </summary>
        public double ArcCenterY { get; set; }

        /// <summary>
        /// 圆弧半径，单位mm。仅Type为Arc时有效，其他类型忽略。
        /// 录制配方时的计算方式：
        ///   半径 = sqrt((ArcCenterX - X)² + (ArcCenterY - Y)²)
        /// 也就是圆心到终点的距离。因为圆弧经过终点，所以半径就是圆心到终点的距离。
        /// </summary>
        public double ArcRadius { get; set; }

        /// <summary>
        /// 圆弧方向是否为顺时针，默认true。仅Type为Arc时有效。
        /// true = 顺时针（CW），false = 逆时针（CCW）。
        /// 从圆心往终点看，顺时针走还是逆时针走。
        /// 这个方向会影响运动控制卡的插补方向参数。
        /// </summary>
        public bool ArcClockwise { get; set; } = true;

        /// <summary>
        /// 圆弧角度，单位度，默认360.0。仅Type为Arc时有效。
        /// 360度就是整圆，90度就是四分之一圆。
        /// 录制配方时的计算方式：
        ///   先算起点到圆心的角度 angle1 = atan2(startY - ArcCenterY, startX - ArcCenterX)
        ///   再算终点到圆心的角度 angle2 = atan2(Y - ArcCenterY, X - ArcCenterX)
        ///   ArcAngle = angle2 - angle1（根据顺逆时针方向调整正负）
        /// 大部分情况下不是整圆，角度需要精确计算。
        /// </summary>
        public double ArcAngle { get; set; } = 360.0;

        /// <summary>
        /// 深拷贝当前轨迹点，返回一个属性值完全相同的新实例。
        /// 配方克隆时用，确保每个配方有自己独立的轨迹点列表，
        /// 修改一个配方的轨迹点不会影响另一个。
        /// </summary>
        /// <returns>克隆后的新TrajectoryPoint实例</returns>
        public TrajectoryPoint Clone()
        {
            return new TrajectoryPoint
            {
                X = X, Y = Y, Z = Z,                     // 位置坐标复制
                Type = Type, Speed = Speed, GlueOn = GlueOn, // 运动参数复制
                ArcCenterX = ArcCenterX, ArcCenterY = ArcCenterY, // 圆弧圆心复制
                ArcRadius = ArcRadius,                     // 圆弧半径复制
                ArcClockwise = ArcClockwise,               // 圆弧方向复制
                ArcAngle = ArcAngle                        // 圆弧角度复制
            };
        }
    }
}
