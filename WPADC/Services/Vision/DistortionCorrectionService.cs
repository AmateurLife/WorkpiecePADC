using HalconDotNet;
using WPADC.Models;
using WPADC.Services.Interfaces;

namespace WPADC.Services.Vision
{
    /// <summary>
    /// 畸变校正服务，实现 IDistortionCorrectionService 接口，
    /// 基于相机内参生成径向畸变映射表，对采集图像进行畸变校正。
    /// <para>
    /// 核心概念说明：
    /// <list type="bullet">
    /// <item><b>径向畸变</b> —— 镜头固有的光学缺陷，导致图像边缘的直线变弯。
    /// 最常见的表现就是"桶形畸变"（边缘向外鼓）或"枕形畸变"（边缘向内凹）。
    /// 离光轴越远的地方畸变越严重，中心区域基本没畸变。
    /// 径向畸变的数学模型用多项式表示：
    /// r_distorted = r * (1 + k1*r² + k2*r⁴ + k3*r⁶)
    /// 其中 k1、k2、k3 是径向畸变系数，r 是点到光轴的距离。
    /// 这些系数包含在相机内参（CameraParameters）里。</item>
    /// <item><b>畸变映射表（DistortionMap）</b> —— 预计算好的查找表，
    /// 告诉你"校正后图像的每个像素，应该从原始图像的哪个位置取值"。
    /// 用映射表的好处是：只需要算一次，以后每帧图像直接查表就行，速度快。
    /// 如果每帧都重新算畸变校正，太慢了。</item>
    /// <item><b>GenRadialDistortionMap</b> —— Halcon 官方算子，生成径向畸变映射表。
    /// 输入两组相机参数（畸变参数和理想参数），输出一个映射图像。
    /// "bilinear" 表示用双线性插值，保证校正后图像平滑。</item>
    /// <item><b>MapImage</b> —— Halcon 官方算子，用映射表对图像做校正。
    /// 输入原始图像和映射表，输出校正后的图像。速度很快，因为是查表操作。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 使用流程：
    /// <list type="number">
    /// <item>标定时获取相机内参，保存到文件</item>
    /// <item>调 CreateMap 生成畸变映射表（只需一次）</item>
    /// <item>每帧图像调 CorrectImage 做校正（查表，速度快）</item>
    /// </list>
    /// </para>
    /// <para>
    /// ⚠️ 注意：畸变校正是可选步骤。如果镜头畸变很小（比如远心镜头），
    /// 或者标定精度要求不高，可以跳过这一步，直接用原始图像。
    /// 当前项目里这个功能是备用的，主流程不一定启用。
    /// </para>
    /// </summary>
    public class DistortionCorrectionService : IDistortionCorrectionService
    {
        /// <summary>
        /// 畸变映射表，HObject 类型（Halcon 的图像对象）。
        /// <para>
        /// 这个映射表本质上是一张"坐标查找图"：
        /// 校正后图像的每个像素 (x,y)，映射表里存着它应该从原始图像的哪个位置 (u,v) 取值。
        /// 用 MapImage 算子查表就行，不需要每帧重新计算畸变公式。
        /// </para>
        /// <para>
        /// 由 GenRadialDistortionMap 生成，只生成一次，后续每帧复用。
        /// </para>
        /// </summary>
        private HObject _distortionMap = new HObject();

        /// <summary>
        /// 畸变映射表是否已创建。
        /// CreateMap 成功后置 true，Dispose 后置 false。
        /// CorrectImage 会先检查这个标志，没创建就直接返回原图。
        /// </summary>
        private bool _mapCreated = false;

        /// <summary>
        /// 获取畸变映射表是否已创建。
        /// 外面用这个属性判断是否需要先调 CreateMap。
        /// </summary>
        public bool IsMapCreated => _mapCreated;

        /// <summary>
        /// 根据标定数据中的相机内参创建径向畸变映射表。
        /// <para>
        /// 完整流程：
        /// <list type="number">
        /// <item>释放旧的映射表（如果有的话）</item>
        /// <item>从文件读取相机内参（ReadCamPar，Halcon 官方算子）</item>
        /// <item>生成径向畸变映射表（GenRadialDistortionMap，Halcon 官方算子）</item>
        /// <item>标记 _mapCreated = true</item>
        /// </list>
        /// </para>
        /// <para>
        /// GenRadialDistortionMap 参数说明：
        /// <list type="bullet">
        /// <item>camParam —— 相机内参，包含焦距、主点、畸变系数等，从 .cal 文件读取</item>
        /// <item>camParam —— 第二个参数也是 camParam，表示"从畸变参数映射到理想参数"，
        /// 两个参数相同表示做自身到自身的映射（这是 Halcon 的标准用法）</item>
        /// <item>"bilinear" —— 插值方式，双线性插值，保证校正后图像平滑无锯齿</item>
        /// </list>
        /// </para>
        /// </summary>
        /// <param name="calibData">
        /// 标定数据，需要包含 CameraParameters 字段。
        /// CameraParameters 是相机内参文件的路径（.cal 格式，Halcon 标定板标定生成的）。
        /// </param>
        /// <exception cref="Exception">相机参数读取失败或映射表创建失败时抛出</exception>
        public void CreateMap(CalibrationData calibData)
        {
            try
            {
                // 释放旧的映射表
                _distortionMap.Dispose();
                // 初始化一个空对象，Halcon 的惯例
                HOperatorSet.GenEmptyObj(out _distortionMap);

                // 从文件读取相机内参
                // ReadCamPar 是 Halcon 官方算子，读取 .cal 格式的相机参数文件
                // camParam 包含：焦距(Focus)、主点(PrincipalPoint)、畸变系数(K1,K2,K3,P1,P2)等
                HOperatorSet.ReadCamPar(calibData.CameraParameters, out HTuple camParam);

                // 生成径向畸变映射表
                // GenRadialDistortionMap 是 Halcon 官方算子
                // 两个 camParam 相同：从畸变图像映射到无畸变图像
                // "bilinear"：双线性插值，图像更平滑
                _distortionMap.Dispose();
                HOperatorSet.GenRadialDistortionMap(out _distortionMap, camParam, camParam, "bilinear");
                _mapCreated = true;
            }
            catch (HalconException ex)
            {
                // 创建失败，重置标志
                _mapCreated = false;
                throw new Exception($"畸变映射表创建失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 使用畸变映射表对输入图像进行畸变校正。
        /// <para>
        /// 如果映射表还没创建（IsMapCreated=false），就直接返回原始图像不做处理。
        /// 这不是错误，只是说明没启用畸变校正功能。
        /// </para>
        /// <para>
        /// MapImage 是 Halcon 官方算子，用映射表对图像做几何变换。
        /// 速度很快，因为是查表操作，不需要每帧重新计算畸变公式。
        /// </para>
        /// </summary>
        /// <param name="image">待校正的输入图像，从 CameraService 采集得到</param>
        /// <returns>
        /// 校正后的图像（HObject）。
        /// 如果映射表未创建，返回原始图像（注意：不是 Clone，是同一个引用）。
        /// </returns>
        public HObject CorrectImage(HObject image)
        {
            // 映射表没创建就直接返回原图，不做校正
            if (!_mapCreated) return image;

            // 用映射表做畸变校正，MapImage 是 Halcon 官方算子
            HObject correctedImage;
            HOperatorSet.MapImage(image, _distortionMap, out correctedImage);
            return correctedImage;
        }

        /// <summary>
        /// 释放畸变映射表资源，重置状态。
        /// </summary>
        public void Dispose()
        {
            _distortionMap.Dispose();
            _mapCreated = false;
        }
    }
}
