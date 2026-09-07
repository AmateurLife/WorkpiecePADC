using HalconDotNet;
using WPADC.Models;

namespace WPADC.Services.Interfaces
{
    /// <summary>
    /// 标定服务接口——管九点标定的计算、保存和加载
    /// </summary>
    /// <remarks>
    /// 九点标定是啥？简单说就是：在9个已知物理坐标的位置拍图，
    /// 记录每个位置的像素坐标，然后用这9对"像素↔物理"对应关系，
    /// 算出一个仿射变换矩阵（HomMat2D），以后就能把任意像素坐标翻译成物理坐标了。
    ///
    /// 为什么是9个点？3x3网格，覆盖整个视场，能算出平移+旋转+缩放，
    /// 对于平面定位来说够用了。Halcon 用 VectorToHomMat2d 算子计算。
    ///
    /// 为什么继承 IDisposable？虽然当前实现没有持有需要释放的资源，
    /// 但作为规范预留，以后如果标定服务持有 Halcon 资源就需要 Dispose。
    ///
    /// 调用方：当前 MainViewModel 构造函数注入了 _calibrationService，但尚未直接调用；
    /// 九点标定的矩阵计算实际在 CalibrationWindow 里用 Halcon 算子直接完成，
    /// 标定结果经 CalibrationData（HomMat2D + PixelToWorld）供主程序使用。
    /// 本服务是更规范的实现预留，以后标定逻辑收拢时可以统一走这个接口。
    /// </remarks>
    public interface ICalibrationService : IDisposable
    {
        /// <summary>
        /// 2D仿射变换矩阵——九点标定的核心输出
        /// </summary>
        /// <remarks>
        /// Halcon HTuple 类型，6个元素的矩阵 [H0,H1,H2,H3,H4,H5]。
        /// 含义：physX = H0*col + H1*row + H2，physY = H3*col + H4*row + H5
        ///
        /// 这个矩阵由 SetCalibrationPoints() 或 LoadCalibration() 设置，
        /// 被坐标变换服务（ICoordinateTransformService）和 MainViewModel 使用。
        ///
        /// 【注意】Halcon VectorToHomMat2d 的参数顺序是 (col, row, worldX, worldY)，
        /// col在前row在后，不是 (row, col)！这个坑之前踩过，导致坐标算反了。
        /// </remarks>
        HTuple HomMat2D { get; }

        /// <summary>
        /// 是否已完成标定——SetCalibrationPoints() 或 LoadCalibration() 成功后为 true
        /// </summary>
        /// <remarks>
        /// MainViewModel 用这个判断能不能开始检测——没标定就没法做坐标变换。
        /// 程序启动时会自动加载标定数据，如果加载成功 IsCalibrated 就是 true。
        /// 低频访问——只在标定/加载时变化。
        /// </remarks>
        bool IsCalibrated { get; }

        /// <summary>
        /// 设置标定点并计算变换矩阵——九点标定的核心方法
        /// </summary>
        /// <param name="data">
        /// 标定数据，包含：
        /// - PixelPoints: 9个像素坐标点 (row, col)
        /// - WorldPoints: 9个物理坐标点 (x, y)，单位mm
        /// 这两组点一一对应，第i个像素点对应第i个物理点。
        /// </param>
        /// <remarks>
        /// 谁调用：MainViewModel.CalibrateCommand，用户完成九点标定采集后
        /// 参数从哪来：用户手动移动到9个位置，记录每个位置的像素坐标和物理坐标
        ///
        /// 内部流程：
        /// 1. 从 data.PixelPoints 提取 col 和 row 数组（注意顺序！）
        /// 2. 从 data.WorldPoints 提取 worldX 和 worldY 数组
        /// 3. 调用 Halcon VectorToHomMat2d(col, row, worldX, worldY) 计算矩阵
        /// 4. 把矩阵存到 HomMat2D 属性
        /// 5. 设置 IsCalibrated = true
        ///
        /// 低频操作——标定一次就行，除非换了相机或镜头才需要重新标定。
        /// </remarks>
        void SetCalibrationPoints(CalibrationData data);

        /// <summary>
        /// 验证标定精度——用标定点反算物理坐标，看误差有多大
        /// </summary>
        /// <param name="data">标定数据，和 SetCalibrationPoints 用同一组数据</param>
        /// <returns>平均误差，单位mm，越小越好（一般应该小于0.1mm）</returns>
        /// <remarks>
        /// 谁调用：MainViewModel 里标定完成后验证精度
        /// 内部逻辑：用 HomMat2D 把每个像素点转成物理坐标，和实际物理坐标比，
        /// 算出平均距离误差。
        /// 低频操作——标定完验证一次。
        /// </remarks>
        double ValidateCalibration(CalibrationData data);

        /// <summary>
        /// 保存标定到文件——把 HomMat2D 和标定数据持久化到磁盘
        /// </summary>
        /// <param name="filePath">保存路径，通常是 "Calibration/{名称}.json"</param>
        /// <remarks>
        /// 谁调用：MainViewModel 里标定完成后自动保存
        /// 参数从哪来：filePath 由程序拼接
        /// 低频操作——标定完保存一次。
        /// </remarks>
        void SaveCalibration(string filePath);

        /// <summary>
        /// 从文件加载标定——把之前保存的标定数据读回来
        /// </summary>
        /// <param name="filePath">标定文件路径</param>
        /// <remarks>
        /// 谁调用：MainViewModel 程序启动时自动加载，或用户手动选择标定文件
        /// 参数从哪来：filePath 来自标定文件列表
        /// 加载成功后 IsCalibrated 变 true，HomMat2D 被设置。
        /// 低频操作——程序启动时加载一次。
        /// </remarks>
        void LoadCalibration(string filePath);
    }
}
