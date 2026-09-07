using HalconDotNet;
using WPADC.Services.Interfaces;

namespace WPADC.Services.Vision
{
    /// <summary>
    /// Mark 检测结果，封装单次形状模板匹配的结果数据。
    /// <para>
    /// 每次调 FindMark 搜一个 Mark，不管搜没搜到，都会返回一个 MarkDetectionResult。
    /// 搜到了 Found=true，里面填着坐标、角度、得分；没搜到 Found=false，用静态方法 NotFound() 生成。
    /// </para>
    /// </summary>
    public class MarkDetectionResult
    {
        /// <summary>
        /// 是否检测到 Mark。true 表示搜到了，下面的 Row/Col/Angle/Score 才有意义；
        /// false 表示没搜到，下面的值都是默认值0，别用。
        /// </summary>
        public bool Found { get; set; }

        /// <summary>
        /// 检测到的 Mark 在图像中的行坐标（像素），对应 Y 方向。
        /// 这是 Halcon FindShapeModel 返回的原始值，不是物理坐标。
        /// </summary>
        public double Row { get; set; }

        /// <summary>
        /// 检测到的 Mark 在图像中的列坐标（像素），对应 X 方向。
        /// 同上，是像素坐标，要转物理坐标得用标定矩阵。
        /// </summary>
        public double Col { get; set; }

        /// <summary>
        /// 检测到的 Mark 相对于模板的旋转角度，单位弧度。
        /// 0 表示没旋转，正值表示逆时针旋转（Halcon 惯例）。
        /// 注意：这里返回的是弧度，显示给用户的时候可能要转成角度（乘 180/π）。
        /// </summary>
        public double Angle { get; set; }

        /// <summary>
        /// 模板匹配得分，0~1 之间，越高越像。
        /// <para>
        /// 一般 0.7 以上算匹配得不错，0.5 以下基本不可信。
        /// 这个值取决于图像质量、光照条件、模板质量等因素。
        /// </para>
        /// </summary>
        public double Score { get; set; }

        /// <summary>
        /// 检测结果描述消息，方便日志和界面显示。
        /// 比如"找到标记，得分=0.856"或"未找到标记"。
        /// </summary>
        public string Message { get; set; }

        /// <summary>
        /// 工厂方法：创建一个"未检测到 Mark"的结果实例。
        /// <para>
        /// 当 FindMark 搜不到 Mark 时，用这个方法生成返回值，
        /// 比直接 new MarkDetectionResult 语义更清晰。
        /// </para>
        /// </summary>
        /// <param name="message">没搜到的原因，比如"输入图像无效"、"模板ID无效"、"未找到标记"</param>
        /// <returns>Found=false 的 MarkDetectionResult，其他字段都是0</returns>
        public static MarkDetectionResult NotFound(string message)
        {
            return new MarkDetectionResult
            {
                Found = false,
                Row = 0,
                Col = 0,
                Angle = 0,
                Score = 0,
                Message = message
            };
        }
    }

