namespace WPADC.Models
{
    /// <summary>
    /// 九点标定数据模型 —— 整个系统最核心的数据结构之一。
    /// 干嘛用的？把相机拍到的像素坐标(row, col)转换成真实世界的物理坐标(X, Y)，单位mm。
    /// 原理很简单：让运动平台走9个已知位置，每个位置拍一张图，记录下像素坐标和物理坐标，
    /// 然后用Halcon的VectorToHomMat2d算出一个2D仿射变换矩阵，以后就能直接用矩阵做坐标转换了。
    /// </summary>
    public class CalibrationData
    {
        /// <summary>
        /// 标定用的像素坐标点集合。每个元素是(row, col)元组。
        /// row是图像的行号（Y方向，从上往下递增），col是列号（X方向，从左往右递增）。
        /// 这9个点是在九点标定流程中，用户手动移动平台到9个位置后，从相机图像里提取出来的。
        /// 注意：这里存的顺序是标准3x3网格顺序（从左上到右下逐行扫描），
        /// 不是用户实际走位的蛇形顺序，蛇形顺序到标准顺序的转换由SnakeMap处理。
        /// </summary>
        public List<(double row, double col)> PixelPoints { get; set; } = new List<(double row, double col)>();

        /// <summary>
        /// 标定用的物理坐标点集合。每个元素是(x, y)元组，单位mm。
        /// 这9个点是运动平台实际走到的位置，从运动控制卡读取的编码器位置。
        /// 和PixelPoints一一对应：PixelPoints[i]对应WorldPoints[i]。
        /// 物理坐标系：X向右增大，Y向上增大（和图像坐标系Y轴方向相反）。
        /// </summary>
        public List<(double x, double y)> WorldPoints { get; set; } = new List<(double x, double y)>();

        /// <summary>
        /// 2D仿射变换矩阵，6个元素的double数组 [H0, H1, H2, H3, H4, H5]。
        /// 由Halcon的VectorToHomMat2d函数根据9组对应点计算得出。
        ///
        /// 各元素含义（这是最关键的，别搞混了）：
        ///   H[0] —— col对worldX的缩放系数（像素列→物理X方向的缩放）
        ///   H[1] —— row对worldX的缩放系数（像素行→物理X方向的缩放，理想情况下接近0）
        ///   H[2] —— worldX的平移量（偏移）
        ///   H[3] —— col对worldY的缩放系数（像素列→物理Y方向的缩放，理想情况下接近0）
        ///   H[4] —— row对worldY的缩放系数（像素行→物理Y方向的缩放，因为Y轴反向，所以是负值）
        ///   H[5] —— worldY的平移量（偏移）
        ///
        /// 转换公式：
        ///   worldX = H[0]*col + H[1]*row + H[2]
        ///   worldY = H[3]*col + H[4]*row + H[5]
        ///
        /// 注意col在前row在后！这是Halcon的VectorToHomMat2d规定的参数顺序。
        /// </summary>
        public double[] HomMat2D { get; set; } = new double[6];

        /// <summary>
        /// 相机参数字符串。记录标定时使用的相机配置信息。
        /// 如果是通过Halcon标定板做完整标定，这里存的是Halcon的相机内参字符串；
        /// 如果是九点标定（我们项目用的方式），这里就存个标记字符串，比如"simulated_9point_calibration"。
        /// 实际上九点标定不需要相机内参，这个字段主要是兼容Halcon标定流程留的。
        /// </summary>
        public string CameraParameters { get; set; } = "";

        /// <summary>
        /// 是否已完成标定。标定完成后设为true，表示HomMat2D已经可用。
        /// 如果是false，调用PixelToWorld会得到无意义的结果（矩阵全是0）。
        /// </summary>
        public bool IsCalibrated { get; set; } = false;

