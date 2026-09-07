using HalconDotNet;
using WPADC.Models;

namespace WPADC.Services.Interfaces
{
    /// <summary>
    /// 畸变校正服务接口——消除镜头带来的图像变形
    /// </summary>
    /// <remarks>
    /// 工业镜头（尤其是广角镜头）拍出来的图像边缘会弯曲，这叫径向畸变。
    /// 这个接口就是干这个校正的：先根据相机内参生成一个映射表，
    /// 然后用映射表把弯曲的图像"拉直"。
    ///
    /// 底层用的是 Halcon 的 GenRadialDistortionMap + MapImage，这是官方标准做法。
    ///
    /// 为什么继承 IDisposable？因为内部持有 Halcon 的 HObject _distortionMap，
    /// 这是 C++ 层的图像映射表，不手动 Dispose 会内存泄漏。
    ///
    /// 调用方：目前 MainViewModel 里注入了但没直接调用（_distortionService 字段存在），
    /// 该服务通过 DI 容器注册，视觉处理流程中可能会用到。
    /// 当前项目用的是九点标定（仿射变换），畸变校正作为可选的高级功能预留。
    /// </remarks>
    public interface IDistortionCorrectionService : IDisposable
    {
        /// <summary>
        /// 畸变映射表是否已创建——CreateMap() 成功后为 true
        /// </summary>
        /// <remarks>
        /// 只有映射表创建成功了，CorrectImage() 才会真正做校正，
        /// 否则直接返回原图（相当于跳过校正）。
        /// 低频访问——只在创建映射表后读一下确认状态。
        /// </remarks>
        bool IsMapCreated { get; }

        /// <summary>
        /// 创建径向畸变映射表——根据相机内参生成校正用的映射表
        /// </summary>
        /// <param name="calibData">
        /// 标定数据，关键是里面的 CameraParameters 属性——
        /// 这是相机内参文件的路径（Halcon 格式），里面存着焦距、畸变系数等参数。
        /// 这些参数通常由 Halcon 标定板标定流程生成，不是我们自己算的。
        /// </param>
        /// <remarks>
        /// 内部流程：
        /// 1. 用 ReadCamPar 读取相机内参文件
        /// 2. 用 GenRadialDistortionMap 生成映射表（bilinear 双线性插值）
        ///
        /// 如果相机内参文件不存在或格式不对，会抛异常，IsMapCreated 保持 false。
        /// 低频操作——相机换镜头或重新标定后才需要重新创建。
        /// </remarks>
        void CreateMap(CalibrationData calibData);

        /// <summary>
        /// 校正图像畸变——把弯曲的图像拉直
        /// </summary>
        /// <param name="image">待校正的输入图像，通常是相机采集的原始图像</param>
        /// <returns>校正后的图像；如果映射表没创建就直接返回原图</returns>
        /// <remarks>
        /// 内部用 Halcon MapImage 算子，把输入图像的每个像素按照映射表重新映射位置。
        /// 如果 IsMapCreated 为 false，说明没做畸变标定，直接返回原图不做处理。
        ///
        /// 中频调用——每帧图像采集后如果启用了畸变校正就会调用。
        /// </remarks>
        HObject CorrectImage(HObject image);
    }
}
