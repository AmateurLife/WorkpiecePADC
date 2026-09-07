using System.IO;
using System.Text.Json;
using HalconDotNet;
using WPADC.Services.Interfaces;

namespace WPADC.Services.Vision
{
    /// <summary>
    /// 模板管理服务，实现 ITemplateManagerService 接口，管理双 Mark 模板的完整生命周期。
    /// <para>
    /// 这个类管着两个 Mark 模板（Mark1 和 Mark2），从创建到保存到加载到销毁，全生命周期都在这儿。
    /// 双 Mark 是这个项目的核心定位方案：两个 Mark 的连线确定方向，中心点确定位置。
    /// </para>
    /// <para>
    /// 核心概念说明：
    /// <list type="bullet">
    /// <item><b>CreateShapeModel / CreateShapeModelXld</b> —— Halcon 官方算子，创建形状模板。
    /// 形状模板就是从一张图里提取出形状特征（边缘轮廓），保存成一个"模型"，
    /// 以后用 FindShapeModel 在新图像里搜索跟这个模型匹配的区域。
    /// CreateShapeModel 是从图像区域创建，CreateShapeModelXld 是从 XLD 轮廓创建，
    /// 我们这里用的是 CreateShapeModelXld（因为先做了边缘提取，拿 XLD 轮廓来创建模板更灵活）。</item>
    /// <item><b>双 Mark 模板管理逻辑</b> —— 这个类同时管理 Mark1 和 Mark2 两个模板，
    /// 每个模板有独立的 ID、搜索区域、轮廓、参考位置。
    /// 通过 markIndex（1或2）区分操作哪个 Mark。
    /// 两个 Mark 必须都创建/加载完成（TemplateLoaded=true），才能开始检测。</item>
    /// <item><b>模板文件结构</b> —— 保存时生成3个文件：
    /// <list type="bullet">
    /// <item>MarkModel1.shm —— Mark1 的形状模板文件（Halcon 官方格式）</item>
    /// <item>MarkModel2.shm —— Mark2 的形状模板文件（Halcon 官方格式）</item>
    /// <item>MarkParams.json —— 我们自己定义的参数文件，存区域坐标和参考位置</item>
    /// </list>
    /// .shm 文件用 Halcon 的 WriteShapeModel/ReadShapeModel 读写，
    /// .json 文件用 System.Text.Json 序列化，两边配合才能完整恢复模板。
    /// </item>
    /// </list>
    /// </para>
    /// </summary>
    public class TemplateManagerService : ITemplateManagerService
    {
        /// <summary>
        /// Mark1 的 Halcon 形状模板ID。
        /// 由 CreateShapeModelXld 创建，或者从 .shm 文件加载。
        /// 后面调 FindShapeModel 时要传这个 ID 进去。
        /// </summary>
        private HTuple _modelID1 = new HTuple();

        /// <summary>
        /// Mark2 的 Halcon 形状模板ID。同上，第二个 Mark 的。
        /// </summary>
        private HTuple _modelID2 = new HTuple();

        /// <summary>
        /// Mark1 的搜索区域（矩形 ROI）。
        /// 用户在图像上画的矩形框，后续检测只在这个框里搜 Mark。
        /// 用 GenRectangle1 生成，类型是 HObject（Halcon 的区域类型）。
        /// </summary>
        private HObject _mark1Region;

        /// <summary>
        /// Mark2 的搜索区域（矩形 ROI）。同上，第二个 Mark 的搜索框。
        /// </summary>
        private HObject _mark2Region;

        /// <summary>
        /// Mark1 区域的 XLD 轮廓，用于界面可视化。
        /// 把矩形区域转成轮廓线，显示在 Halcon 窗口上，让用户看到搜索框在哪。
        /// 用 GenContourRegionXld 生成。
        /// </summary>
        private HObject _mark1RegionXld;

        /// <summary>
        /// Mark2 区域的 XLD 轮廓，用于界面可视化。同上。
        /// </summary>
        private HObject _mark2RegionXld;

        /// <summary>
        /// Mark1 模板的轮廓，用于界面可视化。
        /// 用 GetShapeModelContours 从模板 ID 获取，显示在 Halcon 窗口上，
        /// 让用户看到模板长什么样。
        /// </summary>
        private HObject _model1Contours;

