using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using HalconDotNet;
using WPADC.Models;

namespace WPADC.Views
{
    /// <summary>
    /// 标定数据持久化模型，用于JSON序列化保存/加载九点标定数据。
    /// <para>这个类只负责"存盘"和"读盘"，跟CalibrationData不同——
    /// CalibrationData是运行时用的，这个是往磁盘上写JSON时用的。</para>
    /// <para>保存路径：程序目录/CalibrationData/{名称}.json</para>
    /// </summary>
    public class CalibrationSaveData
    {
        /// <summary>
        /// 标定数据名称，用户保存时自己起的名字（比如"标定1"、"2024-01-15标定"）。
        /// <para>也用作文件名（非法字符会被替换成下划线）。</para>
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// 9个标定点的像素行坐标数组（Halcon的row坐标）。
        /// <para>存储顺序是线性顺序（0~8），不是UI上的蛇形顺序。SnakeMap负责两者之间的映射。</para>
        /// </summary>
        public double[] PixelRows { get; set; }

        /// <summary>
        /// 9个标定点的像素列坐标数组（Halcon的col坐标）。
        /// <para>存储顺序是线性顺序（0~8），不是UI上的蛇形顺序。</para>
        /// </summary>
        public double[] PixelCols { get; set; }

        /// <summary>
        /// 9个标定点的世界X坐标数组（物理坐标，单位mm）。
        /// <para>存储顺序是线性顺序（0~8），不是UI上的蛇形顺序。</para>
        /// </summary>
        public double[] WorldXs { get; set; }

        /// <summary>
        /// 9个标定点的世界Y坐标数组（物理坐标，单位mm）。
        /// <para>存储顺序是线性顺序（0~8），不是UI上的蛇形顺序。</para>
        /// </summary>
        public double[] WorldYs { get; set; }

        /// <summary>
        /// 仿射变换矩阵，6个元素：[a00, a01, a02, a10, a11, a12]。
        /// <para>这是Halcon VectorToHomMat2d算出来的结果，表示从像素坐标到世界坐标的映射关系。</para>
        /// <para>矩阵含义：世界X = a00*像素列 + a01*像素行 + a02，世界Y = a10*像素列 + a11*像素行 + a12</para>
        /// </summary>
        public double[] HomMat2D { get; set; }
    }

    /// <summary>
    /// 九点标定窗口，用于输入9个标定点的像素坐标和世界坐标，生成仿射变换矩阵。
    /// <para>
    /// 九点标定的完整流程：
    /// 1. 打开窗口 → 9组输入框显示已有数据（如果是新建则全空）
    /// 2. 逐点采集 → 用户手动移动到9个位置，记录像素坐标和世界坐标
    /// 3. 计算矩阵 → 点"生成矩阵"按钮，调Halcon的VectorToHomMat2d算仿射变换
    /// 4. 验证精度 → 看矩阵数值是否合理（对角线元素接近1，非对角线接近0说明标定好）
    /// 5. 保存 → 点"保存"关闭窗口，数据写回CalibrationData供主程序使用
    /// </para>
    /// <para>
    /// 也可以从之前保存的JSON文件加载标定数据，不用每次重新标定。
    /// </para>
    /// </summary>
    public partial class CalibrationWindow : Window
    {
        /// <summary>
        /// 当前标定数据（编辑中），构造函数传入，用户修改的就是这个对象。
        /// <para>来源：主窗口打开标定窗口时传入的CalibrationData实例。</para>
        /// </summary>
        private readonly CalibrationData _data;

        /// <summary>
        /// 标定结果数据（保存后供外部读取）。
        /// <para>点"保存"按钮时，把_data赋值给ResultData，同时设DialogResult=true，
        /// 调用方通过ResultData拿到标定结果。</para>
        /// </summary>
        public CalibrationData ResultData { get; private set; }

        /// <summary>
        /// 像素行坐标输入框数组，按UI显示顺序排列（第1行~第9行）。
        /// <para>数组下标0~8对应UI上的第1~9行，但数据存储顺序不同，需要SnakeMap映射。</para>
        /// </summary>
        private readonly TextBox[] _pixelRowBoxes;

        /// <summary>
        /// 像素列坐标输入框数组，按UI显示顺序排列（第1行~第9行）。
        /// </summary>
        private readonly TextBox[] _pixelColBoxes;

        /// <summary>
        /// 世界X坐标输入框数组，按UI显示顺序排列（第1行~第9行）。
        /// </summary>
        private readonly TextBox[] _worldXBoxes;

