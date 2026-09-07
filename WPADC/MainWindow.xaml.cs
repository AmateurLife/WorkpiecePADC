using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using HalconDotNet;
using WPADC.Models;
using WPADC.Services;
using WPADC.ViewModels;

namespace WPADC
{
    /// <summary>
    /// 主窗口的Code-Behind（代码后置）。
    /// <para>
    /// 在MVVM架构里，Code-Behind的角色很明确：<b>只做纯UI操作，不做业务逻辑</b>。
    /// 具体来说就是：Canvas画图、Halcon窗口交互、TextBox输入验证这些必须依赖WPF控件的东西。
    /// 所有业务逻辑（运动控制、配方管理、标定计算等）都在ViewModel里，这里只负责"把ViewModel的数据画到屏幕上"。
    /// </para>
    /// <para>
    /// ViewModel通过DI容器在App.OnStartup中创建，赋值给窗口的DataContext，
    /// 所以这里不new ViewModel，而是从DataContext拿现成的实例。
    /// </para>
    /// </summary>
    public partial class MainWindow : Window
    {
        /// <summary>
        /// 主视图模型实例，从窗口DataContext获取。
        /// <para>来源：DI容器在App.OnStartup中创建MainViewModel并赋值给DataContext，
        /// 窗口Loaded时从DataContext取出来存到这个字段，后面所有事件处理都用它。</para>
        /// <para>用null!标记非空是因为在构造函数里还没赋值，Loaded之后一定有值。</para>
        /// </summary>
        private MainViewModel _viewModel = null!;

        /// <summary>
        /// 轨迹重绘定时器，运动过程中按固定间隔刷新Canvas。
        /// <para>间隔设了10ms（实际WPF DispatcherTimer精度大概15-20ms，所以实际刷新率约50-60fps）。</para>
        /// <para>只在运动中才启动，静止时停掉，避免白白占CPU。</para>
        /// <para>这是WPF官方推荐的定时刷新方式，DispatcherTimer跑在UI线程上，不用操心跨线程问题。</para>
        /// </summary>
        private readonly DispatcherTimer _redrawTimer;

        /// <summary>
        /// 当前Halcon图像的宽度（像素），首帧图像到来时设置。
        /// <para>用于DrawWpfAxes计算坐标轴叠加层的位置和刻度间距。</para>
        /// </summary>
        private int _imageWidth;

        /// <summary>
        /// 当前Halcon图像的高度（像素），首帧图像到来时设置。
        /// <para>用于DrawWpfAxes计算坐标轴叠加层的位置和刻度间距。</para>
        /// </summary>
        private int _imageHeight;

        // ========== 下面是一堆画刷（Brush），定义Canvas上各种元素的颜色 ==========
        // 都是static readonly，因为画刷是不可变的，全局共享一份就行，不用每次绘制都new

        /// <summary>
        /// 出胶段轨迹颜色（红色系），表示胶阀打开时走过的路径。
        /// <para>在DrawMotionTrail里用到，cur.glueOn为true时用这个颜色画线。</para>
        /// </summary>
        private static readonly SolidColorBrush GlueColor = new(Color.FromRgb(220, 40, 40));

        /// <summary>
        /// 移动段轨迹颜色（黄色系），表示胶阀关闭时的空走路径。
        /// <para>在DrawMotionTrail里用到，cur.glueOn为false时用这个颜色画线。</para>
        /// </summary>
        private static readonly SolidColorBrush MoveColor = new(Color.FromRgb(200, 180, 30));

        /// <summary>
        /// 配方预览-出胶段颜色（暗红色），比实时轨迹的红色暗一些，方便区分"预览"和"实际"。
        /// <para>在DrawRecipePreview里用到，pt.GlueOn为true时用这个颜色。</para>
        /// </summary>
        private static readonly SolidColorBrush RecipeGlueColor = new(Color.FromRgb(180, 60, 60));

        /// <summary>
        /// 配方预览-移动段颜色（暗黄色），比实时轨迹的黄色暗一些。
        /// <para>在DrawRecipePreview里用到，pt.GlueOn为false时用这个颜色。</para>
        /// </summary>
        private static readonly SolidColorBrush RecipeMoveColor = new(Color.FromRgb(160, 150, 50));

        /// <summary>
        /// 快速移动段轨迹颜色（灰色虚线），表示G0快速定位移动。
        /// <para>在DrawRecipePreview里用到，pt.Type == TrajectoryType.RapidMove时用这个颜色+虚线。</para>
        /// </summary>
        private static readonly SolidColorBrush RapidColor = new(Color.FromRgb(120, 120, 120));

        /// <summary>
        /// 坐标轴线画刷（浅灰色），画X轴和Y轴的主线。
        /// </summary>
        private static readonly SolidColorBrush AxisBrush = new(Color.FromRgb(200, 200, 200));

        /// <summary>
        /// 刻度线画刷（浅灰色），画坐标轴上的小刻度线和刻度数字。
        /// </summary>
        private static readonly SolidColorBrush TickBrush = new(Color.FromRgb(180, 180, 180));

        /// <summary>
        /// 当前位置标记画刷（蓝色），画运动轴当前位置的十字+圆圈。
        /// </summary>
        private static readonly SolidColorBrush PositionBrush = new(Color.FromRgb(0, 100, 200));

        /// <summary>
        /// 限位区域填充画刷（半透明红色），在±50mm范围外画半透明遮罩，提醒操作者不要超限。
        /// <para>Alpha=50，很淡的红色，不会遮挡下面的轨迹。</para>
        /// </summary>
        private static readonly SolidColorBrush LimitBrush = new(Color.FromArgb(50, 229, 57, 53));

        /// <summary>
        /// 限位边框画刷（较深半透明红色），画限位区域的虚线边框。
        /// <para>Alpha=120，比填充色深一些，边框更明显。</para>
        /// </summary>
        private static readonly SolidColorBrush LimitBorderBrush = new(Color.FromArgb(120, 229, 57, 53));

        /// <summary>
        /// 主窗口构造函数，初始化组件并设置重绘定时器。
        /// <para>这里只做两件事：1. InitializeComponent()是WPF自动生成的，把XAML里的控件树建起来；
        /// 2. 创建重绘定时器，运动时持续刷新Canvas。</para>
        /// <para>注意：这里不订阅事件！事件订阅放在MainWindow_Loaded里，因为那时DataContext才有值。</para>
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();
            // 窗口Loaded事件——等DataContext赋值完了再订阅ViewModel事件
            Loaded += MainWindow_Loaded;

            // 创建10ms间隔的重绘定时器，用于运动过程中持续刷新轨迹Canvas
            // Tick里直接调DrawTrajectory()，这个方法会清空Canvas重新画所有东西
            _redrawTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            _redrawTimer.Tick += (s, e) => DrawTrajectory();
        }

        /// <summary>
        /// 窗口加载完成事件处理。
        /// <para>这是整个Code-Behind的初始化入口，在窗口Loaded之后DataContext已经有值了，
        /// 所以在这里做所有需要ViewModel的初始化工作：</para>
        /// <para>1. 从DataContext拿到DI容器注入的ViewModel实例</para>
        /// <para>2. 订阅ViewModel的各种事件（Mark点绘制、属性变更、首帧图像、轨迹更新）</para>
        /// <para>3. 给Canvas的SizeChanged挂事件，窗口大小变了就重绘</para>
        /// <para>4. 递归遍历所有TextBox绑定小数输入验证（解决WPF绑定double时"3."无法解析的坑）</para>
        /// <para>5. 首次绘制轨迹（用Dispatcher.BeginInvoke延迟到布局完成后再画，否则Canvas尺寸还是0）</para>
        /// </summary>
        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 从DataContext获取DI容器注入的ViewModel实例
            // DataContext是在App.OnStartup里通过 serviceProvider.GetRequiredService<MainViewModel>() 创建的
            _viewModel = (MainViewModel)DataContext;

            // 订阅ViewModel的Mark点绘制请求事件
            // 这两个事件是ViewModel主动发起的，比如用户点"画Mark1"按钮，ViewModel触发事件，这里响应并调Halcon的交互绘制
            _viewModel.RequestDrawMark1 += OnRequestDrawMark1;
            _viewModel.RequestDrawMark2 += OnRequestDrawMark2;
            // 订阅属性变更事件——当CurrentImage变化时，把新图像显示到Halcon窗口
            _viewModel.PropertyChanged += OnPropertyChanged;
            // 订阅首帧图像事件——第一帧图像到来时设置Halcon窗口坐标范围，后续图像才能正确显示
            _viewModel.FirstImageReceived += OnFirstImageReceived;
            // 订阅轨迹更新事件——运动过程中轨迹点变了，就刷新Canvas和数据面板
            _viewModel.TrajectoryUpdated += OnTrajectoryUpdated;