        /// <summary>
        /// Mark2 模板的轮廓，用于界面可视化。同上。
        /// </summary>
        private HObject _model2Contours;

        /// <summary>
        /// Mark1 参考位置：行坐标（像素），就是搜索区域的中心点。
        /// 标定时用这个参考位置算"标准姿态"，
        /// 检测时用当前检测位置跟参考位置比较，算出偏移量。
        /// </summary>
        private double _refMark1Row, _refMark1Col;

        /// <summary>
        /// Mark2 参考位置：行坐标和列坐标（像素），同上，第二个 Mark 的。
        /// </summary>
        private double _refMark2Row, _refMark2Col;

        /// <summary>
        /// Mark1 搜索区域的矩形坐标 (row1, col1, row2, col2)。
        /// 左上角 (row1, col1)，右下角 (row2, col2)。
        /// 保存模板时存到 JSON 里，加载模板时读出来恢复区域。
        /// </summary>
        private double _mark1Row1, _mark1Col1, _mark1Row2, _mark1Col2;

        /// <summary>
        /// Mark2 搜索区域的矩形坐标 (row1, col1, row2, col2)。同上。
        /// </summary>
        private double _mark2Row1, _mark2Col1, _mark2Row2, _mark2Col2;

        /// <summary>
        /// Mark1 模板是否已创建。CreateMarkTemplate 成功后置 true。
        /// </summary>
        private bool _mark1Created;

        /// <summary>
        /// Mark2 模板是否已创建。CreateMarkTemplate 成功后置 true。
        /// </summary>
        private bool _mark2Created;

        /// <summary>
        /// 获取 Mark1 的 Halcon 形状模板ID。
        /// 外面调 FindShapeModel 时要传这个 ID。
        /// </summary>
        public HTuple ModelID1 => _modelID1;

        /// <summary>
        /// 获取 Mark2 的 Halcon 形状模板ID。同上。
        /// </summary>
        public HTuple ModelID2 => _modelID2;

        /// <summary>
        /// 获取 Mark1 参考位置行坐标（像素），即搜索区域中心的 Y 坐标。
        /// </summary>
        public double RefMark1Row => _refMark1Row;

        /// <summary>
        /// 获取 Mark1 参考位置列坐标（像素），即搜索区域中心的 X 坐标。
        /// </summary>
        public double RefMark1Col => _refMark1Col;

        /// <summary>
        /// 获取 Mark2 参考位置行坐标（像素）。同上。
        /// </summary>
        public double RefMark2Row => _refMark2Row;

        /// <summary>
        /// 获取 Mark2 参考位置列坐标（像素）。同上。
        /// </summary>
        public double RefMark2Col => _refMark2Col;

        /// <summary>
        /// 获取 Mark1 模板是否已创建。
        /// </summary>
        public bool Mark1Created => _mark1Created;

        /// <summary>
        /// 获取 Mark2 模板是否已创建。
        /// </summary>
        public bool Mark2Created => _mark2Created;

        /// <summary>
        /// 获取双 Mark 模板是否均已加载/创建完成。
        /// 只有这个属性为 true 时，才能开始检测。
        /// </summary>
        public bool TemplateLoaded => _mark1Created && _mark2Created;

        /// <summary>
        /// 获取 Mark1 区域的 XLD 轮廓，用于界面可视化。
        /// 在 Halcon 窗口上显示搜索框的边框线。
        /// </summary>
        public HObject Mark1RegionXld => _mark1RegionXld;

        /// <summary>
        /// 获取 Mark2 区域的 XLD 轮廓，用于界面可视化。同上。
        /// </summary>
        public HObject Mark2RegionXld => _mark2RegionXld;

        /// <summary>
        /// 获取 Mark1 模板的轮廓，用于界面可视化。
        /// 在 Halcon 窗口上显示模板的形状，让用户确认模板对不对。
        /// </summary>
        public HObject Model1Contours => _model1Contours;

        /// <summary>
        /// 获取 Mark2 模板的轮廓，用于界面可视化。同上。
        /// </summary>
        public HObject Model2Contours => _model2Contours;