        // 【蛇形采集顺序说明】
        // 实际标定时，为了减少平台移动距离，9个点是按蛇形走的：
        //   走位顺序：1(左上) → 2(上中) → 3(右上) → 4(右中) → 5(中心) → 6(左中) → 7(左下) → 8(下中) → 9(右下)
        // 但数据存储是按标准3x3网格索引排列的：
        //   idx0(左上) idx1(上中) idx2(右上)
        //   idx3(左中) idx4(中心) idx5(右中)
        //   idx6(左下) idx7(下中) idx8(右下)
        // 走位序号→网格索引的映射关系：
        //   走位0→索引0, 走位1→索引1, 走位2→索引2,
        //   走位3→索引5(蛇形折返), 走位4→索引4, 走位5→索引3(蛇形折返),
        //   走位6→索引6, 走位7→索引7, 走位8→索引8

        /// <summary>
        /// 像素坐标转物理坐标 —— 纯C#实现，不依赖Halcon运行时。
        /// 标定完之后，每次视觉识别到目标位置，就调这个方法把像素坐标转成物理坐标，
        /// 然后把物理坐标发给运动控制卡去定位。
        /// </summary>
        /// <param name="row">像素行坐标（Y方向，图像中从上往下递增）</param>
        /// <param name="col">像素列坐标（X方向，图像中从左往右递增）</param>
        /// <returns>物理坐标(physX, physY)，单位mm</returns>
        /// <remarks>
        /// 【重要踩坑记录】
        /// Halcon的VectorToHomMat2d函数的参数顺序是(col, row, worldX, worldY)，
        /// col在前row在后！这跟咱们直觉上先row后col的习惯是反的。
        /// 因为Halcon内部把col当作第一个坐标轴（对应X），row当作第二个坐标轴（对应Y），
        /// 所以生成的矩阵是按col、row的顺序来组合的。
        ///
        /// 转换公式：
        ///   physX = H[0]*col + H[1]*row + H[2]
        ///   physY = H[3]*col + H[4]*row + H[5]
        ///
        /// 为什么是col在前row在后？
        ///   - col方向和物理X方向同向（都是向右增大），所以col→X是正向映射
        ///   - row方向和物理Y方向反向（row向下增大，Y向上增大），所以row→Y带个负号
        ///   - Halcon按(col, row)的顺序构建矩阵，所以公式里col在前
        ///
        /// 之前踩过的坑：旧代码把row/col顺序写反了，还做了不必要的交换取反，
        /// 导致坐标计算完全错误，定位偏差巨大。现在这个版本是修正过的。
        /// </remarks>
        public (double physX, double physY) PixelToWorld(double row, double col)
        {
            // 直接套仿射变换公式，col在前row在后，跟Halcon VectorToHomMat2d的参数顺序一致
            double physX = HomMat2D[0] * col + HomMat2D[1] * row + HomMat2D[2];
            double physY = HomMat2D[3] * col + HomMat2D[4] * row + HomMat2D[5];
            return (physX, physY);
        }