        /// <summary>
        /// 世界Y坐标输入框数组，按UI显示顺序排列（第1行~第9行）。
        /// </summary>
        private readonly TextBox[] _worldYBoxes;

        /// <summary>
        /// 蛇形映射数组，将UI显示顺序映射到数据存储顺序。
        /// <para>【踩坑】这个映射关系是九点标定里最容易搞错的地方，详细解释如下：</para>
        /// <para>
        /// 九点标定的9个点在物理上是一个3x3的网格：
        /// <code>
        ///   1(左上)  2(上中)  3(右上)
        ///   4(左中)  5(中心)  6(右中)
        ///   7(左下)  8(下中)  9(右下)
        /// </code>
        /// </para>
        /// <para>
        /// 但用户实际操作时，为了减少移动距离，通常按"蛇形"顺序采集：
        /// <code>
        ///   1→2→3     （第一行从左到右）
        ///       ↓
        ///   6←5←4     （第二行从右到左，反过来走）
        ///   ↓
        ///   7→8→9     （第三行从左到右）
        /// </code>
        /// 所以用户输入的顺序是：1,2,3,6,5,4,7,8,9
        /// </para>
        /// <para>
        /// 但数据存储需要按线性顺序（从左到右、从上到下）：0,1,2,3,4,5,6,7,8
        /// 对应关系：UI第0个位置(1号点)→数据0，UI第1个位置(2号点)→数据1，UI第2个位置(3号点)→数据2，
        /// UI第3个位置(6号点)→数据5，UI第4个位置(5号点)→数据4，UI第5个位置(4号点)→数据3，
        /// UI第6个位置(7号点)→数据6，UI第7个位置(8号点)→数据7，UI第8个位置(9号点)→数据8
        /// </para>
        /// <para>
        /// 所以 SnakeMap = { 0, 1, 2, 5, 4, 3, 6, 7, 8 }
        /// 含义：SnakeMap[UI下标] = 数据存储下标
        /// </para>
        /// </summary>
        private static readonly int[] SnakeMap = { 0, 1, 2, 5, 4, 3, 6, 7, 8 };

        /// <summary>
        /// 标定数据文件存储目录，固定在程序目录下的CalibrationData子文件夹。
        /// <para>每个标定数据保存为一个JSON文件，文件名就是用户起的名字（非法字符替换为下划线）。</para>
        /// </summary>
        private static string CalibDataDir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CalibrationData");

        /// <summary>
        /// 九点标定窗口构造函数，初始化UI控件数组并填充已有数据。
        /// <para>参数data是主窗口传入的CalibrationData实例，可能是新的（空的），也可能是已有的（有数据的）。</para>
        /// <para>构造函数做三件事：</para>
        /// <para>1. InitializeComponent()——WPF自动生成的，把XAML控件树建起来</para>
        /// <para>2. 初始化9组输入框数组——把XAML里命名的TextBox引用存到数组里，方便循环操作</para>
        /// <para>3. PopulateFields()——如果data里有已有数据，填充到输入框里</para>
        /// </summary>
        /// <param name="data">已有的标定数据，用于编辑。如果是新建的CalibrationData则各字段为空/默认值。</param>
        public CalibrationWindow(CalibrationData data)
        {
            InitializeComponent();

            _data = data;
            ResultData = data;  // 默认指向同一个对象，点"保存"时ResultData就是最终结果

            // 初始化9组输入框数组，按UI显示顺序（1~9）排列
            // 这些TextBox是在XAML里用x:Name命名的，InitializeComponent()之后就能访问了
            _pixelRowBoxes = new TextBox[]
            {
                PixelRow1, PixelRow2, PixelRow3,
                PixelRow4, PixelRow5, PixelRow6,
                PixelRow7, PixelRow8, PixelRow9
            };
            _pixelColBoxes = new TextBox[]
            {
                PixelCol1, PixelCol2, PixelCol3,
                PixelCol4, PixelCol5, PixelCol6,
                PixelCol7, PixelCol8, PixelCol9
            };
            _worldXBoxes = new TextBox[]
            {
                WorldX1, WorldX2, WorldX3,
                WorldX4, WorldX5, WorldX6,
                WorldX7, WorldX8, WorldX9
            };
            _worldYBoxes = new TextBox[]
            {
                WorldY1, WorldY2, WorldY3,
                WorldY4, WorldY5, WorldY6,
                WorldY7, WorldY8, WorldY9
            };

            // 将已有数据填充到输入框（如果是新建的data，各字段为空，输入框就都是空的）
            PopulateFields();

            // 如果已有标定结果（IsCalibrated=true），显示变换矩阵
            if (_data.HomMat2D != null && _data.HomMat2D.Length >= 6 && _data.IsCalibrated)
            {
                DisplayMatrix(_data.HomMat2D);
            }
        }