        /// <summary>
        /// 设置 Mark 搜索区域（矩形 ROI），并计算区域中心作为参考位置。
        /// <para>
        /// 这个方法做三件事：
        /// <list type="number">
        /// <item>用 GenRectangle1 生成矩形区域</item>
        /// <item>用 AreaCenter 算出区域中心点坐标，存为参考位置</item>
        /// <item>用 GenContourRegionXld 生成区域轮廓，用于界面显示</item>
        /// </list>
        /// </para>
        /// <para>
        /// 验证逻辑：区域面积必须 >100 像素，太小说明用户画的框太小，没法做模板。
        /// </para>
        /// </summary>
        /// <param name="markIndex">Mark 索引，1 或 2，指定操作哪个 Mark</param>
        /// <param name="row1">矩形左上角行坐标（像素）</param>
        /// <param name="col1">矩形左上角列坐标（像素）</param>
        /// <param name="row2">矩形右下角行坐标（像素）</param>
        /// <param name="col2">矩形右下角列坐标（像素）</param>
        /// <exception cref="Exception">区域面积过小或 XLD 生成失败时抛出</exception>
        public void SetMarkRegion(int markIndex, double row1, double col1, double row2, double col2)
        {
            HObject region = null;
            HObject regionXld = null;
            try
            {
                // 生成矩形区域，GenRectangle1 是 Halcon 官方算子
                HOperatorSet.GenRectangle1(out region, row1, col1, row2, col2);

                // 算区域面积和中心点坐标
                // AreaCenter 是 Halcon 官方算子，返回面积、中心行、中心列
                HOperatorSet.AreaCenter(region, out HTuple area, out HTuple centerRow, out HTuple centerCol);
                if (area.D <= 100)
                    throw new Exception($"Mark{markIndex}区域面积过小({area.D:F0})，请重新绘制");

                // 把区域转成 XLD 轮廓，用于界面显示
                // GenContourRegionXld 是 Halcon 官方算子，"border" 表示只画外边框
                HOperatorSet.GenContourRegionXld(region, out regionXld, "border");
                if (regionXld == null || !regionXld.IsInitialized())
                    throw new Exception($"Mark{markIndex}区域XLD生成失败");

                // 根据 markIndex 存到对应的字段里
                if (markIndex == 1)
                {
                    // 先释放旧的区域和轮廓，防止内存泄漏
                    _mark1Region?.Dispose();
                    _mark1RegionXld?.Dispose();
                    _mark1Region = region;
                    _mark1RegionXld = regionXld;
                    // 存矩形坐标，保存模板时要用
                    _mark1Row1 = row1; _mark1Col1 = col1;
                    _mark1Row2 = row2; _mark1Col2 = col2;
                    // 存参考位置（区域中心），标定和纠偏时要用
                    _refMark1Row = centerRow.D;
                    _refMark1Col = centerCol.D;
                }
                else
                {
                    _mark2Region?.Dispose();
                    _mark2RegionXld?.Dispose();
                    _mark2Region = region;
                    _mark2RegionXld = regionXld;
                    _mark2Row1 = row1; _mark2Col1 = col1;
                    _mark2Row2 = row2; _mark2Col2 = col2;
                    _refMark2Row = centerRow.D;
                    _refMark2Col = centerCol.D;
                }

                // 置 null 防止 finally 里 Dispose 掉已经赋给字段的对象
                region = null;
                regionXld = null;
            }
            finally
            {
                // 只释放没成功赋给字段的临时对象
                region?.Dispose();
                regionXld?.Dispose();
            }
        }