        /// <summary>
        /// 创建默认标定数据 —— 用模拟参数生成9点标定数据，不需要实际相机和Halcon环境就能跑。
        /// 主要用于开发和调试，在没有硬件的环境下也能测试坐标转换逻辑。
        /// </summary>
        /// <returns>包含模拟9点标定数据的CalibrationData实例，IsCalibrated=true</returns>
        /// <remarks>
        /// 模拟参数是怎么来的：
        ///   - 假设相机分辨率约83.2 px/mm（这个值取决于相机像素大小和镜头放大倍率，
        ///     我们用的海康威信MV-CA060-10GC配默认镜头大概就是这个量级）
        ///   - 图像中心像素设为(512, 512)，模拟一个1024x1024的图像
        ///   - 9个点按3x3网格排列，物理间距15mm（这是标定时平台每次移动的距离）
        ///
        /// 坐标系对应关系（这个很重要，搞反了全错）：
        ///   像素坐标系：row向下增大，col向右增大
        ///   物理坐标系：X向右增大，Y向上增大
        ///   对应关系：col→X（同向），row→Y（反向，因为一个向下增大一个向上增大）
        ///
        /// 9点布局（物理坐标，单位mm）：
        ///   (-15, 15)  (0, 15)  (15, 15)
        ///   (-15,  0)  (0,  0)  (15,  0)
        ///   (-15,-15)  (0,-15)  (15,-15)
        /// </remarks>
        public static CalibrationData CreateFromHDevelopExport()
        {
            var data = new CalibrationData();

            // 模拟相机参数：中心像素(512, 512)，分辨率约83.2 px/mm
            // 83.2这个值 = 相机像素尺寸 / 光学放大倍率，实际值需要标定才能确定
            double centerRow = 512;
            double centerCol = 512;
            double pxPerMm = 83.2;
            double gridSpacingPx = 15.0 * pxPerMm; // 15mm物理间距对应的像素间距 = 15 * 83.2 ≈ 1248像素

            // 9个标定点的像素坐标和物理坐标
            // 按标准3x3网格顺序生成（从左上到右下逐行扫描）：
            //   idx0(左上) idx1(上中) idx2(右上)
            //   idx3(左中) idx4(中心) idx5(右中)
            //   idx6(左下) idx7(下中) idx8(右下)
            // 注意：实际走位是蛇形的（1→2→3→6→5→4→7→8→9），但这里按标准顺序存，
            // 蛇形到标准的映射由SnakeMap处理

            double[] pixelRows = new double[9];
            double[] pixelCols = new double[9];
            double[] worldXs = new double[9];
            double[] worldYs = new double[9];

            int idx = 0;
            for (int r = -1; r <= 1; r++)   // r=-1上排, r=0中排, r=1下排
            {
                for (int c = -1; c <= 1; c++)   // c=-1左列, c=0中列, c=1右列
                {
                    // 像素行：向上减小（row向下增大，物理Y向上增大，方向相反）
                    pixelRows[idx] = centerRow - r * gridSpacingPx;
                    // 像素列：向右增大（col向右增大，物理X向右增大，方向相同）
                    pixelCols[idx] = centerCol + c * gridSpacingPx;
                    // 物理X：向右增大，c=-1对应-15mm，c=0对应0mm，c=1对应15mm
                    worldXs[idx] = c * 15.0;
                    // 物理Y：向上增大，r=-1对应15mm（上排），r=0对应0mm（中排），r=1对应-15mm（下排）
                    worldYs[idx] = r * 15.0;
                    idx++;
                }
            }

            // 把9个点分别存入PixelPoints和WorldPoints
            for (int i = 0; i < 9; i++)
            {
                data.PixelPoints.Add((pixelRows[i], pixelCols[i]));
                data.WorldPoints.Add((worldXs[i], worldYs[i]));
            }

            // 手动计算仿射变换矩阵（正常应该调Halcon的VectorToHomMat2d，这里用数学公式模拟）
            // 因为是理想的无旋转、无倾斜情况，所以H[1]和H[3]都是0
            // 映射关系：(col, row) → (worldX, worldY)

            // col→X的缩放系数：15mm / 1248px ≈ 0.01202 mm/px
            double scaleX = 15.0 / gridSpacingPx;
            // row→Y的缩放系数：同样是15mm / 1248px，但符号为负（row向下增大，Y向上增大，方向相反）
            double scaleY = 15.0 / gridSpacingPx;

            data.HomMat2D = new double[6];
            data.HomMat2D[0] = scaleX;                    // H[0]: col对worldX的贡献，正值（同向）
            data.HomMat2D[1] = 0;                         // H[1]: row对worldX的贡献，理想情况下为0（无旋转）
            data.HomMat2D[2] = -scaleX * centerCol;       // H[2]: worldX的偏移量，让图像中心对应物理原点
            data.HomMat2D[3] = 0;                         // H[3]: col对worldY的贡献，理想情况下为0（无旋转）
            data.HomMat2D[4] = -scaleY;                   // H[4]: row对worldY的贡献，负值（方向相反）
            data.HomMat2D[5] = scaleY * centerRow;        // H[5]: worldY的偏移量，让图像中心对应物理原点

            data.IsCalibrated = true;
            data.CameraParameters = "simulated_9point_calibration";

            return data;
        }
    }
}
