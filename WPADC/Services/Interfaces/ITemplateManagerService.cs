using HalconDotNet;

namespace WPADC.Services.Interfaces
{
    /// <summary>
    /// 模板管理服务接口——管双Mark模板的整个生命周期
    /// </summary>
    /// <remarks>
    /// 这个接口管的是"模板"这个东西从创建到保存到加载的全过程。
    /// 双Mark就是工件上两个定位标记（Mark1和Mark2），通过两个Mark的位置和角度
    /// 可以算出工件的偏移和旋转，这是整个视觉纠偏的基础。
    ///
    /// 为什么继承 IDisposable？因为模板服务持有 Halcon 的 HObject 资源
    /// （ModelID1/2、Mark1RegionXld 等），这些是 C++ 层的图像对象，
    /// 不手动 Dispose 会内存泄漏。MainViewModel.Dispose() 里会调用清理。
    ///
    /// 调用方：MainViewModel 里的模板相关 Command（DrawMark1Command、
    /// DrawMark2Command、SaveTemplateCommand、LoadTemplateCommand 等）
    ///
    /// 工作流程：
    /// 1. 用户在图像上画矩形框 → SetMarkRegion() 记录区域
    /// 2. 根据框内图像创建形状模板 → CreateMarkTemplate()
    /// 3. 保存模板到磁盘 → SaveTemplate()
    /// 4. 下次开机从磁盘加载 → LoadTemplate()
    /// 5. 加载后用保存的坐标重建区域 → RebuildRegions()
    /// </remarks>
    public interface ITemplateManagerService : IDisposable
    {
        /// <summary>
        /// Mark1的形状模型ID——Halcon FindShapeModel 用的模板句柄
        /// </summary>
        /// <remarks>
        /// 这是 Halcon 的 HTuple 类型，本质是一个长整型句柄，指向 C++ 层的形状模板。
        /// CreateMarkTemplate(1, ...) 时创建，FindBothMarks() 时用来做匹配。
        /// 属于 Halcon 官方 API 的固定搭配，不是我们自己定义的。
        /// </remarks>
        HTuple ModelID1 { get; }

        /// <summary>
        /// Mark2的形状模型ID——和 ModelID1 一样，对应第二个Mark
        /// </summary>
        /// <remarks>
        /// CreateMarkTemplate(2, ...) 时创建。
        /// 双Mark检测时 ModelID1 和 ModelID2 同时传给 FindBothMarks()。
        /// </remarks>
        HTuple ModelID2 { get; }

        /// <summary>
        /// 参考Mark1的行坐标（像素，Y方向）——创建模板时Mark1中心的位置
        /// </summary>
        /// <remarks>
        /// 创建模板时记录下来，后续检测时作为"标准位置"传给坐标变换服务。
        /// 坐标系：Halcon图像坐标系，row向下增大，col向右增大。
        /// 这个值在 CreateMarkTemplate() 时写入，SaveTemplate() 时持久化到磁盘。
        /// </remarks>
        double RefMark1Row { get; }

        /// <summary>
        /// 参考Mark1的列坐标（像素，X方向）
        /// </summary>
        /// <remarks>
        /// 和 RefMark1Row 配对，组成 Mark1 的像素坐标 (row, col)。
        /// </remarks>
        double RefMark1Col { get; }

        /// <summary>
        /// 参考Mark2的行坐标（像素，Y方向）——创建模板时Mark2中心的位置
        /// </summary>
        /// <remarks>
        /// 和 RefMark1Row 同理，对应第二个Mark。
        /// </remarks>
        double RefMark2Row { get; }

        /// <summary>
        /// 参考Mark2的列坐标（像素，X方向）
        /// </summary>
        double RefMark2Col { get; }

        /// <summary>
        /// Mark1模板是否已创建——CreateMarkTemplate(1,...) 成功后为 true
        /// </summary>
        /// <remarks>
        /// MainViewModel 用这个判断能不能创建 Mark2（必须先有 Mark1），
        /// 以及能不能保存模板（两个都得创建才行）。
        /// UI上的状态显示也读这个：Mark1TemplateStatus → "✓ 已创建" / "✗ 未创建"。
        /// </remarks>
        bool Mark1Created { get; }

        /// <summary>
        /// Mark2模板是否已创建——CreateMarkTemplate(2,...) 成功后为 true
        /// </summary>
        /// <remarks>
        /// 和 Mark1Created 配合，两个都是 true 才能保存模板。
        /// </remarks>
        bool Mark2Created { get; }

        /// <summary>
        /// 模板是否已加载——从磁盘 LoadTemplate() 成功后为 true
        /// </summary>
        /// <remarks>
        /// MainViewModel 用这个判断能不能开始检测——没有加载模板就没法做形状匹配。
        /// StartDetectionCommand 的 CanExecute 条件之一就是 TemplateLoaded == true。
        /// </remarks>
        bool TemplateLoaded { get; }

        /// <summary>
        /// Mark1区域的XLD轮廓——用来在图像上画出Mark1的矩形框
        /// </summary>
        /// <remarks>
        /// Halcon HObject 类型，存储的是 Mark1 矩形区域的边缘轮廓。
        /// UI上显示"你选的Mark1区域在哪"用的。
        /// CreateMarkTemplate() 或 RebuildRegions() 时生成。
        /// </remarks>
        HObject Mark1RegionXld { get; }

