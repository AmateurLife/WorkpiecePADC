using HalconDotNet;
using WPADC.Models;
using WPADC.Services.Interfaces;

namespace WPADC.Services.Vision
{
    /// <summary>
    /// 标定服务，实现 ICalibrationService 接口，封装 Halcon VectorToHomMat2d 九点标定。
    /// <para>
    /// 标定干的事情就是：找出一组像素坐标和物理坐标的对应关系，算出一个变换矩阵，
    /// 以后只要知道像素坐标，就能通过这个矩阵算出物理坐标（单位mm）。
    /// </para>
    /// <para>
    /// 核心概念说明：
    /// <list type="bullet">
    /// <item><b>VectorToHomMat2d</b> —— Halcon 官方算子，全称"向量到齐次矩阵2D"，
    /// 作用是根据两组对应点（像素点和物理点）计算一个2D仿射变换矩阵。
    /// 它是 Halcon 标定流程里的标准算子，不是我们自己写的。
    /// 至少需要3组对应点才能计算（3点确定仿射变换），但实际一般用9点（3x3网格），
    /// 点越多越精确，因为会做最小二乘拟合。</item>
    /// <item><b>HomMat2D</b> —— 2D齐次变换矩阵，就是 VectorToHomMat2d 算出来的结果。
    /// 它是一个6元素的数组，表示一个2D仿射变换（平移+旋转+缩放+剪切）。
    /// 6个元素的含义：
    /// <list type="number">
    /// <item>H[0] —— 列(col)对物理X的缩放系数</item>
    /// <item>H[1] —— 行(row)对物理X的缩放系数</item>
    /// <item>H[2] —— 物理X的平移偏移量</item>
    /// <item>H[3] —— 列(col)对物理Y的缩放系数</item>
    /// <item>H[4] —— 行(row)对物理Y的缩放系数</item>
    /// <item>H[5] —— 物理Y的平移偏移量</item>
    /// </list>
    /// 变换公式：worldX = H[0]*col + H[1]*row + H[2]
    ///           worldY = H[3]*col + H[4]*row + H[5]
    /// </item>
    /// </list>
    /// </para>
    /// <para>
    /// ⚠️ <b>踩坑提醒（超级重要！）：</b>
    /// VectorToHomMat2d 的参数顺序是 <b>(col, row, worldX, worldY)</b>，
    /// 注意是先 col 后 row，不是先 row 后 col！
    /// 这跟咱们平时习惯的 (row, col) 顺序是反的。
    /// Halcon 里图像坐标用 (col, row) 表示 (x, y)，col 对应水平方向（X），row 对应垂直方向（Y）。
    /// 如果把 row 和 col 传反了，算出来的矩阵是错的，而且不会报错！
    /// 这个坑踩过好几次了，一定要记住。
    /// </para>
    /// </summary>
    public class CalibrationService : ICalibrationService
    {
        /// <summary>
        /// 仿射变换矩阵（2D齐次矩阵），6个元素的 HTuple。
        /// <para>
        /// 这个矩阵是标定的核心产出，后面所有像素→物理坐标的转换都要用它。
        /// 由 VectorToHomMat2d 算出来，也可以从文件加载。
        /// </para>
        /// <para>
        /// 6个元素含义：
        /// worldX = H[0]*col + H[1]*row + H[2]
        /// worldY = H[3]*col + H[4]*row + H[5]
        /// 其中 H[0]~H[1] 是旋转+缩放，H[2] 是X平移，H[3]~H[4] 是旋转+缩放，H[5] 是Y平移。
        /// </para>
        /// </summary>
        private HTuple _homMat2D = new HTuple();

        /// <summary>
        /// 标定是否已完成。SetCalibrationPoints 成功后置 true。
        /// 其他服务（坐标变换、纠偏等）会先检查这个标志，没标定就不干活。
        /// </summary>
        private bool _isCalibrated = false;

        /// <summary>
        /// 获取当前的仿射变换矩阵。
        /// 外面拿到这个矩阵后可以传给 CoordinateTransformService 做坐标变换，
        /// 也可以传给 DistortionCorrectionService 做畸变校正。
        /// </summary>
        public HTuple HomMat2D => _homMat2D;

        /// <summary>
        /// 获取标定是否已完成。
        /// </summary>
        public bool IsCalibrated => _isCalibrated;