        /// <summary>
        /// 将标定数据填充到UI输入框，使用SnakeMap将数据存储顺序映射到UI显示顺序。
        /// <para>数据存储是线性顺序（0~8从左到右从上到下），UI显示是蛇形顺序（1→2→3→6→5→4→7→8→9）。</para>
        /// <para>SnakeMap[i]表示UI第i个位置对应的数据下标。</para>
        /// </summary>
        private void PopulateFields()
        {
            for (int i = 0; i < 9; i++)
            {
                // SnakeMap[i]：UI第i个位置对应的数据存储下标
                int dataIdx = SnakeMap[i];
                if (dataIdx < _data.PixelPoints.Count)
                {
                    _pixelRowBoxes[i].Text = _data.PixelPoints[dataIdx].row.ToString("F3");
                    _pixelColBoxes[i].Text = _data.PixelPoints[dataIdx].col.ToString("F3");
                }
                if (dataIdx < _data.WorldPoints.Count)
                {
                    _worldXBoxes[i].Text = _data.WorldPoints[dataIdx].x.ToString("F3");
                    _worldYBoxes[i].Text = _data.WorldPoints[dataIdx].y.ToString("F3");
                }
            }
        }

        /// <summary>
        /// 生成变换矩阵按钮点击事件，读取9点数据并通过Halcon VectorToHomMat2d计算仿射变换矩阵。
        /// <para>这是九点标定的核心步骤，流程：</para>
        /// <para>1. 从9组输入框读取像素坐标和世界坐标</para>
        /// <para>2. 用SnakeMap把UI顺序映射回数据存储顺序</para>
        /// <para>3. 调Halcon的VectorToHomMat2d算仿射变换矩阵</para>
        /// <para>4. 把矩阵和标定点数据存到_data里</para>
        /// <para>5. 在界面上显示矩阵</para>
        /// </summary>
        private void GenerateMatrix_Click(object sender, RoutedEventArgs e)
        {
            // 准备9个点的数据数组
            double[] pixelRows = new double[9];
            double[] pixelCols = new double[9];
            double[] worldXs = new double[9];
            double[] worldYs = new double[9];

            // 从输入框读取数据，同时做格式校验
            for (int i = 0; i < 9; i++)
            {
                if (!double.TryParse(_pixelRowBoxes[i].Text, out double pr) ||
                    !double.TryParse(_pixelColBoxes[i].Text, out double pc) ||
                    !double.TryParse(_worldXBoxes[i].Text, out double wx) ||
                    !double.TryParse(_worldYBoxes[i].Text, out double wy))
                {
                    MessageBox.Show($"第 {i + 1} 行数据格式错误，请输入有效数值", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                // SnakeMap映射：UI第i个位置的数据存到pixelRows[pixelCols等]的第dataIdx个位置
                int dataIdx = SnakeMap[i];
                pixelRows[dataIdx] = pr;
                pixelCols[dataIdx] = pc;
                worldXs[dataIdx] = wx;
                worldYs[dataIdx] = wy;
            }

            try
            {
                // 调Halcon的VectorToHomMat2d计算仿射变换矩阵
                // 参数：像素列坐标(px)、像素行坐标(py)、世界X坐标(qx)、世界Y坐标(qy)
                // 注意：Halcon里px是列(col)，py是行(row)，别搞反了
                HTuple px = new HTuple(pixelCols);
                HTuple py = new HTuple(pixelRows);
                HTuple qx = new HTuple(worldXs);
                HTuple qy = new HTuple(worldYs);

                // VectorToHomMat2d是Halcon官方的仿射变换计算算子
                // 输入：两组对应点（像素坐标和世界坐标），输出：2D仿射变换矩阵
                HOperatorSet.VectorToHomMat2d(px, py, qx, qy, out HTuple homMat2D);

                // 从HTuple提取6个矩阵元素
                double[] mat = new double[6];
                for (int i = 0; i < 6; i++)
                    mat[i] = homMat2D[i].D;

                // 保存到_data
                _data.HomMat2D = mat;
                _data.IsCalibrated = true;  // 标记为已标定

                // 重新存储标定点数据（用线性顺序，不是UI顺序）
                _data.PixelPoints.Clear();
                _data.WorldPoints.Clear();
                for (int i = 0; i < 9; i++)
                {
                    _data.PixelPoints.Add((pixelRows[i], pixelCols[i]));
                    _data.WorldPoints.Add((worldXs[i], worldYs[i]));
                }

                // 在界面上显示矩阵
                DisplayMatrix(mat);
                MessageBox.Show("变换矩阵生成成功", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (HalconException ex)
            {
                // Halcon计算失败，通常是9个点共线或数据有问题
                MessageBox.Show($"Halcon 计算失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 在文本框中显示仿射变换矩阵，格式化成2x3矩阵的样子。
        /// <para>显示格式：</para>
        /// <code>
        /// HomMat2D:
        ///   [  a00  a01  a02]
        ///   [  a10  a11  a12]
        /// </code>
        /// <para>矩阵含义：世界X = a00*像素列 + a01*像素行 + a02，世界Y = a10*像素列 + a11*像素行 + a12</para>
        /// </summary>
        /// <param name="mat">6元素变换矩阵 [a00, a01, a02, a10, a11, a12]</param>
        private void DisplayMatrix(double[] mat)
        {
            HomMatText.Text = $"HomMat2D:\n" +
                              $"  [{mat[0],12:F6}  {mat[1],12:F6}  {mat[2],12:F6}]\n" +
                              $"  [{mat[3],12:F6}  {mat[4],12:F6}  {mat[5],12:F6}]";
        }

        /// <summary>
        /// 保存标定数据到文件按钮点击事件，把当前9组数据序列化为JSON保存到磁盘。
        /// <para>保存流程：</para>
        /// <para>1. 弹出InputBox让用户输入标定数据名称</para>
        /// <para>2. 校验9组输入框数据格式</para>
        /// <para>3. 创建CalibrationData目录（如果不存在）</para>
        /// <para>4. 序列化为JSON写入文件</para>
        /// <para>注意：这个"保存"是保存到文件，和点窗口底部的"保存"按钮不同——
        /// 那个是确认结果并关闭窗口，这个是持久化到磁盘。</para>
        /// </summary>
        private void SaveData_Click(object sender, RoutedEventArgs e)
        {
            // 用VB的InputBox弹出一个简单的输入框让用户起名字
            // 这里用Microsoft.VisualBasic.Interaction.InputBox是因为WPF没有内置InputBox
            string name = Microsoft.VisualBasic.Interaction.InputBox("请输入标定数据名称：", "保存标定数据", "标定1");
            if (string.IsNullOrWhiteSpace(name)) return;

            // 校验9组输入框数据
            double[] pixelRows = new double[9];
            double[] pixelCols = new double[9];
            double[] worldXs = new double[9];
            double[] worldYs = new double[9];

            for (int i = 0; i < 9; i++)
            {
                if (!double.TryParse(_pixelRowBoxes[i].Text, out pixelRows[i]) ||
                    !double.TryParse(_pixelColBoxes[i].Text, out pixelCols[i]) ||
                    !double.TryParse(_worldXBoxes[i].Text, out worldXs[i]) ||
                    !double.TryParse(_worldYBoxes[i].Text, out worldYs[i]))
                {
                    MessageBox.Show($"第 {i + 1} 行数据格式错误", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            try
            {
                // 确保目录存在
                Directory.CreateDirectory(CalibDataDir);

                // 构造保存数据对象
                var saveData = new CalibrationSaveData
                {
                    Name = name,
                    PixelRows = pixelRows,
                    PixelCols = pixelCols,
                    WorldXs = worldXs,
                    WorldYs = worldYs,
                    HomMat2D = _data.HomMat2D
                };

                // 文件名：去掉非法字符，防止文件系统报错
                string safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
                string filePath = Path.Combine(CalibDataDir, $"{safeName}.json");

                // 用System.Text.Json序列化，WriteIndented=true让JSON有缩进，方便人工查看
                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(filePath, JsonSerializer.Serialize(saveData, options));
                MessageBox.Show($"标定数据 '{name}' 已保存", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 加载标定数据按钮点击事件，从JSON文件列表中选择并加载已保存的标定数据。
        /// <para>加载流程：</para>
        /// <para>1. 扫描CalibrationData目录下的所有.json文件</para>
        /// <para>2. 反序列化每个文件，提取名称列表</para>
        /// <para>3. 弹出CalibrationSelectionWindow让用户选择（也支持删除）</para>
        /// <para>4. 把选中文件的数据填充到输入框</para>
        /// <para>注意：加载后还需要点窗口底部的"保存"按钮才会生效（关闭窗口并返回数据给调用方）。</para>
        /// </summary>
        private void LoadData_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 检查目录是否存在
                if (!Directory.Exists(CalibDataDir))
                {
                    MessageBox.Show("没有已保存的标定数据", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // 扫描所有JSON文件
                var files = Directory.GetFiles(CalibDataDir, "*.json");
                if (files.Length == 0)
                {
                    MessageBox.Show("没有已保存的标定数据", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // 逐个反序列化，收集名称和数据
                var names = new System.Collections.Generic.List<string>();
                var dataList = new System.Collections.Generic.List<CalibrationSaveData>();

                foreach (var f in files)
                {
                    try
                    {
                        var d = JsonSerializer.Deserialize<CalibrationSaveData>(File.ReadAllText(f));
                        if (d != null)
                        {
                            // 名称优先用JSON里的Name字段，没有就用文件名
                            names.Add(d.Name ?? Path.GetFileNameWithoutExtension(f));
                            dataList.Add(d);
                        }
                    }
                    catch { }  // 单个文件反序列化失败不影响其他文件
                }

                if (names.Count == 0)
                {
                    MessageBox.Show("没有可用的标定数据", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // 弹出选择窗口，让用户选一个（或删除一个）
                var dialog = new CalibrationSelectionWindow(names, dataList);
                dialog.Owner = this;

                if (dialog.ShowDialog() == true)
                {
                    // 用户点了删除
                    if (dialog.DeleteRequested && dialog.SelectedIndex >= 0 && dialog.SelectedIndex < files.Length)
                    {
                        File.Delete(files[dialog.SelectedIndex]);
                        MessageBox.Show("标定数据已删除", "删除成功", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    // 用户选了某条数据，加载到输入框
                    if (dialog.SelectedIndex >= 0 && dialog.SelectedIndex < dataList.Count)
                    {
                        var loaded = dataList[dialog.SelectedIndex];
                        // 注意：加载时直接按顺序填充，不需要SnakeMap映射
                        // 因为保存时就是按UI顺序存的（pixelRows[0]对应PixelRow1，以此类推）
                        for (int i = 0; i < 9; i++)
                        {
                            _pixelRowBoxes[i].Text = loaded.PixelRows[i].ToString("F3");
                            _pixelColBoxes[i].Text = loaded.PixelCols[i].ToString("F3");
                            _worldXBoxes[i].Text = loaded.WorldXs[i].ToString("F3");
                            _worldYBoxes[i].Text = loaded.WorldYs[i].ToString("F3");
                        }

                        // 如果有变换矩阵，也加载过来
                        if (loaded.HomMat2D != null && loaded.HomMat2D.Length >= 6)
                        {
                            _data.HomMat2D = loaded.HomMat2D;
                            _data.IsCalibrated = true;
                            DisplayMatrix(loaded.HomMat2D);
                        }

                        // 同步到_data的标定点列表
                        _data.PixelPoints.Clear();
                        _data.WorldPoints.Clear();
                        for (int i = 0; i < 9; i++)
                        {
                            _data.PixelPoints.Add((loaded.PixelRows[i], loaded.PixelCols[i]));
                            _data.WorldPoints.Add((loaded.WorldXs[i], loaded.WorldYs[i]));
                        }

                        MessageBox.Show($"已加载标定数据 '{names[dialog.SelectedIndex]}'，点击保存后生效", "加载成功", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 保存按钮点击事件，确认保存标定数据并关闭窗口。
        /// <para>这里不做任何校验或计算，只是把_data赋给ResultData，然后设DialogResult=true关闭窗口。</para>
        /// <para>调用方通过检查DialogResult和ResultData来获取标定结果。</para>
        /// </summary>
        private void Save_Click(object sender, RoutedEventArgs e)
        {
            ResultData = _data;
            DialogResult = true;  // 设为true表示用户点了保存，调用方据此判断
        }

        /// <summary>
        /// 取消按钮点击事件，放弃修改并关闭窗口。
        /// <para>设DialogResult=false，调用方据此判断用户取消了操作，不使用ResultData。</para>
        /// </summary>
        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