        /// <summary>
        /// Mark2区域的XLD轮廓——用来在图像上画出Mark2的矩形框
        /// </summary>
        /// <remarks>
        /// 和 Mark1RegionXld 同理，对应第二个Mark。
        /// </remarks>
        HObject Mark2RegionXld { get; }

        /// <summary>
        /// Mark1的模型轮廓——形状模板的边缘轮廓，显示模板"长什么样"
        /// </summary>
        /// <remarks>
        /// Halcon GetShapeModelContours() 的输出，是模板本身的轮廓（不是搜索区域的轮廓）。
        /// 用来在UI上预览模板形状，让用户确认模板是否正确。
        /// </remarks>
        HObject Model1Contours { get; }

        /// <summary>
        /// Mark2的模型轮廓——和 Model1Contours 同理，对应第二个Mark
        /// </summary>
        HObject Model2Contours { get; }

        /// <summary>
        /// 设置Mark的矩形搜索区域——用户在图像上画框后调用
        /// </summary>
        /// <param name="markIndex">1=Mark1，2=Mark2</param>
        /// <param name="row1">矩形左上角行坐标（像素）</param>
        /// <param name="col1">矩形左上角列坐标（像素）</param>
        /// <param name="row2">矩形右下角行坐标（像素）</param>
        /// <param name="col2">矩形右下角列坐标（像素）</param>
        /// <remarks>
        /// 谁调用：MainViewModel 的 DrawMark1/DrawMark2 逻辑
        /// 参数从哪来：用户在 Halcon 窗口上拖拽画矩形，Halcon 的 draw_rectangle1 算子返回坐标
        /// 这个方法只是记录区域坐标，不会创建模板，创建模板要调 CreateMarkTemplate()。
        /// </remarks>
        void SetMarkRegion(int markIndex, double row1, double col1, double row2, double col2);

        /// <summary>
        /// 创建Mark形状模板——根据框内图像生成 Halcon 形状模板
        /// </summary>
        /// <param name="markIndex">1=Mark1，2=Mark2</param>
        /// <param name="grayImage">当前灰度图像，从相机采集得到</param>
        /// <param name="row1">矩形左上角行坐标（像素）</param>
        /// <param name="col1">矩形左上角列坐标（像素）</param>
        /// <param name="row2">矩形右下角行坐标（像素）</param>
        /// <param name="col2">矩形右下角列坐标（像素）</param>
        /// <remarks>
        /// 谁调用：MainViewModel 的 DrawMark1/DrawMark2 逻辑，画完框后紧接着调用
        /// 参数从哪来：
        /// - markIndex: 由用户点的是"画Mark1"还是"画Mark2"按钮决定
        /// - grayImage: 从 _cameraService.CurrentImage 取的当前图像
        /// - row1/col1/row2/col2: 用户画的矩形坐标
        ///
        /// 内部做了什么：
        /// 1. 裁剪矩形区域内的图像
        /// 2. 调用 Halcon CreateShapeModel 创建形状模板
        /// 3. 记录模板中心坐标到 RefMark1Row/Col 或 RefMark2Row/Col
        /// 4. 生成区域XLD轮廓和模型轮廓
        /// 5. 设置 Mark1Created/Mark2Created = true
        ///
        /// 注意：必须先创建 Mark1 才能创建 Mark2，这是业务约束。
        /// </remarks>
        void CreateMarkTemplate(int markIndex, HObject grayImage, double row1, double col1, double row2, double col2);

        /// <summary>
        /// 保存模板到磁盘——把双Mark模板的所有数据序列化到指定目录
        /// </summary>
        /// <param name="dirPath">保存目录路径，通常是 "Templates/{模板名称}/"</param>
        /// <remarks>
        /// 谁调用：MainViewModel.SaveTemplateCommand，用户点击"保存模板"按钮
        /// 参数从哪来：dirPath 由用户输入的模板名称拼接而成
        ///
        /// 保存的内容包括：两个形状模型文件(.shm)、参考坐标、区域参数等。
        /// 低频操作——创建好模板后保存一次。
        /// </remarks>
        void SaveTemplate(string dirPath);

        /// <summary>
        /// 从磁盘加载模板——把之前保存的双Mark模板数据反序列化回来
        /// </summary>
        /// <param name="dirPath">模板目录路径，通常是 "Templates/{模板名称}/"</param>
        /// <remarks>
        /// 谁调用：MainViewModel 的模板加载逻辑（程序启动时自动加载，或用户手动选择）
        /// 参数从哪来：dirPath 来自模板列表里用户选择的模板名称
        ///
        /// 加载后会设置 TemplateLoaded = true，Mark1Created = true，Mark2Created = true。
        /// 低频操作——程序启动时加载一次，或切换模板时加载。
        /// </remarks>
        void LoadTemplate(string dirPath);

        /// <summary>
        /// 根据保存的坐标重建区域——加载模板后把矩形框画回来
        /// </summary>
        /// <param name="grayImage">当前灰度图像，用来确定图像尺寸</param>
        /// <remarks>
        /// 谁调用：MainViewModel 的模板加载逻辑，LoadTemplate() 之后紧接着调用
        /// 参数从哪来：grayImage 从相机采集得到
        ///
        /// 为什么要重建？因为保存到磁盘的只有坐标数值，没有 XLD 轮廓对象，
        /// 加载后需要根据坐标重新生成 Mark1RegionXld/Mark2RegionXld，
        /// 这样UI上才能重新显示矩形框。
        /// 低频操作——每次加载模板后调用一次。
        /// </remarks>
        void RebuildRegions(HObject grayImage);
    }
}