        /// <summary>
        /// 设置标定点并计算仿射变换矩阵。这是标定的主入口方法。
        /// <para>
        /// 完整流程：
        /// <list type="number">
        /// <item>从 CalibrationData 里取出像素点列表和物理坐标点列表</item>
        /// <item>把像素点的 row/col 拆成两个 HTuple（pixelCols 和 pixelRows），
        /// 把物理坐标的 x/y 拆成两个 HTuple（worldXs 和 worldYs）</item>
        /// <item>调 Halcon 的 VectorToHomMat2d 算子计算变换矩阵</item>
        /// <item>把矩阵的6个元素存到 CalibrationData.HomMat2D 数组里（方便序列化）</item>
        /// <item>标记标定完成</item>
        /// </list>
        /// </para>
        /// <para>
        /// ⚠️ <b>再次强调踩坑点：</b> VectorToHomMat2d 的参数顺序是
        /// <b>(pixelCols, pixelRows, worldXs, worldYs)</b>，
        /// 先传列再传行！不是 (pixelRows, pixelCols)！
        /// 这是因为 Halcon 的坐标系里 col 对应 X 轴、row 对应 Y 轴，
        /// 算子按 (X, Y) 的逻辑设计，所以 col 在前。
        /// 传反了不会报错，但算出来的矩阵完全错误，后面所有坐标转换都会偏。
        /// </para>
        /// </summary>
        /// <param name="data">
        /// 标定数据，包含：
        /// <list type="bullet">
        /// <item>PixelPoints —— 像素坐标点列表，每个元素是 (row, col) 元组，从图像上手动选取或自动检测得到</item>
        /// <item>WorldPoints —— 物理坐标点列表，每个元素是 (x, y) 元组，单位mm，从运动控制器读取或手动输入</item>
        /// </list>
        /// 标定完成后，data 里的 HomMat2D 和 IsCalibrated 也会被更新。
        /// </param>
        public void SetCalibrationPoints(CalibrationData data)
        {
            // 准备4个 HTuple，分别存像素列、像素行、物理X、物理Y
            HTuple pixelCols = new HTuple();
            HTuple pixelRows = new HTuple();
            HTuple worldXs = new HTuple();
            HTuple worldYs = new HTuple();

            // 把 CalibrationData 里的点列表拆成4个数组
            // 注意：PixelPoints 存的是 (row, col)，取的时候 col 是 .col，row 是 .row
            for (int i = 0; i < data.PixelPoints.Count; i++)
            {
                pixelCols.Append(data.PixelPoints[i].col);  // 像素列 → 对应 X 方向
                pixelRows.Append(data.PixelPoints[i].row);  // 像素行 → 对应 Y 方向
                worldXs.Append(data.WorldPoints[i].x);      // 物理X坐标，单位mm
                worldYs.Append(data.WorldPoints[i].y);      // 物理Y坐标，单位mm
            }

            // 释放旧矩阵（如果有的话），防止内存泄漏
            _homMat2D.Dispose();

            // 调 Halcon 算子计算仿射变换矩阵
            // ⚠️ 参数顺序：pixelCols 在前，pixelRows 在后！
            // 这是 Halcon 的约定，因为 col 对应 X，算子按 (X, Y) 顺序设计
            HOperatorSet.VectorToHomMat2d(pixelCols, pixelRows, worldXs, worldYs, out _homMat2D);

            // 把矩阵存到 CalibrationData 里，方便后续序列化保存
            // _homMat2D[i].D 取第 i 个元素的 double 值
            data.HomMat2D = new double[6];
            for (int i = 0; i < 6; i++)
                data.HomMat2D[i] = _homMat2D[i].D;

            // 标记标定完成
            data.IsCalibrated = true;
            _isCalibrated = true;
        }

        /// <summary>
        /// 验证标定精度，计算所有标定点的平均重投影误差。
        /// <para>
        /// 原理：把像素点通过刚算出来的变换矩阵映射到物理坐标，
        /// 然后跟真实的物理坐标比较，算欧氏距离，最后取平均值。
        /// 误差越小标定越准，一般要求在 0.01mm 以内。
        /// </para>
        /// <para>
        /// 这个方法通常在标定完成后调一下，看看标定质量怎么样。
        /// 如果误差太大，说明标定点可能选得不好，或者参数顺序搞错了。
        /// </para>
        /// </summary>
        /// <param name="data">标定数据，包含像素点和物理坐标点</param>
        /// <returns>
        /// 平均重投影误差（物理单位，mm）。
        /// 如果还没标定就返回 -1，调用方要注意判断。
        /// </returns>
        public double ValidateCalibration(CalibrationData data)
        {
            // 还没标定就没法验证
            if (!_isCalibrated) return -1;

            double errorSum = 0.0;
            for (int i = 0; i < data.PixelPoints.Count; i++)
            {
                // 用变换矩阵把像素坐标转换成物理坐标
                // AffineTransPoint2d 也是 Halcon 官方算子，参数顺序同样是 (col, row)
                HOperatorSet.AffineTransPoint2d(_homMat2D, data.PixelPoints[i].col, data.PixelPoints[i].row,
                    out HTuple calcX, out HTuple calcY);

                // 算计算值和真实值的欧氏距离
                double diffX = calcX.D - data.WorldPoints[i].x;
                double diffY = calcY.D - data.WorldPoints[i].y;
                double error = Math.Sqrt(diffX * diffX + diffY * diffY);
                errorSum += error;
            }

            // 返回平均误差
            return errorSum / data.PixelPoints.Count;
        }

        /// <summary>
        /// 把标定矩阵保存到文件。
        /// <para>
        /// 用 Halcon 的 WriteTuple 算子保存，文件格式是 Halcon 自己的 tuple 格式（.htuple）。
        /// 加载的时候用 ReadTuple 读回来就行。
        /// </para>
        /// </summary>
        /// <param name="filePath">保存路径，建议用 .htuple 后缀，跟 Halcon 惯例一致</param>
        /// <exception cref="InvalidOperationException">还没标定就调这个方法会抛异常</exception>
        public void SaveCalibration(string filePath)
        {
            if (!_isCalibrated) throw new InvalidOperationException("尚未完成标定");
            // WriteTuple 是 Halcon 官方算子，把 HTuple 写到文件
            HOperatorSet.WriteTuple(_homMat2D, filePath);
        }

        /// <summary>
        /// 从文件加载标定矩阵。
        /// <para>
        /// 加载成功后自动标记为已标定状态，不需要再调 SetCalibrationPoints。
        /// </para>
        /// </summary>
        /// <param name="filePath">标定文件路径，跟 SaveCalibration 保存的路径一致</param>
        public void LoadCalibration(string filePath)
        {
            // 释放旧矩阵
            _homMat2D.Dispose();
            // ReadTuple 是 Halcon 官方算子，从文件读取 HTuple
            HOperatorSet.ReadTuple(filePath, out _homMat2D);
            // 加载成功就算标定完成了
            _isCalibrated = true;
        }

        /// <summary>
        /// 释放标定矩阵资源，重置标定状态。
        /// </summary>
        public void Dispose()
        {
            _homMat2D.Dispose();
            _isCalibrated = false;
        }
    }
}
