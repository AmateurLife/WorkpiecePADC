using HalconDotNet;
using WPADC.Models;
using WPADC.Services.Interfaces;

namespace WPADC.Services.Vision
{
    /// <summary>
    /// 坐标变换服务，实现 ICoordinateTransformService 接口，
    /// 基于标定矩阵实现像素→物理坐标转换和纠偏计算。
    /// <para>
    /// 这个服务是视觉定位的核心，把"图像上 Mark 在哪"翻译成"物理世界偏了多少"。
    /// 整个纠偏流程的数学计算都在这里。
    /// </para>
    /// <para>
    /// 核心概念说明：
    /// <list type="bullet">
    /// <item><b>AffineTransPoint2d</b> —— Halcon 官方算子，用仿射变换矩阵对点做坐标变换。
    /// 输入一个变换矩阵和一个像素坐标，输出对应的物理坐标。
    /// 本质上就是做矩阵乘法：[physX, physY] = HomMat2D × [col, row, 1]。</item>
    /// <item><b>纠偏算法</b> —— 比较当前姿态和标准姿态的差异，算出 X/Y 偏移和角度偏差。
    /// 标准姿态是标定时两个参考 Mark 的位置确定的，
    /// 当前姿态是检测时两个当前 Mark 的位置确定的。</item>
    /// </list>
    /// </para>
    /// <para>
    /// ⚠️ <b>踩坑提醒：</b> AffineTransPoint2d 的参数顺序也是 <b>(col, row)</b>，
    /// 不是 (row, col)！这跟 VectorToHomMat2d 的参数顺序是一致的，
    /// 因为 Halcon 的坐标系里 col 对应 X、row 对应 Y。
    /// 传反了坐标会算错，而且不会报错。
    /// </para>
    /// </summary>
    public class CoordinateTransformService : ICoordinateTransformService
    {
        /// <summary>
        /// 将像素坐标转换为物理坐标。
        /// <para>
        /// 这是最基础的坐标变换方法，其他方法都依赖它。
        /// 原理就是用标定矩阵做仿射变换：
        /// physX = H[0]*col + H[1]*row + H[2]
        /// physY = H[3]*col + H[4]*row + H[5]
        /// </para>
        /// <para>
        /// ⚠️ 参数顺序：pixelCol 在前，pixelRow 在后！
        /// 这是 Halcon AffineTransPoint2d 的约定，跟 VectorToHomMat2d 一致。
        /// 因为在 Halcon 的坐标系里，col 对应 X 轴（水平），row 对应 Y 轴（垂直），
        /// 算子按 (X, Y) 的逻辑设计，所以 col 在前。
        /// </para>
        /// </summary>
        /// <param name="homMat2D">
        /// 仿射变换矩阵，由 CalibrationService 计算得到。
        /// 6个元素的 HTuple，含义见 CalibrationService 的注释。
        /// </param>
        /// <param name="pixelCol">像素列坐标（X方向），从 FindShapeModel 的返回值获取</param>
        /// <param name="pixelRow">像素行坐标（Y方向），从 FindShapeModel 的返回值获取</param>
        /// <returns>元组 (物理X坐标, 物理Y坐标)，单位 mm</returns>
        public (double physX, double physY) PixelToPhysical(HTuple homMat2D, double pixelCol, double pixelRow)
        {
            // AffineTransPoint2d 是 Halcon 官方算子
            // 参数顺序：矩阵、列坐标(col/X)、行坐标(row/Y)
            // 输出：物理X坐标、物理Y坐标
            HOperatorSet.AffineTransPoint2d(homMat2D, pixelCol, pixelRow, out HTuple physX, out HTuple physY);
            return (physX.D, physY.D);
        }

        /// <summary>
        /// 计算标准姿态：将两个参考 Mark 的像素坐标转换为物理坐标，
        /// 并计算中心点坐标和连线角度。
        /// <para>
        /// 标准姿态是纠偏的基准，由标定时两个参考 Mark 的位置确定。
        /// 中心点 = 两个 Mark 物理坐标的中点
        /// 角度 = 两个 Mark 连线相对于 X 轴正方向的角度（Atan2 计算）
        /// </para>
        /// <para>
        /// 这个方法通常在标定完成后调用一次，把结果缓存起来，
        /// 后续每次检测时跟当前姿态比较就行，不用重复计算。
        /// </para>
        /// </summary>
        /// <param name="homMat2D">仿射变换矩阵，由 CalibrationService 提供</param>
        /// <param name="refMark1Col">参考 Mark1 列坐标（像素），从 TemplateManagerService.RefMark1Col 获取</param>
        /// <param name="refMark1Row">参考 Mark1 行坐标（像素），从 TemplateManagerService.RefMark1Row 获取</param>
        /// <param name="refMark2Col">参考 Mark2 列坐标（像素），从 TemplateManagerService.RefMark2Col 获取</param>
        /// <param name="refMark2Row">参考 Mark2 行坐标（像素），从 TemplateManagerService.RefMark2Row 获取</param>
        /// <returns>
        /// 元组 (标准中心X, 标准中心Y, 标准角度, Mark1物理X, Mark1物理Y, Mark2物理X, Mark2物理Y)
        /// 角度单位是弧度，中心坐标单位是 mm。
        /// </returns>
        public (double stdCenterX, double stdCenterY, double stdAngle,
                double stdMark1X, double stdMark1Y, double stdMark2X, double stdMark2Y)
            CalculateStandardPose(HTuple homMat2D, double refMark1Col, double refMark1Row,
                double refMark2Col, double refMark2Row)
        {
            // 把两个参考 Mark 的像素坐标转成物理坐标
            var (mark1X, mark1Y) = PixelToPhysical(homMat2D, refMark1Col, refMark1Row);
            var (mark2X, mark2Y) = PixelToPhysical(homMat2D, refMark2Col, refMark2Row);

            // 中心点 = 两个 Mark 的中点
            double centerX = (mark1X + mark2X) / 2.0;
            double centerY = (mark1Y + mark2Y) / 2.0;

            // 角度 = 两个 Mark 连线相对于 X 轴正方向的角度
            // Atan2(dy, dx) 返回弧度值，范围 [-π, π]
            double angle = Math.Atan2(mark2Y - mark1Y, mark2X - mark1X);

            return (centerX, centerY, angle, mark1X, mark1Y, mark2X, mark2Y);
        }

