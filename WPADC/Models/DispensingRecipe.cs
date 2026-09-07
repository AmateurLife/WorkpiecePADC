namespace WPADC.Models
{
    /// <summary>
    /// 点胶配方模型 —— 存储单个点胶任务的完整配置。
    /// 一个配方就是一套点胶方案：从哪个工件开始、到哪个结束、基准标记在哪、走什么轨迹。
    /// 配方可以保存到文件、从文件加载、复制克隆。
    /// 实际生产时，不同产品对应不同配方，切换产品就切换配方。
    /// </summary>
    public class DispensingRecipe
    {
        /// <summary>
        /// 配方名称，默认"新配方"。
        /// 在配方列表里显示的名字，方便用户区分不同配方。
        /// 克隆配方时会自动加"_副本"后缀。
        /// </summary>
        public string Name { get; set; } = "新配方";

        /// <summary>
        /// 关联的模板名称，用于视觉匹配。
        /// 点胶前需要先视觉定位工件位置，这个模板就是告诉视觉系统要找什么形状。
        /// 模板是在"模板管理"里创建和保存的，这里只是引用模板的名字。
        /// 如果没设模板，视觉匹配就找不到工件，点胶位置会偏。
        /// </summary>
        public string TemplateName { get; set; } = "";

        /// <summary>
        /// 起始点X坐标，单位mm。
        /// 这是配方录制时记录的第一个基准点的物理X坐标。
        /// 配方纠偏时，会用这个点和终止点计算整体偏移量。
        /// </summary>
        public double StartPointX { get; set; }

        /// <summary>
        /// 起始点Y坐标，单位mm。
        /// 和StartPointX配对，记录起始点的物理Y坐标。
        /// </summary>
        public double StartPointY { get; set; }

        /// <summary>
        /// 终止点X坐标，单位mm。
        /// 配方录制时记录的最后一个基准点的物理X坐标。
        /// 和起始点一起确定工件的方向和长度，用于角度纠偏。
        /// </summary>
        public double EndPointX { get; set; }

        /// <summary>
        /// 终止点Y坐标，单位mm。
        /// 和EndPointX配对，记录终止点的物理Y坐标。
        /// </summary>
        public double EndPointY { get; set; }

        /// <summary>
        /// 基准标记1的X坐标，单位mm。
        /// 基准标记是工件上的特征点（比如定位孔、标记线），
        /// 视觉系统通过匹配找到这两个标记的实际位置，和录制时的位置做对比，
        /// 算出XY偏移和角度偏移，然后对整个轨迹做纠偏。
        /// RefMark1是第一个基准标记的物理X坐标。
        /// </summary>
        public double RefMark1X { get; set; }

        /// <summary>
        /// 基准标记1的Y坐标，单位mm。
        /// 和RefMark1X配对。
        /// </summary>
        public double RefMark1Y { get; set; }

        /// <summary>
        /// 基准标记2的X坐标，单位mm。
        /// 第二个基准标记的物理X坐标。
        /// 两个基准标记确定一条线，这条线的方向就是工件的朝向。
        /// 两个标记距离越远，角度计算越精确。
        /// </summary>
        public double RefMark2X { get; set; }

        /// <summary>
        /// 基准标记2的Y坐标，单位mm。
        /// 和RefMark2X配对。
        /// </summary>
        public double RefMark2Y { get; set; }

        /// <summary>
        /// 轨迹点列表 —— 定义点胶路径的完整运动序列。
        /// 每个TrajectoryPoint代表轨迹上的一个运动段（直线/圆弧/快速移动），
        /// 按列表顺序依次执行就是完整的点胶路径。
        /// 录制配方时，用户手动走一遍路径，每个关键点记录为一个TrajectoryPoint。
        /// </summary>
        public List<TrajectoryPoint> Points { get; set; } = new();

        /// <summary>
        /// 配方创建时间，默认为当前时间。
        /// 新建配方时自动记录，之后不会变。
        /// </summary>
        public DateTime CreatedTime { get; set; } = DateTime.Now;

        /// <summary>
        /// 配方最后修改时间，默认为当前时间。
        /// 每次修改配方（添加/删除轨迹点、改参数等）都应该更新这个时间。
        /// 克隆配方时也会更新为新实例的创建时间。
        /// </summary>
        public DateTime ModifiedTime { get; set; } = DateTime.Now;

        /// <summary>
        /// 深拷贝当前配方，返回一个属性值相同的新实例。
        /// 克隆后配方名称自动加"_副本"后缀，轨迹点列表做深拷贝（不是引用拷贝），
        /// 修改时间更新为当前时间，创建时间保留原配方的。
        /// 这样用户可以基于已有配方快速创建新配方，改改参数就能用。
        /// </summary>
        /// <returns>克隆后的新DispensingRecipe实例</returns>
        public DispensingRecipe Clone()
        {
            return new DispensingRecipe
            {
                Name = Name + "_副本",          // 名字加后缀，避免重名
                TemplateName = TemplateName,      // 模板名称直接复制
                StartPointX = StartPointX,        // 起始点坐标复制
                StartPointY = StartPointY,
                EndPointX = EndPointX,            // 终止点坐标复制
                EndPointY = EndPointY,
                RefMark1X = RefMark1X,            // 基准标记1坐标复制
                RefMark1Y = RefMark1Y,
                RefMark2X = RefMark2X,            // 基准标记2坐标复制
                RefMark2Y = RefMark2Y,
                Points = Points.Select(p => p.Clone()).ToList(), // 轨迹点深拷贝，每个点都是新实例
                CreatedTime = CreatedTime,        // 创建时间保留原配方的
                ModifiedTime = DateTime.Now       // 修改时间更新为当前时间
            };
        }
    }
}