    /// <summary>
    /// Mark 检测服务，实现 IMarkDetectionService 接口，基于 Halcon FindShapeModel 实现形状模板匹配。
    /// <para>
    /// 核心概念说明：
    /// <list type="bullet">
    /// <item><b>FindShapeModel</b> —— Halcon 官方算子，形状模板匹配。
    /// 它的原理是：先有一张模板图（通过 CreateShapeModel 创建），然后在目标图像里搜索跟模板最像的区域。
    /// 返回匹配到的位置(row, col)、旋转角度(angle)和匹配得分(score)。
    /// 这是工业视觉里最常用的定位方法之一，速度快、精度高、对光照变化有一定鲁棒性。</item>
    /// <item><b>三级降级搜索策略</b> —— 这是我们自己设计的策略，不是 Halcon 的功能。
    /// 思路是：先用严格条件搜，搜不到就放宽条件再搜，再搜不到就进一步放宽。
    /// 这样在光照好的时候能快速精确匹配，光照差的时候也能尽量找到，不至于直接返回"没找到"。
    /// 三级分别是：
    /// <list type="number">
    /// <item>第一级（高精度）：用用户指定的 minScore 和 greediness，标准搜索</item>
    /// <item>第二级（中精度）：降低 minScore 到 max(0.1, min(0.3, 原始minScore))，放宽匹配阈值</item>
    /// <item>第三级（低精度）：在第二级基础上，把 greediness 固定为 0.5，牺牲速度换匹配率</item>
    /// </list>
    /// </item>
    /// </list>
    /// </para>
    /// <para>
    /// ⚠️ 注意：降级搜索意味着即使用户设了较高的 minScore（比如0.7），
    /// 在光照不好的情况下也可能以较低的得分（比如0.3）匹配成功。
    /// 调用方应该根据返回的 Score 值判断结果可信度，不能只看 Found=true 就认为一定准。
    /// </para>
    /// </summary>
    public class MarkDetectionService : IMarkDetectionService
    {
        /// <summary>
        /// 在灰度图像中查找单个 Mark，采用三级降级搜索策略。
        /// <para>
        /// 三级搜索策略详解：
        /// <list type="number">
        /// <item><b>第一级（高精度）</b>：用用户指定的 minScore 和 greediness 搜索。
        /// 这是最理想的情况，光照好、工件位置正常时第一级就能找到。</item>
        /// <item><b>第二级（中精度）</b>：把 minScore 降到 max(0.1, min(0.3, 原始minScore))。
        /// 比如用户设 minScore=0.7，第二级就用 0.3；用户设 minScore=0.2，第二级就用 0.2。
        /// 适用于光照稍有变化、图像质量略差的情况。</item>
        /// <item><b>第三级（低精度）</b>：在第二级基础上，把 greediness 固定为 0.5。
        /// greediness 越小搜索越仔细但越慢，0.5 是个折中值。
        /// 适用于光照变化大、图像质量差的情况，但速度会慢一些。</item>
        /// </list>
        /// </para>
        /// <para>
        /// FindShapeModel 参数说明（这些是 Halcon 官方算子的参数）：
        /// <list type="bullet">
        /// <item>grayImage —— 输入灰度图像，必须是单通道灰度图</item>
        /// <item>modelId —— 形状模板ID，由 CreateShapeModel/CreateShapeModelXld 创建</item>
        /// <item>angleStart —— 搜索起始角度，这里设 -π（即 -180°），表示从 -180° 开始搜</item>
        /// <item>angleExtent —— 搜索角度范围，这里设 π（即 180°），配合 angleStart 就是全角度搜索</item>
        /// <item>minScore —— 最低匹配得分，低于这个值的结果直接丢弃</item>
        /// <item>1 —— numMatches，最多找几个匹配，1 表示只找一个</item>
        /// <item>1 —— maxOverlap，最大重叠率，1 表示允许完全重叠（只找一个时无所谓）</item>
        /// <item>"least_squares" —— 亚像素精度模式，用最小二乘法做亚像素级定位，精度最高</item>
        /// <item>0 —— numPyramids，金字塔层数，0 表示自动选择</item>
        /// <item>greediness —— 搜索贪婪度，0 最安全最慢，1 最快但可能漏检</item>
        /// </list>
        /// </para>
        /// </summary>
        /// <param name="grayImage">输入灰度图像（单通道），从 CameraService 采集后转灰度得到</param>
        /// <param name="modelId">Halcon 形状模板ID，从 TemplateManagerService 获取</param>
        /// <param name="minScore">
        /// 最低匹配得分阈值（0~1），会被钳位到 [0.1, 1.0]。
        /// 常用值：0.6~0.8，越高越严格但可能搜不到。
        /// </param>
        /// <param name="greediness">
        /// 搜索贪婪度（0~1），会被钳位到 [0.0, 1.0]。
        /// 0 = 安全但慢（不漏检但耗时），1 = 快但可能漏检。
        /// 常用值：0.7~0.9，速度和可靠性的折中。
        /// </param>
        /// <returns>包含匹配结果的 MarkDetectionResult</returns>
        public MarkDetectionResult FindMark(HObject grayImage, HTuple modelId, double minScore, double greediness)
        {
            // 输入检查：图像无效就直接返回
            if (grayImage == null || !grayImage.IsInitialized())
            {
                return MarkDetectionResult.NotFound("输入图像无效");
            }

            // 输入检查：模板ID无效就直接返回
            if (modelId == null || modelId.Length == 0)
            {
                return MarkDetectionResult.NotFound("模板ID无效");
            }

            // 钳位参数到合法范围，防止传了越界值导致 Halcon 报错
            double safeMinScore = Math.Max(0.1, Math.Min(1.0, minScore));
            double safeGreediness = Math.Max(0.0, Math.Min(1.0, greediness));

            // 搜索角度范围：-180° 到 +180°，全角度搜索
            // TupleRad() 把角度值从度转成弧度，Halcon 的算子都用弧度
            HTuple angleStart = (new HTuple(-180)).TupleRad();
            HTuple angleExtent = (new HTuple(180)).TupleRad();

            // ===== 第一级：高精度搜索 =====
            // 用用户指定的 minScore 和 greediness，这是最理想的搜索条件
            HOperatorSet.FindShapeModel(
                grayImage,
                modelId,
                angleStart,
                angleExtent,
                safeMinScore,
                1,              // numMatches：只找一个
                1,              // maxOverlap：允许完全重叠
                "least_squares", // 亚像素精度模式，精度最高
                0,              // 金字塔层数：自动
                safeGreediness,
                out HTuple row, out HTuple col, out HTuple angle, out HTuple score);

            // score.Length > 0 表示找到了至少一个匹配
            if (score.Length > 0 && score[0].D > 0)
            {
                return new MarkDetectionResult
                {
                    Found = true,
                    Row = row[0].D,
                    Col = col[0].D,
                    Angle = angle[0].D,
                    Score = score[0].D,
                    Message = $"找到标记，得分={score[0].D:F3}"
                };
            }

            // ===== 第二级：中精度搜索 =====
            // 降低 minScore，放宽匹配阈值
            // 取 max(0.1, min(0.3, 原始minScore))，确保不会降得太离谱
            double fallbackScore = Math.Max(0.1, Math.Min(0.3, safeMinScore));

            HOperatorSet.FindShapeModel(
                grayImage,
                modelId,
                angleStart,
                angleExtent,
                fallbackScore,   // 降低后的 minScore
                1,
                1,
                "least_squares",
                0,
                safeGreediness,  // greediness 保持不变
                out row, out col, out angle, out score);

            if (score.Length > 0 && score[0].D > 0)
            {
                return new MarkDetectionResult
                {
                    Found = true,
                    Row = row[0].D,
                    Col = col[0].D,
                    Angle = angle[0].D,
                    Score = score[0].D,
                    Message = $"降分搜索找到标记，得分={score[0].D:F3}"
                };
            }

            // ===== 第三级：低精度搜索 =====
            // 在第二级基础上，把 greediness 固定为 0.5
            // greediness 小意味着搜索更仔细，不容易漏检，但速度慢
            HOperatorSet.FindShapeModel(
                grayImage,
                modelId,
                angleStart,
                angleExtent,
                fallbackScore,   // 第二级降低后的 minScore
                1,
                1,
                "least_squares",
                0,
                0.5,             // 固定 greediness=0.5，牺牲速度换匹配率
                out row, out col, out angle, out score);

            if (score.Length > 0 && score[0].D > 0)
            {
                return new MarkDetectionResult
                {
                    Found = true,
                    Row = row[0].D,
                    Col = col[0].D,
                    Angle = angle[0].D,
                    Score = score[0].D,
                    Message = $"调整greediness搜索找到标记，得分={score[0].D:F3}"
                };
            }

            // 三级都没搜到，返回"未找到"
            return MarkDetectionResult.NotFound("未找到标记");
        }