        /// <summary>
        /// 创建 Mark 形状模板。这是模板创建的主入口方法。
        /// <para>
        /// 完整流程：
        /// <list type="number">
        /// <item>调 SetMarkRegion 设置搜索区域</item>
        /// <item>用 ReduceDomain 把图像裁剪到搜索区域内</item>
        /// <item>用 MeanImage 做均值滤波去噪</item>
        /// <item>用 EdgesSubPix 提取亚像素级边缘</item>
        /// <item>用 SelectShapeXld 筛选有效轮廓（太短的不要）</item>
        /// <item>用 CreateShapeModelXld 基于轮廓创建形状模板</item>
        /// <item>用 GetShapeModelContours 获取模板轮廓（用于界面显示）</item>
        /// </list>
        /// </para>
        /// <para>
        /// CreateShapeModelXld 参数说明（Halcon 官方算子）：
        /// <list type="bullet">
        /// <item>finalXld —— 输入的 XLD 轮廓，从边缘提取得到</item>
        /// <item>"auto" —— NumLevels，金字塔层数，自动选择</item>
        /// <item>-360° ~ +360° —— 角度范围，全角度搜索（360度旋转都能匹配）</item>
        /// <item>"auto" —— AngleStep，角度步长，自动选择</item>
        /// <item>"auto" —— Optimization，优化方式，自动选择</item>
        /// <item>"ignore_local_polarity" —— 极性模式，忽略局部极性变化，
        /// 这样光照变化时也能匹配（常用设置）</item>
        /// <item>5 —— MinContrast，最小对比度，低于这个值的边缘不参与模板创建</item>
        /// </list>
        /// </para>
        /// </summary>
        /// <param name="markIndex">Mark 索引（1 或 2）</param>
        /// <param name="grayImage">输入灰度图像，从 CameraService 采集后转灰度得到</param>
        /// <param name="row1">搜索区域左上角行坐标</param>
        /// <param name="col1">搜索区域左上角列坐标</param>
        /// <param name="row2">搜索区域右下角行坐标</param>
        /// <param name="col2">搜索区域右下角列坐标</param>
        /// <exception cref="Exception">图像无效、区域未设置、无有效边缘或模板创建失败时抛出</exception>
        public void CreateMarkTemplate(int markIndex, HObject grayImage, double row1, double col1, double row2, double col2)
        {
            if (grayImage == null || !grayImage.IsInitialized())
                throw new Exception("输入图像无效");

            // 先设置搜索区域（里面会验证面积、生成XLD等）
            SetMarkRegion(markIndex, row1, col1, row2, col2);

            // 拿到刚设置的区域
            HObject region = markIndex == 1 ? _mark1Region : _mark2Region;
            if (region == null || !region.IsInitialized())
                throw new Exception($"Mark{markIndex}区域未设置");

            // 这些都是中间变量，用完要在 finally 里释放
            HObject imageReduced = null;
            HObject imageMean = null;
            HObject edges = null;
            HObject finalXld = null;
            HObject contours = null;

            try
            {
                // 第1步：裁剪图像到搜索区域内
                // ReduceDomain 是 Halcon 官方算子，把图像的定义域缩小到指定区域
                // 这样后续的边缘提取只在这个区域内做，速度快、噪声少
                HOperatorSet.ReduceDomain(grayImage, region, out imageReduced);

                // 第2步：均值滤波去噪
                // MeanImage 是 Halcon 官方算子，9x9 的均值滤波核
                // 去掉细小噪声，保留主要边缘
                HOperatorSet.MeanImage(imageReduced, out imageMean, 9, 9);

                // 第3步：亚像素级边缘提取
                // EdgesSubPix 是 Halcon 官方算子，用 Canny 算法提取边缘
                // 参数："canny" 是算法名，1.5 是 Alpha（低阈值），15 和 30 是高低阈值
                // 返回的是 XLD 轮廓（亚像素精度），不是简单的像素级边缘
                HOperatorSet.EdgesSubPix(imageMean, out edges, "canny", 1.5, 15, 30);

                // 第4步：筛选有效轮廓
                // SelectShapeXld 是 Halcon 官方算子，按形状特征筛选 XLD
                // "contlength" 是轮廓长度，100~99999 表示只保留长度 >100 像素的轮廓
                // 太短的轮廓通常是噪声，不适合做模板
                HOperatorSet.SelectShapeXld(edges, out finalXld, "contlength", "and", 100, 99999);

                if (finalXld == null || !finalXld.IsInitialized())
                    throw new Exception($"Mark{markIndex}区域无有效边缘特征");

                // 第5步：基于 XLD 轮廓创建形状模板
                HTuple modelID;
                HOperatorSet.CreateShapeModelXld(finalXld, "auto",
                    (new HTuple(-360)).TupleRad(), (new HTuple(360)).TupleRad(),
                    "auto", "auto", "ignore_local_polarity", 5, out modelID);

                if (modelID.Length == 0)
                    throw new Exception($"Mark{markIndex}模板创建失败");

                // 第6步：获取模板轮廓，用于界面可视化
                // GetShapeModelContours 是 Halcon 官方算子，参数 1 表示返回最高层级的轮廓
                HOperatorSet.GetShapeModelContours(out contours, modelID, 1);

                // 存到对应的字段里
                if (markIndex == 1)
                {
                    ClearModel(ref _modelID1);      // 先释放旧模板
                    _model1Contours?.Dispose();     // 释放旧轮廓
                    _modelID1 = modelID;
                    _model1Contours = contours;
                    _mark1Created = true;
                }
                else
                {
                    ClearModel(ref _modelID2);
                    _model2Contours?.Dispose();
                    _modelID2 = modelID;
                    _model2Contours = contours;
                    _mark2Created = true;
                }

                // 置 null 防止 finally 里 Dispose 掉已经赋给字段的对象
                modelID = null;
                contours = null;
            }
            catch
            {
                // 创建失败，重置创建标志
                if (markIndex == 1) _mark1Created = false;
                else _mark2Created = false;
                throw;
            }
            finally
            {
                // 释放所有中间变量，防止内存泄漏
                imageReduced?.Dispose();
                imageMean?.Dispose();
                edges?.Dispose();
                finalXld?.Dispose();
            }
        }

