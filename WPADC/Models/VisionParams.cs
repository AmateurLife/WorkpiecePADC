namespace WPADC.Models
{
    /// <summary>
    /// 相机参数模型 —— 存储工业相机的采集配置。
    /// 这些参数在"参数设置"界面里配置，保存到JSON文件，启动时加载。
    /// 主要控制相机的曝光和增益，影响图像亮度和信噪比。
    /// </summary>
    public class CameraParams
    {
        /// <summary>
        /// 相机序列号，用于唯一标识连接的相机设备，默认"GEV_MVCA06010GC"。
        /// GEV = GigE Vision（千兆网口相机协议），MVCA06010GC是海康威视的型号编码。
        /// 如果电脑连了多个相机，靠序列号区分用哪个。
        /// 序列号可以在海康的MVS软件里查到，每台相机出厂就绑定了唯一的序列号。
        /// </summary>
        public string CameraSerialNumber { get; set; } = "GEV_MVCA06010GC";

        /// <summary>
        /// 曝光时间，单位微秒(μs)，默认5000.0（即5毫秒）。
        /// 曝光时间越长图像越亮，但太长会糊（运动模糊）。
        /// 典型值：1000~20000 μs，看环境光照和工件反光情况。
        /// 我们用的LED光源比较亮，5000μs一般够了。
        /// </summary>
        public double ExposureTime { get; set; } = 5000.0;

        /// <summary>
        /// 增益值，默认1.0。
        /// 增益就是信号放大倍数，增大增益能提亮图像，但也会放大噪声。
        /// 一般优先调曝光时间，曝光调不够再加增益。
        /// 增益太大图像噪点会很明显，影响模板匹配精度。
        /// </summary>
        public double Gain { get; set; } = 1.0;

        /// <summary>
        /// 是否启用自动曝光，默认false。
        /// 开启后相机会自动调整曝光时间，让图像亮度保持稳定。
        /// 生产环境一般不用自动曝光，因为每次曝光时间不一样，
        /// 图像亮度会波动，影响匹配稳定性。手动设固定曝光更靠谱。
        /// </summary>
        public bool AutoExposure { get; set; } = false;

        /// <summary>
        /// 是否启用自动增益，默认false。
        /// 和AutoExposure类似，自动增益会导致图像亮度波动。
        /// 生产环境一般关掉，手动设固定增益。
        /// </summary>
        public bool AutoGain { get; set; } = false;
    }

    /// <summary>
    /// 模板匹配参数模型 —— 存储Halcon形状模板匹配的搜索配置。
    /// 这些参数控制匹配的精度和速度，调好了匹配又快又准，调不好要么漏检要么太慢。
    /// 在"参数设置"界面里配置，保存到JSON文件。
    /// </summary>
    public class MatchingParams
    {
        /// <summary>
        /// 最低匹配得分阈值，范围0~1，默认0.6。
        /// 低于这个分数的匹配结果会被丢弃，认为不可靠。
        /// 设太低容易误检（把不是目标的当成目标），设太高容易漏检（明明在就是找不到）。
        /// 0.6是比较常用的值，0.7~0.8更严格，0.4~0.5更宽松。
        /// </summary>
        public double MinScore { get; set; } = 0.6;

        /// <summary>
        /// 搜索起始角度，单位度，默认0.0。
        /// 和AngleRangeDeg一起定义搜索的角度范围。
        /// 比如 AngleStartDeg=-10, AngleRangeDeg=20 表示在-10°到+10°范围内搜索。
        /// 如果工件基本不会旋转，缩小角度范围能大幅提升搜索速度。
        /// </summary>
        public double AngleStartDeg { get; set; } = 0.0;

        /// <summary>
        /// 搜索角度范围，单位度，默认360.0（全角度搜索）。
        /// 360表示从AngleStartDeg开始搜索整个圆周。
        /// 如果知道工件旋转不会超过±30°，设成60就能快很多。
        /// 全角度搜索最慢但最保险，不知道工件朝向时就用360。
        /// </summary>
        public double AngleRangeDeg { get; set; } = 360.0;

        /// <summary>
        /// 期望匹配的最大数量，默认1（只匹配一个目标）。
        /// 设1表示只找一个基准标记，设2表示找两个。
        /// 我们项目一般设1，因为每个标记单独匹配一次。
        /// 设大了搜索会变慢，因为要找更多候选。
        /// </summary>
        public int NumMatches { get; set; } = 1;

        /// <summary>
        /// 搜索贪婪度，范围0~1，默认0.9。
        /// 值越大搜索越快但可能遗漏目标，值越小搜索越慢但更不容易漏。
        /// 0.9是Halcon推荐的常用值，速度和可靠性的折中。
        /// 如果发现偶尔漏检，可以试试降到0.7~0.8。
        /// </summary>
        public double Greediness { get; set; } = 0.9;
    }

    /// <summary>
    /// 纠偏参数模型 —— 存储XY偏移和角度偏移的判定阈值。
    /// 视觉识别出来的偏移量如果小于阈值，就认为没有偏移，不执行纠偏。
    /// 这是为了避免微小抖动导致的频繁纠偏（反复调来调去反而不好）。
    /// </summary>
    public class CorrectionParams
    {
        /// <summary>
        /// XY偏移校正阈值，单位mm，默认0.01（即10μm）。
        /// 如果DeltaX和DeltaY的绝对值都小于这个阈值，就不做XY纠偏。
        /// 0.01mm是比较合理的精度，太小了会频繁纠偏，太大了精度不够。
        /// 具体设多少看工艺要求，一般点胶精度0.05mm以内就行。
        /// </summary>
        public double XYThreshold { get; set; } = 0.01;

        /// <summary>
        /// 角度偏移校正阈值，单位度，默认0.1。
        /// 如果DeltaAngle的绝对值小于这个阈值，就不做角度纠偏。
        /// 0.1度大概是1.75mrad，对于一般点胶精度够用了。
        /// 角度纠偏比较复杂（要做旋转变换），能不做就不做。
        /// </summary>
        public double AngleThreshold { get; set; } = 0.1;
    }
}