        /// <summary>
        /// 同时查找两个 Mark（Mark1 和 Mark2），分别使用各自的形状模板。
        /// <para>
        /// 这个项目用双 Mark 定位：两个 Mark 的连线确定方向，中心点确定位置。
        /// 所以每次检测都要同时找两个 Mark。
        /// </para>
        /// <para>
        /// 内部就是分别调两次 FindMark，每个 Mark 独立执行三级降级搜索。
        /// 两个 Mark 的搜索互不影响，一个搜到另一个没搜到也是可能的。
        /// </para>
        /// </summary>
        /// <param name="grayImage">输入灰度图像，两个 Mark 在同一张图上</param>
        /// <param name="modelId1">Mark1 的 Halcon 形状模板ID，从 TemplateManagerService.ModelID1 获取</param>
        /// <param name="modelId2">Mark2 的 Halcon 形状模板ID，从 TemplateManagerService.ModelID2 获取</param>
        /// <param name="minScore">最低匹配得分阈值（0~1），两个 Mark 用同一个阈值</param>
        /// <param name="greediness">搜索贪婪度（0~1），两个 Mark 用同一个贪婪度</param>
        /// <returns>
        /// 元组 (Mark1检测结果, Mark2检测结果)。
        /// 调用方需要检查两个结果的 Found 属性，都为 true 才能做后续的坐标变换和纠偏。
        /// </returns>
        public (MarkDetectionResult, MarkDetectionResult) FindBothMarks(
            HObject grayImage,
            HTuple modelId1,
            HTuple modelId2,
            double minScore,
            double greediness)
        {
            // 分别搜两个 Mark，各自独立执行三级降级搜索
            MarkDetectionResult result1 = FindMark(grayImage, modelId1, minScore, greediness);
            MarkDetectionResult result2 = FindMark(grayImage, modelId2, minScore, greediness);
            return (result1, result2);
        }
    }
}