        /// <summary>
        /// 保存双 Mark 模板到指定目录。
        /// <para>
        /// 保存的文件结构：
        /// <list type="bullet">
        /// <item>MarkModel1.shm —— Mark1 的形状模板文件（Halcon 官方格式，二进制）</item>
        /// <item>MarkModel2.shm —— Mark2 的形状模板文件（Halcon 官方格式，二进制）</item>
        /// <item>MarkParams.json —— 我们自己定义的参数文件（JSON 格式，文本），
        /// 存搜索区域坐标和参考位置，加载模板时需要这些信息来恢复区域</item>
        /// </list>
        /// </para>
        /// <para>
        /// 为什么需要 JSON 文件？因为 .shm 文件只保存模板本身（形状特征），
        /// 不保存搜索区域坐标和参考位置。而这些信息在检测时是必须的
        /// （搜索区域决定在哪搜，参考位置决定跟谁比），所以单独用 JSON 存。
        /// </para>
        /// </summary>
        /// <param name="dirPath">保存目录路径，由界面上的"保存模板"按钮传入</param>
        /// <exception cref="InvalidOperationException">双 Mark 模板未完整创建或模型 ID 无效时抛出</exception>
        public void SaveTemplate(string dirPath)
        {
            // 两个 Mark 都创建好了才能保存
            if (!_mark1Created || !_mark2Created)
                throw new InvalidOperationException("双Mark模板未完整创建");

            // 确保目录存在
            Directory.CreateDirectory(dirPath);

            string model1Path = Path.Combine(dirPath, "MarkModel1.shm");
            string model2Path = Path.Combine(dirPath, "MarkModel2.shm");

            // 检查模型 ID 是否有效
            if (_modelID1.Length == 0)
                throw new InvalidOperationException("Mark1模型ID无效，无法序列化");
            if (_modelID2.Length == 0)
                throw new InvalidOperationException("Mark2模型ID无效，无法序列化");

            // 用 Halcon 官方算子保存形状模板到 .shm 文件
            HOperatorSet.WriteShapeModel(_modelID1, model1Path);
            HOperatorSet.WriteShapeModel(_modelID2, model2Path);

            // 把搜索区域坐标和参考位置存到 JSON 文件
            // 这些信息 .shm 文件不包含，必须单独存
            var config = new MarkTemplateConfig
            {
                Mark1Row1 = _mark1Row1, Mark1Col1 = _mark1Col1,
                Mark1Row2 = _mark1Row2, Mark1Col2 = _mark1Col2,
                Mark2Row1 = _mark2Row1, Mark2Col1 = _mark2Col1,
                Mark2Row2 = _mark2Row2, Mark2Col2 = _mark2Col2,
                RefMark1Row = _refMark1Row, RefMark1Col = _refMark1Col,
                RefMark2Row = _refMark2Row, RefMark2Col = _refMark2Col
            };

            string jsonPath = Path.Combine(dirPath, "MarkParams.json");
            string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(jsonPath, json);
        }

