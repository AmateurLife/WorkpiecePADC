using HalconDotNet;
using WPADC.Models;

namespace WPADC.Services.Interfaces
{
    /// <summary>
    /// 坐标变换服务接口——把像素坐标翻译成物理坐标，算出工件偏了多少
    /// </summary>
    /// <remarks>
    /// 这个接口是视觉定位的核心：相机拍出来的是像素坐标（第几行第几列），
    /// 但运动控制需要的是物理坐标（多少毫米）。这个接口就是做这个翻译的。
    ///
    /// 翻译的依据是九点标定生成的仿射变换矩阵（HomMat2D），
    /// 这个矩阵描述了"像素坐标→物理坐标"的映射关系。
    ///
    /// 三个方法的关系：
    /// - PixelToPhysical: 最基础的，把一个像素点翻译成物理坐标
    /// - CalculateStandardPose: 用两个参考Mark算出"标准姿态"（中心+角度）
    /// - CalculateCorrection: 对比当前姿态和标准姿态，算出偏了多少
    ///
    /// 没有继承 IDisposable，因为不持有需要释放的资源（矩阵是 HTuple 值类型）。
    ///
    /// 调用方：目前 MainViewModel 里注入了但没直接调用（_coordTransform 字段存在），
    /// 坐标变换逻辑当前在 MainViewModel 内部直接用 CalibrationData.PixelToWorld() 实现。
    /// 这个服务作为更规范的实现预留，以后重构时可以统一走这个接口。
    /// </remarks>
    public interface ICoordinateTransformService
    {
        /// <summary>
        /// 像素坐标转物理坐标——最基础的坐标翻译
        /// </summary>
        /// <param name="homMat2D">
        /// 仿射变换矩阵（HTuple，6个元素），
        /// 来自 ICalibrationService.HomMat2D，由九点标定计算得到。
        /// 这是 Halcon VectorToHomMat2d 的输出，官方格式。
        /// </param>
        /// <param name="pixelCol">像素列坐标（X方向），从 Mark 检测结果的 Col 字段来</param>
        /// <param name="pixelRow">像素行坐标（Y方向），从 Mark 检测结果的 Row 字段来</param>
        /// <returns>元组 (物理X坐标mm, 物理Y坐标mm)</returns>
        /// <remarks>
        /// 内部用 Halcon AffineTransPoint2d 算子做变换。
        ///
        /// 【重要】Halcon 的 AffineTransPoint2d 参数顺序是 (col, row)，
        /// 不是 (row, col)！这是 Halcon 官方约定，col 对应 X，row 对应 Y。
        /// 传反了坐标会算错，这个坑之前踩过。
        ///
        /// 高频调用——每次检测到 Mark 后都要调，用来把像素坐标翻译成物理坐标。
        /// </remarks>
        (double physX, double physY) PixelToPhysical(HTuple homMat2D, double pixelCol, double pixelRow);

        /// <summary>
        /// 计算标准姿态——根据两个参考Mark的位置，算出工件"应该在哪"
        /// </summary>
        /// <param name="homMat2D">仿射变换矩阵，来自标定服务</param>
        /// <param name="refMark1Col">参考Mark1的列坐标（像素），来自 ITemplateManagerService.RefMark1Col</param>
        /// <param name="refMark1Row">参考Mark1的行坐标（像素），来自 ITemplateManagerService.RefMark1Row</param>
        /// <param name="refMark2Col">参考Mark2的列坐标（像素），来自 ITemplateManagerService.RefMark2Col</param>
        /// <param name="refMark2Row">参考Mark2的行坐标（像素），来自 ITemplateManagerService.RefMark2Row</param>
        /// <returns>
        /// 元组 (标准中心X, 标准中心Y, 标准角度, Mark1物理X, Mark1物理Y, Mark2物理X, Mark2物理Y)
        /// - 标准中心: 两个Mark物理坐标的中点
        /// - 标准角度: 两个Mark连线的角度（弧度），用 Math.Atan2 算
        /// </returns>
        /// <remarks>
        /// "标准姿态"就是创建模板时Mark的位置——那时候工件放得正正好的，
        /// 后续每次检测都跟这个标准比，看偏了多少。
        ///
        /// 低频调用——只在首次检测到双Mark时调用一次，记录标准位置。
        /// </remarks>
        (double stdCenterX, double stdCenterY, double stdAngle, double stdMark1X, double stdMark1Y, double stdMark2X, double stdMark2Y) CalculateStandardPose(HTuple homMat2D, double refMark1Col, double refMark1Row, double refMark2Col, double refMark2Row);

        /// <summary>
        /// 计算纠偏量——对比当前Mark位置和标准位置，算出工件偏了多少
        /// </summary>
        /// <param name="homMat2D">仿射变换矩阵，来自标定服务</param>
        /// <param name="refMark1Col">参考Mark1列坐标（像素），创建模板时记录的标准位置</param>
        /// <param name="refMark1Row">参考Mark1行坐标（像素）</param>
        /// <param name="refMark2Col">参考Mark2列坐标（像素）</param>
        /// <param name="refMark2Row">参考Mark2行坐标（像素）</param>
        /// <param name="curMark1Col">当前Mark1列坐标（像素），刚检测出来的实际位置</param>
        /// <param name="curMark1Row">当前Mark1行坐标（像素）</param>
        /// <param name="curMark2Col">当前Mark2列坐标（像素）</param>
        /// <param name="curMark2Row">当前Mark2行坐标（像素）</param>
        /// <param name="score1">Mark1匹配得分（0~1），来自 MarkDetectionResult.Score</param>
        /// <param name="score2">Mark2匹配得分（0~1），来自 MarkDetectionResult.Score</param>
        /// <returns>
        /// VisionResult 包含：
        /// - DeltaX/DeltaY: XY方向偏移量（mm），正值=偏正方向
        /// - DeltaAngle: 角度偏移量（弧度），正值=逆时针偏
        /// - Mark1/2PixelRow/Col: 当前Mark的像素坐标
        /// - Mark1/2PhysX/Y: 当前Mark的物理坐标
        /// - Score1/2: 匹配得分
        /// - IsValid: 是否有效
        /// </returns>
        /// <remarks>
        /// 计算逻辑：
        /// 1. 用 CalculateStandardPose 算出标准姿态
        /// 2. 用 PixelToPhysical 把当前Mark像素坐标转成物理坐标
        /// 3. 算出当前姿态（中心+角度）
        /// 4. 当前姿态 - 标准姿态 = 纠偏量
        ///
        /// 算出来的 DeltaX/DeltaY/DeltaAngle 会传给 ExecuteRecipe() 作为偏移补偿，
        /// 让点胶轨迹跟着工件偏移走，而不是按原始坐标点。
        ///
        /// 高频调用——检测定时器每200ms触发一次，每次检测到双Mark都调这个。
        /// </remarks>
        VisionResult CalculateCorrection(HTuple homMat2D, double refMark1Col, double refMark1Row, double refMark2Col, double refMark2Row, double curMark1Col, double curMark1Row, double curMark2Col, double curMark2Row, double score1, double score2);
    }
}
