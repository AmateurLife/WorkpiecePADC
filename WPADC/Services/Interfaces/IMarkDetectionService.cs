using HalconDotNet;
using WPADC.Services.Vision;

namespace WPADC.Services.Interfaces
{
    /// <summary>
    /// Mark检测服务接口——用形状模板在图像里找Mark点
    /// </summary>
    /// <remarks>
    /// 这个接口干的事很简单：给你一张图和一个模板，帮你找到Mark在哪。
    /// 底层用的是 Halcon 的 FindShapeModel 算子，这是 Halcon 官方的形状匹配方法，
    /// 属于固定搭配，不是我们自己发明的。
    ///
    /// 有两种检测模式：
    /// - FindMark: 找一个Mark，返回一个结果
    /// - FindBothMarks: 同时找两个Mark，返回两个结果
    ///
    /// 实际使用中几乎都是 FindBothMarks，因为我们的系统是双Mark定位，
    /// 需要两个Mark的位置才能算出偏移和旋转。
    ///
    /// 调用方：MainViewModel 的检测定时器回调（每200ms触发一次）
    /// 参数从哪来：
    /// - grayImage: 相机采集的当前帧灰度图
    /// - modelId1/2: 从 ITemplateManagerService.ModelID1/2 取的模板句柄
    /// - minScore/greediness: 从 MatchingParams 配置取的匹配参数
    ///
    /// 高频调用——检测定时器每200ms调用一次 FindBothMarks()。
    /// </remarks>
    public interface IMarkDetectionService
    {
        /// <summary>
        /// 单Mark检测——在图像里找一个Mark
        /// </summary>
        /// <param name="grayImage">输入灰度图像，从相机采集得到，必须是灰度图（不能是彩色图）</param>
        /// <param name="modelId">Halcon 形状模板ID，从 ITemplateManagerService.ModelID1 或 ModelID2 取</param>
        /// <param name="minScore">
        /// 最低匹配得分阈值（0~1），低于这个分数的匹配结果会被丢弃。
        /// 来自 MatchingParams.MinScore，默认0.6。
        /// 实际实现里有三级降级策略：第一级用这个值，找不到就降低阈值再找。
        /// </param>
        /// <param name="greediness">
        /// 搜索贪婪度（0~1），来自 MatchingParams.Greediness，默认0.9。
        /// 0 = 安全但慢（仔细搜），1 = 快但可能漏检（粗略搜）。
        /// 这是 Halcon FindShapeModel 的官方参数，不是我们定义的。
        /// </param>
        /// <returns>
        /// MarkDetectionResult 包含：
        /// - Found: 是否找到
        /// - Row/Col: 找到的位置（像素坐标）
        /// - Angle: 旋转角度（弧度）
        /// - Score: 匹配得分（0~1）
        /// - Message: 描述信息
        /// </returns>
        /// <remarks>
        /// 实际项目中很少单独调用这个，基本都是用 FindBothMarks。
        /// 但保留这个方法是为了灵活性——万一以后只需要找单个Mark的场景。
        /// </remarks>
        MarkDetectionResult FindMark(HObject grayImage, HTuple modelId, double minScore, double greediness);

        /// <summary>
        /// 双Mark同时检测——在图像里同时找Mark1和Mark2
        /// </summary>
        /// <param name="grayImage">输入灰度图像，从相机采集得到</param>
        /// <param name="modelId1">Mark1的形状模板ID，从 ITemplateManagerService.ModelID1 取</param>
        /// <param name="modelId2">Mark2的形状模板ID，从 ITemplateManagerService.ModelID2 取</param>
        /// <param name="minScore">最低匹配得分阈值（0~1），两个Mark共用同一个阈值</param>
        /// <param name="greediness">搜索贪婪度（0~1），两个Mark共用同一个贪婪度</param>
        /// <returns>
        /// 元组 (Mark1结果, Mark2结果)，每个都是 MarkDetectionResult。
        /// 只有两个都 Found=true 时，后续的坐标变换和纠偏计算才有意义。
        /// </returns>
        /// <remarks>
        /// 谁调用：MainViewModel 的检测定时器回调 OnDetectionTimerTick()
        /// 参数从哪来：
        /// - grayImage: _cameraService.CurrentImage 取的当前帧
        /// - modelId1/2: _templateService.ModelID1/2
        /// - minScore: 当前固定传 0.70（代码里写死的，不是从配置读的）
        /// - greediness: 当前固定传 0.90
        ///
        /// 高频调用——检测定时器每200ms触发一次，是视觉检测的核心方法。
        /// </remarks>
        (MarkDetectionResult, MarkDetectionResult) FindBothMarks(HObject grayImage, HTuple modelId1, HTuple modelId2, double minScore, double greediness);
    }
}