        /// <summary>
        /// 从指定目录加载双 Mark 模板。
        /// <para>
        /// 加载流程：
        /// <list type="number">
        /// <item>从 .shm 文件读取形状模板，拿到模型 ID</item>
        /// <item>从 .json 文件读取搜索区域坐标和参考位置</item>
        /// <item>用 GetShapeModelContours 获取模板轮廓（用于界面显示）</item>
        /// </list>
        /// </para>
        /// <para>
        /// 加载完成后 Mark1Created、Mark2Created 都会置 true，
        /// TemplateLoaded 也变为 true，可以开始检测了。
        /// </para>
        /// </summary>
        /// <param name="dirPath">模板目录路径，跟 SaveTemplate 保存的目录一致</param>
        /// <exception cref="InvalidOperationException">模型加载失败或模型 ID 无效时抛出</exception>
        public void LoadTemplate(string dirPath)
        {
            string model1Path = Path.Combine(dirPath, "MarkModel1.shm");
            string model2Path = Path.Combine(dirPath, "MarkModel2.shm");
            string jsonPath = Path.Combine(dirPath, "MarkParams.json");

            // 先释放旧模板，再加载新的
            ClearModel(ref _modelID1);
            // ReadShapeModel 是 Halcon 官方算子，从 .shm 文件读取形状模板
            HOperatorSet.ReadShapeModel(model1Path, out _modelID1);
            if (_modelID1.Length == 0)
                throw new InvalidOperationException("Mark1模型加载失败，模型ID无效");

            ClearModel(ref _modelID2);
            HOperatorSet.ReadShapeModel(model2Path, out _modelID2);
            if (_modelID2.Length == 0)
                throw new InvalidOperationException("Mark2模型加载失败，模型ID无效");

            // 标记模板已加载
            _mark1Created = true;
            _mark2Created = true;

            // 从 JSON 文件读取搜索区域坐标和参考位置
            if (File.Exists(jsonPath))
            {
                string json = File.ReadAllText(jsonPath);
                var config = JsonSerializer.Deserialize<MarkTemplateConfig>(json);
                if (config != null)
                {
                    // 恢复搜索区域坐标
                    _mark1Row1 = config.Mark1Row1; _mark1Col1 = config.Mark1Col1;
                    _mark1Row2 = config.Mark1Row2; _mark1Col2 = config.Mark1Col2;
                    _mark2Row1 = config.Mark2Row1; _mark2Col1 = config.Mark2Col1;
                    _mark2Row2 = config.Mark2Row2; _mark2Col2 = config.Mark2Col2;
                    // 恢复参考位置
                    _refMark1Row = config.RefMark1Row; _refMark1Col = config.RefMark1Col;
                    _refMark2Row = config.RefMark2Row; _refMark2Col = config.RefMark2Col;
                }
            }

            // 获取模板轮廓，用于界面显示
            HOperatorSet.GetShapeModelContours(out _model1Contours, _modelID1, 1);
            HOperatorSet.GetShapeModelContours(out _model2Contours, _modelID2, 1);
        }

        /// <summary>
        /// 根据已保存的区域坐标重建 Mark 搜索区域。
        /// <para>
        /// 通常在加载模板后调用。因为加载模板只恢复了坐标数值，
        /// 还需要根据这些坐标重新生成 HObject 区域和 XLD 轮廓，
        /// 界面上才能显示搜索框。
        /// </para>
        /// </summary>
        /// <param name="grayImage">
        /// 输入灰度图像。当前没用到，保留着是为了以后可能需要根据图像内容优化区域。
        /// </param>
        public void RebuildRegions(HObject grayImage)
        {
            // 如果 Mark1 的区域坐标有效，就重建区域
            if (_mark1Row1 != 0 || _mark1Col1 != 0)
            {
                SetMarkRegion(1, _mark1Row1, _mark1Col1, _mark1Row2, _mark1Col2);
            }
            // 如果 Mark2 的区域坐标有效，就重建区域
            if (_mark2Row1 != 0 || _mark2Col1 != 0)
            {
                SetMarkRegion(2, _mark2Row1, _mark2Col1, _mark2Row2, _mark2Col2);
            }
        }

