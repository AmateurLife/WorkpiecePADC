namespace WPADC.Models
{
    /// <summary>
    /// 视觉识别结果模型 —— 存储一次视觉匹配的完整结果。
    /// 每次拍照识别后，Halcon的形状模板匹配会找到工件上的基准标记位置，
    /// 然后把匹配结果填到这个结构里：像素坐标、物理坐标、偏移量、匹配得分。
    /// 纠偏模块根据DeltaX、DeltaY、DeltaAngle来修正点胶轨迹。
    /// </summary>
    public class VisionResult
    {
        /// <summary>
        /// 基准标记1的像素行坐标（Y方向，图像中从上往下递增）。
        /// 这是Halcon模板匹配直接输出的像素坐标，还没经过标定转换。
        /// </summary>
        public double Mark1PixelRow { get; set; }

        /// <summary>
        /// 基准标记1的像素列坐标（X方向，图像中从左往右递增）。
        /// 和Mark1PixelRow配对，组成标记1的完整像素坐标。
        /// </summary>
        public double Mark1PixelCol { get; set; }

        /// <summary>
        /// 基准标记2的像素行坐标（Y方向）。
        /// 第二个基准标记的像素行坐标。
        /// </summary>
        public double Mark2PixelRow { get; set; }

        /// <summary>
        /// 基准标记2的像素列坐标（X方向）。
        /// 和Mark2PixelRow配对，组成标记2的完整像素坐标。
        /// </summary>
        public double Mark2PixelCol { get; set; }

        /// <summary>
        /// 基准标记1的物理X坐标，单位mm。
        /// 由Mark1PixelRow和Mark1PixelCol经过CalibrationData.PixelToWorld转换得到。
        /// 这个值就是标记1在物理坐标系中的实际位置。
        /// </summary>
        public double Mark1PhysX { get; set; }

        /// <summary>
        /// 基准标记1的物理Y坐标，单位mm。
        /// 和Mark1PhysX配对。
        /// </summary>
        public double Mark1PhysY { get; set; }

        /// <summary>
        /// 基准标记2的物理X坐标，单位mm。
        /// 由Mark2PixelRow和Mark2PixelCol经过CalibrationData.PixelToWorld转换得到。
        /// </summary>
        public double Mark2PhysX { get; set; }

        /// <summary>
        /// 基准标记2的物理Y坐标，单位mm。
        /// 和Mark2PhysX配对。
        /// </summary>
        public double Mark2PhysY { get; set; }

        /// <summary>
        /// X方向偏移量，单位mm。
        /// 计算方式：实际位置的中心X - 录制配方时的中心X。
        /// 正值表示工件往右偏了，负值表示往左偏了。
        /// 纠偏时把整个轨迹的X坐标都加上这个偏移量。
        /// </summary>
        public double DeltaX { get; set; }

        /// <summary>
        /// Y方向偏移量，单位mm。
        /// 计算方式：实际位置的中心Y - 录制配方时的中心Y。
        /// 正值表示工件往上偏了，负值表示往下偏了。
        /// 纠偏时把整个轨迹的Y坐标都加上这个偏移量。
        /// </summary>
        public double DeltaY { get; set; }

        /// <summary>
        /// 角度偏移量，单位度。
        /// 计算方式：实际两个标记连线的角度 - 录制配方时两个标记连线的角度。
        /// 正值表示工件逆时针转了，负值表示顺时针转了。
        /// 纠偏时需要对轨迹做旋转变换，以标记中心为旋转中心。
        /// </summary>
        public double DeltaAngle { get; set; }

        /// <summary>
        /// 基准标记1的匹配得分，范围0~1，越高越可靠。
        /// Halcon模板匹配输出的相似度，1.0表示完全匹配。
        /// 一般低于0.6（MinScore阈值）的匹配结果会被丢弃。
        /// 如果Score1很低，说明标记1可能没找到或者找错了。
        /// </summary>
        public double Score1 { get; set; }

        /// <summary>
        /// 基准标记2的匹配得分，范围0~1，越高越可靠。
        /// 和Score1一样，标记2的匹配相似度。
        /// 两个标记的得分都高才算识别成功。
        /// </summary>
        public double Score2 { get; set; }

        /// <summary>
        /// 识别结果是否有效。
        /// 只有两个标记都成功匹配（得分都超过阈值）时才为true。
        /// 如果任何一个标记没找到，就是false，这时候不能做纠偏。
        /// 调用方在使用偏移量之前必须先检查这个标志。
        /// </summary>
        public bool IsValid { get; set; }
    }
}