        /// <summary>
        /// 计算纠偏量：比较当前 Mark 检测结果与标准姿态的偏差。
        /// <para>
        /// 这是整个视觉定位的核心算法，7步纠偏流程：
        /// <list type="number">
        /// <item><b>第1步：算标准姿态</b> —— 用参考 Mark 坐标算出标准中心点和标准角度</item>
        /// <item><b>第2步：当前 Mark 像素→物理</b> —— 把当前检测到的两个 Mark 像素坐标转成物理坐标</item>
        /// <item><b>第3步：算当前中心点</b> —— 当前两个 Mark 物理坐标的中点</item>
        /// <item><b>第4步：算当前角度</b> —— 当前两个 Mark 连线的角度</item>
        /// <item><b>第5步：算 X 偏移</b> —— 当前中心X - 标准中心X</item>
        /// <item><b>第6步：算 Y 偏移</b> —— 当前中心Y - 标准中心Y</item>
        /// <item><b>第7步：算角度偏差</b> —— 当前角度 - 标准角度</item>
        /// </list>
        /// </para>
        /// <para>
        /// 偏移量含义：
        /// <list type="bullet">
        /// <item>DeltaX > 0：工件向右偏了，运动控制器需要向左补偿</item>
        /// <item>DeltaY > 0：工件向上偏了（物理坐标系Y向上），运动控制器需要向下补偿</item>
        /// <item>DeltaAngle > 0：工件逆时针转了，运动控制器需要顺时针补偿</item>
        /// </list>
        /// </para>
        /// <para>
        /// 注意：角度偏差是弧度，显示给用户时可能要转成角度（乘 180/π）。
        /// </para>
        /// </summary>
        /// <param name="homMat2D">仿射变换矩阵，由 CalibrationService 提供</param>
        /// <param name="refMark1Col">参考 Mark1 列坐标（像素），标定时记录的位置</param>
        /// <param name="refMark1Row">参考 Mark1 行坐标（像素）</param>
        /// <param name="refMark2Col">参考 Mark2 列坐标（像素）</param>
        /// <param name="refMark2Row">参考 Mark2 行坐标（像素）</param>
        /// <param name="curMark1Col">当前 Mark1 列坐标（像素），FindShapeModel 刚检测到的位置</param>
        /// <param name="curMark1Row">当前 Mark1 行坐标（像素）</param>
        /// <param name="curMark2Col">当前 Mark2 列坐标（像素）</param>
        /// <param name="curMark2Row">当前 Mark2 行坐标（像素）</param>
        /// <param name="score1">Mark1 匹配得分（0~1），来自 FindMark 的返回值</param>
        /// <param name="score2">Mark2 匹配得分（0~1），来自 FindMark 的返回值</param>
        /// <returns>
        /// 包含完整纠偏结果的 VisionResult 实例，包括：
        /// 像素坐标、物理坐标、X/Y偏移量、角度偏差、匹配得分、有效性标志。
        /// </returns>
        public VisionResult CalculateCorrection(HTuple homMat2D,
            double refMark1Col, double refMark1Row, double refMark2Col, double refMark2Row,
            double curMark1Col, double curMark1Row, double curMark2Col, double curMark2Row,
            double score1, double score2)
        {
            // 第1步：算标准姿态（中心点 + 角度）
            var stdPose = CalculateStandardPose(homMat2D, refMark1Col, refMark1Row, refMark2Col, refMark2Row);

            // 第2步：把当前两个 Mark 的像素坐标转成物理坐标
            var (curMark1X, curMark1Y) = PixelToPhysical(homMat2D, curMark1Col, curMark1Row);
            var (curMark2X, curMark2Y) = PixelToPhysical(homMat2D, curMark2Col, curMark2Row);

            // 第3步：算当前中心点
            double curCenterX = (curMark1X + curMark2X) / 2.0;
            double curCenterY = (curMark1Y + curMark2Y) / 2.0;

            // 第4步：算当前角度
            double curAngle = Math.Atan2(curMark2Y - curMark1Y, curMark2X - curMark1X);

            // 第5步：算 X 偏移（当前 - 标准）
            double deltaX = curCenterX - stdPose.stdCenterX;

            // 第6步：算 Y 偏移
            double deltaY = curCenterY - stdPose.stdCenterY;

            // 第7步：算角度偏差
            double deltaAngle = curAngle - stdPose.stdAngle;

            // 打包成 VisionResult 返回
            return new VisionResult
            {
                Mark1PixelRow = curMark1Row,
                Mark1PixelCol = curMark1Col,
                Mark2PixelRow = curMark2Row,
                Mark2PixelCol = curMark2Col,
                Mark1PhysX = curMark1X,
                Mark1PhysY = curMark1Y,
                Mark2PhysX = curMark2X,
                Mark2PhysY = curMark2Y,
                DeltaX = deltaX,
                DeltaY = deltaY,
                DeltaAngle = deltaAngle,
                Score1 = score1,
                Score2 = score2,
                IsValid = true
            };
        }
    }
}