        /// <summary>
        /// 清除指定形状模板，释放 Halcon 资源。
        /// <para>
        /// 先调 ClearShapeModel（Halcon 官方算子）释放模板占用的内存，
        /// 再把 modelId 重置为空 HTuple。
        /// </para>
        /// </summary>
        /// <param name="modelId">要清除的模板 ID，引用传递，清除后重置为空 HTuple</param>
        private void ClearModel(ref HTuple modelId)
        {
            if (modelId.Length > 0)
            {
                // ClearShapeModel 是 Halcon 官方算子，释放形状模板资源
                // try-catch：万一模板已经被释放了（比如重复调用），不报错
                try { HOperatorSet.ClearShapeModel(modelId); } catch { }
            }
            modelId = new HTuple();
        }

        /// <summary>
        /// 释放所有模板和区域资源。
        /// 实现 IDisposable 接口，在服务替换或应用退出时调用。
        /// </summary>
        public void Dispose()
        {
            // 释放两个模板
            ClearModel(ref _modelID1);
            ClearModel(ref _modelID2);
            // 释放搜索区域
            _mark1Region?.Dispose(); _mark1Region = null;
            _mark2Region?.Dispose(); _mark2Region = null;
            // 释放区域轮廓
            _mark1RegionXld?.Dispose(); _mark1RegionXld = null;
            _mark2RegionXld?.Dispose(); _mark2RegionXld = null;
            // 释放模板轮廓
            _model1Contours?.Dispose(); _model1Contours = null;
            _model2Contours?.Dispose(); _model2Contours = null;
            // 重置创建标志
            _mark1Created = false;
            _mark2Created = false;
        }
    }

    /// <summary>
    /// Mark 模板配置参数，用于序列化/反序列化模板的区域坐标和参考位置。
    /// <para>
    /// 这个类是我们自己定义的，不是 Halcon 的。
    /// 保存为 JSON 文件（MarkParams.json），跟 .shm 模板文件配合使用。
    /// </para>
    /// <para>
    /// 为什么需要这个类？因为 Halcon 的 WriteShapeModel 只保存模板的形状特征，
    /// 不保存搜索区域坐标和参考位置。而这些信息在检测时是必须的，
    /// 所以用这个类把它们序列化到 JSON 文件里。
    /// </para>
    /// </summary>
    public class MarkTemplateConfig
    {
        /// <summary>Mark1 搜索区域左上角行坐标</summary>
        public double Mark1Row1 { get; set; }

        /// <summary>Mark1 搜索区域左上角列坐标</summary>
        public double Mark1Col1 { get; set; }

        /// <summary>Mark1 搜索区域右下角行坐标</summary>
        public double Mark1Row2 { get; set; }

        /// <summary>Mark1 搜索区域右下角列坐标</summary>
        public double Mark1Col2 { get; set; }

        /// <summary>Mark2 搜索区域左上角行坐标</summary>
        public double Mark2Row1 { get; set; }

        /// <summary>Mark2 搜索区域左上角列坐标</summary>
        public double Mark2Col1 { get; set; }

        /// <summary>Mark2 搜索区域右下角行坐标</summary>
        public double Mark2Row2 { get; set; }

        /// <summary>Mark2 搜索区域右下角列坐标</summary>
        public double Mark2Col2 { get; set; }

        /// <summary>Mark1 参考位置行坐标（搜索区域中心的 Y 坐标）</summary>
        public double RefMark1Row { get; set; }

        /// <summary>Mark1 参考位置列坐标（搜索区域中心的 X 坐标）</summary>
        public double RefMark1Col { get; set; }

        /// <summary>Mark2 参考位置行坐标（搜索区域中心的 Y 坐标）</summary>
        public double RefMark2Row { get; set; }

        /// <summary>Mark2 参考位置列坐标（搜索区域中心的 X 坐标）</summary>
        public double RefMark2Col { get; set; }
    }
}