            // Canvas尺寸变化时重绘轨迹（比如窗口最大化/还原）
            TrajectoryCanvas.SizeChanged += (s2, e2) => DrawTrajectory();

            // 为所有参数TextBox绑定小数输入验证，允许输入浮点数（含负号和小数点）
            // 【踩坑】WPF TextBox绑定double属性时，输入"3."这种中间状态会被拒绝，
            // 因为"3."无法解析为double。这个方法递归遍历所有TextBox，给它们加上自定义验证。
            BindDecimalValidationToAllTextBoxes(this);

            // 窗口加载后首次绘制轨迹
            // 用Dispatcher.BeginInvoke延迟到Loaded优先级，确保布局计算完成、Canvas有尺寸了再画
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => DrawTrajectory());
        }

        /// <summary>
        /// 窗口关闭事件处理，做好清理工作，防止资源泄漏。
        /// <para>1. 停止重绘定时器</para>
        /// <para>2. 取消所有事件订阅（防止ViewModel还活着时继续触发回调）</para>
        /// <para>3. 调用ViewModel的Dispose释放资源（比如停止运动、关闭相机等）</para>
        /// </summary>
        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            // 先停定时器，不然关窗口了还在画
            _redrawTimer.Stop();

            if (_viewModel != null)
            {
                // 取消事件订阅，防止内存泄漏
                // .NET里事件订阅会让发布者持有订阅者的引用，不取消订阅的话GC回收不了
                _viewModel.RequestDrawMark1 -= OnRequestDrawMark1;
                _viewModel.RequestDrawMark2 -= OnRequestDrawMark2;
                _viewModel.PropertyChanged -= OnPropertyChanged;
                _viewModel.FirstImageReceived -= OnFirstImageReceived;
                // 调用ViewModel的Dispose，释放它持有的资源（运动卡、相机等）
                _viewModel.Dispose();
            }
        }

        /// <summary>
        /// 配方列表选中项变更事件，通知ViewModel切换当前配方。
        /// <para>sender是ListBox，e里有选中的项。这里只调ViewModel的方法，不直接操作数据。</para>
        /// </summary>
        private void RecipeListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _viewModel?.OnRecipeSelected();
        }

        /// <summary>
        /// Halcon容器尺寸变更事件，保持1.5:1宽高比自适应，并重绘WPF坐标轴叠加层。
        /// <para>1.5:1是工业相机的常见宽高比（比如2048x1536），保持这个比例图像才不会变形。</para>
        /// <para>计算逻辑：先按高度算宽度（targetW = availH * 1.5），如果宽度超了就反过来按宽度算高度。</para>
        /// </summary>
        private void HalconContainer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (HalconWindow == null) return;
            double availW = HalconContainer.ActualWidth;
            double availH = HalconContainer.ActualHeight;
            // 按1.5:1宽高比计算目标尺寸
            double targetW = availH * 1.5;
            double targetH = availW / 1.5;

            // 选能放得下的那个方案
            if (targetW <= availW)
            {
                HalconWindow.Width = targetW;
                HalconWindow.Height = availH;
            }
            else
            {
                HalconWindow.Width = availW;
                HalconWindow.Height = targetH;
            }

            // 延迟重绘WPF坐标轴，等待布局更新完成
            // 不延迟的话，HalconWindow的新尺寸还没生效，算出来的坐标轴位置会不对
            Dispatcher.BeginInvoke(new Action(() => DrawWpfAxes()), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // ========== 小数输入验证 ==========
        // 这三个方法解决的是同一个问题：WPF TextBox绑定double属性时，输入"3."、"0."等中间状态会报错。
        // 因为WPF默认的验证逻辑用double.TryParse，"3."解析失败就直接拒绝输入了。
        // 但用户打字时"3."是必经的中间状态（先打3再打.再打5），不能不让输。
        // 解决方案：在PreviewTextInput阶段自己验证，允许以小数点结尾的中间状态。

        /// <summary>
        /// 小数输入验证——TextBox的PreviewTextInput事件处理。
        /// <para>【踩坑】WPF TextBox绑定double时，"3."等中间状态无法解析，需要特殊处理。</para>
        /// <para>原理：在用户还没输入之前（Preview阶段），把当前文本+即将输入的字符拼起来，
        /// 判断拼完后是不是合法的double格式。如果是就放行，不是就拦截。</para>
        /// <para>特殊处理：允许以小数点结尾的中间状态（如"3."、"10."、"-5."、"0."），
        /// 因为这些是输入小数时的必经状态，最终会变成"3.5"等合法格式。</para>
        /// <para>也允许"."、"-."、"-0."这种看起来不完整但用户正在输入的状态。</para>
        /// </summary>
        /// <param name="sender">触发事件的TextBox</param>
        /// <param name="e">WPF自动传入的文本输入事件参数，e.Text是即将输入的字符，e.Handled=true则拦截</param>
        private void DecimalPreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (sender is System.Windows.Controls.TextBox tb)
            {
                // 拼出输入后的完整文本：选中部分之前 + 新输入字符 + 选中部分之后
                // tb.SelectionStart是光标位置，tb.SelectionLength是选中了几个字符（覆盖输入）
                string newText = tb.Text.Substring(0, tb.SelectionStart) + e.Text + tb.Text.Substring(tb.SelectionStart + tb.SelectionLength);
                if (string.IsNullOrEmpty(newText)) return;

                // 允许以小数点结尾的中间输入状态（如"3."、"10."、"-5."、"0."等）
                // 去掉末尾的小数点，剩下的部分如果能解析为double，说明这是合法的中间状态
                if (newText.EndsWith(".") && newText.Length > 1)
                {
                    string withoutDot = newText.Substring(0, newText.Length - 1);
                    if (double.TryParse(withoutDot, out _))
                    {
                        e.Handled = false;  // 放行
                        return;
                    }
                }

                // 允许单独的"."或"-."作为输入起始
                // 用户刚打了"."，后面还要打数字，不能拦住
                if (newText == "." || newText == "-." || newText == "-0.")
                {
                    e.Handled = false;  // 放行
                    return;
                }

                // 其他情况：尝试解析为double，能解析就放行，不能就拦截
                e.Handled = !double.TryParse(newText, out _);
            }
        }

        /// <summary>
        /// 递归遍历可视化树中所有TextBox，为它们绑定小数输入验证。
        /// <para>在MainWindow_Loaded里调用，传入this（整个窗口）作为根节点。</para>
        /// <para>用VisualTreeHelper遍历，因为XAML里的控件是嵌套的，需要递归才能找到所有TextBox。</para>
        /// <para>给每个TextBox绑定两个事件：</para>
        /// <para>1. PreviewTextInput → DecimalPreviewTextInput（验证键盘输入）</para>
        /// <para>2. Pasting → OnTextBoxPasting（验证粘贴内容）</para>
        /// </summary>
        /// <param name="parent">要遍历的根节点，通常是this（整个窗口）</param>
        private void BindDecimalValidationToAllTextBoxes(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is System.Windows.Controls.TextBox tb)
                {
                    // 为所有TextBox绑定小数输入验证
                    tb.PreviewTextInput += DecimalPreviewTextInput;
                    // 禁止粘贴非数字内容（比如从网页复制一段文字粘贴进来）
                    // DataObject.AddPastingHandler是WPF官方提供的粘贴事件挂载方式
                    DataObject.AddPastingHandler(tb, OnTextBoxPasting);
                }
                // 递归遍历子元素（比如Grid里的StackPanel里的TextBox）
                BindDecimalValidationToAllTextBoxes(child);
            }
        }

        /// <summary>
        /// TextBox粘贴事件处理，验证粘贴内容是否为合法数字格式。
        /// <para>用户Ctrl+V粘贴时触发，如果粘贴的是"abc"这种非数字内容，直接取消粘贴操作。</para>
        /// </summary>
        /// <param name="sender">触发事件的TextBox</param>
        /// <param name="e">WPF粘贴事件参数，调用e.CancelCommand()可以取消粘贴</param>
        private void OnTextBoxPasting(object sender, DataObjectPastingEventArgs e)
        {
            if (e.DataObject.GetDataPresent(typeof(string)))
            {
                // 从剪贴板拿到要粘贴的文本
                string text = (string)e.DataObject.GetData(typeof(string));
                // 尝试解析为double，失败就取消粘贴
                if (!double.TryParse(text, out _))
                {
                    e.CancelCommand();  // 取消粘贴操作
                }
            }
            else
            {
                // 剪贴板里不是文本（比如图片），直接取消
                e.CancelCommand();
            }
        }

        #region 视觉模块事件处理

        /// <summary>
        /// ViewModel属性变更回调，当CurrentImage变化时将图像显示到Halcon窗口并绘制检测结果。
        /// <para>这是PropertyChanged事件的订阅者，ViewModel里只要一set CurrentImage就会触发。</para>
        /// <para>流程：ViewModel更新图像 → 触发PropertyChanged → 这里把图像丢给Halcon窗口显示 → 再画检测结果（Mark点十字、圆圈等）</para>
        /// </summary>
        private void OnPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // 只关心CurrentImage属性的变化
            if (e.PropertyName == nameof(MainViewModel.CurrentImage) && sender is MainViewModel vm)
            {
                if (vm.CurrentImage != null && vm.CurrentImage.IsInitialized())
                {
                    try
                    {
                        // HOperatorSet.DispObj是Halcon官方的图像显示方法
                        // 第一个参数是HImage对象，第二个是Halcon窗口句柄
                        HOperatorSet.DispObj(vm.CurrentImage, HalconWindow.HalconWindow);
                        // 显示完图像后，在图像上叠加检测结果（Mark1、Mark2的十字和圆圈）
                        DrawDetectionResult(vm);
                    }
                    catch { }  // Halcon偶尔会抛异常（比如窗口还没准备好），忽略就好
                }
            }
        }

        /// <summary>
        /// 在Halcon窗口上绘制检测结果：Mark1（绿色十字+圆+坐标标签）、Mark2（品红色）、连线。
        /// <para>每次新图像到来后调用，在图像上叠加检测结果。</para>
        /// <para>Mark1用绿色，Mark2用品红色（magenta），两个Mark之间画黄色连线。</para>
        /// <para>每个Mark显示：十字线（GenCrossContourXld）、圆圈（DispCircle）、像素坐标标签、物理坐标标签。</para>
        /// </summary>
        /// <param name="vm">ViewModel实例，从中获取Mark点的像素坐标和物理坐标</param>
        private void DrawDetectionResult(MainViewModel vm)
        {
            // 安全检查：Halcon窗口必须已初始化，且检测正在运行
            if (HalconWindow.HalconWindow == null || !HalconWindow.HalconWindow.IsInitialized()) return;
            if (!vm.DetectionRunning) return;

            try
            {
                var hw = HalconWindow.HalconWindow;

                // ===== 画Mark1（绿色） =====
                // 只有Mark1坐标不为0（即已经检测到）才画
                if (vm.Mark1PixelRow != 0 || vm.Mark1PixelCol != 0)
                {
                    // GenCrossContourXld：Halcon官方的画十字线方法
                    // 参数：输出轮廓、行坐标、列坐标、十字长度、角度
                    HOperatorSet.GenCrossContourXld(out HObject cross1, vm.Mark1PixelRow, vm.Mark1PixelCol, 30, 0);
                    hw.SetColor("green");
                    hw.DispObj(cross1);
                    cross1.Dispose();  // Halcon的HObject用完必须Dispose，否则内存泄漏

                    // 画圆圈，半径12像素
                    HOperatorSet.SetColor(hw, "green");
                    HOperatorSet.SetLineWidth(hw, 2);
                    double r1 = vm.Mark1PixelRow, c1 = vm.Mark1PixelCol;
                    HOperatorSet.DispCircle(hw, r1, c1, 12);

                    // 画像素坐标标签（用Halcon的SetFont + WriteString，这是Halcon官方的文字显示方式）
                    // "-Consolas-12-*-*-*-*-1-" 是Halcon的字体格式字符串，固定搭配
                    HOperatorSet.SetFont(hw, "-Consolas-12-*-*-*-*-1-");
                    hw.SetTposition((int)r1 - 40, (int)c1);
                    hw.WriteString($"M1({r1:F0},{c1:F0})");
                    // 画物理坐标标签（用DispText，比WriteString更灵活，可以指定位置和颜色）
                    HOperatorSet.DispText(hw, $"M1({vm.Mark1PhysX:F1},{vm.Mark1PhysY:F1})", "window", r1 - 25, c1 + 15, "green", "false", "false");
                }

                // ===== 画Mark2（品红色） =====
                // 和Mark1一样的画法，只是颜色换成magenta
                if (vm.Mark2PixelRow != 0 || vm.Mark2PixelCol != 0)
                {
                    HOperatorSet.GenCrossContourXld(out HObject cross2, vm.Mark2PixelRow, vm.Mark2PixelCol, 30, 0);
                    hw.SetColor("magenta");
                    hw.DispObj(cross2);
                    cross2.Dispose();

                    HOperatorSet.SetColor(hw, "magenta");
                    HOperatorSet.SetLineWidth(hw, 2);
                    double r2 = vm.Mark2PixelRow, c2 = vm.Mark2PixelCol;
                    HOperatorSet.DispCircle(hw, r2, c2, 12);

                    HOperatorSet.SetFont(hw, "-Consolas-12-*-*-*-*-1-");
                    hw.SetTposition((int)r2 - 40, (int)c2);
                    hw.WriteString($"M2({r2:F0},{c2:F0})");
                    HOperatorSet.DispText(hw, $"M2({vm.Mark2PhysX:F1},{vm.Mark2PhysY:F1})", "window", r2 + 15, c2 + 15, "magenta", "false", "false");
                }

                // ===== 画两个Mark之间的连线（黄色） =====
                if ((vm.Mark1PixelRow != 0 || vm.Mark1PixelCol != 0) &&
                    (vm.Mark2PixelRow != 0 || vm.Mark2PixelCol != 0))
                {
                    HOperatorSet.SetColor(hw, "yellow");
                    HOperatorSet.SetLineWidth(hw, 1);
                    // DispLine是Halcon官方的画线方法，参数：窗口句柄、起点行、起点列、终点行、终点列
                    HOperatorSet.DispLine(hw, vm.Mark1PixelRow, vm.Mark1PixelCol, vm.Mark2PixelRow, vm.Mark2PixelCol);
                }
            }
            catch { }  // Halcon绘制偶尔失败（比如窗口被关闭），忽略
        }

        /// <summary>
        /// 首帧图像接收事件处理，设置Halcon窗口坐标范围并记录图像尺寸。
        /// <para>第一帧图像到来时触发（由ViewModel的FirstImageReceived事件发出）。</para>
        /// <para>必须调SetPart设置窗口坐标范围，否则Halcon默认的坐标范围不对，图像会显示不全或变形。</para>
        /// <para>SetPart(窗口, row1, col1, row2, col2)是Halcon官方方法，设置显示区域的像素坐标范围。</para>
        /// </summary>
        private void OnFirstImageReceived(object sender, ImageSizeEventArgs ev)
        {
            // 用Dispatcher.Invoke确保在UI线程执行（事件可能从后台线程触发）
            Dispatcher.Invoke(() =>
            {
                try
                {
                    if (HalconWindow.HalconWindow != null && HalconWindow.HalconWindow.IsInitialized())
                    {
                        // 设置Halcon窗口显示范围：从(0,0)到(图像高度-1, 图像宽度-1)
                        // 这样图像的每个像素都刚好对应窗口上的一个点
                        HOperatorSet.SetPart(HalconWindow.HalconWindow, 0, 0, ev.Height - 1, ev.Width - 1);
                        // 记录图像尺寸，后面DrawWpfAxes要用
                        _imageWidth = ev.Width;
                        _imageHeight = ev.Height;
                        // 图像尺寸知道了，可以画WPF坐标轴叠加层了
                        DrawWpfAxes();
                    }
                }
                catch { }
            });
        }

        /// <summary>
        /// Mark1区域绘制请求回调，在Halcon窗口上交互式绘制矩形ROI。
        /// <para>流程：用户点"画Mark1"按钮 → ViewModel触发RequestDrawMark1事件 → 这里响应 →
        /// 调Halcon的DrawRectangle1让用户在图像上拖拽画矩形 → 把矩形坐标传回ViewModel。</para>
        /// <para>DrawRectangle1是Halcon官方的交互式绘制方法，会阻塞直到用户画完（鼠标松开）。</para>
        /// </summary>
        private void OnRequestDrawMark1(object sender, EventArgs e)
        {
            if (HalconWindow.HalconWindow == null || !HalconWindow.HalconWindow.IsInitialized())
            {
                MessageBox.Show("Halcon窗口未初始化");
                return;
            }

            // 让Halcon窗口获得焦点，否则DrawRectangle1的鼠标交互可能不响应
            HalconWindow.Focus();
            HalconWindow.UpdateLayout();
            // 强制等一帧渲染完成，确保窗口状态更新
            Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

            try
            {
                // DrawRectangle1：Halcon官方的交互式矩形绘制方法
                // 会弹出十字光标让用户拖拽，返回矩形的左上角(row1,col1)和右下角(row2,col2)
                HOperatorSet.DrawRectangle1(HalconWindow.HalconWindow,
                    out HTuple row1, out HTuple col1, out HTuple row2, out HTuple col2);

                // 把矩形坐标传回ViewModel
                if (DataContext is MainViewModel vm)
                {
                    vm.SetMark1Region(row1.D, col1.D, row2.D, col2.D);
                }
            }
            catch (HalconException ex)
            {
                // 用户按ESC取消绘制也会抛异常，这里统一捕获
                MessageBox.Show($"绘制Mark1失败: {ex.Message}");
            }
        }

        /// <summary>
        /// Mark2区域绘制请求回调，和OnRequestDrawMark1逻辑完全一样，只是调ViewModel的SetMark2Region。
        /// <para>流程同Mark1：用户点"画Mark2"按钮 → ViewModel触发事件 → 这里响应 →
        /// Halcon交互画矩形 → 坐标传回ViewModel。</para>
        /// </summary>
        private void OnRequestDrawMark2(object sender, EventArgs e)
        {
            if (HalconWindow.HalconWindow == null || !HalconWindow.HalconWindow.IsInitialized())
            {
                MessageBox.Show("Halcon窗口未初始化");
                return;
            }

            HalconWindow.Focus();
            HalconWindow.UpdateLayout();
            Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

            try
            {
                HOperatorSet.DrawRectangle1(HalconWindow.HalconWindow,
                    out HTuple row1, out HTuple col1, out HTuple row2, out HTuple col2);

                if (DataContext is MainViewModel vm)
                {
                    vm.SetMark2Region(row1.D, col1.D, row2.D, col2.D);
                }
            }
            catch (HalconException ex)
            {
                MessageBox.Show($"绘制Mark2失败: {ex.Message}");
            }
        }

        #endregion

        /// <summary>
        /// 轨迹更新回调，触发轨迹重绘和数据面板刷新。
        /// <para>由ViewModel的TrajectoryUpdated事件触发，运动过程中频繁调用。</para>
        /// <para>如果重绘定时器已经在跑（说明正在运动中），就不重复调DrawTrajectory了，定时器会自动刷新；
        /// 否则手动调一次DrawTrajectory画最新的轨迹。</para>
        /// </summary>
        private void OnTrajectoryUpdated()
        {
            if (!_redrawTimer.IsEnabled)
                DrawTrajectory();
            // 不管定时器跑没跑，数据面板都要更新（显示当前坐标、偏差等）
            UpdateDataPanel();
        }

        /// <summary>
        /// 更新数据面板，显示当前轴位置、Mark点物理坐标、偏差量和出胶状态。
        /// <para>数据面板是Canvas右上角的几个TextBlock，实时显示关键数据。</para>
        /// <para>数据来源全部是ViewModel的属性，这里只负责把数据格式化后显示到TextBlock上。</para>
        /// </summary>
        private void UpdateDataPanel()
        {
            if (_viewModel == null) return;
            // X/Y轴当前位置
            DataPanelX.Text = $"X: {_viewModel.AxisXPos:F2} mm";
            DataPanelY.Text = $"Y: {_viewModel.AxisYPos:F2} mm";
            // Mark1物理坐标（没检测到时显示"--"）
            DataPanelM1.Text = _viewModel.Mark1PhysX != 0 || _viewModel.Mark1PhysY != 0
                ? $"M1: ({_viewModel.Mark1PhysX:F2}, {_viewModel.Mark1PhysY:F2})"
                : "M1: --";
            // Mark2物理坐标
            DataPanelM2.Text = _viewModel.Mark2PhysX != 0 || _viewModel.Mark2PhysY != 0
                ? $"M2: ({_viewModel.Mark2PhysX:F2}, {_viewModel.Mark2PhysY:F2})"
                : "M2: --";
            // X/Y方向偏差量（Mark1相对于原点的偏移）
            DataPanelDX.Text = $"ΔX: {_viewModel.DeltaX:F3} mm";
            DataPanelDY.Text = $"ΔY: {_viewModel.DeltaY:F3} mm";
            // 出胶状态，开的时候显示红色，关的时候显示灰色
            DataPanelGlue.Text = _viewModel.GlueOutputOn ? "出胶: 开" : "出胶: 关";
            DataPanelGlue.Foreground = _viewModel.GlueOutputOn
                ? new SolidColorBrush(Color.FromRgb(229, 57, 53))   // 红色，出胶中
                : new SolidColorBrush(Color.FromRgb(100, 100, 100)); // 灰色，未出胶
        }

        #region Jog按钮事件

        /// <summary>
        /// X轴正方向点动按钮按下，启动X轴正向Jog运动。
        /// <para>JogMove(1, 1)：第一个参数1表示X轴（轴号），第二个参数1表示正方向。</para>
        /// </summary>
        private void JogXPos_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _viewModel?.JogMove(1, 1);
        }

        /// <summary>
        /// X轴负方向点动按钮按下，启动X轴负向Jog运动。
        /// <para>JogMove(1, -1)：轴号1=X轴，方向-1=负方向。</para>
        /// </summary>
        private void JogXNeg_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _viewModel?.JogMove(1, -1);
        }

        /// <summary>
        /// Y轴正方向点动按钮按下，启动Y轴正向Jog运动。
        /// <para>JogMove(2, 1)：轴号2=Y轴，方向1=正方向。</para>
        /// </summary>
        private void JogYPos_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _viewModel?.JogMove(2, 1);
        }

        /// <summary>
        /// Y轴负方向点动按钮按下，启动Y轴负向Jog运动。
        /// <para>JogMove(2, -1)：轴号2=Y轴，方向-1=负方向。</para>
        /// </summary>
        private void JogYNeg_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _viewModel?.JogMove(2, -1);
        }

        /// <summary>
        /// Jog按钮松开事件，根据按钮内容判断轴号并停止对应轴的Jog运动。
        /// <para>四个Jog按钮共用这一个MouseUp事件处理，通过按钮Content（"X+"、"X-"、"Y+"、"Y-"）判断是哪个轴。</para>
        /// <para>用PreviewMouseUp而不是MouseUp，是因为Preview是隧道事件，优先级更高，确保不会漏掉。</para>
        /// </summary>
        private void Jog_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is Button btn)
            {
                // 根据按钮文字判断轴号，这是我们自己定义的约定
                short axis = btn.Content switch
                {
                    "X+" or "X-" => 1,  // X轴
                    "Y+" or "Y-" => 2,  // Y轴
                    _ => 0               // 未知，不处理
                };
                if (axis > 0) _viewModel?.StopJog(axis);
            }
        }

        /// <summary>
        /// 回零按钮按下事件，执行轴回原点操作。
        /// <para>回零就是让运动轴回到机械原点，建立坐标系。</para>
        /// </summary>
        private void JogHome_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _viewModel?.Home();
        }

        #endregion

        /// <summary>
        /// 绘制Halcon坐标系（预留方法，当前未实现）。
        /// <para>以后可能用来在Halcon图像上画坐标轴和刻度，目前是空的。</para>
        /// </summary>
        private void DrawHalconCoordinates()
        {
        }

        /// <summary>
        /// 在Halcon窗口上叠加WPF坐标轴，显示行列刻度和标签。
        /// <para>和DrawCoordinateAxes不同，这个是画在Halcon图像上方的WPF叠加层（AxisOverlay Canvas），
        /// 不是画在轨迹Canvas上的。显示的是图像的行列坐标（像素），不是物理坐标（mm）。</para>
        /// <para>只在首帧图像到来后和Halcon容器尺寸变化时调用。</para>
        /// </summary>
        private void DrawWpfAxes()
        {
            // 图像尺寸还没拿到，没法画
            if (_imageWidth <= 0 || _imageHeight <= 0) return;
            var overlay = AxisOverlay;
            if (overlay == null) return;
            overlay.Children.Clear();

            // HalconWindow在容器里居中显示，要算出偏移量
            double hwW = HalconWindow?.ActualWidth ?? 0;
            double hwH = HalconWindow?.ActualHeight ?? 0;
            if (hwW <= 0 || hwH <= 0) return;

            double containerW = HalconContainer.ActualWidth;
            double containerH = HalconContainer.ActualHeight;
            // HalconWindow居中，所以偏移量 = (容器尺寸 - 窗口尺寸) / 2
            double offsetX = (containerW - hwW) / 2.0;
            double offsetY = (containerH - hwH) / 2.0;

            // 刻度间距：大图用200像素一格，小图用100像素一格
            int cStep = _imageWidth > 2000 ? 200 : 100;
            int rStep = _imageHeight > 1500 ? 200 : 100;

            // 坐标轴颜色用绿色系，和Halcon图像的灰度背景区分开
            var axisBrush = new SolidColorBrush(Color.FromRgb(0, 180, 0));
            var tickBrush = new SolidColorBrush(Color.FromRgb(0, 150, 0));
            var textBrush = new SolidColorBrush(Color.FromRgb(0, 120, 0));

            // 画顶部横轴（列方向）
            var topAxis = new Line
            {
                X1 = offsetX, Y1 = offsetY, X2 = offsetX + hwW, Y2 = offsetY,
                Stroke = axisBrush, StrokeThickness = 1.5
            };
            overlay.Children.Add(topAxis);

            // 画左侧纵轴（行方向）
            var leftAxis = new Line
            {
                X1 = offsetX, Y1 = offsetY, X2 = offsetX, Y2 = offsetY + hwH,
                Stroke = axisBrush, StrokeThickness = 1.5
            };
            overlay.Children.Add(leftAxis);

            // 画列方向刻度和标签
            for (int c = 0; c <= _imageWidth; c += cStep)
            {
                // 把像素坐标映射到Canvas坐标
                double x = offsetX + (c / (double)_imageWidth) * hwW;

                // 刻度线（顶部横轴上的小竖线）
                var tick = new Line
                {
                    X1 = x, Y1 = offsetY - 6, X2 = x, Y2 = offsetY,
                    Stroke = tickBrush, StrokeThickness = 1
                };
                overlay.Children.Add(tick);

                // 刻度标签（列号）
                var label = new TextBlock
                {
                    Text = c.ToString(), FontSize = 8, Foreground = textBrush
                };
                Canvas.SetLeft(label, x - 10);
                Canvas.SetTop(label, offsetY - 16);
                overlay.Children.Add(label);
            }

            // 画行方向刻度和标签
            for (int r = 0; r <= _imageHeight; r += rStep)
            {
                double y = offsetY + (r / (double)_imageHeight) * hwH;

                var tick = new Line
                {
                    X1 = offsetX - 6, Y1 = y, X2 = offsetX, Y2 = y,
                    Stroke = tickBrush, StrokeThickness = 1
                };
                overlay.Children.Add(tick);

                var label = new TextBlock
                {
                    Text = r.ToString(), FontSize = 8, Foreground = textBrush
                };
                Canvas.SetLeft(label, offsetX - 30);
                Canvas.SetTop(label, y - 5);
                overlay.Children.Add(label);
            }

            // 列方向标签 "C→"
            var colLabel = new TextBlock
            {
                Text = "C→", FontSize = 10, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0, 100, 0))
            };
            Canvas.SetLeft(colLabel, offsetX + hwW - 20);
            Canvas.SetTop(colLabel, offsetY - 16);
            overlay.Children.Add(colLabel);

            // 行方向标签 "R↓"
            var rowLabel = new TextBlock
            {
                Text = "R↓", FontSize = 10, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0, 100, 0))
            };
            Canvas.SetLeft(rowLabel, offsetX - 30);
            Canvas.SetTop(rowLabel, offsetY + hwH - 5);
            overlay.Children.Add(rowLabel);
        }

        #region 轨迹绘制

        // ========== 轨迹绘制是整个Code-Behind最核心的部分 ==========
        // 所有绘制都在Canvas上完成，每次重绘都是"清空→全部重画"的方式。
        // 虽然效率不是最优，但Canvas上元素不多（几百条线+几十个文字），性能完全够用。
        //
        // 绘制顺序（后画的覆盖先画的，所以重要的东西放后面画）：
        // 1. 刻度标记（最底层）
        // 2. 坐标轴和网格线
        // 3. 限位区域遮罩
        // 4. 配方预览轨迹（虚线）
        // 5. 运动轨迹历史（实线/虚线）
        // 6. Mark点和偏差线
        // 7. 当前位置光标（最顶层）

        /// <summary>
        /// 主轨迹绘制入口，Canvas上所有东西都从这里画。
        /// <para>绘制顺序：清空Canvas → 画刻度 → 画坐标轴 → 画限位区域 → 画配方预览 → 画运动轨迹 → 画Mark点 → 画当前位置。</para>
        /// <para>坐标系说明：Canvas中心(cx,cy)对应物理坐标原点(0,0)，
        /// X正方向向右，Y正方向向上（注意Canvas的Y轴是向下的，所以画Y时要取反：cy - y*scale）。</para>
        /// <para>scale是像素/mm的比例，由Canvas尺寸和显示范围决定，当前固定显示±60mm范围。</para>
        /// </summary>
        private void DrawTrajectory()
        {
            if (TrajectoryCanvas == null) return;

            double w = TrajectoryCanvas.ActualWidth;
            double h = TrajectoryCanvas.ActualHeight;
            // Canvas太小就不画了（比如窗口最小化时尺寸为0）
            if (w < 10 || h < 10) return;

            // Canvas中心就是物理坐标原点
            double cx = w / 2;
            double cy = h / 2;
            // 缩放比例：120mm对应Canvas短边，这样±60mm的范围刚好填满
            double scale = Math.Min(w, h) / 120.0;

            // 每次重绘都清空Canvas，然后全部重新画
            TrajectoryCanvas.Children.Clear();

            // 按层次从底到顶依次绘制
            DrawTickMarks(cx, cy, w, h, scale);       // 1. 刻度标记
            DrawCoordinateAxes(cx, cy, w, h);          // 2. 坐标轴和网格
            DrawLimitBlocks(cx, cy, w, h, scale);      // 3. 限位区域
            if (!_viewModel.IsExecutingRecipe)
                DrawRecipePreview(cx, cy, scale);      // 4. 配方预览（执行配方时不画预览，避免和实时轨迹重叠）
            DrawMotionTrail(cx, cy, scale);             // 5. 运动轨迹
            DrawWorkpieceMarkers(cx, cy, scale);        // 6. Mark点
            DrawCurrentPosition(cx, cy, scale);         // 7. 当前位置

            // 根据运动状态控制重绘定时器
            // 运动中或执行配方时启动定时器持续刷新，静止时停掉定时器省CPU
            if (_viewModel.IsCardConnected)
            {
                if (_viewModel.IsMoving || _viewModel.IsExecutingRecipe)
                {
                    if (!_redrawTimer.IsEnabled) _redrawTimer.Start();
                }
                else
                {
                    _redrawTimer.Stop();
                }
            }

            // 每次绘制完都更新数据面板
            UpdateDataPanel();
        }

        /// <summary>
        /// 绘制刻度标记，沿四个方向每隔5mm绘制刻度线和数值标签。
        /// <para>刻度线是坐标轴上的小短线，标签是mm数值。</para>
        /// <para>正方向标正数，负方向标负数。</para>
        /// </summary>
        private void DrawTickMarks(double cx, double cy, double w, double h, double scale)
        {
            double tickInterval = 5;  // 每5mm一个刻度
            double tickLen = 4;       // 刻度线长度（像素）

            // X轴正方向刻度
            for (double v = tickInterval; cx + v * scale < w - 10; v += tickInterval)
            {
                double px = cx + v * scale;
                // 刻度线（垂直于X轴的小短线）
                var tick = new Line
                {
                    X1 = px, Y1 = cy - tickLen, X2 = px, Y2 = cy + tickLen,
                    Stroke = TickBrush, StrokeThickness = 0.5
                };
                TrajectoryCanvas.Children.Add(tick);

                // 刻度数值标签
                var label = new TextBlock
                {
                    Text = $"{v:F0}", Foreground = TickBrush, FontSize = 9,
                    RenderTransform = new TranslateTransform(0, 8)  // 稍微往下偏移，不压着刻度线
                };
                Canvas.SetLeft(label, px - 8); Canvas.SetTop(label, cy + 4);
                TrajectoryCanvas.Children.Add(label);
            }

            // X轴负方向刻度（标签带负号）
            for (double v = tickInterval; cx - v * scale > 10; v += tickInterval)
            {
                double px = cx - v * scale;
                var tick = new Line
                {
                    X1 = px, Y1 = cy - tickLen, X2 = px, Y2 = cy + tickLen,
                    Stroke = TickBrush, StrokeThickness = 0.5
                };
                TrajectoryCanvas.Children.Add(tick);

                var label = new TextBlock
                {
                    Text = $"-{v:F0}", Foreground = TickBrush, FontSize = 9,
                    RenderTransform = new TranslateTransform(0, 8)
                };
                Canvas.SetLeft(label, px - 10); Canvas.SetTop(label, cy + 4);
                TrajectoryCanvas.Children.Add(label);
            }

            // Y轴正方向刻度（Canvas的Y轴向下，但物理Y轴向上，所以py = cy - v*scale）
            for (double v = tickInterval; cy - v * scale > 10; v += tickInterval)
            {
                double py = cy - v * scale;
                var tick = new Line
                {
                    X1 = cx - tickLen, Y1 = py, X2 = cx + tickLen, Y2 = py,
                    Stroke = TickBrush, StrokeThickness = 0.5
                };
                TrajectoryCanvas.Children.Add(tick);

                var label = new TextBlock
                {
                    Text = $"{v:F0}", Foreground = TickBrush, FontSize = 9
                };
                Canvas.SetLeft(label, cx + 6); Canvas.SetTop(label, py - 6);
                TrajectoryCanvas.Children.Add(label);
            }

            // Y轴负方向刻度
            for (double v = tickInterval; cy + v * scale < h - 10; v += tickInterval)
            {
                double py = cy + v * scale;
                var tick = new Line
                {
                    X1 = cx - tickLen, Y1 = py, X2 = cx + tickLen, Y2 = py,
                    Stroke = TickBrush, StrokeThickness = 0.5
                };
                TrajectoryCanvas.Children.Add(tick);

                var label = new TextBlock
                {
                    Text = $"-{v:F0}", Foreground = TickBrush, FontSize = 9
                };
                Canvas.SetLeft(label, cx + 6); Canvas.SetTop(label, py - 6);
                TrajectoryCanvas.Children.Add(label);
            }
        }

        /// <summary>
        /// 绘制坐标系：网格线（淡蓝色）+ X/Y坐标轴（浅灰色粗线）+ 轴标签。
        /// <para>网格线每隔10mm一条，用很淡的颜色，只是给用户一个空间参考感。</para>
        /// <para>坐标轴用稍粗的线，比网格线明显一些。</para>
        /// <para>轴标签显示"X(mm)"和"Y(mm)"，标在轴的末端。</para>
        /// </summary>
        private void DrawCoordinateAxes(double cx, double cy, double w, double h)
        {
            // 网格线：每隔10mm绘制一条淡蓝色竖线/横线
            var gridBrush = new SolidColorBrush(Color.FromRgb(220, 225, 240));
            double tickInterval = 10;
            double scale = Math.Min(w, h) / 120.0;

            // X方向网格线（竖线）
            for (double v = tickInterval; cx + v * scale < w - 10; v += tickInterval)
            {
                double px = cx + v * scale;
                var gl = new Line { X1 = px, Y1 = 10, X2 = px, Y2 = h - 10, Stroke = gridBrush, StrokeThickness = 0.3 };
                TrajectoryCanvas.Children.Add(gl);
            }
            for (double v = tickInterval; cx - v * scale > 10; v += tickInterval)
            {
                double px = cx - v * scale;
                var gl = new Line { X1 = px, Y1 = 10, X2 = px, Y2 = h - 10, Stroke = gridBrush, StrokeThickness = 0.3 };
                TrajectoryCanvas.Children.Add(gl);
            }
            // Y方向网格线（横线）
            for (double v = tickInterval; cy - v * scale > 10; v += tickInterval)
            {
                double py = cy - v * scale;
                var gl = new Line { X1 = 10, Y1 = py, X2 = w - 10, Y2 = py, Stroke = gridBrush, StrokeThickness = 0.3 };
                TrajectoryCanvas.Children.Add(gl);
            }
            for (double v = tickInterval; cy + v * scale < h - 10; v += tickInterval)
            {
                double py = cy + v * scale;
                var gl = new Line { X1 = 10, Y1 = py, X2 = w - 10, Y2 = py, Stroke = gridBrush, StrokeThickness = 0.3 };
                TrajectoryCanvas.Children.Add(gl);
            }

            // X轴（水平粗线）
            var xAxis = new Line
            {
                X1 = 10, Y1 = cy, X2 = w - 10, Y2 = cy,
                Stroke = AxisBrush, StrokeThickness = 0.8
            };
            TrajectoryCanvas.Children.Add(xAxis);

            // Y轴（垂直粗线）
            var yAxis = new Line
            {
                X1 = cx, Y1 = 10, X2 = cx, Y2 = h - 10,
                Stroke = AxisBrush, StrokeThickness = 0.8
            };
            TrajectoryCanvas.Children.Add(yAxis);

            // X轴标签，放在右端
            var xLabel = new TextBlock { Text = "X(mm)", Foreground = AxisBrush, FontSize = 9 };
            Canvas.SetLeft(xLabel, w - 40); Canvas.SetTop(xLabel, cy - 14);
            TrajectoryCanvas.Children.Add(xLabel);

            // Y轴标签，放在顶端
            var yLabel = new TextBlock { Text = "Y(mm)", Foreground = AxisBrush, FontSize = 9 };
            Canvas.SetLeft(yLabel, cx + 4); Canvas.SetTop(yLabel, 4);
            TrajectoryCanvas.Children.Add(yLabel);
        }

        /// <summary>
        /// 绘制限位区域，在±50mm范围外显示半透明红色遮罩和虚线边框。
        /// <para>限位区域就是运动轴能走的安全范围，超出这个范围可能会撞机。</para>
        /// <para>画法：在安全范围外画半透明红色矩形遮罩，边界画红色虚线，再标注限位数值。</para>
        /// <para>目前限位值硬编码为±50mm，后续可以改成从配置读取。</para>
        /// </summary>
        private void DrawLimitBlocks(double cx, double cy, double w, double h, double scale)
        {
            if (_viewModel == null) return;
            // 限位范围，目前硬编码±50mm
            double limitX = 50;
            double limitY = 50;
            if (limitX <= 0 || limitY <= 0) return;

            // 计算限位边界在Canvas上的像素位置
            double leftLimitPx = cx - limitX * scale;
            double rightLimitPx = cx + limitX * scale;
            double topLimitPx = cy - limitY * scale;
            double bottomLimitPx = cy + limitY * scale;

            // 左侧超限区域（半透明红色遮罩）
            if (leftLimitPx > 0)
            {
                var rect = new Rectangle
                {
                    Width = leftLimitPx, Height = h,
                    Fill = LimitBrush, Stroke = LimitBorderBrush, StrokeThickness = 1
                };
                Canvas.SetLeft(rect, 0); Canvas.SetTop(rect, 0);
                TrajectoryCanvas.Children.Add(rect);
            }

            // 右侧超限区域
            if (rightLimitPx < w)
            {
                var rect = new Rectangle
                {
                    Width = w - rightLimitPx, Height = h,
                    Fill = LimitBrush, Stroke = LimitBorderBrush, StrokeThickness = 1
                };
                Canvas.SetLeft(rect, rightLimitPx); Canvas.SetTop(rect, 0);
                TrajectoryCanvas.Children.Add(rect);
            }

            // 上方超限区域（只画限位范围内的宽度，左右已经被上面的矩形覆盖了）
            if (topLimitPx > 0)
            {
                var rect = new Rectangle
                {
                    Width = rightLimitPx - leftLimitPx, Height = topLimitPx,
                    Fill = LimitBrush, Stroke = LimitBorderBrush, StrokeThickness = 1
                };
                Canvas.SetLeft(rect, Math.Max(0, leftLimitPx)); Canvas.SetTop(rect, 0);
                TrajectoryCanvas.Children.Add(rect);
            }

            // 下方超限区域
            if (bottomLimitPx < h)
            {
                var rect = new Rectangle
                {
                    Width = rightLimitPx - leftLimitPx, Height = h - bottomLimitPx,
                    Fill = LimitBrush, Stroke = LimitBorderBrush, StrokeThickness = 1
                };
                Canvas.SetLeft(rect, Math.Max(0, leftLimitPx)); Canvas.SetTop(rect, bottomLimitPx);
                TrajectoryCanvas.Children.Add(rect);
            }

            // 四条限位边界虚线
            // StrokeDashArray = {6, 3} 表示6像素实线+3像素空白，这是WPF官方的虚线设置方式
            var limitLine1 = new Line
            {
                X1 = leftLimitPx, Y1 = 0, X2 = leftLimitPx, Y2 = h,
                Stroke = LimitBorderBrush, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 6, 3 }
            };
            TrajectoryCanvas.Children.Add(limitLine1);

            var limitLine2 = new Line
            {
                X1 = rightLimitPx, Y1 = 0, X2 = rightLimitPx, Y2 = h,
                Stroke = LimitBorderBrush, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 6, 3 }
            };
            TrajectoryCanvas.Children.Add(limitLine2);

            var limitLine3 = new Line
            {
                X1 = 0, Y1 = topLimitPx, X2 = w, Y2 = topLimitPx,
                Stroke = LimitBorderBrush, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 6, 3 }
            };
            TrajectoryCanvas.Children.Add(limitLine3);

            var limitLine4 = new Line
            {
                X1 = 0, Y1 = bottomLimitPx, X2 = w, Y2 = bottomLimitPx,
                Stroke = LimitBorderBrush, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 6, 3 }
            };
            TrajectoryCanvas.Children.Add(limitLine4);

            // 限位标签，显示在右上角限位边界内侧
            var limitLabel = new TextBlock { Text = $"限位 ±{limitX:F0}×±{limitY:F0}mm", Foreground = LimitBorderBrush, FontSize = 9 };
            Canvas.SetLeft(limitLabel, rightLimitPx - 80); Canvas.SetTop(limitLabel, topLimitPx + 2);
            TrajectoryCanvas.Children.Add(limitLabel);
        }

        /// <summary>
        /// 绘制配方轨迹预览，显示当前配方的点位路径。
        /// <para>只在非执行配方时显示（执行时实时轨迹会覆盖预览，画了也看不清）。</para>
        /// <para>轨迹类型区分：</para>
        /// <para>- 直线段（Linear）：出胶=暗红色实线，移动=暗黄色实线</para>
        /// <para>- 快速移动（RapidMove）：灰色虚线，表示G0快速定位</para>
        /// <para>- 圆弧段（Arc）：和直线段同样的颜色，但用折线逼近圆弧曲线</para>
        /// <para>起点用青色圆点标记，终点用橙红色圆点标记。</para>
        /// </summary>
        private void DrawRecipePreview(double cx, double cy, double scale)
        {
            // 没有配方点就不画
            if (_viewModel.CurrentRecipePoints.Count == 0) return;

            var points = _viewModel.CurrentRecipePoints;
            double prevX = 0, prevY = 0;
            bool hasPrev = false;

            for (int i = 0; i < points.Count; i++)
            {
                var pt = points[i];
                // 物理坐标转Canvas坐标：X正方向向右，Y正方向向上（Canvas的Y要取反）
                double ptX = cx + pt.X * scale;
                double ptY = cy - pt.Y * scale;

                if (hasPrev)
                {
                    // 根据出胶状态选颜色和线宽
                    Brush segBrush = pt.GlueOn ? RecipeGlueColor : RecipeMoveColor;
                    double segThickness = pt.GlueOn ? 2.0 : 1.0;

                    switch (pt.Type)
                    {
                        case TrajectoryType.Linear:
                            {
                                // 直线段：直接画一条线
                                var line = new Line
                                {
                                    X1 = prevX, Y1 = prevY, X2 = ptX, Y2 = ptY,
                                    Stroke = segBrush, StrokeThickness = segThickness
                                };
                                TrajectoryCanvas.Children.Add(line);
                            }
                            break;

                        case TrajectoryType.RapidMove:
                            {
                                // 快速移动：灰色虚线，线宽1.0
                                var line = new Line
                                {
                                    X1 = prevX, Y1 = prevY, X2 = ptX, Y2 = ptY,
                                    Stroke = RapidColor, StrokeThickness = 1.0,
                                    StrokeDashArray = new DoubleCollection { 4, 3 }  // 虚线
                                };
                                TrajectoryCanvas.Children.Add(line);
                            }
                            break;

                        case TrajectoryType.Arc:
                            {
                                // 圆弧段：用折线逼近
                                // 已知：前一个点、当前点、圆心坐标、顺逆时针方向
                                double arcCX = cx + pt.ArcCenterX * scale;
                                double arcCY = cy - pt.ArcCenterY * scale;
                                // 用前一个点到圆心的距离作为半径
                                double dx = prevX - arcCX;
                                double dy = prevY - arcCY;
                                double radius = Math.Sqrt(dx * dx + dy * dy);
                                // 起始角和终止角
                                double startAngle = Math.Atan2(dy, dx);
                                double endDx = ptX - arcCX;
                                double endDy = ptY - arcCY;
                                double endAngle = Math.Atan2(endDy, endDx);
                                // 计算扫掠角（从起始角到终止角的角度差）
                                double sweepAngle = endAngle - startAngle;
                                // 顺时针且扫掠角>0时，需要减2π；逆时针且<0时加2π
                                if (pt.ArcClockwise && sweepAngle > 0) sweepAngle -= 2 * Math.PI;
                                if (!pt.ArcClockwise && sweepAngle < 0) sweepAngle += 2 * Math.PI;

                                if (radius > 0.1)
                                {
                                    // 把圆弧分成若干段折线逼近，段数根据弧长自适应
                                    int segments = Math.Max(16, (int)(Math.Abs(sweepAngle) * radius / 3));
                                    var arcPoints = new PointCollection();
                                    for (int j = 0; j <= segments; j++)
                                    {
                                        double t = (double)j / segments;
                                        double angle = startAngle + sweepAngle * t;
                                        arcPoints.Add(new Point(arcCX + radius * Math.Cos(angle), arcCY + radius * Math.Sin(angle)));
                                    }
                                    if (arcPoints.Count >= 2)
                                    {
                                        var polyline = new Polyline { Points = arcPoints, Stroke = segBrush, StrokeThickness = segThickness };
                                        TrajectoryCanvas.Children.Add(polyline);
                                    }

                                    // 画圆心小点，方便调试
                                    var centerDot = new Ellipse { Width = 4, Height = 4, Fill = LimitBorderBrush };
                                    Canvas.SetLeft(centerDot, arcCX - 2); Canvas.SetTop(centerDot, arcCY - 2);
                                    TrajectoryCanvas.Children.Add(centerDot);
                                }
                            }
                            break;
                    }
                }

                // 每个配方点画一个小圆点
                var dot = new Ellipse
                {
                    Width = 3, Height = 3,
                    Fill = pt.Type == TrajectoryType.RapidMove ? RapidColor : (pt.GlueOn ? RecipeGlueColor : RecipeMoveColor)
                };
                Canvas.SetLeft(dot, ptX - 1.5); Canvas.SetTop(dot, ptY - 1.5);
                TrajectoryCanvas.Children.Add(dot);

                prevX = ptX; prevY = ptY; hasPrev = true;
            }

            // 画起点（青色）和终点（橙红色）标记
            if (points.Count > 0)
            {
                // 起点用青色大圆点
                var startDot = new Ellipse { Width = 6, Height = 6, Fill = Brushes.Cyan };
                double sx = cx + points[0].X * scale;
                double sy = cy - points[0].Y * scale;
                Canvas.SetLeft(startDot, sx - 3); Canvas.SetTop(startDot, sy - 3);
                TrajectoryCanvas.Children.Add(startDot);

                // 终点用橙红色大圆点
                var endDot = new Ellipse { Width = 6, Height = 6, Fill = Brushes.OrangeRed };
                double ex = cx + points[points.Count - 1].X * scale;
                double ey = cy - points[points.Count - 1].Y * scale;
                Canvas.SetLeft(endDot, ex - 3); Canvas.SetTop(endDot, ey - 3);
                TrajectoryCanvas.Children.Add(endDot);
            }
        }

        /// <summary>
        /// 绘制运动轨迹历史，从IMotionService获取轨迹点列表，按出胶状态区分颜色。
        /// <para>数据来源：IMotionService.TrajectoryTrail，这是一个List，记录了运动过程中每个采样点的坐标和出胶状态。</para>
        /// <para>【踩坑】之前用MotionSimulatorService具体类获取轨迹点，DI改造后改为IMotionService接口，
        /// 这样模拟器和真实运动卡都能提供轨迹数据，不用改这里的代码。</para>
        /// <para>颜色区分：出胶段=红色实线（GlueColor），移动段=黄色实线（MoveColor）。</para>
        /// <para>性能优化：轨迹点可能很多（几千个），所以做了降采样——最多画1000条线段，
        /// 超过的按步长跳着画（step = max(1, 总点数/1000)）。</para>
        /// </summary>
        private void DrawMotionTrail(double cx, double cy, double scale)
        {
            // 从ViewModel获取轨迹点列表
            var trail = _viewModel.TrajectoryTrail;
            if (trail == null || trail.Count < 2) return;

            // 降采样：轨迹点太多时按步长跳着画，保证最多画1000条线段
            int step = Math.Max(1, trail.Count / 1000);
            int drawn = 0;
            const int maxLines = 1000;

            for (int i = step; i < trail.Count && drawn < maxLines; i += step, drawn++)
            {
                var prev = trail[i - step];
                var cur = trail[i];

                // 画线段：出胶=红色粗线，移动=黄色细线
                var line = new Line
                {
                    X1 = cx + prev.x * scale,
                    Y1 = cy - prev.y * scale,
                    X2 = cx + cur.x * scale,
                    Y2 = cy - cur.y * scale,
                    Stroke = cur.glueOn ? GlueColor : MoveColor,
                    StrokeThickness = cur.glueOn ? 2.0 : 1.2
                };
                TrajectoryCanvas.Children.Add(line);
            }
        }

        /// <summary>
        /// 绘制工件Mark点和偏差线。
        /// <para>Mark1用绿色（Lime）十字+圆圈+坐标标签，Mark2用品红色（Magenta）。</para>
        /// <para>两个Mark之间画黄色虚线连线。</para>
        /// <para>偏差线：从坐标轴到Mark点的分解线——</para>
        /// <para>【踩坑】X方向偏差线用红色虚线（水平），Y方向偏差线用蓝色虚线（垂直），
        /// 这样一眼就能看出Mark点在X和Y方向各偏了多少。</para>
        /// <para>偏差线画法：从X轴到Mark点画水平红线（表示X偏移），从Y轴到Mark点画垂直蓝线（表示Y偏移）。</para>
        /// </summary>
        private void DrawWorkpieceMarkers(double cx, double cy, double scale)
        {
            // ===== 画Mark1（绿色） =====
            if (_viewModel.Mark1PhysX != 0 || _viewModel.Mark1PhysY != 0)
            {
                double px = cx + _viewModel.Mark1PhysX * scale;
                double py = cy - _viewModel.Mark1PhysY * scale;

                // 十字线（横线+竖线）
                var cross1 = new Line { X1 = px - 8, Y1 = py, X2 = px + 8, Y2 = py, Stroke = Brushes.Lime, StrokeThickness = 2.5 };
                var cross2 = new Line { X1 = px, Y1 = py - 8, X2 = px, Y2 = py + 8, Stroke = Brushes.Lime, StrokeThickness = 2.5 };
                TrajectoryCanvas.Children.Add(cross1);
                TrajectoryCanvas.Children.Add(cross2);

                // 圆圈
                var circle = new Ellipse { Width = 16, Height = 16, Stroke = Brushes.Lime, StrokeThickness = 1.5, Fill = Brushes.Transparent };
                Canvas.SetLeft(circle, px - 8); Canvas.SetTop(circle, py - 8);
                TrajectoryCanvas.Children.Add(circle);

                // 坐标标签
                var mark1Label = new TextBlock { Text = $"M1({_viewModel.Mark1PhysX:F1},{_viewModel.Mark1PhysY:F1})", Foreground = Brushes.Lime, FontSize = 10, FontWeight = FontWeight.FromOpenTypeWeight(700) };
                Canvas.SetLeft(mark1Label, px + 12); Canvas.SetTop(mark1Label, py - 8);
                TrajectoryCanvas.Children.Add(mark1Label);
            }

            // ===== 画Mark2（品红色） =====
            if (_viewModel.Mark2PhysX != 0 || _viewModel.Mark2PhysY != 0)
            {
                double px = cx + _viewModel.Mark2PhysX * scale;
                double py = cy - _viewModel.Mark2PhysY * scale;

                var cross1 = new Line { X1 = px - 8, Y1 = py, X2 = px + 8, Y2 = py, Stroke = Brushes.Magenta, StrokeThickness = 2.5 };
                var cross2 = new Line { X1 = px, Y1 = py - 8, X2 = px, Y2 = py + 8, Stroke = Brushes.Magenta, StrokeThickness = 2.5 };
                TrajectoryCanvas.Children.Add(cross1);
                TrajectoryCanvas.Children.Add(cross2);

                var circle = new Ellipse { Width = 16, Height = 16, Stroke = Brushes.Magenta, StrokeThickness = 1.5, Fill = Brushes.Transparent };
                Canvas.SetLeft(circle, px - 8); Canvas.SetTop(circle, py - 8);
                TrajectoryCanvas.Children.Add(circle);

                var mark2Label = new TextBlock { Text = $"M2({_viewModel.Mark2PhysX:F1},{_viewModel.Mark2PhysY:F1})", Foreground = Brushes.Magenta, FontSize = 10, FontWeight = FontWeight.FromOpenTypeWeight(700) };
                Canvas.SetLeft(mark2Label, px + 12); Canvas.SetTop(mark2Label, py + 4);
                TrajectoryCanvas.Children.Add(mark2Label);
            }

            // ===== 两个Mark都有时，画连线和偏差线 =====
            if ((_viewModel.Mark1PhysX != 0 || _viewModel.Mark1PhysY != 0) &&
                (_viewModel.Mark2PhysX != 0 || _viewModel.Mark2PhysY != 0))
            {
                double px1 = cx + _viewModel.Mark1PhysX * scale;
                double py1 = cy - _viewModel.Mark1PhysY * scale;
                double px2 = cx + _viewModel.Mark2PhysX * scale;
                double py2 = cy - _viewModel.Mark2PhysY * scale;

                // Mark间连线：黄色虚线
                var connLine = new Line
                {
                    X1 = px1, Y1 = py1, X2 = px2, Y2 = py2,
                    Stroke = Brushes.Yellow, StrokeThickness = 1.5,
                    StrokeDashArray = new DoubleCollection { 4, 3 }
                };
                TrajectoryCanvas.Children.Add(connLine);

                // 偏差线：X方向红色虚线，Y方向蓝色虚线
                // 【踩坑】之前偏差线颜色没区分X/Y，看起来很混乱，改成红色=水平=X偏移，蓝色=垂直=Y偏移就好理解了

                // M1偏差线
                if (_viewModel.Mark1PhysX != 0 || _viewModel.Mark1PhysY != 0)
                {
                    // X方向偏差线（水平）：从Y轴(cx)到Mark1的X位置，红色虚线
                    var dxLine1 = new Line { X1 = cx, Y1 = py1, X2 = px1, Y2 = py1, Stroke = Brushes.Red, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 2 } };
                    TrajectoryCanvas.Children.Add(dxLine1);
                    // Y方向偏差线（垂直）：从X轴(cy)到Mark1的Y位置，蓝色虚线
                    var dyLine1 = new Line { X1 = px1, Y1 = cy, X2 = px1, Y2 = py1, Stroke = Brushes.Blue, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 2 } };
                    TrajectoryCanvas.Children.Add(dyLine1);
                }
                // M2偏差线
                if (_viewModel.Mark2PhysX != 0 || _viewModel.Mark2PhysY != 0)
                {
                    // X方向偏差线（水平），红色虚线
                    var dxLine2 = new Line { X1 = cx, Y1 = py2, X2 = px2, Y2 = py2, Stroke = Brushes.Red, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 2 } };
                    TrajectoryCanvas.Children.Add(dxLine2);
                    // Y方向偏差线（垂直），蓝色虚线
                    var dyLine2 = new Line { X1 = px2, Y1 = cy, X2 = px2, Y2 = py2, Stroke = Brushes.Blue, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 2 } };
                    TrajectoryCanvas.Children.Add(dyLine2);
                }
            }
        }

        /// <summary>
        /// 绘制当前位置光标，显示运动轴当前所在的物理坐标位置。
        /// <para>画法：蓝色十字+圆圈+坐标标签。</para>
        /// <para>位置数据来源：优先从IMotionService.GetAxisStatus获取实时位置（更准确），
        /// 如果获取不到就用ViewModel的AxisXPos/AxisYPos（从运动卡读取的缓存值）。</para>
        /// <para>【踩坑】之前直接用ViewModel的属性，但那个是定时刷新的，有延迟；
        /// 改成从IMotionService.GetAxisStatus获取，能拿到更实时的位置。</para>
        /// </summary>
        private void DrawCurrentPosition(double cx, double cy, double scale)
        {
            // 运动卡没连接就不画
            if (!_viewModel.IsCardConnected) return;

            // 优先从ViewModel获取实时位置
            double posX = _viewModel.AxisXPos;
            double posY = _viewModel.AxisYPos;
            if (_viewModel.IsCardConnected)
            {
                // GetAxisStatus(1)获取X轴状态，GetAxisStatus(2)获取Y轴状态
                // 返回的AxisStatus里有Position字段，是轴的实时位置（mm）
                var statusX = _viewModel.GetAxisStatus(1);
                var statusY = _viewModel.GetAxisStatus(2);
                posX = statusX.Position;
                posY = statusY.Position;
            }

            // 物理坐标转Canvas坐标
            double px = cx + posX * scale;
            double py = cy - posY * scale;

            // 十字线
            var cross1 = new Line { X1 = px - 8, Y1 = py, X2 = px + 8, Y2 = py, Stroke = PositionBrush, StrokeThickness = 2.5 };
            var cross2 = new Line { X1 = px, Y1 = py - 8, X2 = px, Y2 = py + 8, Stroke = PositionBrush, StrokeThickness = 2.5 };
            TrajectoryCanvas.Children.Add(cross1);
            TrajectoryCanvas.Children.Add(cross2);

            // 圆圈
            var circle = new Ellipse { Width = 12, Height = 12, Stroke = PositionBrush, StrokeThickness = 1.5, Fill = Brushes.Transparent };
            Canvas.SetLeft(circle, px - 6); Canvas.SetTop(circle, py - 6);
            TrajectoryCanvas.Children.Add(circle);

            // 坐标标签
            var posLabel = new TextBlock
            {
                Text = $"({posX:F1},{posY:F1})",
                Foreground = PositionBrush, FontSize = 9, FontWeight = FontWeight.FromOpenTypeWeight(700)
            };
            Canvas.SetLeft(posLabel, px + 10); Canvas.SetTop(posLabel, py + 2);
            TrajectoryCanvas.Children.Add(posLabel);
        }

        #endregion
    }
}
